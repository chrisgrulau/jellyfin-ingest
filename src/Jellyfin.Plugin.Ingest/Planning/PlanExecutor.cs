using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

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

    /// <summary>Removes empty directories under (and including) a directory.</summary>
    /// <param name="path">Absolute path.</param>
    void DeleteEmptyDirectories(string path);

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

    /// <summary>Gets a value indicating whether every operation completed.</summary>
    public bool Succeeded => Failed is null && !Cancelled && Error is null;
}

/// <summary>
/// Carries out an <see cref="IngestPlan"/> so that a release is filed completely or not at all, and a crash can never
/// leave a half-copied file under a real name:
/// <list type="bullet">
/// <item>every file first moves to a hidden temporary name in its destination folder (Jellyfin ignores hidden files),
/// its size is verified, then it is renamed into place;</item>
/// <item>each move is written to the action log before (<c>intent</c>) and after (<c>done</c>), so an interrupted move can
/// be finished or discarded at the next start (<see cref="Recover"/>);</item>
/// <item>if a move fails, the moves already made are undone in reverse order;</item>
/// <item>nothing is ever overwritten, and every destination is re-checked against the plan's allowed folders first.</item>
/// </list>
/// </summary>
public sealed class PlanExecutor
{
    private const string TempPrefix = ".ingest-";
    private const string TempSuffix = ".partial";

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
            if (!PathGuard.IsSameOrUnder(op.Source, releaseRoot))
            {
                return new ExecutionReport { Failed = op, Error = $"Refused: {op.Source} is outside the release being filed." };
            }

            if (!PathGuard.IsUnderAny(op.Destination, plan.AllowedRoots))
            {
                return new ExecutionReport { Failed = op, Error = $"Refused: {op.Destination} is outside the folders this release may be filed into." };
            }
        }

        var done = new List<(PlannedOperation Op, long Bytes, string Temp)>();
        var created = new List<string>();
        foreach (var op in plan.Operations)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ExecutionReport
                {
                    Completed = [.. done.Select(d => d.Op)],
                    Cancelled = true,
                    Error = string.Create(CultureInfo.InvariantCulture, $"Stopped because the server is shutting down: {done.Count} of {plan.Operations.Count} files moved; the rest are still in the watch folder."),
                };
            }

            var folder = Path.GetDirectoryName(op.Destination)!;
            var temp = Path.Combine(folder, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);
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

                Log(actionLogPath, plan.ReleaseName, op, size, "intent", temp);

                // 1. into a hidden name (a rename, or a copy across file systems); 2. verify; 3. rename into place
                _fs.Move(op.Source, temp);
                if (!_fs.Exists(temp) || _fs.Length(temp) != size || _fs.Exists(op.Source))
                {
                    throw new IOException($"Move could not be verified: {op.Source} -> {temp}");
                }

                if (_fs.Exists(op.Destination))
                {
                    throw new IOException($"Destination appeared while moving: {op.Destination}");
                }

                _fs.Move(temp, op.Destination);
                if (!_fs.Exists(op.Destination) || _fs.Length(op.Destination) != size)
                {
                    throw new IOException($"Move could not be verified: {temp} -> {op.Destination}");
                }

                // Counted as done before the "done" line is written: if writing it fails (a full disk), the rollback also
                // moves this file back instead of leaving it filed while the rest of the release returns
                done.Add((op, size, temp));
                Log(actionLogPath, plan.ReleaseName, op, size, "done", temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var (rolledBack, problems) = RollBack(actionLogPath, plan, op, size, temp, done, created);
                return new ExecutionReport { Failed = op, Error = ex.Message, RolledBack = rolledBack, RollbackProblems = problems };
            }
        }

        // Tidying up is best-effort: every move has succeeded, so a failure here is a warning, not a failed ingest
        string? warning = null;
        if (_fs.Exists(releaseRoot) && !done.Any(d => string.Equals(d.Op.Source, releaseRoot, StringComparison.Ordinal)))
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

        return new ExecutionReport { Completed = [.. done.Select(d => d.Op)], Warning = warning };
    }

    /// <summary>
    /// Finishes or discards moves that a crash, restart or power cut interrupted, using the action log: a move logged as
    /// started but not finished whose hidden temporary file holds the whole file is completed; one whose copy was cut
    /// short (the original is still in place) has its partial copy deleted.
    /// </summary>
    /// <param name="actionLogLines">Lines of the action log.</param>
    /// <param name="actionLogPath">The action log, to record what recovery did.</param>
    /// <returns>A description of each thing done or needing attention.</returns>
    public IReadOnlyList<string> Recover(IEnumerable<string> actionLogLines, string actionLogPath)
    {
        ArgumentNullException.ThrowIfNull(actionLogLines);

        var open = new Dictionary<string, LogEntry>(StringComparer.Ordinal);
        foreach (var line in actionLogLines)
        {
            if (LogEntry.TryParse(line) is not { Temp: { Length: > 0 } temp } entry)
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
            var op = new PlannedOperation(Enum.TryParse<OperationKind>(e.Kind, out var k) ? k : OperationKind.Video, e.Source, e.Destination);
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
                    Log(actionLogPath, e.Release, op, e.Bytes, "discarded", e.Temp!);
                    results.Add($"Discarded an interrupted copy of {e.Source}; the original is still in place.");
                }
                else if (_fs.Length(e.Temp!) == e.Bytes && !_fs.Exists(e.Destination))
                {
                    _fs.Move(e.Temp!, e.Destination);
                    Log(actionLogPath, e.Release, op, e.Bytes, "recovered", e.Temp!);
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

    private (IReadOnlyList<PlannedOperation> RolledBack, IReadOnlyList<string> Problems) RollBack(
        string logPath, IngestPlan plan, PlannedOperation failed, long size, string temp, List<(PlannedOperation Op, long Bytes, string Temp)> done, List<string> created)
    {
        var rolledBack = new List<PlannedOperation>();
        var problems = new List<string>();

        // The failed move itself: put a file that reached the hidden name back, or drop a partial copy
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

                Log(logPath, plan.ReleaseName, failed, size, "rolled-back", temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{failed.Source}: {ex.Message} (left at {temp})");
        }

        // Then everything already filed, newest first
        foreach (var (op, bytes, opTemp) in Enumerable.Reverse(done))
        {
            try
            {
                if (!_fs.Exists(op.Destination) || _fs.Exists(op.Source))
                {
                    problems.Add($"{op.Destination}: can't be moved back to {op.Source}.");
                    continue;
                }

                _fs.CreateDirectory(Path.GetDirectoryName(op.Source)!);
                _fs.Move(op.Destination, op.Source);
                Log(logPath, plan.ReleaseName, op, bytes, "rolled-back", opTemp);
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

    private void Log(string path, string release, PlannedOperation op, long bytes, string phase, string temp)
        => _fs.AppendLine(path, JsonSerializer.Serialize(new LogEntry
        {
            Time = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            Release = release,
            Kind = op.Kind.ToString(),
            Source = op.Source,
            Destination = op.Destination,
            Bytes = bytes,
            Phase = phase,
            Temp = temp,
        }));

    /// <summary>One action-log line. Lines written before phases existed have no <see cref="Phase"/> and count as done.</summary>
    private sealed record LogEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("time")]
        public string Time { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("release")]
        public string Release { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("kind")]
        public string Kind { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("destination")]
        public string Destination { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("bytes")]
        public long Bytes { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("phase")]
        public string? Phase { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("temp")]
        public string? Temp { get; init; }

        public static LogEntry? TryParse(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<LogEntry>(line);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
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
    public void AppendLine(string path, string line) => File.AppendAllLines(path, [line]);

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
