using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
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

    /// <summary>
    /// Provider searches in a row that may come back empty before identification pauses (a release can make up to five,
    /// so this is about three unknown releases running). An outage looks like "nothing found" to plugins.
    /// </summary>
    public const int EmptySearchesBeforePause = 12;

    private const long MaxSubtitleBytesToRead = 4L * 1024 * 1024;

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<IngestService> _logger;
    private readonly IngestStateStore _state;
    private readonly IngestProgress _progress;
    private readonly IApplicationPaths _paths;
    private readonly IConfigurationManager _configuration;
    private bool _markersMigrated;
    private DateTimeOffset _logTrimmed;
    private readonly Dictionary<string, ReleaseTracker> _trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _folderErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string WatchFolder, string Release, DateTimeOffset At, int Attempt)> _retries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = TimeProvider.System;
    private CachingMetadataLookup? _lookup;

    // When a missing watch folder was last reported
    private readonly Dictionary<string, DateTimeOffset> _missingReported = new(StringComparer.Ordinal);

    // Kept for the service's life, so a file is transcribed once while it is unchanged
    private readonly SpeechTranscriber _transcriber = new();
    private DateTimeOffset _identificationPausedUntil;
    private int _pauses;
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
    /// <param name="paths">Jellyfin's own folders (never usable as watch or quarantine folders).</param>
    /// <param name="configuration">Jellyfin's configuration (for the transcode folder).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="activity">Jellyfin's Activity log (entries that need attention are copied there).</param>
    public IngestService(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, IngestStateStore state, IngestProgress progress, IApplicationPaths paths, IConfigurationManager configuration, ILogger<IngestService> logger, MediaBrowser.Model.Activity.IActivityManager? activity = null)
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

    /// <summary>
    /// Resolves where a watch folder's quarantine lives.
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="watchFolder">The watch folder.</param>
    /// <returns>Absolute quarantine path.</returns>
    public static string QuarantineFor(PluginConfiguration configuration, WatchFolder watchFolder)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(watchFolder);
        return string.IsNullOrWhiteSpace(configuration.QuarantinePath)
            ? Path.Combine(watchFolder.Path, ".ingest-quarantine")
            : configuration.QuarantinePath;
    }

    /// <summary>
    /// Jellyfin's own folders, which no watch or quarantine folder may overlap.
    /// </summary>
    /// <param name="paths">Jellyfin's application paths.</param>
    /// <returns>The folders.</returns>
    /// <param name="configuration">Jellyfin's configuration (for a transcode folder moved elsewhere), if available.</param>
    public static IReadOnlyList<string> ProtectedFolders(IApplicationPaths paths, IConfigurationManager? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // The metadata and transcode folders can be moved out of the data folder in Jellyfin's settings
        string? metadata = (paths as IServerApplicationPaths)?.InternalMetadataPath;
        string? transcode = null;
        try
        {
            transcode = configuration?.GetTranscodePath();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Not configured yet: the default lives in the cache folder, which is already listed
        }

        return [.. new[] { paths.ProgramDataPath, paths.ProgramSystemPath, paths.DataPath, paths.ConfigurationDirectoryPath, paths.CachePath, paths.LogDirectoryPath, paths.PluginsPath, paths.TempDirectory, metadata, transcode }
            .OfType<string>().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Checks the configured watch and quarantine folders against the rules in <see cref="FolderRules"/>.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="libraries">The server's libraries.</param>
    /// <param name="paths">Jellyfin's application paths.</param>
    /// <param name="configuration">Jellyfin's configuration, if available.</param>
    /// <returns>The problems found.</returns>
    public static IReadOnlyList<FolderProblem> FolderProblems(PluginConfiguration config, IEnumerable<MediaLibrary> libraries, IApplicationPaths paths, IConfigurationManager? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return FolderRules.Check(
            [.. config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path)).Select(w => w.Path)],
            config.QuarantinePath,
            [.. libraries.SelectMany(l => l.Locations)],
            ProtectedFolders(paths, configuration));
    }

    /// <summary>
    /// The quarantine folders in use that pass the folder rules (one pointed at a library, say, is never purged or listed).
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="problems">Problems from <see cref="FolderProblems"/>.</param>
    /// <returns>The quarantine folders, each once.</returns>
    public static IReadOnlyList<string> SafeQuarantineRoots(PluginConfiguration config, IReadOnlyList<FolderProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(problems);
        return [.. config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path))
            .Where(w => !problems.Any(p => PathGuard.SamePath(p.Folder, w.Path) || PathGuard.SamePath(p.Folder, config.QuarantinePath)))
            .Select(w => PathGuard.Normalise(QuarantineFor(config, w)))
            .Distinct(PathGuard.Comparer)];
    }

    /// <summary>
    /// Reduces Jellyfin's libraries to what routing needs.
    /// </summary>
    /// <param name="libraries">The server's libraries.</param>
    /// <returns>The libraries.</returns>
    public static IReadOnlyList<MediaLibrary> Libraries(IEnumerable<VirtualFolderInfo> libraries)
        => [.. (libraries ?? throw new ArgumentNullException(nameof(libraries)))
            .Where(l => !string.IsNullOrEmpty(l.ItemId))
            .Select(l => new MediaLibrary(l.ItemId, l.Name ?? l.ItemId, LibraryRouting.KindOf(l.CollectionType?.ToString()), l.Locations ?? []))];

    /// <summary>
    /// A watch folder's configured destinations (a configuration from 0.1.0-alpha.1 has a single library instead).
    /// </summary>
    /// <param name="watch">The watch folder.</param>
    /// <returns>The destinations, in order.</returns>
    public static IReadOnlyList<DestinationSetting> DestinationsOf(WatchFolder watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        return watch.Destinations.Count > 0
            ? [.. watch.Destinations.Select(d => new DestinationSetting(d.LibraryId, d.Path))]
            : [new DestinationSetting(watch.TargetLibraryId, watch.TargetPath)];
    }

    /// <summary>
    /// How long to wait before trying a release again by itself. An offline library is retried until it is back (5
    /// minutes, doubling to hourly); providers that found nothing are retried three times (after 10 minutes, 1 hour and
    /// 6 hours), since an outage and an unknown title look the same, then left for a person.
    /// </summary>
    /// <param name="kind">Why the release couldn't be planned.</param>
    /// <param name="attempt">Which retry this would be (1 for the first).</param>
    /// <returns>The delay, or <c>null</c> to stop retrying.</returns>
    public static TimeSpan? RetryDelay(RetryKind kind, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return kind switch
        {
            RetryKind.FolderUnavailable => TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Min(attempt - 1, 10)))),
            RetryKind.NothingFound => attempt switch
            {
                1 => TimeSpan.FromMinutes(10),
                2 => TimeSpan.FromHours(1),
                3 => TimeSpan.FromHours(6),
                _ => null,
            },
            _ => null,
        };
    }

    // Schedules (or ends) automatic retries of a release; returns when the next one is due
    private DateTimeOffset? ScheduleRetry(string id, WatchFolder watch, string release, RetryKind kind)
    {
        var attempt = _retries.TryGetValue(id, out var r) ? r.Attempt + 1 : 1;
        if (RetryDelay(kind, attempt) is not { } delay)
        {
            _retries.Remove(id);
            return null;
        }

        var at = _clock.GetUtcNow() + delay;
        _retries[id] = (watch.Path, release, at, attempt);
        return at;
    }

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
        var libraries = Libraries(_libraryManager.GetVirtualFolders());

        // One search cache for the service's life (kept on disk), and one library index per sweep
        _lookup ??= new CachingMetadataLookup(new JellyfinMetadataLookup(_providerManager, () => (_configuration as MediaBrowser.Controller.Configuration.IServerConfigurationManager)?.Configuration.PreferredMetadataLanguage), _clock, Path.Combine(plugin.DataFolderPath, "search-cache.json"));
        _lookup.BeginSweep();
        var sweep = new Sweep(
            new MediaIdentifier(_lookup, new JellyfinLibraryIndex(_libraryManager, []), config.UseAiTiebreak ? new AiTiebreaker() : null, config.UseTranscripts ? _transcriber : null),
            [.. libraries.SelectMany(l => l.Locations)]);
        var problems = FolderProblems(config, libraries, _paths, _configuration);
        if (!_markersMigrated)
        {
            _markersMigrated = true;
            RecoverInterruptedMoves(plugin.DataFolderPath);
            MigrateQuarantineMarkers(plugin.DataFolderPath, config, problems);
        }

        // Keep only recent history in the action log (after any recovery has read it), checked daily
        if (_clock.GetUtcNow() - _logTrimmed >= TimeSpan.FromDays(1))
        {
            _logTrimmed = _clock.GetUtcNow();
            TrimActionLog(plugin.DataFolderPath);
        }
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

                await SweepFolderAsync(plugin, config, libraries, sweep, watch, ct).ConfigureAwait(false);
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

    private async Task SweepFolderAsync(IngestPlugin plugin, PluginConfiguration config, IReadOnlyCollection<MediaLibrary> libraries, Sweep sweep, WatchFolder watch, CancellationToken ct)
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

        var destinations = DestinationsOf(watch);
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

        var quarantine = QuarantineFor(config, watch);
        var scan = ReleaseScanner.Scan(watch.Path, quarantine);
        var snapshot = scan.Releases;
        if (!_trackers.TryGetValue(watch.Path, out var tracker))
        {
            tracker = new ReleaseTracker();
            _trackers[watch.Path] = tracker;
        }

        // Settings that change the outcome (dry run, destinations) changed: plan everything waiting again.
        var settings = LibraryRouting.SettingsFingerprint(watch.IsDryRun(config.DryRun), DestinationsOf(watch));
        if (_settings.TryGetValue(watch.Path, out var before) && !string.Equals(before, settings, StringComparison.Ordinal))
        {
            LogReplanning(_logger, watch.Path);
            tracker.ForgetAll();
        }

        _settings[watch.Path] = settings;

        // Releases due an automatic retry are offered again; retries of releases that have gone are dropped
        foreach (var (id, retry) in _retries.Where(r => r.Value.WatchFolder == watch.Path).ToList())
        {
            if (!snapshot.ContainsKey(retry.Release))
            {
                _retries.Remove(id);
            }
            else if (retry.At <= _clock.GetUtcNow())
            {
                tracker.Forget(retry.Release);
            }
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
                    QuarantineRelease(config, watch, review, releaseFiles, quarantine);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogReleaseFailed(_logger, review.Release, ex);
                    ReportFailure(watch, review.Release, "Quarantine failed: " + ex.Message, [], review.RequestVersion);
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
            if (_clock.GetUtcNow() < _identificationPausedUntil)
            {
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
                await IngestAsync(plugin.DataFolderPath, config, sweep, watch, release, snapshot[release], targets, quarantine, seen, ct).ConfigureAwait(false);
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
                ReportFailure(watch, release, ex.Message, [], seen);
            }
            finally
            {
                _progress.SetWorking(null);
            }

            tracker.MarkHandled(release);
            PublishWaiting(watch, tracker, settle, snapshot);
            PauseIfProvidersLookDown();
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

    // Many searches in a row found nothing: most likely an outage, so stop spending quota and try again later
    private void PauseIfProvidersLookDown()
    {
        if (_lookup is null)
        {
            return;
        }

        if (_lookup.ConsecutiveEmpty == 0)
        {
            _pauses = 0;
            return;
        }

        if (_lookup.ConsecutiveEmpty < EmptySearchesBeforePause)
        {
            return;
        }

        var pause = TimeSpan.FromMinutes(Math.Min(120, 10 * Math.Pow(2, Math.Min(_pauses, 10))));
        _identificationPausedUntil = _clock.GetUtcNow() + pause;
        LogProvidersPaused(_logger, _lookup.ConsecutiveEmpty, pause.TotalMinutes);
        if (_pauses == 0)
        {
            _state.Record(new ActivityEntry
            {
                Time = _clock.GetUtcNow(),
                Status = ActivityStatus.Failed,
                Release = "Metadata providers",
                Summary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The last {_lookup.ConsecutiveEmpty} title searches found nothing, so the metadata providers may be down. Identifying new releases is paused for {pause.TotalMinutes:0} minutes, then tried again (pausing for longer, up to 2 hours, while they still find nothing)."),
            });
        }

        _pauses++;
        _lookup.ResetEmptyCount();
    }

    // The dated folder (directly inside the quarantine) that a quarantine destination falls in
    private static string? DatedFolder(string quarantine, string destination)
    {
        if (!PathGuard.IsUnder(destination, quarantine))
        {
            return null;
        }

        var first = Path.GetRelativePath(PathGuard.Normalise(quarantine), PathGuard.Normalise(destination)).Split(Path.DirectorySeparatorChar)[0];
        return Path.Combine(quarantine, first);
    }

    // Moves cut short by a crash or restart are finished (the file had fully arrived) or discarded (the original is intact)
    private void RecoverInterruptedMoves(string dataFolder)
    {
        var log = Path.Combine(dataFolder, "actions.jsonl");
        if (!File.Exists(log))
        {
            return;
        }

        try
        {
            // Only inside today's library and quarantine folders
            var config = IngestPlugin.Instance?.Configuration ?? new PluginConfiguration();
            var roots = Libraries(_libraryManager.GetVirtualFolders()).SelectMany(l => l.Locations)
                .Concat(config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path)).Select(w => QuarantineFor(config, w)))
                .ToList();
            var results = new PlanExecutor(new PhysicalFileOperations(), _clock).Recover([.. File.ReadLines(log)], log, roots);
            foreach (var result in results)
            {
                LogRecovery(_logger, result);
            }

            if (results.Count > 0)
            {
                _state.Record(new ActivityEntry
                {
                    Time = _clock.GetUtcNow(),
                    Status = results.Any(r => r.StartsWith("Needs attention", StringComparison.Ordinal)) ? ActivityStatus.Failed : ActivityStatus.Filed,
                    Release = "Interrupted moves",
                    Summary = string.Create(CultureInfo.InvariantCulture, $"Recovered {results.Count} move(s) interrupted by a restart."),
                    Details = results,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMarkerMigrationFailed(_logger, ex);
        }
    }

    private void TrimActionLog(string dataFolder)
    {
        try
        {
            var removed = ActionLog.TrimFile(Path.Combine(dataFolder, "actions.jsonl"), _clock.GetUtcNow());
            if (removed > 0)
            {
                LogActionLogTrimmed(_logger, removed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogActionLogTrimFailed(_logger, ex);
        }
    }

    // Dated quarantine folders created before markers existed are marked if the action log shows Ingest filled them
    private void MigrateQuarantineMarkers(string dataFolder, PluginConfiguration config, IReadOnlyList<FolderProblem> problems)
    {
        var log = Path.Combine(dataFolder, "actions.jsonl");
        if (!File.Exists(log))
        {
            return;
        }

        var roots = config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path))
            .Select(w => QuarantineFor(config, w))
            .Where(q => !problems.Any(p => PathGuard.SamePath(p.Folder, q)))
            .Select(PathGuard.Normalise)
            .Distinct(PathGuard.Comparer)
            .ToList();
        try
        {
            IReadOnlySet<string>? logged = null;
            foreach (var (root, dated) in QuarantineMarkers.FromActionLog(File.ReadLines(log), roots))
            {
                if (!Directory.Exists(dated) || QuarantineMarkers.IsMarked(dated))
                {
                    continue;
                }

                // Only a folder holding nothing but files Ingest logged moving there: marking it lets the purge delete it
                logged ??= QuarantineMarkers.QuarantinedFiles(File.ReadLines(log));
                if (QuarantineMarkers.HoldsOnlyLoggedFiles(dated, logged))
                {
                    File.WriteAllText(Path.Combine(dated, QuarantineMarkers.DatedMarker), "Created by the Jellyfin Ingest plugin (marked from its action log).\n");
                    LogMarkedQuarantine(_logger, dated);
                }
                else
                {
                    LogNotMarkedQuarantine(_logger, dated);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMarkerMigrationFailed(_logger, ex);
        }
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
        string dataFolder,
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
            Transfer = watch.Transfer,
        };
        var plan = await planner.PlanAsync(watch.Path, release, files, targets, quarantine, previous?.Chosen, ct).ConfigureAwait(false);

        Directory.CreateDirectory(dataFolder);
        if (!plan.IsReady)
        {
            var retryAt = ScheduleRetry(id, watch, release, plan.Retry);
            var items = plan.Review.Select(r => new PendingReviewItem(Path.GetRelativePath(watch.Path, r.Source), r.Reason) { Existing = r.Existing }).ToList();
            LogNeedsReview(_logger, release, items.Count);
            foreach (var item in items)
            {
                LogNeedsReviewItem(_logger, release, item.Source, item.Reason);
            }

            // Only report a review once per distinct set of reasons (restarts and retries plan the release again).
            if (previous is null || previous.Request != ReviewRequest.None || !previous.Items.SequenceEqual(items))
            {
                _state.Record(new ActivityEntry
                {
                    Time = _clock.GetUtcNow(),
                    Status = ActivityStatus.NeedsReview,
                    Release = release,
                    WatchFolder = watch.Path,
                    Summary = items.Count == 1 ? items[0].Reason : $"{items.Count} files need a decision.",
                    Details = [.. items.Select(i => $"{i.Source}: {i.Reason}")],
                });
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

        _retries.Remove(id);

        // Mark the dated quarantine folder as Ingest's own before anything is moved into it (the purge only deletes marked folders)
        if (!dryRun)
        {
            foreach (var dated in plan.Operations.Where(o => o.Kind == OperationKind.Quarantine).Select(o => DatedFolder(quarantine, o.Destination)).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                QuarantineMarkers.Mark(quarantine, dated);
            }
        }

        var working = new WorkInProgress { Id = id, WatchFolder = watch.Path, Release = release, Stage = "Filing", Since = _clock.GetUtcNow() };
        var report = new PlanExecutor(new PhysicalFileOperations(), _clock) { Filing = (file, count) => _progress.SetWorking(working with { File = file, Files = count }) }
            .Execute(plan, Path.Combine(watch.Path, release), Path.Combine(dataFolder, "actions.jsonl"), dryRun, ct);
        var prefix = dryRun ? "[dry run] would" : "Did";
        // Per-file detail (paths can hold user and share names) only at Debug; one line per release at Information
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var (op, source) in report.Completed.Select(o => (o, Path.GetRelativePath(watch.Path, o.Source))))
            {
                LogOperation(_logger, prefix, op.Kind, source, op.Destination);
            }
        }

        var summaryLine = Summarise(report.Completed, dryRun);
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
            var error = report.Error ?? "unknown error";
            LogExecutionFailed(_logger, release, error);
            var summary = report.Cancelled
                ? error
                : report.RollbackProblems.Count == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Nothing was filed: {error} ({report.RolledBack.Count} completed move(s) were undone.)")
                    : string.Create(CultureInfo.InvariantCulture, $"Failed and couldn't be fully undone: {error}. Needs attention: {string.Join("; ", report.RollbackProblems)}");
            var retryAt = report.FolderUnavailable ? ScheduleRetry(id, watch, release, RetryKind.FolderUnavailable) : null;
            ReportFailure(watch, release, summary, report.Completed, seenVersion, retryAt);
            return;
        }

        _state.RecordUnlessRepeat(new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = dryRun ? ActivityStatus.DryRun : ActivityStatus.Filed,
            Release = release,
            WatchFolder = watch.Path,
            Summary = summaryLine
                + (plan.Replacing.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" Replaced the copies already on the server ({plan.Replacing.Count} file(s), now in quarantine).") : string.Empty)
                + (plan.Skipped.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" {plan.Skipped.Count} file(s) quarantined as chosen, not filed.") : string.Empty)
                + (plan.Notes.Count > 0 ? " The AI plugin decided part of this (see the details)." : string.Empty),
            Details = [.. plan.Notes, .. plan.Replacing.Select(p => "Replaced (moved to quarantine): " + p), .. plan.Skipped.Select(p => "Quarantined as chosen: " + Path.GetRelativePath(watch.Path, p)), .. Describe(watch.Path, report.Completed)],
        });

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
    /// A one-line summary of what an ingest did (or would do).
    /// </summary>
    /// <param name="operations">Completed operations.</param>
    /// <param name="dryRun">Whether nothing was actually moved.</param>
    /// <returns>E.g. <c>Filed 1 video and 2 subtitles; quarantined 3 files.</c>.</returns>
    public static string Summarise(IReadOnlyCollection<PlannedOperation> operations, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(operations);

        static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
        var filed = new List<string>();
        foreach (var (kind, noun) in new[] { (OperationKind.Video, "video"), (OperationKind.Extra, "extra"), (OperationKind.Subtitle, "subtitle") })
        {
            var n = operations.Count(o => o.Kind == kind);
            if (n > 0)
            {
                filed.Add(Count(n, noun));
            }
        }

        var parts = new List<string>();
        if (filed.Count > 0)
        {
            var list = filed.Count == 1 ? filed[0] : string.Join(", ", filed.Take(filed.Count - 1)) + " and " + filed[^1];
            parts.Add((dryRun ? "Would file " : "Filed ") + list);
        }

        var quarantined = operations.Count(o => o.Kind == OperationKind.Quarantine);
        if (quarantined > 0)
        {
            parts.Add((dryRun ? "would quarantine " : "quarantined ") + Count(quarantined, "file"));
        }

        var text = string.Join("; ", parts);
        return text.Length == 0 ? "Nothing to do." : char.ToUpperInvariant(text[0]) + text[1..] + ".";
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

    private static List<string> Describe(string watchFolder, IEnumerable<PlannedOperation> operations)
        => [.. operations.Select(o => $"{o.Kind}: {Path.GetRelativePath(watchFolder, o.Source)} → {o.Destination}")];

    private void ReportFailure(WatchFolder watch, string release, string error, IReadOnlyList<PlannedOperation> completed, int? seenVersion = null, DateTimeOffset? retryAt = null)
    {
        _state.Record(new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = ActivityStatus.Failed,
            Release = release,
            WatchFolder = watch.Path,
            Summary = error,
            Details = Describe(watch.Path, completed),
        });
        _state.PutReview(new PendingReview
        {
            Id = IngestStateStore.ReviewId(watch.Path, release),
            WatchFolder = watch.Path,
            Release = release,
            Time = _clock.GetUtcNow(),
            Items = [new PendingReviewItem(release, error)],
            RetryAt = retryAt,
        },
        seenVersion);
    }

    // A whole release quarantined from the review screen goes through the same crash-safe executor as filing: hidden
    // temporary name then rename, write-ahead action log, rollback on failure, best-effort tidy-up
    private void QuarantineRelease(PluginConfiguration config, WatchFolder watch, PendingReview review, IReadOnlyList<ReleaseFile> files, string quarantine)
    {
        var dryRun = watch.IsDryRun(config.DryRun);
        var dated = QuarantineMarkers.DatedFolderFor(quarantine, DateOnly.FromDateTime(_clock.GetLocalNow().DateTime));
        var fs = new PhysicalFileOperations();
        var taken = new HashSet<string>(PathGuard.Comparer);
        var ops = new List<PlannedOperation>();
        foreach (var file in files)
        {
            var source = Path.Combine(watch.Path, file.RelativePath);
            var destination = Path.Combine(dated, file.RelativePath);
            for (var n = 2; fs.Exists(destination) || taken.Contains(destination); n++)
            {
                destination = Path.Combine(dated, string.Create(CultureInfo.InvariantCulture, $"{file.RelativePath} ({n})"));
            }

            if (!PathGuard.IsUnder(destination, dated) || !PathGuard.IsUnder(source, watch.Path))
            {
                ReportFailure(watch, review.Release, "Refused: a file would leave the watch folder or the quarantine folder.", [], review.RequestVersion);
                return;
            }

            taken.Add(destination);
            ops.Add(new PlannedOperation(OperationKind.Quarantine, source, destination));
        }

        var plan = new IngestPlan { ReleaseName = review.Release, Operations = ops, AllowedRoots = [dated], WholeReleaseQuarantine = true };
        ExecutionReport report;
        try
        {
            if (!dryRun)
            {
                QuarantineMarkers.Mark(quarantine, dated);
            }

            var dataFolder = IngestPlugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The plugin isn't loaded.");
            report = new PlanExecutor(fs, _clock).Execute(plan, Path.Combine(watch.Path, review.Release), Path.Combine(dataFolder, "actions.jsonl"), dryRun);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogExecutionFailed(_logger, review.Release, ex.Message);
            ReportFailure(watch, review.Release, "Quarantine failed: " + ex.Message, [], review.RequestVersion);
            return;
        }

        if (report.Warning is not null)
        {
            LogTidyWarning(_logger, review.Release, report.Warning);
        }

        if (!report.Succeeded)
        {
            var error = report.Error ?? "unknown error";
            LogExecutionFailed(_logger, review.Release, error);
            var summary = report.RollbackProblems.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Nothing was quarantined: {error} ({report.RolledBack.Count} completed move(s) were undone.)")
                : string.Create(CultureInfo.InvariantCulture, $"Quarantine failed and couldn't be fully undone: {error}. Needs attention: {string.Join("; ", report.RollbackProblems)}");
            ReportFailure(watch, review.Release, summary, report.Completed, review.RequestVersion);
            return;
        }

        _state.Record(new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = dryRun ? ActivityStatus.DryRun : ActivityStatus.Quarantined,
            Release = review.Release,
            WatchFolder = watch.Path,
            Summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{(dryRun ? "Would quarantine" : "Quarantined")} the whole release ({files.Count} file{(files.Count == 1 ? string.Empty : "s")}) to {dated}."),
            Details = Describe(watch.Path, report.Completed),
        });
        if (dryRun)
        {
            _state.ClearRequest(review.Id, review.RequestVersion);
        }
        else
        {
            _state.RemoveReview(review.Id);
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest quarantine: {Path} holds files Ingest didn't put there, so it isn't marked and will never be purged")]
    private static partial void LogNotMarkedQuarantine(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest: the last {Count} title searches found nothing; pausing identification for {Minutes} minutes")]
    private static partial void LogProvidersPaused(ILogger logger, int count, double minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest watch folder {Path}: settings changed, planning waiting releases again")]
    private static partial void LogReplanning(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest watch folder {Path} couldn't be swept")]
    private static partial void LogWatchFolderFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest of {Release}: filed, but tidying up the release folder failed: {Warning}")]
    private static partial void LogTidyWarning(ILogger logger, string release, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: marked existing quarantine folder {Folder} as Ingest's own (from the action log)")]
    private static partial void LogMarkedQuarantine(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest: couldn't mark existing quarantine folders")]
    private static partial void LogMarkerMigrationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest recovery: {Result}")]
    private static partial void LogRecovery(ILogger logger, string result);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: removed {Count} old line(s) from the action log")]
    private static partial void LogActionLogTrimmed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest: couldn't trim the action log")]
    private static partial void LogActionLogTrimFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest of {Release} failed")]
    private static partial void LogReleaseFailed(ILogger logger, string release, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest of {Release} stopped: {Error}")]
    private static partial void LogExecutionFailed(ILogger logger, string release, string error);

    // What one sweep shares across its releases: the identifier (with its library index, loaded once) and the server's
    // library folders
    private sealed record Sweep(MediaIdentifier Identifier, IReadOnlyList<string> LibraryFolders);
}
