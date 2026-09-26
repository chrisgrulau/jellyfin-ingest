using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Quarantine;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// FEAT-05: restore from quarantine, "delete now" and the pause switch, on a real (temporary) file system.
public sealed class QuarantineRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ingest-restore-" + Guid.NewGuid().ToString("N"));

    public QuarantineRestoreTests()
    {
        Directory.CreateDirectory(Drop);
        Directory.CreateDirectory(Films);
        QuarantineMarkers.Mark(Quarantine, Dated);
        State = new IngestStateStore(Path.Combine(_root, "state.json"));
    }

    private string Drop => Path.Combine(_root, "drop");

    private string Films => Path.Combine(_root, "lib", "Films");

    private string Quarantine => Path.Combine(Drop, ".ingest-quarantine");

    private string Dated => Path.Combine(Quarantine, "2026-09-25");

    private string LogPath => Path.Combine(_root, "actions.jsonl");

    private IngestStateStore State { get; }

    private ReturnScope Scope => new([Drop], [Films], [Quarantine]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Put(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    // Quarantines files through the executor, as a filing or a whole-release quarantine would
    private void QuarantineFiles(params (string Source, string Destination)[] moves)
    {
        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations = [.. moves.Select(m => new PlannedOperation(OperationKind.Quarantine, m.Source, m.Destination))],
            AllowedRoots = [Dated],
            WholeReleaseQuarantine = true,
            Replacing = [.. moves.Select(m => m.Source).Where(s => !PathGuard.IsUnder(s, Drop))],
        };
        var report = new PlanExecutor(new PhysicalFileOperations(), TimeProvider.System).Execute(plan, Path.Combine(Drop, "r"), LogPath, dryRun: false);
        Assert.True(report.Succeeded, report.Error);
    }

    private ReturnOutcome Restore(string name, ReturnScope? scope = null)
        => new QuarantineRestore(State, new PhysicalFileOperations(), TimeProvider.System).Restore(scope ?? Scope, Quarantine, "2026-09-25", name, File.ReadAllLines(LogPath), LogPath);

    [Fact]
    public void A_release_goes_back_to_the_watch_folder_and_waits_for_review()
    {
        QuarantineFiles((Put(Path.Combine(Drop, "r", "a.mkv"), 100), Path.Combine(Dated, "r", "a.mkv")), (Put(Path.Combine(Drop, "r", "Subs", "en.srt"), 5), Path.Combine(Dated, "r", "Subs", "en.srt")));

        var outcome = Restore("r");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(100, new FileInfo(Path.Combine(Drop, "r", "a.mkv")).Length);
        Assert.True(File.Exists(Path.Combine(Drop, "r", "Subs", "en.srt")));
        Assert.False(Directory.Exists(Path.Combine(Dated, "r")));
        Assert.True(QuarantineMarkers.IsMarked(Dated));

        var review = State.GetReview(IngestStateStore.ReviewId(Drop, "r"));
        Assert.True(IngestService.IsHeld(review));
        Assert.Equal(QuarantineRestore.HeldReason, Assert.Single(review!.Items).Reason);
        Assert.Contains(State.Snapshot().Activity, a => a.Status == ActivityStatus.Restored);
    }

    [Fact]
    public void A_replaced_copy_goes_back_into_the_library_without_a_review()
    {
        var copy = Put(Path.Combine(Films, "Rocket Club (2019)", "Rocket Club (2019).mkv"), 50);
        QuarantineFiles((copy, Path.Combine(Dated, "Replaced", "Rocket Club (2019).mkv")));

        var outcome = Restore("Replaced");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(50, new FileInfo(copy).Length);
        Assert.Empty(State.Snapshot().Reviews);
        Assert.Contains(outcome.Refresh, f => PathGuard.SamePath(f, Path.Combine(Films, "Rocket Club (2019)")));
    }

    [Fact]
    public void An_occupied_place_refuses_the_whole_restore()
    {
        QuarantineFiles((Put(Path.Combine(Drop, "r", "a.mkv"), 100), Path.Combine(Dated, "r", "a.mkv")), (Put(Path.Combine(Drop, "r", "b.nfo"), 5), Path.Combine(Dated, "r", "b.nfo")));
        Put(Path.Combine(Drop, "r", "b.nfo"), 7);

        var outcome = Restore("r");

        Assert.False(outcome.Succeeded);
        Assert.Contains("is taken now", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Dated, "r", "a.mkv")));
        Assert.False(File.Exists(Path.Combine(Drop, "r", "a.mkv")));
        Assert.Empty(State.Snapshot().Reviews);
    }

    [Fact]
    public void Files_from_outside_the_watch_folders_and_libraries_arent_put_back()
    {
        QuarantineFiles((Put(Path.Combine(Drop, "r", "a.mkv"), 100), Path.Combine(Dated, "r", "a.mkv")));

        var outcome = Restore("r", new ReturnScope([], [Films], [Quarantine]));

        Assert.False(outcome.Succeeded);
        Assert.Contains("outside the watch folders and libraries", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Dated, "r", "a.mkv")));
    }

    [Fact]
    public void A_file_the_action_log_doesnt_know_isnt_restored()
    {
        QuarantineFiles((Put(Path.Combine(Drop, "r", "a.mkv"), 100), Path.Combine(Dated, "r", "a.mkv")));
        Put(Path.Combine(Dated, "r", "stranger.txt"), 3);

        var outcome = Restore("r");

        Assert.False(outcome.Succeeded);
        Assert.Contains("isn't in the action log", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Dated, "r", "a.mkv")));
    }

    [Fact]
    public void Only_entries_of_a_marked_dated_folder_in_a_quarantine_in_use_can_be_located()
    {
        Put(Path.Combine(Dated, "r", "a.mkv"), 1);
        Directory.CreateDirectory(Path.Combine(Quarantine, "2026-01-01"));

        Assert.Null(QuarantineRestore.Locate(Scope, Quarantine, "2026-09-25", "r", out var path));
        Assert.Equal(Path.Combine(Dated, "r"), path);
        Assert.NotNull(QuarantineRestore.Locate(Scope, Drop, "2026-09-25", "r", out _));
        Assert.NotNull(QuarantineRestore.Locate(Scope, Quarantine, "2026-01-01", "r", out _));
        Assert.NotNull(QuarantineRestore.Locate(Scope, Quarantine, "..", "r", out _));
        Assert.NotNull(QuarantineRestore.Locate(Scope, Quarantine, "2026-09-25", "../../drop", out _));
        Assert.NotNull(QuarantineRestore.Locate(Scope, Quarantine, "2026-09-25", QuarantineMarkers.DatedMarker, out _));
        Assert.NotNull(QuarantineRestore.Locate(Scope, Quarantine, "2026-09-25", "gone", out _));
    }

    [Fact]
    public void Delete_now_removes_one_release_or_the_whole_day()
    {
        Put(Path.Combine(Dated, "r", "a.mkv"), 1);
        var other = Put(Path.Combine(Dated, "s", "b.nfo"), 1);
        File.SetAttributes(other, FileAttributes.ReadOnly);

        QuarantinePurger.DeleteNow(Dated, "r");

        Assert.False(Directory.Exists(Path.Combine(Dated, "r")));
        Assert.True(File.Exists(other));
        Assert.True(QuarantineMarkers.IsMarked(Dated));

        QuarantinePurger.DeleteNow(Dated, null);

        Assert.False(Directory.Exists(Dated));
        Assert.True(Directory.Exists(Quarantine));
    }

    [Fact]
    public void Delete_now_refuses_what_isnt_ingests()
    {
        var theirs = Path.Combine(Quarantine, "2026-01-01");
        Put(Path.Combine(theirs, "keep.txt"), 1);

        Assert.Throws<ArgumentException>(() => QuarantinePurger.DeleteNow(theirs, null));
        Assert.Throws<ArgumentException>(() => QuarantinePurger.DeleteNow(Dated, ".."));
        Assert.Throws<ArgumentException>(() => QuarantinePurger.DeleteNow(Dated, QuarantineMarkers.DatedMarker));
        Assert.Throws<ArgumentException>(() => QuarantinePurger.DeleteNow(Dated, "missing"));
        Assert.Throws<ArgumentException>(() => QuarantinePurger.DeleteNow(Drop, null));
        Assert.True(File.Exists(Path.Combine(theirs, "keep.txt")));
    }

    [Fact]
    public void Delete_now_never_follows_a_link()
    {
        var outside = Put(Path.Combine(_root, "elsewhere", "precious.mkv"), 1);
        Directory.CreateDirectory(Path.Combine(Dated, "r"));
        Directory.CreateSymbolicLink(Path.Combine(Dated, "r", "link"), Path.GetDirectoryName(outside)!);
        Directory.CreateSymbolicLink(Path.Combine(Dated, "linked"), Path.GetDirectoryName(outside)!);

        QuarantinePurger.DeleteNow(Dated, "r");
        QuarantinePurger.DeleteNow(Dated, "linked");

        Assert.True(File.Exists(outside));
        Assert.False(Directory.Exists(Path.Combine(Dated, "r")));
        Assert.False(Directory.Exists(Path.Combine(Dated, "linked")));
    }

    [Fact]
    public void Pausing_is_kept_across_a_restart()
    {
        Assert.False(State.IsPaused);
        Assert.True(State.SetPaused(true));
        Assert.False(State.SetPaused(true));

        var reopened = new IngestStateStore(Path.Combine(_root, "state.json"));
        Assert.True(reopened.IsPaused);
        Assert.True(reopened.Snapshot().Paused);

        Assert.True(reopened.SetPaused(false));
        Assert.False(new IngestStateStore(Path.Combine(_root, "state.json")).IsPaused);
    }

    [Fact]
    public void A_restore_is_copied_to_the_activity_log()
        => Assert.NotNull(ActivityNotifier.NoteFor(new ActivityEntry { Time = DateTimeOffset.UtcNow, Status = ActivityStatus.Restored, Release = "r" }));

    [Fact]
    public void Queued_actions_are_kept_once_until_completed()
    {
        var progress = new IngestProgress();
        var restore = new QueuedAction { Kind = QueuedActionKind.Restore, Root = Quarantine, Folder = "2026-09-25", Name = "r" };

        Assert.True(progress.Queue(restore));
        Assert.False(progress.Queue(restore with { }));
        Assert.True(progress.Queue(new QueuedAction { Kind = QueuedActionKind.Undo, Run = "abc" }));
        Assert.Equal(2, progress.Queued().Count);

        progress.Complete(restore);
        Assert.Equal(QueuedActionKind.Undo, Assert.Single(progress.Queued()).Kind);
    }
}
