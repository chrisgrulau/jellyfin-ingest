using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Quarantine;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// "Delete now" and the purge hold the file-operations gate, delete what arrives meanwhile, wait out temporary names that
// network and FUSE mounts leave for files still open, and never leave a dated folder without its marker.
public sealed class QuarantineDeleteRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ingest-qrace-" + Guid.NewGuid().ToString("N"));

    public QuarantineDeleteRaceTests()
    {
        QuarantineMarkers.Mark(_root, Dated);
        Put(Path.Combine(Dated, "Release", "info.nfo"));
        Put(Path.Combine(Dated, "Replaced", "Show", "Season 01", "S01E01.mkv"));
    }

    private string Dated => Path.Combine(_root, "2026-09-27");

    public void Dispose()
    {
        foreach (var dir in new DirectoryInfo(_root).EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows() && dir.LinkTarget is null)
            {
                File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Something_arriving_during_the_delete_is_deleted_in_a_second_pass()
    {
        var options = new QuarantineDeleteOptions
        {
            AfterPass = pass =>
            {
                if (pass == 1)
                {
                    Put(Path.Combine(Dated, "Replaced", "Show", "Season 01", "S01E02.mkv"));
                }
            },
        };

        await QuarantinePurger.DeleteNowAsync(Dated, null, options);

        Assert.False(Directory.Exists(Dated));
    }

    [Fact]
    public async Task A_leftover_keeps_the_marker_and_says_how_many()
    {
        var late = Path.Combine(Dated, "late.nfo");
        var options = new QuarantineDeleteOptions { AfterPass = pass => { Arrive(pass); if (pass == 2) { Put(late); } } };

        var ex = await Assert.ThrowsAsync<IOException>(() => QuarantinePurger.DeleteNowAsync(Dated, null, options));

        Assert.Equal("1 item couldn't be deleted yet (still in use?); it'll be removed by the next purge, or try again.", ex.Message);
        Assert.True(QuarantineMarkers.IsMarked(Dated));
        Assert.True(File.Exists(late));
        Assert.False(Directory.Exists(Path.Combine(Dated, "Release")));
    }

    [Fact]
    public async Task Something_arriving_just_before_the_folder_is_removed_puts_the_marker_back()
    {
        var options = new QuarantineDeleteOptions { BeforeFolderDelete = () => Put(Path.Combine(Dated, "Replaced", "late.mkv")) };

        await Assert.ThrowsAsync<IOException>(() => QuarantinePurger.DeleteNowAsync(Dated, null, options));

        Assert.True(QuarantineMarkers.IsMarked(Dated));
    }

    [Theory]
    [InlineData(".fuse_hidden000001a200000003")]
    [InlineData(".nfs0000000000c2f3b700000001")]
    [InlineData(".smb0001")]
    public async Task A_temporary_name_that_goes_away_is_waited_for(string name)
    {
        var waits = 0;
        var options = new QuarantineDeleteOptions
        {
            AfterPass = pass => { Arrive(pass); if (pass == 2) { Put(Path.Combine(Dated, "Replaced", "Show", name)); } },
            Delay = (_, _) => { waits++; return Task.CompletedTask; },
        };

        await QuarantinePurger.DeleteNowAsync(Dated, null, options);

        Assert.False(Directory.Exists(Dated));
        Assert.Equal(1, waits);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_temporary_name_that_stays_is_waited_for_a_bounded_time_then_reported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "Needs a non-root Unix account.");
        var stuck = Path.Combine(Dated, "Replaced", "Show");
        Put(Path.Combine(stuck, ".nfs000000001"));
        File.SetUnixFileMode(stuck, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var waits = 0;
        var options = new QuarantineDeleteOptions { Delay = (_, _) => { waits++; return Task.CompletedTask; } };

        var ex = await Assert.ThrowsAsync<IOException>(() => QuarantinePurger.DeleteNowAsync(Dated, null, options));

        Assert.Equal(QuarantineDeleteOptions.Default.TransientRetries, waits);
        Assert.StartsWith("1 item couldn't be deleted yet", ex.Message, StringComparison.Ordinal);
        Assert.True(QuarantineMarkers.IsMarked(Dated));
    }

    [Fact]
    public async Task Waiting_between_retries_can_be_cancelled_and_the_folder_stays_marked()
    {
        using var cts = new CancellationTokenSource();
        var options = new QuarantineDeleteOptions
        {
            AfterPass = pass => { Arrive(pass); if (pass == 2) { Put(Path.Combine(Dated, ".fuse_hidden0001")); } },
            Delay = (_, ct) => { cts.Cancel(); return Task.FromCanceled(ct.IsCancellationRequested ? ct : cts.Token); },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QuarantinePurger.DeleteNowAsync(Dated, null, options, cts.Token));

        Assert.True(QuarantineMarkers.IsMarked(Dated));
    }

    [Fact]
    public async Task One_release_is_deleted_with_the_same_care()
    {
        var options = new QuarantineDeleteOptions { AfterPass = pass => { if (pass == 1) { Put(Path.Combine(Dated, "Release", "again.nfo")); } } };

        await QuarantinePurger.DeleteNowAsync(Dated, "Release", options);

        Assert.False(Directory.Exists(Path.Combine(Dated, "Release")));
        Assert.True(Directory.Exists(Path.Combine(Dated, "Replaced")));
        Assert.True(QuarantineMarkers.IsMarked(Dated));
    }

    [Fact]
    public async Task The_purge_never_leaves_an_unmarked_folder()
    {
        var options = new QuarantineDeleteOptions { BeforeFolderDelete = () => Put(Path.Combine(Dated, "late.nfo")) };

        var first = await QuarantinePurger.PurgeAsync(_root, new DateOnly(2026, 12, 1), 30, options);

        Assert.Empty(first.Deleted);
        Assert.Single(first.Failed);
        Assert.True(QuarantineMarkers.IsMarked(Dated));

        // So the next purge deletes it
        var second = await QuarantinePurger.PurgeAsync(_root, new DateOnly(2026, 12, 1), 30);
        Assert.Equal([Dated], second.Deleted);
        Assert.False(Directory.Exists(Dated));
    }

    [Fact]
    public async Task While_filing_holds_the_gate_nothing_is_deleted_and_Ingest_says_it_is_busy()
    {
        var gate = new FileOperationsGate();
        using (gate.Enter())
        {
            var result = await QuarantinePurger.DeleteNowAsync(gate, TimeSpan.FromMilliseconds(20), Dated, null);

            Assert.Equal(QuarantineDeleteStatus.Busy, result.Status);
            Assert.Equal("Ingest is filing right now; try again in a minute.", result.Message);
            Assert.Null(await QuarantinePurger.PurgeAsync(gate, TimeSpan.FromMilliseconds(20), [_root], new DateOnly(2026, 12, 1), 30));
            Assert.True(File.Exists(Path.Combine(Dated, "Release", "info.nfo")));
        }

        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task The_gate_is_held_for_the_whole_delete_and_released_after()
    {
        var gate = new FileOperationsGate();
        var heldDuring = false;
        var options = new QuarantineDeleteOptions { AfterPass = _ => heldDuring |= gate.IsHeld };

        var result = await QuarantinePurger.DeleteNowAsync(gate, TimeSpan.FromSeconds(5), Dated, null, options);

        Assert.Equal(QuarantineDeleteStatus.Deleted, result.Status);
        Assert.True(heldDuring);
        Assert.False(gate.IsHeld);
        Assert.False(Directory.Exists(Dated));
    }

    [Fact]
    public async Task A_delete_waits_for_a_filing_that_finishes_in_time()
    {
        var gate = new FileOperationsGate();
        var filing = gate.Enter();
        var delete = QuarantinePurger.DeleteNowAsync(gate, TimeSpan.FromSeconds(30), Dated, null);
        Assert.False(delete.IsCompleted);

        filing.Dispose();
        var result = await delete;

        Assert.Equal(QuarantineDeleteStatus.Deleted, result.Status);
        Assert.False(Directory.Exists(Dated));
    }

    [Fact]
    public async Task A_partial_delete_and_a_refusal_are_reported_not_thrown()
    {
        var gate = new FileOperationsGate();
        var options = new QuarantineDeleteOptions { AfterPass = pass => { Arrive(pass); if (pass == 2) { Put(Path.Combine(Dated, "late.nfo")); } } };

        var partial = await QuarantinePurger.DeleteNowAsync(gate, TimeSpan.FromSeconds(5), Dated, null, options);
        var refused = await QuarantinePurger.DeleteNowAsync(gate, TimeSpan.FromSeconds(5), Dated, "missing");

        Assert.Equal(QuarantineDeleteStatus.Failed, partial.Status);
        Assert.Contains("couldn't be deleted yet", partial.Message, StringComparison.Ordinal);
        Assert.Equal(QuarantineDeleteStatus.Refused, refused.Status);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task Todays_folder_deleted_during_the_day_is_made_and_marked_again()
    {
        var today = new DateOnly(2026, 9, 27);
        await QuarantinePurger.DeleteNowAsync(Dated, null);

        var dated = QuarantineMarkers.DatedFolderFor(_root, today);
        QuarantineMarkers.Mark(_root, dated);

        Assert.Equal(Dated, dated);
        Assert.True(QuarantineMarkers.IsMarked(Dated));
    }

    [Theory]
    [InlineData(".smb1234", true)]
    [InlineData(".SMB1234", true)]
    [InlineData(".fuse_hidden0000000a00000001", true)]
    [InlineData(".nfs00000001", true)]
    [InlineData("info.nfo", false)]
    [InlineData("smb.mkv", false)]
    public void Temporary_names_are_recognised(string name, bool transient)
        => Assert.Equal(transient, QuarantinePurger.IsTransientName(name));

    // Something arrives after the first pass (so there is a second pass)
    private void Arrive(int pass)
    {
        if (pass == 1)
        {
            Put(Path.Combine(Dated, "Replaced", "arrival.mkv"));
        }
    }

    private static void Put(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }
}
