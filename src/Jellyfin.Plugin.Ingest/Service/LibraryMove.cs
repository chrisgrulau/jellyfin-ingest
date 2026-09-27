using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Presentation;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// The folders and libraries a move to another library works with, as configured now.
/// </summary>
/// <param name="Libraries">The server's libraries.</param>
/// <param name="Folders">The watch, library and quarantine folders (for tidying up, as an undo does).</param>
/// <param name="Forbidden">Folders no library folder filed into may overlap: every configured watch and quarantine
/// folder (safe or not), and Jellyfin's own folders.</param>
/// <param name="FreeBytes">The free space on a folder's drive (<c>null</c> if unknown), to choose between a library's
/// folders as filing does; <c>null</c> for the first folder.</param>
/// <param name="ListDirectories">Lists a folder's sub-folders (empty if it doesn't exist).</param>
/// <param name="ListFiles">Lists a folder's files (empty if it doesn't exist).</param>
public sealed record MoveScope(
    IReadOnlyList<MediaLibrary> Libraries,
    ReturnScope Folders,
    IReadOnlyList<string> Forbidden,
    Func<string, long?>? FreeBytes,
    Func<string, IEnumerable<string>> ListDirectories,
    Func<string, IEnumerable<string>> ListFiles);

/// <summary>
/// A library a filing can be moved to.
/// </summary>
/// <param name="LibraryId">The library's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Folder">The library folder the files would go into.</param>
/// <param name="HasTitle">Whether that folder already holds a film or show folder of the same name (it is reused).</param>
public sealed record MoveTarget(string LibraryId, string Name, LibraryKind Kind, string Folder, bool HasTitle);

/// <summary>
/// The libraries a filing can be moved to, for the page's picker.
/// </summary>
public sealed record MoveOptions
{
    /// <summary>Gets the library the filing is in now (its name).</summary>
    public required string From { get; init; }

    /// <summary>Gets a value indicating whether it is a show (else a film).</summary>
    public bool IsSeries { get; init; }

    /// <summary>Gets the compatible libraries, other than the one it is in.</summary>
    public IReadOnlyList<MoveTarget> Targets { get; init; } = [];

    /// <summary>Gets whether the target library has several folders, so the page shows which one is used.</summary>
    public bool ShowFolders { get; init; }
}

/// <summary>
/// What a move would do, or why it can't.
/// </summary>
public sealed record MovePlan
{
    /// <summary>Gets why nothing can be done, or <c>null</c> when <see cref="Plan"/> can be carried out.</summary>
    public string? Refusal { get; init; }

    /// <summary>Gets the moves, for the executor.</summary>
    public IngestPlan? Plan { get; init; }

    /// <summary>Gets the library the files are in now.</summary>
    public MediaLibrary? From { get; init; }

    /// <summary>Gets the library they go to.</summary>
    public MediaLibrary? To { get; init; }

    /// <summary>Gets the film or show folder the files leave.</summary>
    public string SourceTitle { get; init; } = string.Empty;

    /// <summary>Gets the film or show folder they go into.</summary>
    public string TargetTitle { get; init; } = string.Empty;

    /// <summary>Gets the folders to remove afterwards if they are left truly empty, deepest first.</summary>
    public IReadOnlyList<EmptiedFolder> Tidy { get; init; } = [];

    /// <summary>Gets how many subtitle files had changed since filing (they are moved as they are now).</summary>
    public int ChangedSubtitles { get; init; }

    /// <summary>Creates a refusal.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The plan.</returns>
    public static MovePlan Refuse(string reason) => new() { Refusal = reason };
}

/// <summary>
/// Moves a filed film or show to another library that can hold it, when Ingest chose the library itself (not one picked
/// in review). Every file the filing put in the library (videos, subtitles, extras) moves to the same place under the
/// other library (the same film or show folder name and season folders, reusing a folder of that name already there),
/// through the same executor as filing (hidden temporary names, verified sizes, a write-ahead log, rollback on failure).
/// It is all or nothing: if any file has gone or changed (a subtitle may have changed, and moves as it is), or a place
/// it would go is taken (such as the same episode already in that library), nothing is moved. Copies the filing replaced
/// stay in quarantine. The filing can still be undone afterwards: an undo follows the files to where they were moved.
/// </summary>
public sealed partial class LibraryMove
{
    private readonly IngestStateStore _state;
    private readonly IFileOperations _fs;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMove"/> class.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="fileOperations">File-system access.</param>
    /// <param name="clock">Clock.</param>
    public LibraryMove(IngestStateStore state, IFileOperations fileOperations, TimeProvider clock)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _fs = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Whether Ingest chose a filing's library itself. Filings record it (<see cref="ActivityEntry.ChosenBy"/>); for one
    /// recorded before that, it counts as automatic only if Recent activity holds no review decision for the same
    /// release (and watch folder) made before it was filed.
    /// </summary>
    /// <param name="filing">The filing's entry.</param>
    /// <param name="activity">Recent activity.</param>
    /// <returns><c>true</c> if Ingest chose the library.</returns>
    public static bool IsAutomatic(ActivityEntry filing, IEnumerable<ActivityEntry> activity)
    {
        ArgumentNullException.ThrowIfNull(filing);
        ArgumentNullException.ThrowIfNull(activity);
        return filing.ChosenBy switch
        {
            // An approved AI suggestion chose the title, not the library: Ingest still chose that itself
            FilingChoice.Automatic or FilingChoice.AiApproved => true,
            FilingChoice.Review => false,
            _ => !activity.Any(a => a.Status == ActivityStatus.Decision
                && string.Equals(a.Release, filing.Release, StringComparison.Ordinal)
                && PathGuard.SamePath(a.WatchFolder, filing.WatchFolder)
                && a.Time <= filing.Time),
        };
    }

    /// <summary>
    /// Why a filing can't be moved to another library (whatever the library), or <c>null</c> if it may be.
    /// </summary>
    /// <param name="filing">The filing's entry.</param>
    /// <param name="activity">Recent activity.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The reason, or <c>null</c>.</returns>
    public static string? Ineligible(ActivityEntry filing, IEnumerable<ActivityEntry> activity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(filing);
        if (filing.Status != ActivityStatus.Filed || string.IsNullOrEmpty(filing.Run))
        {
            return "Only a release Ingest filed (since undo was added) can be moved to another library.";
        }

        if (filing.UndoneAt is not null)
        {
            return "This filing has been undone.";
        }

        if (filing.MoveRun is not null)
        {
            return "This filing has already been moved to another library" + (filing.MovedTo is null ? "." : " (" + filing.MovedTo + ").");
        }

        if (now - filing.Time > ActionLog.MaxAge)
        {
            return "The action log no longer holds this filing's moves (it keeps 90 days), so it can't be moved.";
        }

        return IsAutomatic(filing, activity) ? null : "Its library was chosen in review, so Ingest doesn't offer to move it.";
    }

    /// <summary>
    /// The moves a filing completed, followed to where its files are now: after a move to another library, each file's
    /// destination (and size and modified time) is the move's. Clutter and replaced copies in quarantine stay as filed.
    /// </summary>
    /// <param name="filing">The filing's entry.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <returns>The moves, in the order they were made.</returns>
    public static IReadOnlyList<ActionLogLine> FiledMoves(ActivityEntry filing, IEnumerable<string> actionLogLines)
    {
        ArgumentNullException.ThrowIfNull(filing);
        ArgumentNullException.ThrowIfNull(actionLogLines);
        if (string.IsNullOrEmpty(filing.Run))
        {
            return [];
        }

        var lines = actionLogLines as IReadOnlyCollection<string> ?? [.. actionLogLines];
        var filed = ActionLogLine.CompletedMoves(lines, filing.Run);
        if (string.IsNullOrEmpty(filing.MoveRun))
        {
            return filed;
        }

        var moved = ActionLogLine.CompletedMoves(lines, filing.MoveRun);
        return [.. filed.Select(f =>
        {
            if (f.OperationKind == OperationKind.Quarantine)
            {
                return f;
            }

            foreach (var m in moved.Where(m => PathGuard.SamePath(m.Source, f.Destination)))
            {
                f = f with { Destination = m.Destination, Bytes = m.Bytes, Modified = m.Modified };
            }

            return f;
        })];
    }

    /// <summary>
    /// The libraries a filing can be moved to: films to Movies or mixed libraries, shows to Shows or mixed libraries,
    /// never the one it is in.
    /// </summary>
    /// <param name="filing">The filing's entry.</param>
    /// <param name="activity">Recent activity.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <param name="scope">Libraries and folders as configured now.</param>
    /// <param name="now">Current time.</param>
    /// <param name="refusal">Why it can't be moved at all.</param>
    /// <returns>The options, or <c>null</c> with <paramref name="refusal"/> set.</returns>
    public static MoveOptions? Options(ActivityEntry filing, IEnumerable<ActivityEntry> activity, IEnumerable<string> actionLogLines, MoveScope scope, DateTimeOffset now, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(scope);
        refusal = Ineligible(filing, activity, now);
        if (refusal is not null)
        {
            return null;
        }

        var where = Locate(FiledMoves(filing, actionLogLines), scope, out refusal);
        if (where is null)
        {
            return null;
        }

        var targets = scope.Libraries
            .Where(l => Holds(l.Kind, where.IsSeries) && !string.Equals(l.Id, where.Library.Id, StringComparison.OrdinalIgnoreCase))
            .Select(l => (Library: l, Folder: FolderIn(l, where.TitleName, scope)))
            .Where(t => t.Folder is not null)
            .Select(t => new MoveTarget(t.Library.Id, t.Library.Name, t.Library.Kind, t.Folder!.Value.Root, t.Folder.Value.HasTitle))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            refusal = where.IsSeries ? "There is no other Shows or mixed library to move it to." : "There is no other Movies or mixed library to move it to.";
            return null;
        }

        return new MoveOptions
        {
            From = where.Library.Name,
            IsSeries = where.IsSeries,
            Targets = targets,
            ShowFolders = targets.Any(t => scope.Libraries.First(l => l.Id == t.LibraryId).Locations.Count > 1),
        };
    }

    /// <summary>
    /// Plans a move of a filing's files to another library. Nothing is changed.
    /// </summary>
    /// <param name="filing">The filing's entry.</param>
    /// <param name="activity">Recent activity.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <param name="libraryId">The library to move to.</param>
    /// <param name="scope">Libraries and folders as configured now.</param>
    /// <param name="fs">File-system access.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The plan, or why it can't be moved.</returns>
    public static MovePlan Plan(ActivityEntry filing, IEnumerable<ActivityEntry> activity, IEnumerable<string> actionLogLines, string? libraryId, MoveScope scope, IFileOperations fs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(filing);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fs);
        if (Ineligible(filing, activity, now) is { } why)
        {
            return MovePlan.Refuse(why);
        }

        var moves = FiledMoves(filing, actionLogLines);
        var where = Locate(moves, scope, out var refusal);
        if (where is null)
        {
            return MovePlan.Refuse(refusal!);
        }

        var target = scope.Libraries.FirstOrDefault(l => string.Equals(l.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return MovePlan.Refuse("That library isn't on the server any more.");
        }

        if (string.Equals(target.Id, where.Library.Id, StringComparison.OrdinalIgnoreCase))
        {
            return MovePlan.Refuse($"It is already in {target.Name}.");
        }

        if (!Holds(target.Kind, where.IsSeries))
        {
            return MovePlan.Refuse(where.IsSeries ? "A show can only be moved to a Shows or mixed library." : "A film can only be moved to a Movies or mixed library.");
        }

        if (FolderIn(target, where.TitleName, scope) is not { } folder)
        {
            return MovePlan.Refuse($"{target.Name} has no folder Ingest can file into.");
        }

        var root = folder.Root;
        if (scope.Forbidden.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f) && (PathGuard.IsSameOrUnder(root, f) || PathGuard.IsSameOrUnder(f, root))) is { } clash)
        {
            return MovePlan.Refuse($"{target.Name}'s folder {root} overlaps {clash}, which Ingest never files into.");
        }

        if (!fs.Exists(root))
        {
            return MovePlan.Refuse($"{target.Name}'s folder isn't available (is the share mounted?): {root}");
        }

        var targetTitle = Path.Combine(root, where.TitleName);
        if (PathGuard.SamePath(targetTitle, where.TitleFolder) || PathGuard.IsSameOrUnder(targetTitle, where.TitleFolder) || PathGuard.IsSameOrUnder(where.TitleFolder, targetTitle))
        {
            return MovePlan.Refuse($"{target.Name} shares its folder with {where.Library.Name}, so there is nothing to move.");
        }

        var titleExists = fs.Exists(targetTitle);
        var seasons = new Dictionary<int, string?>();
        var ops = new List<PlannedOperation>();
        var taken = new HashSet<string>(PathGuard.Comparer);
        var changedSubtitles = 0;
        foreach (var m in where.Files)
        {
            var kind = m.OperationKind;
            if (!fs.Exists(m.Destination))
            {
                return MovePlan.Refuse($"{m.Destination} is missing, so it isn't moved. Nothing was changed.");
            }

            // As for an undo: a subtitle may have been corrected since filing and moves as it is; anything else must be as filed
            var changed = fs.Length(m.Destination) != m.Bytes || (m.ModifiedUtc is { } logged && fs.LastWriteUtc(m.Destination) is { } current && current.ToUniversalTime() != logged);
            if (changed && kind != OperationKind.Subtitle)
            {
                return MovePlan.Refuse($"{m.Destination} has changed since it was filed, so it isn't moved. Nothing was changed.");
            }

            if (changed)
            {
                changedSubtitles++;
            }

            var parts = Path.GetRelativePath(PathGuard.Normalise(where.TitleFolder), PathGuard.Normalise(m.Destination)).Split(Path.DirectorySeparatorChar);

            // A show already in that library keeps its own season folders (Season 1, S01, Specials …)
            if (titleExists && parts.Length > 1 && SeasonFolder().Match(parts[0]) is { Success: true } s)
            {
                var n = int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!seasons.TryGetValue(n, out var existing))
                {
                    existing = ExistingFiles.FindSeasonFolder(targetTitle, n, scope.ListDirectories, scope.ListFiles);
                    seasons[n] = existing;
                }

                if (existing is not null)
                {
                    parts[0] = Path.GetFileName(existing);
                }
            }

            var destination = Path.Combine([targetTitle, .. parts]);
            if (fs.Exists(destination) || !taken.Add(destination))
            {
                return MovePlan.Refuse($"{destination} is already there, so nothing is moved.");
            }

            if (kind == OperationKind.Video && titleExists && Duplicate(m.Destination, where.IsSeries, targetTitle, scope) is { } duplicate)
            {
                return MovePlan.Refuse($"{duplicate.What} is already in {target.Name} ({duplicate.Path}), so nothing is moved.");
            }

            ops.Add(new PlannedOperation(kind, m.Destination, destination));
        }

        return new MovePlan
        {
            Plan = new IngestPlan
            {
                ReleaseName = filing.Release,
                Operations = ops,
                Returning = [.. ops.Select(o => o.Source)],
                AllowedRoots = [targetTitle],
                RequiredFolders = [root],
            },
            From = where.Library,
            To = target,
            SourceTitle = where.TitleFolder,
            TargetTitle = targetTitle,
            Tidy = scope.Folders.FoldersLeftBy(ops.Select(o => o.Source)),
            ChangedSubtitles = changedSubtitles,
        };
    }

    /// <summary>
    /// Checks whether a filing can be moved to a library now, without changing anything (for the page's immediate answer).
    /// </summary>
    /// <param name="run">The filing's run.</param>
    /// <param name="libraryId">The library.</param>
    /// <param name="scope">Libraries and folders as configured now.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <returns>Why it can't be moved, or <c>null</c> if it can.</returns>
    public string? Check(string run, string? libraryId, MoveScope scope, IEnumerable<string> actionLogLines)
    {
        var filing = _state.FindFiling(run);
        return filing is null
            ? "That filing isn't in Recent activity any more."
            : Plan(filing, _state.Snapshot().Activity, actionLogLines, libraryId, scope, _fs, _clock.GetUtcNow()).Refusal;
    }

    /// <summary>
    /// Moves a filing's files to another library (all or nothing), and records it.
    /// </summary>
    /// <param name="run">The filing's run.</param>
    /// <param name="libraryId">The library to move to.</param>
    /// <param name="scope">Libraries and folders as configured now.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <param name="actionLogPath">The action log, which the move's own moves are written to.</param>
    /// <returns>What happened; <see cref="ReturnOutcome.Refresh"/> holds the film or show folders left and entered.</returns>
    public ReturnOutcome Move(string run, string? libraryId, MoveScope scope, IEnumerable<string> actionLogLines, string actionLogPath)
    {
        var filing = _state.FindFiling(run);
        if (filing is null)
        {
            return new ReturnOutcome(false, "That filing isn't in Recent activity any more.", []);
        }

        var planned = Plan(filing, _state.Snapshot().Activity, actionLogLines, libraryId, scope, _fs, _clock.GetUtcNow());
        if (planned.Refusal is not null)
        {
            Record(filing, ActivityStatus.Failed, "Couldn't move to another library: " + planned.Refusal, [], null);
            return new ReturnOutcome(false, planned.Refusal, []);
        }

        // Noted on the filing before anything moves, so an undo follows whatever a crash part-way has moved
        var moveRun = Guid.NewGuid().ToString("N");
        _state.UpdateFiling(run, f => f with { MoveRun = moveRun });

        var report = new PlanExecutor(_fs, _clock) { RunId = moveRun }.Execute(planned.Plan!, planned.SourceTitle, actionLogPath, dryRun: false);
        if (!report.Succeeded)
        {
            string why;
            if (report.RollbackProblems.Count == 0)
            {
                _state.UpdateFiling(run, f => f with { MoveRun = null });
                why = string.Create(CultureInfo.InvariantCulture, $"Moving to {planned.To!.Name} failed, and everything was put back as it was: {report.Error} ({report.RolledBack.Count} completed move(s) were reversed.)");
            }
            else
            {
                why = $"Moving to {planned.To!.Name} failed and couldn't be fully rolled back: {report.Error}. Needs attention: {string.Join("; ", report.RollbackProblems)}";
            }

            Record(filing, ActivityStatus.Failed, why, [.. report.Completed.Select(o => $"{o.Kind}: {o.Source} → {o.Destination}")], null);
            return new ReturnOutcome(false, why, [planned.SourceTitle, planned.TargetTitle]);
        }

        var problems = new List<string>();
        ReleaseUndo.TidyUp(_fs, planned.Tidy, problems);
        problems.AddRange(report.Unreadable.Select(p => $"{p} was written, but can't be read back through the library's mount."));
        ActivityReport.RecordUnreadable(_state, _clock.GetUtcNow(), filing.Release, report);

        var now = _clock.GetUtcNow();
        var to = planned.To!.Name;
        _state.UpdateFiling(run, f => f with { MovedAt = now, MovedTo = to });
        var ops = planned.Plan!.Operations;
        var summary = string.Create(CultureInfo.InvariantCulture, $"Moved to {to} by an administrator: {ops.Count} file(s) moved from {planned.From!.Name}.")
            + (planned.ChangedSubtitles > 0 ? string.Create(CultureInfo.InvariantCulture, $" {planned.ChangedSubtitles} {(planned.ChangedSubtitles == 1 ? "subtitle had" : "subtitles had")} changed since filing and {(planned.ChangedSubtitles == 1 ? "was" : "were")} moved as {(planned.ChangedSubtitles == 1 ? "it is" : "they are")}.") : string.Empty)
            + " Jellyfin sees it as a new item there, so watched state and resume points may not carry over.";
        Record(
            filing,
            ActivityStatus.Moved,
            summary,
            [.. problems.Select(p => "Needs attention: " + p), .. ops.Select(o => $"{o.Kind}: {o.Source} → {o.Destination}")],
            [.. ops.Where(o => o.Kind == OperationKind.Video).Select(o => o.Destination)]);
        return new ReturnOutcome(true, summary, [planned.SourceTitle, planned.TargetTitle]);
    }

    private static bool Holds(LibraryKind kind, bool isSeries)
        => isSeries ? kind is LibraryKind.Shows or LibraryKind.Mixed : kind is LibraryKind.Films or LibraryKind.Mixed;

    // The folder of a library the files would go into: one already holding a film or show folder of this name (wherever
    // it is in the library), else the roomiest, as filing chooses
    private static (string Root, bool HasTitle)? FolderIn(MediaLibrary library, string titleName, MoveScope scope)
    {
        var locations = library.Locations.Where(l => !string.IsNullOrWhiteSpace(l) && Path.IsPathFullyQualified(l)).ToList();
        if (locations.Count == 0)
        {
            return null;
        }

        var holding = locations.FirstOrDefault(l => scope.ListDirectories(l).Any(d => string.Equals(Path.GetFileName(d), titleName, StringComparison.Ordinal)));
        return holding is not null ? (holding, true) : (LibraryRouting.RoomiestOf(locations, scope.FreeBytes)!, false);
    }

    // Where a filing's files are now: one film or show folder, directly inside one library folder
    private static Whereabouts? Locate(IReadOnlyList<ActionLogLine> moves, MoveScope scope, out string? refusal)
    {
        refusal = null;
        var files = moves.Where(m => m.OperationKind != OperationKind.Quarantine).ToList();
        if (files.Count == 0)
        {
            refusal = "The action log no longer holds this filing's moves (it keeps 90 days), so it can't be moved.";
            return null;
        }

        var locations = scope.Libraries
            .SelectMany(l => l.Locations.Where(loc => !string.IsNullOrWhiteSpace(loc) && Path.IsPathFullyQualified(loc)).Select(loc => (Library: l, Root: loc)))
            .ToList();
        (MediaLibrary Library, string Root)? home = null;
        string? titleFolder = null;
        foreach (var f in files)
        {
            var here = locations.Where(x => PathGuard.IsUnder(f.Destination, x.Root)).OrderByDescending(x => PathGuard.Normalise(x.Root).Length).FirstOrDefault();
            if (here.Library is null)
            {
                refusal = $"{f.Destination} is no longer inside one of the server's libraries, so it isn't moved.";
                return null;
            }

            var rel = Path.GetRelativePath(PathGuard.Normalise(here.Root), PathGuard.Normalise(f.Destination)).Split(Path.DirectorySeparatorChar);
            if (rel.Length < 2)
            {
                refusal = $"{f.Destination} isn't in a film or show folder, so it isn't moved.";
                return null;
            }

            var title = Path.Combine(here.Root, rel[0]);
            if (titleFolder is null)
            {
                (home, titleFolder) = (here, title);
            }
            else if (!PathGuard.SamePath(title, titleFolder))
            {
                refusal = "Its files are in more than one film or show folder, so it isn't moved.";
                return null;
            }
        }

        var titles = files.Where(f => f.OperationKind == OperationKind.Video).Select(f => MediaTitle.FromFiledPath(f.Destination)).ToList();
        if (titles.Count == 0 || titles.Any(t => t is null) || titles.Select(t => t!.IsSeries).Distinct().Count() != 1)
        {
            refusal = "Ingest can't tell from its files whether it is a film or a show, so it isn't moved.";
            return null;
        }

        return new Whereabouts(home!.Value.Library, titleFolder!, Path.GetFileName(titleFolder!), titles[0]!.IsSeries, files);
    }

    // The same episode (any name or container) or the same film edition already in the target's film or show folder
    private static (string What, string Path)? Duplicate(string video, bool isSeries, string targetTitle, MoveScope scope)
    {
        if (isSeries)
        {
            if (MediaTitle.FromFiledPath(video) is not { Season: { } season, Episode: { } first } t)
            {
                return null;
            }

            for (var e = first; e <= (t.LastEpisode ?? first); e++)
            {
                if (ExistingFiles.FindEpisode(targetTitle, season, e, scope.ListDirectories, scope.ListFiles) is { } path)
                {
                    return (t.Title + " " + Naming.MediaNamer.EpisodeCode(season, e), path);
                }
            }

            return null;
        }

        var (_, edition) = ExistingFiles.MovieEdition(video);
        foreach (var existing in scope.ListFiles(targetTitle).Where(f => ReleaseClassifier.Classify(new ReleaseFile(Path.GetFileName(f), long.MaxValue)) == FileRole.Video))
        {
            var (known, other) = ExistingFiles.MovieEdition(existing);
            if (!known || string.Equals(other, edition, StringComparison.OrdinalIgnoreCase))
            {
                return ("This film" + (edition is null ? string.Empty : " (" + edition + ")"), existing);
            }
        }

        return null;
    }

    private void Record(ActivityEntry filing, ActivityStatus status, string summary, IReadOnlyList<string> details, IReadOnlyList<string>? videos)
        => _state.Record(new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = status,
            Release = filing.Release,
            WatchFolder = filing.WatchFolder,
            Summary = summary,
            Details = details,
            Videos = videos ?? filing.Videos,
        });

    // Ingest's season folders: "Season 01" (and "Season 1")
    [GeneratedRegex(@"^Season (\d{1,4})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    private sealed record Whereabouts(MediaLibrary Library, string TitleFolder, string TitleName, bool IsSeries, IReadOnlyList<ActionLogLine> Files);
}
