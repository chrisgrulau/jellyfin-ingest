using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;

namespace Jellyfin.Plugin.Ingest.Quarantine;

/// <summary>
/// Puts a quarantined release (an entry of a dated quarantine folder) back where its files came from, as the action log
/// recorded when they were quarantined: clutter back into its release in the watch folder, replaced copies back into
/// the library. All or nothing, through the same executor as filing. A release put back into a watch folder waits for
/// review, so it isn't filed by itself.
/// </summary>
public sealed class QuarantineRestore
{
    /// <summary>The reason a restored release waits for review.</summary>
    public const string HeldReason = "Restored from quarantine by an administrator — choose what to do.";

    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly IngestStateStore _state;
    private readonly IFileOperations _fs;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuarantineRestore"/> class.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="fileOperations">File-system access.</param>
    /// <param name="clock">Clock.</param>
    public QuarantineRestore(IngestStateStore state, IFileOperations fileOperations, TimeProvider clock)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _fs = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Finds a quarantined release on disk: <c>root/folder/name</c>, checked to be an entry of a dated folder Ingest
    /// created in one of the quarantine folders in use.
    /// </summary>
    /// <param name="scope">The folders as configured now.</param>
    /// <param name="root">The quarantine folder.</param>
    /// <param name="folder">The dated folder's name.</param>
    /// <param name="name">The release's name in it (a folder or a single file); <c>null</c> for the whole dated folder.</param>
    /// <param name="path">The entry's path.</param>
    /// <returns>What's wrong, or <c>null</c>.</returns>
    public static string? Locate(ReturnScope scope, string? root, string? folder, string? name, out string path)
    {
        ArgumentNullException.ThrowIfNull(scope);
        path = string.Empty;
        var quarantine = scope.QuarantineRoots.FirstOrDefault(q => PathGuard.SamePath(q, root));
        if (quarantine is null)
        {
            return "That isn't a quarantine folder Ingest uses.";
        }

        if (!IsPlainName(folder) || QuarantineMarkers.DateOf(folder) is null)
        {
            return "That isn't a dated quarantine folder.";
        }

        // Paths are taken from what is on disk, matched by name, never built from what the page sent
        var dated = Directory.Exists(quarantine)
            ? Directory.EnumerateDirectories(quarantine).FirstOrDefault(d => string.Equals(Path.GetFileName(d), folder, StringComparison.Ordinal))
            : null;
        if (dated is null || new DirectoryInfo(dated).LinkTarget is not null || !QuarantineMarkers.IsMarked(dated))
        {
            return "That dated folder isn't there, or Ingest didn't create it.";
        }

        if (name is null)
        {
            path = dated;
            return null;
        }

        if (!IsPlainName(name) || string.Equals(name, QuarantineMarkers.DatedMarker, StringComparison.Ordinal))
        {
            return "That isn't a quarantined release.";
        }

        var entry = Directory.EnumerateFileSystemEntries(dated).FirstOrDefault(e => string.Equals(Path.GetFileName(e), name, StringComparison.Ordinal));
        if (entry is null)
        {
            return "That release isn't in quarantine any more.";
        }

        path = entry;
        return null;
    }

    /// <summary>
    /// Plans putting quarantined files back. Nothing is changed.
    /// </summary>
    /// <param name="datedFolder">The dated quarantine folder they are in.</param>
    /// <param name="files">The files (absolute).</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <param name="scope">The folders as configured now.</param>
    /// <param name="fs">File-system access.</param>
    /// <param name="name">The release's name, for the log and activity.</param>
    /// <returns>The plan, or why it can't be done.</returns>
    public static ReturnPlan Plan(string datedFolder, IReadOnlyList<string> files, IEnumerable<string> actionLogLines, ReturnScope scope, IFileOperations fs, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datedFolder);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(actionLogLines);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fs);
        if (files.Count == 0)
        {
            return ReturnPlan.Refuse("There is nothing in it to restore.");
        }

        // Where each quarantined file came from: the latest completed quarantine move to it
        var origins = new Dictionary<string, ActionLogLine>(PathGuard.Comparer);
        foreach (var line in actionLogLines.Select(ActionLogLine.TryParse).OfType<ActionLogLine>())
        {
            if (line.IsCompleted && line.OperationKind == OperationKind.Quarantine && Path.IsPathFullyQualified(line.Destination) && PathGuard.IsUnder(line.Destination, datedFolder))
            {
                origins[PathGuard.Normalise(line.Destination)] = line;
            }
        }

        var ops = new List<PlannedOperation>();
        var taken = new HashSet<string>(PathGuard.Comparer);
        var required = new HashSet<string>(PathGuard.Comparer);
        var releases = new List<(string, string)>();
        var refresh = new HashSet<string>(PathGuard.Comparer);
        foreach (var file in files)
        {
            if (!origins.TryGetValue(PathGuard.Normalise(file), out var origin))
            {
                return ReturnPlan.Refuse($"{file} isn't in the action log (it keeps 90 days), so where it came from isn't known. Nothing was restored.");
            }

            if (fs.Length(file) != origin.Bytes)
            {
                return ReturnPlan.Refuse($"{file} has changed since it was quarantined. Nothing was restored.");
            }

            var watch = scope.WatchFolders.FirstOrDefault(w => PathGuard.IsUnder(origin.Source, w));
            var library = watch is null ? scope.LibraryFolders.FirstOrDefault(l => PathGuard.IsUnder(origin.Source, l)) : null;
            if (watch is null && library is null)
            {
                return ReturnPlan.Refuse($"{file} came from {origin.Source}, which is outside the watch folders and libraries, so it isn't put back.");
            }

            if (scope.QuarantineRoots.Any(q => PathGuard.IsSameOrUnder(origin.Source, q)))
            {
                return ReturnPlan.Refuse($"{file} came from inside a quarantine folder, so it isn't put back.");
            }

            if (fs.Exists(origin.Source) || !taken.Add(origin.Source))
            {
                return ReturnPlan.Refuse($"{origin.Source} is taken now, so nothing was restored.");
            }

            var root = (watch ?? library)!;
            if (!fs.Exists(root))
            {
                return ReturnPlan.Refuse($"The folder it goes back to isn't available (is it mounted?): {root}");
            }

            required.Add(root);
            if (watch is not null)
            {
                releases.Add((watch, Path.GetRelativePath(PathGuard.Normalise(watch), PathGuard.Normalise(origin.Source)).Split(Path.DirectorySeparatorChar)[0]));
            }
            else
            {
                refresh.Add(Path.Combine(root, Path.GetRelativePath(PathGuard.Normalise(root), PathGuard.Normalise(origin.Source)).Split(Path.DirectorySeparatorChar)[0]));
            }

            ops.Add(new PlannedOperation(OperationKind.Quarantine, file, origin.Source));
        }

        return new ReturnPlan
        {
            Plan = new IngestPlan
            {
                ReleaseName = name,
                Operations = ops,
                Returning = [.. files],
                AllowedRoots = [.. required],
                RequiredFolders = [.. required],
                TidyIfEmpty = scope.FoldersLeftBy(files, datedFolder),
            },
            Releases = [.. releases.Distinct()],
            Refresh = [.. refresh],
        };
    }

    /// <summary>
    /// Checks whether a quarantined release can be restored now, without changing anything.
    /// </summary>
    /// <param name="scope">The folders as configured now.</param>
    /// <param name="root">The quarantine folder.</param>
    /// <param name="folder">The dated folder's name.</param>
    /// <param name="name">The release's name in it.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <returns>Why it can't be restored, or <c>null</c> if it can.</returns>
    public string? Check(ReturnScope scope, string? root, string? folder, string? name, IEnumerable<string> actionLogLines)
        => Prepare(scope, root, folder, name, actionLogLines).Refusal;

    /// <summary>
    /// Restores a quarantined release (all or nothing), records it, and leaves releases put back into a watch folder
    /// waiting for review.
    /// </summary>
    /// <param name="scope">The folders as configured now.</param>
    /// <param name="root">The quarantine folder.</param>
    /// <param name="folder">The dated folder's name.</param>
    /// <param name="name">The release's name in it.</param>
    /// <param name="actionLogLines">The action log's lines.</param>
    /// <param name="actionLogPath">The action log, which the moves are written to.</param>
    /// <returns>What happened.</returns>
    public ReturnOutcome Restore(ReturnScope scope, string? root, string? folder, string? name, IEnumerable<string> actionLogLines, string actionLogPath)
    {
        var planned = Prepare(scope, root, folder, name, actionLogLines);
        if (planned.Refusal is not null)
        {
            Record(name ?? folder ?? string.Empty, null, ActivityStatus.Failed, "Couldn't restore from quarantine: " + planned.Refusal, []);
            return new ReturnOutcome(false, planned.Refusal, []);
        }

        var plan = planned.Plan!;
        var watch = planned.Releases.Count > 0 ? planned.Releases[0].WatchFolder : null;
        var held = ReleaseUndo.Hold(_state, planned.Releases, _clock.GetUtcNow(), HeldReason);
        var report = new PlanExecutor(_fs, _clock).Execute(plan, plan.Operations[0].Source, actionLogPath, dryRun: false);
        if (!report.Succeeded)
        {
            ReleaseUndo.Release(_state, held);
            var why = report.RollbackProblems.Count == 0
                ? $"Restore failed, and everything was put back in quarantine: {report.Error}"
                : $"Restore failed and couldn't be fully rolled back: {report.Error}. Needs attention: {string.Join("; ", report.RollbackProblems)}";
            Record(name!, watch, ActivityStatus.Failed, why, [.. report.Completed.Select(o => $"{o.Source} → {o.Destination}")]);
            return new ReturnOutcome(false, why, []);
        }

        var summary = string.Create(CultureInfo.InvariantCulture, $"Restored from quarantine by an administrator: {plan.Operations.Count} file(s) put back where they came from.")
            + (planned.Releases.Count > 0 ? " Put back into a watch folder, it now waits under Needs review." : string.Empty);
        Record(name!, watch, ActivityStatus.Restored, summary, [.. (report.Warning is null ? [] : new[] { "Needs attention: " + report.Warning }), .. plan.Operations.Select(o => $"{o.Source} → {o.Destination}")]);
        return new ReturnOutcome(true, summary, planned.Refresh);
    }

    private static bool IsPlainName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name is not "." and not ".."
            && name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private ReturnPlan Prepare(ReturnScope scope, string? root, string? folder, string? name, IEnumerable<string> actionLogLines)
    {
        if (name is null)
        {
            return ReturnPlan.Refuse("Choose a release in the dated folder to restore.");
        }

        if (Locate(scope, root, folder, name, out var path) is { } problem)
        {
            return ReturnPlan.Refuse(problem);
        }

        List<string> files;
        try
        {
            files = File.Exists(path) ? [path] : [.. Directory.EnumerateFiles(path, "*", AllFiles)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReturnPlan.Refuse("It couldn't be read: " + ex.Message);
        }

        return Plan(Path.GetDirectoryName(path)!, files, actionLogLines, scope, _fs, name);
    }

    private void Record(string release, string? watch, ActivityStatus status, string summary, IReadOnlyList<string> details)
        => _state.Record(new ActivityEntry
        {
            Time = _clock.GetUtcNow(),
            Status = status,
            Release = release,
            WatchFolder = watch,
            Summary = summary,
            Details = details,
        });
}
