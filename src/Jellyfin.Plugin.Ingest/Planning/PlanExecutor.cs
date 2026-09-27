using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

using Jellyfin.Plugin.Ingest.Configuration;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// The file-system operations the executor needs (abstracted so plans can be executed against a fake in tests).
/// </summary>
public interface IFileOperations
{
    /// <summary>Gets whether a file or directory exists.</summary>
    /// <param name="path">Absolute path.</param>
    /// <returns><c>true</c> if it exists.</returns>
    bool Exists(string path);

    /// <summary>Gets a file's size in bytes.</summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The size.</returns>
    long Length(string path);

    /// <summary>Creates a directory (and parents) if missing.</summary>
    /// <param name="path">Absolute path.</param>
    void CreateDirectory(string path);

    /// <summary>Moves a file without overwriting (a rename, or copy + delete across file systems).</summary>
    /// <param name="source">Current path.</param>
    /// <param name="destination">Target path, which must not exist.</param>
    void Move(string source, string destination);

    /// <summary>Deletes a file.</summary>
    /// <param name="path">Absolute path.</param>
    void Delete(string path);

    /// <summary>
    /// Copies a file (never over an existing one).
    /// </summary>
    /// <param name="source">From.</param>
    /// <param name="destination">To.</param>
    void Copy(string source, string destination) => throw new NotSupportedException();

    /// <summary>
    /// Makes a hard link (a second name for the same file), if the file system allows it.
    /// </summary>
    /// <param name="source">The existing file.</param>
    /// <param name="destination">The new name.</param>
    /// <returns>Whether the link was made (<c>false</c>: another drive, or no hard links; copy instead).</returns>
    bool TryHardLink(string source, string destination) => false;

    /// <summary>Removes empty directories under (and including) a directory.</summary>
    /// <param name="path">Absolute path.</param>
    void DeleteEmptyDirectories(string path);

    /// <summary>
    /// Removes a directory only if it is empty (nothing in it at all) and isn't a link.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>Whether it was removed.</returns>
    bool DeleteIfEmpty(string path) => false;

    /// <summary>
    /// Gets a file's last-write time (UTC), recorded in the action log so an undo can tell whether it has changed since.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The time, or <c>null</c> if it can't be read.</returns>
    DateTime? LastWriteUtc(string path) => null;

    /// <summary>Appends a line to a text file (the action log).</summary>
    /// <param name="path">Absolute path.</param>
    /// <param name="line">The line.</param>
    void AppendLine(string path, string line);

    /// <summary>
    /// Whether a file can be moved into a folder without running out of space: always true within one file system (a
    /// rename); across file systems (a copy), the destination needs room for it.
    /// </summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destinationFolder">The folder it goes into.</param>
    /// <param name="bytes">Its size.</param>
    /// <returns><c>false</c> only when there is known not to be enough room.</returns>
    bool HasRoomFor(string source, string destinationFolder, long bytes);

    /// <summary>
    /// Opens a file for reading by its path, as a player or Jellyfin's scan would, and returns its length: a caching
    /// network file system (virtiofs, CIFS) can list a name it then fails to open, which a size check alone doesn't catch.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    /// <returns>The length, or <c>null</c> when the file can't be opened.</returns>
    long? ReadBack(string path) => Exists(path) ? Length(path) : null;

    /// <summary>
    /// Lists a folder's entries, which makes a caching network file system look the folder up afresh (and can revalidate
    /// a stale entry for a file just renamed into it). Never throws.
    /// </summary>
    /// <param name="path">Absolute path of the folder.</param>
    void Relist(string path)
    {
    }
}

/// <summary>
/// What happened when a plan was executed.
/// </summary>
public sealed record ExecutionReport
{
    /// <summary>Gets the operations that completed (and weren't rolled back).</summary>
    public IReadOnlyList<PlannedOperation> Completed { get; init; } = [];

    /// <summary>Gets the operation that failed (execution stops there), if any.</summary>
    public PlannedOperation? Failed { get; init; }

    /// <summary>Gets the failure message, if any.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the operations that had completed before a failure and were undone.</summary>
    public IReadOnlyList<PlannedOperation> RolledBack { get; init; } = [];

    /// <summary>Gets anything that couldn't be undone after a failure; each needs attention.</summary>
    public IReadOnlyList<string> RollbackProblems { get; init; } = [];

    /// <summary>Gets a value indicating whether execution stopped early because the server is shutting down.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Gets a warning about a non-essential step that failed after every move succeeded (e.g. tidying up).</summary>
    public string? Warning { get; init; }

    /// <summary>Gets a value indicating whether nothing was changed because it was a dry run.</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets a value indicating whether nothing was moved because a required library folder is missing (offline share).</summary>
    public bool FolderUnavailable { get; init; }

    /// <summary>
    /// Gets the id this execution's moves carry in the action log (so an undo finds exactly them); <c>null</c> in a
    /// dry run or when nothing was attempted.
    /// </summary>
    public string? Run { get; init; }

    /// <summary>
    /// Gets the files that were filed (renamed into place) but couldn't be opened again by their new name, even after
    /// looking the folder up afresh: most likely a stale cache in the mount the library is on. They are filed and logged
    /// (an undo finds them), but need attention; see <see cref="PlanExecutor.UnreadableAdvice"/>.
    /// </summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];

    /// <summary>Gets a value indicating whether every operation completed.</summary>
    public bool Succeeded => Failed is null && !Cancelled && Error is null;
}

/// <summary>
/// Carries out an <see cref="IngestPlan"/> so that a release is filed completely or not at all, and a crash can never
/// leave a half-copied file under a real name:
/// <list type="bullet">
/// <item>every file first moves to a temporary name beside its destination (<c>&lt;name&gt;.ingest-&lt;id&gt;.partial</c>, which
/// Jellyfin doesn't treat as media), its size is verified, then it is renamed into place and opened again by its new
/// name (<see cref="ReadBackAttempts"/>);</item>
/// <item>each move is written to the action log before (<c>intent</c>) and after (<c>done</c>), so an interrupted move can
/// be finished or discarded at the next start (<see cref="Recover"/>);</item>
/// <item>if a move fails, the moves already made are undone in reverse order;</item>
/// <item>nothing is ever overwritten, and every destination is re-checked against the plan's allowed folders first.</item>
/// </list>
/// </summary>
public sealed class PlanExecutor
{
    // Temporary names are not hidden (no leading dot): a Samba share stores the DOS "hidden" attribute on a file created
    // with a dot name, and it stayed on the filed file after the rename. Older versions used ".ingest-<32 hex>.partial";
    // recovery still recognises those
    private const string TempMarker = ".ingest-";
    private const string TempSuffix = ".partial";
    private const int TempIdLength = 8;
    private const string TempPrefix = ".ingest-";
    private const string TrashSuffix = ".undone";

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdef");

    private readonly IFileOperations _fs;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlanExecutor"/> class.
    /// </summary>
    /// <param name="fileOperations">File-system access.</param>
    /// <param name="clock">Clock for log timestamps.</param>
    public PlanExecutor(IFileOperations fileOperations, TimeProvider clock)
    {
        _fs = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Gets what is told, before each file is moved, which file it is (from 1) and how many there are, so the page can
    /// show "filing 2 of 5" during a long copy (ING-36). Called on the executing thread; it must not throw.
    /// </summary>
    public Action<int, int>? Filing { get; init; }

    /// <summary>
    /// Gets the id this execution's moves carry in the action log, when the caller needs it before anything moves (a
    /// move to another library records it first, so an undo can follow the files even after a crash part-way);
    /// <c>null</c> for a new one.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Gets how many times a file just renamed into place is opened again by its new name before it counts as unreadable
    /// (the folder is listed again before each retry, which can revalidate a stale cache entry).
    /// </summary>
    public int ReadBackAttempts { get; init; } = 4;

    /// <summary>Gets the wait before the first retry of <see cref="ReadBackAttempts"/>; it doubles each time.</summary>
    public TimeSpan ReadBackDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets what to do about files that were filed but can't be read back (<see cref="ExecutionReport.Unreadable"/>).
    /// </summary>
    public static string UnreadableAdvice =>
        "The file was written, but can't be read back through the mount the library is on (it may be listed with '?' "
        + "fields, or fail to open as missing). This is usually a stale cache in a shared-folder mount, such as virtiofs "
        + "in a virtual machine over a network share: set the host's virtiofs cache to \"metadata\" or \"never\", or drop "
        + "the caches in the guest (sysctl vm.drop_caches=2), then scan the library. Nothing needs moving: the file is "
        + "complete on the storage.";

    /// <summary>
    /// The temporary name a file is moved or copied to before it is renamed into place: beside the destination, named
    /// after it, so a leftover is recognisable (<c>Film (2019).mkv.ingest-1a2b3c4d.partial</c>). It isn't hidden and
    /// ends in <c>.partial</c>, which Jellyfin doesn't treat as media and download filters treat as unfinished.
    /// </summary>
    /// <param name="destination">The final path.</param>
    /// <returns>The temporary path.</returns>
    public static string TempPathFor(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return destination + TempMarker + Guid.NewGuid().ToString("N")[..TempIdLength] + TempSuffix;
    }

    /// <summary>
    /// Executes a plan.
    /// </summary>
    /// <param name="plan">A ready plan.</param>
    /// <param name="releaseRoot">Absolute path of the release in the watch folder (tidied afterwards).</param>
    /// <param name="actionLogPath">JSON-lines file every move is recorded in.</param>
    /// <param name="dryRun">When <c>true</c>, nothing is touched and every operation is reported as it would run.</param>
    /// <param name="cancellationToken">Stops between files (without undoing what has moved, which the log records).</param>
    /// <returns>The execution report.</returns>
    public ExecutionReport Execute(IngestPlan plan, string releaseRoot, string actionLogPath, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionLogPath);

        if (!plan.IsReady)
        {
            return new ExecutionReport { Error = "The plan needs review and was not executed." };
        }

        if (dryRun)
        {
            return new ExecutionReport { Completed = plan.Operations, DryRun = true };
        }

        // A library folder that has gone (an unmounted share) is never recreated on the local disk
        foreach (var folder in plan.RequiredFolders)
        {
            if (!_fs.Exists(folder))
            {
                return new ExecutionReport { Error = $"The library folder isn't available (is the share mounted?): {folder}", FolderUnavailable = true };
            }
        }

        // Defence in depth: check every operation before touching anything, so a bad plan moves nothing at all
        foreach (var op in plan.Operations)
        {
            var replaced = op.Kind == OperationKind.Quarantine && plan.Replacing.Contains(op.Source, StringComparer.Ordinal);
            var returning = plan.Returning.Contains(op.Source, PathGuard.Comparer);
            if (plan.Returning.Count > 0 ? !returning : !replaced && !PathGuard.IsSameOrUnder(op.Source, releaseRoot))
            {
                return new ExecutionReport { Failed = op, Error = $"Refused: {op.Source} is outside the release being filed." };
            }

            if (!PathGuard.IsUnderAny(op.Destination, plan.AllowedRoots))
            {
                return new ExecutionReport { Failed = op, Error = $"Refused: {op.Destination} is outside the folders this release may be filed into." };
            }
        }

        var run = string.IsNullOrEmpty(RunId) ? Guid.NewGuid().ToString("N") : RunId;
        var done = new List<(PlannedOperation Op, long Bytes, string Temp)>();
        var created = new List<string>();
        var unreadable = new List<string>();
        foreach (var op in plan.Operations)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ExecutionReport
                {
                    Run = run,
                    Completed = [.. done.Select(d => d.Op)],
                    Unreadable = unreadable,
                    Cancelled = true,
                    Error = string.Create(CultureInfo.InvariantCulture, $"Stopped because the server is shutting down: {done.Count} of {plan.Operations.Count} files moved; the rest are still in the watch folder."),
                };
            }

            Filing?.Invoke(done.Count + 1, plan.Operations.Count);
            var folder = Path.GetDirectoryName(op.Destination)!;
            var temp = TempPathFor(op.Destination);
            long size = 0;
            try
            {
                if (_fs.Exists(op.Destination))
                {
                    throw new IOException($"Destination appeared since planning: {op.Destination}");
                }

                size = _fs.Length(op.Source);

                // Remember which folders this creates, so a rollback removes exactly those (and nothing that was there)
                for (var dir = folder; !string.IsNullOrEmpty(dir) && !_fs.Exists(dir); dir = Path.GetDirectoryName(dir))
                {
                    created.Add(dir);
                }

                _fs.CreateDirectory(folder);
                if (!_fs.HasRoomFor(op.Source, folder, size))
                {
                    throw new IOException($"Not enough free space for {op.Source} in {folder}.");
                }

                var keeps = KeepsSource(plan, op);
                Log(actionLogPath, run, plan.ReleaseName, op, size, "intent", temp, kept: keeps);

                // 1. into the temporary name (a rename, or a copy across file systems; or, when the release stays for
                //    seeding, a copy or hard link); 2. verify; 3. rename into place; 4. open it again by its new name
                if (!keeps)
                {
                    _fs.Move(op.Source, temp);
                }
                else if (plan.Transfer != TransferMode.HardLink || !_fs.TryHardLink(op.Source, temp))
                {
                    _fs.Copy(op.Source, temp);
                }

                if (!_fs.Exists(temp) || _fs.Length(temp) != size || _fs.Exists(op.Source) != keeps)
                {
                    throw new IOException($"Move could not be verified: {op.Source} -> {temp}");
                }

                if (_fs.Exists(op.Destination))
                {
                    throw new IOException($"Destination appeared while moving: {op.Destination}");
                }

                _fs.Move(temp, op.Destination);

                // The rename returned, so the file is in place on the storage. One that can't be opened by its new name
                // (a stale cache in the mount) is still filed, not rolled back through the same cache, but reported
                if (ReadBack(op.Destination, cancellationToken) is not { } read)
                {
                    unreadable.Add(op.Destination);
                }
                else if (read != size)
                {
                    // It reached its real name, so the rollback puts it back from there
                    done.Add((op, size, temp));
                    throw new IOException($"Move could not be verified: {temp} -> {op.Destination}");
                }

                // Counted as done before the "done" line is written: if writing it fails (a full disk), the rollback also
                // moves this file back instead of leaving it filed while the rest of the release returns
                done.Add((op, size, temp));
                Log(actionLogPath, run, plan.ReleaseName, op, size, "done", temp, _fs.LastWriteUtc(op.Destination), keeps);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var (rolledBack, problems) = RollBack(actionLogPath, run, plan, op, size, temp, done, created, unreadable);
                return new ExecutionReport { Run = run, Failed = op, Error = ex.Message, RolledBack = rolledBack, RollbackProblems = problems };
            }
        }

        // Tidying up is best-effort: every move has succeeded, so a failure here is a warning, not a failed ingest. An
        // undo or restore leaves the folder it returns files to exactly as it finds it
        string? warning = null;
        if (plan.Returning.Count == 0 && _fs.Exists(releaseRoot) && !done.Any(d => string.Equals(d.Op.Source, releaseRoot, StringComparison.Ordinal)))
        {
            try
            {
                _fs.DeleteEmptyDirectories(releaseRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = ex.Message;
            }
        }

        // A replaced copy's folder left empty because its replacement went elsewhere; never outside its library folder
        foreach (var emptied in plan.TidyIfEmpty.Where(e => PathGuard.IsUnder(e.Folder, e.LibraryRoot)))
        {
            try
            {
                _fs.DeleteIfEmpty(emptied.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning ??= ex.Message;
            }
        }

        return new ExecutionReport { Run = run, Completed = [.. done.Select(d => d.Op)], Warning = warning, Unreadable = unreadable };
    }

    /// <summary>
    /// Finishes or discards moves that a crash, restart or power cut interrupted, using the action log: a move logged as
    /// started but not finished whose temporary file holds the whole file is completed; one whose copy was cut
    /// short (the original is still in place) has its partial copy deleted.
    /// </summary>
    /// <param name="actionLogLines">Lines of the action log.</param>
    /// <param name="actionLogPath">The action log, to record what recovery did.</param>
    /// <param name="allowedRoots">The current library and quarantine folders: recovery only touches a temporary file that
    /// has Ingest's own name, sits beside its destination, and is inside one of these.</param>
    /// <returns>A description of each thing done or needing attention.</returns>
    public IReadOnlyList<string> Recover(IEnumerable<string> actionLogLines, string actionLogPath, IReadOnlyCollection<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(actionLogLines);
        ArgumentNullException.ThrowIfNull(allowedRoots);

        var open = new Dictionary<string, ActionLogLine>(StringComparer.Ordinal);
        foreach (var line in actionLogLines)
        {
            if (ActionLogLine.TryParse(line) is not { Temp: { Length: > 0 } temp } entry)
            {
                continue;
            }

            if (entry.Phase == "intent")
            {
                open[temp] = entry;
            }
            else
            {
                open.Remove(temp);
            }
        }

        var results = new List<string>();
        foreach (var e in open.Values)
        {
            var op = new PlannedOperation(e.OperationKind, e.Source, e.Destination);

            // Defence in depth: the log's contents alone never decide what is deleted or renamed
            if (!IsOwnTemp(e.Temp!, e.Destination, allowedRoots))
            {
                results.Add($"Needs attention: the action log names an interrupted move that doesn't look like Ingest's ({e.Temp} for {e.Destination}); nothing was changed.");
                continue;
            }

            try
            {
                if (!_fs.Exists(e.Temp!))
                {
                    // Either never started, or finished just before the "done" record; either way nothing is left over
                    continue;
                }

                if (_fs.Exists(e.Source))
                {
                    // The copy was cut short: the original is intact, so the partial copy goes
                    _fs.Delete(e.Temp!);
                    Log(actionLogPath, e.Run, e.Release, op, e.Bytes, "discarded", e.Temp!);
                    results.Add($"Discarded an interrupted copy of {e.Source}; the original is still in place.");
                }
                else if (_fs.Length(e.Temp!) == e.Bytes && !_fs.Exists(e.Destination))
                {
                    _fs.Move(e.Temp!, e.Destination);
                    Log(actionLogPath, e.Run, e.Release, op, e.Bytes, "recovered", e.Temp!, _fs.LastWriteUtc(e.Destination), e.Kept);
                    results.Add($"Finished an interrupted move of {e.Source} to {e.Destination}.");
                }
                else
                {
                    results.Add($"Needs attention: an interrupted move left {e.Temp} (expected {e.Bytes} bytes for {e.Destination}).");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add($"Needs attention: couldn't recover the move of {e.Source} ({ex.Message}).");
            }
        }

        return results;
    }

    /// <summary>
    /// Whether a temporary file named in the action log is one Ingest would have made for that destination: its
    /// temporary name (<see cref="TempPathFor"/>, or the hidden <c>.ingest-&lt;id&gt;.partial</c> of older versions), in the
    /// destination's folder, inside a current library or quarantine folder.
    /// </summary>
    /// <param name="temp">The temporary file.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="allowedRoots">Current library and quarantine folders.</param>
    /// <returns><c>true</c> if recovery may act on it.</returns>
    public static bool IsOwnTemp(string temp, string destination, IReadOnlyCollection<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        if (string.IsNullOrEmpty(temp) || string.IsNullOrEmpty(destination) || !Path.IsPathFullyQualified(temp) || !Path.IsPathFullyQualified(destination))
        {
            return false;
        }

        var name = Path.GetFileName(temp);
        var own = Path.GetFileName(destination) + TempMarker;
        var current = own.Length > TempMarker.Length
            && name.Length == own.Length + TempIdLength + TempSuffix.Length
            && name.StartsWith(own, StringComparison.Ordinal) && name.EndsWith(TempSuffix, StringComparison.Ordinal)
            && !name.AsSpan(own.Length, TempIdLength).ContainsAnyExcept(HexDigits);
        var legacy = name.Length == TempPrefix.Length + 32 + TempSuffix.Length
            && name.StartsWith(TempPrefix, StringComparison.Ordinal) && name.EndsWith(TempSuffix, StringComparison.Ordinal)
            && !name.AsSpan(TempPrefix.Length, 32).ContainsAnyExcept(HexDigits);
        return (current || legacy)
            && PathGuard.SamePath(Path.GetDirectoryName(temp), Path.GetDirectoryName(destination))
            && PathGuard.IsUnderAny(destination, allowedRoots);
    }

    /// <summary>
    /// A hidden name beside a file, for a library copy an undo removes: the copy is first moved there (so the undo can
    /// still be rolled back), and deleted once the whole undo has succeeded (<see cref="DeleteSetAside"/>).
    /// </summary>
    /// <param name="file">The library copy.</param>
    /// <returns>The hidden name, in the same folder.</returns>
    public static string SetAsidePathFor(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        return Path.Combine(Path.GetDirectoryName(file)!, TempPrefix + Guid.NewGuid().ToString("N") + TrashSuffix);
    }

    /// <summary>
    /// Whether a path is a copy set aside by an undo (<see cref="SetAsidePathFor"/>) inside one of the given folders.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="allowedRoots">Current library folders.</param>
    /// <returns><c>true</c> if it may be deleted.</returns>
    public static bool IsSetAside(string path, IReadOnlyCollection<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return name.Length == TempPrefix.Length + 32 + TrashSuffix.Length
            && name.StartsWith(TempPrefix, StringComparison.Ordinal) && name.EndsWith(TrashSuffix, StringComparison.Ordinal)
            && !name.AsSpan(TempPrefix.Length, 32).ContainsAnyExcept(HexDigits)
            && PathGuard.IsUnderAny(path, allowedRoots);
    }

    /// <summary>
    /// Deletes the library copies an undo set aside, once every move of the undo has succeeded; each deletion is logged.
    /// </summary>
    /// <param name="setAside">The moves that set a copy aside (their destinations are deleted).</param>
    /// <param name="release">The release, for the log.</param>
    /// <param name="run">The undo's id, for the log.</param>
    /// <param name="actionLogPath">The action log.</param>
    /// <param name="allowedRoots">Current library folders; nothing else is deleted.</param>
    /// <returns>Anything that couldn't be deleted (hidden, so Jellyfin ignores it; it is tried again at the next start).</returns>
    public IReadOnlyList<string> DeleteSetAside(IEnumerable<PlannedOperation> setAside, string release, string run, string actionLogPath, IReadOnlyCollection<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(setAside);
        var problems = new List<string>();
        foreach (var op in setAside)
        {
            if (!IsSetAside(op.Destination, allowedRoots))
            {
                problems.Add($"{op.Destination}: not a copy set aside by Ingest; left alone.");
                continue;
            }

            try
            {
                var bytes = _fs.Exists(op.Destination) ? _fs.Length(op.Destination) : 0;
                _fs.Delete(op.Destination);
                Log(actionLogPath, run, release, op, bytes, "deleted", op.Destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{op.Destination}: {ex.Message}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Deletes library copies that an undo set aside but a crash or restart stopped it deleting (see
    /// <see cref="DeleteSetAside"/>). Run after <see cref="Recover"/>, with the log read again.
    /// </summary>
    /// <param name="actionLogLines">Lines of the action log.</param>
    /// <param name="actionLogPath">The action log, to record what was deleted.</param>
    /// <param name="allowedRoots">The current library folders.</param>
    /// <returns>A description of each thing done or needing attention.</returns>
    public IReadOnlyList<string> FinishDeletes(IEnumerable<string> actionLogLines, string actionLogPath, IReadOnlyCollection<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(actionLogLines);
        var pending = new Dictionary<string, ActionLogLine>(StringComparer.Ordinal);
        foreach (var entry in actionLogLines.Select(ActionLogLine.TryParse).OfType<ActionLogLine>())
        {
            if (entry.IsCompleted && entry.Phase is not null && Path.GetFileName(entry.Destination).EndsWith(TrashSuffix, StringComparison.Ordinal))
            {
                pending[entry.Destination] = entry;
            }
            else if (entry.Phase is "deleted" or "rolled-back")
            {
                pending.Remove(entry.Destination);
            }
        }

        var results = new List<string>();
        foreach (var e in pending.Values.Where(e => _fs.Exists(e.Destination)))
        {
            if (!IsSetAside(e.Destination, allowedRoots))
            {
                results.Add($"Needs attention: the action log names a set-aside copy that doesn't look like Ingest's ({e.Destination}); nothing was changed.");
                continue;
            }

            results.AddRange(DeleteSetAside([new PlannedOperation(e.OperationKind, e.Source, e.Destination)], e.Release, e.Run ?? string.Empty, actionLogPath, allowedRoots)
                .Select(p => "Needs attention: " + p)
                .DefaultIfEmpty($"Deleted the library copy {e.Source} that an interrupted undo had set aside."));
        }

        return results;
    }

    // With copy or hard link the release's own files stay (only library copies being replaced are moved)
    private static bool KeepsSource(IngestPlan plan, PlannedOperation op)
        => plan.Transfer != TransferMode.Move && op.Kind != OperationKind.Quarantine;

    // Opens a file just renamed into place by its new name; when that fails, lists its folder again (a fresh look-up) and
    // retries, waiting a little longer each time. Null: it still can't be opened
    private long? ReadBack(string path, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(path)!;
        var delay = ReadBackDelay;
        for (var attempt = 1; ; attempt++)
        {
            if (_fs.ReadBack(path) is { } length)
            {
                return length;
            }

            if (attempt >= ReadBackAttempts)
            {
                return null;
            }

            if (delay > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(delay))
            {
                return null;
            }

            delay += delay;
            _fs.Relist(folder);
        }
    }

    private (IReadOnlyList<PlannedOperation> RolledBack, IReadOnlyList<string> Problems) RollBack(
        string logPath, string run, IngestPlan plan, PlannedOperation failed, long size, string temp, List<(PlannedOperation Op, long Bytes, string Temp)> done, List<string> created, List<string> unreadable)
    {
        var rolledBack = new List<PlannedOperation>();
        var problems = new List<string>();

        // A file the mount couldn't read back may look missing here while it is still on the storage: it is never
        // assumed removed or moved back
        foreach (var path in unreadable)
        {
            problems.Add($"{path}: filed, but it couldn't be read back through the library's mount, so it is left where it is. {UnreadableAdvice}");
        }

        // The failed move itself: put a file that reached the temporary name back, or drop a partial copy
        try
        {
            if (_fs.Exists(temp))
            {
                if (_fs.Exists(failed.Source))
                {
                    _fs.Delete(temp);
                }
                else
                {
                    _fs.Move(temp, failed.Source);
                }

                Log(logPath, run, plan.ReleaseName, failed, size, "rolled-back", temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{failed.Source}: {ex.Message} (left at {temp})");
        }

        // Then everything already filed, newest first (a copy or link of a file that stayed is simply removed)
        foreach (var (op, bytes, opTemp) in Enumerable.Reverse(done))
        {
            if (unreadable.Contains(op.Destination, StringComparer.Ordinal))
            {
                continue;
            }

            try
            {
                if (KeepsSource(plan, op) && _fs.Exists(op.Source))
                {
                    if (_fs.Exists(op.Destination))
                    {
                        _fs.Delete(op.Destination);
                    }

                    Log(logPath, run, plan.ReleaseName, op, bytes, "rolled-back", opTemp);
                    rolledBack.Add(op);
                    continue;
                }

                if (!_fs.Exists(op.Destination) || _fs.Exists(op.Source))
                {
                    problems.Add($"{op.Destination}: can't be moved back to {op.Source}.");
                    continue;
                }

                _fs.CreateDirectory(Path.GetDirectoryName(op.Source)!);
                _fs.Move(op.Destination, op.Source);
                Log(logPath, run, plan.ReleaseName, op, bytes, "rolled-back", opTemp);
                rolledBack.Add(op);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{op.Destination}: {ex.Message}");
            }
        }

        // Remove the folders this attempt created, deepest first, if they are empty again (never anything with content)
        foreach (var dir in created.Distinct(StringComparer.Ordinal).OrderByDescending(d => d.Length))
        {
            try
            {
                _fs.DeleteEmptyDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{dir}: {ex.Message}");
            }
        }

        return (rolledBack, problems);
    }

    private void Log(string path, string? run, string release, PlannedOperation op, long bytes, string phase, string temp, DateTime? modified = null, bool? kept = null)
        => _fs.AppendLine(path, JsonSerializer.Serialize(new ActionLogLine
        {
            Time = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            Release = release,
            Kind = op.Kind.ToString(),
            Source = op.Source,
            Destination = op.Destination,
            Bytes = bytes,
            Phase = phase,
            Temp = temp,
            Run = run,
            Modified = modified?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Kept = kept == true ? true : null,
        }));
}

/// <summary>
/// <see cref="IFileOperations"/> on the real file system.
/// </summary>
public sealed class PhysicalFileOperations : IFileOperations
{
    // Headroom kept free on the destination when copying across file systems
    private const long SpareBytes = 256L * 1024 * 1024;

    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <inheritdoc />
    public long Length(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);

    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);

    /// <inheritdoc />
    public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: false);

    /// <inheritdoc />
    public bool TryHardLink(string source, string destination) => NativeMethods.TryHardLink(source, destination);

    /// <inheritdoc />
    public void AppendLine(string path, string line) => File.AppendAllLines(path, [line]);

    /// <inheritdoc />
    public long? ReadBack(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Relist(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            while (entries.MoveNext())
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a nudge to the file system's cache
        }
    }

    /// <inheritdoc />
    public DateTime? LastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The free space on the file system a folder is on, for choosing between a library's folders.
    /// </summary>
    /// <param name="folder">An existing folder.</param>
    /// <returns>The bytes free, or <c>null</c> if the folder isn't there or the space can't be read.</returns>
    public static long? FreeBytes(string folder)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && MountOf(folder) is { } mount ? mount.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool HasRoomFor(string source, string destinationFolder, long bytes)
    {
        try
        {
            var from = MountOf(source);
            var to = MountOf(destinationFolder);
            return from is null || to is null || string.Equals(from.RootDirectory.FullName, to.RootDirectory.FullName, StringComparison.Ordinal)
                || to.AvailableFreeSpace >= bytes + SpareBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell: don't block the move; a real shortage still fails the copy and is rolled back
            return true;
        }
    }

    /// <inheritdoc />
    public bool DeleteIfEmpty(string path)
    {
        if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null || Directory.EnumerateFileSystemEntries(path).Any())
        {
            return false;
        }

        // Not recursive: fails rather than deletes if something arrived in the meantime
        Directory.Delete(path, recursive: false);
        return true;
    }

    /// <inheritdoc />
    public void DeleteEmptyDirectories(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Never walk into or delete through a symbolic link or junction
        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(path, "*", Jellyfin.Plugin.Ingest.Service.ReleaseScanner.DeepWalk).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    // The mounted file system a path is on (the longest mount point that contains it)
    private static DriveInfo? MountOf(string path)
    {
        // The mount is chosen by path alone, and only that one is queried: asking every mount whether it is ready would
        // wait on any network share that has stopped answering, even one Ingest never uses
        var full = PathGuard.Normalise(path);
        var mount = DriveInfo.GetDrives()
            .Where(d => PathGuard.IsSameOrUnder(full, d.Name))
            .OrderByDescending(d => d.Name.Length)
            .FirstOrDefault();
        return mount is { IsReady: true } ? mount : null;
    }
}
