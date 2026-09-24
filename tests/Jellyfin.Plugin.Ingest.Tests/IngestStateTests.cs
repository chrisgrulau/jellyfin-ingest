using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented titles throughout.
public sealed class IngestStateTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ingest-state-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static PendingReview Review(string release, params ScoredCandidate[] candidates) => new()
    {
        Id = IngestStateStore.ReviewId("/drop", release),
        WatchFolder = "/drop",
        Release = release,
        Time = T0,
        Items = [new PendingReviewItem(release + "/a.mkv", "Too close to call.")],
        Candidates = candidates,
    };

    private static ScoredCandidate Cand(string name, int year, string tmdb, double score)
        => new(new MetadataCandidate { Name = name, Year = year, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = tmdb } }, score);

    [Fact]
    public void Review_ids_are_stable_and_distinct()
    {
        Assert.Equal(IngestStateStore.ReviewId("/drop", "a"), IngestStateStore.ReviewId("/drop", "a"));
        Assert.NotEqual(IngestStateStore.ReviewId("/drop", "a"), IngestStateStore.ReviewId("/drop2", "a"));
        Assert.Equal(16, IngestStateStore.ReviewId("/drop", "a").Length);
    }

    [Fact]
    public void State_survives_a_restart()
    {
        var store = new IngestStateStore(StatePath);
        var r = Review("Quiet.Harbour", Cand("Quiet Harbour", 2010, "1", 0.9), Cand("Quiet Harbour", 2018, "2", 0.88));
        store.PutReview(r);
        store.RequestRetry(r.Id, new ChosenMatch(r.Candidates[1].Candidate, new LibraryTarget("/lib/Movies", IsTv: false)));
        store.Record(new ActivityEntry { Time = T0, Status = ActivityStatus.NeedsReview, Release = "Quiet.Harbour", Summary = "x" });

        var reloaded = new IngestStateStore(StatePath).Snapshot();

        var review = Assert.Single(reloaded.Reviews);
        Assert.Equal(ReviewRequest.Retry, review.Request);
        Assert.Equal(2018, review.Chosen!.Candidate.Year);
        Assert.Equal("2", review.Chosen.Candidate.ProviderIds["Tmdb"]);
        Assert.Equal("/lib/Movies", review.Chosen.Target.Root);
        Assert.Equal(2, review.Candidates.Count);
        Assert.Equal(ActivityStatus.NeedsReview, Assert.Single(reloaded.Activity).Status);
    }

    [Fact]
    public void Replanning_keeps_the_choice_but_clears_the_request()
    {
        var store = new IngestStateStore(StatePath);
        var r = Review("Quiet.Harbour", Cand("Quiet Harbour", 2010, "1", 0.9));
        store.PutReview(r);
        store.RequestRetry(r.Id, new ChosenMatch(r.Candidates[0].Candidate, new LibraryTarget("/lib/Movies", IsTv: false)));

        store.PutReview(r with { Items = [new PendingReviewItem("x", "Destination already exists")] });

        var review = store.GetReview(r.Id)!;
        Assert.Equal(ReviewRequest.None, review.Request);
        Assert.Equal("Quiet Harbour", review.Chosen!.Candidate.Name);
        Assert.Equal("Destination already exists", Assert.Single(review.Items).Reason);
    }

    [Fact]
    public void Requests_for_unknown_reviews_are_refused()
    {
        var store = new IngestStateStore(StatePath);
        Assert.False(store.RequestRetry("nope", null));
        Assert.False(store.RequestQuarantine("nope"));
    }

    [Fact]
    public void Reviews_for_releases_that_left_the_watch_folder_are_pruned()
    {
        var store = new IngestStateStore(StatePath);
        store.PutReview(Review("kept"));
        store.PutReview(Review("gone"));
        store.PutReview(Review("elsewhere") with { WatchFolder = "/other" });

        store.PruneReviews("/drop", ["kept"]);

        Assert.Equal(["elsewhere", "kept"], store.Snapshot().Reviews.Select(r => r.Release).Order());
    }

    [Fact]
    public void Activity_is_newest_first_and_capped()
    {
        var store = new IngestStateStore(StatePath);
        for (var i = 0; i < IngestStateStore.MaxActivity + 5; i++)
        {
            store.Record(new ActivityEntry { Time = T0.AddMinutes(i), Status = ActivityStatus.Filed, Release = $"r{i}" });
        }

        var activity = store.Snapshot().Activity;
        Assert.Equal(IngestStateStore.MaxActivity, activity.Count);
        Assert.Equal($"r{IngestStateStore.MaxActivity + 4}", activity[0].Release);
    }

    [Fact]
    public void Long_detail_lists_are_trimmed()
    {
        var store = new IngestStateStore(StatePath);
        store.Record(new ActivityEntry { Time = T0, Status = ActivityStatus.Filed, Release = "big", Details = [.. Enumerable.Range(0, 100).Select(i => $"line {i}")] });

        var details = Assert.Single(store.Snapshot().Activity).Details;
        Assert.Equal(IngestStateStore.MaxDetails + 1, details.Count);
        Assert.Equal("… and 40 more", details[^1]);
    }

    [Fact]
    public void A_damaged_state_file_starts_fresh()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ not json");
        Assert.Empty(new IngestStateStore(StatePath).Snapshot().Reviews);
    }

    [Theory]
    [InlineData(false, "Filed 1 video and 2 subtitles; quarantined 3 files.")]
    [InlineData(true, "Would file 1 video and 2 subtitles; would quarantine 3 files.")]
    public void Summaries_read_naturally(bool dryRun, string expected)
    {
        PlannedOperation Op(OperationKind k) => new(k, "/s", "/d");
        var ops = new[] { Op(OperationKind.Video), Op(OperationKind.Subtitle), Op(OperationKind.Subtitle), Op(OperationKind.Quarantine), Op(OperationKind.Quarantine), Op(OperationKind.Quarantine) };
        Assert.Equal(expected, IngestService.Summarise(ops, dryRun));
    }

    [Fact]
    public void Candidates_across_files_are_deduplicated_best_first()
    {
        var a = Cand("Quiet Harbour", 2010, "1", 0.80);
        var b = Cand("Quiet Harbour", 2018, "2", 0.85);
        var items = new[]
        {
            new ReviewItem("/x/1.mkv", "r") { Candidates = [a, b] },
            new ReviewItem("/x/2.mkv", "r") { Candidates = [a with { Score = 0.82 }, b] },
        };

        var result = IngestService.DistinctCandidates(items);

        Assert.Equal(["2", "1"], result.Select(c => c.Candidate.ProviderIds["Tmdb"]));
        Assert.Equal(0.82, result[1].Score);
    }

    [Fact]
    public void Identical_repeats_are_not_recorded_twice()
    {
        var store = new IngestStateStore(StatePath);
        var e = new ActivityEntry { Time = T0, Status = ActivityStatus.DryRun, Release = "r", WatchFolder = "/drop", Summary = "Would file 1 video.", Details = ["a"] };

        Assert.True(store.RecordUnlessRepeat(e));
        Assert.False(store.RecordUnlessRepeat(e with { Time = T0.AddHours(1) }));
        Assert.True(store.RecordUnlessRepeat(e with { Details = ["b"] }));
        Assert.Equal(2, store.Snapshot().Activity.Count);
    }

    [Fact]
    public void The_outcome_of_a_decision_is_always_recorded()
    {
        var store = new IngestStateStore(StatePath);
        var e = new ActivityEntry { Time = T0, Status = ActivityStatus.DryRun, Release = "r", WatchFolder = "/drop", Summary = "Would file 1 video." };
        store.Record(e);
        store.Record(new ActivityEntry { Time = T0, Status = ActivityStatus.Decision, Release = "r", WatchFolder = "/drop", Summary = "Chose X." });

        Assert.True(store.RecordUnlessRepeat(e with { Time = T0.AddMinutes(1) }));
    }

    [Fact]
    public void A_dry_run_plan_replaces_the_old_reasons_but_keeps_the_choice()
    {
        var store = new IngestStateStore(StatePath);
        var r = Review("Quiet.Harbour", Cand("Quiet Harbour", 2010, "1", 0.9));
        store.PutReview(r);
        store.RequestRetry(r.Id, new ChosenMatch(r.Candidates[0].Candidate, new LibraryTarget("/lib/Movies", IsTv: false)));

        store.MarkPlannedInDryRun(r.Id, "Dry run: planned.");

        var review = store.GetReview(r.Id)!;
        Assert.Equal("Dry run: planned.", Assert.Single(review.Items).Reason);
        Assert.Equal(ReviewRequest.None, review.Request);
        Assert.NotNull(review.Chosen);
    }

    [Fact]
    public void An_incomplete_saved_choice_is_dropped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Reviews": [ { "Id": "a", "WatchFolder": "/drop", "Release": "r", "Time": "2026-09-24T10:00:00+00:00", "Chosen": { "Name": "Old Format", "Year": 2020 } } ] }""");

        var review = Assert.Single(new IngestStateStore(StatePath).Snapshot().Reviews);
        Assert.Null(review.Chosen);
    }
}
