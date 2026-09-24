using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Parsing;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented titles and ids throughout.
public class MediaIdentifierTests
{
    private static MetadataCandidate C(string name, int? year, string tmdb, string? tvdb = null)
    {
        var ids = new Dictionary<string, string> { ["Tmdb"] = tmdb };
        if (tvdb is not null)
        {
            ids["Tvdb"] = tvdb;
        }

        return new MetadataCandidate { Name = name, Year = year, ProviderIds = ids };
    }

    private sealed class FakeLookup : IMetadataLookup
    {
        public List<MetadataCandidate> Series { get; } = [];

        public List<MetadataCandidate> Movies { get; } = [];

        public Dictionary<(int, int), string> Episodes { get; } = [];

        public List<string> Queries { get; } = [];

        private static bool Matches(MetadataCandidate c, string name, int? year)
            => TitleMatcher.Similarity(name, c.Name) >= 0.6 && (year is null || c.Year == year);

        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
        {
            Queries.Add($"tv:{name}:{year}");
            return Task.FromResult<IReadOnlyList<MetadataCandidate>>([.. Series.Where(c => Matches(c, name, year))]);
        }

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
        {
            Queries.Add($"movie:{name}:{year}");
            return Task.FromResult<IReadOnlyList<MetadataCandidate>>([.. Movies.Where(c => Matches(c, name, year))]);
        }

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult(Episodes.TryGetValue((season, episode), out var t) ? t : null);
    }

    private sealed class FakeLibrary(params MetadataCandidate[] series) : ILibraryIndex
    {
        public Task<IReadOnlyList<MetadataCandidate>> FindSeriesAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([.. series.Where(s => TitleMatcher.Similarity(name, s.Name) >= 0.7)]);

        public Task<IReadOnlyList<MetadataCandidate>> FindMoviesAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);
    }

    private static Task<IdentificationResult> Identify(IMetadataLookup lookup, string fileName, ILibraryIndex? library = null)
        => new MediaIdentifier(lookup, library).IdentifyAsync(ReleaseNameParser.Parse(fileName), preferTv: false, CancellationToken.None);

    [Fact]
    public async Task Identifies_an_episode_and_fetches_its_title()
    {
        var lookup = new FakeLookup();
        lookup.Series.Add(C("Lantern", 2001, "100", "200"));
        lookup.Episodes[(1, 4)] = "Glass Harbour";

        var r = await Identify(lookup, "n00b-Lantern1E4.mp4");

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal("100", r.Episode!.Series.TmdbId);
        Assert.Equal("200", r.Episode.Series.TvdbId);
        Assert.Equal("Glass Harbour", r.Episode.Title);
    }

    [Fact]
    public async Task Tolerates_a_typo_in_the_release_name()
    {
        var lookup = new FakeLookup();
        lookup.Series.Add(C("Farscope", 1999, "1"));

        var r = await Identify(lookup, "Fasrcope Season 04 Episode 01 - Part Two.mkv");

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal("Farscope", r.Episode!.Series.Title);
    }

    [Fact]
    public async Task Same_named_shows_without_a_year_go_to_review()
    {
        var lookup = new FakeLookup();
        lookup.Series.AddRange([C("Time Doctor", 1963, "1"), C("Time Doctor", 2005, "2")]);

        var r = await Identify(lookup, "Time.Doctor.S01E02.mkv");

        Assert.Equal(IdentificationStatus.NeedsReview, r.Status);
        Assert.Null(r.Episode);
    }

    [Fact]
    public async Task A_show_already_in_the_library_breaks_the_tie()
    {
        var lookup = new FakeLookup();
        lookup.Series.AddRange([C("Time Doctor", 1963, "1"), C("Time Doctor", 2005, "2")]);
        var library = new FakeLibrary(C("Time Doctor", 2005, "2"));

        var r = await Identify(lookup, "Time.Doctor.S01E02.mkv", library);

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal("2", r.Episode!.Series.TmdbId);
    }

    [Fact]
    public async Task The_year_in_the_name_picks_the_right_one()
    {
        var lookup = new FakeLookup();
        lookup.Series.AddRange([C("Time Doctor", 1963, "1"), C("Time Doctor", 2005, "2")]);

        var r = await Identify(lookup, "Time Doctor (2005) S01E02.mkv");

        Assert.Equal("2", r.Episode!.Series.TmdbId);
    }

    [Fact]
    public async Task Sequel_numbers_must_agree()
    {
        var lookup = new FakeLookup();
        lookup.Movies.AddRange([C("Deadly Partner 3", 1992, "3"), C("Deadly Partner 4", 1998, "4")]);

        var r = await Identify(lookup, "Deadly Partner 4 (1998).mkv");

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal("4", r.Movie!.TmdbId);
    }

    [Fact]
    public async Task Wrong_year_in_the_name_still_finds_the_film()
    {
        var lookup = new FakeLookup();
        lookup.Movies.Add(C("Family Gathering", 2011, "9"));

        var r = await Identify(lookup, "Family.Gathering.2010.DVDRip.avi");

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal("9", r.Movie!.TmdbId);
        Assert.Contains(lookup.Queries, q => q == "movie:Family Gathering:");
    }

    [Fact]
    public async Task Unknown_titles_are_not_found_and_never_guessed()
    {
        var r = await Identify(new FakeLookup(), "Nothing Like This Exists (2020).mkv");

        Assert.Equal(IdentificationStatus.NotFound, r.Status);
        Assert.Null(r.Movie);
    }

    [Fact]
    public async Task Specials_without_a_number_need_review()
    {
        var lookup = new FakeLookup();
        lookup.Series.Add(C("Lantern", 2001, "100"));

        var r = await Identify(lookup, "Lantern S03SP1 A Special.mp4");

        Assert.Equal(IdentificationStatus.NeedsReview, r.Status);
        Assert.Contains("A Special", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_hits_from_two_providers_are_merged_with_all_ids()
    {
        var merged = MediaIdentifier.Merge(
        [
            new MetadataCandidate { Name = "Lantern", Year = 2001, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "100" } },
            new MetadataCandidate { Name = "Lantern", Year = 2001, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "200", ["Tmdb"] = "100" } },
        ]);

        var only = Assert.Single(merged);
        Assert.Equal("200", only.ProviderIds["Tvdb"]);
    }
    [Fact]
    public async Task Prefers_a_tmdb_match_over_same_named_imdb_only_hits()
    {
        var lookup = new FakeLookup();
        lookup.Series.Add(C("Night Shift Club", 2022, "300", "400"));
        lookup.Series.Add(new MetadataCandidate { Name = "Night Shift Club", Year = 2014, ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0000001" }, ProviderRank = 0 });
        lookup.Series.Add(new MetadataCandidate { Name = "Night Shift Club", Year = 2019, ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0000002" }, ProviderRank = 1 });
        lookup.Episodes[(1, 1)] = "Opening";

        var r = await Identify(lookup, "Night.Shift.Club.S01E01.mkv");

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal("300", r.Episode!.Series.TmdbId);
    }

    [Fact]
    public void Imdb_only_hits_score_lower_but_library_hits_do_not()
    {
        var imdbOnly = new MetadataCandidate { Name = "Quiet Harbour", Year = 2010, ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0000003" } };
        var tmdb = C("Quiet Harbour", 2010, "500");
        Assert.True(MediaIdentifier.IsImdbOnly(imdbOnly));
        Assert.False(MediaIdentifier.IsImdbOnly(tmdb));
        Assert.False(MediaIdentifier.IsImdbOnly(imdbOnly with { Source = MediaIdentifier.LibrarySource }));
        Assert.Equal(
            MediaIdentifier.Score("Quiet Harbour", 2010, tmdb) - MediaIdentifier.ImdbOnlyPenalty,
            MediaIdentifier.Score("Quiet Harbour", 2010, imdbOnly),
            precision: 6);
    }
}
