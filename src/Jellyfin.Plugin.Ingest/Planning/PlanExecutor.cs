using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

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

    /// <summary>Moves a file without overwriting (copy + delete across file systems).</summary>
    /// <param name="source">Current path.</param>
    /// <param name="destination">Target path, which must not exist.</param>
    void Move(string source, string destination);

    /// <summary>Removes empty directories under (and including) a directory.</summary>
    /// <param name="path">Absolute path.</param>
    void DeleteEmptyDirectories(string path);

    /// <summary>Appends a line to a text file (the action log).</summary>
    /// <param name="path">Absolute path.</param>
    /// <param name="line">The line.</param>
    void AppendLine(string path, string line);
}

/// <summary>
/// What happened when a plan was executed.
/// </summary>
public sealed record ExecutionReport
{
    /// <summary>Gets the operations that completed.</summary>
    public IReadOnlyList<PlannedOperation> Completed { get; init; } = [];

    /// <summary>Gets the operation that failed (execution stops there), if any.</summary>
    public PlannedOperation? Failed { get; init; }

    /// <summary>Gets the failure message, if any.</summary>
    public string? Error { get; init; }

    /// <summary>Gets a warning about a non-essential step that failed after every move succeeded (e.g. tidying up).</summary>
    public string? Warning { get; init; }

    /// <summary>Gets a value indicating whether nothing was changed because it was a dry run.</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets a value indicating whether every operation completed.</summary>
    public bool Succeeded => Failed is null;
}

/// <summary>
/// Carries out an <see cref="IngestPlan"/>: never overwrites, verifies sizes after each move, records every completed
/// move in a JSON-lines action log (so it can be undone), stops at the first failure, and tidies empty folders.
/// </summary>
public sealed class PlanExecutor
{
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
    /// <param name="actionLogPath">JSON-lines file every completed move is appended to.</param>
    /// <param name="dryRun">When <c>true</c>, nothing is touched and every operation is reported as it would run.</param>
    /// <returns>The execution report.</returns>
    public ExecutionReport Execute(IngestPlan plan, string releaseRoot, string actionLogPath, bool dryRun)
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

        var done = new List<PlannedOperation>();
        foreach (var op in plan.Operations)
        {
            try
            {
                if (_fs.Exists(op.Destination))
                {
                    throw new IOException($"Destination appeared since planning: {op.Destination}");
                }

                var size = _fs.Length(op.Source);
                _fs.CreateDirectory(Path.GetDirectoryName(op.Destination)!);
                _fs.Move(op.Source, op.Destination);
                if (!_fs.Exists(op.Destination) || _fs.Length(op.Destination) != size || _fs.Exists(op.Source))
                {
                    throw new IOException($"Move could not be verified: {op.Source} -> {op.Destination}");
                }

                _fs.AppendLine(actionLogPath, JsonSerializer.Serialize(new
                {
                    time = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                    release = plan.ReleaseName,
                    kind = op.Kind.ToString(),
                    source = op.Source,
                    destination = op.Destination,
                    bytes = size,
                }));
                done.Add(op);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ExecutionReport { Completed = done, Failed = op, Error = ex.Message };
            }
        }

        // Tidying up is best-effort: every move has succeeded, so a failure here is a warning, not a failed ingest
        string? warning = null;
        if (_fs.Exists(releaseRoot) && !done.Any(d => string.Equals(d.Source, releaseRoot, StringComparison.Ordinal)))
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

        return new ExecutionReport { Completed = done, Warning = warning };
    }
}

/// <summary>
/// <see cref="IFileOperations"/> on the real file system.
/// </summary>
public sealed class PhysicalFileOperations : IFileOperations
{
    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <inheritdoc />
    public long Length(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);

    /// <inheritdoc />
    public void AppendLine(string path, string line) => File.AppendAllLines(path, [line]);

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
}
