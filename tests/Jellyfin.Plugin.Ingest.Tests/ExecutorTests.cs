using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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

        public void AppendLine(string path, string line) => Log.Add(line);

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
}
