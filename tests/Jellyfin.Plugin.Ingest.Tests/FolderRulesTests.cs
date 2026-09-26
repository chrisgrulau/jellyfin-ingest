using System;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class FolderRulesTests
{
    private static readonly string[] Libraries = ["/media/Movies", "/media/Shows"];
    private static readonly string[] Jellyfin = ["/var/lib/jellyfin", "/etc/jellyfin", "/var/cache/jellyfin", "/usr/lib/jellyfin"];

    private static string[] Problems(string[] watch, string? quarantine = null)
        => [.. FolderRules.Check(watch, quarantine, Libraries, Jellyfin).Select(p => p.Folder + ": " + p.Problem)];

    [Fact]
    public void A_separate_drop_folder_is_fine()
        => Assert.Empty(Problems(["/media/incoming", "/downloads/done"], "/media/incoming/.q"));

    [Theory]
    [InlineData("/")]
    [InlineData("relative/path")]
    [InlineData("/media")]
    [InlineData("/media/Movies")]
    [InlineData("/media/Movies/incoming")]
    [InlineData("/var/lib/jellyfin/plugins/x")]
    [InlineData("/var")]
    public void Unsafe_watch_folders_are_refused(string watch)
        => Assert.Single(Problems([watch]));

    [Fact]
    public void Watch_folders_may_not_overlap_each_other()
    {
        var p = Problems(["/in", "/in/sub", "/in2"]);
        Assert.Equal(2, p.Length);
        Assert.All(p, x => Assert.Contains("another watch folder", x, System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_trailing_slash_does_not_hide_an_overlap()
        => Assert.Single(Problems(["/media/Movies/"]));

    [Theory]
    [InlineData("/media/Movies/.quarantine")]
    [InlineData("/media")]
    [InlineData("/in")]
    [InlineData("/")]
    [InlineData("/var/cache/jellyfin/q")]
    public void Unsafe_quarantine_folders_are_refused(string quarantine)
        => Assert.Contains(Problems(["/in"], quarantine), x => x.StartsWith(quarantine + ":", System.StringComparison.Ordinal));

    [Fact]
    public void A_quarantine_inside_a_watch_folder_or_elsewhere_is_fine()
    {
        Assert.Empty(Problems(["/in"], "/in/.q"));
        Assert.Empty(Problems(["/in"], "/srv/quarantine"));
    }
}

// Review pass 2: ING-19 (roots) and ING-24 (links, unreadable releases)
public sealed class RootsAndLinksTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ingest-roots-" + System.Guid.NewGuid().ToString("N"));

    public RootsAndLinksTests() => System.IO.Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in System.IO.Directory.EnumerateDirectories(_root, "*", System.IO.SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
            {
                System.IO.File.SetUnixFileMode(d, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute);
            }
        }

        System.IO.Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_file_system_root_contains_everything_on_it()
    {
        var root = System.IO.Path.GetPathRoot(_root)!;
        Assert.True(Jellyfin.Plugin.Ingest.Planning.PathGuard.IsUnder(System.IO.Path.Combine(root, "Films", "X"), root));
        Assert.True(Jellyfin.Plugin.Ingest.Planning.PathGuard.IsSameOrUnder(root, root));
        Assert.False(Jellyfin.Plugin.Ingest.Planning.PathGuard.IsUnder(root, root));
    }

    [Fact]
    public void A_watch_folder_inside_a_whole_drive_library_is_flagged()
    {
        var root = System.IO.Path.GetPathRoot(_root)!;
        var problems = Jellyfin.Plugin.Ingest.Planning.FolderRules.Check([System.IO.Path.Combine(_root, "in")], null, [root], []);
        Assert.Contains(problems, p => p.Problem.Contains("library folder", StringComparison.Ordinal));
    }

    [Fact]
    public void A_watch_folder_that_reaches_a_library_through_a_link_is_flagged()
    {
        var library = System.IO.Path.Combine(_root, "library");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(library, "in"));
        var link = System.IO.Path.Combine(_root, "shortcut");
        System.IO.Directory.CreateSymbolicLink(link, library);

        var problems = Jellyfin.Plugin.Ingest.Planning.FolderRules.Check([System.IO.Path.Combine(link, "in")], null, [library], []);

        Assert.Contains(problems, p => p.Problem.Contains("library folder", StringComparison.Ordinal));
    }

    [Fact]
    public void A_release_the_account_cannot_read_is_reported_not_skipped()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var watch = System.IO.Path.Combine(_root, "watch");
        var locked = System.IO.Path.Combine(watch, "Locked.Release.2024");
        System.IO.Directory.CreateDirectory(locked);
        System.IO.File.WriteAllText(System.IO.Path.Combine(locked, "a.mkv"), "x");
        System.IO.File.SetUnixFileMode(locked, System.IO.UnixFileMode.None);

        var scan = Jellyfin.Plugin.Ingest.Service.ReleaseScanner.Scan(watch, System.IO.Path.Combine(watch, ".ingest-quarantine"));

        Assert.Equal(["Locked.Release.2024"], scan.Unreadable);
        Assert.False(scan.Releases.ContainsKey("Locked.Release.2024"));
    }
}
