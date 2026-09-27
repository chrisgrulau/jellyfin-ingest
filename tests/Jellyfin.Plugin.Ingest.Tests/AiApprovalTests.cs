using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.Ingest.Api;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Parsing;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Presentation;
using Jellyfin.Plugin.Ingest.Service;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// "AI suggests, you approve" (FEAT-02): a watch folder that asks before filing what the AI decided. Invented titles,
// ids and paths throughout.
public sealed class AiApprovalTests : IDisposable
{
    private const string Watch = "/drop/incoming";
    private const string Quarantine = "/drop/incoming/.ingest-quarantine";
    private const string Heard = "Inspector Vale said the lighthouse lamp went dark the night the ferry sank and nobody on the quay saw a thing, and then the harbour master closed the gates before the storm came in over the breakwater";
    private static readonly LibraryTarget Tv = new("/lib/Shows", IsTv: true);
    private static readonly LibraryTarget Films = new("/lib/Movies", IsTv: false);

    private static readonly MetadataCandidate Older = new() { Name = "Harbour Lights", Year = 1998, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "801" } };
    private static readonly MetadataCandidate Newer = new() { Name = "Harbour Lights", Year = 2003, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "802" } };
    private static readonly MetadataCandidate Show = new() { Name = "Harbour Watch", Year = 2004, IsSeries = true, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "71" } };

    private static readonly IngestPresenter Presenter = new([new MediaLibrary("m", "Movies", LibraryKind.Films, ["/lib/Movies"]), new MediaLibrary("s", "Shows", LibraryKind.Shows, ["/lib/Shows"])]);

    private const string FilmRelease = "Harbour.Lights.1080p-GRP";
    private const string FilmVideo = FilmRelease + "/Harbour.Lights.1080p-GRP.mkv";
    private const string ShowRelease = "Harbour Watch";
    private const string ShowVideo = ShowRelease + "/Harbour Watch - unknown episode.mkv";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ingest-ai-approve-" + Guid.NewGuid().ToString("N"));

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

    // Two films of the same name (no year in the release name: too close to call), and one show with two seasons
    private sealed class Lookup : IMetadataLookup
    {
        public int EpisodeTitles { get; private set; }

        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>(TitleMatcher.Similarity(name, "Harbour Watch") > 0.9 ? [Show] : []);

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>(TitleMatcher.Similarity(name, "Harbour Lights") > 0.9 ? [Older, Newer] : []);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
        {
            EpisodeTitles++;
            return Task.FromResult<string?>($"Episode {season}-{episode}");
        }

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EpisodeListing>>(season is 1 or 2
                ? [.. Enumerable.Range(1, 4).Select(e => new EpisodeListing(season, e, $"Episode {season}-{e}", 2004 + season, $"Synopsis {season}-{e}."))]
                : []);
    }

    // Counts every question, so approving can be shown not to ask again
    private sealed class Ai : ITiebreaker, IEpisodePicker
    {
        public int Calls { get; private set; }

        public Task<TiebreakPick> PickAsync(ParsedRelease release, string fileName, IReadOnlyList<ScoredCandidate> options, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TiebreakPick(options.ToList().FindIndex(o => o.Candidate.Year == 2003), "The release group only encodes the remake.", "AI (test-model)"));
        }

        public Task<TiebreakPick> PickEpisodeAsync(string fileName, string series, string episodeTitle, int? year, IReadOnlyList<EpisodeListing> options, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TiebreakPick(null, string.Empty, null));
        }

        public Task<TiebreakPick> PickFromTranscriptAsync(string fileName, string series, string transcript, IReadOnlyList<EpisodeListing> options, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TiebreakPick(options.ToList().FindIndex(o => o.Season == 2 && o.Episode == 3), "The lighthouse and the ferry.", "AI (test-model)"));
        }
    }

    private sealed class Transcriber : ITranscriber
    {
        public int Calls { get; private set; }

        public Task<HeardText> TranscribeAsync(string videoPath, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HeardText(Heard, string.Empty) { From = TimeSpan.FromMinutes(5) });
        }
    }

    // A copy of the remake is already on the server
    private sealed class OnServer(bool remake) : IExistingMedia
    {
        public const string Remake = "/lib/Movies/Harbour Lights (2003)/Harbour Lights (2003).mkv";

        public string? FindSeriesFolder(IReadOnlyDictionary<string, string> seriesProviderIds) => null;

        public string? FindEpisode(IReadOnlyDictionary<string, string> seriesProviderIds, string seriesFolder, int season, int episode) => null;

        public string? FindSeasonFolder(string seriesFolder, int season) => null;

        public string? FindMovie(IReadOnlyDictionary<string, string> movieProviderIds, string? edition, string plannedPath)
            => remake && movieProviderIds.TryGetValue("Tmdb", out var id) && id == "802" ? Remake : null;
    }

    private static IngestPlanner Planner(Ai ai, Transcriber? transcriber = null, bool askFirst = false, PendingReview? review = null, bool onServer = false, Lookup? lookup = null) => new(
        new MediaIdentifier(lookup ?? new Lookup(), null, ai, transcriber),
        p => !Path.HasExtension(p),
        _ => null,
        new FixedClock(),
        new OnServer(onServer),
        p => PathGuard.IsUnder(p, "/lib"))
    {
        AskBeforeAiFiling = askFirst,
        ApprovedSuggestions = review?.Approved ?? new Dictionary<string, AiSuggestion>(StringComparer.Ordinal),
        ReplaceExisting = review?.Request == ReviewRequest.Replace,
    };

    private static Task<IngestPlan> PlanFilm(IngestPlanner planner)
        => planner.PlanAsync(Watch, FilmRelease, [new ReleaseFile(FilmVideo, 500_000_000)], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

    private static Task<IngestPlan> PlanShow(IngestPlanner planner)
        => planner.PlanAsync(Watch, ShowRelease, [new ReleaseFile(ShowVideo, 500_000_000)], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

    // What the sweep stores after planning (see IngestService.IngestAsync)
    private static PendingReview FromPlan(string release, IngestPlan plan) => new()
    {
        Id = IngestStateStore.ReviewId(Watch, release),
        WatchFolder = Watch,
        Release = release,
        Time = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero),
        Items = IngestService.ItemsOf(Watch, plan),
        Candidates = IngestService.DistinctCandidates(plan.Review),
        Matched = IngestService.PlannedMatch(plan.Review),
    };

    private IngestStateStore Store()
    {
        Directory.CreateDirectory(_dir);
        return new IngestStateStore(Path.Combine(_dir, "state.json"));
    }

    [Fact]
    public async Task A_close_match_the_AI_settles_is_filed_automatically_by_default()
    {
        var ai = new Ai();

        var plan = await PlanFilm(Planner(ai));

        Assert.True(plan.IsReady);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Video && o.Destination.Contains("tmdbid-802", StringComparison.Ordinal));
        Assert.Equal(1, ai.Calls);
        Assert.Equal(0, plan.ApprovedAi);
        Assert.Equal(FilingChoice.Automatic, IngestService.ChoiceOf(null, plan));
    }

    [Fact]
    public async Task Asking_first_holds_the_AIs_match_for_approval_with_its_choice_in_use()
    {
        var ai = new Ai();

        var plan = await PlanFilm(Planner(ai, askFirst: true));

        Assert.False(plan.IsReady);
        Assert.Empty(plan.Operations);
        var item = Assert.Single(plan.Review);
        Assert.True(item.ApprovalOnly);
        Assert.Equal(string.Empty, item.Existing);
        var s = Assert.IsType<AiSuggestion>(item.Suggestion);
        Assert.True(ReviewChoice.SameTitle(Newer, s.Title));
        Assert.Null(s.Season);
        var d = Assert.Single(s.Decisions);
        Assert.Equal(AiDecisionKind.Match, d.Kind);
        Assert.Equal("AI (test-model)", d.By);
        Assert.Equal("The release group only encodes the remake.", d.Reason);
        Assert.Equal(2, d.Options);
        Assert.NotNull(d.Confidence);
        Assert.StartsWith("Suggested by AI (test-model): Harbour Lights (2003).", item.Reason, StringComparison.Ordinal);
        Assert.True(ReviewChoice.SameTitle(Newer, item.Matched));

        // As the sweep stores it: the AI's title is the one in use, the other offered instead
        var review = FromPlan(FilmRelease, plan);
        Assert.True(ReviewChoice.SameTitle(Newer, review.Matched));
        Assert.True(AiApproval.IsOnlyAi(review));
        Assert.Equal("the AI made a suggestion for you to approve", Friendly.Reason(item.Reason));
    }

    [Fact]
    public async Task A_transcript_pick_is_filed_automatically_by_default_and_held_when_asking_first()
    {
        var auto = await PlanShow(Planner(new Ai(), new Transcriber()));
        Assert.True(auto.IsReady);
        Assert.Contains(auto.Operations, o => o.Kind == OperationKind.Video && o.Destination.EndsWith("Harbour Watch S02E03 - Episode 2-3.mkv", StringComparison.Ordinal));

        var ai = new Ai();
        var transcriber = new Transcriber();
        var held = await PlanShow(Planner(ai, transcriber, askFirst: true));

        Assert.False(held.IsReady);
        var item = Assert.Single(held.Review);
        Assert.True(item.ApprovalOnly);
        var s = item.Suggestion!;
        Assert.True(ReviewChoice.SameTitle(Show, s.Title));
        Assert.True(s.Title.IsSeries);
        Assert.Equal((2, 3), (s.Season!.Value, s.Episode!.Value));
        Assert.Equal("Harbour Watch (2004) S02E03 'Episode 2-3'", s.Describe());
        var d = Assert.Single(s.Decisions);
        Assert.Equal(AiDecisionKind.Transcript, d.Kind);
        Assert.Equal(Heard.Length, d.TranscriptCharacters);
        Assert.Equal(TimeSpan.FromMinutes(5), d.TranscriptFrom);
        Assert.Equal(8, d.Options);
        Assert.Null(d.Confidence);

        // Only a snippet of the transcript is kept, never the whole of it
        Assert.True(d.TranscriptSnippet!.Length <= AiDecision.SnippetLength + 1);
        Assert.StartsWith("Inspector Vale said", d.TranscriptSnippet, StringComparison.Ordinal);
        Assert.NotEqual(Heard, d.TranscriptSnippet);
        Assert.Equal((1, 1), (ai.Calls, transcriber.Calls));
    }

    [Fact]
    public async Task Approving_files_exactly_the_suggestion_without_asking_the_AI_again()
    {
        var store = Store();
        var ai = new Ai();
        var transcriber = new Transcriber();
        var first = await PlanShow(Planner(ai, transcriber, askFirst: true));
        var review = FromPlan(ShowRelease, first);
        store.PutReview(review);
        var shown = Presenter.Match(store.GetReview(review.Id)!).Ai!;

        Assert.IsType<NoContentResult>(ReviewDecisions.Approve(store, review.Id, new ApproveRequest { Key = shown.Key }));

        var approved = store.GetReview(review.Id)!;
        Assert.Equal(ReviewRequest.Retry, approved.Request);
        Assert.Equal((2, 3), (approved.Approved[ShowVideo].Season!.Value, approved.Approved[ShowVideo].Episode!.Value));
        Assert.Contains(store.Snapshot().Activity, a => a.Status == ActivityStatus.Decision && a.Summary == "Approved AI suggestion: Harbour Watch (2004) S02E03 'Episode 2-3'.");

        // The next sweep plans with the approval (still asking first): filed as suggested, nothing asked again
        var second = await PlanShow(Planner(ai, transcriber, askFirst: true, review: approved));

        Assert.True(second.IsReady);
        Assert.Contains(second.Operations, o => o.Kind == OperationKind.Video && o.Destination.EndsWith(Path.Combine("Season 02", "Harbour Watch S02E03 - Episode 2-3.mkv"), StringComparison.Ordinal));
        Assert.Equal((1, 1), (ai.Calls, transcriber.Calls));
        Assert.Equal(1, second.ApprovedAi);
        Assert.Contains(second.Notes, n => n.Contains("Approved AI suggestion: Harbour Watch (2004) S02E03", StringComparison.Ordinal));
        Assert.Equal(FilingChoice.AiApproved, IngestService.ChoiceOf(approved, second));

        // A title chosen in review decides how the library was chosen, even with an approval
        Assert.Equal(FilingChoice.Review, IngestService.ChoiceOf(approved with { Chosen = new ChosenMatch(Show, Tv) }, second));
    }

    [Fact]
    public async Task Approving_a_close_match_files_the_AIs_title_and_asks_nothing()
    {
        var store = Store();
        var ai = new Ai();
        var review = FromPlan(FilmRelease, await PlanFilm(Planner(ai, askFirst: true)));
        store.PutReview(review);
        store.RequestApprove(review.Id, AiApproval.KeyOf(review));

        var plan = await PlanFilm(Planner(ai, askFirst: true, review: store.GetReview(review.Id)));

        Assert.True(plan.IsReady);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Video && o.Destination.Contains("tmdbid-802", StringComparison.Ordinal));
        Assert.Equal(1, ai.Calls);
    }

    [Fact]
    public async Task An_approval_from_a_page_drawn_before_the_suggestion_changed_is_refused()
    {
        var store = Store();
        var review = FromPlan(ShowRelease, await PlanShow(Planner(new Ai(), new Transcriber(), askFirst: true)));
        store.PutReview(review);
        var shown = AiApproval.KeyOf(review)!;

        // The release was planned again meanwhile, and the AI now says episode 4
        var changed = review with { Items = [review.Items[0] with { Suggestion = review.Items[0].Suggestion! with { Episode = 4 } }] };
        store.PutReview(changed);
        var stale = ReviewDecisions.Approve(store, review.Id, new ApproveRequest { Key = shown });
        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(stale).StatusCode);
        Assert.Empty(store.GetReview(review.Id)!.Approved);

        // A decision already waiting for the next sweep is refused too, and a review without a suggestion can't be approved
        store.RequestRetry(review.Id, null);
        Assert.IsType<ConflictObjectResult>(ReviewDecisions.Approve(store, review.Id, new ApproveRequest { Key = AiApproval.KeyOf(changed) }));
        Assert.IsType<NotFoundResult>(ReviewDecisions.Approve(store, "missing", new ApproveRequest { Key = shown }));
        var plain = changed with { Id = "plain", Items = [new PendingReviewItem(ShowVideo, "Too close to call.")] };
        store.PutReview(plain);
        Assert.IsType<BadRequestObjectResult>(ReviewDecisions.Approve(store, "plain", new ApproveRequest { Key = shown }));
    }

    [Fact]
    public async Task A_suggestion_that_duplicates_what_is_on_the_server_is_approved_by_replacing_it()
    {
        var store = Store();
        var ai = new Ai();
        var plan = await PlanFilm(Planner(ai, askFirst: true, onServer: true));

        // The duplicate guard still runs: the item carries both the copy and the suggestion
        var item = Assert.Single(plan.Review);
        Assert.Equal(OnServer.Remake, item.Existing);
        Assert.NotNull(item.Suggestion);
        Assert.False(item.ApprovalOnly);
        var review = FromPlan(FilmRelease, plan);
        store.PutReview(review);
        Assert.False(AiApproval.IsOnlyAi(review));
        var view = Presenter.Match(review);
        Assert.True(view.Ai!.Replaces);
        Assert.False(view.Ai.OnlyAi);
        Assert.Equal("Suggested by the AI: Harbour Lights (2003). It is already in Movies.", view.Headline);

        // Replacing repeats the copies the page showed
        Assert.IsType<ConflictObjectResult>(ReviewDecisions.Approve(store, review.Id, new ApproveRequest { Key = view.Ai.Key, Replace = true, Existing = "/lib/Movies/other.mkv" }));
        Assert.IsType<NoContentResult>(ReviewDecisions.Approve(store, review.Id, new ApproveRequest { Key = view.Ai.Key, Replace = true, Existing = ReviewDecisions.ExistingOf(review) }));
        var approved = store.GetReview(review.Id)!;
        Assert.Equal(ReviewRequest.Replace, approved.Request);

        var filed = await PlanFilm(Planner(ai, askFirst: true, review: approved, onServer: true));
        Assert.True(filed.IsReady);
        Assert.Equal([OnServer.Remake], filed.Replacing);
        Assert.Equal(1, ai.Calls);
        Assert.Equal(FilingChoice.AiApproved, IngestService.ChoiceOf(approved, filed));
    }

    [Fact]
    public async Task Approve_all_takes_only_reviews_that_wait_for_nothing_but_the_AI()
    {
        var store = Store();
        var pure = FromPlan(FilmRelease, await PlanFilm(Planner(new Ai(), askFirst: true)));
        var duplicate = FromPlan("Dup", await PlanFilm(Planner(new Ai(), askFirst: true, onServer: true))) with { Id = "dup", Release = "Dup" };
        var plain = pure with { Id = "plain", Release = "Plain", Items = [new PendingReviewItem("Plain/a.mkv", "Too close to call.")] };
        var stale = FromPlan(ShowRelease, await PlanShow(Planner(new Ai(), new Transcriber(), askFirst: true)));
        foreach (var r in new[] { pure, duplicate, plain, stale })
        {
            store.PutReview(r);
        }

        var outcome = ReviewDecisions.ApproveAll(store,
        [
            new ReviewApproval(pure.Id, AiApproval.KeyOf(pure)!),
            new ReviewApproval(duplicate.Id, AiApproval.KeyOf(duplicate)!),
            new ReviewApproval(plain.Id, "x"),
            new ReviewApproval(stale.Id, "changed since"),
        ]);

        Assert.Equal(ReviewRequest.Retry, store.GetReview(pure.Id)!.Request);
        Assert.Single(store.GetReview(pure.Id)!.Approved);
        foreach (var id in new[] { duplicate.Id, plain.Id, stale.Id })
        {
            Assert.Equal(ReviewRequest.None, store.GetReview(id)!.Request);
            Assert.Empty(store.GetReview(id)!.Approved);
        }

        Assert.StartsWith("Approved 1 AI suggestion (Approve all)", outcome, StringComparison.Ordinal);
        Assert.Contains("3 skipped", outcome, StringComparison.Ordinal);
        var entry = store.Snapshot().Activity[0];
        Assert.Equal(ActivityStatus.Decision, entry.Status);
        Assert.Equal([FilmRelease + ": Approved AI suggestion: Harbour Lights (2003)"], entry.Details);

        // The page offers it only for the pure one
        Assert.True(Presenter.Match(pure).Ai!.OnlyAi);
        Assert.False(Presenter.Match(duplicate).Ai!.OnlyAi);
        Assert.Null(Presenter.Match(plain).Ai);
    }

    [Fact]
    public void Approve_all_is_queued_for_the_sweep_and_reports_its_count()
    {
        var progress = new IngestProgress();
        var action = new QueuedAction { Kind = QueuedActionKind.ApproveAll, Approvals = [new ReviewApproval("a", "k"), new ReviewApproval("b", "k")] };

        Assert.True(progress.Queue(action));

        var queued = Assert.Single(progress.Queued());
        Assert.Equal(2, queued.Count);
        Assert.DoesNotContain("Approvals", System.Text.Json.JsonSerializer.Serialize(queued), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_card_says_what_the_AI_suggests_why_from_what_and_how_sure()
    {
        var film = FromPlan(FilmRelease, await PlanFilm(Planner(new Ai(), askFirst: true)));
        film = film with { Candidates = [new ScoredCandidate(Newer, 1.025), new ScoredCandidate(Older, 1.05)] };

        var match = Presenter.Match(film);
        Assert.Equal("Suggested by the AI: Harbour Lights (2003).", match.Headline);
        Assert.Equal("Wrong? Pick another", match.PickHeading);
        Assert.Equal(new[] { CandidateState.InUse, CandidateState.Alternative }, match.Candidates);
        var ai = match.Ai!;
        Assert.Equal(AiApproval.KeyOf(film), ai.Key);
        Assert.Equal("AI (test-model)", ai.By);
        Assert.Equal("The release group only encodes the remake.", ai.Reason);
        Assert.Equal("Picked Harbour Lights (2003) from 2 close candidates.", ai.Basis);
        Assert.Matches(@"^Name match \d+%$", ai.Confidence);
        Assert.Equal(0, ai.More);
        Assert.True(ai.OnlyAi);
        Assert.False(ai.Replaces);
        Assert.False(ai.Episode);

        var view = Presenter.Review(film);
        Assert.Equal("The AI suggests Harbour Lights (2003). Approve it, or pick another.", view.Summary);
        Assert.Contains(view.Chips, c => c.Icon == Icons.Ai && c.Label == "Suggested by the AI; waiting for your approval");
        Assert.Contains(view.Details, d => d.Group == "AI suggestion" && d.Label == "Why" && d.Value == "The release group only encodes the remake.");

        // From a transcript: how much was heard and from where, a snippet in the details only, never the transcript
        var show = FromPlan(ShowRelease, await PlanShow(Planner(new Ai(), new Transcriber(), askFirst: true)));
        var episode = Presenter.Match(show).Ai!;
        Assert.Equal($"Picked S02E03 'Episode 2-3' from 8 episodes by comparing {Heard.Length:N0} characters of transcript (from 5:00 in) with their synopses.", episode.Basis);
        Assert.Equal("The name says nothing about the episode; the AI went by what is said in it.", episode.Confidence);
        Assert.True(episode.Episode);
        var details = Presenter.Review(show).Details;
        var snippet = Assert.Single(details, d => d.Label == "Transcript begins").Value;
        Assert.True(snippet.Length <= AiDecision.SnippetLength + 3);
        Assert.DoesNotContain(details, d => d.Value.Contains(Heard, StringComparison.Ordinal));
        Assert.DoesNotContain(Heard, episode.Basis + episode.Reason + episode.Confidence, StringComparison.Ordinal);

        // Once approved, the card says so
        Assert.Equal("Trying again on the next sweep.", Presenter.Review(AiApproval.Approve(show, replace: false)).Summary);
    }

    [Fact]
    public void A_review_without_a_suggestion_reads_as_before()
    {
        var film = new PendingReview
        {
            Id = "f",
            WatchFolder = Watch,
            Release = FilmRelease,
            Time = DateTimeOffset.UnixEpoch,
            Items = [new PendingReviewItem(FilmVideo, "already on the server") { Existing = OnServer.Remake }],
            Candidates = [new ScoredCandidate(Newer, 0.9)],
            Matched = Newer,
        };

        var match = Presenter.Match(film);

        Assert.Null(match.Ai);
        Assert.Equal("Matched to Harbour Lights (2003). It is already in Movies.", match.Headline);
        Assert.Equal("Wrong film? Pick another", match.PickHeading);
    }

    [Fact]
    public async Task Choosing_another_title_or_retrying_drops_the_approval_and_planning_again_keeps_it()
    {
        var store = Store();
        var review = FromPlan(FilmRelease, await PlanFilm(Planner(new Ai(), askFirst: true)));
        store.PutReview(review);
        store.RequestApprove(review.Id, AiApproval.KeyOf(review));
        var approved = store.GetReview(review.Id)!;

        // Planning again (say the file was busy) keeps the approval, and it survives a restart
        store.PutReview(review with { Items = [new PendingReviewItem(FilmVideo, "Destination already exists: x")] }, approved.RequestVersion);
        Assert.Single(new IngestStateStore(Path.Combine(_dir, "state.json")).GetReview(review.Id)!.Approved);

        store.RequestRetry(review.Id, new ChosenMatch(Older, Films));
        Assert.Empty(store.GetReview(review.Id)!.Approved);
    }

    [Fact]
    public async Task A_suggestion_waiting_for_approval_survives_a_restart()
    {
        var store = Store();
        var review = FromPlan(ShowRelease, await PlanShow(Planner(new Ai(), new Transcriber(), askFirst: true)));
        store.PutReview(review);

        var reloaded = new IngestStateStore(Path.Combine(_dir, "state.json")).GetReview(review.Id)!;

        Assert.Equal(AiApproval.KeyOf(review), AiApproval.KeyOf(reloaded));
        var d = reloaded.Items[0].Suggestion!.Decisions[0];
        Assert.Equal(AiDecisionKind.Transcript, d.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), d.TranscriptFrom);
        Assert.True(reloaded.Items[0].ApprovalOnly);
    }

    [Fact]
    public void A_filing_of_an_approved_suggestion_can_still_be_moved_to_another_library()
    {
        var filing = new ActivityEntry
        {
            Time = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero),
            Status = ActivityStatus.Filed,
            Release = FilmRelease,
            WatchFolder = Watch,
            Run = "run1",
            ChosenBy = FilingChoice.AiApproved,
            Details = [FilmVideo + ": Approved AI suggestion: Harbour Lights (2003)."],
        };

        Assert.True(LibraryMove.IsAutomatic(filing, [filing]));
        Assert.Null(LibraryMove.Ineligible(filing, [filing], filing.Time.AddDays(1)));
        var view = Presenter.Activity(filing, filing.Time.AddDays(1));
        Assert.True(view.CanMove);
        Assert.Contains("You approved the AI's suggestion.", view.Item.Summary, StringComparison.Ordinal);
        Assert.Contains(view.Item.Chips, c => c.Label == "Approved AI suggestion (see the details)");
    }

    [Fact]
    public void Existing_and_new_watch_folders_file_automatically_when_the_AI_decides()
    {
        var serializer = new XmlSerializer(typeof(WatchFolder));
        using (var reader = new StringReader("<WatchFolder><Path>/in</Path><Enabled>true</Enabled><DryRun>false</DryRun></WatchFolder>"))
        {
            Assert.Equal(AiDecisionMode.FileAutomatically, ((WatchFolder)serializer.Deserialize(reader)!).WhenAiDecides);
        }

        Assert.Equal(AiDecisionMode.FileAutomatically, new WatchFolder().WhenAiDecides);

        using var writer = new StringWriter();
        serializer.Serialize(writer, new WatchFolder { Path = "/in", WhenAiDecides = AiDecisionMode.AskFirst });
        using var back = new StringReader(writer.ToString());
        Assert.Equal(AiDecisionMode.AskFirst, ((WatchFolder)serializer.Deserialize(back)!).WhenAiDecides);
    }
}
