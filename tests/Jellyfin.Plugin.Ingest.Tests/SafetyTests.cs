using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Naming;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented titles and ids throughout.
public class SafetyTests
{
    [Theory]
    [InlineData("Tmdb", "95396", true)]
    [InlineData("Tvdb", "371980", true)]
    [InlineData("Imdb", "tt0111161", true)]
    [InlineData("Tmdb", "1]/../../srv/x", false)]
    [InlineData("Tmdb", "12a", false)]
    [InlineData("Tvdb", "", false)]
    [InlineData("Imdb", "0111161", false)]
    [InlineData("Imdb", "tt1/../x", false)]
    [InlineData("TvMaze", "abc-123", true)]
    [InlineData("TvMaze", "a/b", false)]
    public void Provider_ids_must_have_the_expected_shape(string provider, string value, bool valid)
        => Assert.Equal(valid, ProviderIdRules.IsValid(provider, value));

    [Fact]
    public void Invalid_ids_are_dropped_and_valid_ones_kept()
    {
        var clean = ProviderIdRules.Clean(new Dictionary<string, string> { ["Tmdb"] = "1]/..", ["Imdb"] = "tt1234567", ["Tvdb"] = "42" });
        Assert.Equal(["Imdb", "Tvdb"], [.. clean.Keys]);
    }

    [Theory]
    [InlineData("1]/../../../../srv/x")]
    [InlineData("..\\..\\x")]
    [InlineData("1] [tmdbid-2")]
    public void Crafted_ids_never_reach_a_folder_name(string id)
    {
        var film = MediaNamer.MovieFolderName(new MovieIdentity { Title = "Rocket Club", Year = 2019, TmdbId = id });
        var show = MediaNamer.SeriesFolderName(new SeriesIdentity { Title = "Lantern", Year = 2001, TvdbId = id, TmdbId = id });
        Assert.Equal("Rocket Club (2019)", film);
        Assert.Equal("Lantern (2001)", show);
    }

    [Theory]
    [InlineData("/lib/Movies/x", "/lib/Movies", true)]
    [InlineData("/lib/Movies/a/../b", "/lib/Movies", true)]
    [InlineData("/lib/Movies/../../etc", "/lib/Movies", false)]
    [InlineData("/lib/MoviesExtra/x", "/lib/Movies", false)]
    [InlineData("/lib/Movies", "/lib/Movies", false)]
    public void Paths_are_contained_by_folder_after_normalising(string path, string root, bool under)
        => Assert.Equal(under, PathGuard.IsUnder(path, root));

    private static PendingReview Review() => new()
    {
        Id = "r",
        WatchFolder = "/drop",
        Release = "x",
        Time = DateTimeOffset.UnixEpoch,
        Candidates = [new ScoredCandidate(new MetadataCandidate { Name = "A", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" } }, 0.9)],
        SearchResults = [new ScoredCandidate(new MetadataCandidate { Name = "B", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "2]/..", ["Imdb"] = "tt1234567" } }, 0.8)],
    };

    [Fact]
    public void A_choice_is_resolved_from_the_server_s_own_lists()
    {
        Assert.Equal("A", ReviewChoice.Pick(Review(), ReviewChoice.Suggested, 0)?.Name);
        var b = ReviewChoice.Pick(Review(), ReviewChoice.Search, 0);
        Assert.Equal("B", b?.Name);
        Assert.Equal(["Imdb"], [.. b!.ProviderIds.Keys]);
    }

    [Theory]
    [InlineData("suggested", 1)]
    [InlineData("suggested", -1)]
    [InlineData("search", 5)]
    [InlineData("anything", 0)]
    [InlineData(null, 0)]
    public void Unknown_lists_and_positions_are_refused(string? list, int index)
        => Assert.Null(ReviewChoice.Pick(Review(), list, index));

    [Theory]
    [InlineData("/in", "/in/", true)]
    [InlineData("/in/./a/..", "/in", true)]
    [InlineData("/in", "/inbox", false)]
    [InlineData("/in", "", false)]
    public void Paths_are_compared_after_normalising(string a, string b, bool same)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(same, PathGuard.SamePath(a, b));
    }

    [Theory]
    [InlineData(" /in/ ", "/in")]
    [InlineData("/", "/")]
    [InlineData("relative/", "relative/")]
    [InlineData(null, "")]
    public void Configured_folders_are_tidied(string? input, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, PathGuard.Tidy(input));
    }
}
