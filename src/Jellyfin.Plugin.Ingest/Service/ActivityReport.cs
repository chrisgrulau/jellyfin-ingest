using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Planning;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// How the sweep's work reads in the activity panel: one-line summaries, per-file details, and the entries recorded for
/// a release that was filed, needs a decision or failed.
/// </summary>
public sealed class ActivityReport
{
    private readonly IngestStateStore _state;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityReport"/> class.
    /// </summary>
    /// <param name="state">Reviews and activity shown on the dashboard.</param>
    /// <param name="clock">Clock.</param>
    public ActivityReport(IngestStateStore state, TimeProvider clock)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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
    /// One detail line per operation: its kind, the source relative to the watch folder, and where it went.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="operations">The operations.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Describe(string watchFolder, IEnumerable<PlannedOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(watchFolder);
        ArgumentNullException.ThrowIfNull(operations);
        return [.. operations.Select(o => $"{o.Kind}: {Path.GetRelativePath(watchFolder, o.Source)} → {o.Destination}")];
    }

    /// <summary>
    /// What a failed filing says: the error, whether the completed moves were undone, and what needs attention if not.
    /// </summary>
    /// <param name="report">The failed execution.</param>
    /// <returns>The summary.</returns>
    public static string FilingFailed(ExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var error = report.Error ?? "unknown error";
        return report.Cancelled
            ? error
            : report.RollbackProblems.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Nothing was filed: {error} ({report.RolledBack.Count} completed move(s) were undone.)")
                : string.Create(CultureInfo.InvariantCulture, $"Failed and couldn't be fully undone: {error}. Needs attention: {string.Join("; ", report.RollbackProblems)}");
    }

    /// <summary>
    /// What a failed whole-release quarantine says.
    /// </summary>
    /// <param name="report">The failed execution.</param>
    /// <returns>The summary.</returns>
    public static string QuarantineFailed(ExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var error = report.Error ?? "unknown error";
        return report.RollbackProblems.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Nothing was quarantined: {error} ({report.RolledBack.Count} completed move(s) were undone.)")
            : string.Create(CultureInfo.InvariantCulture, $"Quarantine failed and couldn't be fully undone: {error}. Needs attention: {string.Join("; ", report.RollbackProblems)}");
    }

    /// <summary>
    /// The entry for a release that was filed (or would be, in dry run).
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="release">The release.</param>
    /// <param name="plan">The plan that was carried out.</param>
    /// <param name="completed">The operations completed.</param>
    /// <param name="summaryLine">The summary from <see cref="Summarise"/>.</param>
    /// <param name="dryRun">Whether nothing was actually moved.</param>
    /// <returns>The entry.</returns>
    public ActivityEntry Filed(string watchFolder, string release, IngestPlan plan, IReadOnlyList<PlannedOperation> completed, string summaryLine, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(watchFolder);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(completed);
        return new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = dryRun ? ActivityStatus.DryRun : ActivityStatus.Filed,
            Release = release,
            WatchFolder = watchFolder,
            Summary = summaryLine
                + (plan.Replacing.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" Replaced the copies already on the server ({plan.Replacing.Count} file(s), now in quarantine).") : string.Empty)
                + (plan.Skipped.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" {plan.Skipped.Count} file(s) quarantined as chosen, not filed.") : string.Empty)
                + (plan.Notes.Count > 0 ? " The AI plugin decided part of this (see the details)." : string.Empty),
            Details = [.. plan.Notes, .. plan.Replacing.Select(p => "Replaced (moved to quarantine): " + p), .. plan.Skipped.Select(p => "Quarantined as chosen: " + Path.GetRelativePath(watchFolder, p)), .. Describe(watchFolder, completed)],
        };
    }

    /// <summary>
    /// The entry for a release that needs a decision.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="release">The release.</param>
    /// <param name="items">What needs deciding, and why.</param>
    /// <returns>The entry.</returns>
    public ActivityEntry NeedsReview(string watchFolder, string release, IReadOnlyList<PendingReviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = ActivityStatus.NeedsReview,
            Release = release,
            WatchFolder = watchFolder,
            Summary = items.Count == 1 ? items[0].Reason : $"{items.Count} files need a decision.",
            Details = [.. items.Select(i => $"{i.Source}: {i.Reason}")],
        };
    }

    /// <summary>
    /// Records a release that failed, and leaves it waiting for review with the error as its reason.
    /// </summary>
    /// <param name="watch">The watch folder.</param>
    /// <param name="release">The release.</param>
    /// <param name="error">What went wrong.</param>
    /// <param name="completed">Operations completed before it failed.</param>
    /// <param name="seenVersion">The review request version this attempt acted on, if any.</param>
    /// <param name="retryAt">When it will be tried again by itself, if it will.</param>
    public void ReportFailure(WatchFolder watch, string release, string error, IReadOnlyList<PlannedOperation> completed, int? seenVersion = null, DateTimeOffset? retryAt = null)
    {
        ArgumentNullException.ThrowIfNull(watch);
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
}
