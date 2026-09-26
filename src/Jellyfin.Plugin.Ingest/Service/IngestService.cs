using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Quarantine;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Background service: sweeps every enabled watch folder, and for each release that has finished arriving, plans and
/// (unless in dry-run) executes the ingest, then queues a library scan. A periodic sweep is used rather than file-system
/// notifications, which are unreliable on network and virtualised shares.
/// </summary>
public sealed partial class IngestService : IHostedService, IDisposable
{
    /// <summary>How often watch folders are swept.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private const long MaxSubtitleBytesToRead = 4L * 1024 * 1024;

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<IngestService> _logger;
    private readonly IngestStateStore _state;
    private readonly IngestProgress _progress;
    private readonly IngestPaths _ingestPaths;
    private readonly ActivityReport _activity;
    private readonly StartupMaintenance _maintenance;
    private readonly QuarantineRelease _quarantineRelease;
    private readonly RetrySchedule _retries;
    private readonly ProviderOutageBreaker _breaker;
    private readonly IApplicationPaths _paths;
    private readonly IConfigurationManager _configuration;
    private readonly Dictionary<string, ReleaseTracker> _trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _folderErrors = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = TimeProvider.System;
    private CachingMetadataLookup? _lookup;

    // When a missing watch folder was last reported
    private readonly Dictionary<string, DateTimeOffset> _missingReported = new(StringComparer.Ordinal);

    // Kept for the service's life, so a file is transcribed once while it is unchanged
    private readonly SpeechTranscriber _transcriber = new();
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="libraryMonitor">Jellyfin library monitor (told which folders changed, so only those are refreshed).</param>
    /// <param name="providerManager">Jellyfin provider manager.</param>
    /// <param name="state">Reviews and activity shown on the dashboard.</param>
    /// <param name="progress">What the sweep is waiting for and working on, shown on the dashboard.</param>
    /// <param name="ingestPaths">Where Ingest keeps its own files.</param>
    /// <param name="paths">Jellyfin's own folders (never usable as watch or quarantine folders).</param>
    /// <param name="configuration">Jellyfin's configuration (for the transcode folder).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="activity">Jellyfin's Activity log (entries that need attention are copied there).</param>
    public IngestService(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, IngestStateStore state, IngestProgress progress, IngestPaths ingestPaths, IApplicationPaths paths, IConfigurationManager configuration, ILogger<IngestService> logger, MediaBrowser.Model.Activity.IActivityManager? activity = null)
    {
        // What needs attention (and what was done) is copied to Jellyfin's Activity log, unless switched off (FAM-05)
        if (activity is not null && state is not null)
        {
            var notifier = new ActivityNotifier(
                note => activity.CreateAsync(new Jellyfin.Database.Implementations.Entities.ActivityLog(note.Name, ActivityNotifier.Type, Guid.Empty)
                {
                    ShortOverview = note.ShortOverview,
                    Overview = note.Overview,
                    LogSeverity = note.Severity,
                }),
                TimeProvider.System);
            state.Recorded = entry =>
            {
                if (IngestPlugin.Instance?.Configuration.WriteToActivityLog != false)
                {
                    _ = notifier.NotifyAsync(entry);
                }
            };
        }

        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _libraryMonitor = libraryMonitor ?? throw new ArgumentNullException(nameof(libraryMonitor));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _providerManager = providerManager ?? throw new ArgumentNullException(nameof(providerManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ingestPaths = ingestPaths ?? throw new ArgumentNullException(nameof(ingestPaths));
        _activity = new ActivityReport(_state, _clock);
        _maintenance = new StartupMaintenance(_state, _ingestPaths, _clock, _logger);
        _quarantineRelease = new QuarantineRelease(_state, _activity, _ingestPaths, _clock, _logger);
        _retries = new RetrySchedule(_clock);
        _breaker = new ProviderOutageBreaker(_state, _clock, _logger);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null || _loop is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The service must survive any single failed sweep; the error is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSweepFailed(_logger, ex);
            }

            try
            {
                // Sooner when "Process now" is pressed (ING-36)
                await _progress.WaitForNextSweepAsync(SweepInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var plugin = IngestPlugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var config = plugin.Configuration;
        var libraries = FolderPolicy.Libraries(_libraryManager.GetVirtualFolders());

        // One search cache for the service's life (kept on disk), and one library index per sweep
        _lookup ??= new CachingMetadataLookup(new JellyfinMetadataLookup(_providerManager, () => (_configuration as MediaBrowser.Controller.Configuration.IServerConfigurationManager)?.Configuration.PreferredMetadataLanguage), _clock, _ingestPaths.SearchCache);
        _lookup.BeginSweep();
        var sweep = new Sweep(
            new MediaIdentifier(_lookup, new JellyfinLibraryIndex(_libraryManager, []), config.UseAiTiebreak ? new AiTiebreaker() : null, config.UseTranscripts ? _transcriber : null),
            [.. libraries.SelectMany(l => l.Locations)],
            libraries);
        var problems = FolderPolicy.FolderProblems(config, libraries, _paths, _configuration);
        _maintenance.RunDue(config, libraries, problems);

        // Undos asked for on the page, between releases so they never race a filing or a scan
        RunQueued(config, libraries, problems);

        // Reviews of a watch folder that has been removed from the settings can never be acted on
        _state.PruneWatchFolders([.. config.WatchFolders.Select(w => w.Path)]);
        _progress.KeepOnly([.. config.WatchFolders.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Path)).Select(w => w.Path)]);
        foreach (var watch in config.WatchFolders.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Path)))
        {
            // Each watch folder on its own: one that can't be read (permissions, offline share) mustn't stop the others
            try
            {
                // An unsafe watch or quarantine folder is never swept; the reason is shown once in the activity panel
                var problem = problems.FirstOrDefault(p => PathGuard.SamePath(p.Folder, watch.Path) || PathGuard.SamePath(p.Folder, config.QuarantinePath));
                if (problem is not null)
                {
                    throw new InvalidOperationException("Not swept, because the folder settings are unsafe: " + problem.Problem + " (" + problem.Folder + ")");
                }

                await SweepFolderAsync(config, libraries, sweep, watch, ct).ConfigureAwait(false);
                _folderErrors.Remove(watch.Path);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // A failing watch folder is reported (once per distinct error) and the sweep moves on.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogWatchFolderFailed(_logger, watch.Path, ex);
                if (!_folderErrors.TryGetValue(watch.Path, out var last) || !string.Equals(last, ex.Message, StringComparison.Ordinal))
                {
                    _folderErrors[watch.Path] = ex.Message;
                    _state.Record(new ActivityEntry
                    {
                        Time = _clock.GetUtcNow(),
                        Status = ActivityStatus.Failed,
                        Release = watch.Path,
                        WatchFolder = watch.Path,
                        Summary = "This watch folder couldn't be read: " + ex.Message,
                    });
                }
            }
        }

        _lookup.Save();
    }

    private async Task SweepFolderAsync(PluginConfiguration config, IReadOnlyCollection<MediaLibrary> libraries, Sweep sweep, WatchFolder watch, CancellationToken ct)
    {
        if (!Directory.Exists(watch.Path))
        {
            // Said once in Recent activity, and logged at most hourly, not every sweep (ING-29)
            var now = _clock.GetUtcNow();
            if (!_missingReported.TryGetValue(watch.Path, out var last) || now - last >= TimeSpan.FromHours(1))
            {
                _missingReported[watch.Path] = now;
                LogMissingWatchFolder(_logger, watch.Path);
                _state.RecordUnlessRepeat(new ActivityEntry
                {
                    Time = now,
                    Status = ActivityStatus.Failed,
                    Release = watch.Path,
                    WatchFolder = watch.Path,
                    Summary = "The watch folder isn't available: it isn't mounted, or it isn't the path as this server sees it (a Docker host path, or a mapped drive a service can't see). Nothing is ingested from it until it is.",
                });
            }

            _progress.SetWaiting(watch.Path, []);
            return;
        }

        _missingReported.Remove(watch.Path);

        var destinations = FolderPolicy.DestinationsOf(watch);
        var routing = LibraryRouting.Route(destinations, libraries);
        foreach (var problem in routing.Problems)
        {
            LogRoutingProblem(_logger, watch.Path, problem);
        }

        var targets = routing.Targets;
        if (targets.Tv is null && targets.Films is null)
        {
            LogNoLibrary(_logger, watch.Path);
            return;
        }

        var quarantine = FolderPolicy.QuarantineFor(config, watch);
        var scan = ReleaseScanner.Scan(watch.Path, quarantine);
        var snapshot = scan.Releases;
        if (!_trackers.TryGetValue(watch.Path, out var tracker))
        {
            tracker = new ReleaseTracker();
            _trackers[watch.Path] = tracker;
        }

        // Settings that change the outcome (dry run, destinations) changed: plan everything waiting again.
        var settings = LibraryRouting.SettingsFingerprint(watch.IsDryRun(config.DryRun), FolderPolicy.DestinationsOf(watch));
        if (_settings.TryGetValue(watch.Path, out var before) && !string.Equals(before, settings, StringComparison.Ordinal))
        {
            LogReplanning(_logger, watch.Path);
            tracker.ForgetAll();
        }

        _settings[watch.Path] = settings;

        // Releases due an automatic retry are offered again; retries of releases that have gone are dropped
        foreach (var release in _retries.Due(watch.Path, snapshot.ContainsKey))
        {
            tracker.Forget(release);
        }

        _state.PruneReviews(watch.Path, [.. snapshot.Keys, .. scan.Links, .. scan.Unsettled, .. scan.Unreadable]);
        _state.PruneCopied(watch.Path, [.. snapshot.Keys, .. scan.Unsettled, .. scan.Unreadable]);
        ReviewLinks(watch, scan.Links);
        ReviewUnreadable(watch, scan.Unreadable);
        foreach (var review in _state.Snapshot().Reviews.Where(r => r.WatchFolder == watch.Path && r.Request != ReviewRequest.None))
        {
            if (review.Request == ReviewRequest.Quarantine && snapshot.TryGetValue(review.Release, out var releaseFiles))
            {
                try
                {
                    lock (_progress.FileGate)
                    {
                        _quarantineRelease.Run(config, watch, review, releaseFiles, quarantine);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogReleaseFailed(_logger, review.Release, ex);
                    _activity.ReportFailure(watch, review.Release, "Quarantine failed: " + ex.Message, [], review.RequestVersion);
                }
            }
            else
            {
                tracker.Forget(review.Release);
            }
        }

        // "Process now" skips the rest of a release's settle wait (ING-36)
        foreach (var release in snapshot.Keys.Where(r => _progress.TakeProcessNow(IngestStateStore.ReviewId(watch.Path, r))))
        {
            tracker.SettleNow(release);
        }

        var settle = TimeSpan.FromSeconds(Math.Max(5, config.SettleSeconds));
        var ready = tracker.Observe(snapshot, _clock.GetUtcNow(), settle);
        PublishWaiting(watch, tracker, settle, snapshot);
        foreach (var release in ready)
        {
            ct.ThrowIfCancellationRequested();

            // Providers look down: leave the release unhandled so it is planned once the pause ends
            if (_breaker.IsPaused)
            {
                continue;
            }

            // Undone or restored by an administrator: it waits for a decision, however confidently it could be filed
            if (IsHeld(_state.GetReview(IngestStateStore.ReviewId(watch.Path, release))))
            {
                tracker.MarkHandled(release);
                continue;
            }

            // Filed earlier by copy or hard link and unchanged since: it stays for seeding, and isn't filed again
            if (watch.Transfer != TransferMode.Move && _state.WasCopied(watch.Path, release, IngestStateStore.CopySignature(snapshot[release])))
            {
                tracker.MarkHandled(release);
                continue;
            }

            // The request this planning acts on; one made while planning runs is kept for the next sweep
            var seen = _state.GetReview(IngestStateStore.ReviewId(watch.Path, release))?.RequestVersion ?? 0;
            _progress.SetWorking(new WorkInProgress { Id = IngestStateStore.ReviewId(watch.Path, release), WatchFolder = watch.Path, Release = release, Stage = "Identifying", Since = _clock.GetUtcNow() });
            try
            {
                await IngestAsync(config, sweep, watch, release, snapshot[release], targets, quarantine, seen, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // One bad release (provider outage, unreadable file) mustn't stop the others; it is reported.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogReleaseFailed(_logger, release, ex);
                _activity.ReportFailure(watch, release, ex.Message, [], seen);
            }
            finally
            {
                _progress.SetWorking(null);
            }

            tracker.MarkHandled(release);
            PublishWaiting(watch, tracker, settle, snapshot);
            if (_lookup is not null)
            {
                _breaker.Check(_lookup);
            }
        }
    }

    // What this watch folder is still waiting for, for the page (ING-36). Releases with a review are shown there instead,
    // and one filed by copy or hard link that is only being checked again isn't news.
    private void PublishWaiting(WatchFolder watch, ReleaseTracker tracker, TimeSpan settle, IReadOnlyDictionary<string, IReadOnlyList<ReleaseFile>> snapshot)
    {
        var now = _clock.GetUtcNow();
        var waiting = new List<WaitingRelease>();
        foreach (var (release, settlesAt) in tracker.Pending(settle))
        {
            var id = IngestStateStore.ReviewId(watch.Path, release);
            if (_state.GetReview(id) is not null
                || (watch.Transfer != TransferMode.Move && snapshot.TryGetValue(release, out var files) && _state.WasCopied(watch.Path, release, IngestStateStore.CopySignature(files))))
            {
                continue;
            }

            waiting.Add(new WaitingRelease
            {
                Id = id,
                WatchFolder = watch.Path,
                Release = release,
                SettlesAt = settlesAt is { } at && at < now ? now : settlesAt,
            });
        }

        _progress.SetWaiting(watch.Path, waiting);
    }

    // A top-level symbolic link is never followed; it waits for review so the user knows why nothing happened
    private void ReviewLinks(WatchFolder watch, IReadOnlyList<string> links)
        => ReviewOnce(watch, links, "This is a symbolic link. Links aren't followed, so nothing outside the watch folder can be moved; move or copy the files themselves into the watch folder.");

    // Likewise a release the server's account can't open, which would otherwise be skipped without a word
    private void ReviewUnreadable(WatchFolder watch, IReadOnlyList<string> entries)
        => ReviewOnce(watch, entries, "Jellyfin's account can't read this, so it can't be filed. Give the account read access (and write access to the watch folder), then retry.");

    private void ReviewOnce(WatchFolder watch, IReadOnlyList<string> entries, string reason)
    {
        foreach (var entry in entries)
        {
            var id = IngestStateStore.ReviewId(watch.Path, entry);
            if (_state.GetReview(id) is not null)
            {
                continue;
            }

            _state.Record(new ActivityEntry { Time = _clock.GetUtcNow(), Status = ActivityStatus.NeedsReview, Release = entry, WatchFolder = watch.Path, Summary = reason });
            _state.PutReview(new PendingReview { Id = id, WatchFolder = watch.Path, Release = entry, Time = _clock.GetUtcNow(), Items = [new PendingReviewItem(entry, reason)] });
        }
    }

    private async Task IngestAsync(
        PluginConfiguration config,
        Sweep sweep,
        WatchFolder watch,
        string release,
        IReadOnlyList<ReleaseFile> files,
        LibraryTargets targets,
        string quarantine,
        int seenVersion,
        CancellationToken ct)
    {
        var id = IngestStateStore.ReviewId(watch.Path, release);
        var dryRun = watch.IsDryRun(config.DryRun);
        var previous = _state.GetReview(id);
        if (previous is not null && previous.RequestVersion != seenVersion)
        {
            // A decision arrived between the sweep noticing the release and planning it: plan with that decision
            seenVersion = previous.RequestVersion;
        }

        // Titles anywhere on the server count as "already in the library" (a show kept in another library is still
        // strong evidence, and its new episodes join it there), but existing shows may only be joined inside one of
        // the server's library folders
        var planner = new IngestPlanner(
            sweep.Identifier,
            p => File.Exists(p) || Directory.Exists(p),
            ReadSmallText,
            _clock,
            new JellyfinExistingMedia(_libraryManager),
            p => PathGuard.IsUnderAny(p, sweep.LibraryFolders),
            FilesIn)
        {
            ReplaceExisting = previous?.Request == ReviewRequest.Replace,
            FileDecisions = previous?.FileDecisions ?? new Dictionary<string, FileDecision>(StringComparer.Ordinal),
            EpisodeNumbers = previous?.FileEpisodes ?? new Dictionary<string, EpisodeNumber>(StringComparer.Ordinal),
            Transfer = watch.Transfer,

            // A replacement is filed where the copy it replaces lives
            Libraries = sweep.Libraries,
        };
        var plan = await planner.PlanAsync(watch.Path, release, files, targets, quarantine, previous?.Chosen, ct).ConfigureAwait(false);

        Directory.CreateDirectory(_ingestPaths.DataFolder);
        if (!plan.IsReady)
        {
            var retryAt = _retries.Schedule(id, watch.Path, release, plan.Retry);
            var items = plan.Review.Select(r => new PendingReviewItem(Path.GetRelativePath(watch.Path, r.Source), r.Reason) { Existing = r.Existing }).ToList();
            LogNeedsReview(_logger, release, items.Count);
            foreach (var item in items)
            {
                LogNeedsReviewItem(_logger, release, item.Source, item.Reason);
            }

            // Only report a review once per distinct set of reasons (restarts and retries plan the release again).
            if (previous is null || previous.Request != ReviewRequest.None || !previous.Items.SequenceEqual(items))
            {
                _state.Record(_activity.NeedsReview(watch.Path, release, items));
            }

            _state.PutReview(new PendingReview
            {
                Id = id,
                WatchFolder = watch.Path,
                Release = release,
                Time = _clock.GetUtcNow(),
                Items = items,
                Candidates = DistinctCandidates(plan.Review),
                RetryAt = retryAt,
            },
            seenVersion);
            return;
        }

        _retries.Clear(id);

        var working = new WorkInProgress { Id = id, WatchFolder = watch.Path, Release = release, Stage = "Filing", Since = _clock.GetUtcNow() };
        ExecutionReport report;
        lock (_progress.FileGate)
        {
            // Mark the dated quarantine folder as Ingest's own before anything is moved into it (the purge only deletes marked folders)
            if (!dryRun)
            {
                foreach (var dated in plan.Operations.Where(o => o.Kind == OperationKind.Quarantine).Select(o => QuarantineMarkers.DatedFolderOf(quarantine, o.Destination)).OfType<string>().Distinct(StringComparer.Ordinal))
                {
                    QuarantineMarkers.Mark(quarantine, dated);
                }
            }

            report = new PlanExecutor(new PhysicalFileOperations(), _clock) { Filing = (file, count) => _progress.SetWorking(working with { File = file, Files = count }) }
                .Execute(plan, Path.Combine(watch.Path, release), _ingestPaths.ActionLog, dryRun, ct);
        }

        var prefix = dryRun ? "[dry run] would" : "Did";
        // Per-file detail (paths can hold user and share names) only at Debug; one line per release at Information
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var (op, source) in report.Completed.Select(o => (o, Path.GetRelativePath(watch.Path, o.Source))))
            {
                LogOperation(_logger, prefix, op.Kind, source, op.Destination);
            }
        }

        var summaryLine = ActivityReport.Summarise(report.Completed, dryRun);
        if (report.Succeeded)
        {
            LogFiled(_logger, release, summaryLine);
        }

        if (report.Warning is not null)
        {
            LogTidyWarning(_logger, release, report.Warning);
        }

        if (!report.Succeeded)
        {
            LogExecutionFailed(_logger, release, report.Error ?? "unknown error");
            var summary = ActivityReport.FilingFailed(report);
            var retryAt = report.FolderUnavailable ? _retries.Schedule(id, watch.Path, release, RetryKind.FolderUnavailable) : null;
            _activity.ReportFailure(watch, release, summary, report.Completed, seenVersion, retryAt);
            return;
        }

        // A real filing carries its run, so it can be undone from Recent activity
        _state.RecordUnlessRepeat(_activity.Filed(watch.Path, release, plan, report.Completed, summaryLine, dryRun) with { Run = dryRun ? null : report.Run });

        // A release filed by copy or hard link stays in the watch folder: remember it so it isn't filed again
        if (!dryRun && watch.Transfer != TransferMode.Move)
        {
            _state.MarkCopied(new CopiedRelease(watch.Path, release, IngestStateStore.CopySignature(files)));
        }

        if (dryRun && previous?.Chosen is { } chosen)
        {
            // Keep the decision so the release files the same way once dry run is turned off.
            _state.MarkPlannedInDryRun(id, $"Dry run: planned as {chosen.Candidate.Name}{(chosen.Candidate.Year is { } y ? $" ({y})" : string.Empty)}; it will be filed once dry run is off.", seenVersion);
        }
        else
        {
            _state.RemoveReview(id);
        }

        if (!dryRun && config.ScanLibraryAfterIngest)
        {
            // Only the film or show folders that changed are refreshed (as Jellyfin's real-time monitoring would), not
            // every library on the server; a new folder is picked up through its library folder
            foreach (var folder in FiledFolders(plan))
            {
                _libraryMonitor.ReportFileSystemChanged(folder);
            }
        }
    }

    // Carries out the undos asked for on the page (each checked again now), and refreshes what changed
    private void RunQueued(PluginConfiguration config, IReadOnlyList<MediaLibrary> libraries, IReadOnlyList<FolderProblem> problems)
    {
        var queued = _progress.Queued();
        if (queued.Count == 0)
        {
            return;
        }

        var scope = FolderPolicy.ReturnScopeOf(config, libraries, problems);
        var fs = new PhysicalFileOperations();
        foreach (var action in queued)
        {
            try
            {
                Directory.CreateDirectory(_ingestPaths.DataFolder);
                var lines = File.Exists(_ingestPaths.ActionLog) ? File.ReadAllLines(_ingestPaths.ActionLog) : [];
                ReturnOutcome outcome;
                lock (_progress.FileGate)
                {
                    outcome = new ReleaseUndo(_state, fs, _clock).Undo(action.Run ?? string.Empty, scope, lines, _ingestPaths.ActionLog);
                }

                LogReturned(_logger, action.Kind, outcome.Succeeded ? "carried out" : "not carried out (see Recent activity)");
                LogReturnedDetail(_logger, action.Kind, outcome.Message);
                if (outcome.Succeeded && config.ScanLibraryAfterIngest)
                {
                    foreach (var folder in outcome.Refresh)
                    {
                        _libraryMonitor.ReportFileSystemChanged(folder);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogReturnFailed(_logger, action.Kind, ex);
                _state.Record(new ActivityEntry { Time = _clock.GetUtcNow(), Status = ActivityStatus.Failed, Release = action.Run ?? string.Empty, Summary = "The undo couldn't be carried out: " + ex.Message });
            }
            finally
            {
                _progress.Complete(action);
            }
        }
    }

    // The files in a folder (for the subtitle files of a copy being replaced); an unreadable folder has none
    private static List<string> FilesIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether a release is held for a person (undone or restored by an administrator) and so isn't planned by the
    /// sweep: until someone chooses, retries, decides file by file or asks for quarantine.
    /// </summary>
    /// <param name="review">The release's review, if any.</param>
    /// <returns><c>true</c> to leave it alone.</returns>
    public static bool IsHeld(PendingReview? review) => review is { Held: true, Request: ReviewRequest.None };

    /// <summary>
    /// The film and show folders a plan files into (not the quarantine), for refreshing just those.
    /// </summary>
    /// <param name="plan">The executed plan.</param>
    /// <returns>The folders.</returns>
    public static IReadOnlyList<string> FiledFolders(IngestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. plan.AllowedRoots
            .Where(root => plan.Operations.Any(o => o.Kind != OperationKind.Quarantine && PathGuard.IsUnder(o.Destination, root)))
            .Distinct(PathGuard.Comparer)];
    }

    /// <summary>
    /// The distinct candidates across a plan's review items, best score first.
    /// </summary>
    /// <param name="items">Review items.</param>
    /// <returns>Candidates, one per title.</returns>
    public static IReadOnlyList<ScoredCandidate> DistinctCandidates(IEnumerable<ReviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var best = new List<ScoredCandidate>();
        foreach (var c in items.SelectMany(i => i.Candidates).OrderByDescending(c => c.Score))
        {
            if (!best.Any(b => MediaIdentifier.Merge([b.Candidate, c.Candidate]).Count == 1))
            {
                best.Add(c);
            }
        }

        return best;
    }

    private static string? ReadSmallText(string path)
    {
        try
        {
            return new FileInfo(path).Length <= MaxSubtitleBytesToRead ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest {Kind}: {Result}")]
    private static partial void LogReturned(ILogger logger, QueuedActionKind kind, string result);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ingest {Kind}: {Message}")]
    private static partial void LogReturnedDetail(ILogger logger, QueuedActionKind kind, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest {Kind} failed")]
    private static partial void LogReturnFailed(ILogger logger, QueuedActionKind kind, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest watch folder {Path}: settings changed, planning waiting releases again")]
    private static partial void LogReplanning(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest watch folder {Path} couldn't be swept")]
    private static partial void LogWatchFolderFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest of {Release}: filed, but tidying up the release folder failed: {Warning}")]
    private static partial void LogTidyWarning(ILogger logger, string release, string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest watch folder does not exist: {Path}")]
    private static partial void LogMissingWatchFolder(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest watch folder {Path}: {Problem}")]
    private static partial void LogRoutingProblem(ILogger logger, string path, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest watch folder {Path} has no valid target library")]
    private static partial void LogNoLibrary(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: {Release} needs review ({Count} item(s); see the Ingest page)")]
    private static partial void LogNeedsReview(ILogger logger, string release, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ingest: {Release} needs review - {Source}: {Reason}")]
    private static partial void LogNeedsReviewItem(ILogger logger, string release, string source, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ingest: {Prefix} {Kind} {Source} -> {Destination}")]
    private static partial void LogOperation(ILogger logger, string prefix, OperationKind kind, string source, string destination);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: {Release}: {Summary}")]
    private static partial void LogFiled(ILogger logger, string release, string summary);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest of {Release} failed")]
    private static partial void LogReleaseFailed(ILogger logger, string release, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest of {Release} stopped: {Error}")]
    private static partial void LogExecutionFailed(ILogger logger, string release, string error);

    // What one sweep shares across its releases: the identifier (with its library index, loaded once) and the server's
    // library folders
    private sealed record Sweep(MediaIdentifier Identifier, IReadOnlyList<string> LibraryFolders, IReadOnlyList<MediaLibrary> Libraries);
}
