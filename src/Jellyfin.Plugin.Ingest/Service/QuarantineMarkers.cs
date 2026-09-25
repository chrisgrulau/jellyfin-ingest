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

    /// <summary>
    /// Creates a dated folder (if needed) and marks it, and the quarantine folder, as Ingest's own. Done before anything
    /// is moved in, so a folder that holds quarantined files is always marked.
    /// </summary>
    /// <param name="quarantineRoot">The quarantine folder.</param>
    /// <param name="datedFolder">The dated folder inside it.</param>
    public static void Mark(string quarantineRoot, string datedFolder)
    {
        if (!PathGuard.IsUnder(datedFolder, quarantineRoot))
        {
            throw new ArgumentException("The dated folder must be inside the quarantine folder.", nameof(datedFolder));
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
                if (DateOnly.TryParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    found.Add((PathGuard.Normalise(root), Path.Combine(PathGuard.Normalise(root), first)));
                }
            }
        }

        return [.. found.OrderBy(f => f.Item2, StringComparer.Ordinal)];
    }

    private static void WriteIfMissing(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, MarkerText);
        }
    }
}
