using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Quarantine;
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
    private readonly IConfigurationManager _configuration;
    private readonly IngestProgress _progress;

    /// <summary>
    /// Initializes a new instance of the <see cref="PurgeQuarantineTask"/> class.
    /// </summary>
    /// <param name="state">Activity shown on the dashboard.</param>
    /// <param name="libraryManager">Jellyfin library manager (to check the quarantine is safe).</param>
    /// <param name="paths">Jellyfin's own folders (to check the quarantine is safe).</param>
    /// <param name="configuration">Jellyfin's configuration (for the transcode folder).</param>
    /// <param name="progress">Holds the gate filing uses, so the purge never runs while files are being moved.</param>
    /// <param name="logger">Logger.</param>
    public PurgeQuarantineTask(IngestStateStore state, ILibraryManager libraryManager, IApplicationPaths paths, IConfigurationManager configuration, IngestProgress progress, ILogger<PurgeQuarantineTask> logger)
    {
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
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
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var config = IngestPlugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        // Never purge in a quarantine that breaks the folder rules (e.g. one pointed at a library)
        var problems = FolderPolicy.FolderProblems(config, FolderPolicy.Libraries(_libraryManager.GetVirtualFolders()), _paths, _configuration);
        var roots = FolderPolicy.SafeQuarantineRoots(config, problems);
        var retention = Math.Max(1, config.QuarantineRetentionDays);
        var today = DateOnly.FromDateTime(DateTime.Now);

        // Holds the gate filing uses for the whole purge (waiting a little for a filing that is running)
        var results = await QuarantinePurger.PurgeAsync(_progress.FileGate, FileOperationsGate.DefaultWait, roots, today, retention, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (results is null)
        {
            _state.Record(new ActivityEntry
            {
                Time = DateTimeOffset.UtcNow,
                Status = ActivityStatus.Failed,
                Release = "Quarantine purge",
                Summary = "Ingest was filing, so the quarantine wasn't purged this time; it is tried again next time.",
            });
            return;
        }

        for (var i = 0; i < roots.Count; i++)
        {
            var (deleted, failed) = results[i];
            if (failed.Count > 0)
            {
                _state.Record(new ActivityEntry
                {
                    Time = DateTimeOffset.UtcNow,
                    Status = ActivityStatus.Failed,
                    Release = roots[i],
                    Summary = failed.Count == 1 ? "1 quarantine folder couldn't be fully deleted; it is tried again next time." : $"{failed.Count} quarantine folders couldn't be fully deleted; they are tried again next time.",
                    Details = failed,
                });
            }

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
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest quarantine: purged {Path}")]
    private static partial void LogPurged(ILogger logger, string path);
}
