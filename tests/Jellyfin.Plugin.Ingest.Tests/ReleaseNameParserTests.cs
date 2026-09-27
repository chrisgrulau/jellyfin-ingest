using Jellyfin.Plugin.Ingest.Naming;
using Jellyfin.Plugin.Ingest.Parsing;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// All names here are invented. Never use real library file names in this public repository.
public class ReleaseNameParserTests
{
    [Theory]
    [InlineData("n00b-Lantern1E4.mp4", "Lantern", 1, 4, null)]
    [InlineData("Show.Name.S02E05.1080p.WEB-DL.x264-GRP.mkv", "Show Name", 2, 5, null)]
    [InlineData("Show.Name.S02E05E06.720p.HDTV.mkv", "Show Name", 2, 5, 6)]
    [InlineData("Show Name - S01E01-E03 - Pilot.mkv", "Show Name", 1, 1, 3)]
    [InlineData("show.name.1x04.hdtv.xvid.avi", "show name", 1, 4, null)]
    [InlineData("Show Name Season 1 Episode 01, 02 & 03 - Extended.avi", "Show Name", 1, 1, 3)]
    [InlineData("Show Name (2005) S03E02 The Episode Title (1080p BluRay x265).mkv", "Show Name", 3, 2, null)]
    [InlineData("Show Name S01/S01E04.mkv", "Show Name", 1, 4, null)]
    [InlineData("Show Name/Season 1/S01E04.mkv", "Show Name", 1, 4, null)]
    public void Parses_episode_codes(string name, string title, int season, int episode, int? ending)
    {
        var p = ReleaseNameParser.Parse(name);

        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal(season, p.Season);
        Assert.Equal(episode, p.Episode);
        Assert.Equal(ending, p.EndingEpisode);
    }

    [Fact]
    public void Keeps_year_and_episode_title()
    {
        var p = ReleaseNameParser.Parse("Show Name (2005) S03E02 The Episode Title (1080p BluRay x265).mkv");

        Assert.Equal(2005, p.Year);
        Assert.Equal("The Episode Title", p.EpisodeTitle);
    }

    [Fact]
    public void Release_tags_are_not_an_episode_title()
    {
        Assert.Null(ReleaseNameParser.Parse("Show.Name.S01E01.PROPER.720p.HDTV.mkv").EpisodeTitle);
    }

    [Fact]
    public void Special_code_means_season_zero_matched_by_title()
    {
        var p = ReleaseNameParser.Parse("Show Name S13SP2 A Special Title.mp4");

        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal("Show Name", p.Title);
        Assert.Equal(0, p.Season);
        Assert.Null(p.Episode);
        Assert.Equal("A Special Title", p.EpisodeTitle);
    }

    [Theory]
    [InlineData("Movie Title (2019) [1080p] [BluRay] [5.1] [YTS.MX].mp4", "Movie Title", 2019)]
    [InlineData("Movie.Title.2019.1080p.BluRay.x264-GRP.mkv", "Movie Title", 2019)]
    [InlineData("Rocket Club 2049 (2017).mkv", "Rocket Club 2049", 2017)]
    [InlineData("2001 A Space Journey (1968).mkv", "2001 A Space Journey", 1968)]
    [InlineData("Internal Matters (1990).mkv", "Internal Matters", 1990)]
    [InlineData("Movie Title 2012 Ita Eng Sub Ita Eng.mkv", "Movie Title", 2012)]
    public void Parses_movie_title_and_year(string name, string title, int year)
    {
        var p = ReleaseNameParser.Parse(name);

        Assert.Equal(MediaKind.Movie, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal(year, p.Year);
    }

    [Theory]
    [InlineData("Movie Title 1992 Assembly Cut 1080p BluRay HEVC x265.mkv", "Assembly Cut")]
    [InlineData("Movie Title (2008) Director's Cut.mkv", "Director's Cut")]
    public void Detects_edition_and_keeps_it_out_of_the_title(string name, string edition)
    {
        var p = ReleaseNameParser.Parse(name);

        Assert.Equal(edition, p.Edition);
        Assert.Equal("Movie Title", p.Title);
    }

    [Theory]
    [InlineData("Movie.Title.2010.1080p.Sample.mkv", ExtraType.Sample)]
    [InlineData("Show Name S01/Featurettes/Making the Show.mkv", ExtraType.Featurette)]
    [InlineData("Show Name/Season 2/Extras/Bloopers.mkv", ExtraType.Other)]
    public void Detects_extras(string name, ExtraType expected)
    {
        Assert.Equal(expected, ReleaseNameParser.Parse(name).Extra);
    }

    [Fact]
    public void Title_less_name_without_episode_code_is_unknown()
    {
        Assert.Equal(MediaKind.Unknown, ReleaseNameParser.ParseName("Something Undated").Kind);
    }
    [Theory]
    [InlineData("Show Name (2022) Season 1/Show.Name.S01E01.1080p.WEB.mkv", 2022, MediaKind.Episode)]
    [InlineData("Show.Name.2022.S01.1080p.WEB-GRP/Show.Name.S01E01.1080p.WEB-GRP.mkv", 2022, MediaKind.Episode)]
    [InlineData("Movie.Title.2019.1080p.BluRay-GRP/movie.title.1080p-grp.mkv", 2019, MediaKind.Movie)]
    public void Takes_the_year_from_a_folder_with_the_same_title(string path, int year, MediaKind kind)
    {
        var p = ReleaseNameParser.Parse(path);
        Assert.Equal(year, p.Year);
        Assert.Equal(kind, p.Kind);
    }

    [Fact]
    public void Ignores_the_year_of_an_unrelated_folder()
    {
        var p = ReleaseNameParser.Parse("Downloads 2024/Show.Name.S01E01.mkv");
        Assert.Null(p.Year);
        Assert.Equal("Show Name", p.Title);
    }

    [Fact]
    public void Keeps_the_file_year_over_the_folder_year()
    {
        Assert.Equal(2005, ReleaseNameParser.Parse("Show Name (2004)/Show.Name.2005.S01E01.mkv").Year);
    }

    // ING-35: digits outside ASCII are not episode or season numbers, and never throw
    [Theory]
    [InlineData("Show.Name.S０１E０２.mkv")]
    [InlineData("Show Name Season １ Episode ２.mkv")]
    [InlineData("Show.Name.１x０２.mkv")]
    public void Non_ascii_digits_never_throw(string name)
    {
        var p = ReleaseNameParser.Parse(name);
        Assert.Null(p.Episode);
    }
    // A season with no episode number is a series whose episode is unknown, never a film
    [Theory]
    [InlineData("Example Show S02 - unknown episode.mkv", "Example Show", 2, null)]
    [InlineData("Example Show - Season 2 - Untitled.mkv", "Example Show", 2, null)]
    [InlineData("Example.Show.Series.3.720p.HDTV.mkv", "Example Show", 3, null)]
    [InlineData("Example Show 2nd Season.mkv", "Example Show", 2, null)]
    [InlineData("Example Show (2004) S02 - The Lighthouse.mkv", "Example Show", 2, "The Lighthouse")]
    public void A_season_only_code_means_a_series_with_the_episode_unknown(string name, string title, int season, string? episodeTitle)
    {
        var p = ReleaseNameParser.Parse(name);

        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal(season, p.Season);
        Assert.Null(p.Episode);
        Assert.Equal(episodeTitle, p.EpisodeTitle);
    }

    [Theory]
    [InlineData("Example Show Season 1-3.mkv")]
    [InlineData("Example Show Seasons.mkv")]
    public void A_season_range_or_plain_word_gives_no_season(string name)
    {
        Assert.Null(ReleaseNameParser.Parse(name).Season);
    }

    [Theory]
    [InlineData("Season of the Harbour (2011).mkv", "Season of the Harbour", 2011)]
    [InlineData("Series 9 The Contest (2003).mkv", "Series 9 The Contest", 2003)]
    public void A_title_that_starts_with_season_or_series_stays_a_film(string name, string title, int year)
    {
        var p = ReleaseNameParser.Parse(name);

        Assert.Equal(MediaKind.Movie, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal(year, p.Year);
    }

    // A file name that says nothing takes its identity from the release folder
    [Theory]
    [InlineData("Example Show Season 2/episode.mkv", "Example Show", 2)]
    [InlineData("Example Show S02/video.mkv", "Example Show", 2)]
    [InlineData("Example Show Series 2/untitled.mp4", "Example Show", 2)]
    [InlineData("Example Show/Season 2/unknown episode.mkv", "Example Show", 2)]
    [InlineData("Example Show (2004) Season 2/title_t00.mkv", "Example Show", 2)]
    public void A_generic_file_name_in_a_season_folder_is_that_series_and_season(string path, string title, int season)
    {
        var p = ReleaseNameParser.Parse(path);

        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal(season, p.Season);
        Assert.Null(p.Episode);
    }

    [Theory]
    [InlineData("Example Film (2019)/movie.mkv")]
    [InlineData("Example Film (2019)/VTS_01_1.mkv")]
    [InlineData("Example Film (2019)/track01.mkv")]
    [InlineData("Example Film (2019)/BDMV.mkv")]
    [InlineData("Example Film (2019)/film.mp4")]
    [InlineData("Example Film (2019)/0001.mkv")]
    public void A_generic_file_name_in_a_film_folder_is_that_film(string path)
    {
        var p = ReleaseNameParser.Parse(path);

        Assert.Equal(MediaKind.Movie, p.Kind);
        Assert.Equal("Example Film", p.Title);
        Assert.Equal(2019, p.Year);
    }

    [Fact]
    public void A_generic_file_name_on_its_own_names_no_title()
    {
        Assert.Equal(string.Empty, ReleaseNameParser.Parse("episode.mkv").Title);
        Assert.Equal(string.Empty, ReleaseNameParser.Parse("clip.mkv").Title);
    }

    [Theory]
    [InlineData("Mainstream Harbour.mkv")]
    [InlineData("The Episode Keeper (2010).mkv")]
    [InlineData("Filmed Harbour.mkv")]
    public void Real_titles_are_not_generic(string name)
    {
        Assert.False(ReleaseNameParser.IsGenericName(System.IO.Path.GetFileNameWithoutExtension(name)));
    }

    // Numbers alone inside a season folder are episode numbers (a multi-file season pack)
    [Theory]
    [InlineData("Example Show Season 2/01.mkv", 1)]
    [InlineData("Example Show Season 2/02.mkv", 2)]
    [InlineData("Example Show/Season 2/E03.mkv", 3)]
    [InlineData("Example Show S02/Episode 4.mkv", 4)]
    public void A_bare_number_in_a_season_folder_is_the_episode(string path, int episode)
    {
        var p = ReleaseNameParser.Parse(path);

        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal("Example Show", p.Title);
        Assert.Equal(2, p.Season);
        Assert.Equal(episode, p.Episode);
    }

    [Fact]
    public void A_bare_number_without_a_season_leaves_the_episode_unknown()
    {
        var p = ReleaseNameParser.Parse("Example Show/01.mkv");

        Assert.Equal("Example Show", p.Title);
        Assert.Null(p.Episode);
    }
}
