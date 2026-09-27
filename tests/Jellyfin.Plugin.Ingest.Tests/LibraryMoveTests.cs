using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Presentation;
using Jellyfin.Plugin.Ingest.Quarantine;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Moving an automatically filed film or show to another library, on a real (temporary) file system. Invented titles
// and library names throughout.
public sealed class LibraryMoveTests : IDisposable
{
    private const string FilmFolder = "Harbour Lights (2024) [tmdbid-7]";
    private const string ShowFolder = "Lantern (2001) [tvdbid-5]";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ingest-move-" + Guid.NewGuid().ToString("N"));

    public LibraryMoveTests()
    {
        foreach (var d in new[] { Drop, Films, Films2, Shows, Shows2a, Shows2b, Mixed, Music, Quarantine })
        {
            Directory.CreateDirectory(d);
        }

        State = new IngestStateStore(Path.Combine(_root, "state.json"));
    }

    private string Drop => Path.Combine(_root, "drop");

    private string Quarantine => Path.Combine(Drop, ".ingest-quarantine");

    private string Dated => Path.Combine(Quarantine, "2026-09-25");

    private string Films => Path.Combine(_root, "lib", "Films");

    private string Films2 => Path.Combine(_root, "lib", "Classics");

    private string Shows => Path.Combine(_root, "lib", "Shows");

    private string Shows2a => Path.Combine(_root, "lib", "Kids A");

    private string Shows2b => Path.Combine(_root, "lib", "Kids B");

    private string Mixed => Path.Combine(_root, "lib", "Mixed");

    private string Music => Path.Combine(_root, "lib", "Music");

    private string LogPath => Path.Combine(_root, "actions.jsonl");

    private IngestStateStore State { get; }

    private IReadOnlyList<MediaLibrary> Libraries =>
    [
        new("films", "Movies", LibraryKind.Films, [Films]),
        new("classics", "Classics", LibraryKind.Films, [Films2]),
        new("shows", "Shows", LibraryKind.Shows, [Shows]),
        new("kids", "Kids", LibraryKind.Shows, [Shows2a, Shows2b]),
        new("mixed", "Everything", LibraryKind.Mixed, [Mixed]),
        new("music", "Music", LibraryKind.Other, [Music]),
    ];

    // Kids A has more room, so a new show goes there
    private MoveScope Scope => new(
        Libraries,
        new ReturnScope([Drop], [.. Libraries.SelectMany(l => l.Locations)], [Quarantine]),
        [Drop, Quarantine],
        p => PathGuard.SamePath(p, Shows2a) ? 1000 : 10,
        d => Directory.Exists(d) ? Directory.EnumerateDirectories(d) : [],
        d => Directory.Exists(d) ? Directory.EnumerateFiles(d) : []);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(Drop, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static void Put(string path, int bytes = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private string FilmVideo(string library) => Path.Combine(library, FilmFolder, FilmFolder + ".mkv");

    private string FilmSubtitle(string library) => Path.Combine(library, FilmFolder, FilmFolder + ".en.srt");

    private string FilmExtra(string library) => Path.Combine(library, FilmFolder, "featurettes", "Making Of.mkv");

    private static string Episode(string library, int n, string season = "Season 01") => Path.Combine(library, ShowFolder, season, $"Lantern S01E0{n}.mkv");

    private string Execute(string release, IReadOnlyList<PlannedOperation> ops, string[] roots, FilingChoice? chosenBy)
    {
        var plan = new IngestPlan { ReleaseName = release, Operations = ops, AllowedRoots = roots };
        var report = new PlanExecutor(new PhysicalFileOperations(), TimeProvider.System).Execute(plan, Path.Combine(Drop, release), LogPath, dryRun: false);
        Assert.True(report.Succeeded, report.Error);
        State.Record(new ActivityEntry
        {
            Time = DateTimeOffset.UtcNow,
            Status = ActivityStatus.Filed,
            Release = release,
            WatchFolder = Drop,
            Summary = "Filed.",
            Run = report.Run,
            ChosenBy = chosenBy,
            Videos = [.. ops.Where(o => o.Kind == OperationKind.Video).Select(o => o.Destination)],
        });
        return report.Run!;
    }

    // A film with a subtitle, an extra and clutter, filed into Movies as the sweep would
    private string FileFilm(FilingChoice? chosenBy = FilingChoice.Automatic)
    {
        QuarantineMarkers.Mark(Quarantine, Dated);
        return Execute(
            "f",
            [
                new(OperationKind.Video, Write("f/Harbour.Lights.2024.mkv", 1000), FilmVideo(Films)),
                new(OperationKind.Subtitle, Write("f/english.srt", 10), FilmSubtitle(Films)),
                new(OperationKind.Extra, Write("f/Extras/making.of.mkv", 300), FilmExtra(Films)),
                new(OperationKind.Quarantine, Write("f/info.nfo", 5), Path.Combine(Dated, "f", "info.nfo")),
            ],
            [Path.Combine(Films, FilmFolder), Dated],
            chosenBy);
    }

    // Two episodes and a subtitle of a season, filed into Shows
    private string FileSeason()
        => Execute(
            "s",
            [
                new(OperationKind.Video, Write("s/Lantern.S01E01.mkv", 700), Episode(Shows, 1)),
                new(OperationKind.Video, Write("s/Lantern.S01E02.mkv", 800), Episode(Shows, 2)),
                new(OperationKind.Subtitle, Write("s/Lantern.S01E02.srt", 20), Path.ChangeExtension(Episode(Shows, 2), ".en.srt")),
            ],
            [Path.Combine(Shows, ShowFolder)],
            FilingChoice.Automatic);

    private LibraryMove Mover(IFileOperations? fs = null) => new(State, fs ?? new PhysicalFileOperations(), TimeProvider.System);

    private ReturnOutcome Move(string run, string libraryId, IFileOperations? fs = null)
        => Mover(fs).Move(run, libraryId, Scope, File.ReadAllLines(LogPath), LogPath);

    private MoveOptions? Options(string run, out string? refusal)
        => LibraryMove.Options(State.FindFiling(run)!, State.Snapshot().Activity, File.ReadAllLines(LogPath), Scope, DateTimeOffset.UtcNow, out refusal);

    private ActivityEntry Filing(string run) => State.FindFiling(run)!;

    // ---- Eligibility ----

    [Fact]
    public void An_automatic_filing_can_be_moved_one_chosen_in_review_cant()
    {
        var auto = new ActivityEntry { Time = DateTimeOffset.UtcNow, Status = ActivityStatus.Filed, Release = "a", WatchFolder = Drop, Run = "r1", ChosenBy = FilingChoice.Automatic };
        var review = auto with { Run = "r2", ChosenBy = FilingChoice.Review };
        var now = DateTimeOffset.UtcNow;

        Assert.Null(LibraryMove.Ineligible(auto, [auto], now));
        Assert.Contains("chosen in review", LibraryMove.Ineligible(review, [review], now), StringComparison.Ordinal);

        var presenter = new IngestPresenter(Libraries);
        Assert.True(presenter.Activity(auto, now).CanMove);
        Assert.False(presenter.Activity(review, now).CanMove);
    }

    [Fact]
    public void Undone_moved_old_or_runless_filings_cant_be_moved()
    {
        var now = DateTimeOffset.UtcNow;
        var filing = new ActivityEntry { Time = now, Status = ActivityStatus.Filed, Release = "a", WatchFolder = Drop, Run = "r1", ChosenBy = FilingChoice.Automatic };

        Assert.NotNull(LibraryMove.Ineligible(filing with { UndoneAt = now }, [], now));
        Assert.NotNull(LibraryMove.Ineligible(filing with { MoveRun = "m", MovedTo = "Classics" }, [], now));
        Assert.NotNull(LibraryMove.Ineligible(filing with { Run = null }, [], now));
        Assert.NotNull(LibraryMove.Ineligible(filing with { Time = now - TimeSpan.FromDays(91) }, [], now));
        Assert.NotNull(LibraryMove.Ineligible(filing with { Status = ActivityStatus.DryRun }, [], now));
    }

    [Fact]
    public void An_older_filing_counts_as_automatic_only_without_an_earlier_review_decision()
    {
        var now = DateTimeOffset.UtcNow;
        var filing = new ActivityEntry { Time = now, Status = ActivityStatus.Filed, Release = "a", WatchFolder = Drop, Run = "r1" };
        var decision = new ActivityEntry { Time = now.AddMinutes(-5), Status = ActivityStatus.Decision, Release = "a", WatchFolder = Drop, Summary = "Chose X." };
        var other = decision with { Release = "b" };
        var later = decision with { Time = now.AddMinutes(5) };

        Assert.True(LibraryMove.IsAutomatic(filing, [filing, other, later]));
        Assert.False(LibraryMove.IsAutomatic(filing, [filing, decision]));
        Assert.Null(LibraryMove.Ineligible(filing, [filing], now));
        Assert.NotNull(LibraryMove.Ineligible(filing, [filing, decision], now));

        var page = new IngestPresenter(Libraries).ActivityPage([filing, decision], null, 0, 10, now);
        Assert.False(page.Items.Single(i => i.Status == "Filed").CanMove);
    }

    [Fact]
    public void A_filing_not_in_recent_activity_is_refused()
    {
        FileFilm();
        Assert.False(Move("nope", "classics").Succeeded);
        Assert.NotNull(Mover().Check("nope", "classics", Scope, File.ReadAllLines(LogPath)));
    }

    // ---- Compatible libraries ----

    [Fact]
    public void A_film_can_go_to_other_movies_and_mixed_libraries()
    {
        var run = FileFilm();

        var options = Options(run, out var refusal);

        Assert.Null(refusal);
        Assert.Equal("Movies", options!.From);
        Assert.False(options.IsSeries);
        Assert.Equal(["Classics", "Everything"], options.Targets.Select(t => t.Name));
    }

    [Fact]
    public void A_show_can_go_to_other_shows_and_mixed_libraries_in_the_roomiest_folder()
    {
        var run = FileSeason();

        var options = Options(run, out _);

        Assert.True(options!.IsSeries);
        Assert.Equal(["Everything", "Kids"], options.Targets.Select(t => t.Name));
        var kids = options.Targets.Single(t => t.LibraryId == "kids");
        Assert.Equal(Shows2a, kids.Folder);
        Assert.False(kids.HasTitle);
        Assert.True(options.ShowFolders);
    }

    [Fact]
    public void A_film_in_a_mixed_library_can_go_to_any_movies_library()
    {
        var run = Execute("m", [new(OperationKind.Video, Write("m/Harbour.Lights.2024.mkv", 100), FilmVideo(Mixed))], [Path.Combine(Mixed, FilmFolder)], FilingChoice.Automatic);

        var options = Options(run, out _);

        Assert.Equal("Everything", options!.From);
        Assert.Equal(["Classics", "Movies"], options.Targets.Select(t => t.Name));
    }

    [Fact]
    public void How_the_library_was_chosen_is_kept_with_the_filing()
    {
        var run = FileFilm(FilingChoice.Review);

        var reloaded = new IngestStateStore(Path.Combine(_root, "state.json")).FindFiling(run);

        Assert.Equal(FilingChoice.Review, reloaded!.ChosenBy);
        Assert.Contains("chosen in review", Mover().Check(run, "classics", Scope, File.ReadAllLines(LogPath)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_incompatible_or_the_same_library_is_refused()
    {
        var run = FileFilm();

        Assert.Contains("Movies or mixed", Mover().Check(run, "shows", Scope, File.ReadAllLines(LogPath)), StringComparison.Ordinal);
        Assert.Contains("Movies or mixed", Mover().Check(run, "music", Scope, File.ReadAllLines(LogPath)), StringComparison.Ordinal);
        Assert.Contains("already in Movies", Mover().Check(run, "films", Scope, File.ReadAllLines(LogPath)), StringComparison.Ordinal);
        Assert.NotNull(Mover().Check(run, "gone", Scope, File.ReadAllLines(LogPath)));
        Assert.Null(Mover().Check(run, "classics", Scope, File.ReadAllLines(LogPath)));
    }

    [Fact]
    public void A_library_folder_overlapping_a_watch_folder_is_refused()
    {
        var run = FileFilm();
        var scope = Scope with { Forbidden = [Films2] };

        var refusal = LibraryMove.Plan(Filing(run), State.Snapshot().Activity, File.ReadAllLines(LogPath), "classics", scope, new PhysicalFileOperations(), DateTimeOffset.UtcNow).Refusal;

        Assert.Contains("never files into", refusal, StringComparison.Ordinal);
    }

    // ---- The move ----

    [Fact]
    public void A_film_moves_with_its_subtitle_and_extra_and_the_old_folder_goes()
    {
        var run = FileFilm();
        var clutter = Path.Combine(Dated, "f", "info.nfo");

        var outcome = Move(run, "classics");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(1000, new FileInfo(FilmVideo(Films2)).Length);
        Assert.True(File.Exists(FilmSubtitle(Films2)));
        Assert.Equal(300, new FileInfo(FilmExtra(Films2)).Length);
        Assert.False(Directory.Exists(Path.Combine(Films, FilmFolder)));
        Assert.True(Directory.Exists(Films));
        Assert.True(File.Exists(clutter));
        Assert.Contains(Path.Combine(Films, FilmFolder), outcome.Refresh);
        Assert.Contains(Path.Combine(Films2, FilmFolder), outcome.Refresh);

        // The filing shows where it went, and the move is recorded with its own entry
        var filing = Filing(run);
        Assert.Equal("Classics", filing.MovedTo);
        Assert.NotNull(filing.MoveRun);
        var moved = Assert.Single(State.Snapshot().Activity, a => a.Status == ActivityStatus.Moved);
        Assert.StartsWith("Moved to Classics", moved.Summary, StringComparison.Ordinal);
        Assert.Equal([FilmVideo(Films2)], moved.Videos);
        Assert.NotNull(ActivityNotifier.NoteFor(moved));

        var view = new IngestPresenter(Libraries).Activity(filing, DateTimeOffset.UtcNow, State.Snapshot().Activity);
        Assert.False(view.CanMove);
        Assert.True(view.CanUndo);
        Assert.Contains("Later moved to Classics", view.Item.Summary, StringComparison.Ordinal);
        Assert.Contains("Classics", new IngestPresenter(Libraries).Activity(moved, DateTimeOffset.UtcNow).Item.Summary, StringComparison.Ordinal);

        // Moved once: not offered again
        Assert.NotNull(Mover().Check(run, "mixed", Scope, File.ReadAllLines(LogPath)));
    }

    [Fact]
    public void A_season_pack_moves_with_its_subtitles_into_a_mixed_library()
    {
        var run = FileSeason();

        var outcome = Move(run, "mixed");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(700, new FileInfo(Episode(Mixed, 1)).Length);
        Assert.Equal(800, new FileInfo(Episode(Mixed, 2)).Length);
        Assert.True(File.Exists(Path.ChangeExtension(Episode(Mixed, 2), ".en.srt")));
        Assert.False(Directory.Exists(Path.Combine(Shows, ShowFolder)));
        Assert.True(Directory.Exists(Shows));
    }

    [Fact]
    public void A_show_already_in_the_target_library_is_joined_in_its_own_season_folder()
    {
        var run = FileSeason();

        // Already in Kids' second folder (less room), with its own season folder naming
        Put(Episode(Shows2b, 5, "Season 1"));

        var options = Options(run, out _);
        var kids = options!.Targets.Single(t => t.LibraryId == "kids");
        Assert.Equal(Shows2b, kids.Folder);
        Assert.True(kids.HasTitle);

        var outcome = Move(run, "kids");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(File.Exists(Episode(Shows2b, 1, "Season 1")));
        Assert.True(File.Exists(Episode(Shows2b, 2, "Season 1")));
        Assert.True(File.Exists(Path.ChangeExtension(Episode(Shows2b, 2, "Season 1"), ".en.srt")));
        Assert.False(Directory.Exists(Path.Combine(Shows2b, ShowFolder, "Season 01")));
        Assert.False(Directory.Exists(Path.Combine(Shows2a, ShowFolder)));
    }

    [Fact]
    public void An_episode_already_in_the_target_library_refuses_the_whole_move()
    {
        var run = FileSeason();
        Put(Path.Combine(Mixed, ShowFolder, "Season 01", "Lantern S01E02 - Old Copy.mp4"));

        var outcome = Move(run, "mixed");

        Assert.False(outcome.Succeeded);
        Assert.Contains("Lantern S01E02 is already in Everything", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Episode(Shows, 1)));
        Assert.True(File.Exists(Episode(Shows, 2)));
        Assert.False(File.Exists(Episode(Mixed, 1)));
        Assert.Null(Filing(run).MoveRun);
        Assert.Contains("Couldn't move", State.Snapshot().Activity[0].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_taken_place_or_the_same_film_in_the_target_refuses_the_move()
    {
        var run = FileFilm();
        Put(Path.Combine(Films2, FilmFolder, FilmFolder + ".mp4"));

        var outcome = Move(run, "classics");

        Assert.False(outcome.Succeeded);
        Assert.Contains("already in Classics", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(FilmVideo(Films)));
        Assert.False(File.Exists(FilmVideo(Films2)));
    }

    [Fact]
    public void A_video_changed_since_filing_refuses_the_move()
    {
        var run = FileFilm();
        File.WriteAllBytes(FilmVideo(Films), new byte[999]);

        var outcome = Move(run, "classics");

        Assert.False(outcome.Succeeded);
        Assert.Contains("has changed since it was filed", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(FilmSubtitle(Films)));
        Assert.False(Directory.Exists(Path.Combine(Films2, FilmFolder)));
    }

    [Fact]
    public void A_missing_file_refuses_the_move()
    {
        var run = FileFilm();
        File.Delete(FilmExtra(Films));

        var outcome = Move(run, "classics");

        Assert.False(outcome.Succeeded);
        Assert.Contains("is missing", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(FilmVideo(Films)));
    }

    [Fact]
    public void A_subtitle_changed_since_filing_moves_as_it_is()
    {
        var run = FileFilm();
        File.WriteAllBytes(FilmSubtitle(Films), new byte[42]);

        var outcome = Move(run, "classics");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(42, new FileInfo(FilmSubtitle(Films2)).Length);
        Assert.Contains("1 subtitle had changed since filing", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_folders_left_truly_empty_inside_the_library_are_removed()
    {
        var run = FileFilm();

        // Something Jellyfin (or a person) put in the film folder stays, and so does the folder
        var poster = Path.Combine(Films, FilmFolder, "poster.jpg");
        Put(poster);

        var outcome = Move(run, "classics");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(File.Exists(poster));
        Assert.False(Directory.Exists(Path.Combine(Films, FilmFolder, "featurettes")));
        Assert.True(Directory.Exists(Films));

        // A film filed right under a library folder alone would leave the library folder empty: it is never removed
        var tidy = new ReturnScope([Drop], [Films], [Quarantine]).FoldersLeftBy([FilmVideo(Films)]);
        Assert.DoesNotContain(tidy, t => PathGuard.SamePath(t.Folder, Films));
    }

    [Fact]
    public void A_failure_part_way_puts_everything_back()
    {
        var run = FileFilm();
        var failing = new FailingMoves(new PhysicalFileOperations(), p => p == FilmExtra(Films));

        var outcome = Move(run, "classics", failing);

        Assert.False(outcome.Succeeded);
        Assert.Contains("everything was put back", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(FilmVideo(Films)));
        Assert.True(File.Exists(FilmSubtitle(Films)));
        Assert.True(File.Exists(FilmExtra(Films)));
        Assert.False(Directory.Exists(Path.Combine(Films2, FilmFolder)));
        Assert.Null(Filing(run).MoveRun);
        Assert.Null(Filing(run).MovedTo);

        // It can be tried again
        Assert.True(Move(run, "classics").Succeeded);
    }

    // ---- Undo after a move ----

    [Fact]
    public void Undo_follows_the_files_to_the_library_they_were_moved_to()
    {
        var run = FileFilm();
        Assert.True(Move(run, "classics").Succeeded);

        var outcome = new ReleaseUndo(State, new PhysicalFileOperations(), TimeProvider.System).Undo(run, new ReturnScope([Drop], [.. Libraries.SelectMany(l => l.Locations)], [Quarantine]), File.ReadAllLines(LogPath), LogPath);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(1000, new FileInfo(Path.Combine(Drop, "f", "Harbour.Lights.2024.mkv")).Length);
        Assert.True(File.Exists(Path.Combine(Drop, "f", "english.srt")));
        Assert.True(File.Exists(Path.Combine(Drop, "f", "Extras", "making.of.mkv")));
        Assert.True(File.Exists(Path.Combine(Drop, "f", "info.nfo")));
        Assert.False(Directory.Exists(Path.Combine(Films2, FilmFolder)));
        Assert.Contains(Path.Combine(Films2, FilmFolder), outcome.Refresh);
        Assert.NotNull(Filing(run).UndoneAt);
    }

    [Fact]
    public void Undo_after_a_move_still_refuses_a_file_changed_since_the_move()
    {
        var run = FileFilm();
        Assert.True(Move(run, "classics").Succeeded);
        File.WriteAllBytes(FilmVideo(Films2), new byte[5]);

        var outcome = new ReleaseUndo(State, new PhysicalFileOperations(), TimeProvider.System).Undo(run, new ReturnScope([Drop], [.. Libraries.SelectMany(l => l.Locations)], [Quarantine]), File.ReadAllLines(LogPath), LogPath);

        Assert.False(outcome.Succeeded);
        Assert.Contains(FilmVideo(Films2), outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Filed_moves_follow_only_what_the_move_completed()
    {
        var run = FileFilm();
        var lines = File.ReadAllLines(LogPath).ToList();

        // A move interrupted after its first file (a crash): only that file is followed
        var moveRun = "m1";
        lines.Add(System.Text.Json.JsonSerializer.Serialize(new ActionLogLine { Kind = "Video", Source = FilmVideo(Films), Destination = FilmVideo(Films2), Bytes = 1000, Phase = "done", Temp = "t1", Run = moveRun }));
        lines.Add(System.Text.Json.JsonSerializer.Serialize(new ActionLogLine { Kind = "Subtitle", Source = FilmSubtitle(Films), Destination = FilmSubtitle(Films2), Bytes = 10, Phase = "intent", Temp = "t2", Run = moveRun }));

        var moves = LibraryMove.FiledMoves(Filing(run) with { MoveRun = moveRun }, lines);

        Assert.Equal(FilmVideo(Films2), moves.Single(m => m.OperationKind == OperationKind.Video).Destination);
        Assert.Equal(FilmSubtitle(Films), moves.Single(m => m.OperationKind == OperationKind.Subtitle).Destination);
        Assert.Equal(Path.Combine(Dated, "f", "info.nfo"), moves.Single(m => m.OperationKind == OperationKind.Quarantine).Destination);
    }

    private sealed class FailingMoves(IFileOperations inner, Func<string, bool> failOn) : IFileOperations
    {
        public bool Exists(string path) => inner.Exists(path);

        public long Length(string path) => inner.Length(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string source, string destination)
        {
            if (failOn(source))
            {
                throw new IOException("disk error");
            }

            inner.Move(source, destination);
        }

        public void Delete(string path) => inner.Delete(path);

        public void Copy(string source, string destination) => inner.Copy(source, destination);

        public bool TryHardLink(string source, string destination) => inner.TryHardLink(source, destination);

        public void DeleteEmptyDirectories(string path) => inner.DeleteEmptyDirectories(path);

        public bool DeleteIfEmpty(string path) => inner.DeleteIfEmpty(path);

        public void AppendLine(string path, string line) => inner.AppendLine(path, line);

        public bool HasRoomFor(string source, string destinationFolder, long bytes) => true;

        public DateTime? LastWriteUtc(string path) => inner.LastWriteUtc(path);
    }
}
