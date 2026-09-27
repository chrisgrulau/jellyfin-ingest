using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Parsing;
using Jellyfin.Plugin.Ingest.Planning;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// A release the providers know as a film that the TV library already has as a show's episode (a TV film or special kept
// in Season 00): it is that episode, so it replaces the copy there (when asked) rather than being filed again as a film.
// Modelled on "24: Redemption" (2008), kept as 24 S00E09 'Redemption'.
public class FilmAsEpisodeTests
{
    private const string Watch = "/drop/incoming";
    private const string Quarantine = "/drop/incoming/.ingest-quarantine";
    private const string ShowFolder = "/lib/Shows/24 (2001) [tvdbid-76290] [tmdbid-1973]";
    private const string Specials = ShowFolder + "/Season 00";
    private const string OldCopy = Specials + "/24 S00E09 - Redemption.avi";
    private const string Release = "24.Redemption.2008.1080p.BluRay.x264-SHORTBREHD[rarbg]";
    private const string Video = Release + "/24.Redemption.2008.1080p.BluRay.x264-SHORTBREHD.mkv";

    private static readonly LibraryTarget Tv = new("/lib/Shows", IsTv: true);
    private static readonly LibraryTarget Films = new("/lib/Movies", IsTv: false);
    private static readonly LibraryTargets Both = new(Tv, Films);
    private static readonly MediaLibrary[] Libraries =
    [
        new("m", "Movies", LibraryKind.Films, ["/lib/Movies"]),
        new("s", "TV Series", LibraryKind.Shows, ["/lib/Shows"]),
    ];

    private static readonly MetadataCandidate Film = new()
    {
        Name = "24: Redemption",
        Year = 2008,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1233", ["Imdb"] = "tt0813980" },
    };

    private static readonly MetadataCandidate Show = new()
    {
        Name = "24",
        Year = 2001,
        IsSeries = true,
        ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "76290", ["Tmdb"] = "1973" },
    };

    private static ExistingEpisode Special(int? year = 2008, string title = "Redemption", string? imdb = null) => new()
    {
        Path = OldCopy,
        SeriesName = "24",
        SeriesYear = 2001,
        SeriesProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = "76290", ["Tmdb"] = "1973" },
        Season = 0,
        Episode = 9,
        Title = title,
        Year = year,
        ProviderIds = imdb is null ? new Dictionary<string, string>() : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = imdb },
    };

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Lookup : IMetadataLookup
    {
        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>(TitleMatcher.Similarity(name, "24") > 0.9 ? [Show] : []);

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>(TitleMatcher.Similarity(name, "24: Redemption") > 0.9 ? [Film] : []);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EpisodeListing>>([]);
    }

    private sealed class Existing(params ExistingEpisode[] episodes) : IExistingMedia
    {
        public List<IReadOnlyCollection<string>> Asked { get; } = [];

        public string? FindSeriesFolder(IReadOnlyDictionary<string, string> seriesProviderIds)
            => seriesProviderIds.TryGetValue("Tvdb", out var id) && id == "76290" ? ShowFolder : null;

        public string? FindEpisode(IReadOnlyDictionary<string, string> seriesProviderIds, string seriesFolder, int season, int episode)
            => episodes.FirstOrDefault(e => e.Season == season && e.Episode == episode)?.Path;

        public string? FindMovie(IReadOnlyDictionary<string, string> movieProviderIds, string? edition, string plannedPath) => null;

        public string? FindSeasonFolder(string seriesFolder, int season) => season == 0 ? Specials : null;

        public IReadOnlyList<ExistingEpisode> FindEpisodesTitled(IReadOnlyCollection<string> titles)
        {
            Asked.Add(titles);
            return [.. episodes.Where(e => FilmAsEpisode.TitleFits(titles, e.SeriesName, e.Title))];
        }
    }

    private static IngestPlanner Planner(IExistingMedia existing, bool replace = false) => new(
        new MediaIdentifier(new Lookup()),
        p => !Path.HasExtension(p) || p == OldCopy,
        _ => null,
        new FixedClock(),
        existing,
        p => PathGuard.IsUnder(p, "/lib"))
    {
        Libraries = Libraries,
        ReplaceExisting = replace,
    };

    private static Task<IngestPlan> Plan(IngestPlanner planner, ChosenMatch? chosen = null)
        => planner.PlanAsync(Watch, Release, [new ReleaseFile(Video, 8_000_000_000), new ReleaseFile(Release + "/RARBG.txt", 30)], Both, Quarantine, chosen, CancellationToken.None);

    [Theory]
    [InlineData("24 Redemption", "24", "REDEMPTION")]
    [InlineData("24: Redemption", "24", "REDEMPTION")]
    [InlineData("The Office: The Accountants", "The Office", "THE ACCOUNTANTS")]
    [InlineData("240 Robots", "24", null)]
    [InlineData("24", "24", null)]
    [InlineData("Redemption", "24", null)]
    public void The_rest_of_a_title_follows_the_show_name_on_a_word_boundary(string title, string series, string? rest)
        => Assert.Equal(rest, FilmAsEpisode.Rest(title, series));

    [Fact]
    public void Title_and_year_agreeing_is_confident()
        => Assert.Equal(EpisodeMatchStrength.Confident, FilmAsEpisode.Assess(["24: Redemption", "24 Redemption"], [2008], null, Special()));

    [Fact]
    public void Title_alone_is_only_possible()
    {
        Assert.Equal(EpisodeMatchStrength.Possible, FilmAsEpisode.Assess(["24: Redemption"], [2011], null, Special()));
        Assert.Equal(EpisodeMatchStrength.Possible, FilmAsEpisode.Assess(["24: Redemption"], [2008], null, Special(year: null)));
    }

    [Fact]
    public void The_same_imdb_id_is_confident_whatever_the_titles()
        => Assert.Equal(EpisodeMatchStrength.Confident, FilmAsEpisode.Assess(["Something Else"], [], "tt0813980", Special(title: "Day Zero", imdb: "TT0813980")));

    [Fact]
    public void Another_title_is_no_match()
    {
        Assert.Equal(EpisodeMatchStrength.None, FilmAsEpisode.Assess(["24: Legacy"], [2008], "tt1", Special()));
        Assert.Null(FilmAsEpisode.Best(["Rocket Club"], [2019], null, [Special()]));
    }

    [Fact]
    public void Two_episodes_that_fit_equally_are_only_possible()
    {
        var other = Special() with { Path = Specials + "/24 S00E10 - Redemption.avi", Episode = 10 };

        var best = FilmAsEpisode.Best(["24: Redemption"], [2008], null, [Special(), other]);

        Assert.Equal(EpisodeMatchStrength.Possible, best!.Value.Strength);
    }

    [Fact]
    public async Task A_film_that_is_a_special_on_the_server_waits_to_replace_it_instead_of_being_filed_as_a_film()
    {
        var plan = await Plan(Planner(new Existing(Special())));

        Assert.False(plan.IsReady);
        Assert.Empty(plan.Operations);
        var item = Assert.Single(plan.Review);
        Assert.Equal(OldCopy, item.Existing);
        Assert.Contains("Identified as the film '24: Redemption' (2008), but it is 24 S00E09 'Redemption' (same title and year)", item.Reason, StringComparison.Ordinal);
        Assert.Contains("Replace it", item.Reason, StringComparison.Ordinal);
        Assert.True(item.Matched!.IsSeries);
        Assert.Equal("24", item.Matched.Name);

        // The film stays on offer, to file it as a film instead
        Assert.Contains(item.Candidates, c => !c.Candidate.IsSeries && c.Candidate.Name == "24: Redemption");
    }

    [Fact]
    public async Task Replacing_files_it_in_the_episodes_slot_with_its_name_and_quarantines_the_old_copy()
    {
        var plan = await Plan(Planner(new Existing(Special()), replace: true));

        Assert.True(plan.IsReady);
        Assert.Equal([OldCopy], plan.Replacing);
        Assert.Equal(OldCopy, plan.Operations[0].Source);
        Assert.StartsWith(Path.Combine(Quarantine, "2026-09-28", "Replaced"), plan.Operations[0].Destination, StringComparison.Ordinal);
        var video = Assert.Single(plan.Operations, o => o.Kind == OperationKind.Video);
        Assert.Equal(Specials + "/24 S00E09 - Redemption.mkv", video.Destination);
        Assert.DoesNotContain(plan.Operations, o => o.Destination.StartsWith("/lib/Movies", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_year_that_disagrees_still_asks_but_says_it_only_may_be_the_episode()
    {
        var plan = await Plan(Planner(new Existing(Special(year: 2011))));

        var item = Assert.Single(plan.Review);
        Assert.Contains("it may be 24 S00E09 'Redemption'", item.Reason, StringComparison.Ordinal);
        Assert.Contains("the year doesn't agree", item.Reason, StringComparison.Ordinal);
        Assert.Equal(OldCopy, item.Existing);
    }

    [Fact]
    public async Task Choosing_the_film_in_review_files_it_as_a_film()
    {
        var existing = new Existing(Special());

        var plan = await Plan(Planner(existing), new ChosenMatch(Film, Films));

        Assert.True(plan.IsReady);
        var video = Assert.Single(plan.Operations, o => o.Kind == OperationKind.Video);
        Assert.StartsWith("/lib/Movies/24 - Redemption (2008)", video.Destination, StringComparison.Ordinal);
        Assert.Empty(plan.Replacing);
        Assert.Empty(existing.Asked);
    }

    [Fact]
    public async Task Choosing_the_show_in_review_finds_its_episode_by_title()
    {
        var plan = await Plan(Planner(new Existing(Special()), replace: true), new ChosenMatch(Show, Tv));

        Assert.True(plan.IsReady);
        Assert.Equal([OldCopy], plan.Replacing);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Video && o.Destination.EndsWith("/24 S00E09 - Redemption.mkv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_film_with_no_such_episode_is_filed_as_a_film()
    {
        var plan = await Plan(Planner(new Existing()));

        Assert.True(plan.IsReady);
        Assert.StartsWith("/lib/Movies/", Assert.Single(plan.Operations, o => o.Kind == OperationKind.Video).Destination, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_name_reads_as_a_film_titled_24_Redemption()
    {
        var parsed = ReleaseNameParser.Parse(Video);

        Assert.Equal("24 Redemption", parsed.Title);
        Assert.Equal(2008, parsed.Year);
        Assert.Null(parsed.Episode);
    }
}
