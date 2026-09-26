using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Ai;
using Jellyfin.Plugin.Common.Speech;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Parsing;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Episodes whose names don't say which episode they are, identified from a short transcript. Invented titles throughout.
public sealed class EpisodeByTranscriptTests : IDisposable
{
    private const string Heard = "Inspector Vale said the lighthouse lamp went dark the night the ferry sank and nobody on the quay saw a thing";

    private static readonly string Video = Path.Combine(Path.GetTempPath(), "Harbour Watch", "Harbour Watch - unknown episode.mkv");

    private static readonly MetadataCandidate Show = new()
    {
        Name = "Harbour Watch",
        Year = 2004,
        IsSeries = true,
        ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "71" },
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ingest-stt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class Lookup(int seasons, int perSeason) : IMetadataLookup
    {
        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([Show]);

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EpisodeListing>>(season >= 1 && season <= seasons
                ? [.. Enumerable.Range(1, perSeason).Select(e => new EpisodeListing(season, e, $"Episode {season}-{e}", 2004 + season, $"Synopsis {season}-{e}."))]
                : []);
    }

    private sealed class Transcriber(HeardText heard) : ITranscriber
    {
        public List<string> Asked { get; } = [];

        public Task<HeardText> TranscribeAsync(string videoPath, CancellationToken cancellationToken)
        {
            Asked.Add(videoPath);
            return Task.FromResult(heard);
        }
    }

    private sealed class Picker(Func<IReadOnlyList<EpisodeListing>, TiebreakPick> answer) : ITiebreaker, IEpisodePicker
    {
        public string? FileName { get; private set; }

        public string? Transcript { get; private set; }

        public int Offered { get; private set; }

        public Task<TiebreakPick> PickAsync(ParsedRelease release, string fileName, IReadOnlyList<ScoredCandidate> options, CancellationToken cancellationToken)
            => Task.FromResult(new TiebreakPick(null, string.Empty, null));

        public Task<TiebreakPick> PickEpisodeAsync(string fileName, string series, string episodeTitle, int? year, IReadOnlyList<EpisodeListing> options, CancellationToken cancellationToken)
            => Task.FromResult(new TiebreakPick(null, string.Empty, null));

        public Task<TiebreakPick> PickFromTranscriptAsync(string fileName, string series, string transcript, IReadOnlyList<EpisodeListing> options, CancellationToken cancellationToken)
        {
            FileName = fileName;
            Transcript = transcript;
            Offered = options.Count;
            return Task.FromResult(answer(options));
        }
    }

    private static Task<IdentificationResult> Identify(IMetadataLookup lookup, ITiebreaker? picker, ITranscriber? transcriber, string video = "")
    {
        var path = video.Length > 0 ? video : Video;
        return new MediaIdentifier(lookup, null, picker, transcriber).IdentifyAsync(ReleaseNameParser.Parse(Path.GetFileName(path)), preferTv: true, CancellationToken.None, path);
    }

    private static Picker Picks(int season, int episode)
        => new(options => new TiebreakPick(options.ToList().FindIndex(o => o.Season == season && o.Episode == episode), "The lighthouse and the ferry.", "AI (test)"));

    [Fact]
    public async Task The_transcript_decides_among_every_season_when_the_name_gives_none()
    {
        var picker = Picks(2, 3);
        var transcriber = new Transcriber(new HeardText(Heard, string.Empty));

        var r = await Identify(new Lookup(3, 6), picker, transcriber);

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal(2, r.Episode!.Season);
        Assert.Equal(3, r.Episode.Episode);
        Assert.Equal("AI (test)", r.DecidedBy);
        Assert.Contains("identified from a transcript", r.Reason, StringComparison.Ordinal);
        Assert.Equal(18, picker.Offered);
        Assert.Equal(Heard, picker.Transcript);
        Assert.Equal(Path.GetFileName(Video), picker.FileName);
        Assert.Equal([Video], transcriber.Asked);
    }

    [Fact]
    public async Task No_pick_or_no_transcript_waits_for_review_with_the_reason()
    {
        var noPick = await Identify(new Lookup(1, 5), new Picker(_ => new TiebreakPick(-1, "Nothing fits.", "AI (test)")), new Transcriber(new HeardText(Heard, string.Empty)));
        var silent = await Identify(new Lookup(1, 5), Picks(1, 1), new Transcriber(new HeardText(null, "Too little speech was heard to tell which episode this is.")));

        Assert.Equal(IdentificationStatus.NeedsReview, noPick.Status);
        Assert.Contains("Nothing fits.", noPick.Reason, StringComparison.Ordinal);
        Assert.Equal(IdentificationStatus.NeedsReview, silent.Status);
        Assert.Contains("Too little speech", silent.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_transcriber_or_a_full_path_nothing_is_transcribed()
    {
        var transcriber = new Transcriber(new HeardText(Heard, string.Empty));

        var none = await Identify(new Lookup(1, 5), Picks(1, 1), null);
        var bare = await Identify(new Lookup(1, 5), Picks(1, 1), transcriber, "Harbour Watch - unknown episode.mkv");

        Assert.Equal(IdentificationStatus.NeedsReview, none.Status);
        Assert.Equal(IdentificationStatus.NeedsReview, bare.Status);
        Assert.Empty(transcriber.Asked);
    }

    [Fact]
    public async Task A_show_with_too_many_episodes_isnt_transcribed()
    {
        var transcriber = new Transcriber(new HeardText(Heard, string.Empty));

        var r = await Identify(new Lookup(12, 22), Picks(1, 1), transcriber);

        Assert.Equal(IdentificationStatus.NeedsReview, r.Status);
        Assert.Contains("too many episodes", r.Reason, StringComparison.Ordinal);
        Assert.Empty(transcriber.Asked);
    }

    [Fact]
    public void Replies_become_text_or_a_reason()
    {
        Assert.Equal(Heard, SpeechTranscriber.Read(new SpeechReply(true, Heard, "en", "builtin", null, null)).Text);
        Assert.Contains("Too little speech", SpeechTranscriber.Read(new SpeechReply(true, "Hello there.", "en", "builtin", null, null)).Note, StringComparison.Ordinal);
        Assert.Equal(string.Empty, SpeechTranscriber.Read(new SpeechReply(false, null, null, null, "Not installed.", "not-installed")).Note);
        Assert.Equal(string.Empty, SpeechTranscriber.Read(new SpeechReply(false, null, null, null, "Not allowed.", "not-allowed")).Note);
        Assert.Contains("over the monthly limit", SpeechTranscriber.Read(new SpeechReply(false, null, null, null, "It would go over the monthly limit.", "provider-limit")).Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_short_or_quiet_video_is_tried_from_one_minute_and_each_file_is_transcribed_once()
    {
        Directory.CreateDirectory(_dir);
        var video = Path.Combine(_dir, "a.mkv");
        await File.WriteAllBytesAsync(video, [1, 2, 3], TestContext.Current.CancellationToken);
        var starts = new List<TimeSpan>();
        var transcriber = new SpeechTranscriber((caller, purpose, path, start, length, language, ct) =>
        {
            starts.Add(start);
            Assert.Equal("ingest.episode", purpose);
            Assert.Equal(TimeSpan.FromMinutes(2), length);
            return Task.FromResult(new SpeechReply(true, start == TimeSpan.FromMinutes(5) ? string.Empty : Heard, "en", "builtin", null, null));
        });

        var first = await transcriber.TranscribeAsync(video, TestContext.Current.CancellationToken);
        var again = await transcriber.TranscribeAsync(video, TestContext.Current.CancellationToken);

        Assert.Equal(Heard, first.Text);
        Assert.Equal(first, again);
        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)], starts);
    }

    [Fact]
    public async Task The_AI_gets_the_transcript_as_data_with_codes_titles_and_synopses()
    {
        string? sent = null, instructions = null;
        var ai = new AiTiebreaker((caller, purpose, i, data, schema, max, effort, ct) =>
        {
            instructions = i;
            sent = JsonSerializer.Serialize(data);
            Assert.Equal("ingest.episode", purpose);
            return Task.FromResult(new AiReply(true, JsonDocument.Parse("{\"choice\":1,\"reason\":\"x\"}").RootElement.Clone(), "m", null, null));
        });

        var pick = await ai.PickFromTranscriptAsync("a.mkv", "Harbour Watch", Heard, [new(1, 1, "Storm Night", 2005, "A storm."), new(1, 2, "The Keeper", 2005, "The lamp goes dark.")], TestContext.Current.CancellationToken);

        Assert.Equal(1, pick.Index);
        Assert.Contains("not instructions", instructions, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(sent!);
        Assert.Equal(Heard, doc.RootElement.GetProperty("transcript").GetString());
        Assert.Equal("S01E02", doc.RootElement.GetProperty("episodes")[1].GetProperty("code").GetString());
    }
}
