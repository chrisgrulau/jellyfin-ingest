using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Presentation;
using Jellyfin.Plugin.Ingest.Quarantine;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented titles and paths throughout.
public sealed class PresenterTests
{
    private const string Shows = "/media/shows";
    private const string Films = "/media/films";
    private const string Watch = "/downloads/incoming";

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly IngestPresenter Presenter = new([
        new MediaLibrary("s", "Shows", LibraryKind.Shows, [Shows]),
        new MediaLibrary("f", "Movies", LibraryKind.Films, [Films]),
    ]);

    private static string Episode(int season, int episode, string title)
        => $"{Shows}/Lantern (2001) [tvdbid-100001]/Season {season:00}/Lantern S{season:00}E{episode:00} - {title}.mkv";

    [Fact]
    public void A_filed_episode_reads_as_show_and_code_with_its_title_and_library()
    {
        var entry = new ActivityEntry
        {
            Time = Now.AddHours(-3),
            Status = ActivityStatus.Filed,
            Release = "Lantern.S01E05.1080p-GRP",
            WatchFolder = Watch,
            Summary = "Filed 1 video and 2 subtitles; quarantined 3 files.",
            Run = "run1",
            Counts = new ActivityCounts { Videos = 1, Subtitles = 2, Clutter = 3 },
            Videos = [Episode(1, 5, "Glass Harbour")],
            Details = [$"Video: Lantern.S01E05.1080p-GRP/a.mkv → {Episode(1, 5, "Glass Harbour")}"],
        };

        var view = Presenter.Activity(entry, Now);

        Assert.Equal("Lantern S01E05", view.Item.Headline);
        Assert.Equal("Glass Harbour", view.Item.Subline);
        Assert.Equal("Filed into Shows.", view.Item.Summary);
        Assert.Equal(Icons.Filed, view.Item.Icon);
        Assert.Equal("ok", view.Item.Tone);
        Assert.True(view.CanUndo);
        Assert.Equal([Icons.Video, Icons.Subtitles, Icons.Quarantine], view.Item.Chips.Select(c => c.Icon));
        Assert.Equal([1, 2, 3], view.Item.Chips.Select(c => c.Count));
        Assert.Equal("2 subtitles filed", view.Item.Chips[1].Label);
        Assert.Contains(view.Item.Details, d => d.Group == "Release" && d.Label == "Release" && d.Value == "Lantern.S01E05.1080p-GRP");
        Assert.Contains(view.Item.Details, d => d.Group == "Release" && d.Label == "Action id" && d.Value == "run1");
        Assert.Contains(view.Item.Details, d => d.Group == "Files" && d.Label == "Video");
        Assert.Contains(view.Item.Details, d => d.Group == "Message" && d.Value == entry.Summary);
    }

    [Fact]
    public void A_filed_film_reads_as_title_and_year_with_edition_replacements_and_ai_help()
    {
        var film = $"{Films}/Harbour Lights (2024) [tmdbid-300001]/Harbour Lights (2024) [tmdbid-300001] - Director's Cut.mkv";
        var entry = new ActivityEntry
        {
            Time = Now.AddDays(-1),
            Status = ActivityStatus.Filed,
            Release = "Harbour.Lights.2024.DC.2160p-GRP",
            WatchFolder = Watch,
            Summary = "Filed 1 video.",
            Run = "run2",
            Counts = new ActivityCounts { Videos = 1, Replaced = 1 },
            Videos = [film],
            Details = ["a.mkv: Chosen by the AI plugin from 2 close candidates: 'Harbour Lights' (2024). It fits.", "Replaced (moved to quarantine): /media/films/Old/old.mkv", $"Video: x/a.mkv → {film}"],
        };

        var view = Presenter.Activity(entry, Now);

        Assert.Equal("Harbour Lights (2024)", view.Item.Headline);
        Assert.Equal("Director's Cut", view.Item.Subline);
        Assert.Equal("Filed into Movies. It replaced the copy that was there. The AI plugin helped decide.", view.Item.Summary);
        Assert.Contains(view.Item.Chips, c => c.Icon == Icons.Replaced && c.Count == 1 && c.Label == "1 copy on the server replaced");
        Assert.Contains(view.Item.Chips, c => c.Icon == Icons.Ai);
        Assert.Contains(view.Item.Details, d => d.Group == "Decisions" && d.Label == "a.mkv" && d.Value.StartsWith("Chosen by the AI plugin", StringComparison.Ordinal));
        Assert.Contains(view.Item.Details, d => d.Group == "Replaced" && d.Value == "/media/films/Old/old.mkv");
    }

    [Fact]
    public void A_season_pack_reads_as_show_season_and_episode_count()
    {
        var entry = new ActivityEntry
        {
            Time = Now,
            Status = ActivityStatus.DryRun,
            Release = "Lantern.S02.1080p-GRP",
            WatchFolder = Watch,
            Summary = "Would file 10 videos.",
            Counts = new ActivityCounts { Videos = 10 },
            Videos = [.. Enumerable.Range(1, 10).Select(e => Episode(2, e, "Part " + e))],
        };

        var view = Presenter.Activity(entry, Now);

        Assert.Equal("Lantern — Season 2, 10 episodes", view.Item.Headline);
        Assert.Equal(string.Empty, view.Item.Subline);
        Assert.Equal("Dry run: would be filed into Shows. Nothing was moved.", view.Item.Summary);
        Assert.Equal("10 videos would be filed", Assert.Single(view.Item.Chips).Label);
        Assert.False(view.CanUndo);
    }

    [Fact]
    public void Several_seasons_and_multi_episode_files_are_counted()
    {
        var titles = new[]
        {
            MediaTitle.FromFiledPath($"{Shows}/Lantern (2001)/Season 01/Lantern S01E01-E02 - Pilot.mkv")!,
            MediaTitle.FromFiledPath(Episode(2, 1, "Return"))!,
            MediaTitle.FromFiledPath(Episode(3, 1, "End"))!,
        };

        Assert.Equal(("Lantern — Seasons 1–3, 4 episodes", string.Empty), MediaTitle.Describe(titles));
        Assert.Equal("Lantern S01E01-E02", MediaTitle.Describe([titles[0]])!.Value.Headline);
    }

    [Fact]
    public void An_older_entry_without_counts_is_read_from_its_details()
    {
        var entry = new ActivityEntry
        {
            Time = Now,
            Status = ActivityStatus.Filed,
            Release = "Lantern.S01E04-GRP",
            WatchFolder = Watch,
            Summary = "Filed 1 video and 1 subtitle; quarantined 1 file.",
            Details =
            [
                $"Video: Lantern.S01E04-GRP/a.mkv → {Episode(1, 4, "Glass Harbour")}",
                $"Subtitle: Lantern.S01E04-GRP/a.srt → {Episode(1, 4, "Glass Harbour")[..^4]}.en.srt",
                $"Quarantine: Lantern.S01E04-GRP/info.nfo → {Watch}/.ingest-quarantine/2026-09-27/Lantern.S01E04-GRP/info.nfo",
            ],
        };

        var view = Presenter.Activity(entry, Now);

        Assert.Equal("Lantern S01E04", view.Item.Headline);
        Assert.Equal([1, 1, 1], view.Item.Chips.Select(c => c.Count));
        Assert.False(view.CanUndo);
    }

    [Fact]
    public void An_undone_filing_and_its_undo_entry()
    {
        var filing = new ActivityEntry
        {
            Time = Now.AddDays(-2),
            Status = ActivityStatus.Filed,
            Release = "Lantern.S01E04-GRP",
            Run = "run3",
            UndoneAt = Now.AddDays(-1),
            Counts = new ActivityCounts { Videos = 1 },
            Videos = [Episode(1, 4, "Glass Harbour")],
        };
        var undo = new ActivityEntry
        {
            Time = Now.AddDays(-1),
            Status = ActivityStatus.Undone,
            Release = "Lantern.S01E04-GRP",
            WatchFolder = Watch,
            Summary = "Undone by an administrator: 1 file(s) moved back to the watch folder. It now waits under Needs review.",
            Details = [$"Video: {Episode(1, 4, "Glass Harbour")} → {Watch}/Lantern.S01E04-GRP/a.mkv"],
        };

        var filed = Presenter.Activity(filing, Now);
        var undone = Presenter.Activity(undo, Now);

        Assert.False(filed.CanUndo);
        Assert.Equal("Filed into Shows. Later undone.", filed.Item.Summary);
        Assert.Contains(filed.Item.Chips, c => c.Icon == Icons.PutBack && c.Label == "Undone");
        Assert.Equal("Lantern S01E04", undone.Item.Headline);
        Assert.Equal(Icons.PutBack, undone.Item.Icon);
        Assert.Equal("Undone: it's back in the watch folder, waiting under Needs review.", undone.Item.Summary);
        Assert.Contains(undone.Item.Chips, c => c.Icon == Icons.PutBack && c.Count == 1);
    }

    [Fact]
    public void Needs_review_says_what_is_needed_in_plain_words()
    {
        var entry = new ActivityEntry
        {
            Time = Now,
            Status = ActivityStatus.NeedsReview,
            Release = "Harbour.2024.1080p-GRP",
            WatchFolder = Watch,
            Summary = "'Harbour' and 'Harbour Lights' are too close to call.",
            Details = ["Harbour.2024.1080p-GRP/a.mkv: 'Harbour' and 'Harbour Lights' are too close to call."],
        };

        var view = Presenter.Activity(entry, Now);

        Assert.Equal("Harbour (2024)", view.Item.Headline);
        Assert.Equal("Waiting for you: two matches look alike.", view.Item.Summary);
        Assert.Equal("warn", view.Item.Tone);
        Assert.Contains(view.Item.Details, d => d.Group == "Why it waits" && d.Label == "a.mkv");
    }

    [Fact]
    public void A_whole_release_quarantine_and_a_purge()
    {
        var quarantined = Presenter.Activity(new ActivityEntry
        {
            Time = Now,
            Status = ActivityStatus.Quarantined,
            Release = "Junk.Release-GRP",
            WatchFolder = Watch,
            Summary = "Quarantined the whole release (4 files) to /downloads/incoming/.ingest-quarantine/2026-09-27.",
            Counts = new ActivityCounts { Clutter = 4 },
        }, Now);
        var purged = Presenter.Activity(new ActivityEntry
        {
            Time = Now,
            Status = ActivityStatus.Purged,
            Release = Watch + "/.ingest-quarantine",
            Summary = "Deleted 2 quarantine folders older than 30 days.",
            Details = [Watch + "/.ingest-quarantine/2026-08-01", Watch + "/.ingest-quarantine/2026-08-02"],
        }, Now);

        Assert.Equal("Moved the whole release to quarantine (4 files).", quarantined.Item.Summary);
        Assert.Equal("4 files quarantined", Assert.Single(quarantined.Item.Chips).Label);
        Assert.Equal(".ingest-quarantine", purged.Item.Headline);
        Assert.Equal(Icons.Purged, purged.Item.Icon);
        Assert.Equal("Deleted 2 quarantine folders older than 30 days.", purged.Item.Summary);
        Assert.Equal(2, purged.Item.Details.Count(d => d.Group == "Deleted"));
    }

    [Fact]
    public void Activity_pages_filter_and_count_by_status()
    {
        var entries = Enumerable.Range(0, 40).Select(i => new ActivityEntry
        {
            Time = Now.AddMinutes(-i),
            Status = i % 4 == 0 ? ActivityStatus.Failed : ActivityStatus.Filed,
            Release = "Release " + i,
        }).ToList();

        var first = Presenter.ActivityPage(entries, null, 0, 15, Now);
        var failed = Presenter.ActivityPage(entries, "Failed", 5, 15, Now);
        var clamped = Presenter.ActivityPage(entries, "All", -3, 10_000, Now);

        Assert.Equal(15, first.Items.Count);
        Assert.Equal(40, first.Total);
        Assert.True(first.HasMore);
        Assert.Equal(40, first.StatusCounts["All"]);
        Assert.Equal(10, first.StatusCounts["Failed"]);
        Assert.Equal(30, first.StatusCounts["Filed"]);
        Assert.Equal(5, failed.Items.Count);
        Assert.Equal(10, failed.Total);
        Assert.False(failed.HasMore);
        Assert.All(failed.Items, a => Assert.Equal("Failed", a.Status));
        Assert.Equal(0, clamped.Offset);
        Assert.Equal(Paging.MaxLimit, clamped.Limit);
        Assert.Equal(40, clamped.Items.Count);
        Assert.Equal(40, first.Items.Concat(Presenter.ActivityPage(entries, null, 15, 30, Now).Items).Select(a => a.Item.Key).Distinct().Count());
    }

    [Fact]
    public void Quarantine_rows_page_across_days_and_read_as_titles()
    {
        var day1 = new QuarantineFolder
        {
            Root = Watch + "/.ingest-quarantine",
            Date = "2026-09-27",
            Folder = "2026-09-27",
            Managed = true,
            DeletesOn = new DateOnly(2026, 10, 28),
            Releases =
            [
                new QuarantinedRelease { Name = "Lantern.S01E04.1080p-GRP", FileCount = 2, Bytes = 1536, Files = [new("info.nfo", 512), new("sample.mkv", 1024)] },
                new QuarantinedRelease { Name = "Replaced", FileCount = 1, Bytes = 3L << 30, Files = [new("Lantern S01E04 - Glass Harbour.mkv", 3L << 30)] },
            ],
        };
        var day2 = day1 with
        {
            Date = "2026-09-20",
            Folder = "2026-09-20",
            Releases = [.. Enumerable.Range(1, 20).Select(i => new QuarantinedRelease { Name = $"Harbour.{2000 + i}-GRP", FileCount = 1, Bytes = 10, Files = [new("a.txt", 10)] })],
        };

        var page = IngestPresenter.QuarantinePage([day1, day2], 0, 15);
        var rest = IngestPresenter.QuarantinePage([day1, day2], 15, 15);

        Assert.Equal(22, page.Total);
        Assert.Equal(15, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Equal(7, rest.Items.Count);
        Assert.False(rest.HasMore);
        Assert.Equal(2, page.Days.Count);
        Assert.Equal(23, page.FileCount);
        var release = page.Items[0];
        Assert.Equal("Lantern S01E04", release.Item.Headline);
        Assert.Equal(string.Empty, release.Item.Subline);
        Assert.Equal("Lantern.S01E04.1080p-GRP", release.Name);
        Assert.Equal("1.5 KB", release.Item.Chips[0].Label.Split(", ")[1]);
        Assert.Contains(release.Item.Details, d => d.Group == "Files" && d.Label == "sample.mkv" && d.Value == "1.0 KB");
        var replaced = page.Items[1];
        Assert.Equal("Replaced copies", replaced.Item.Headline);
        Assert.Equal("Lantern S01E04", replaced.Item.Subline);
        Assert.Equal(Icons.Replaced, replaced.Item.Icon);
        Assert.Contains(replaced.Item.Chips, c => c.Icon == Icons.Video && c.Count == 1);
        Assert.Equal("Harbour (2001)", page.Items[2].Item.Headline);
    }

    [Fact]
    public void A_review_card_leads_with_the_likely_title_and_why_it_waits()
    {
        var review = new PendingReview
        {
            Id = "abc",
            WatchFolder = Watch,
            Release = "Harbour.2024.1080p-GRP",
            Time = Now,
            Items = [new PendingReviewItem("Harbour.2024.1080p-GRP/a.mkv", "Harbour (2024) is already on the server: /media/films/Harbour (2024)/Harbour (2024).mkv") { Existing = "/media/films/Harbour (2024)/Harbour (2024).mkv" }],
            Candidates = [new ScoredCandidate(new MetadataCandidate { Name = "Harbour", Year = 2024, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "400001" } }, 0.93)],
        };

        var view = Presenter.Review(review);

        Assert.Equal("Harbour (2024)", view.Headline);
        Assert.Equal(string.Empty, view.Subline);
        Assert.Equal("Looks like Harbour Lights (2024)", Presenter.Review(review with { Candidates = [new ScoredCandidate(new MetadataCandidate { Name = "Harbour Lights", Year = 2024 }, 0.7)] }).Subline);
        Assert.Equal("Waiting for you: it's already on the server.", view.Summary);
        Assert.Contains(view.Chips, c => c.Icon == Icons.Replaced && c.Count == 1);
        Assert.Contains(view.Chips, c => c.Label == "1 possible match");
        Assert.Equal("2 matches", MediaTitle.Plural(2, "match"));
        Assert.Contains(view.Details, d => d.Group == "Already on the server" && d.Label == "Movies");
        Assert.Contains(view.Details, d => d.Group == "Candidates" && d.Value.StartsWith("score 0.93 · film · Tmdb 400001", StringComparison.Ordinal));
    }

    [Fact]
    public void Waiting_and_working_rows_read_as_titles()
    {
        var waiting = IngestPresenter.Waiting(new WaitingRelease { Id = "w", WatchFolder = Watch, Release = "Lantern.S03.720p-GRP" });
        var working = IngestPresenter.Working(new WorkInProgress { Id = "w", WatchFolder = Watch, Release = "Harbour.2024-GRP", Stage = "Filing", File = 2, Files = 5 });

        Assert.Equal("Lantern — Season 3", waiting.Headline);
        Assert.Equal("Still downloading.", waiting.Summary);
        Assert.Equal("Harbour (2024)", working.Headline);
        Assert.Equal("Filing file 2 of 5…", working.Summary);
    }

    [Theory]
    [InlineData("Nothing was filed: disk full (2 completed move(s) were undone.)", "Nothing was filed: disk full (2 completed move(s) were undone.)")]
    [InlineData("First part. Second part.", "First part.")]
    [InlineData("A size of 1.5 GB was too big", "A size of 1.5 GB was too big.")]
    public void First_sentence(string text, string expected) => Assert.Equal(expected, Friendly.FirstSentence(text));

    [Fact]
    public void Long_messages_are_shortened_at_a_word()
    {
        var shortened = Friendly.FirstSentence(string.Join(' ', Enumerable.Repeat("word", 60)));

        Assert.True(shortened.Length <= Friendly.MaxSentence);
        Assert.EndsWith("word…", shortened, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_tell_replaced_and_set_aside_files_from_clutter()
    {
        var plan = new IngestPlan { ReleaseName = "R", Replacing = ["/media/films/old.mkv"], Skipped = [Watch + "/R/b.mkv"] };
        PlannedOperation[] done =
        [
            new(OperationKind.Quarantine, "/media/films/old.mkv", "/q/Replaced/old.mkv"),
            new(OperationKind.Quarantine, Watch + "/R/b.mkv", "/q/R/b.mkv"),
            new(OperationKind.Quarantine, Watch + "/R/info.nfo", "/q/R/info.nfo"),
            new(OperationKind.Video, Watch + "/R/a.mkv", "/media/films/New (2020) [tmdbid-1]/New (2020) [tmdbid-1].mkv"),
            new(OperationKind.Subtitle, Watch + "/R/a.srt", "/media/films/New (2020) [tmdbid-1]/New (2020) [tmdbid-1].en.srt"),
        ];

        var counts = ActivityReport.CountOf(plan, done);

        Assert.Equal(new ActivityCounts { Videos = 1, Subtitles = 1, Clutter = 1, Replaced = 1, SetAside = 1 }, counts);
        Assert.Equal(["/media/films/New (2020) [tmdbid-1]/New (2020) [tmdbid-1].mkv"], ActivityReport.VideosOf(done));
    }

    [Theory]
    [InlineData("/x/Some.Film.2020/Some.Film.2020.mkv")]
    [InlineData("/x/Season 01/random.mkv")]
    [InlineData("")]
    public void Names_Ingest_does_not_give_are_not_read_as_filed(string path) => Assert.Null(MediaTitle.FromFiledPath(path));
}
