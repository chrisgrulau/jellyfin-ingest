using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented titles and ids throughout.
public class PlanningTests
{
    private const string Watch = "/drop/incoming";
    private const string Quarantine = "/drop/incoming/.ingest-quarantine";
    private static readonly LibraryTarget Tv = new("/lib/Shows", IsTv: true);
    private static readonly LibraryTarget Films = new("/lib/Movies", IsTv: false);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Lookup : IMetadataLookup
    {
        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>(TitleMatcher.Similarity(name, "Lantern") > 0.9
                ? [new MetadataCandidate { Name = "Lantern", Year = 2001, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "7", ["Tmdb"] = "9" } }]
                : []);

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>(TitleMatcher.Similarity(name, "Rocket Club") > 0.9
                ? [
                    new MetadataCandidate { Name = "Rocket Club", Year = 2019, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } },
                    new MetadataCandidate { Name = "Rocket Club", Year = 2024, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "124" } },
                ]
                : []);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult<string?>(season == 1 && episode == 4 ? "Glass Harbour" : null);
    }

    private static IngestPlanner Planner(Func<string, bool>? exists = null, IExistingMedia? existing = null)
        => new(new MediaIdentifier(new Lookup()), exists ?? (_ => false), _ => null, new FixedClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)), existing);

    private sealed class Existing(string? seriesFolder = null) : IExistingMedia
    {
        public List<IReadOnlyDictionary<string, string>> Asked { get; } = [];

        public Dictionary<(int Season, int Episode), string> Episodes { get; } = [];

        public List<(string Tmdb, string? Edition, string Path)> Movies { get; } = [];

        public string? FindSeriesFolder(IReadOnlyDictionary<string, string> seriesProviderIds)
        {
            Asked.Add(seriesProviderIds);
            return seriesFolder;
        }

        public string? FindEpisode(IReadOnlyDictionary<string, string> seriesProviderIds, string seriesFolder, int season, int episode)
            => Episodes.TryGetValue((season, episode), out var p) ? p : null;

        public string? SeasonFolder { get; init; }

        public string? FindSeasonFolder(string seriesFolder, int season) => SeasonFolder;

        public string? FindMovie(IReadOnlyDictionary<string, string> movieProviderIds, string? edition, string plannedPath)
            => Movies.Where(m => movieProviderIds.TryGetValue("Tmdb", out var id) && id == m.Tmdb && m.Edition == edition).Select(m => m.Path).FirstOrDefault();
    }

    private static ReleaseFile F(string rel, long size = 500_000_000) => new(rel, size);

    [Fact]
    public async Task Files_the_readme_example()
    {
        var plan = await Planner().PlanAsync(Watch, "n00b-Lantern1E4",
            [F("n00b-Lantern1E4/n00b-Lantern1E4.mp4"), F("n00b-Lantern1E4/english.srt", 40_000), F("n00b-Lantern1E4/README.txt", 900), F("n00b-Lantern1E4/sample.mkv", 12_000_000)],
            LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady);
        var series = Path.Combine("/lib/Shows", "Lantern (2001) [tvdbid-7] [tmdbid-9]", "Season 01");
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Video && o.Destination == Path.Combine(series, "Lantern S01E04 - Glass Harbour.mp4"));
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Subtitle && o.Destination == Path.Combine(series, "Lantern S01E04 - Glass Harbour.en.srt"));
        Assert.Equal(2, plan.Operations.Count(o => o.Kind == OperationKind.Quarantine));
        Assert.All(plan.Operations.Where(o => o.Kind == OperationKind.Quarantine), o => Assert.StartsWith(Path.Combine(Quarantine, "2026-09-24", "n00b-Lantern1E4"), o.Destination, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unidentified_releases_are_left_untouched_for_review()
    {
        var plan = await Planner().PlanAsync(Watch, "Mystery", [F("Mystery/Unknown.Thing.S01E01.mkv"), F("Mystery/info.txt", 10)], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.False(plan.IsReady);
        Assert.Empty(plan.Operations);
        Assert.Single(plan.Review);
    }

    [Fact]
    public async Task Never_overwrites_an_existing_file()
    {
        var existing = Path.Combine("/lib/Shows", "Lantern (2001) [tvdbid-7] [tmdbid-9]", "Season 01", "Lantern S01E04 - Glass Harbour.mkv");

        var plan = await Planner(p => p == existing).PlanAsync(Watch, "x", [F("x/Lantern.S01E04.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.False(plan.IsReady);
        Assert.Contains("already exists", plan.Review[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_film_dropped_for_a_tv_library_goes_to_review()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.False(plan.IsReady);
    }

    [Fact]
    public async Task Extras_are_filed_under_their_film()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv"), F("r/Featurettes/Building the Rocket.mkv", 300_000_000)], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Extra
            && o.Destination == Path.Combine("/lib/Movies", "Rocket Club (2019) [tmdbid-123]", "featurettes", "Building the Rocket.mkv"));
    }

    [Fact]
    public void Pairs_per_episode_subtitle_folders_and_reads_flags()
    {
        var videos = new[] { "p/Show.S01E01.mkv", "p/Show.S01E02.mkv" };
        var subs = new[] { "p/Subs/Show.S01E01/2_English.srt", "p/Subs/Show.S01E02/3_English.SDH.srt", "p/Subs/Show.S01E02/Commentary.en.srt", "p/Subs/other/stray.srt" };

        var paired = SubtitlePairer.Pair(videos, subs, _ => null, out var unpaired);

        Assert.Equal("en", Assert.Single(paired["p/Show.S01E01.mkv"]).Track.Language);
        var second = paired["p/Show.S01E02.mkv"];
        Assert.Contains(second, s => s.Track.HearingImpaired);
        Assert.Contains(second, s => s.Track.Title == "Commentary");
        Assert.Equal("p/Subs/other/stray.srt", Assert.Single(unpaired));
    }

    [Fact]
    public void Sniffer_recognises_english_and_refuses_short_text()
    {
        var english = string.Join(' ', Enumerable.Repeat("I think that you and the others have what it is for this", 30));
        Assert.Equal("en", SubtitleLanguageSniffer.Guess(english));
        Assert.Null(SubtitleLanguageSniffer.Guess("Hello there."));
    }

    private sealed class FakeFs : IFileOperations
    {
        public Dictionary<string, long> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Log { get; } = [];

        public bool Exists(string path) => Files.ContainsKey(path);

        public long Length(string path) => Files[path];

        public void CreateDirectory(string path)
        {
        }

        public void Move(string source, string destination)
        {
            if (Files.ContainsKey(destination))
            {
                throw new IOException("exists");
            }

            Files[destination] = Files[source];
            Files.Remove(source);
        }

        public void DeleteEmptyDirectories(string path)
        {
        }

        public void AppendLine(string path, string line) => Log.Add(line);
    }

    [Fact]
    public void Executor_moves_logs_and_can_dry_run()
    {
        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations = [new PlannedOperation(OperationKind.Video, "/drop/r/a.mkv", "/lib/A/a.mkv"), new PlannedOperation(OperationKind.Quarantine, "/drop/r/x.txt", "/q/r/x.txt")],
        };
        var fs = new FakeFs();
        fs.Files["/drop/r/a.mkv"] = 100;
        fs.Files["/drop/r/x.txt"] = 1;
        var executor = new PlanExecutor(fs, TimeProvider.System);

        var dry = executor.Execute(plan, "/drop/r", "/log.jsonl", dryRun: true);
        Assert.True(dry.DryRun);
        Assert.True(fs.Exists("/drop/r/a.mkv"));

        var real = executor.Execute(plan, "/drop/r", "/log.jsonl", dryRun: false);
        Assert.True(real.Succeeded);
        Assert.Equal(100, fs.Length("/lib/A/a.mkv"));
        Assert.Equal(2, fs.Log.Count);
    }

    [Fact]
    public void Executor_stops_at_a_destination_that_appeared_after_planning()
    {
        var plan = new IngestPlan { ReleaseName = "r", Operations = [new PlannedOperation(OperationKind.Video, "/drop/r/a.mkv", "/lib/A/a.mkv")] };
        var fs = new FakeFs();
        fs.Files["/drop/r/a.mkv"] = 100;
        fs.Files["/lib/A/a.mkv"] = 5;

        var report = new PlanExecutor(fs, TimeProvider.System).Execute(plan, "/drop/r", "/log.jsonl", dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Equal(5, fs.Length("/lib/A/a.mkv"));
        Assert.True(fs.Exists("/drop/r/a.mkv"));
    }

    [Fact]
    public async Task Files_as_the_title_chosen_in_review_without_searching()
    {
        var chosen = new MetadataCandidate { Name = "Harbour Lights", Year = 2011, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "55", ["Tmdb"] = "66" } };
        var plan = await Planner().PlanAsync(Watch, "hl", [F("hl/Harbour.Lights.S02E03.mkv")], LibraryTargets.Of(Tv), Quarantine, new ChosenMatch(chosen, Tv), CancellationToken.None);

        Assert.True(plan.IsReady);
        Assert.Equal("/lib/Shows/Harbour Lights (2011) [tvdbid-55] [tmdbid-66]/Season 02/Harbour Lights S02E03.mkv", plan.Operations[0].Destination);
    }

    [Fact]
    public async Task Review_items_carry_the_candidates_considered()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.False(plan.IsReady);
        var item = Assert.Single(plan.Review);
        Assert.Equal(["123", "124"], item.Candidates.Select(c => c.Candidate.ProviderIds["Tmdb"]));
    }

    [Fact]
    public async Task Routes_films_and_episodes_to_their_own_library()
    {
        var both = new LibraryTargets(Tv, Films);
        var film = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], both, Quarantine, null, CancellationToken.None);
        var episode = await Planner().PlanAsync(Watch, "a", [F("a/Lantern.S01E04.mkv")], both, Quarantine, null, CancellationToken.None);

        Assert.StartsWith("/lib/Movies/Rocket Club (2019) [tmdbid-123]/", film.Operations[0].Destination, StringComparison.Ordinal);
        Assert.StartsWith("/lib/Shows/Lantern (2001) ", episode.Operations[0].Destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_film_without_a_film_library_keeps_its_match_for_review()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        var item = Assert.Single(plan.Review);
        Assert.Contains("no film library", item.Reason, StringComparison.Ordinal);
        Assert.Equal("123", item.Candidates[0].Candidate.ProviderIds["Tmdb"]);
        Assert.False(item.Candidates[0].Candidate.IsSeries);
    }

    [Fact]
    public async Task A_film_chosen_in_review_goes_to_the_chosen_library()
    {
        var gay = new LibraryTarget("/lib/Other Movies", IsTv: false);
        var chosen = new ChosenMatch(new MetadataCandidate { Name = "Rocket Club", Year = 2019, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } }, gay);
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], new LibraryTargets(Tv, Films), Quarantine, chosen, CancellationToken.None);

        Assert.StartsWith("/lib/Other Movies/Rocket Club (2019) [tmdbid-123]/", plan.Operations[0].Destination, StringComparison.Ordinal);
    }

    [Fact]
    public void The_main_subtitle_is_the_default_when_a_language_has_several()
    {
        var en = new Jellyfin.Plugin.Ingest.Naming.SubtitleTrack { Language = "en" };
        var subs = new PairedSubtitle[]
        {
            new("r/Commentary.en.srt", en with { Title = "Commentary" }),
            new("r/en.sdh.srt", en with { HearingImpaired = true }),
            new("r/en.srt", en),
            new("r/en.forced.srt", en with { Forced = true }),
            new("r/fr.srt", new Jellyfin.Plugin.Ingest.Naming.SubtitleTrack { Language = "fr" }),
        };

        var result = IngestPlanner.WithMainTrackDefault(subs);

        Assert.Equal(["r/en.srt"], result.Where(s => s.Track.Default).Select(s => s.RelativePath));
    }

    [Fact]
    public void A_single_subtitle_per_language_is_left_alone()
    {
        var subs = new PairedSubtitle[] { new("r/en.srt", new Jellyfin.Plugin.Ingest.Naming.SubtitleTrack { Language = "en" }) };
        Assert.False(Assert.Single(IngestPlanner.WithMainTrackDefault(subs)).Track.Default);
    }

    [Fact]
    public async Task A_new_episode_joins_its_show_in_whichever_library_it_lives()
    {
        var elsewhere = "/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]";
        var locator = new Existing(elsewhere);
        var plan = await Planner(existing: locator).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.Equal(Path.Combine(elsewhere, "Season 01", "Lantern S01E04 - Glass Harbour.mkv"), Assert.Single(plan.Operations).Destination);
        Assert.Equal("7", locator.Asked[0]["Tvdb"]);
    }

    [Fact]
    public async Task A_show_already_on_the_server_is_followed_even_without_a_tv_destination()
    {
        var elsewhere = "/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]";
        var plan = await Planner(existing: new Existing(elsewhere)).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady);
        Assert.StartsWith(elsewhere, plan.Operations[0].Destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_show_goes_to_the_watch_folders_destination()
    {
        var plan = await Planner(existing: new Existing(null)).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.StartsWith("/lib/Shows/Lantern (2001) ", plan.Operations[0].Destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_chosen_in_review_wins_over_following_the_show()
    {
        var locator = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        var chosen = new ChosenMatch(new MetadataCandidate { Name = "Lantern", Year = 2001, IsSeries = true, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "7", ["Tmdb"] = "9" } }, Tv);
        var plan = await Planner(existing: locator).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.mkv")], LibraryTargets.Of(Tv), Quarantine, chosen, CancellationToken.None);

        Assert.StartsWith("/lib/Shows/", plan.Operations[0].Destination, StringComparison.Ordinal);
        Assert.Empty(locator.Asked);
    }

    [Fact]
    public async Task An_episode_already_on_the_server_goes_to_review()
    {
        var existing = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        existing.Episodes[(1, 4)] = "/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]/Season 01/Lantern S01E04.mp4";

        var plan = await Planner(existing: existing).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.720p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        var item = Assert.Single(plan.Review);
        Assert.Contains("already on the server", item.Reason, StringComparison.Ordinal);
        Assert.Contains("Lantern S01E04.mp4", item.Reason, StringComparison.Ordinal);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public async Task A_multi_episode_file_overlapping_an_existing_episode_goes_to_review()
    {
        var existing = new Existing();
        existing.Episodes[(1, 5)] = "/lib/Shows/x/Season 01/Lantern S01E05.mkv";

        var plan = await Planner(existing: existing).PlanAsync(Watch, "a", [F("a/Lantern.S01E04E05.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.Contains("already on the server", Assert.Single(plan.Review).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_episode_twice_in_one_release_goes_to_review()
    {
        var plan = await Planner(existing: new Existing()).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv"), F("a/Lantern.S01E04.720p.mp4")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.Contains("more than once", Assert.Single(plan.Review).Reason, StringComparison.Ordinal);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public async Task A_film_already_on_the_server_goes_to_review_but_another_edition_is_filed()
    {
        var existing = new Existing();
        existing.Movies.Add(("123", null, "/lib/Other Movies/Rocket Club (2019) [tmdbid-123]/Rocket Club (2019) [tmdbid-123].mp4"));

        var same = await Planner(existing: existing).PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);
        var cut = await Planner(existing: existing).PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.Directors.Cut.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.Contains("already on the server", Assert.Single(same.Review).Reason, StringComparison.Ordinal);
        Assert.True(cut.IsReady);
        Assert.EndsWith(" - Director's Cut.mkv", cut.Operations[0].Destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_episode_uses_the_season_folder_the_show_already_has()
    {
        var show = "/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]";
        var existing = new Existing(show) { SeasonFolder = show + "/Season 1" };
        var plan = await Planner(existing: existing).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.Equal(show + "/Season 1/Lantern S01E04 - Glass Harbour.mkv", Assert.Single(plan.Operations).Destination);
    }

    private sealed class TidyFails : IFileOperations
    {
        private readonly HashSet<string> _files = new(StringComparer.Ordinal) { Watch + "/r/Rocket.Club.2019.1080p.mkv" };

        public bool Exists(string path) => _files.Contains(path) || path == Watch + "/r";

        public long Length(string path) => 1;

        public void CreateDirectory(string path)
        {
        }

        public void Move(string source, string destination)
        {
            _files.Remove(source);
            _files.Add(destination);
        }

        public void AppendLine(string path, string line)
        {
        }

        public void DeleteEmptyDirectories(string path) => throw new IOException("Directory not empty");
    }

    [Fact]
    public async Task A_tidy_up_failure_after_every_move_is_a_warning_not_a_failure()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);
        var report = new PlanExecutor(new TidyFails(), new FixedClock(DateTimeOffset.UnixEpoch)).Execute(plan, Watch + "/r", "/log", dryRun: false);

        Assert.True(report.Succeeded);
        Assert.Equal("Directory not empty", report.Warning);
    }
}
