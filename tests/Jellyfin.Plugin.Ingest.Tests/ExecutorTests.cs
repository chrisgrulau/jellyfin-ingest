using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Planning;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class ExecutorTests
{
    /// <summary>An in-memory file system with folders, and hooks to make chosen steps fail.</summary>
    private sealed class Fs : IFileOperations
    {
        public Dictionary<string, long> Files { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Dirs { get; } = new(StringComparer.Ordinal) { "/", "/drop", "/drop/r", "/lib", "/lib/Films" };

        public List<string> Log { get; } = [];

        public Func<string, string, bool>? FailMove { get; set; }

        public Func<string, long>? CorruptCopy { get; set; }

        public Action? OnMove { get; set; }

        public bool Room { get; set; } = true;

        public bool Exists(string path) => Files.ContainsKey(path) || Dirs.Contains(path);

        public long Length(string path) => Files[path];

        public void CreateDirectory(string path)
        {
            for (var d = path; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            {
                Dirs.Add(d);
            }
        }

        public void Move(string source, string destination)
        {
            OnMove?.Invoke();
            if (FailMove?.Invoke(source, destination) == true)
            {
                throw new IOException("disk error");
            }

            if (Files.ContainsKey(destination))
            {
                throw new IOException("exists");
            }

            Files[destination] = CorruptCopy?.Invoke(source) ?? Files[source];
            Files.Remove(source);
        }

        public void Delete(string path) => Files.Remove(path);

        public bool HardLinks { get; set; }

        public List<string> Linked { get; } = [];

        public void Copy(string source, string destination)
        {
            if (FailMove?.Invoke(source, destination) == true)
            {
                throw new IOException("copy failed");
            }

            Files[destination] = Files[source];
        }

        public bool TryHardLink(string source, string destination)
        {
            if (!HardLinks)
            {
                return false;
            }

            Files[destination] = Files[source];
            Linked.Add(destination);
            return true;
        }

        public void DeleteEmptyDirectories(string path)
        {
            foreach (var d in Dirs.Where(d => d == path || d.StartsWith(path + "/", StringComparison.Ordinal)).OrderByDescending(d => d.Length).ToList())
            {
                if (!Files.Keys.Any(f => f.StartsWith(d + "/", StringComparison.Ordinal)) && !Dirs.Any(x => x.StartsWith(d + "/", StringComparison.Ordinal)))
                {
                    Dirs.Remove(d);
                }
            }
        }

        public Func<string, bool>? FailAppend { get; set; }

        public void AppendLine(string path, string line)
        {
            if (FailAppend?.Invoke(line) == true)
            {
                throw new IOException("No space left on device");
            }

            Log.Add(line);
        }

        public bool HasRoomFor(string source, string destinationFolder, long bytes) => Room;

        public IEnumerable<string> Phases() => Log.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("phase").GetString()!);
    }

    private static IngestPlan Plan() => new()
    {
        ReleaseName = "r",
        Operations =
        [
            new PlannedOperation(OperationKind.Video, "/drop/r/a.mkv", "/lib/Films/A (2019)/A (2019).mkv"),
            new PlannedOperation(OperationKind.Subtitle, "/drop/r/a.srt", "/lib/Films/A (2019)/A (2019).en.srt"),
            new PlannedOperation(OperationKind.Quarantine, "/drop/r/info.nfo", "/drop/q/2026-09-25/r/info.nfo"),
        ],
        AllowedRoots = ["/lib/Films/A (2019)", "/drop/q/2026-09-25"],
    };

    private static Fs Seeded()
    {
        var fs = new Fs();
        fs.Files["/drop/r/a.mkv"] = 1000;
        fs.Files["/drop/r/a.srt"] = 10;
        fs.Files["/drop/r/info.nfo"] = 5;
        return fs;
    }

    private static ExecutionReport Run(Fs fs, CancellationToken ct = default)
        => new PlanExecutor(fs, TimeProvider.System).Execute(Plan(), "/drop/r", "/log", dryRun: false, ct);

    [Fact]
    public void Each_move_goes_through_a_hidden_name_and_is_logged_before_and_after()
    {
        var fs = Seeded();
        var moves = new List<(string, string)>();
        fs.FailMove = (s, d) => { moves.Add((s, d)); return false; };

        var report = Run(fs);

        Assert.True(report.Succeeded);
        Assert.Equal(1000, fs.Files["/lib/Films/A (2019)/A (2019).mkv"]);
        Assert.Contains(moves, m => m.Item1 == "/drop/r/a.mkv" && Path.GetFileName(m.Item2).StartsWith(".ingest-", StringComparison.Ordinal) && m.Item2.EndsWith(".partial", StringComparison.Ordinal));
        Assert.Equal(["intent", "done", "intent", "done", "intent", "done"], fs.Phases());
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public void Says_which_file_it_is_filing()
    {
        var fs = Seeded();
        var seen = new List<(int, int)>();

        var report = new PlanExecutor(fs, TimeProvider.System) { Filing = (file, count) => seen.Add((file, count)) }
            .Execute(Plan(), "/drop/r", "/log", dryRun: false);

        Assert.True(report.Succeeded);
        Assert.Equal([(1, 3), (2, 3), (3, 3)], seen);
    }

    [Fact]
    public void A_failure_undoes_the_moves_already_made()
    {
        var fs = Seeded();
        fs.FailMove = (s, _) => s == "/drop/r/a.srt";

        var report = Run(fs);

        Assert.False(report.Succeeded);
        Assert.Equal("/drop/r/a.srt", report.Failed!.Source);
        Assert.Single(report.RolledBack);
        Assert.Empty(report.RollbackProblems);
        Assert.Equal(1000, fs.Files["/drop/r/a.mkv"]);
        Assert.False(fs.Exists("/lib/Films/A (2019)/A (2019).mkv"));
        Assert.DoesNotContain("/lib/Films/A (2019)", fs.Dirs);
        Assert.Contains("/lib/Films", fs.Dirs);
        Assert.Contains("rolled-back", fs.Phases());
    }

    [Fact]
    public void A_copy_that_fails_verification_is_undone()
    {
        var fs = Seeded();
        fs.CorruptCopy = s => s == "/drop/r/a.mkv" ? 400 : fs.Files[s];

        var report = Run(fs);

        Assert.False(report.Succeeded);
        Assert.Contains("could not be verified", report.Error, StringComparison.Ordinal);
        Assert.True(fs.Exists("/drop/r/a.mkv"));
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public void Not_enough_room_fails_before_moving_anything()
    {
        var fs = Seeded();
        fs.Room = false;

        var report = Run(fs);

        Assert.Contains("Not enough free space", report.Error, StringComparison.Ordinal);
        Assert.Equal(3, fs.Files.Keys.Count(k => k.StartsWith("/drop/r/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Cancelling_stops_between_files_without_undoing()
    {
        var fs = Seeded();
        using var cts = new CancellationTokenSource();
        var moves = 0;
        fs.OnMove = () => { if (++moves == 2) { cts.Cancel(); } }; // after the first file's two moves

        var report = Run(fs, cts.Token);

        Assert.True(report.Cancelled);
        Assert.Single(report.Completed);
        Assert.True(fs.Exists("/lib/Films/A (2019)/A (2019).mkv"));
        Assert.True(fs.Exists("/drop/r/a.srt"));
    }

    private static string Line(string phase, string source, string destination, string temp, long bytes)
        => JsonSerializer.Serialize(new { time = "t", release = "r", kind = "Video", source, destination, bytes, phase, temp });

    [Fact]
    public void An_interrupted_move_whose_file_had_arrived_is_finished()
    {
        var fs = new Fs();
        fs.Files["/lib/Films/A/.ingest-11111111111111111111111111111111.partial"] = 1000;

        var results = new PlanExecutor(fs, TimeProvider.System).Recover([Line("intent", "/drop/r/a.mkv", "/lib/Films/A/A.mkv", "/lib/Films/A/.ingest-11111111111111111111111111111111.partial", 1000)], "/log", Roots);

        Assert.Single(results);
        Assert.Equal(1000, fs.Files["/lib/Films/A/A.mkv"]);
        Assert.False(fs.Exists("/lib/Films/A/.ingest-11111111111111111111111111111111.partial"));
        Assert.Equal(["recovered"], fs.Phases());
    }

    [Fact]
    public void An_interrupted_copy_is_discarded_when_the_original_is_intact()
    {
        var fs = new Fs();
        fs.Files["/drop/r/a.mkv"] = 1000;
        fs.Files["/lib/Films/A/.ingest-22222222222222222222222222222222.partial"] = 300;

        new PlanExecutor(fs, TimeProvider.System).Recover([Line("intent", "/drop/r/a.mkv", "/lib/Films/A/A.mkv", "/lib/Films/A/.ingest-22222222222222222222222222222222.partial", 1000)], "/log", Roots);

        Assert.False(fs.Exists("/lib/Films/A/.ingest-22222222222222222222222222222222.partial"));
        Assert.Equal(1000, fs.Files["/drop/r/a.mkv"]);
        Assert.Equal(["discarded"], fs.Phases());
    }

    [Fact]
    public void Finished_and_old_style_log_entries_need_no_recovery()
    {
        var fs = new Fs();
        string[] log =
        [
            Line("intent", "/drop/r/a.mkv", "/lib/A.mkv", "/lib/.ingest-33333333333333333333333333333333.partial", 1),
            Line("done", "/drop/r/a.mkv", "/lib/A.mkv", "/lib/.ingest-33333333333333333333333333333333.partial", 1),
            "{\"kind\":\"Video\",\"source\":\"/x\",\"destination\":\"/y\",\"bytes\":1}",
            Line("intent", "/drop/r/b.mkv", "/lib/B.mkv", "/lib/.ingest-44444444444444444444444444444444.partial", 1),
        ];

        Assert.Empty(new PlanExecutor(fs, TimeProvider.System).Recover(log, "/log", Roots));
        Assert.Empty(fs.Log);
    }

    // ING-20: a failed "done" line after a successful rename still rolls that file back with the rest
    [Fact]
    public void A_log_failure_after_a_move_rolls_that_file_back_too()
    {
        var fs = Seeded();
        var dones = 0;
        fs.FailAppend = line => line.Contains("\"phase\":\"done\"", StringComparison.Ordinal) && ++dones == 2;

        var report = Run(fs);

        Assert.False(report.Succeeded);
        Assert.Equal(1000, fs.Files["/drop/r/a.mkv"]);
        Assert.Equal(10, fs.Files["/drop/r/a.srt"]);
        Assert.False(fs.Exists("/lib/Films/A (2019)/A (2019).mkv"));
        Assert.False(fs.Exists("/lib/Films/A (2019)/A (2019).en.srt"));
        Assert.Equal(2, report.RolledBack.Count);
        Assert.Empty(report.RollbackProblems);
    }

    private static readonly string[] Roots = ["/lib"];

    [Theory]
    [InlineData("/lib/Films/A/not-ours.partial", "/lib/Films/A/A.mkv")]
    [InlineData("/lib/Films/B/.ingest-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.partial", "/lib/Films/A/A.mkv")]
    [InlineData("/etc/.ingest-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.partial", "/etc/passwd")]
    [InlineData("relative/.ingest-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.partial", "relative/A.mkv")]
    public void Recovery_never_acts_on_a_file_that_is_not_ingests_own_temporary_file(string temp, string destination)
    {
        var fs = new Fs();
        fs.Files[temp] = 1000;

        var results = new PlanExecutor(fs, TimeProvider.System).Recover([Line("intent", "/drop/r/a.mkv", destination, temp, 1000)], "/log", Roots);

        Assert.StartsWith("Needs attention", Assert.Single(results), StringComparison.Ordinal);
        Assert.True(fs.Exists(temp));
        Assert.False(fs.Exists(destination));
        Assert.Empty(fs.Log);
    }

    // ING-22: a whole release quarantined from the review screen uses the same executor: logged, all-or-nothing
    [Fact]
    public void A_whole_release_quarantine_is_logged_and_rolled_back_on_failure()
    {
        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations =
            [
                new PlannedOperation(OperationKind.Quarantine, "/drop/r/a.mkv", "/drop/q/2026-09-26/r/a.mkv"),
                new PlannedOperation(OperationKind.Quarantine, "/drop/r/info.nfo", "/drop/q/2026-09-26/r/info.nfo"),
            ],
            AllowedRoots = ["/drop/q/2026-09-26"],
            WholeReleaseQuarantine = true,
        };
        Assert.False((plan with { WholeReleaseQuarantine = false }).IsReady);

        var ok = Seeded();
        Assert.True(new PlanExecutor(ok, TimeProvider.System).Execute(plan, "/drop/r", "/log", dryRun: false).Succeeded);
        Assert.Equal(1000, ok.Files["/drop/q/2026-09-26/r/a.mkv"]);
        Assert.Equal(["intent", "done", "intent", "done"], ok.Phases());

        var failing = Seeded();
        failing.FailMove = (s, _) => s == "/drop/r/info.nfo";
        var report = new PlanExecutor(failing, TimeProvider.System).Execute(plan, "/drop/r", "/log", dryRun: false);
        Assert.False(report.Succeeded);
        Assert.Equal(1000, failing.Files["/drop/r/a.mkv"]);
        Assert.False(failing.Exists("/drop/q/2026-09-26/r/a.mkv"));
    }

    // ING-30: only the exact files a plan replaces may be moved from outside the release, and only into quarantine
    [Fact]
    public void A_replaced_copy_moves_to_quarantine_before_the_new_file_and_comes_back_on_failure()
    {
        var fs = Seeded();
        fs.Files["/lib/Films/A (2019)/A (2019).avi"] = 700;
        fs.Dirs.Add("/lib/Films/A (2019)");
        fs.Dirs.Add("/drop/q");
        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations =
            [
                new(OperationKind.Quarantine, "/lib/Films/A (2019)/A (2019).avi", "/drop/q/Replaced/A (2019).avi"),
                new(OperationKind.Video, "/drop/r/a.mkv", "/lib/Films/A (2019)/A (2019).mkv"),
            ],
            AllowedRoots = ["/lib/Films/A (2019)", "/drop/q"],
            Replacing = ["/lib/Films/A (2019)/A (2019).avi"],
        };

        Assert.True(new PlanExecutor(fs, TimeProvider.System).Execute(plan, "/drop/r", "/log", dryRun: false, CancellationToken.None).Succeeded);
        Assert.Equal(700, fs.Files["/drop/q/Replaced/A (2019).avi"]);
        Assert.True(fs.Exists("/lib/Films/A (2019)/A (2019).mkv"));

        var again = Seeded();
        again.Files["/lib/Films/A (2019)/A (2019).avi"] = 700;
        again.Dirs.Add("/lib/Films/A (2019)");
        again.Dirs.Add("/drop/q");
        again.FailMove = (s, _) => s == "/drop/r/a.mkv";
        Assert.False(new PlanExecutor(again, TimeProvider.System).Execute(plan, "/drop/r", "/log", dryRun: false, CancellationToken.None).Succeeded);
        Assert.Equal(700, again.Files["/lib/Films/A (2019)/A (2019).avi"]);
    }

    [Fact]
    public void Nothing_else_outside_the_release_may_be_moved()
    {
        var fs = Seeded();
        fs.Files["/lib/Films/B.avi"] = 700;
        var plan = new IngestPlan
        {
            ReleaseName = "r",
            Operations = [new(OperationKind.Quarantine, "/lib/Films/B.avi", "/drop/q/B.avi"), new(OperationKind.Video, "/drop/r/a.mkv", "/lib/Films/A.mkv")],
            AllowedRoots = ["/lib/Films", "/drop/q"],
            Replacing = ["/lib/Films/Other.avi"],
        };

        var report = new PlanExecutor(fs, TimeProvider.System).Execute(plan, "/drop/r", "/log", dryRun: false, CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("outside the release", report.Error, StringComparison.Ordinal);
        Assert.Equal(700, fs.Files["/lib/Films/B.avi"]);
    }

    // ING-28: copy and hard link leave the release where it is
    private static IngestPlan Kept(TransferMode transfer) => Plan() with
    {
        Operations = [.. Plan().Operations.Where(o => o.Kind != OperationKind.Quarantine)],
        Transfer = transfer,
    };

    [Theory]
    [InlineData(TransferMode.Copy, false)]
    [InlineData(TransferMode.HardLink, true)]
    [InlineData(TransferMode.HardLink, false)]
    public void Copy_and_hard_link_file_the_release_and_leave_it_in_place(TransferMode transfer, bool linksWork)
    {
        var fs = Seeded();
        fs.HardLinks = linksWork;

        var report = new PlanExecutor(fs, TimeProvider.System).Execute(Kept(transfer), "/drop/r", "/log", dryRun: false, CancellationToken.None);

        Assert.True(report.Succeeded, report.Error);
        Assert.Equal(1000, fs.Files["/drop/r/a.mkv"]);
        Assert.Equal(1000, fs.Files["/lib/Films/A (2019)/A (2019).mkv"]);
        Assert.Equal(linksWork ? 2 : 0, fs.Linked.Count);
    }

    [Fact]
    public void A_failed_copy_removes_the_copies_already_made_and_keeps_the_release()
    {
        var fs = Seeded();
        fs.FailMove = (s, _) => s == "/drop/r/a.srt";

        var report = new PlanExecutor(fs, TimeProvider.System).Execute(Kept(TransferMode.Copy), "/drop/r", "/log", dryRun: false, CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Empty(report.RollbackProblems);
        Assert.False(fs.Exists("/lib/Films/A (2019)/A (2019).mkv"));
        Assert.Equal(1000, fs.Files["/drop/r/a.mkv"]);
        Assert.Equal(10, fs.Files["/drop/r/a.srt"]);
    }

    [Fact]
    public void A_real_hard_link_is_a_second_name_for_the_same_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ingest-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.mkv");
            File.WriteAllText(a, "video");
            var ops = new PhysicalFileOperations();

            Assert.True(ops.TryHardLink(a, Path.Combine(dir, "b.mkv")));
            File.AppendAllText(a, " more");
            Assert.Equal("video more", File.ReadAllText(Path.Combine(dir, "b.mkv")));
            Assert.False(ops.TryHardLink(a, Path.Combine(dir, "b.mkv")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
