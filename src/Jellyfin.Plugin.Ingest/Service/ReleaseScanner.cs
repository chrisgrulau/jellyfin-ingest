using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Ingest.Planning;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// What one look at a watch folder found.
/// </summary>
public sealed record ScanResult
{
    /// <summary>Gets each release (top-level file or folder) and its files, relative to the watch folder.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<ReleaseFile>> Releases { get; init; }

    /// <summary>Gets top-level entries that are symbolic links (or junctions); they are never ingested.</summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    /// <summary>Gets releases skipped this time because a file changed or vanished while it was being read.</summary>
    public IReadOnlyList<string> Unsettled { get; init; } = [];
}

/// <summary>
/// Lists the releases in a watch folder safely: symbolic links and junctions are never followed (so nothing outside the
/// watch folder can become part of a release, and link loops can't hang the sweep), unreadable entries are skipped,
/// and a file that disappears while being read (a download renamed from <c>.part</c>) only defers that release.
/// </summary>
public static class ReleaseScanner
{
    /// <summary>
    /// Enumeration used for everything inside a release: recursive, skipping unreadable entries and any symbolic link or
    /// junction (neither returned nor descended into). Hidden files are still listed so they are quarantined with the
    /// rest of the release.
    /// </summary>
    public static readonly EnumerationOptions DeepWalk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    /// <summary>
    /// Scans a watch folder.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="quarantine">The quarantine folder; it and everything under it are left out when it sits inside the watch folder.</param>
    /// <returns>The releases found, links refused and releases deferred.</returns>
    public static ScanResult Scan(string watchFolder, string quarantine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantine);

        var releases = new Dictionary<string, IReadOnlyList<ReleaseFile>>(StringComparer.Ordinal);
        var links = new List<string>();
        var unsettled = new List<string>();
        var quarantineRoot = Normalise(quarantine);

        foreach (var entry in new DirectoryInfo(watchFolder).EnumerateFileSystemInfos("*", TopLevel))
        {
            var name = entry.Name;
            if (ReleaseTracker.IsIgnored(name) || IsSameOrUnder(Normalise(entry.FullName), quarantineRoot) || IsSameOrUnder(quarantineRoot, Normalise(entry.FullName)))
            {
                continue;
            }

            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                links.Add(name);
                continue;
            }

            try
            {
                releases[name] = entry is FileInfo file
                    ? [new ReleaseFile(name, file.Length)]
                    : Files(watchFolder, (DirectoryInfo)entry);
            }
            catch (FileNotFoundException)
            {
                unsettled.Add(name);
            }
            catch (DirectoryNotFoundException)
            {
                unsettled.Add(name);
            }
        }

        return new ScanResult { Releases = releases, Links = links, Unsettled = unsettled };
    }

    /// <summary>
    /// Normalises a path for comparison: absolute, without a trailing separator.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The normalised path.</returns>
    public static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="root"/> or inside it (both already normalised).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="root">The folder.</param>
    /// <returns><c>true</c> if it is the same folder or below it.</returns>
    public static bool IsSameOrUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, cmp) || path.StartsWith(root + Path.DirectorySeparatorChar, cmp);
    }

    private static List<ReleaseFile> Files(string watchFolder, DirectoryInfo folder)
    {
        var files = new List<ReleaseFile>();
        foreach (var f in folder.EnumerateFiles("*", DeepWalk))
        {
            // Length is read from the enumeration; a file that vanished since raises FileNotFoundException here or later
            files.Add(new ReleaseFile(Path.GetRelativePath(watchFolder, f.FullName), f.Length));
        }

        return files;
    }
}
