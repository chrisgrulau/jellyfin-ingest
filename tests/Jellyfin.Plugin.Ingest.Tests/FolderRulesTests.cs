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
