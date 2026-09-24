using Jellyfin.Plugin.Ingest.Planning;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class LibraryRoutingTests
{
    private static readonly MediaLibrary Movies = new("m", "Movies", LibraryKind.Films, ["/lib/Movies"]);
    private static readonly MediaLibrary OtherMovies = new("m2", "Other Movies", LibraryKind.Films, ["/lib/Movies2"]);
    private static readonly MediaLibrary Shows = new("t", "Shows", LibraryKind.Shows, ["/lib/TV", "/lib/TV2"]);
    private static readonly MediaLibrary Mixed = new("x", "Mixed", LibraryKind.Mixed, ["/lib/Mixed"]);
    private static readonly MediaLibrary Music = new("mu", "Music", LibraryKind.Other, ["/lib/Music"]);
    private static readonly MediaLibrary[] All = [Movies, OtherMovies, Shows, Mixed, Music];

    [Theory]
    [InlineData("movies", LibraryKind.Films)]
    [InlineData("tvshows", LibraryKind.Shows)]
    [InlineData("mixed", LibraryKind.Mixed)]
    [InlineData(null, LibraryKind.Mixed)]
    [InlineData("music", LibraryKind.Other)]
    [InlineData("musicvideos", LibraryKind.Other)]
    [InlineData("homevideos", LibraryKind.Other)]
    [InlineData("books", LibraryKind.Other)]
    public void Collection_types_map_to_kinds(string? type, LibraryKind kind)
        => Assert.Equal(kind, LibraryRouting.KindOf(type));

    [Fact]
    public void Each_library_kind_gives_its_targets()
    {
        Assert.Equal(new LibraryTargets(null, new LibraryTarget("/lib/Movies", false)), LibraryRouting.TargetsOf(Movies, null));
        Assert.Equal(new LibraryTargets(new LibraryTarget("/lib/TV2", true), null), LibraryRouting.TargetsOf(Shows, "/lib/TV2"));
        Assert.Equal(new LibraryTargets(new LibraryTarget("/lib/TV", true), null), LibraryRouting.TargetsOf(Shows, "/not/a/library/folder"));
        Assert.Equal(new LibraryTargets(new LibraryTarget("/lib/Mixed", true), new LibraryTarget("/lib/Mixed", false)), LibraryRouting.TargetsOf(Mixed, null));
        Assert.Equal(new LibraryTargets(null, null), LibraryRouting.TargetsOf(Music, null));
    }

    [Fact]
    public void A_shows_and_a_movies_destination_route_by_kind()
    {
        var r = LibraryRouting.Route([new("t", null), new("m", null)], All);

        Assert.Empty(r.Problems);
        Assert.Equal(new LibraryTargets(new LibraryTarget("/lib/TV", true), new LibraryTarget("/lib/Movies", false)), r.Targets);
    }

    [Fact]
    public void Overlapping_destinations_are_ambiguous_and_ignored()
    {
        var twoFilm = LibraryRouting.Route([new("m", null), new("m2", null)], All);
        var mixedPlus = LibraryRouting.Route([new("x", null), new("m", null)], All);
        var plusMixed = LibraryRouting.Route([new("t", null), new("x", null)], All);

        Assert.Equal(new LibraryTargets(null, new LibraryTarget("/lib/Movies", false)), twoFilm.Targets);
        Assert.Contains("overlaps", Assert.Single(twoFilm.Problems), System.StringComparison.Ordinal);
        Assert.Equal(LibraryRouting.TargetsOf(Mixed, null), mixedPlus.Targets);
        Assert.Single(mixedPlus.Problems);
        Assert.Equal(new LibraryTargets(new LibraryTarget("/lib/TV", true), null), plusMixed.Targets);
        Assert.Single(plusMixed.Problems);
    }

    [Fact]
    public void Missing_and_unsupported_libraries_are_reported()
    {
        var r = LibraryRouting.Route([new("gone", null), new("mu", null), new("", null)], All);

        Assert.Equal(new LibraryTargets(null, null), r.Targets);
        Assert.Equal(2, r.Problems.Count);
    }
}
