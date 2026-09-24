using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Scheduled task (Dashboard → Scheduled Tasks) that deletes quarantined release clutter older than the retention period.
/// </summary>
public sealed partial class PurgeQuarantineTask : IScheduledTask
{
    private readonly ILogger<PurgeQuarantineTask> _logger;
    private readonly IngestStateStore _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="PurgeQuarantineTask"/> class.
    /// </summary>
    /// <param name="state">Activity shown on the dashboard.</param>
    /// <param name="logger">Logger.</param>
    public PurgeQuarantineTask(IngestStateStore state, ILogger<PurgeQuarantineTask> logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Purge Ingest quarantine";

    /// <inheritdoc />
    public string Key => "IngestPurgeQuarantine";

    /// <inheritdoc />
    public string Description => "Deletes release clutter quarantined by Ingest once it is older than the retention period.";

    /// <inheritdoc />
    public string Category => "Ingest";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var config = IngestPlugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.CompletedTask;
        }

        var roots = config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path))
            .Select(w => IngestService.QuarantineFor(config, w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var i = 0; i < roots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = QuarantinePurger.Purge(roots[i], today, config.QuarantineRetentionDays);
            foreach (var path in deleted)
            {
                LogPurged(_logger, path);
            }

            if (deleted.Count > 0)
            {
                _state.Record(new ActivityEntry
                {
                    Time = DateTimeOffset.UtcNow,
                    Status = ActivityStatus.Purged,
                    Release = roots[i],
                    Summary = deleted.Count == 1
                        ? $"Deleted 1 quarantine folder older than {config.QuarantineRetentionDays} days."
                        : $"Deleted {deleted.Count} quarantine folders older than {config.QuarantineRetentionDays} days.",
                    Details = deleted,
                });
            }

            progress.Report(100.0 * (i + 1) / roots.Count);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest quarantine: purged {Path}")]
    private static partial void LogPurged(ILogger logger, string path);
}
