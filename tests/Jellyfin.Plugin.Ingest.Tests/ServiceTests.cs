using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class ServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    private static Dictionary<string, IReadOnlyList<ReleaseFile>> Snap(params (string Release, string Rel, long Size)[] files)
        => files.GroupBy(f => f.Release).ToDictionary(g => g.Key, g => (IReadOnlyList<ReleaseFile>)[.. g.Select(f => new ReleaseFile(f.Rel, f.Size))]);

    [Fact]
    public void A_release_is_ready_only_after_it_stops_changing()
    {
        var t = new ReleaseTracker();

        Assert.Empty(t.Observe(Snap(("r", "r/a.mkv", 100)), T0, Settle));
        Assert.Empty(t.Observe(Snap(("r", "r/a.mkv", 200)), T0.AddSeconds(90), Settle)); // still growing
        Assert.Empty(t.Observe(Snap(("r", "r/a.mkv", 200)), T0.AddSeconds(120), Settle)); // 30 s stable
        Assert.Equal(["r"], t.Observe(Snap(("r", "r/a.mkv", 200)), T0.AddSeconds(151), Settle));
    }

    [Fact]
    public void A_handled_release_is_not_offered_again_until_it_changes()
    {
        var t = new ReleaseTracker();
        t.Observe(Snap(("r", "r/a.mkv", 1)), T0, Settle);
        Assert.Single(t.Observe(Snap(("r", "r/a.mkv", 1)), T0.AddSeconds(61), Settle));

        t.MarkHandled("r");
        Assert.Empty(t.Observe(Snap(("r", "r/a.mkv", 1)), T0.AddSeconds(200), Settle));

        t.Observe(Snap(("r", "r/a.mkv", 1), ("r", "r/a.srt", 5)), T0.AddSeconds(210), Settle); // someone added subtitles
        Assert.Single(t.Observe(Snap(("r", "r/a.mkv", 1), ("r", "r/a.srt", 5)), T0.AddSeconds(271), Settle));
    }

    [Fact]
    public void Releases_with_partial_downloads_are_never_ready()
    {
        var t = new ReleaseTracker();
        t.Observe(Snap(("r", "r/a.mkv.part", 1)), T0, Settle);
        Assert.Empty(t.Observe(Snap(("r", "r/a.mkv.part", 1)), T0.AddHours(1), Settle));
    }

    [Theory]
    [InlineData(".ingest-quarantine", true)]
    [InlineData(".DS_Store", true)]
    [InlineData("movie.mkv.crdownload", true)]
    [InlineData("Show.S01E01.mkv", false)]
    public void Ignores_hidden_and_partial_entries(string name, bool ignored)
    {
        Assert.Equal(ignored, ReleaseTracker.IsIgnored(name));
    }

    [Fact]
    public void Purge_only_touches_expired_dated_folders()
    {
        var expired = QuarantinePurger.Expired(["2026-08-01", "2026-08-25", "2026-08-26", "2026-09-24", "keep-me", "2026-13-40"], new DateOnly(2026, 9, 24), 30);

        // Exactly 30 days old (2026-08-25) is kept; strictly older is purged.
        Assert.Equal(["2026-08-01"], expired);
    }

    [Fact]
    public void Purge_deletes_on_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "ingest-purge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "2020-01-01", "release"));
            File.WriteAllText(Path.Combine(root, "2020-01-01", "release", "readme.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "not-a-date"));

            var deleted = QuarantinePurger.Purge(root, new DateOnly(2026, 9, 24), 30);

            Assert.Single(deleted);
            Assert.False(Directory.Exists(Path.Combine(root, "2020-01-01")));
            Assert.True(Directory.Exists(Path.Combine(root, "not-a-date")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Forgetting_everything_offers_handled_releases_again()
    {
        var t = new ReleaseTracker();
        var snap = Snap(("r", "r/a.mkv", 100));
        t.Observe(snap, T0, Settle);
        Assert.Equal(["r"], t.Observe(snap, T0.AddSeconds(61), Settle));
        t.MarkHandled("r");
        Assert.Empty(t.Observe(snap, T0.AddSeconds(90), Settle));

        t.ForgetAll();

        Assert.Equal(["r"], t.Observe(snap, T0.AddSeconds(120), Settle));
    }
}
