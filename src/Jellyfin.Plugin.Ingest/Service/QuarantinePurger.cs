using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Ingest.Service;

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
    {
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

            Directory.Delete(path, recursive: true);
            deleted.Add(path);
        }

        return deleted;
    }
}
