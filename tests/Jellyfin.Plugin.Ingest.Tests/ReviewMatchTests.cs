using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Api;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Presentation;
using Jellyfin.Plugin.Ingest.Service;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// The title a review is using ("Using this"), switching to another one, and replacing only against the title shown.
// Invented titles, ids and paths throughout.
public sealed class ReviewMatchTests : IDisposable
{
    private const string Watch = "/drop/incoming";
    private const string Quarantine = "/drop/incoming/.ingest-quarantine";
    private const string Release = "Example.Show.S04.1080p-GRP";
    private static readonly LibraryTarget Tv = new("/lib/Shows", IsTv: true);

    private static readonly MetadataCandidate Older = new() { Name = "Example Show", Year = 2001, IsSeries = true, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "501", ["Tmdb"] = "601" } };
    private static readonly MetadataCandidate Newer = new() { Name = "Example Show", Year = 2019, IsSeries = true, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "502", ["Tmdb"] = "602" } };
    private static readonly MetadataCandidate Other = new() { Name = "Example Shop", Year = 2010, IsSeries = true, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "503" } };

    private static readonly IngestPresenter Presenter = new([new MediaLibrary("s", "Shows", LibraryKind.Shows, ["/lib/Shows"])]);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ingest-match-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    // Searching finds only the older show (so it is identified by itself); the newer one comes from a search in review
    private sealed class Lookup : IMetadataLookup
    {
        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>(TitleMatcher.Similarity(name, "Example Show") > 0.9 ? [Older] : []);

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EpisodeListing>>([]);
    }

    // Episodes on the server, by show (Tvdb id): the older show has all of season 4, the newer one only episode 2
    private sealed class OnServer : IExistingMedia
    {
        public static string Path(string tvdb, int episode) => $"/lib/Shows/Example Show [{tvdb}]/Season 04/Example Show S04E{episode:00}.mkv";

        public string? FindSeriesFolder(IReadOnlyDictionary<string, string> seriesProviderIds)
            => seriesProviderIds.TryGetValue("Tvdb", out var id) ? $"/lib/Shows/Example Show [{id}]" : null;

        public string? FindEpisode(IReadOnlyDictionary<string, string> seriesProviderIds, string seriesFolder, int season, int episode)
            => !seriesProviderIds.TryGetValue("Tvdb", out var id) || season != 4 ? null
                : id == "501" ? Path(id, episode)
                : id == "502" && episode == 2 ? Path(id, episode)
                : null;

        public string? FindSeasonFolder(string seriesFolder, int season) => null;

        public string? FindMovie(IReadOnlyDictionary<string, string> movieProviderIds, string? edition, string plannedPath) => null;
    }

    private static readonly ReleaseFile[] Files = [new(Release + "/Example.Show.S04E01.mkv", 500_000_000), new(Release + "/Example.Show.S04E02.mkv", 500_000_000)];

    private static IngestPlanner Planner(PendingReview? review) => new(
        new MediaIdentifier(new Lookup()),
        p => !Path.HasExtension(p),
        _ => null,
        new FixedClock(),
        new OnServer(),
        p => PathGuard.IsUnder(p, "/lib"))
    {
        ReplaceExisting = review?.Request == ReviewRequest.Replace,
        FileDecisions = review?.FileDecisions ?? new Dictionary<string, FileDecision>(StringComparer.Ordinal),
    };

    // What the sweep stores after planning (see IngestService.IngestAsync)
    private static PendingReview FromPlan(IngestPlan plan) => new()
    {
        Id = IngestStateStore.ReviewId(Watch, Release),
        WatchFolder = Watch,
        Release = Release,
        Time = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero),
        Items = [.. plan.Review.Select(r => new PendingReviewItem(Path.GetRelativePath(Watch, r.Source), r.Reason) { Existing = r.Existing })],
        Candidates = IngestService.DistinctCandidates(plan.Review),
        Matched = IngestService.PlannedMatch(plan.Review),
    };

    private static PendingReview Duplicates(params ScoredCandidate[] candidates) => new()
    {
        Id = "dup",
        WatchFolder = Watch,
        Release = Release,
        Time = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero),
        Items =
        [
            new PendingReviewItem(Release + "/Example.Show.S04E01.mkv", "Example Show S04E01 is already on the server") { Existing = OnServer.Path("501", 1) },
            new PendingReviewItem(Release + "/Example.Show.S04E02.mkv", "Example Show S04E02 is already on the server") { Existing = OnServer.Path("501", 2) },
        ],
        Candidates = candidates,
        Matched = Older,
    };

    [Fact]
    public void A_duplicate_review_says_which_title_it_is_using_and_offers_the_others_instead()
    {
        var review = Duplicates(new ScoredCandidate(Older, 0.95), new ScoredCandidate(Newer, 0.9), new ScoredCandidate(Other, 0.5));

        var match = Assert.IsType<ReviewMatchView>(Presenter.Review(review).Match);

        Assert.Equal(ReviewChoice.KeyOf(Older), match.CurrentKey);
        Assert.Equal("Example Show (2001)", match.Title);
        Assert.Equal("Matched to Example Show (2001). 2 of these episodes are already in Shows.", match.Headline);
        Assert.False(match.Checking);
        Assert.Equal("Wrong show? Pick another", match.PickHeading);
        Assert.Equal(new[] { CandidateState.InUse, CandidateState.Alternative, CandidateState.Alternative }, match.Candidates);
    }

    [Fact]
    public void An_ambiguous_review_offers_every_candidate_as_an_option()
    {
        var review = Duplicates(new ScoredCandidate(Older, 0.8), new ScoredCandidate(Newer, 0.78)) with
        {
            Matched = null,
            Items = [new PendingReviewItem(Release + "/Example.Show.S04E01.mkv", "Too close to call.")],
        };

        var match = Assert.IsType<ReviewMatchView>(Presenter.Review(review).Match);

        Assert.Null(match.CurrentKey);
        Assert.Equal(string.Empty, match.Headline);
        Assert.Equal("Is it one of these?", match.PickHeading);
        Assert.Equal(new[] { CandidateState.Option, CandidateState.Option }, match.Candidates);
    }

    [Fact]
    public void A_single_film_on_the_server_reads_naturally()
    {
        var film = new MetadataCandidate { Name = "Harbour Lights", Year = 2024, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "700" } };
        var review = new PendingReview
        {
            Id = "f",
            WatchFolder = Watch,
            Release = "Harbour.Lights.2024",
            Time = DateTimeOffset.UnixEpoch,
            Items = [new PendingReviewItem("Harbour.Lights.2024/a.mkv", "already on the server") { Existing = "/lib/Shows/x.mkv" }],
            Candidates = [new ScoredCandidate(film, 0.9)],
            Matched = film,
        };

        var match = Presenter.Match(review);

        Assert.Equal("Matched to Harbour Lights (2024). It is already in Shows.", match.Headline);
        Assert.Equal("Wrong film? Pick another", match.PickHeading);
    }

    [Fact]
    public async Task Choosing_another_title_drops_the_replacement_and_plans_against_the_new_titles_copies()
    {
        Directory.CreateDirectory(_dir);
        var store = new IngestStateStore(Path.Combine(_dir, "state.json"));

        // 1. Identified as the older show; all of season 4 is already on the server
        var first = await Planner(null).PlanAsync(Watch, Release, Files, LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);
        Assert.False(first.IsReady);
        var planned = FromPlan(first);
        Assert.True(ReviewChoice.SameTitle(Older, planned.Matched));
        Assert.All(planned.Items, i => Assert.Contains("[501]", i.Existing, StringComparison.Ordinal));
        store.PutReview(planned);
        var id = planned.Id;

        // 2. A replacement was asked for (whole release, and file by file), for the older show's copies
        store.RequestFiles(id, new Dictionary<string, FileDecision?> { [planned.Items[0].Source] = FileDecision.Quarantine, [planned.Items[1].Source] = FileDecision.Replace });
        store.RequestReplace(id);
        var oldKey = ReviewChoice.CurrentMatchKey(store.GetReview(id)!);
        Assert.Null(ReviewChoice.StaleReplace(store.GetReview(id)!, oldKey));

        // 3. The newer show is picked instead (from a search)
        store.SetSearchResults(id, [new ScoredCandidate(Newer, 0.9)]);
        var picked = ReviewChoice.Pick(store.GetReview(id)!, ReviewChoice.Search, 0)!;
        store.RequestRetry(id, new ChosenMatch(picked, Tv));
        var switched = store.GetReview(id)!;

        Assert.Equal(ReviewRequest.Retry, switched.Request);
        Assert.DoesNotContain(switched.FileDecisions.Values, d => d == FileDecision.Replace);
        Assert.Equal(FileDecision.Quarantine, switched.FileDecisions[planned.Items[0].Source]);
        Assert.NotNull(ReviewChoice.StaleReplace(switched, oldKey));
        Assert.NotNull(ReviewChoice.StaleReplace(switched, ReviewChoice.CurrentMatchKey(switched)));
        var checking = Presenter.Match(switched);
        Assert.True(checking.Checking);
        Assert.Equal("Matched to Example Show (2019). Ingest looks for copies of it on the server on the next sweep.", checking.Headline);

        // 4. The next sweep plans with the newer show: its own copy of episode 2 holds that episode back, and nothing is
        // replaced (the replace request and decision were for the older show); episode 1 stays set aside
        var second = await Planner(switched).PlanAsync(Watch, Release, [Files[0], Files[1]], LibraryTargets.Of(Tv), Quarantine, switched.Chosen, CancellationToken.None);
        Assert.False(second.IsReady);
        store.PutReview(FromPlan(second), switched.RequestVersion);
        var replanned = store.GetReview(id)!;

        Assert.Equal(OnServer.Path("502", 2), Assert.Single(replanned.Items, i => i.Existing.Length > 0).Existing);
        Assert.DoesNotContain(replanned.Items, i => i.Existing.Contains("[501]", StringComparison.Ordinal));
        Assert.True(ReviewChoice.SameTitle(Newer, replanned.Matched));
        Assert.Equal(ReviewRequest.None, replanned.Request);
        Assert.Equal(new[] { CandidateState.InUse }, Presenter.Match(replanned).SearchResults);
        Assert.Equal(new[] { CandidateState.Alternative }, Presenter.Match(replanned).Candidates);
        Assert.Null(ReviewChoice.StaleReplace(replanned, ReviewChoice.CurrentMatchKey(replanned)));
        Assert.NotNull(ReviewChoice.StaleReplace(replanned, oldKey));
    }

    [Fact]
    public void Choosing_the_title_already_in_use_keeps_the_decisions()
    {
        Directory.CreateDirectory(_dir);
        var store = new IngestStateStore(Path.Combine(_dir, "state.json"));
        var review = Duplicates(new ScoredCandidate(Older, 0.95));
        store.PutReview(review);
        store.RequestFiles(review.Id, new Dictionary<string, FileDecision?> { [review.Items[0].Source] = FileDecision.Replace });

        store.RequestRetry(review.Id, new ChosenMatch(ReviewChoice.Pick(review, ReviewChoice.Suggested, 0)!, Tv));

        Assert.Equal(FileDecision.Replace, store.GetReview(review.Id)!.FileDecisions[review.Items[0].Source]);
    }

    [Fact]
    public void A_replace_from_a_page_drawn_before_the_title_changed_is_refused()
    {
        Directory.CreateDirectory(_dir);
        var store = new IngestStateStore(Path.Combine(_dir, "state.json"));
        var review = Duplicates(new ScoredCandidate(Older, 0.95), new ScoredCandidate(Newer, 0.9));
        store.PutReview(review);
        var shown = ReviewChoice.CurrentMatchKey(review);
        var existing = ReviewDecisions.ExistingOf(review);

        // Another tab picks the newer show; the old page still offers "Replace existing copies" for the older one
        store.RequestRetry(review.Id, new ChosenMatch(ReviewChoice.Pick(review, ReviewChoice.Suggested, 1)!, Tv));

        var stale = ReviewDecisions.Replace(store, review.Id, new ReplaceRequest { Existing = existing, Key = shown });
        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(stale).StatusCode);
        var files = ReviewDecisions.Files(store, review.Id, new FilesRequest { Key = shown, Decisions = [new FileChoice { Source = review.Items[0].Source, Action = "replace", Existing = review.Items[0].Existing }] });
        Assert.IsType<ConflictObjectResult>(files);
        Assert.NotEqual(ReviewRequest.Replace, store.GetReview(review.Id)!.Request);
        Assert.Empty(store.GetReview(review.Id)!.FileDecisions);

        // Without a key (a page that showed no title in use) it is refused too
        Assert.IsType<ConflictObjectResult>(ReviewDecisions.Replace(store, review.Id, new ReplaceRequest { Existing = existing }));
    }

    [Fact]
    public void A_replace_for_the_title_shown_goes_ahead()
    {
        Directory.CreateDirectory(_dir);
        var store = new IngestStateStore(Path.Combine(_dir, "state.json"));
        var review = Duplicates(new ScoredCandidate(Older, 0.95));
        store.PutReview(review);

        var result = ReviewDecisions.Replace(store, review.Id, new ReplaceRequest { Existing = ReviewDecisions.ExistingOf(review), Key = ReviewChoice.CurrentMatchKey(review) });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(ReviewRequest.Replace, store.GetReview(review.Id)!.Request);
    }
}
