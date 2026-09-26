using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Ingest.Naming;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class MediaNamerTests
{
    private static readonly SeriesIdentity Series = new() { Title = "Show Name", Year = 2005, TvdbId = "7", TmdbId = "9" };

    [Fact]
    public void Movie_folder_uses_tmdb_id()
    {
        var movie = new MovieIdentity { Title = "Movie Title", Year = 2019, TmdbId = "123", ImdbId = "tt0000001" };
        Assert.Equal("Movie Title (2019) [tmdbid-123]", MediaNamer.MovieFolderName(movie));
    }

    [Fact]
    public void Movie_folder_falls_back_to_imdb_id_then_to_no_id()
    {
        Assert.Equal("Movie Title (2019) [imdbid-tt0000001]", MediaNamer.MovieFolderName(new MovieIdentity { Title = "Movie Title", Year = 2019, ImdbId = "tt0000001" }));
        Assert.Equal("Movie Title", MediaNamer.MovieFolderName(new MovieIdentity { Title = "Movie Title" }));
    }

    [Fact]
    public void Movie_file_starts_with_exact_folder_name_so_versions_group()
    {
        var movie = new MovieIdentity { Title = "Movie: Part Two", Year = 2019, TmdbId = "123", Edition = "Director's Cut" };

        var folder = MediaNamer.MovieFolderName(movie);
        var file = MediaNamer.MovieFileName(movie, ".mkv");

        Assert.Equal("Movie - Part Two (2019) [tmdbid-123]", folder);
        Assert.Equal("Movie - Part Two (2019) [tmdbid-123] - Director's Cut.mkv", file);
        Assert.StartsWith(folder, file, StringComparison.Ordinal);
    }

    [Fact]
    public void Series_folder_lists_tvdb_then_tmdb_ids()
    {
        Assert.Equal("Show Name (2005) [tvdbid-7] [tmdbid-9]", MediaNamer.SeriesFolderName(Series));
        Assert.Equal("Show Name (2005) [tmdbid-9]", MediaNamer.SeriesFolderName(Series with { TvdbId = null }));
    }

    [Theory]
    [InlineData(0, "Season 00")]
    [InlineData(5, "Season 05")]
    [InlineData(12, "Season 12")]
    public void Season_folders_are_zero_padded(int season, string expected)
    {
        Assert.Equal(expected, MediaNamer.SeasonFolderName(season));
    }

    [Theory]
    [InlineData(1, 4, null, "S01E04")]
    [InlineData(1, 1, 2, "S01E01-E02")]
    [InlineData(0, 148, null, "S00E148")]
    [InlineData(2, 5, 5, "S02E05")]
    public void Episode_codes(int season, int episode, int? ending, string expected)
    {
        Assert.Equal(expected, MediaNamer.EpisodeCode(season, episode, ending));
    }

    [Fact]
    public void Episode_file_name_with_and_without_title()
    {
        var ep = new EpisodeIdentity { Series = Series, Season = 1, Episode = 4, Title = "A Broken: Heart?" };

        Assert.Equal("Show Name S01E04 - A Broken - Heart.mp4", MediaNamer.EpisodeFileName(ep, ".mp4"));
        Assert.Equal("Show Name S01E04.mkv", MediaNamer.EpisodeFileName(ep with { Title = null }, "mkv"));
    }

    [Theory]
    [InlineData(ExtraType.Featurette, "featurettes")]
    [InlineData(ExtraType.DeletedScene, "deleted scenes")]
    [InlineData(ExtraType.BehindTheScenes, "behind the scenes")]
    [InlineData(ExtraType.ShortFilm, "shorts")]
    [InlineData(ExtraType.Other, "extras")]
    public void Extras_use_jellyfin_folder_names(ExtraType type, string folder)
    {
        Assert.Equal(folder, MediaNamer.ExtrasFolderName(type));
    }

    [Fact]
    public void Samples_are_never_filed_as_extras()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaNamer.ExtrasFolderName(ExtraType.Sample));
    }

    [Theory]
    [InlineData("  A<b>c: d/e|f?g*h\"i.. ", "Abc - d-efghi")]
    [InlineData("Title:  Sub   Title...", "Title - Sub Title")]
    public void Sanitizer_removes_reserved_characters(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Leading_dots_never_make_a_hidden_folder()
    {
        var name = MediaNamer.SeriesFolderName(new SeriesIdentity { Title = ".hack//Sign", Year = 2002, TvdbId = "1" });
        Assert.Equal("hack--Sign (2002) [tvdbid-1]", name);
        Assert.False(name.StartsWith('.'));
    }

    [Theory]
    [InlineData("?")]
    [InlineData("...")]
    [InlineData("  ")]
    public void A_title_with_nothing_printable_becomes_untitled(string title)
    {
        Assert.Equal("Untitled (2011) [tmdbid-5]", MediaNamer.MovieFolderName(new MovieIdentity { Title = title, Year = 2011, TmdbId = "5" }));
        Assert.Equal("Untitled", MediaNamer.MovieFolderName(new MovieIdentity { Title = title }));
    }

    [Fact]
    public void Long_titles_are_shortened_to_fit_the_file_system()
    {
        var series = new SeriesIdentity { Title = new string('長', 200), Year = 2020, TvdbId = "1234567890", TmdbId = "1234567890" };
        var episode = new EpisodeIdentity { Series = series, Season = 1, Episode = 1, EndingEpisode = 2, Title = string.Concat(Enumerable.Repeat("とても長いエピソードのタイトル", 30)) };

        var folder = MediaNamer.SeriesFolderName(series);
        var file = MediaNamer.EpisodeFileName(episode, ".mkv");

        Assert.True(Encoding.UTF8.GetByteCount(folder) <= 240, folder);
        Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileNameWithoutExtension(file)) <= 180, file);
        Assert.Contains("S01E01-E02 - とても", file, StringComparison.Ordinal);

        // Room left for a subtitle's title, language and flags
        var sidecar = SubtitleNamer.SidecarName(Path.GetFileNameWithoutExtension(file), new SubtitleTrack { Title = new string('x', 100), Language = "en", Default = true, HearingImpaired = true, Forced = true }, ".srt", _ => false);
        Assert.True(Encoding.UTF8.GetByteCount(sidecar) <= 255, sidecar);
    }

    [Fact]
    public void Truncation_never_splits_a_character()
    {
        var text = "👍🏽👍🏽👍🏽";
        var cut = FileNameSanitizer.Truncate(text, 10);
        Assert.Equal("👍🏽", cut);
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("nul.txt", true)]
    [InlineData("COM1", true)]
    [InlineData("Con Air", false)]
    [InlineData("Console", false)]
    public void Windows_device_names_are_recognised(string name, bool reserved)
    {
        Assert.Equal(reserved, FileNameSanitizer.IsReservedOnWindows(name));
    }

    [Fact]
    public void A_reserved_name_gets_an_underscore()
    {
        Assert.Equal("Con_", MediaNamer.MovieFolderName(new MovieIdentity { Title = "Con" }));
    }
}
