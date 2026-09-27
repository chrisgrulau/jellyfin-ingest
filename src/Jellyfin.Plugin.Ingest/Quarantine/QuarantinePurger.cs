using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Service;

namespace Jellyfin.Plugin.Ingest.Quarantine;

/// <summary>
/// Deletes quarantine folders past the retention period. Only top-level folders named <c>yyyy-MM-dd</c> (the date the
/// clutter was quarantined) that Ingest created and marked are ever deleted; anything else is left alone.
/// </summary>
public static class QuarantinePurger
{
    /// <summary>What "Delete now" says when Ingest is filing and doesn't finish in time.</summary>
    public const string BusyMessage = "Ingest is filing right now; try again in a minute.";

    /// <summary>
    /// Lists the dated folders that have expired.
    /// </summary>
    /// <param name="folderNames">Top-level folder names in the quarantine folder.</param>
    /// <param name="today">Today's date.</param>
    /// <param name="retentionDays">Days to keep.</param>
    /// <returns>The folder names to delete.</returns>
    public static IReadOnlyList<string> Expired(IEnumerable<string> folderNames, DateOnly today, int retentionDays)
    {
        ArgumentNullException.ThrowIfNull(folderNames);
        ArgumentOutOfRangeException.ThrowIfNegative(retentionDays);

        var cutoff = today.AddDays(-retentionDays);
        return [.. folderNames.Where(n => QuarantineMarkers.DateOf(n) is { } d && d < cutoff)];
    }

    /// <summary>
    /// Deletes expired dated folders under a quarantine root. A folder that can't be fully deleted (a read-only or open
    /// file) keeps its marker, so it is tried again next time, and the other folders are still purged. The caller holds
    /// the <see cref="FileOperationsGate"/> (see <see cref="PurgeAsync(FileOperationsGate, TimeSpan, IReadOnlyList{string}, DateOnly, int, QuarantineDeleteOptions?, CancellationToken)"/>).
    /// </summary>
    /// <param name="quarantineRoot">Absolute quarantine folder.</param>
    /// <param name="today">Today's date.</param>
    /// <param name="retentionDays">Days to keep.</param>
    /// <param name="options">Retry settings (the defaults when <c>null</c>).</param>
    /// <param name="cancellationToken">Stops waiting between retries.</param>
    /// <returns>The folders deleted, and those that couldn't be fully deleted, with why.</returns>
    public static async Task<PurgeResult> PurgeAsync(string quarantineRoot, DateOnly today, int retentionDays, QuarantineDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        if (!Directory.Exists(quarantineRoot))
        {
            return new PurgeResult([], []);
        }

        var expired = Expired(Directory.EnumerateDirectories(quarantineRoot).Select(Path.GetFileName).OfType<string>(), today, retentionDays);
        var deleted = new List<string>();
        var problems = new List<string>();
        foreach (var name in expired)
        {
            // Only folders Ingest created and marked; a date-named folder that was already there is never touched
            var path = Path.Combine(quarantineRoot, name);
            if (!QuarantineMarkers.IsMarked(path) || new DirectoryInfo(path).LinkTarget is not null)
            {
                continue;
            }

            try
            {
                await DeleteMarkedFolderAsync(path, options ?? QuarantineDeleteOptions.Default, cancellationToken).ConfigureAwait(false);
                deleted.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(path + ": " + ex.Message);
            }
        }

        return new PurgeResult(deleted, problems);
    }

    /// <summary>
    /// Purges several quarantine folders while holding Ingest's file-operations gate for the whole purge, waiting up to
    /// <paramref name="wait"/> for a filing that is running to finish.
    /// </summary>
    /// <param name="gate">The file-operations gate.</param>
    /// <param name="wait">How long to wait for it.</param>
    /// <param name="quarantineRoots">The quarantine folders.</param>
    /// <param name="today">Today's date.</param>
    /// <param name="retentionDays">Days to keep.</param>
    /// <param name="options">Retry settings (the defaults when <c>null</c>).</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Each quarantine folder's result, or <c>null</c> if Ingest was busy filing and nothing was touched.</returns>
    public static async Task<IReadOnlyList<PurgeResult>?> PurgeAsync(FileOperationsGate gate, TimeSpan wait, IReadOnlyList<string> quarantineRoots, DateOnly today, int retentionDays, QuarantineDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(quarantineRoots);
        using var held = await gate.TryEnterAsync(wait, cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            return null;
        }

        var results = new List<PurgeResult>();
        foreach (var root in quarantineRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await PurgeAsync(root, today, retentionDays, options, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// "Delete now" from the page, while holding Ingest's file-operations gate for the whole deletion (so nothing is moved
    /// into the folder while it is deleted), waiting up to <paramref name="wait"/> for a filing that is running to finish.
    /// </summary>
    /// <param name="gate">The file-operations gate.</param>
    /// <param name="wait">How long to wait for it.</param>
    /// <param name="datedFolder">The dated folder (checked by the caller to be inside a quarantine folder in use).</param>
    /// <param name="release">An entry in it to delete, or <c>null</c> for the whole folder.</param>
    /// <param name="options">Retry settings (the defaults when <c>null</c>).</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>What happened, with a message to show when it didn't.</returns>
    public static async Task<QuarantineDeleteResult> DeleteNowAsync(FileOperationsGate gate, TimeSpan wait, string datedFolder, string? release, QuarantineDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        using var held = await gate.TryEnterAsync(wait, cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            return new QuarantineDeleteResult(QuarantineDeleteStatus.Busy, BusyMessage);
        }

        try
        {
            await DeleteNowAsync(datedFolder, release, options, cancellationToken).ConfigureAwait(false);
            return new QuarantineDeleteResult(QuarantineDeleteStatus.Deleted, null);
        }
        catch (ArgumentException ex)
        {
            return new QuarantineDeleteResult(QuarantineDeleteStatus.Refused, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new QuarantineDeleteResult(QuarantineDeleteStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Deletes a dated quarantine folder, or one release in it, now ("Delete now" on the page), with the same care as the
    /// purge: only in a dated folder Ingest created and marked, never through a link (a link is removed, not followed),
    /// and read-only files made writable first. Deleting the whole folder removes its marker last, and only once nothing
    /// else is left. The caller holds the <see cref="FileOperationsGate"/>.
    /// </summary>
    /// <param name="datedFolder">The dated folder (checked by the caller to be inside a quarantine folder in use).</param>
    /// <param name="release">An entry in it to delete, or <c>null</c> for the whole folder.</param>
    /// <param name="options">Retry settings (the defaults when <c>null</c>).</param>
    /// <param name="cancellationToken">Stops waiting between retries.</param>
    /// <returns>A task.</returns>
    /// <exception cref="IOException">Something couldn't be deleted (what was deleted stays deleted; the folder stays marked).</exception>
    /// <exception cref="ArgumentException">The folder isn't Ingest's, or the release isn't a plain entry in it.</exception>
    public static async Task DeleteNowAsync(string datedFolder, string? release, QuarantineDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datedFolder);
        if (!Directory.Exists(datedFolder) || new DirectoryInfo(datedFolder).LinkTarget is not null || !QuarantineMarkers.IsMarked(datedFolder)
            || QuarantineMarkers.DateOf(Path.GetFileName(Path.TrimEndingDirectorySeparator(datedFolder))) is null)
        {
            throw new ArgumentException("Only a dated quarantine folder Ingest created can be deleted.", nameof(datedFolder));
        }

        options ??= QuarantineDeleteOptions.Default;
        if (release is null)
        {
            await DeleteMarkedFolderAsync(datedFolder, options, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (release is "." or ".." || release.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || string.Equals(release, QuarantineMarkers.DatedMarker, StringComparison.Ordinal))
        {
            throw new ArgumentException("That isn't a release in the dated folder.", nameof(release));
        }

        // Found on disk by name, never a path built from the name
        var dir = new DirectoryInfo(datedFolder);
        if (!dir.EnumerateFileSystemInfos().Any(e => string.Equals(e.Name, release, StringComparison.Ordinal)))
        {
            throw new ArgumentException("That release isn't in quarantine any more.", nameof(release));
        }

        var (left, _) = await DeleteEntriesAsync(dir, e => string.Equals(e.Name, release, StringComparison.Ordinal), options, cancellationToken).ConfigureAwait(false);
        if (left.Count > 0)
        {
            throw new IOException(NotYetDeleted(CountItems(left)));
        }
    }

    /// <summary>
    /// Whether a name is a temporary entry a network or FUSE file system leaves in place of a file deleted while still
    /// open (<c>.smbXXXX</c>, <c>.fuse_hiddenXXXX</c>, <c>.nfsXXXX</c>), which goes away by itself shortly after.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns><c>true</c> for such a name.</returns>
    public static bool IsTransientName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith(".smb", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".fuse_hidden", StringComparison.Ordinal)
            || name.StartsWith(".nfs", StringComparison.Ordinal);
    }

    // The message when something is still there after every pass and retry
    private static string NotYetDeleted(int count) => string.Create(
        CultureInfo.InvariantCulture,
        $"{count} item{(count == 1 ? string.Empty : "s")} couldn't be deleted yet (still in use?); {(count == 1 ? "it" : "they")}'ll be removed by the next purge, or try again.");

    // Everything but the marker; then anything that arrived meanwhile; then (for leftovers that are only temporary names
    // of files still open elsewhere) a few short waits. Only once nothing but the marker is left are the marker and then
    // the folder deleted, so a folder is never left without its marker (an unmarked folder is never purged)
    private static async Task DeleteMarkedFolderAsync(string path, QuarantineDeleteOptions options, CancellationToken cancellationToken)
    {
        var dir = new DirectoryInfo(path);
        static bool NotMarker(FileSystemInfo e) => !string.Equals(e.Name, QuarantineMarkers.DatedMarker, StringComparison.Ordinal);
        var (left, _) = await DeleteEntriesAsync(dir, NotMarker, options, cancellationToken).ConfigureAwait(false);
        if (left.Count == 0)
        {
            options.BeforeFolderDelete?.Invoke();
            File.Delete(Path.Combine(path, QuarantineMarkers.DatedMarker));
            try
            {
                Directory.Delete(path);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (Directory.Exists(path))
            {
                // Something arrived after the last look: the folder stays, so it keeps its marker
                QuarantineMarkers.Remark(path);
                left = [.. dir.EnumerateFileSystemInfos().Where(NotMarker)];
            }
        }

        throw new IOException(NotYetDeleted(Math.Max(1, CountItems(left))));
    }

    // Deletes the entries of a folder that match, in passes: a first pass, a second for anything still there or newly
    // arrived, then short bounded waits while all that is left are temporary names that go away by themselves
    private static async Task<(IReadOnlyList<FileSystemInfo> Left, string? LastError)> DeleteEntriesAsync(DirectoryInfo dir, Func<FileSystemInfo, bool> include, QuarantineDeleteOptions options, CancellationToken cancellationToken)
    {
        string? lastError = null;
        List<FileSystemInfo> Remaining()
        {
            dir.Refresh();
            return dir.Exists ? [.. dir.EnumerateFileSystemInfos().Where(include)] : [];
        }

        void Pass()
        {
            foreach (var entry in Remaining())
            {
                try
                {
                    DeleteEntry(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastError = ex.Message;
                }
            }
        }

        Pass();
        options.AfterPass?.Invoke(1);
        var left = Remaining();
        if (left.Count > 0)
        {
            Pass();
            options.AfterPass?.Invoke(2);
            left = Remaining();
        }

        for (var attempt = 0; left.Count > 0 && attempt < options.TransientRetries && left.All(IsTransient); attempt++)
        {
            await options.Delay(options.TransientWait, cancellationToken).ConfigureAwait(false);
            Pass();
            left = Remaining();
        }

        return (left, lastError);
    }

    // A temporary name, or a folder holding nothing but temporary names (its delete failed because of them)
    private static bool IsTransient(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo dir && dir.LinkTarget is null)
        {
            try
            {
                return dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
                    .All(f => IsTransientName(f.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return IsTransientName(entry.Name);
    }

    // Files and links left (a folder counts what is in it), for the message
    private static int CountItems(IEnumerable<FileSystemInfo> left)
        => left.Sum(e =>
        {
            if (e is DirectoryInfo dir && dir.LinkTarget is null)
            {
                try
                {
                    return Math.Max(1, dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Count());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return 1;
                }
            }

            return 1;
        });

    // A folder with everything in it (never following a link: a link is removed itself), or a file
    private static void DeleteEntry(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo dir && dir.LinkTarget is null)
        {
            foreach (var file in dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                file.IsReadOnly = false;
            }

            dir.Delete(recursive: true);
        }
        else
        {
            if (entry is FileInfo file)
            {
                file.IsReadOnly = false;
            }

            entry.Delete();
        }
    }
}

/// <summary>
/// What a purge of one quarantine folder did.
/// </summary>
/// <param name="Deleted">The dated folders deleted.</param>
/// <param name="Failed">Those that couldn't be fully deleted (they keep their marker and are tried again), with why.</param>
public sealed record PurgeResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed);

/// <summary>
/// How a "Delete now" went.
/// </summary>
public enum QuarantineDeleteStatus
{
    /// <summary>Deleted.</summary>
    Deleted,

    /// <summary>Ingest was filing and didn't finish in time; nothing was touched.</summary>
    Busy,

    /// <summary>Not a folder or release that can be deleted; nothing was touched.</summary>
    Refused,

    /// <summary>Partly deleted: something couldn't be deleted yet (the folder stays marked).</summary>
    Failed,
}

/// <summary>
/// The outcome of a "Delete now".
/// </summary>
/// <param name="Status">How it went.</param>
/// <param name="Message">What to show when it didn't go through.</param>
public sealed record QuarantineDeleteResult(QuarantineDeleteStatus Status, string? Message);

/// <summary>
/// How hard a quarantine deletion tries: temporary names left by files still open elsewhere (on network and FUSE mounts)
/// go away by themselves shortly after, so they are waited for a few times.
/// </summary>
public sealed class QuarantineDeleteOptions
{
    /// <summary>Gets the defaults: up to 5 waits of 1 second.</summary>
    public static QuarantineDeleteOptions Default { get; } = new();

    /// <summary>Gets how many times to wait for temporary names to go away.</summary>
    public int TransientRetries { get; init; } = 5;

    /// <summary>Gets how long each wait is.</summary>
    public TimeSpan TransientWait { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how to wait (replaced in tests).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Gets a test hook called after each deletion pass (1, 2).</summary>
    internal Action<int>? AfterPass { get; init; }

    /// <summary>Gets a test hook called just before the marker and folder are deleted.</summary>
    internal Action? BeforeFolderDelete { get; init; }
}
