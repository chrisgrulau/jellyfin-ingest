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
using Jellyfin.Plugin.Ingest.Planning;
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
    public async Task A_show_with_too_many_episodes_waits_for_review_with_the_reason()
    {
        var transcriber = new Transcriber(new HeardText(Heard, string.Empty));

        var r = await Identify(new Lookup(12, 22), Picks(1, 1), transcriber);

        Assert.Equal(IdentificationStatus.NeedsReview, r.Status);
        Assert.Contains("too many episodes", r.Reason, StringComparison.Ordinal);
    }

    // ING-31: the transcript is asked for first, so without one the seasons aren't listed at all
    [Fact]
    public async Task Without_a_transcript_no_season_is_listed()
    {
        var lookup = new CountingLookup();

        await Identify(lookup, Picks(1, 1), new Transcriber(new HeardText(null, string.Empty)));

        Assert.Equal(0, lookup.Listed);
    }

    private sealed class CountingLookup : IMetadataLookup
    {
        public int Listed { get; private set; }

        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([Show]);

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
        {
            Listed++;
            return Task.FromResult<IReadOnlyList<EpisodeListing>>([]);
        }
    }

    // FAM-02: a question in another script is fitted to the AI plugin's limit without dropping any offered episode
    [Fact]
    public async Task A_large_non_latin_question_is_fitted_to_the_limit()
    {
        object? sent = null;
        var ai = new AiTiebreaker((caller, purpose, instructions, data, schema, max, effort, ct) =>
        {
            sent = data;
            return Task.FromResult(new AiReply(true, JsonDocument.Parse("{\"choice\":149,\"reason\":\"x\"}").RootElement.Clone(), "m", null, null));
        });
        var episodes = Enumerable.Range(1, 150).Select(e => new EpisodeListing(1, e, "Серия " + e, 2004, new string('ж', 400))).ToList();

        var pick = await ai.PickFromTranscriptAsync("a.mkv", "Гавань", new string('я', 4000), episodes, TestContext.Current.CancellationToken);

        Assert.Equal(149, pick.Index);
        Assert.True(Jellyfin.Plugin.Common.BridgeJson.Bytes(sent) <= Jellyfin.Plugin.Common.Ai.AiBridgeClient.MaxDataBytes);
        Assert.Equal(150, JsonDocument.Parse(JsonSerializer.Serialize(sent)).RootElement.GetProperty("episodes").GetArrayLength());
    }

    [Fact]
    public void Replies_become_text_or_a_reason()
    {
        Assert.Equal(Heard, SpeechTranscriber.Read(new SpeechReply(true, Heard, "en", "builtin", null, null)).Text);
        Assert.Contains("Too little speech", SpeechTranscriber.Read(new SpeechReply(true, "Hello there.", "en", "builtin", null, null)).Note, StringComparison.Ordinal);
        Assert.Contains("isn't installed", SpeechTranscriber.Read(new SpeechReply(false, null, null, null, "Not installed.", "not-installed")).Note, StringComparison.Ordinal);
        Assert.Equal("Episode unknown; the Subtitles plugin didn't allow a transcript.", SpeechTranscriber.Read(new SpeechReply(false, null, null, null, "Not allowed.", "not-allowed")).Note);
        Assert.Contains("transcription is off", SpeechTranscriber.Read(new SpeechReply(false, null, null, null, "Off.", "off")).Note, StringComparison.Ordinal);
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
    // A series chosen in review (or read from the name) with the episode unknown: the transcript decides
    private static readonly string SeasonFolderVideo = Path.Combine(Path.GetTempPath(), "Harbour Watch Season 2", "episode.mkv");

    private static Task<IdentificationResult> Choose(ITiebreaker? picker, ITranscriber? transcriber, string video)
        => new MediaIdentifier(new Lookup(3, 6), null, picker, transcriber).IdentifyAsChosenAsync(ReleaseNameParser.Parse(video), Show, isTv: true, CancellationToken.None, video);

    [Fact]
    public async Task A_chosen_series_with_the_season_in_the_folder_is_picked_from_that_season()
    {
        var picker = Picks(2, 3);
        var transcriber = new Transcriber(new HeardText(Heard, string.Empty));

        var r = await Choose(picker, transcriber, SeasonFolderVideo);

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal(2, r.Episode!.Season);
        Assert.Equal(3, r.Episode.Episode);
        Assert.Equal(6, picker.Offered);
        Assert.Equal([SeasonFolderVideo], transcriber.Asked);
    }

    [Fact]
    public async Task A_chosen_series_with_no_season_is_picked_from_every_season()
    {
        var video = Path.Combine(Path.GetTempPath(), "Some Release", "episode.mkv");
        var picker = Picks(3, 1);

        var r = await Choose(picker, new Transcriber(new HeardText(Heard, string.Empty)), video);

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal(3, r.Episode!.Season);
        Assert.Equal(18, picker.Offered);
    }

    [Fact]
    public async Task A_season_only_name_is_picked_from_that_season()
    {
        var video = Path.Combine(Path.GetTempPath(), "Harbour Watch S02 - unknown episode.mkv");
        var picker = Picks(2, 5);

        var r = await Identify(new Lookup(3, 6), picker, new Transcriber(new HeardText(Heard, string.Empty)), video);

        Assert.Equal(IdentificationStatus.Identified, r.Status);
        Assert.Equal(5, r.Episode!.Episode);
        Assert.Equal(6, picker.Offered);
    }

    [Fact]
    public async Task With_transcripts_or_the_AI_off_the_reason_says_so()
    {
        var off = await Choose(Picks(2, 3), null, SeasonFolderVideo);
        var noAi = await Choose(null, new Transcriber(new HeardText(Heard, string.Empty)), SeasonFolderVideo);
        var aiSilent = await Choose(new Picker(_ => new TiebreakPick(null, string.Empty, null)), new Transcriber(new HeardText(Heard, string.Empty)), SeasonFolderVideo);

        Assert.Equal(IdentificationStatus.NeedsReview, off.Status);
        Assert.Contains(MediaIdentifier.TranscriptsOff, off.Reason, StringComparison.Ordinal);
        Assert.Contains(MediaIdentifier.AiOff, noAi.Reason, StringComparison.Ordinal);
        Assert.Contains(MediaIdentifier.AiUnavailable, aiSilent.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_Subtitles_plugin_says_no_the_reason_says_so_and_a_retry_asks_again()
    {
        var asked = 0;
        var transcriber = new SpeechTranscriber((caller, purpose, path, start, length, language, ct) =>
        {
            asked++;
            return Task.FromResult(new SpeechReply(false, null, null, null, "Not allowed.", "not-allowed"));
        });

        var r = await Choose(Picks(2, 3), transcriber, SeasonFolderVideo);
        await Choose(Picks(2, 3), transcriber, SeasonFolderVideo);

        Assert.Equal(IdentificationStatus.NeedsReview, r.Status);
        Assert.Contains("the Subtitles plugin didn't allow a transcript", r.Reason, StringComparison.Ordinal);
        Assert.Equal(2, asked);
    }

    // The planner hands the identifier the video's full path (a relative one can't be transcribed)
    [Fact]
    public async Task A_series_chosen_in_review_is_transcribed_by_its_full_path()
    {
        var watch = Path.Combine(Path.GetTempPath(), "drop");
        var tv = new LibraryTarget(Path.Combine(Path.GetTempPath(), "lib", "Shows"), IsTv: true);
        var picker = Picks(2, 3);
        var transcriber = new Transcriber(new HeardText(Heard, string.Empty));
        var planner = new IngestPlanner(new MediaIdentifier(new Lookup(3, 6), null, picker, transcriber), p => !Path.HasExtension(p), _ => null, TimeProvider.System);

        var plan = await planner.PlanAsync(watch, "Harbour Watch Season 2", [new ReleaseFile("Harbour Watch Season 2/episode.mkv", 500_000_000)], LibraryTargets.Of(tv), Path.Combine(watch, ".ingest-quarantine"), new ChosenMatch(Show, tv), CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.Equal([Path.Combine(watch, "Harbour Watch Season 2", "episode.mkv")], transcriber.Asked);
        Assert.Contains(plan.Operations, o => Path.GetFileName(o.Destination).Contains("S02E03", StringComparison.Ordinal));
    }

    // A season pack whose files are only numbers: series and season from the folder, episodes from the numbers
    [Fact]
    public async Task A_season_folder_of_numbered_files_is_filed_as_those_episodes()
    {
        var watch = Path.Combine(Path.GetTempPath(), "drop");
        var tv = new LibraryTarget(Path.Combine(Path.GetTempPath(), "lib", "Shows"), IsTv: true);
        var transcriber = new Transcriber(new HeardText(Heard, string.Empty));
        var planner = new IngestPlanner(new MediaIdentifier(new Lookup(3, 6), null, null, transcriber), p => !Path.HasExtension(p), _ => null, TimeProvider.System);

        var plan = await planner.PlanAsync(watch, "Harbour Watch Season 2", [new ReleaseFile("Harbour Watch Season 2/01.mkv", 500_000_000), new ReleaseFile("Harbour Watch Season 2/02.mkv", 500_000_000)], LibraryTargets.Of(tv), Path.Combine(watch, ".ingest-quarantine"), null, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.Contains(plan.Operations, o => Path.GetFileName(o.Destination).Contains("S02E01", StringComparison.Ordinal));
        Assert.Contains(plan.Operations, o => Path.GetFileName(o.Destination).Contains("S02E02", StringComparison.Ordinal));
        Assert.Empty(transcriber.Asked);
    }
}
