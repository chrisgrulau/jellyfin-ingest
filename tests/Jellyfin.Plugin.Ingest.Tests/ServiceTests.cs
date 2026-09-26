using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Quarantine;
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

    [Fact]
    public void An_offline_library_is_retried_until_it_is_back_at_most_hourly()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), RetrySchedule.RetryDelay(RetryKind.FolderUnavailable, 1));
        Assert.Equal(TimeSpan.FromMinutes(10), RetrySchedule.RetryDelay(RetryKind.FolderUnavailable, 2));
        Assert.Equal(TimeSpan.FromMinutes(60), RetrySchedule.RetryDelay(RetryKind.FolderUnavailable, 5));
        Assert.Equal(TimeSpan.FromMinutes(60), RetrySchedule.RetryDelay(RetryKind.FolderUnavailable, 500));
    }

    [Fact]
    public void Nothing_found_is_retried_three_times_then_left_for_a_person()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), RetrySchedule.RetryDelay(RetryKind.NothingFound, 1));
        Assert.Equal(TimeSpan.FromHours(1), RetrySchedule.RetryDelay(RetryKind.NothingFound, 2));
        Assert.Equal(TimeSpan.FromHours(6), RetrySchedule.RetryDelay(RetryKind.NothingFound, 3));
        Assert.Null(RetrySchedule.RetryDelay(RetryKind.NothingFound, 4));
        Assert.Null(RetrySchedule.RetryDelay(RetryKind.None, 1));
    }

    [Fact]
    public void Only_the_folders_filed_into_are_refreshed()
    {
        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations =
            [
                new PlannedOperation(OperationKind.Video, "/drop/r/a.mkv", "/lib/Shows/Lantern (2001)/Season 01/a.mkv"),
                new PlannedOperation(OperationKind.Subtitle, "/drop/r/a.srt", "/lib/Shows/Lantern (2001)/Season 01/a.en.srt"),
                new PlannedOperation(OperationKind.Quarantine, "/drop/r/x.nfo", "/drop/.ingest-quarantine/2026-09-25/r/x.nfo"),
            ],
            AllowedRoots = ["/lib/Shows/Lantern (2001)", "/drop/.ingest-quarantine/2026-09-25"],
        };

        Assert.Equal(["/lib/Shows/Lantern (2001)"], IngestService.FiledFolders(plan));
    }

    private static string Line(DateTimeOffset time, string release = "r")
        => $$"""{"time":"{{time:O}}","release":"{{release}}","kind":"Video","source":"/drop/a.mkv","destination":"/lib/a.mkv","bytes":1,"phase":"done"}""";

    [Fact]
    public void The_action_log_keeps_ninety_days()
    {
        var now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        string[] lines = [Line(now.AddDays(-200)), "not json", Line(now.AddDays(-91)), Line(now.AddDays(-89)), Line(now)];

        var keep = ActionLog.Trim(lines, now, ActionLog.MaxAge, ActionLog.MaxBytes);

        Assert.Equal([lines[3], lines[4]], keep);
    }

    [Fact]
    public void The_action_log_keeps_the_newest_that_fit()
    {
        var now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        var lines = Enumerable.Range(0, 10).Select(i => Line(now.AddMinutes(i), "r" + i)).ToList();
        var budget = lines.Skip(7).Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + 1);

        var keep = ActionLog.Trim(lines, now.AddMinutes(10), ActionLog.MaxAge, budget);

        Assert.Equal(lines.Skip(7), keep);
    }

    [Fact]
    public void Trimming_the_action_log_file_rewrites_it_only_when_needed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ingest-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "actions.jsonl");
            var now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
            File.WriteAllLines(path, [Line(now.AddDays(-120)), Line(now)]);

            Assert.Equal(1, ActionLog.TrimFile(path, now));
            Assert.Equal([Line(now)], File.ReadAllLines(path));
            Assert.Equal(0, ActionLog.TrimFile(path, now));
            Assert.Equal(0, ActionLog.TrimFile(Path.Combine(dir, "missing.jsonl"), now));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
