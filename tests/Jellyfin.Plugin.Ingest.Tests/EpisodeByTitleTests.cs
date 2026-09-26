using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Ai;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Parsing;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Files that name an episode by title only (specials such as "S13SP2 A Special Title"). Invented titles throughout.
public class EpisodeByTitleTests
{
    private const string File = "Harbour Watch S13SP2 The Lighthouse Keeper.mkv";

    private static readonly MetadataCandidate Show = new()
    {
        Name = "Harbour Watch",
        Year = 2004,
        IsSeries = true,
        ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "71", ["Tmdb"] = "72" },
    };

    private static readonly EpisodeListing[] Specials =
    [
        new(0, 1, "Storm Night", 2005, "A storm cuts the town off."),
        new(0, 2, "The Lighthouse Keeper", 2019, "An old keeper returns to the island."),
        new(0, 3, "The Lighthouse Keeper's Daughter", 2021, "Years later, his daughter takes the post."),
        new(0, 4, "Christmas at the Quay", 2020, new string('x', 500)),
    ];

    private sealed class Lookup(params EpisodeListing[] specials) : IMetadataLookup
    {
        public int Listings { get; private set; }

        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([Show]);

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
        {
            Listings++;
            return Task.FromResult<IReadOnlyList<EpisodeListing>>(season == 0 ? specials : []);
        }
    }

    private sealed class Picker(Func<IReadOnlyList<EpisodeListing>, TiebreakPick> answer) : ITiebreaker, IEpisodePicker
    {
        public IReadOnlyList<EpisodeListing>? Offered { get; private set; }

        public Task<TiebreakPick> PickAsync(ParsedRelease release, string fileName, IReadOnlyList<ScoredCandidate> options, CancellationToken cancellationToken)
            => Task.FromResult(new TiebreakPick(null, string.Empty, null));

        public Task<TiebreakPick> PickEpisodeAsync(string fileName, string series, string episodeTitle, int? year, IReadOnlyList<EpisodeListing> options, CancellationToken cancellationToken)
        {
            Offered = options;
            return Task.FromResult(answer(options));
        }
    }

    private static Task<IdentificationResult> Identify(IMetadataLookup lookup, string file, ITiebreaker? picker = null)
        => new MediaIdentifier(lookup, null, picker).IdentifyAsync(ReleaseNameParser.Parse(file), preferTv: false, CancellationToken.None, file);

    [Fact]
    public async Task A_clear_title_match_files_the_special_without_asking_anyone()
    {
        var picker = new Picker(_ => throw new InvalidOperationException("not asked"));

        var r = await Identify(new Lookup(Specials), File, picker);

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal(0, r.Episode!.Season);
        Assert.Equal(2, r.Episode.Episode);
        Assert.Equal("The Lighthouse Keeper", r.Episode.Title);
        Assert.Null(r.DecidedBy);
        Assert.Contains("found by its title", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unclear_title_is_left_to_the_picker_which_may_only_choose_a_listed_episode()
    {
        var picker = new Picker(options => new TiebreakPick(options.ToList().FindIndex(o => o.Episode == 4), "Christmas special of that year.", "AI (test)"));

        var r = await Identify(new Lookup(Specials), "Harbour Watch S13SP1 Christmas Special 2020.mkv", picker);

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal(4, r.Episode!.Episode);
        Assert.Equal("AI (test)", r.DecidedBy);
        Assert.Contains("chosen by AI (test)", r.Reason, StringComparison.Ordinal);
        Assert.Equal(2020, picker.Offered!.First().Year);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(9)]
    public async Task Without_a_valid_pick_it_waits_for_review(int? index)
    {
        var r = await Identify(new Lookup(Specials), "Harbour Watch S13SP1 Christmas Special 2020.mkv", new Picker(_ => new TiebreakPick(index, "None fits.", "AI (test)")));

        Assert.Equal(IdentificationStatus.NeedsReview, r.Status);
        Assert.Null(r.DecidedBy);
        Assert.Contains("None fits.", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Near_identical_titles_are_not_guessed()
    {
        var specials = new EpisodeListing[] { new(0, 1, "The Lighthouse Keeper Part 1", 2019, null), new(0, 2, "The Lighthouse Keeper Part 2", 2019, null) };

        var r = await Identify(new Lookup(specials), File);

        Assert.Equal(IdentificationStatus.NeedsReview, r.Status);
        Assert.Contains("No episode of season 0 clearly has that title", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_episode_list_says_so()
    {
        var r = await Identify(new Lookup(), File);

        Assert.Equal(IdentificationStatus.NeedsReview, r.Status);
        Assert.Contains("no episode list for season 0", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_series_chosen_in_review_also_finds_the_special_by_title()
    {
        var identifier = new MediaIdentifier(new Lookup(Specials), null);

        var r = await identifier.IdentifyAsChosenAsync(ReleaseNameParser.Parse(File), Show, isTv: true, CancellationToken.None, File);

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal(2, r.Episode!.Episode);
        Assert.Contains("Chosen in review", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Season_lists_are_remembered()
    {
        var inner = new Lookup(Specials);
        var cache = new CachingMetadataLookup(inner, TimeProvider.System);

        await cache.ListSeasonAsync(Show.ProviderIds, 0, TestContext.Current.CancellationToken);
        var again = await cache.ListSeasonAsync(Show.ProviderIds, 0, TestContext.Current.CancellationToken);

        Assert.Equal(1, inner.Listings);
        Assert.Equal(4, again.Count);
    }

    [Fact]
    public async Task The_AI_is_sent_titles_years_and_short_synopses_under_its_own_purpose()
    {
        string? purpose = null, sent = null;
        var ai = new AiTiebreaker((caller, p, instructions, data, schema, max, effort, ct) =>
        {
            purpose = p;
            sent = JsonSerializer.Serialize(data);
            return Task.FromResult(new AiReply(true, JsonDocument.Parse("{\"choice\":3,\"reason\":\"x\"}").RootElement.Clone(), "m", null, null));
        });

        var pick = await ai.PickEpisodeAsync(File, "Harbour Watch", "Christmas Special", 2020, Specials, TestContext.Current.CancellationToken);

        Assert.Equal(3, pick.Index);
        Assert.Equal("ingest.episode", purpose);
        using var doc = JsonDocument.Parse(sent!);
        var episodes = doc.RootElement.GetProperty("episodes");
        Assert.Equal("S00E02", episodes[1].GetProperty("code").GetString());
        Assert.True(episodes[3].GetProperty("synopsis").GetString()!.Length <= 201);
        Assert.DoesNotContain("Tvdb", sent, StringComparison.Ordinal);
    }
}
