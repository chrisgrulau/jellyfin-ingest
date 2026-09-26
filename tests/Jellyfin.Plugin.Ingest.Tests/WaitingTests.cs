using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

public class WaitingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(5);

    private static Dictionary<string, IReadOnlyList<ReleaseFile>> Snap(params (string Release, string Rel, long Size)[] files)
        => files.GroupBy(f => f.Release).ToDictionary(g => g.Key, g => (IReadOnlyList<ReleaseFile>)[.. g.Select(f => new ReleaseFile(f.Rel, f.Size))]);

    [Fact]
    public void Pending_lists_settling_and_downloading_releases_but_not_handled_ones()
    {
        var t = new ReleaseTracker();
        var snap = Snap(("a", "a/a.mkv", 1), ("b", "b/b.mkv.part", 1), ("c", "c/c.mkv", 1));
        t.Observe(snap, T0, Settle);
        t.Observe(snap, T0.AddMinutes(6), Settle);
        t.MarkHandled("c");

        var pending = t.Pending(Settle);

        Assert.Equal([("a", (DateTimeOffset?)T0 + Settle), ("b", null)], pending);
    }

    [Fact]
    public void Settle_now_makes_an_unchanged_release_ready_on_the_next_look()
    {
        var t = new ReleaseTracker();
        var snap = Snap(("a", "a/a.mkv", 1));
        t.Observe(snap, T0, Settle);

        Assert.True(t.SettleNow("a"));

        Assert.Equal(["a"], t.Observe(snap, T0.AddSeconds(30), Settle));
    }

    [Fact]
    public void Settle_now_doesnt_skip_changes_or_downloads()
    {
        var t = new ReleaseTracker();
        t.Observe(Snap(("a", "a/a.mkv", 1), ("b", "b/b.mkv.part", 1)), T0, Settle);

        Assert.True(t.SettleNow("a"));
        Assert.False(t.SettleNow("b")); // still downloading

        // It grew since: the wait starts again
        Assert.Empty(t.Observe(Snap(("a", "a/a.mkv", 2), ("b", "b/b.mkv.part", 1)), T0.AddSeconds(30), Settle));
    }

    private static WaitingRelease W(string id, DateTimeOffset? at, string folder = "/in")
        => new() { Id = id, WatchFolder = folder, Release = id, SettlesAt = at };

    [Fact]
    public void Process_now_is_only_accepted_for_a_waiting_release_that_can_settle()
    {
        var p = new IngestProgress();
        p.SetWaiting("/in", [W("a", T0), W("b", null)]);

        Assert.True(p.RequestProcessNow("a"));
        Assert.False(p.RequestProcessNow("b"));
        Assert.False(p.RequestProcessNow("gone"));

        Assert.True(p.Waiting().Single(w => w.Id == "a").ProcessNow);
        Assert.True(p.TakeProcessNow("a"));
        Assert.False(p.TakeProcessNow("a"));
    }

    [Fact]
    public void Waiting_is_soonest_first_and_forgets_folders_no_longer_swept()
    {
        var p = new IngestProgress();
        p.SetWaiting("/in", [W("late", T0.AddMinutes(4)), W("dl", null), W("soon", T0.AddMinutes(1))]);
        p.SetWaiting("/old", [W("x", T0, "/old")]);
        p.RequestProcessNow("x");

        p.KeepOnly(["/in"]);

        Assert.Equal(["soon", "late", "dl"], p.Waiting().Select(w => w.Id));
        Assert.False(p.TakeProcessNow("x"));
    }

    [Fact]
    public async Task Process_now_wakes_the_sweep_early()
    {
        var p = new IngestProgress();
        p.SetWaiting("/in", [W("a", T0)]);
        var wait = p.WaitForNextSweepAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.False(wait.IsCompleted);

        p.RequestProcessNow("a");

        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public void Reports_the_release_being_worked_on()
    {
        var p = new IngestProgress();
        p.SetWorking(new WorkInProgress { Id = "a", WatchFolder = "/in", Release = "a", Stage = "Filing", File = 2, Files = 5 });

        Assert.Equal(2, p.Working!.File);
        p.SetWorking(null);
        Assert.Null(p.Working);
    }
}
