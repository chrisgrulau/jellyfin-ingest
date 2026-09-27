using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Presentation;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// A file filed but not readable back through the library's mount (a stale virtiofs cache) is never shown as a plain
// success: the filing says so, and the storage problem gets an entry of its own saying what to change
public sealed class UnreadableReportTests : IDisposable
{
    private const string Filed = "/lib/Films/A (2019)/A (2019).mkv";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ingest-unreadable-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static ExecutionReport Report(params string[] unreadable) => new()
    {
        Completed = [new PlannedOperation(OperationKind.Video, "/drop/r/a.mkv", Filed)],
        Unreadable = unreadable,
    };

    [Fact]
    public void A_filing_with_an_unreadable_file_says_so()
    {
        var report = Report(Filed);

        Assert.True(report.Succeeded);
        Assert.Equal([$"Needs attention: {Filed} was written, but can't be read back through the library's mount."], ActivityReport.UnreadableLines(report));
        Assert.Contains("can't be read back", ActivityReport.UnreadableSentence(report), StringComparison.Ordinal);
        Assert.Empty(ActivityReport.UnreadableLines(Report()));
        Assert.Empty(ActivityReport.UnreadableSentence(Report()));
    }

    [Fact]
    public void The_storage_problem_is_recorded_as_a_failure_of_its_own_with_what_to_do()
    {
        Directory.CreateDirectory(_dir);
        var state = new IngestStateStore(Path.Combine(_dir, "state.json"));

        Assert.False(ActivityReport.RecordUnreadable(state, Now, "r", Report()));
        Assert.Empty(state.Snapshot().Activity);

        Assert.True(ActivityReport.RecordUnreadable(state, Now, "r", Report(Filed)));
        var entry = Assert.Single(state.Snapshot().Activity);
        Assert.Equal(ActivityStatus.Failed, entry.Status);
        Assert.Equal(ActivityReport.StorageRelease, entry.Release);
        Assert.Equal(PlanExecutor.UnreadableAdvice, entry.Summary);
        Assert.Contains(Filed, entry.Details);
    }

    [Fact]
    public void The_page_shows_the_filing_as_needing_attention()
    {
        var entry = new ActivityEntry
        {
            Time = Now,
            Status = ActivityStatus.Filed,
            Release = "A.2019.1080p-GRP",
            WatchFolder = "/drop",
            Run = "run",
            Summary = "Filed 1 video." + ActivityReport.UnreadableSentence(Report(Filed)),
            Details = [.. ActivityReport.UnreadableLines(Report(Filed)), "Video: r/a.mkv → " + Filed],
        };

        var view = new IngestPresenter([new MediaLibrary("m", "Movies", LibraryKind.Films, ["/lib/Films"])]).Activity(entry, Now);

        Assert.Contains("needs attention", view.Item.Summary, StringComparison.Ordinal);
        Assert.Contains(view.Item.Chips, c => c.Label.Contains("need attention", StringComparison.Ordinal));
        Assert.Contains(view.Item.Details, d => d.Group == "Needs attention" && d.Value.Contains(Filed, StringComparison.Ordinal));
    }
}
