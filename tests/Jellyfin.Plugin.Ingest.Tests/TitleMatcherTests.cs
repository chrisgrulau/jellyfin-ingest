using Jellyfin.Plugin.Ingest.Identification;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class TitleMatcherTests
{
    [Theory]
    [InlineData("The Show & Friends", "SHOW AND FRIENDS")]
    [InlineData("Café: Übung!", "CAFE UBUNG")]
    [InlineData("Space Saga Episode III", "SPACE SAGA EPISODE 3")]
    [InlineData("Robo³", "ROBO3")]
    [InlineData("The", "THE")]
    public void Normalises(string input, string expected)
    {
        Assert.Equal(expected, TitleMatcher.Normalise(input));
    }

    [Theory]
    [InlineData("Farscope", "Farscope")]
    [InlineData("The Show", "Show")]
    [InlineData("Show and Friends", "Show & Friends")]
    [InlineData("Space Saga Episode 3", "Space Saga: Episode III")]
    public void Equivalent_titles_score_one(string a, string b)
    {
        Assert.Equal(1.0, TitleMatcher.Similarity(a, b), 3);
    }

    [Fact]
    public void Transposition_typo_still_scores_high()
    {
        Assert.True(TitleMatcher.Similarity("Fasrcope", "Farscope") >= 0.85);
    }

    [Fact]
    public void Extra_words_are_tolerated()
    {
        Assert.True(TitleMatcher.Similarity("Home Base 5 The Holiday Job Family Comedy", "Home Base: The Holiday Job") >= 0.8);
    }

    [Fact]
    public void Different_titles_score_low()
    {
        Assert.True(TitleMatcher.Similarity("Lantern", "Alien") < 0.7);
        Assert.Equal(0, TitleMatcher.Similarity(string.Empty, "Anything"));
    }
}
