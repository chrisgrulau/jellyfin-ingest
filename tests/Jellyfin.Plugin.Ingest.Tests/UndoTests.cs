using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Quarantine;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// FEAT-01: undoing a filing from Recent activity, on a real (temporary) file system. Invented titles throughout.
public sealed class UndoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ingest-undo-" + Guid.NewGuid().ToString("N"));

    public UndoTests()
    {
        Directory.CreateDirectory(Drop);
        Directory.CreateDirectory(Films);
        Directory.CreateDirectory(Quarantine);
        State = new IngestStateStore(Path.Combine(_root, "state.json"));
    }

    private string Drop => Path.Combine(_root, "drop");

    private string Films => Path.Combine(_root, "lib", "Films");

    private string Quarantine => Path.Combine(Drop, ".ingest-quarantine");

    private string Dated => Path.Combine(Quarantine, "2026-09-25");

    private string LogPath => Path.Combine(_root, "actions.jsonl");

    private string Title => Path.Combine(Films, "Rocket Club (2019) [tmdbid-123]");

    private string Video => Path.Combine(Title, "Rocket Club (2019) [tmdbid-123].mkv");

    private string Subtitle => Path.Combine(Title, "Rocket Club (2019) [tmdbid-123].en.srt");

    private IngestStateStore State { get; }

    private ReturnScope Scope => new([Drop], [Films], [Quarantine]);

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

    // Files a release as the service would, and records its activity entry; returns the run
    private string FileRelease(TransferMode transfer = TransferMode.Move, bool clutter = true, IReadOnlyList<PlannedOperation>? before = null, IReadOnlyList<string>? replacing = null)
    {
        var ops = new List<PlannedOperation>(before ?? [])
        {
            new(OperationKind.Video, Write("r/Rocket.Club.2019.mkv", 1000), Video),
            new(OperationKind.Subtitle, Write("r/english.srt", 10), Subtitle),
        };
        if (clutter)
        {
            ops.Add(new(OperationKind.Quarantine, Write("r/info.nfo", 5), Path.Combine(Dated, "r", "info.nfo")));
            QuarantineMarkers.Mark(Quarantine, Dated);
        }

        if (before is not null)
        {
            QuarantineMarkers.Mark(Quarantine, Dated);
        }

        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations = ops,
            AllowedRoots = [Title, Dated],
            Transfer = transfer,
            Replacing = replacing ?? [],
        };
        var report = new PlanExecutor(new PhysicalFileOperations(), TimeProvider.System).Execute(plan, Path.Combine(Drop, "r"), LogPath, dryRun: false);
        Assert.True(report.Succeeded, report.Error);
        State.Record(new ActivityEntry { Time = DateTimeOffset.UtcNow, Status = ActivityStatus.Filed, Release = "r", WatchFolder = Drop, Summary = "Filed.", Run = report.Run });
        if (transfer != TransferMode.Move)
        {
            State.MarkCopied(new CopiedRelease(Drop, "r", "sig"));
        }

        return report.Run!;
    }

    private ReturnOutcome Undo(string run, IFileOperations? fs = null)
        => new ReleaseUndo(State, fs ?? new PhysicalFileOperations(), TimeProvider.System).Undo(run, Scope, File.ReadAllLines(LogPath), LogPath);

    private PendingReview? Review => State.GetReview(IngestStateStore.ReviewId(Drop, "r"));

    [Fact]
    public void A_moved_release_goes_back_to_the_watch_folder_and_waits_for_review()
    {
        var run = FileRelease();

        var outcome = Undo(run);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(1000, new FileInfo(Path.Combine(Drop, "r", "Rocket.Club.2019.mkv")).Length);
        Assert.True(File.Exists(Path.Combine(Drop, "r", "english.srt")));
        Assert.True(File.Exists(Path.Combine(Drop, "r", "info.nfo")));

        // The folders the filing created are gone; the library and the (marked) dated quarantine folder stay
        Assert.False(Directory.Exists(Title));
        Assert.True(Directory.Exists(Films));
        Assert.False(Directory.Exists(Path.Combine(Dated, "r")));
        Assert.True(QuarantineMarkers.IsMarked(Dated));

        // Held for review, so the sweep doesn't file it again by itself
        var review = Review;
        Assert.NotNull(review);
        Assert.True(review!.Held);
        Assert.Equal(ReleaseUndo.HeldReason, Assert.Single(review.Items).Reason);
        Assert.True(IngestService.IsHeld(review));

        // The filing shows as undone, the undo is recorded, and its moves are in the action log
        var activity = State.Snapshot().Activity;
        Assert.NotNull(Assert.Single(activity, a => a.Status == ActivityStatus.Filed).UndoneAt);
        Assert.Contains("Undone by an administrator", Assert.Single(activity, a => a.Status == ActivityStatus.Undone).Summary, StringComparison.Ordinal);
        var undoLines = File.ReadAllLines(LogPath).Select(ActionLogLine.TryParse).OfType<ActionLogLine>().Where(l => l.Run != run).ToList();
        Assert.Equal(3, undoLines.Count(l => l.Phase == "done"));
    }

    [Fact]
    public void An_undone_filing_cant_be_undone_twice()
    {
        var run = FileRelease();
        Assert.True(Undo(run).Succeeded);

        var again = Undo(run);

        Assert.False(again.Succeeded);
        Assert.Contains("already been undone", again.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.HardLink)]
    public void A_copied_or_linked_release_has_its_library_copies_deleted_and_the_originals_kept(TransferMode transfer)
    {
        var run = FileRelease(transfer, clutter: false);
        Assert.True(File.Exists(Video));

        var outcome = Undo(run);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(File.Exists(Video));
        Assert.False(Directory.Exists(Title));
        Assert.Equal(1000, new FileInfo(Path.Combine(Drop, "r", "Rocket.Club.2019.mkv")).Length);
        Assert.True(File.Exists(Path.Combine(Drop, "r", "english.srt")));
        Assert.DoesNotContain(Directory.EnumerateFiles(Films, "*", SearchOption.AllDirectories), f => f.EndsWith(".undone", StringComparison.Ordinal));
        Assert.Equal(2, File.ReadAllLines(LogPath).Select(ActionLogLine.TryParse).Count(l => l?.Phase == "deleted"));

        // The record of having filed it is cleared, so the review sees it; it waits, held
        Assert.False(State.WasCopied(Drop, "r", "sig"));
        Assert.True(Review!.Held);
    }

    [Fact]
    public void A_copied_release_whose_original_changed_isnt_undone()
    {
        var run = FileRelease(TransferMode.Copy, clutter: false);
        File.AppendAllText(Path.Combine(Drop, "r", "english.srt"), "more");

        var outcome = Undo(run);

        Assert.False(outcome.Succeeded);
        Assert.Contains("has gone or changed", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Video));
        Assert.True(State.WasCopied(Drop, "r", "sig"));
    }

    [Fact]
    public void Replaced_copies_go_back_into_the_library()
    {
        Directory.CreateDirectory(Title);
        File.WriteAllBytes(Video, new byte[777]);
        var replacedTo = Path.Combine(Dated, "Replaced", "Rocket Club (2019) [tmdbid-123].mkv");
        var run = FileRelease(before: [new PlannedOperation(OperationKind.Quarantine, Video, replacedTo)], replacing: [Video], clutter: false);
        Assert.Equal(1000, new FileInfo(Video).Length);
        Assert.Equal(777, new FileInfo(replacedTo).Length);

        var outcome = Undo(run);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(777, new FileInfo(Video).Length);
        Assert.False(File.Exists(replacedTo));
        Assert.Equal(1000, new FileInfo(Path.Combine(Drop, "r", "Rocket.Club.2019.mkv")).Length);
        Assert.Contains("replaced copy", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(outcome.Refresh, f => PathGuard.SamePath(f, Title));
    }

    [Fact]
    public void A_file_changed_since_filing_refuses_the_whole_undo()
    {
        var run = FileRelease();
        File.AppendAllText(Subtitle, "edited");

        var outcome = Undo(run);

        Assert.False(outcome.Succeeded);
        Assert.Contains("has changed since it was filed", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Video));
        Assert.True(File.Exists(Path.Combine(Dated, "r", "info.nfo")));
        Assert.Null(Review);
        Assert.Contains(State.Snapshot().Activity, a => a.Status == ActivityStatus.Failed && a.Summary.StartsWith("Couldn't undo", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_touched_since_filing_refuses_the_undo()
    {
        var run = FileRelease();
        File.SetLastWriteTimeUtc(Video, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var outcome = Undo(run);

        Assert.False(outcome.Succeeded);
        Assert.Contains("has changed", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Video));
    }

    [Fact]
    public void A_missing_file_refuses_the_undo()
    {
        var run = FileRelease();
        File.Delete(Subtitle);

        var outcome = Undo(run);

        Assert.False(outcome.Succeeded);
        Assert.Contains("is missing", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Video));
    }

    [Fact]
    public void An_occupied_place_refuses_the_undo()
    {
        var run = FileRelease();
        Write("r/Rocket.Club.2019.mkv", 3);

        var outcome = Undo(run);

        Assert.False(outcome.Succeeded);
        Assert.Contains("is taken now", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Video));
        Assert.Equal(3, new FileInfo(Path.Combine(Drop, "r", "Rocket.Club.2019.mkv")).Length);
    }

    [Fact]
    public void A_watch_folder_no_longer_configured_refuses_the_undo()
    {
        var run = FileRelease();

        var outcome = new ReleaseUndo(State, new PhysicalFileOperations(), TimeProvider.System).Undo(run, new ReturnScope([], [Films], [Quarantine]), File.ReadAllLines(LogPath), LogPath);

        Assert.False(outcome.Succeeded);
        Assert.Contains("no longer set up", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filing_older_than_the_action_log_keeps_cant_be_undone()
    {
        var filing = new ActivityEntry { Time = DateTimeOffset.UtcNow.AddDays(-91), Status = ActivityStatus.Filed, Release = "r", WatchFolder = Drop, Run = "abc" };

        var plan = ReleaseUndo.Plan(filing, [], Scope, new PhysicalFileOperations(), DateTimeOffset.UtcNow);

        Assert.Contains("90 days", plan.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_answers_without_changing_anything()
    {
        var run = FileRelease();
        var undo = new ReleaseUndo(State, new PhysicalFileOperations(), TimeProvider.System);

        Assert.Null(undo.Check(run, Scope, File.ReadAllLines(LogPath)));
        Assert.Equal("That filing isn't in Recent activity any more.", undo.Check("nope", Scope, File.ReadAllLines(LogPath)));
        Assert.True(File.Exists(Video));
        Assert.Null(Review);
    }

    [Fact]
    public void A_failure_part_way_puts_everything_back_as_filed()
    {
        var run = FileRelease();

        // The second file to go back fails: the first is moved back into the library again
        var fs = new FailingMoves(new PhysicalFileOperations(), failOn: s => s.EndsWith(".en.srt", StringComparison.Ordinal));
        var outcome = Undo(run, fs);

        Assert.False(outcome.Succeeded);
        Assert.Contains("everything was put back", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Video));
        Assert.True(File.Exists(Subtitle));
        Assert.True(File.Exists(Path.Combine(Dated, "r", "info.nfo")));
        Assert.False(File.Exists(Path.Combine(Drop, "r", "info.nfo")));
        Assert.Null(Review);
        Assert.Null(Assert.Single(State.Snapshot().Activity, a => a.Status == ActivityStatus.Filed).UndoneAt);
        Assert.Contains(File.ReadAllLines(LogPath).Select(ActionLogLine.TryParse), l => l?.Phase == "rolled-back");
    }

    [Fact]
    public void A_failed_undo_of_a_copied_release_keeps_its_record()
    {
        var run = FileRelease(TransferMode.Copy, clutter: false);
        var fs = new FailingMoves(new PhysicalFileOperations(), failOn: s => s.EndsWith(".mkv", StringComparison.Ordinal));

        var outcome = Undo(run, fs);

        Assert.False(outcome.Succeeded);
        Assert.True(File.Exists(Video));
        Assert.True(File.Exists(Subtitle));
        Assert.True(State.WasCopied(Drop, "r", "sig"));
        Assert.Null(Review);
    }

    [Fact]
    public void Library_copies_set_aside_by_an_interrupted_undo_are_deleted_at_the_next_start()
    {
        Directory.CreateDirectory(Title);
        var aside = PlanExecutor.SetAsidePathFor(Video);
        File.WriteAllBytes(aside, new byte[5]);
        var line = System.Text.Json.JsonSerializer.Serialize(new ActionLogLine { Time = "t", Release = "r", Kind = "Video", Source = Video, Destination = aside, Bytes = 5, Phase = "done", Temp = aside + ".x", Run = "u" });
        File.WriteAllLines(LogPath, [line]);
        var executor = new PlanExecutor(new PhysicalFileOperations(), TimeProvider.System);

        var results = executor.FinishDeletes(File.ReadAllLines(LogPath), LogPath, [Films]);

        Assert.Single(results);
        Assert.False(File.Exists(aside));
        Assert.Empty(executor.FinishDeletes(File.ReadAllLines(LogPath), LogPath, [Films]));
    }

    [Fact]
    public void Only_hidden_set_aside_names_inside_a_library_count()
    {
        Assert.True(PlanExecutor.IsSetAside(PlanExecutor.SetAsidePathFor(Video), [Films]));
        Assert.False(PlanExecutor.IsSetAside(Video, [Films]));
        Assert.False(PlanExecutor.IsSetAside(PlanExecutor.SetAsidePathFor(Path.Combine(Drop, "x.mkv")), [Films]));
    }

    [Fact]
    public void A_held_release_is_left_alone_until_someone_decides()
    {
        var id = IngestStateStore.ReviewId(Drop, "r");
        State.PutReview(new PendingReview { Id = id, WatchFolder = Drop, Release = "r", Time = DateTimeOffset.UtcNow, Items = [new PendingReviewItem("r", ReleaseUndo.HeldReason)], Held = true });
        Assert.True(IngestService.IsHeld(State.GetReview(id)));

        // A decision lets the sweep plan it; planning it again (a new review) clears the hold
        State.RequestRetry(id, null);
        Assert.False(IngestService.IsHeld(State.GetReview(id)));
        State.PutReview(new PendingReview { Id = id, WatchFolder = Drop, Release = "r", Time = DateTimeOffset.UtcNow, Items = [new PendingReviewItem("r", "Too close to call.")] });
        Assert.False(State.GetReview(id)!.Held);
        Assert.False(IngestService.IsHeld(null));
    }

    [Fact]
    public void A_return_plan_only_moves_the_files_it_lists()
    {
        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations = [new PlannedOperation(OperationKind.Video, Path.Combine(Films, "x.mkv"), Path.Combine(Drop, "r", "x.mkv"))],
            Returning = [Path.Combine(Films, "y.mkv")],
            AllowedRoots = [Drop],
        };

        var report = new PlanExecutor(new PhysicalFileOperations(), TimeProvider.System).Execute(plan, Path.Combine(Drop, "r"), LogPath, dryRun: false);

        Assert.False(report.Succeeded);
        Assert.Contains("outside the release", report.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_move_records_its_run_modified_time_and_whether_the_source_stayed()
    {
        var run = FileRelease(TransferMode.Copy, clutter: false);

        var done = ActionLogLine.CompletedMoves(File.ReadAllLines(LogPath), run);

        Assert.Equal(2, done.Count);
        Assert.All(done, l => Assert.True(l.Kept));
        Assert.Equal(File.GetLastWriteTimeUtc(Video), done[0].ModifiedUtc);
    }

    [Fact]
    public void An_undo_is_copied_to_the_activity_log()
    {
        var note = ActivityNotifier.NoteFor(new ActivityEntry { Time = DateTimeOffset.UtcNow, Status = ActivityStatus.Undone, Release = "r", Summary = "Undone by an administrator." });

        Assert.NotNull(note);
        Assert.StartsWith("Ingest undid", note!.Name, StringComparison.Ordinal);
    }

    // Real file operations, with chosen moves failing
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
