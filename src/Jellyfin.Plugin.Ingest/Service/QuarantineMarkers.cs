using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Ingest.Planning;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Marks the dated quarantine folders Ingest creates, so the purge only ever deletes folders Ingest made itself, never a
/// date-named folder that happens to be in a folder chosen as the quarantine.
/// </summary>
public static class QuarantineMarkers
{
    /// <summary>Marker file in each dated folder Ingest created.</summary>
    public const string DatedMarker = ".ingest-created";

    /// <summary>Marker file in the quarantine folder itself (informational, for anyone looking at the folder).</summary>
    public const string RootMarker = ".ingest-quarantine-root";

    private const string MarkerText = "Created by the Jellyfin Ingest plugin. Folders marked like this are deleted after the quarantine retention period.\n";

    /// <summary>The suffix of a dated folder Ingest created beside a same-dated folder that isn't its own.</summary>
    private const string OwnSuffix = " (Ingest";

    /// <summary>
    /// The dated folder to quarantine into on a day: <c>yyyy-MM-dd</c>, unless a folder of that name exists that Ingest
    /// didn't create (a custom quarantine can hold date-named folders from other tools), in which case
    /// <c>yyyy-MM-dd (Ingest)</c>, <c>yyyy-MM-dd (Ingest 2)</c> … so a folder that isn't Ingest's is never used or purged.
    /// </summary>
    /// <param name="quarantineRoot">The quarantine folder.</param>
    /// <param name="day">The day.</param>
    /// <returns>The dated folder's path.</returns>
    public static string DatedFolderFor(string quarantineRoot, DateOnly day)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        var name = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(quarantineRoot, n == 1 ? name : n == 2 ? name + OwnSuffix + ")" : string.Create(CultureInfo.InvariantCulture, $"{name}{OwnSuffix} {n - 1})"));
            if (!Directory.Exists(candidate) || IsMarked(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The date of a dated folder's name (<c>yyyy-MM-dd</c>, optionally followed by Ingest's own suffix).
    /// </summary>
    /// <param name="name">The folder name.</param>
    /// <returns>The date, or <c>null</c> if the name isn't a dated folder's.</returns>
    public static DateOnly? DateOf(string? name)
    {
        if (name is null || name.Length < 10
            || !DateOnly.TryParseExact(name.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return null;
        }

        var rest = name[10..];
        return rest.Length == 0 || (rest.StartsWith(OwnSuffix, StringComparison.Ordinal) && rest.EndsWith(')')
            && (rest.Length == OwnSuffix.Length + 1 || int.TryParse(rest.AsSpan(OwnSuffix.Length + 1, rest.Length - OwnSuffix.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            ? d
            : null;
    }

    /// <summary>
    /// Creates a dated folder and marks it, and the quarantine folder, as Ingest's own. Done before anything is moved in,
    /// so a folder that holds quarantined files is always marked. A folder that already exists without the marker isn't
    /// Ingest's and is refused (see <see cref="DatedFolderFor"/>).
    /// </summary>
    /// <param name="quarantineRoot">The quarantine folder.</param>
    /// <param name="datedFolder">The dated folder inside it.</param>
    /// <exception cref="IOException">The folder exists and isn't Ingest's.</exception>
    public static void Mark(string quarantineRoot, string datedFolder)
    {
        if (!PathGuard.IsUnder(datedFolder, quarantineRoot))
        {
            throw new ArgumentException("The dated folder must be inside the quarantine folder.", nameof(datedFolder));
        }

        if (Directory.Exists(datedFolder) && !IsMarked(datedFolder))
        {
            throw new IOException("Refused: " + datedFolder + " wasn't created by Ingest, so nothing is quarantined into it.");
        }

        Directory.CreateDirectory(datedFolder);
        WriteIfMissing(Path.Combine(quarantineRoot, RootMarker));
        WriteIfMissing(Path.Combine(datedFolder, DatedMarker));
    }

    /// <summary>
    /// Whether a dated folder was created (and marked) by Ingest.
    /// </summary>
    /// <param name="datedFolder">The folder.</param>
    /// <returns><c>true</c> if it carries the marker.</returns>
    public static bool IsMarked(string datedFolder) => File.Exists(Path.Combine(datedFolder, DatedMarker));

    /// <summary>
    /// Finds the dated folders that Ingest's action log shows it quarantined files into, for marking folders created
    /// before markers existed. Only folders named <c>yyyy-MM-dd</c> directly inside a quarantine folder count.
    /// </summary>
    /// <param name="actionLogLines">Lines of <c>actions.jsonl</c>.</param>
    /// <param name="quarantineRoots">The quarantine folders in use.</param>
    /// <returns>(quarantine folder, dated folder) pairs to mark.</returns>
    public static IReadOnlyList<(string Root, string Dated)> FromActionLog(IEnumerable<string> actionLogLines, IReadOnlyCollection<string> quarantineRoots)
    {
        ArgumentNullException.ThrowIfNull(actionLogLines);
        ArgumentNullException.ThrowIfNull(quarantineRoots);

        var found = new HashSet<(string, string)>();
        foreach (var line in actionLogLines)
        {
            string? kind, destination;
            try
            {
                using var doc = JsonDocument.Parse(line);
                kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
                destination = doc.RootElement.TryGetProperty("destination", out var d) ? d.GetString() : null;
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.Equals(kind, "Quarantine", StringComparison.Ordinal) || string.IsNullOrEmpty(destination))
            {
                continue;
            }

            foreach (var root in quarantineRoots)
            {
                if (!PathGuard.IsUnder(destination, root))
                {
                    continue;
                }

                var first = Path.GetRelativePath(PathGuard.Normalise(root), PathGuard.Normalise(destination)).Split(Path.DirectorySeparatorChar)[0];
                if (DateOf(first) is not null)
                {
                    found.Add((PathGuard.Normalise(root), Path.Combine(PathGuard.Normalise(root), first)));
                }
            }
        }

        return [.. found.OrderBy(f => f.Item2, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether every file in an unmarked dated folder is one the action log says Ingest quarantined there, so marking it
    /// (for folders from before markers existed) can never let the purge delete someone else's files.
    /// </summary>
    /// <param name="datedFolder">The folder.</param>
    /// <param name="quarantinedFiles">Every quarantine destination in the action log.</param>
    /// <returns><c>true</c> if nothing in it is unaccounted for.</returns>
    public static bool HoldsOnlyLoggedFiles(string datedFolder, IReadOnlySet<string> quarantinedFiles)
    {
        ArgumentNullException.ThrowIfNull(quarantinedFiles);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
        return Directory.EnumerateFiles(datedFolder, "*", options).All(f => quarantinedFiles.Contains(PathGuard.Normalise(f)));
    }

    /// <summary>
    /// Every quarantine destination in the action log.
    /// </summary>
    /// <param name="actionLogLines">Lines of <c>actions.jsonl</c>.</param>
    /// <returns>The destinations, normalised.</returns>
    public static IReadOnlySet<string> QuarantinedFiles(IEnumerable<string> actionLogLines)
    {
        ArgumentNullException.ThrowIfNull(actionLogLines);
        var files = new HashSet<string>(PathGuard.Comparer);
        foreach (var line in actionLogLines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("kind", out var k) && k.GetString() == "Quarantine"
                    && doc.RootElement.TryGetProperty("destination", out var d) && d.GetString() is { Length: > 0 } destination)
                {
                    files.Add(PathGuard.Normalise(destination));
                }
            }
            catch (JsonException)
            {
                // Unreadable lines account for nothing
            }
        }

        return files;
    }

    private static void WriteIfMissing(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, MarkerText);
        }
    }
}
