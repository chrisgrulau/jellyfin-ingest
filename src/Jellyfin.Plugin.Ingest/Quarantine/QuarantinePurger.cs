using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;

namespace Jellyfin.Plugin.Ingest.Quarantine;

/// <summary>
/// Deletes quarantine folders past the retention period. Only top-level folders named <c>yyyy-MM-dd</c> (the date the
/// clutter was quarantined) that Ingest created and marked are ever deleted; anything else is left alone.
/// </summary>
public static class QuarantinePurger
{
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
    /// Deletes expired dated folders under a quarantine root.
    /// </summary>
    /// <param name="quarantineRoot">Absolute quarantine folder.</param>
    /// <param name="today">Today's date.</param>
    /// <param name="retentionDays">Days to keep.</param>
    /// <returns>The folders deleted.</returns>
    public static IReadOnlyList<string> Purge(string quarantineRoot, DateOnly today, int retentionDays)
        => Purge(quarantineRoot, today, retentionDays, out _);

    /// <summary>
    /// Deletes expired dated folders under a quarantine root. A folder that can't be fully deleted (a read-only or open
    /// file) keeps its marker, so it is tried again next time, and the other folders are still purged.
    /// </summary>
    /// <param name="quarantineRoot">Absolute quarantine folder.</param>
    /// <param name="today">Today's date.</param>
    /// <param name="retentionDays">Days to keep.</param>
    /// <param name="failed">Folders that couldn't be fully deleted, with why.</param>
    /// <returns>The folders deleted.</returns>
    public static IReadOnlyList<string> Purge(string quarantineRoot, DateOnly today, int retentionDays, out IReadOnlyList<string> failed)
    {
        var problems = new List<string>();
        failed = problems;
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        if (!Directory.Exists(quarantineRoot))
        {
            return [];
        }

        var expired = Expired(Directory.EnumerateDirectories(quarantineRoot).Select(Path.GetFileName).OfType<string>(), today, retentionDays);
        var deleted = new List<string>();
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
                DeleteMarkedFolder(path);
                deleted.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(path + ": " + ex.Message);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Deletes a dated quarantine folder, or one release in it, now ("Delete now" on the page), with the same care as the
    /// purge: only in a dated folder Ingest created and marked, never through a link (a link is removed, not followed),
    /// and read-only files made writable first. Deleting the whole folder removes its marker last.
    /// </summary>
    /// <param name="datedFolder">The dated folder (checked by the caller to be inside a quarantine folder in use).</param>
    /// <param name="release">An entry in it to delete, or <c>null</c> for the whole folder.</param>
    /// <exception cref="IOException">Something couldn't be deleted (what was deleted stays deleted).</exception>
    /// <exception cref="ArgumentException">The folder isn't Ingest's, or the release isn't a plain entry in it.</exception>
    public static void DeleteNow(string datedFolder, string? release)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datedFolder);
        if (!Directory.Exists(datedFolder) || new DirectoryInfo(datedFolder).LinkTarget is not null || !QuarantineMarkers.IsMarked(datedFolder)
            || QuarantineMarkers.DateOf(Path.GetFileName(Path.TrimEndingDirectorySeparator(datedFolder))) is null)
        {
            throw new ArgumentException("Only a dated quarantine folder Ingest created can be deleted.", nameof(datedFolder));
        }

        if (release is null)
        {
            DeleteMarkedFolder(datedFolder);
            return;
        }

        if (release is "." or ".." || release.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || string.Equals(release, QuarantineMarkers.DatedMarker, StringComparison.Ordinal))
        {
            throw new ArgumentException("That isn't a release in the dated folder.", nameof(release));
        }

        // Found on disk by name, never a path built from the name
        var entry = new DirectoryInfo(datedFolder).EnumerateFileSystemInfos().FirstOrDefault(e => string.Equals(e.Name, release, StringComparison.Ordinal))
            ?? throw new ArgumentException("That release isn't in quarantine any more.", nameof(release));
        DeleteEntry(entry);
    }

    // The contents first (read-only files made writable), the marker last, then the folder: an interruption leaves the
    // folder marked, so it is purged next time instead of being orphaned
    private static void DeleteMarkedFolder(string path)
    {
        var marker = Path.Combine(path, QuarantineMarkers.DatedMarker);
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (!string.Equals(entry.FullName, marker, StringComparison.Ordinal))
            {
                DeleteEntry(entry);
            }
        }

        File.Delete(marker);
        Directory.Delete(path);
    }

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
