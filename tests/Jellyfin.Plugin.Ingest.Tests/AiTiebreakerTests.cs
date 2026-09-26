using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Ai;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Parsing;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented titles throughout.
public class AiTiebreakerTests
{
    private static readonly IReadOnlyList<ScoredCandidate> Options =
    [
        new(new MetadataCandidate { Name = "Lantern", Year = 2011, IsSeries = true, Source = MediaIdentifier.LibrarySource }, 1.0),
        new(new MetadataCandidate { Name = "The Lantern", Year = 1958 }, 0.95),
    ];

    private static AiReply Ok(string json) => new(true, JsonDocument.Parse(json).RootElement.Clone(), "claude-opus-5-5", null, null);

    [Fact]
    public void Only_an_offered_position_is_accepted()
    {
        Assert.Equal(1, AiTiebreaker.Read(Ok("{\"choice\":1,\"reason\":\"Film.\"}"), 2).Index);
        Assert.Equal("AI (claude-opus-5-5)", AiTiebreaker.Read(Ok("{\"choice\":0,\"reason\":\"x\"}"), 2).By);
        Assert.Null(AiTiebreaker.Read(Ok("{\"choice\":2,\"reason\":\"x\"}"), 2).Index);
        Assert.Null(AiTiebreaker.Read(Ok("{\"choice\":\"0\",\"reason\":\"x\"}"), 2).Index);
        Assert.Contains("didn't think", AiTiebreaker.Read(Ok("{\"choice\":-1,\"reason\":\"Neither fits.\"}"), 2).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_AI_plugin_nothing_is_said_and_failures_are()
    {
        Assert.Equal(string.Empty, AiTiebreaker.Read(new AiReply(false, null, null, "The AI plugin isn't installed.", "not-installed"), 2).Note);
        Assert.Contains("over the monthly limit", AiTiebreaker.Read(new AiReply(false, null, null, "This would go over the monthly limit.", "provider-limit"), 2).Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_names_titles_and_years_are_sent_and_each_question_is_asked_once()
    {
        var calls = 0;
        string? sent = null;
        var tiebreaker = new AiTiebreaker((caller, purpose, instructions, data, schema, max, effort, ct) =>
        {
            calls++;
            sent = JsonSerializer.Serialize(data);
            Assert.Equal("ingest", caller);
            Assert.Equal("ingest.match", purpose);
            return Task.FromResult(Ok("{\"choice\":0,\"reason\":\"Episode code and a show you have.\"}"));
        });
        var release = ReleaseNameParser.Parse("Lantern.S01E02.720p.mkv");

        var first = await tiebreaker.PickAsync(release, "Lantern.S01E02.720p.mkv", Options, TestContext.Current.CancellationToken);
        await tiebreaker.PickAsync(release, "Lantern.S01E02.720p.mkv", Options, TestContext.Current.CancellationToken);

        Assert.Equal(0, first.Index);
        Assert.Equal(1, calls);
        using var doc = JsonDocument.Parse(sent!);
        Assert.Equal("Lantern.S01E02.720p.mkv", doc.RootElement.GetProperty("file").GetString());
        Assert.Equal("S01E02", doc.RootElement.GetProperty("episode").GetString());
        Assert.True(doc.RootElement.GetProperty("candidates")[0].GetProperty("inLibrary").GetBoolean());
        Assert.DoesNotContain("/", sent, StringComparison.Ordinal);
    }
}
