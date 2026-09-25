using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Lists what is in a quarantine folder, for the plugin page: the dated folders, the releases in each and their files.
/// Read-only; links are listed but never followed, and the file list is capped so a huge folder can't stall the page.
/// </summary>
public static class QuarantineListing
{
    /// <summary>The most files listed per quarantine folder; the rest are only counted.</summary>
    public const int MaxFiles = 2000;

    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// Lists the dated folders (<c>yyyy-MM-dd</c>) directly inside a quarantine folder, newest first.
    /// </summary>
    /// <param name="quarantineRoot">Absolute quarantine folder.</param>
    /// <param name="today">Today's date.</param>
    /// <param name="retentionDays">Days quarantined files are kept.</param>
    /// <param name="maxFiles">The most files to list; the rest are only counted.</param>
    /// <returns>The dated folders.</returns>
    public static IReadOnlyList<QuarantineFolder> List(string quarantineRoot, DateOnly today, int retentionDays, int maxFiles = MaxFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(retentionDays);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFiles);
        if (!Directory.Exists(quarantineRoot))
        {
            return [];
        }

        var listed = 0;
        var folders = new List<QuarantineFolder>();
        var dated = Directory.EnumerateDirectories(quarantineRoot)
            .Select(p => (Path: p, Name: Path.GetFileName(p)))
            .Where(d => DateOnly.TryParseExact(d.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .Where(d => new DirectoryInfo(d.Path).LinkTarget is null)
            .OrderByDescending(d => d.Name, StringComparer.Ordinal);
        foreach (var (path, name) in dated)
        {
            var date = DateOnly.ParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var managed = QuarantineMarkers.IsMarked(path);
            var releases = new Dictionary<string, List<QuarantinedFile>>(StringComparer.Ordinal);
            var counts = new Dictionary<string, (int Files, long Bytes)>(StringComparer.Ordinal);
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", AllFiles))
            {
                var rel = Path.GetRelativePath(path, file.FullName);
                if (string.Equals(rel, QuarantineMarkers.DatedMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                // The first folder is the release; a file directly in the dated folder is its own entry
                var parts = rel.Split(Path.DirectorySeparatorChar, 2);
                var release = parts[0];
                var inRelease = parts.Length > 1 ? parts[1] : parts[0];
                long bytes;
                try
                {
                    bytes = file.Length;
                }
                catch (IOException)
                {
                    bytes = 0;
                }

                counts.TryGetValue(release, out var c);
                counts[release] = (c.Files + 1, c.Bytes + bytes);
                if (listed < maxFiles)
                {
                    if (!releases.TryGetValue(release, out var files))
                    {
                        releases[release] = files = [];
                    }

                    files.Add(new QuarantinedFile(inRelease, bytes));
                    listed++;
                }
            }

            folders.Add(new QuarantineFolder
            {
                Root = quarantineRoot,
                Date = name,
                Managed = managed,
                DeletesOn = managed ? date.AddDays(retentionDays + 1) : null,
                Releases = [.. counts.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => new QuarantinedRelease
                {
                    Name = r.Key,
                    FileCount = r.Value.Files,
                    Bytes = r.Value.Bytes,
                    Files = releases.TryGetValue(r.Key, out var f) ? [.. f.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)] : [],
                })],
            });
        }

        return folders;
    }
}

/// <summary>
/// A dated folder in a quarantine.
/// </summary>
public sealed record QuarantineFolder
{
    /// <summary>Gets the quarantine folder it is in.</summary>
    public required string Root { get; init; }

    /// <summary>Gets the date the files were quarantined (<c>yyyy-MM-dd</c>, the folder's name).</summary>
    public required string Date { get; init; }

    /// <summary>Gets a value indicating whether Ingest created it (only those are ever deleted).</summary>
    public bool Managed { get; init; }

    /// <summary>Gets the first day the purge deletes it, or <c>null</c> if it never will.</summary>
    public DateOnly? DeletesOn { get; init; }

    /// <summary>Gets the releases in it.</summary>
    public IReadOnlyList<QuarantinedRelease> Releases { get; init; } = [];

    /// <summary>Gets how many files it holds.</summary>
    public int FileCount => Releases.Sum(r => r.FileCount);

    /// <summary>Gets its total size in bytes.</summary>
    public long Bytes => Releases.Sum(r => r.Bytes);
}

/// <summary>
/// A release's quarantined files.
/// </summary>
public sealed record QuarantinedRelease
{
    /// <summary>Gets the release name (or a single file's name).</summary>
    public required string Name { get; init; }

    /// <summary>Gets how many files it holds.</summary>
    public int FileCount { get; init; }

    /// <summary>Gets its total size in bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets the files listed (all of them unless the listing was capped).</summary>
    public IReadOnlyList<QuarantinedFile> Files { get; init; } = [];
}

/// <summary>
/// A quarantined file.
/// </summary>
/// <param name="Path">Path inside the release.</param>
/// <param name="Bytes">Size in bytes.</param>
public sealed record QuarantinedFile(string Path, long Bytes);
