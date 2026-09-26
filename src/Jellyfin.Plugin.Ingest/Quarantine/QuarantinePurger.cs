using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

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

    // The contents first (read-only files made writable), the marker last, then the folder: an interruption leaves the
    // folder marked, so it is purged next time instead of being orphaned
    private static void DeleteMarkedFolder(string path)
    {
        var marker = Path.Combine(path, QuarantineMarkers.DatedMarker);
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.FullName, marker, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry is DirectoryInfo dir && dir.LinkTarget is null)
            {
                foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
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

        File.Delete(marker);
        Directory.Delete(path);
    }
}
