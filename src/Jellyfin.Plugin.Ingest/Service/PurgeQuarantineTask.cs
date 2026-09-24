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

    /// <summary>
    /// Initializes a new instance of the <see cref="PurgeQuarantineTask"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public PurgeQuarantineTask(ILogger<PurgeQuarantineTask> logger)
    {
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
            foreach (var deleted in QuarantinePurger.Purge(roots[i], today, config.QuarantineRetentionDays))
            {
                LogPurged(_logger, deleted);
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
