using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest.Quarantine;

/// <summary>
/// Quarantines a whole release, as asked from the review screen. It goes through the same crash-safe executor as
/// filing: hidden temporary name then rename, write-ahead action log, rollback on failure, best-effort tidy-up.
/// </summary>
public sealed partial class QuarantineRelease
{
    private readonly IngestStateStore _state;
    private readonly ActivityReport _activity;
    private readonly IngestPaths _paths;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuarantineRelease"/> class.
    /// </summary>
    /// <param name="state">Reviews and activity shown on the dashboard.</param>
    /// <param name="activity">Records failures.</param>
    /// <param name="paths">Where the action log is.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">The service's logger.</param>
    public QuarantineRelease(IngestStateStore state, ActivityReport activity, IngestPaths paths, TimeProvider clock, ILogger logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Moves every file of a release into today's dated quarantine folder (or says what would move, in dry run), then
    /// clears its review. A failure is reported and the review kept.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="watch">The watch folder.</param>
    /// <param name="review">The review asking for it.</param>
    /// <param name="files">The release's files.</param>
    /// <param name="quarantine">The watch folder's quarantine.</param>
    public void Run(PluginConfiguration config, WatchFolder watch, PendingReview review, IReadOnlyList<ReleaseFile> files, string quarantine)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(files);
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
                _activity.ReportFailure(watch, review.Release, "Refused: a file would leave the watch folder or the quarantine folder.", [], review.RequestVersion);
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

            report = new PlanExecutor(fs, _clock).Execute(plan, Path.Combine(watch.Path, review.Release), _paths.ActionLog, dryRun);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogExecutionFailed(_logger, review.Release, ex.Message);
            _activity.ReportFailure(watch, review.Release, "Quarantine failed: " + ex.Message, [], review.RequestVersion);
            return;
        }

        if (report.Warning is not null)
        {
            LogTidyWarning(_logger, review.Release, report.Warning);
        }

        if (!report.Succeeded)
        {
            LogExecutionFailed(_logger, review.Release, report.Error ?? "unknown error");
            _activity.ReportFailure(watch, review.Release, ActivityReport.QuarantineFailed(report), report.Completed, review.RequestVersion);
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
            Details = ActivityReport.Describe(watch.Path, report.Completed),
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest of {Release}: filed, but tidying up the release folder failed: {Warning}")]
    private static partial void LogTidyWarning(ILogger logger, string release, string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest of {Release} stopped: {Error}")]
    private static partial void LogExecutionFailed(ILogger logger, string release, string error);
}
