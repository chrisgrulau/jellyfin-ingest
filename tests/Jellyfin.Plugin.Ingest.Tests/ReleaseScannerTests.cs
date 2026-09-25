using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Real temporary folders and symbolic links (the CI runner is Linux).
public sealed class ReleaseScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ingest-scan-" + Guid.NewGuid().ToString("N"));

    public ReleaseScannerTests()
    {
        Directory.CreateDirectory(Watch);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "precious.mkv"), "not yours");
        File.WriteAllText(Path.Combine(Outside, "precious.nfo"), "not yours");
    }

    private string Watch => Path.Combine(_root, "watch");

    private string Outside => Path.Combine(_root, "library", "Movies");

    public void Dispose()
    {
        foreach (var d in OperatingSystem.IsWindows() ? [] : Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (OperatingSystem.IsWindows())
            {
                continue;
            }

            try
            {
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, string content = "x")
    {
        var path = Path.Combine(Watch, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static IEnumerable<string> Names(ScanResult r, string release) => r.Releases[release].Select(f => f.RelativePath);

    [Fact]
    public void A_link_inside_a_release_is_neither_listed_nor_followed()
    {
        Write("Show.S01/Show.S01E01.mkv");
        Directory.CreateSymbolicLink(Path.Combine(Watch, "Show.S01", "extras"), Outside);
        File.CreateSymbolicLink(Path.Combine(Watch, "Show.S01", "bonus.mkv"), Path.Combine(Outside, "precious.mkv"));

        var scan = ReleaseScanner.Scan(Watch, Path.Combine(Watch, ".ingest-quarantine"));

        Assert.Equal(["Show.S01/Show.S01E01.mkv"], Names(scan, "Show.S01"));
    }

    [Fact]
    public void A_link_at_the_top_of_the_watch_folder_is_reported_not_ingested()
    {
        Write("Film.2019/Film.2019.mkv");
        Directory.CreateSymbolicLink(Path.Combine(Watch, "old-downloads"), Outside);

        var scan = ReleaseScanner.Scan(Watch, Path.Combine(Watch, ".ingest-quarantine"));

        Assert.Equal(["old-downloads"], scan.Links);
        Assert.Equal(["Film.2019"], scan.Releases.Keys);
    }

    [Fact]
    public void A_link_loop_does_not_hang_or_multiply_files()
    {
        Write("Loop/a.mkv");
        Directory.CreateSymbolicLink(Path.Combine(Watch, "Loop", "again"), Path.Combine(Watch, "Loop"));

        var scan = ReleaseScanner.Scan(Watch, Path.Combine(Watch, ".ingest-quarantine"));

        Assert.Equal(["Loop/a.mkv"], Names(scan, "Loop"));
    }

    [Fact]
    public void An_unreadable_sub_folder_does_not_stop_the_scan()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // root reads everything; Windows has no Unix modes
        }

        Write("Film.2019/Film.2019.mkv");
        Write("Film.2019/locked/secret.txt");
        Write("Other/Other.mkv");
        File.SetUnixFileMode(Path.Combine(Watch, "Film.2019", "locked"), UnixFileMode.None);

        var scan = ReleaseScanner.Scan(Watch, Path.Combine(Watch, ".ingest-quarantine"));

        Assert.Equal(["Film.2019/Film.2019.mkv"], Names(scan, "Film.2019"));
        Assert.Contains("Other", scan.Releases.Keys);
    }

    [Fact]
    public void The_quarantine_folder_is_skipped_even_with_a_trailing_slash()
    {
        Write("q/2026-09-24/Old/readme.txt");
        Write("New/New.mkv");

        var scan = ReleaseScanner.Scan(Watch, Path.Combine(Watch, "q") + Path.DirectorySeparatorChar);

        Assert.Equal(["New"], scan.Releases.Keys);
    }

    [Fact]
    public void Tidying_up_never_deletes_through_a_link()
    {
        Directory.CreateDirectory(Path.Combine(Outside, "empty-but-yours"));
        Directory.CreateDirectory(Path.Combine(Watch, "Done", "Subs"));
        Directory.CreateSymbolicLink(Path.Combine(Watch, "Done", "link"), Outside);

        new PhysicalFileOperations().DeleteEmptyDirectories(Path.Combine(Watch, "Done"));

        Assert.True(Directory.Exists(Path.Combine(Outside, "empty-but-yours")));
        Assert.False(Directory.Exists(Path.Combine(Watch, "Done", "Subs")));
    }

    [Fact]
    public void Paths_compare_by_folder_not_by_prefix()
    {
        Assert.True(PathGuard.IsSameOrUnder("/in/q/x", "/in/q"));
        Assert.True(PathGuard.IsSameOrUnder("/in/q", "/in/q"));
        Assert.False(PathGuard.IsSameOrUnder("/in/queue", "/in/q"));
        Assert.Equal("/in/q", PathGuard.Normalise("/in/q/"));
    }
}
