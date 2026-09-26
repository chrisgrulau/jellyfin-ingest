using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.Ingest.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class WatchFolderConfigTests
{
    private static WatchFolder FromXml(string xml)
    {
        var serializer = new XmlSerializer(typeof(WatchFolder));
        using var reader = new StringReader(xml);
        return (WatchFolder)serializer.Deserialize(reader)!;
    }

    [Fact]
    public void A_folder_saved_before_dry_run_was_per_folder_has_no_setting_of_its_own()
    {
        var w = FromXml("<WatchFolder><Path>/in</Path><Enabled>true</Enabled></WatchFolder>");

        Assert.Null(w.DryRun);
        Assert.True(w.IsDryRun(true));
        Assert.False(w.IsDryRun(false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Migration_keeps_todays_behaviour_for_saved_folders(bool globalDryRun)
    {
        var old = new WatchFolder { Path = "/a" };
        var own = new WatchFolder { Path = "/b", DryRun = !globalDryRun };

        Assert.True(WatchFolder.MigrateDryRun([old, own], globalDryRun));

        Assert.Equal(globalDryRun, old.DryRun);
        Assert.Equal(!globalDryRun, own.DryRun); // a folder's own setting is never overwritten
        Assert.False(WatchFolder.MigrateDryRun([old, own], !globalDryRun)); // nothing left to migrate
        Assert.Equal(globalDryRun, old.DryRun);
    }

    [Fact]
    public void A_folders_own_dry_run_setting_wins_over_the_fallback()
    {
        Assert.False(new WatchFolder { DryRun = false }.IsDryRun(true));
        Assert.True(new WatchFolder { DryRun = true }.IsDryRun(false));
    }

    [Fact]
    public void Per_folder_dry_run_survives_the_xml_round_trip()
    {
        var serializer = new XmlSerializer(typeof(WatchFolder));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new WatchFolder { Path = "/in", DryRun = false });

        Assert.False(FromXml(writer.ToString()).DryRun);
    }
}
