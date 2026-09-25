using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented shows throughout.
public class ExistingFilesTests
{
    private const string Show = "/lib/Shows/Lantern (2001)";

    private static readonly Dictionary<string, string[]> Tree = new()
    {
        [Show] = ["Lantern S03E01 - Loose.mkv", "folder.jpg"],
        [Show + "/Season 1"] = ["Lantern S01E01 - One.mkv", "Lantern S01E02-E03 - Double.mkv", "Lantern S01E01 - One.en.srt"],
        [Show + "/S02"] = ["Lantern S02E05 - Five.mp4"],
        [Show + "/Specials"] = ["Lantern S00E02 - Christmas.mkv"],
        [Show + "/Season 4"] = [],
    };

    private static IEnumerable<string> Dirs(string folder)
        => Tree.Keys.Where(k => k != folder && k.StartsWith(folder + "/", StringComparison.Ordinal) && !k[(folder.Length + 1)..].Contains('/', StringComparison.Ordinal));

    private static IEnumerable<string> Files(string folder)
        => Tree.TryGetValue(folder, out var f) ? f.Select(n => folder + "/" + n) : [];

    [Theory]
    [InlineData(1, 1, "Season 1/Lantern S01E01 - One.mkv")]
    [InlineData(1, 3, "Season 1/Lantern S01E02-E03 - Double.mkv")]
    [InlineData(2, 5, "S02/Lantern S02E05 - Five.mp4")]
    [InlineData(0, 2, "Specials/Lantern S00E02 - Christmas.mkv")]
    [InlineData(3, 1, "Lantern S03E01 - Loose.mkv")]
    public void Episodes_are_found_whatever_the_season_folder_is_called(int season, int episode, string expected)
        => Assert.Equal(Show + "/" + expected, ExistingFiles.FindEpisode(Show, season, episode, Dirs, Files));

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    [InlineData(5, 1)]
    public void Missing_episodes_are_not_found(int season, int episode)
        => Assert.Null(ExistingFiles.FindEpisode(Show, season, episode, Dirs, Files));

    [Theory]
    [InlineData(1, "Season 1")]
    [InlineData(2, "S02")]
    [InlineData(0, "Specials")]
    [InlineData(4, "Season 4")]
    public void The_existing_season_folder_is_reused(int season, string folder)
        => Assert.Equal(Show + "/" + folder, ExistingFiles.FindSeasonFolder(Show, season, Dirs, Files));

    [Fact]
    public void A_season_the_show_does_not_have_yet_has_no_folder()
        => Assert.Null(ExistingFiles.FindSeasonFolder(Show, 7, Dirs, Files));

    [Theory]
    [InlineData("/lib/Movies/Star Voyage - A New Dawn (1977) [tmdbid-11]/Star Voyage - A New Dawn (1977) [tmdbid-11].mkv", true, null)]
    [InlineData("/lib/Movies/Star Voyage - A New Dawn (1977) [tmdbid-11]/Star Voyage - A New Dawn (1977) [tmdbid-11] - Director's Cut.mkv", true, "Director's Cut")]
    [InlineData("/lib/Movies/Some Old Folder/star.voyage.1977.mkv", false, null)]
    public void A_film_title_with_a_colon_is_not_mistaken_for_an_edition(string path, bool known, string? edition)
        => Assert.Equal((known, edition), ExistingFiles.MovieEdition(path));
}
