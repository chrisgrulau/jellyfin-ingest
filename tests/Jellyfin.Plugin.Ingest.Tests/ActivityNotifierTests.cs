using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// FAM-05: what needs attention reaches Jellyfin's Activity log, once a day per release and outcome
public sealed class ActivityNotifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ingest-notify-" + Guid.NewGuid().ToString("N"));

    public ActivityNotifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ActivityEntry Entry(ActivityStatus status, string summary = "Two files need a decision.")
        => new() { Time = DateTimeOffset.UtcNow, Status = status, Release = "Harbour.Watch.S01", WatchFolder = "/drop", Summary = summary, Details = ["a.mkv: already on the server"] };

    [Theory]
    [InlineData(ActivityStatus.NeedsReview, LogLevel.Warning, "Ingest needs a decision: Harbour.Watch.S01")]
    [InlineData(ActivityStatus.Failed, LogLevel.Error, "Ingest couldn't file: Harbour.Watch.S01")]
    [InlineData(ActivityStatus.Filed, LogLevel.Information, "Ingest filed Harbour.Watch.S01")]
    public void Entries_that_matter_become_activity_log_notes(ActivityStatus status, LogLevel severity, string name)
    {
        var note = ActivityNotifier.NoteFor(Entry(status))!;
        Assert.Equal(severity, note.Severity);
        Assert.Equal(name, note.Name);
        Assert.Equal("a.mkv: already on the server", note.Overview);
    }

    [Theory]
    [InlineData(ActivityStatus.DryRun)]
    [InlineData(ActivityStatus.Decision)]
    [InlineData(ActivityStatus.Purged)]
    public void Routine_entries_stay_on_the_plugin_page(ActivityStatus status)
        => Assert.Null(ActivityNotifier.NoteFor(Entry(status)));

    [Fact]
    public async Task The_same_release_and_outcome_is_written_once_a_day()
    {
        var clock = new Clock();
        var written = new List<ActivityNote>();
        var notifier = new ActivityNotifier(n => { written.Add(n); return Task.CompletedTask; }, clock);

        Assert.True(await notifier.NotifyAsync(Entry(ActivityStatus.Failed)));
        Assert.False(await notifier.NotifyAsync(Entry(ActivityStatus.Failed)));
        Assert.True(await notifier.NotifyAsync(Entry(ActivityStatus.Failed, "Another reason.")));
        clock.Now += TimeSpan.FromDays(1);
        Assert.True(await notifier.NotifyAsync(Entry(ActivityStatus.Failed)));

        Assert.Equal(3, written.Count);
    }

    [Fact]
    public async Task A_failure_to_write_is_swallowed()
    {
        var notifier = new ActivityNotifier(_ => throw new InvalidOperationException("database busy"), new Clock());
        Assert.False(await notifier.NotifyAsync(Entry(ActivityStatus.Failed)));
    }

    [Fact]
    public void Recording_an_entry_passes_it_on()
    {
        var store = new IngestStateStore(Path.Combine(_dir, "state.json"), NullLogger<IngestStateStore>.Instance);
        var seen = new List<ActivityEntry>();
        store.Recorded = seen.Add;

        store.Record(Entry(ActivityStatus.Filed));

        Assert.Single(seen);
    }
}
