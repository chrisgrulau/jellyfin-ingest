using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// The folders an undo or restore may touch, as they are configured now.
/// </summary>
/// <param name="WatchFolders">The configured watch folders.</param>
/// <param name="LibraryFolders">The server's library folders.</param>
/// <param name="QuarantineRoots">The quarantine folders in use.</param>
public sealed record ReturnScope(IReadOnlyList<string> WatchFolders, IReadOnlyList<string> LibraryFolders, IReadOnlyList<string> QuarantineRoots)
{
    /// <summary>
    /// The library, quarantine or watch folder a path is inside (the deepest), if any.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The folder, or <c>null</c>.</returns>
    public string? RootOf(string path)
        => LibraryFolders.Concat(QuarantineRoots).Concat(WatchFolders)
            .Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r) && PathGuard.IsUnder(path, r))
            .OrderByDescending(r => PathGuard.Normalise(r).Length)
            .FirstOrDefault();

    /// <summary>
    /// The folders between a file and the library, quarantine or watch folder it is in (deepest first, never that
    /// folder itself): they are removed after an undo or restore if it leaves them truly empty.
    /// </summary>
    /// <param name="files">The files moved away.</param>
    /// <param name="stopAt">A folder to stop at as well (a dated quarantine folder), or <c>null</c>.</param>
    /// <returns>The folders, deepest first.</returns>
    public IReadOnlyList<EmptiedFolder> FoldersLeftBy(IEnumerable<string> files, string? stopAt = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var folders = new Dictionary<string, EmptiedFolder>(PathGuard.Comparer);
        foreach (var file in files)
        {
            var root = stopAt is not null && PathGuard.IsUnder(file, stopAt) ? stopAt : RootOf(file);
            if (root is null)
            {
                continue;
            }

            for (var dir = Path.GetDirectoryName(file); dir is not null && PathGuard.IsUnder(dir, root); dir = Path.GetDirectoryName(dir))
            {
                folders.TryAdd(PathGuard.Normalise(dir), new EmptiedFolder(dir, root));
            }
        }

        return [.. folders.Values.OrderByDescending(f => PathGuard.Normalise(f.Folder).Length)];
    }
}

/// <summary>
/// What an undo or restore would do, or why it can't.
/// </summary>
public sealed record ReturnPlan
{
    /// <summary>Gets why nothing can be done, or <c>null</c> when <see cref="Plan"/> can be carried out.</summary>
    public string? Refusal { get; init; }

    /// <summary>Gets the moves, for the executor.</summary>
    public IngestPlan? Plan { get; init; }

    /// <summary>Gets the moves that set a library copy aside, to be deleted once every move has succeeded (copy and hard-link folders).</summary>
    public IReadOnlyList<PlannedOperation> SetAside { get; init; } = [];

    /// <summary>Gets the folders to remove afterwards if they are left truly empty, deepest first.</summary>
    public IReadOnlyList<EmptiedFolder> Tidy { get; init; } = [];

    /// <summary>Gets the releases (watch folder, release name) files go back into; each then waits for review.</summary>
    public IReadOnlyList<(string WatchFolder, string Release)> Releases { get; init; } = [];

    /// <summary>Gets the library folders whose contents change, for refreshing just those.</summary>
    public IReadOnlyList<string> Refresh { get; init; } = [];

    /// <summary>Creates a refusal.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The plan.</returns>
    public static ReturnPlan Refuse(string reason) => new() { Refusal = reason };
}

/// <summary>
/// What an undo or restore did, for the API and the sweep.
/// </summary>
/// <param name="Succeeded">Whether it was carried out.</param>
/// <param name="Message">What happened, in plain language.</param>
/// <param name="Refresh">Library folders to refresh.</param>
public sealed record ReturnOutcome(bool Succeeded, string Message, IReadOnlyList<string> Refresh);

/// <summary>
/// Undoes a filing, from the moves its run recorded in the action log: filed files go back to where they were in the
/// watch folder, clutter comes back out of quarantine, and copies it replaced go back into the library. For a copy or
/// hard-link watch folder (the release stayed for seeding) the library copies are deleted instead, once the originals
/// are checked. It goes through the same executor as filing (hidden temporary names, verified sizes, a write-ahead
/// log, rollback on failure), and it is all or nothing: if any file has changed or gone since it was filed, or a place
/// it would go back to is taken, nothing is done. The release then waits for review, so it isn't filed again by itself.
/// </summary>
public sealed class ReleaseUndo
{
    /// <summary>The reason an undone release waits for review.</summary>
    public const string HeldReason = "Undone by an administrator — choose what to do.";

    private readonly IngestStateStore _state;
    private readonly IFileOperations _fs;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReleaseUndo"/> class.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="fileOperations">File-system access.</param>
    /// <param name="clock">Clock.</param>
    public ReleaseUndo(IngestStateStore state, IFileOperations fileOperations, TimeProvider clock)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _fs = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Plans the undo of a filing. Nothing is changed.
    /// </summary>
    /// <param name="filing">The filing's activity entry.</param>
    /// <param name="moves">The moves its run completed (<see cref="ActionLogLine.CompletedMoves"/>), in order.</param>
    /// <param name="scope">The folders as configured now.</param>
    /// <param name="fs">File-system access.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The plan, or why it can't be undone.</returns>
    public static ReturnPlan Plan(ActivityEntry filing, IReadOnlyList<ActionLogLine> moves, ReturnScope scope, IFileOperations fs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(filing);
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fs);
        if (filing.Status != ActivityStatus.Filed || string.IsNullOrEmpty(filing.Run))
        {
            return ReturnPlan.Refuse("Only a release Ingest filed (since undo was added) can be undone.");
        }

        if (filing.UndoneAt is not null)
        {
            return ReturnPlan.Refuse("This filing has already been undone.");
        }

        if (now - filing.Time > ActionLog.MaxAge || moves.Count == 0)
        {
            return ReturnPlan.Refuse("The action log no longer holds this filing's moves (it keeps 90 days), so it can't be undone.");
        }

        var watch = scope.WatchFolders.FirstOrDefault(w => PathGuard.SamePath(w, filing.WatchFolder));
        if (watch is null)
        {
            return ReturnPlan.Refuse("The watch folder it came from is no longer set up in Ingest, so there is nowhere to put it back.");
        }

        if (!fs.Exists(watch))
        {
            return ReturnPlan.Refuse("The watch folder isn't available (is it mounted?): " + watch);
        }

        var ops = new List<PlannedOperation>();
        var setAside = new List<PlannedOperation>();
        var freed = new HashSet<string>(PathGuard.Comparer);
        var taken = new HashSet<string>(PathGuard.Comparer);
        var required = new HashSet<string>(PathGuard.Comparer) { watch };
        var refresh = new HashSet<string>(PathGuard.Comparer);
        foreach (var m in moves.Reverse())
        {
            var kind = m.OperationKind;
            var fromRelease = PathGuard.IsUnder(m.Source, watch);
            var sourceRoot = fromRelease ? watch
                : kind == OperationKind.Quarantine ? scope.LibraryFolders.FirstOrDefault(l => PathGuard.IsUnder(m.Source, l))
                : null;
            if (sourceRoot is null)
            {
                return ReturnPlan.Refuse($"{m.Source} is outside the watch folder and the libraries, so nothing is put back.");
            }

            var filedInto = kind == OperationKind.Quarantine
                ? scope.QuarantineRoots.FirstOrDefault(q => PathGuard.IsUnder(m.Destination, q))
                : scope.LibraryFolders.FirstOrDefault(l => PathGuard.IsUnder(m.Destination, l));
            if (filedInto is null)
            {
                return ReturnPlan.Refuse($"{m.Destination} is no longer inside a library or quarantine folder Ingest uses, so it isn't moved.");
            }

            if (!fs.Exists(m.Destination))
            {
                return ReturnPlan.Refuse($"{m.Destination} is missing, so the filing can't be undone completely. Nothing was changed.");
            }

            if (fs.Length(m.Destination) != m.Bytes || (m.ModifiedUtc is { } logged && fs.LastWriteUtc(m.Destination) is { } current && current.ToUniversalTime() != logged))
            {
                return ReturnPlan.Refuse($"{m.Destination} has changed since it was filed, so the filing isn't undone. Nothing was changed.");
            }

            if (m.Kept == true)
            {
                // Filed by copy or hard link: the original stayed in the watch folder, so the library copy is deleted
                if (!fs.Exists(m.Source) || fs.Length(m.Source) != m.Bytes)
                {
                    return ReturnPlan.Refuse($"The original {m.Source} in the watch folder has gone or changed, so the library copy isn't deleted. Nothing was changed.");
                }

                var aside = new PlannedOperation(kind, m.Destination, PlanExecutor.SetAsidePathFor(m.Destination));
                ops.Add(aside);
                setAside.Add(aside);
            }
            else
            {
                // A place taken now, unless an earlier step of this undo frees it (a replaced copy goes back where its
                // replacement was)
                if ((fs.Exists(m.Source) && !freed.Contains(m.Source)) || !taken.Add(m.Source))
                {
                    return ReturnPlan.Refuse($"{m.Source} is taken now, so the filing isn't undone. Nothing was changed.");
                }

                ops.Add(new PlannedOperation(kind, m.Destination, m.Source));
            }

            freed.Add(m.Destination);
            if (!fromRelease)
            {
                required.Add(sourceRoot);
            }

            if (kind != OperationKind.Quarantine)
            {
                required.Add(filedInto);
                refresh.Add(TitleFolder(m.Destination, filedInto));
            }
            else if (!fromRelease)
            {
                refresh.Add(TitleFolder(m.Source, sourceRoot));
            }
        }

        var release = filing.Release;
        return new ReturnPlan
        {
            Plan = new IngestPlan
            {
                ReleaseName = release,
                Operations = ops,
                Returning = [.. ops.Select(o => o.Source)],
                AllowedRoots = [.. scope.LibraryFolders.Append(watch).Distinct(PathGuard.Comparer)],
                RequiredFolders = [.. required],
            },
            SetAside = setAside,
            Tidy = scope.FoldersLeftBy(moves.Select(m => m.Destination)),
            Releases = [(watch, release)],
            Refresh = [.. refresh],
        };
    }

    /// <summary>
    /// Checks whether a filing can be undone now, without changing anything (for the page's immediate answer).
    /// </summary>
    /// <param name="run">The filing's run.</param>
    /// <param name="scope">The folders as configured now.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <returns>Why it can't be undone, or <c>null</c> if it can.</returns>
    public string? Check(string run, ReturnScope scope, IEnumerable<string> actionLogLines)
    {
        var filing = _state.FindFiling(run);
        return filing is null
            ? "That filing isn't in Recent activity any more."
            : Plan(filing, ActionLogLine.CompletedMoves(actionLogLines, run), scope, _fs, _clock.GetUtcNow()).Refusal;
    }

    /// <summary>
    /// Undoes a filing (all or nothing), records it, and leaves the release waiting for review.
    /// </summary>
    /// <param name="run">The filing's run.</param>
    /// <param name="scope">The folders as configured now.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <param name="actionLogPath">The action log, which the undo's own moves are written to.</param>
    /// <returns>What happened.</returns>
    public ReturnOutcome Undo(string run, ReturnScope scope, IEnumerable<string> actionLogLines, string actionLogPath)
    {
        var filing = _state.FindFiling(run);
        if (filing is null)
        {
            return new ReturnOutcome(false, "That filing isn't in Recent activity any more.", []);
        }

        var planned = Plan(filing, ActionLogLine.CompletedMoves(actionLogLines, run), scope, _fs, _clock.GetUtcNow());
        if (planned.Refusal is not null)
        {
            Record(filing, ActivityStatus.Failed, "Couldn't undo: " + planned.Refusal, []);
            return new ReturnOutcome(false, planned.Refusal, []);
        }

        var watch = planned.Releases[0].WatchFolder;
        var held = Hold(_state, planned.Releases, _clock.GetUtcNow(), HeldReason);

        var executor = new PlanExecutor(_fs, _clock);
        var report = executor.Execute(planned.Plan!, Path.Combine(watch, filing.Release), actionLogPath, dryRun: false);
        if (!report.Succeeded)
        {
            Release(_state, held);
            var why = report.RollbackProblems.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Undo failed, and everything was put back as it was: {report.Error} ({report.RolledBack.Count} completed move(s) were reversed.)")
                : $"Undo failed and couldn't be fully rolled back: {report.Error}. Needs attention: {string.Join("; ", report.RollbackProblems)}";
            Record(filing, ActivityStatus.Failed, why, ActivityReport.Describe(watch, report.Completed));
            return new ReturnOutcome(false, why, []);
        }

        var problems = executor.DeleteSetAside(planned.SetAside, filing.Release, report.Run!, actionLogPath, scope.LibraryFolders).ToList();
        TidyUp(_fs, planned.Tidy, problems);

        var now = _clock.GetUtcNow();
        _state.MarkUndone(run, now);
        var returned = planned.Plan!.Operations.Count(o => PathGuard.IsUnder(o.Destination, watch));
        var restored = planned.Plan.Operations.Count(o => o.Kind == OperationKind.Quarantine && !PathGuard.IsUnder(o.Destination, watch));
        var parts = new List<string>();
        if (returned > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{returned} file(s) moved back to the watch folder"));
        }

        if (planned.SetAside.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{planned.SetAside.Count} library copy(ies) deleted (the originals stayed in the watch folder)"));
        }

        if (restored > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{restored} replaced copy(ies) put back into the library"));
        }

        var summary = "Undone by an administrator: " + string.Join("; ", parts) + ". It now waits under Needs review.";
        Record(filing, ActivityStatus.Undone, summary, [.. problems.Select(p => "Needs attention: " + p), .. planned.Plan.Operations.Select(o => $"{o.Kind}: {o.Source} → {(planned.SetAside.Contains(o) ? "deleted" : o.Destination)}")]);
        return new ReturnOutcome(true, summary, planned.Refresh);
    }

    /// <summary>
    /// Puts releases into review, held so they aren't filed again by themselves, before anything is moved back (so a
    /// crash part-way still leaves them waiting). A copy or hard-link folder's record of having filed them is cleared,
    /// so the review sees them.
    /// </summary>
    /// <param name="state">Reviews.</param>
    /// <param name="releases">The releases.</param>
    /// <param name="now">Current time.</param>
    /// <param name="reason">Why they wait.</param>
    /// <returns>What to put back if nothing is moved after all.</returns>
    internal static IReadOnlyList<(string Id, bool HadReview, CopiedRelease? Copied)> Hold(IngestStateStore state, IEnumerable<(string WatchFolder, string Release)> releases, DateTimeOffset now, string reason)
    {
        var held = new List<(string, bool, CopiedRelease?)>();
        foreach (var (watch, release) in releases.Distinct())
        {
            var id = IngestStateStore.ReviewId(watch, release);
            var had = state.GetReview(id) is not null;
            var copied = state.ForgetCopied(watch, release);
            state.PutReview(new PendingReview { Id = id, WatchFolder = watch, Release = release, Time = now, Items = [new PendingReviewItem(release, reason)], Held = true });
            held.Add((id, had, copied));
        }

        return held;
    }

    /// <summary>
    /// Undoes <see cref="Hold"/> after a failure that put everything back.
    /// </summary>
    /// <param name="state">Reviews.</param>
    /// <param name="held">What <see cref="Hold"/> returned.</param>
    internal static void Release(IngestStateStore state, IEnumerable<(string Id, bool HadReview, CopiedRelease? Copied)> held)
    {
        foreach (var (id, had, copied) in held)
        {
            if (!had)
            {
                state.RemoveReview(id);
            }

            if (copied is not null)
            {
                state.MarkCopied(copied);
            }
        }
    }

    /// <summary>
    /// Removes folders left truly empty, never a library, quarantine or watch folder itself.
    /// </summary>
    /// <param name="fs">File-system access.</param>
    /// <param name="folders">The folders, deepest first.</param>
    /// <param name="problems">Where anything that couldn't be removed is noted.</param>
    internal static void TidyUp(IFileOperations fs, IEnumerable<EmptiedFolder> folders, List<string> problems)
    {
        foreach (var f in folders.Where(f => PathGuard.IsUnder(f.Folder, f.LibraryRoot)))
        {
            try
            {
                fs.DeleteIfEmpty(f.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{f.Folder}: {ex.Message}");
            }
        }
    }

    // The film or show folder a library file is in (the first folder under the library folder)
    private static string TitleFolder(string file, string root)
    {
        var first = Path.GetRelativePath(PathGuard.Normalise(root), PathGuard.Normalise(file)).Split(Path.DirectorySeparatorChar)[0];
        return Path.Combine(root, first);
    }

    private void Record(ActivityEntry filing, ActivityStatus status, string summary, IReadOnlyList<string> details)
        => _state.Record(new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = status,
            Release = filing.Release,
            WatchFolder = filing.WatchFolder,
            Summary = summary,
            Details = details,
        });
}
