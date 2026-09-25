using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Planning;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
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
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _paths;

    /// <summary>
    /// Initializes a new instance of the <see cref="PurgeQuarantineTask"/> class.
    /// </summary>
    /// <param name="state">Activity shown on the dashboard.</param>
    /// <param name="libraryManager">Jellyfin library manager (to check the quarantine is safe).</param>
    /// <param name="paths">Jellyfin's own folders (to check the quarantine is safe).</param>
    /// <param name="logger">Logger.</param>
    public PurgeQuarantineTask(IngestStateStore state, ILibraryManager libraryManager, IApplicationPaths paths, ILogger<PurgeQuarantineTask> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Purge Shoal Ingest quarantine";

    /// <inheritdoc />
    public string Key => "IngestPurgeQuarantine";

    /// <inheritdoc />
    public string Description => "Deletes release clutter quarantined by Ingest once it is older than the retention period.";

    /// <inheritdoc />
    public string Category => "Shoal";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var config = IngestPlugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.CompletedTask;
        }

        // Never purge in a quarantine that breaks the folder rules (e.g. one pointed at a library)
        var problems = IngestService.FolderProblems(config, IngestService.Libraries(_libraryManager.GetVirtualFolders()), _paths);
        var roots = IngestService.SafeQuarantineRoots(config, problems);
        var retention = Math.Max(1, config.QuarantineRetentionDays);
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var i = 0; i < roots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = QuarantinePurger.Purge(roots[i], today, retention);
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
                        ? $"Deleted 1 quarantine folder older than {retention} days."
                        : $"Deleted {deleted.Count} quarantine folders older than {retention} days.",
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
