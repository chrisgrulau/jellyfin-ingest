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
            QuarantineMarkers.Mark(root, Path.Combine(root, "2020-01-01"));
            Directory.CreateDirectory(Path.Combine(root, "2020-01-01", "release"));
            File.WriteAllText(Path.Combine(root, "2020-01-01", "release", "readme.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "not-a-date"));

            // A date-named folder Ingest didn't create (e.g. photo imports) is never deleted, however old
            Directory.CreateDirectory(Path.Combine(root, "2019-05-05"));
            File.WriteAllText(Path.Combine(root, "2019-05-05", "holiday.jpg"), "x");

            // A marked but recent folder is kept
            QuarantineMarkers.Mark(root, Path.Combine(root, "2026-09-20"));

            var deleted = QuarantinePurger.Purge(root, new DateOnly(2026, 9, 24), 30);

            Assert.Equal([Path.Combine(root, "2020-01-01")], deleted);
            Assert.True(Directory.Exists(Path.Combine(root, "not-a-date")));
            Assert.True(File.Exists(Path.Combine(root, "2019-05-05", "holiday.jpg")));
            Assert.True(Directory.Exists(Path.Combine(root, "2026-09-20")));
            Assert.True(File.Exists(Path.Combine(root, QuarantineMarkers.RootMarker)));
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

    [Theory]
    [InlineData("lost+found")]
    [InlineData(".Trash-1000")]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("System Volume Information")]
    [InlineData("@eaDir")]
    [InlineData("#recycle")]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData(".DS_Store")]
    [InlineData("Show.S01E01.mkv.part")]
    public void System_and_partial_entries_are_ignored(string name) => Assert.True(ReleaseTracker.IsIgnored(name));

    [Theory]
    [InlineData("Show.S01E01.mkv")]
    [InlineData("Movie Title (2019)")]
    [InlineData("Found Footage (2019)")]
    public void Releases_are_not_ignored(string name) => Assert.False(ReleaseTracker.IsIgnored(name));

    [Fact]
    public void Existing_quarantine_folders_are_recognised_from_the_action_log()
    {
        string[] log =
        [
            "{\"kind\":\"Video\",\"destination\":\"/lib/Shows/x.mkv\"}",
            "{\"kind\":\"Quarantine\",\"destination\":\"/drop/.ingest-quarantine/2026-09-24/Show/readme.txt\"}",
            "{\"kind\":\"Quarantine\",\"destination\":\"/drop/.ingest-quarantine/2026-09-24/Other/x.nfo\"}",
            "{\"kind\":\"Quarantine\",\"destination\":\"/drop/.ingest-quarantine/not-dated/x.nfo\"}",
            "{\"kind\":\"Quarantine\",\"destination\":\"/elsewhere/2026-01-01/x.nfo\"}",
            "not json",
        ];

        var found = QuarantineMarkers.FromActionLog(log, ["/drop/.ingest-quarantine/"]);

        Assert.Equal([("/drop/.ingest-quarantine", "/drop/.ingest-quarantine/2026-09-24")], found);
    }

    [Fact]
    public void A_marker_is_only_written_inside_the_quarantine()
        => Assert.Throws<ArgumentException>(() => QuarantineMarkers.Mark("/tmp/q", "/tmp/other/2026-09-24"));

    [Fact]
    public void A_release_written_at_full_size_is_not_ready_while_writes_continue()
    {
        var t = new ReleaseTracker();
        Dictionary<string, IReadOnlyList<ReleaseFile>> At(int minute) => new() { ["r"] = [new ReleaseFile("r/a.mkv", 5_000_000_000, new DateTime(2026, 9, 25, 10, minute, 0, DateTimeKind.Utc))] };

        // Size never changes (pre-allocated), but the file keeps being written
        for (var m = 0; m < 10; m++)
        {
            Assert.Empty(t.Observe(At(m), T0.AddMinutes(m), Settle));
        }

        // Writing stops: ready once nothing has changed for the settle time
        Assert.Empty(t.Observe(At(10), T0.AddMinutes(10), Settle));
        Assert.Equal(["r"], t.Observe(At(10), T0.AddMinutes(10).AddSeconds(61), Settle));
    }

    [Fact]
    public void File_times_are_compared_with_themselves_not_the_server_clock()
    {
        // A share whose clock runs an hour ahead: the file looks "written in the future", which doesn't matter
        var t = new ReleaseTracker();
        var snap = new Dictionary<string, IReadOnlyList<ReleaseFile>> { ["r"] = [new ReleaseFile("r/a.mkv", 100, T0.UtcDateTime.AddHours(1))] };
        Assert.Empty(t.Observe(snap, T0, Settle));
        Assert.Equal(["r"], t.Observe(snap, T0.AddSeconds(61), Settle));
    }
}
