using Jellyfin.Plugin.Ingest.Naming;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class SubtitleNamerTests
{
    private static string Name(SubtitleTrack track, params string[] taken)
        => SubtitleNamer.SidecarName("Stem", track, ".srt", n => System.Array.IndexOf(taken, n) >= 0);

    [Theory]
    [InlineData("English", "Stem.en.srt")]
    [InlineData("eng", "Stem.en.srt")]
    [InlineData("en", "Stem.en.srt")]
    [InlineData("Klingon", "Stem.srt")]
    public void Language_is_normalised_to_two_letters(string language, string expected)
    {
        Assert.Equal(expected, Name(new SubtitleTrack { Language = language }));
    }

    [Fact]
    public void Flags_follow_the_language_in_jellyfin_order()
    {
        Assert.Equal("Stem.en.default.sdh.forced.srt", Name(new SubtitleTrack { Language = "en", Default = true, HearingImpaired = true, Forced = true }));
    }

    [Fact]
    public void Title_comes_before_the_language()
    {
        Assert.Equal("Stem.Commentary.en.srt", Name(new SubtitleTrack { Language = "en", Title = "Commentary" }));
    }

    [Fact]
    public void Collisions_get_a_readable_title_never_a_bare_number()
    {
        Assert.Equal("Stem.Alternate 2.en.srt", Name(new SubtitleTrack { Language = "en" }, "Stem.en.srt"));
        Assert.Equal("Stem.Alternate 3.en.srt", Name(new SubtitleTrack { Language = "en" }, "Stem.en.srt", "Stem.Alternate 2.en.srt"));
        Assert.Equal("Stem.Commentary 2.en.srt", Name(new SubtitleTrack { Language = "en", Title = "Commentary" }, "Stem.Commentary.en.srt"));
    }

    [Fact]
    public void Dots_in_titles_cannot_break_token_parsing()
    {
        Assert.Equal("Stem.Dir Cut Notes.en.srt", Name(new SubtitleTrack { Language = "en", Title = "Dir.Cut.Notes" }));
    }
}
