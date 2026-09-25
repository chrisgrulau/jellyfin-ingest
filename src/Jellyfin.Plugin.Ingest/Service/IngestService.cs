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

    private const long MaxSubtitleBytesToRead = 4L * 1024 * 1024;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<IngestService> _logger;
    private readonly IngestStateStore _state;
    private readonly Dictionary<string, ReleaseTracker> _trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _folderErrors = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = TimeProvider.System;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="providerManager">Jellyfin provider manager.</param>
    /// <param name="state">Reviews and activity shown on the dashboard.</param>
    /// <param name="logger">Logger.</param>
    public IngestService(ILibraryManager libraryManager, IProviderManager providerManager, IngestStateStore state, ILogger<IngestService> logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
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
                await Task.Delay(SweepInterval, ct).ConfigureAwait(false);
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
        foreach (var watch in config.WatchFolders.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Path)))
        {
            // Each watch folder on its own: one that can't be read (permissions, offline share) mustn't stop the others
            try
            {
                await SweepFolderAsync(plugin, config, libraries, watch, ct).ConfigureAwait(false);
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
    }

    private async Task SweepFolderAsync(IngestPlugin plugin, PluginConfiguration config, IReadOnlyCollection<MediaLibrary> libraries, WatchFolder watch, CancellationToken ct)
    {
        if (!Directory.Exists(watch.Path))
        {
            LogMissingWatchFolder(_logger, watch.Path);
            return;
        }

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
        var settings = LibraryRouting.SettingsFingerprint(config.DryRun, DestinationsOf(watch));
        if (_settings.TryGetValue(watch.Path, out var before) && !string.Equals(before, settings, StringComparison.Ordinal))
        {
            LogReplanning(_logger, watch.Path);
            tracker.ForgetAll();
        }

        _settings[watch.Path] = settings;

        _state.PruneReviews(watch.Path, [.. snapshot.Keys, .. scan.Links, .. scan.Unsettled]);
        ReviewLinks(watch, scan.Links);
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
                    ReportFailure(watch, review.Release, "Quarantine failed: " + ex.Message, []);
                }
            }
            else
            {
                tracker.Forget(review.Release);
            }
        }

        foreach (var release in tracker.Observe(snapshot, _clock.GetUtcNow(), TimeSpan.FromSeconds(Math.Max(5, config.SettleSeconds))))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await IngestAsync(plugin.DataFolderPath, config, watch, release, snapshot[release], targets, quarantine, ct).ConfigureAwait(false);
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
                ReportFailure(watch, release, ex.Message, []);
            }

            tracker.MarkHandled(release);
        }
    }

    // A top-level symbolic link is never followed; it waits for review so the user knows why nothing happened
    private void ReviewLinks(WatchFolder watch, IReadOnlyList<string> links)
    {
        foreach (var link in links)
        {
            var id = IngestStateStore.ReviewId(watch.Path, link);
            if (_state.GetReview(id) is not null)
            {
                continue;
            }

            const string Reason = "This is a symbolic link. Links aren't followed, so nothing outside the watch folder can be moved; move or copy the files themselves into the watch folder.";
            _state.Record(new ActivityEntry { Time = _clock.GetUtcNow(), Status = ActivityStatus.NeedsReview, Release = link, WatchFolder = watch.Path, Summary = Reason });
            _state.PutReview(new PendingReview { Id = id, WatchFolder = watch.Path, Release = link, Time = _clock.GetUtcNow(), Items = [new PendingReviewItem(link, Reason)] });
        }
    }

    private async Task IngestAsync(
        string dataFolder,
        PluginConfiguration config,
        WatchFolder watch,
        string release,
        IReadOnlyList<ReleaseFile> files,
        LibraryTargets targets,
        string quarantine,
        CancellationToken ct)
    {
        var id = IngestStateStore.ReviewId(watch.Path, release);
        var previous = _state.GetReview(id);
        // Titles anywhere on the server count as "already in the library": a show kept in another library is still
        // strong evidence, and its new episodes join it there.
        var identifier = new MediaIdentifier(
            new JellyfinMetadataLookup(_providerManager),
            new JellyfinLibraryIndex(_libraryManager, []));
        // Existing shows may only be joined inside one of the server's library folders
        var libraryFolders = Libraries(_libraryManager.GetVirtualFolders()).SelectMany(l => l.Locations).ToList();
        var planner = new IngestPlanner(
            identifier,
            p => File.Exists(p) || Directory.Exists(p),
            ReadSmallText,
            _clock,
            new JellyfinExistingMedia(_libraryManager),
            p => PathGuard.IsUnderAny(p, libraryFolders));
        var plan = await planner.PlanAsync(watch.Path, release, files, targets, quarantine, previous?.Chosen, ct).ConfigureAwait(false);

        Directory.CreateDirectory(dataFolder);
        if (!plan.IsReady)
        {
            var items = plan.Review.Select(r => new PendingReviewItem(Path.GetRelativePath(watch.Path, r.Source), r.Reason)).ToList();
            foreach (var item in items)
            {
                LogNeedsReview(_logger, release, item.Source, item.Reason);
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
            });
            return;
        }

        var report = new PlanExecutor(new PhysicalFileOperations(), _clock)
            .Execute(plan, Path.Combine(watch.Path, release), Path.Combine(dataFolder, "actions.jsonl"), config.DryRun);
        var prefix = config.DryRun ? "[dry run] would" : "Did";
        foreach (var op in report.Completed)
        {
            LogOperation(_logger, prefix, op.Kind, op.Source, op.Destination);
        }

        if (report.Warning is not null)
        {
            LogTidyWarning(_logger, release, report.Warning);
        }

        if (!report.Succeeded)
        {
            var error = report.Error ?? "unknown error";
            LogExecutionFailed(_logger, release, error);
            ReportFailure(watch, release, $"Stopped after {report.Completed.Count} of {plan.Operations.Count} moves: {error}", report.Completed);
            return;
        }

        _state.RecordUnlessRepeat(new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = config.DryRun ? ActivityStatus.DryRun : ActivityStatus.Filed,
            Release = release,
            WatchFolder = watch.Path,
            Summary = Summarise(report.Completed, config.DryRun),
            Details = Describe(watch.Path, report.Completed),
        });

        if (config.DryRun && previous?.Chosen is { } chosen)
        {
            // Keep the decision so the release files the same way once dry run is turned off.
            _state.MarkPlannedInDryRun(id, $"Dry run: planned as {chosen.Candidate.Name}{(chosen.Candidate.Year is { } y ? $" ({y})" : string.Empty)}; it will be filed once dry run is off.");
        }
        else
        {
            _state.RemoveReview(id);
        }

        if (!config.DryRun && config.ScanLibraryAfterIngest)
        {
            _libraryManager.QueueLibraryScan();
        }
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

    private void ReportFailure(WatchFolder watch, string release, string error, IReadOnlyList<PlannedOperation> completed)
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
        });
    }

    private void QuarantineRelease(PluginConfiguration config, WatchFolder watch, PendingReview review, IReadOnlyList<ReleaseFile> files, string quarantine)
    {
        var dated = Path.Combine(quarantine, _clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var fs = new PhysicalFileOperations();
        var moved = new List<string>();
        string? error = null;
        if (!config.DryRun)
        {
            foreach (var file in files)
            {
                var destination = Path.Combine(dated, file.RelativePath);
                for (var n = 2; fs.Exists(destination); n++)
                {
                    destination = Path.Combine(dated, string.Create(CultureInfo.InvariantCulture, $"{file.RelativePath} ({n})"));
                }

                if (!PathGuard.IsUnder(destination, dated) || !PathGuard.IsUnder(Path.Combine(watch.Path, file.RelativePath), watch.Path))
                {
                    error = "Refused: a file would leave the watch folder or the quarantine folder.";
                    break;
                }

                try
                {
                    fs.CreateDirectory(Path.GetDirectoryName(destination)!);
                    fs.Move(Path.Combine(watch.Path, file.RelativePath), destination);
                    moved.Add($"{file.RelativePath} → {destination}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = ex.Message;
                    break;
                }
            }

            var root = Path.Combine(watch.Path, review.Release);
            if (error is null && Directory.Exists(root))
            {
                fs.DeleteEmptyDirectories(root);
            }
        }

        if (error is not null)
        {
            LogExecutionFailed(_logger, review.Release, error);
            ReportFailure(watch, review.Release, $"Quarantine stopped after {moved.Count} of {files.Count} files: {error}", []);
            return;
        }

        _state.Record(new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = config.DryRun ? ActivityStatus.DryRun : ActivityStatus.Quarantined,
            Release = review.Release,
            WatchFolder = watch.Path,
            Summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{(config.DryRun ? "Would quarantine" : "Quarantined")} the whole release ({files.Count} file{(files.Count == 1 ? string.Empty : "s")}) to {dated}."),
            Details = moved,
        });
        if (config.DryRun)
        {
            _state.ClearRequest(review.Id);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: {Release} needs review - {Source}: {Reason}")]
    private static partial void LogNeedsReview(ILogger logger, string release, string source, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: {Prefix} {Kind} {Source} -> {Destination}")]
    private static partial void LogOperation(ILogger logger, string prefix, OperationKind kind, string source, string destination);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest of {Release} failed")]
    private static partial void LogReleaseFailed(ILogger logger, string release, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest of {Release} stopped: {Error}")]
    private static partial void LogExecutionFailed(ILogger logger, string release, string error);
}
