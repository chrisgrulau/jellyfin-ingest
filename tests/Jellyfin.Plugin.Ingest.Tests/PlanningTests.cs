using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
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

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EpisodeListing>>([]);
    }

    // Folders (no extension) exist unless the libraries are offline; files exist only when the test says so
    private static IngestPlanner Planner(Func<string, bool>? exists = null, IExistingMedia? existing = null, IMetadataLookup? lookup = null, bool librariesOnline = true)
        => new(
            new MediaIdentifier(lookup ?? new Lookup()),
            p => (librariesOnline && !Path.HasExtension(p)) || (exists?.Invoke(p) ?? false),
            _ => null,
            new FixedClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)),
            existing,
            p => PathGuard.IsUnder(p, "/lib"));

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

        public bool Offline { get; set; }

        public bool Exists(string path) => Files.ContainsKey(path) || (!Offline && !Path.HasExtension(path));

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

        public void Delete(string path) => Files.Remove(path);

        public bool HasRoomFor(string source, string destinationFolder, long bytes) => true;
    }

    [Fact]
    public void Executor_moves_logs_and_can_dry_run()
    {
        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations = [new PlannedOperation(OperationKind.Video, "/drop/r/a.mkv", "/lib/A/a.mkv"), new PlannedOperation(OperationKind.Quarantine, "/drop/r/x.txt", "/q/r/x.txt")],
            AllowedRoots = ["/lib/A", "/q"],
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
        Assert.Equal(4, fs.Log.Count); // an intent and a done record per move
        Assert.DoesNotContain(fs.Files.Keys, k => k.Contains(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public void Executor_stops_at_a_destination_that_appeared_after_planning()
    {
        var plan = new IngestPlan { ReleaseName = "r", Operations = [new PlannedOperation(OperationKind.Video, "/drop/r/a.mkv", "/lib/A/a.mkv")], AllowedRoots = ["/lib/A"] };
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

        public bool Exists(string path) => _files.Contains(path) || path == Watch + "/r" || path == "/lib/Movies";

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

        public void Delete(string path) => _files.Remove(path);

        public bool HasRoomFor(string source, string destinationFolder, long bytes) => true;
    }

    [Fact]
    public async Task A_tidy_up_failure_after_every_move_is_a_warning_not_a_failure()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);
        var report = new PlanExecutor(new TidyFails(), new FixedClock(DateTimeOffset.UnixEpoch)).Execute(plan, Watch + "/r", "/log", dryRun: false);

        Assert.True(report.Succeeded);
        Assert.Equal("Directory not empty", report.Warning);
    }

    private sealed class HostileLookup : IMetadataLookup
    {
        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);

        // A metadata plugin (or NFO edit) returning an id crafted to escape the library folder
        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([new MetadataCandidate { Name = "Rocket Club", Year = 2019, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1]/../../../../srv/x" } }]);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EpisodeListing>>([]);
    }

    [Fact]
    public async Task A_crafted_provider_id_cannot_move_a_file_out_of_the_library()
    {
        var plan = await Planner(lookup: new HostileLookup()).PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        var video = Assert.Single(plan.Operations, o => o.Kind == OperationKind.Video);
        Assert.True(PathGuard.IsUnder(video.Destination, "/lib/Movies"));
        Assert.DoesNotContain("..", video.Destination, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdbid", video.Destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_show_outside_every_library_is_not_joined()
    {
        var plan = await Planner(existing: new Existing("/srv/elsewhere/Lantern")).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.Contains("outside the library", Assert.Single(plan.Review).Reason, StringComparison.Ordinal);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public async Task The_executor_refuses_a_plan_that_leaves_its_folders_before_moving_anything()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv"), F("r/info.nfo", 10)], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);
        var tampered = plan with { Operations = [plan.Operations[0], plan.Operations[1] with { Destination = "/etc/cron.d/x" }] };
        var fs = new TidyFails();

        var report = new PlanExecutor(fs, new FixedClock(DateTimeOffset.UnixEpoch)).Execute(tampered, Watch + "/r", "/log", dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Empty(report.Completed);
        Assert.Contains("outside the folders", report.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_offline_library_is_retried_not_recreated()
    {
        var plan = await Planner(librariesOnline: false).PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        var item = Assert.Single(plan.Review);
        Assert.Equal(RetryKind.FolderUnavailable, item.Retry);
        Assert.Equal(RetryKind.FolderUnavailable, plan.Retry);
        Assert.Contains("/lib/Movies", item.Reason, StringComparison.Ordinal);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public async Task The_executor_moves_nothing_if_the_library_went_offline_after_planning()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);
        Assert.Contains("/lib/Movies", plan.RequiredFolders);
        var fs = new FakeFs { Offline = true };
        fs.Files[Watch + "/r/Rocket.Club.2019.1080p.mkv"] = 1;

        var report = new PlanExecutor(fs, new FixedClock(DateTimeOffset.UnixEpoch)).Execute(plan, Watch + "/r", "/log", dryRun: false);

        Assert.False(report.Succeeded);
        Assert.True(report.FolderUnavailable);
        Assert.Empty(report.Completed);
        Assert.True(fs.Files.ContainsKey(Watch + "/r/Rocket.Club.2019.1080p.mkv"));
    }

    [Fact]
    public async Task Nothing_found_anywhere_is_worth_trying_again()
    {
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Completely.Unknown.Thing.2019.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.Equal(RetryKind.NothingFound, plan.Retry);
    }

    [Fact]
    public async Task A_close_call_needs_a_person()
    {
        // Two same-named films and no year: only a person can decide
        var plan = await Planner().PlanAsync(Watch, "r", [F("r/Rocket.Club.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.Equal(RetryKind.None, plan.Retry);
        Assert.NotEmpty(plan.Review);
    }

    // ING-30: replacing copies already on the server, when asked for in review
    private const string OldSeason = "/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]/Season 01";

    private static IngestPlanner Replacer(IExistingMedia existing, Func<string, bool>? exists = null, IEnumerable<string>? filesInSeason = null) =>
        new IngestPlanner(
            new MediaIdentifier(new Lookup()),
            p => !Path.HasExtension(p) || (exists?.Invoke(p) ?? false),
            _ => null,
            new FixedClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)),
            existing,
            p => PathGuard.IsUnder(p, "/lib"),
            dir => dir == OldSeason ? filesInSeason ?? [] : [])
        {
            ReplaceExisting = true,
        };

    [Fact]
    public async Task A_copy_on_the_server_is_offered_for_replacing_in_review()
    {
        var existing = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        existing.Episodes[(1, 4)] = OldSeason + "/Lantern S01E04 - Glass Harbour.avi";

        var plan = await Planner(existing: existing).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.Equal(OldSeason + "/Lantern S01E04 - Glass Harbour.avi", Assert.Single(plan.Review).Existing);
    }

    [Fact]
    public async Task Replacing_moves_the_old_copy_and_its_subtitles_to_quarantine_first_then_files_the_new_one()
    {
        var old = OldSeason + "/Lantern S01E04 - Glass Harbour.avi";
        var existing = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        existing.Episodes[(1, 4)] = old;
        string[] season = [old, OldSeason + "/Lantern S01E04 - Glass Harbour.eng.srt", OldSeason + "/Lantern S01E04 - Glass Harbour.nfo", OldSeason + "/Lantern S01E05 - Other.eng.srt"];

        var plan = await Replacer(existing, p => season.Contains(p), season).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady);
        Assert.Equal([old, OldSeason + "/Lantern S01E04 - Glass Harbour.eng.srt"], plan.Replacing);
        Assert.Equal(OperationKind.Quarantine, plan.Operations[0].Kind);
        Assert.Equal(old, plan.Operations[0].Source);
        Assert.StartsWith(Path.Combine(Quarantine, "2026-09-24", "Replaced"), plan.Operations[0].Destination, StringComparison.Ordinal);
        Assert.Equal(OperationKind.Quarantine, plan.Operations[1].Kind);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Video && o.Destination == OldSeason + "/Lantern S01E04 - Glass Harbour.mkv");
    }

    [Fact]
    public async Task A_new_file_may_take_the_exact_name_of_the_copy_it_replaces()
    {
        var old = OldSeason + "/Lantern S01E04 - Glass Harbour.mkv";
        var existing = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        existing.Episodes[(1, 4)] = old;

        var plan = await Replacer(existing, p => p == old).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Video && o.Destination == old);
        Assert.True(plan.Operations.ToList().FindIndex(o => o.Source == old) < plan.Operations.ToList().FindIndex(o => o.Destination == old));
    }

    [Fact]
    public async Task A_multi_episode_file_replaces_every_copy_it_covers()
    {
        var existing = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        existing.Episodes[(1, 4)] = OldSeason + "/Lantern S01E04.avi";
        existing.Episodes[(1, 5)] = OldSeason + "/Lantern S01E05.avi";

        var plan = await Replacer(existing).PlanAsync(Watch, "a", [F("a/Lantern.S01E04E05.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady);
        Assert.Equal([OldSeason + "/Lantern S01E04.avi", OldSeason + "/Lantern S01E05.avi"], plan.Replacing);
    }

    [Fact]
    public async Task A_copy_outside_the_libraries_is_never_replaced()
    {
        var existing = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        existing.Episodes[(1, 4)] = "/elsewhere/Lantern S01E04.avi";

        var plan = await Replacer(existing).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.False(plan.IsReady);
        Assert.Empty(plan.Replacing);
        Assert.Contains("already on the server", Assert.Single(plan.Review).Reason, StringComparison.Ordinal);
    }

    // Decisions made file by file in review
    private static IngestPlanner Deciding(IExistingMedia existing, Dictionary<string, FileDecision> decisions, Func<string, bool>? exists = null) =>
        new IngestPlanner(
            new MediaIdentifier(new Lookup()),
            p => !Path.HasExtension(p) || (exists?.Invoke(p) ?? false),
            _ => null,
            new FixedClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)),
            existing,
            p => PathGuard.IsUnder(p, "/lib"))
        {
            FileDecisions = decisions,
        };

    // Season and episode given in review (ING-30)
    private static IngestPlanner Numbered(Dictionary<string, EpisodeNumber> numbers) =>
        new IngestPlanner(
            new MediaIdentifier(new Lookup()),
            p => !Path.HasExtension(p),
            _ => null,
            new FixedClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)))
        {
            EpisodeNumbers = numbers,
        };

    [Fact]
    public async Task A_season_and_episode_given_in_review_files_a_video_whose_name_has_none()
    {
        ReleaseFile[] files = [F("a/Lantern.mkv"), F("a/Lantern.en.srt", 40_000)];
        var without = await Numbered([]).PlanAsync(Watch, "a", files, LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);
        Assert.False(without.IsReady);

        var plan = await Numbered(new() { ["a/Lantern.mkv"] = new EpisodeNumber(1, 4) }).PlanAsync(Watch, "a", files, LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        var season = Path.Combine("/lib/Shows", "Lantern (2001) [tvdbid-7] [tmdbid-9]", "Season 01");
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Video && o.Destination == Path.Combine(season, "Lantern S01E04 - Glass Harbour.mkv"));
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Subtitle && o.Destination == Path.Combine(season, "Lantern S01E04 - Glass Harbour.en.srt"));
    }

    [Fact]
    public async Task Numbers_given_in_review_win_over_the_name_and_work_with_a_chosen_show()
    {
        var chosen = new ChosenMatch(new MetadataCandidate { Name = "Lantern", Year = 2001, IsSeries = true, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "7" } }, Tv);
        var numbers = new Dictionary<string, EpisodeNumber> { ["a/Lantern - 125.mkv"] = new(3, 12), ["a/Lantern.S01E01.mkv"] = new(0, 2) };

        var plan = await Numbered(numbers).PlanAsync(Watch, "a", [F("a/Lantern - 125.mkv"), F("a/Lantern.S01E01.mkv")], LibraryTargets.Of(Tv), Quarantine, chosen, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.Contains(plan.Operations, o => o.Destination.EndsWith(Path.Combine("Season 03", "Lantern S03E12.mkv"), StringComparison.Ordinal));
        Assert.Contains(plan.Operations, o => o.Destination.EndsWith(Path.Combine("Season 00", "Lantern S00E02.mkv"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_chosen_for_quarantine_goes_there_with_its_subtitles_and_the_rest_is_filed()
    {
        var existing = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        existing.Episodes[(1, 4)] = OldSeason + "/Lantern S01E04 - Glass Harbour.avi";
        var decisions = new Dictionary<string, FileDecision> { ["a/Lantern.S01E04.1080p.mkv"] = FileDecision.Quarantine };

        var plan = await Deciding(existing, decisions).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv"), F("a/Lantern.S01E04.1080p.en.srt", 40_000), F("a/Lantern.S01E05.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.False(plan.WholeReleaseQuarantine);
        Assert.Equal([Path.Combine(Watch, "a/Lantern.S01E04.1080p.mkv")], plan.Skipped);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Quarantine && o.Source.EndsWith("S01E04.1080p.mkv", StringComparison.Ordinal));
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Quarantine && o.Source.EndsWith(".en.srt", StringComparison.Ordinal));
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Video && o.Source.EndsWith("S01E05.1080p.mkv", StringComparison.Ordinal));
        Assert.Empty(plan.Replacing);
    }

    [Fact]
    public async Task Replace_can_be_chosen_for_one_file_while_another_still_waits()
    {
        var existing = new Existing("/lib/Other Shows/Lantern (2001) [tvdbid-7] [tmdbid-9]");
        existing.Episodes[(1, 4)] = OldSeason + "/Lantern S01E04.avi";
        existing.Episodes[(1, 5)] = OldSeason + "/Lantern S01E05.avi";
        var decisions = new Dictionary<string, FileDecision> { ["a/Lantern.S01E04.1080p.mkv"] = FileDecision.Replace };

        var planner = Deciding(existing, decisions);
        var waiting = await planner.PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv"), F("a/Lantern.S01E05.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.False(waiting.IsReady);
        Assert.EndsWith("Lantern.S01E05.1080p.mkv", Assert.Single(waiting.Review).Source, StringComparison.Ordinal);

        decisions["a/Lantern.S01E05.1080p.mkv"] = FileDecision.Quarantine;
        var ready = await planner.PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv"), F("a/Lantern.S01E05.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(ready.IsReady);
        Assert.Equal([OldSeason + "/Lantern S01E04.avi"], ready.Replacing);
        Assert.Single(ready.Skipped);
    }

    [Fact]
    public async Task Choosing_quarantine_for_every_video_quarantines_the_release()
    {
        var decisions = new Dictionary<string, FileDecision> { ["a/Lantern.S01E04.mkv"] = FileDecision.Quarantine, ["a/Lantern.S01E05.mkv"] = FileDecision.Quarantine };

        var plan = await Deciding(new Existing(), decisions).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.mkv"), F("a/Lantern.S01E05.mkv"), F("a/readme.txt", 100)], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.WholeReleaseQuarantine);
        Assert.True(plan.IsReady);
        Assert.All(plan.Operations, o => Assert.Equal(OperationKind.Quarantine, o.Kind));
    }

    // ING-28: with copy or hard link the release stays, clutter included
    [Fact]
    public async Task Copy_mode_files_the_video_and_leaves_the_clutter()
    {
        var planner = new IngestPlanner(
            new MediaIdentifier(new Lookup()),
            p => !Path.HasExtension(p),
            _ => null,
            new FixedClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)))
        {
            Transfer = Jellyfin.Plugin.Ingest.Configuration.TransferMode.HardLink,
        };

        var plan = await planner.PlanAsync(Watch, "n00b-Lantern1E4", [F("n00b-Lantern1E4/n00b-Lantern1E4.mp4"), F("n00b-Lantern1E4/README.txt", 900), F("n00b-Lantern1E4/sample.mkv", 12_000_000)], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady);
        Assert.DoesNotContain(plan.Operations, o => o.Kind == OperationKind.Quarantine);
        Assert.Equal(Jellyfin.Plugin.Ingest.Configuration.TransferMode.HardLink, plan.Transfer);
    }

    // A replacement is filed where the copy it replaces lives
    private static readonly MediaLibrary[] ServerLibraries =
    [
        new("f1", "Films One", LibraryKind.Films, ["/lib/Movies"]),
        new("f2", "Films Two", LibraryKind.Films, ["/lib/Films Two"]),
        new("s1", "Shows One", LibraryKind.Shows, ["/lib/Shows"]),
        new("s2", "Shows Two", LibraryKind.Shows, ["/lib/Other Shows"]),
        new("h", "Home Videos", LibraryKind.Other, ["/lib/Home"]),
    ];

    private static IngestPlanner Following(IExistingMedia existing) =>
        new IngestPlanner(
            new MediaIdentifier(new Lookup()),
            p => !Path.HasExtension(p),
            _ => null,
            new FixedClock(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)),
            existing,
            p => PathGuard.IsUnder(p, "/lib"))
        {
            ReplaceExisting = true,
            Libraries = ServerLibraries,
        };

    private static PlannedOperation VideoOf(IngestPlan plan, string name)
        => Assert.Single(plan.Operations, o => o.Kind == OperationKind.Video && o.Source.EndsWith(name, StringComparison.Ordinal));

    [Fact]
    public async Task A_film_replacing_a_copy_in_another_movies_library_is_filed_there_in_the_same_folder()
    {
        var old = "/lib/Films Two/Rocket Club (2019)/Rocket Club.avi";
        var existing = new Existing();
        existing.Movies.Add(("123", null, old));

        var plan = await Following(existing).PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv"), F("r/Featurettes/Building the Rocket.mkv", 300_000_000)], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.Equal([old], plan.Replacing);
        Assert.Equal("/lib/Films Two/Rocket Club (2019)", Path.GetDirectoryName(VideoOf(plan, "Rocket.Club.2019.1080p.mkv").Destination));
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Extra && o.Destination == "/lib/Films Two/Rocket Club (2019)/featurettes/Building the Rocket.mkv");
        Assert.Contains("/lib/Films Two", plan.RequiredFolders);
        Assert.DoesNotContain(plan.Operations, o => o.Destination.StartsWith("/lib/Movies/", StringComparison.Ordinal));
        Assert.Empty(plan.TidyIfEmpty);
        Assert.Equal("Replacing 1 copy in Films Two.", plan.ReplacementSummary);
        Assert.Equal("Rocket.Club.2019.1080p.mkv: replacing Rocket Club.avi in Films Two.", Assert.Single(plan.ReplacementNotes));
    }

    [Fact]
    public async Task A_library_chosen_in_review_wins_over_where_the_replaced_copy_lives()
    {
        var old = "/lib/Films Two/Rocket Club (2019)/Rocket Club.avi";
        var existing = new Existing();
        existing.Movies.Add(("123", null, old));
        var chosen = new ChosenMatch(new MetadataCandidate { Name = "Rocket Club", Year = 2019, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } }, Films);

        var plan = await Following(existing).PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, chosen, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.Equal([old], plan.Replacing);
        Assert.StartsWith("/lib/Movies/Rocket Club (2019) [tmdbid-123]/", VideoOf(plan, "Rocket.Club.2019.1080p.mkv").Destination, StringComparison.Ordinal);
        Assert.Equal("Replacing 1 copy in Films One.", plan.ReplacementSummary);
        Assert.Contains("chosen in review", Assert.Single(plan.ReplacementNotes), StringComparison.Ordinal);

        // The old film folder is removed after filing, if nothing is left in it
        Assert.Equal([new EmptiedFolder("/lib/Films Two/Rocket Club (2019)", "/lib/Films Two")], plan.TidyIfEmpty);
    }

    [Fact]
    public async Task Episodes_of_a_season_pack_each_follow_the_copy_they_replace()
    {
        // The show is in two libraries, so there's no single show folder to join
        var existing = new Existing();
        existing.Episodes[(1, 4)] = "/lib/Shows/Lantern (2001)/Season 1/Lantern S01E04.avi";
        existing.Episodes[(1, 5)] = "/lib/Other Shows/Lantern/S01/Lantern 1x05.avi";

        var plan = await Following(existing).PlanAsync(Watch, "a", [F("a/Lantern.S01E04.1080p.mkv"), F("a/Lantern.S01E05.1080p.mkv"), F("a/Lantern.S01E06.1080p.mkv")], LibraryTargets.Of(Tv), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.Equal(2, plan.Replacing.Count);
        Assert.Equal("/lib/Shows/Lantern (2001)/Season 1/Lantern S01E04 - Glass Harbour.mkv", VideoOf(plan, "S01E04.1080p.mkv").Destination);
        Assert.Equal("/lib/Other Shows/Lantern/S01/Lantern S01E05.mkv", VideoOf(plan, "S01E05.1080p.mkv").Destination);
        Assert.Equal(Path.Combine("/lib/Shows", "Lantern (2001) [tvdbid-7] [tmdbid-9]", "Season 01", "Lantern S01E06.mkv"), VideoOf(plan, "S01E06.1080p.mkv").Destination);
        Assert.Equal("Replacing 2 copies in Shows One, Shows Two.", plan.ReplacementSummary);
        Assert.Empty(plan.TidyIfEmpty);
    }

    [Fact]
    public async Task A_replaced_copy_outside_a_library_of_its_kind_falls_back_to_normal_routing_and_its_folder_is_tidied()
    {
        var old = "/lib/Home/Rocket Club/Rocket Club.avi";
        var existing = new Existing();
        existing.Movies.Add(("123", null, old));

        var plan = await Following(existing).PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.Equal([old], plan.Replacing);
        Assert.StartsWith("/lib/Movies/Rocket Club (2019) [tmdbid-123]/", VideoOf(plan, "Rocket.Club.2019.1080p.mkv").Destination, StringComparison.Ordinal);
        var note = Assert.Single(plan.ReplacementNotes);
        Assert.Contains("in Films One", note, StringComparison.Ordinal);
        Assert.Contains("isn't in one of the server's Movies libraries", note, StringComparison.Ordinal);
        Assert.Equal([new EmptiedFolder("/lib/Home/Rocket Club", "/lib/Home")], plan.TidyIfEmpty);
    }

    [Fact]
    public async Task A_replaced_copy_in_a_library_that_is_no_longer_known_falls_back_and_nothing_is_tidied()
    {
        var old = "/lib/Removed/Rocket Club (2019)/Rocket Club.avi";
        var existing = new Existing();
        existing.Movies.Add(("123", null, old));

        var plan = await Following(existing).PlanAsync(Watch, "r", [F("r/Rocket.Club.2019.1080p.mkv")], LibraryTargets.Of(Films), Quarantine, null, CancellationToken.None);

        Assert.True(plan.IsReady, string.Join("; ", plan.Review.Select(r => r.Reason)));
        Assert.StartsWith("/lib/Movies/", VideoOf(plan, "Rocket.Club.2019.1080p.mkv").Destination, StringComparison.Ordinal);
        Assert.Contains("isn't in one of the server's Movies libraries", Assert.Single(plan.ReplacementNotes), StringComparison.Ordinal);
        Assert.Empty(plan.TidyIfEmpty);
    }
}
