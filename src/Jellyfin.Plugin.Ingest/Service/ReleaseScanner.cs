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

    /// <summary>Gets top-level entries the server's account can't read (reported, never moved).</summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];
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
        var unreadable = new List<string>();
        var quarantineRoot = PathGuard.Normalise(quarantine);

        foreach (var entry in new DirectoryInfo(watchFolder).EnumerateFileSystemInfos("*", TopLevel))
        {
            var name = entry.Name;
            if (ReleaseTracker.IsIgnored(name) || PathGuard.IsSameOrUnder(entry.FullName, quarantineRoot) || PathGuard.IsSameOrUnder(quarantineRoot, entry.FullName))
            {
                continue;
            }

            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                links.Add(name);
                continue;
            }

            if (!CanRead(entry))
            {
                unreadable.Add(name);
                continue;
            }

            try
            {
                releases[name] = entry is FileInfo file
                    ? [new ReleaseFile(name, file.Length, file.LastWriteTimeUtc)]
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

        return new ScanResult { Releases = releases, Links = links, Unsettled = unsettled, Unreadable = unreadable };
    }

    // A release the server's account can't open would otherwise look empty and be skipped without a word
    private static bool CanRead(FileSystemInfo entry)
    {
        try
        {
            if (entry is DirectoryInfo dir)
            {
                using var e = Directory.EnumerateFileSystemEntries(dir.FullName).GetEnumerator();
                e.MoveNext();
            }
            else
            {
                using var f = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // Locked or vanished: handled by the settle and scan logic as before
            return true;
        }
    }

    private static List<ReleaseFile> Files(string watchFolder, DirectoryInfo folder)
    {
        var files = new List<ReleaseFile>();
        foreach (var f in folder.EnumerateFiles("*", DeepWalk))
        {
            // Length is read from the enumeration; a file that vanished since raises FileNotFoundException here or later
            files.Add(new ReleaseFile(Path.GetRelativePath(watchFolder, f.FullName), f.Length, f.LastWriteTimeUtc));
        }

        return files;
    }
}
