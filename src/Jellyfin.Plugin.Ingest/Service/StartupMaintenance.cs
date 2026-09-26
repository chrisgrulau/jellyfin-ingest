using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Quarantine;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// The service's housekeeping, run at the start of a sweep: on the first sweep, moves interrupted by a crash or restart
/// are recovered and dated quarantine folders made before markers existed are marked; about daily, the action log is
/// trimmed (after any recovery has read it).
/// </summary>
public sealed partial class StartupMaintenance
{
    private readonly IngestStateStore _state;
    private readonly IngestPaths _paths;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private bool _markersMigrated;
    private DateTimeOffset _logTrimmed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupMaintenance"/> class.
    /// </summary>
    /// <param name="state">Activity shown on the dashboard (recoveries are reported there).</param>
    /// <param name="paths">Where the action log is.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">The service's logger.</param>
    public StartupMaintenance(IngestStateStore state, IngestPaths paths, TimeProvider clock, ILogger logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs whatever is due: recovery and marker migration once, then trimming the action log if a day has passed.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="libraries">The server's libraries (recovery only touches their folders and the quarantines).</param>
    /// <param name="problems">Folder problems (a quarantine that breaks the rules is never marked).</param>
    public void RunDue(PluginConfiguration config, IReadOnlyList<MediaLibrary> libraries, IReadOnlyList<FolderProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(problems);
        if (!_markersMigrated)
        {
            _markersMigrated = true;
            RecoverInterruptedMoves(config, libraries);
            MigrateQuarantineMarkers(config, problems);
        }

        // Keep only recent history in the action log (after any recovery has read it), checked daily
        if (_clock.GetUtcNow() - _logTrimmed >= TimeSpan.FromDays(1))
        {
            _logTrimmed = _clock.GetUtcNow();
            TrimActionLog();
        }
    }

    // Moves cut short by a crash or restart are finished (the file had fully arrived) or discarded (the original is intact)
    private void RecoverInterruptedMoves(PluginConfiguration config, IReadOnlyList<MediaLibrary> libraries)
    {
        var log = _paths.ActionLog;
        if (!File.Exists(log))
        {
            return;
        }

        try
        {
            // Only inside today's library and quarantine folders
            var roots = libraries.SelectMany(l => l.Locations)
                .Concat(config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path)).Select(w => FolderPolicy.QuarantineFor(config, w)))
                .ToList();
            var results = new PlanExecutor(new PhysicalFileOperations(), _clock).Recover([.. File.ReadLines(log)], log, roots);
            foreach (var result in results)
            {
                LogRecovery(_logger, result);
            }

            if (results.Count > 0)
            {
                _state.Record(new ActivityEntry
                {
                    Time = _clock.GetUtcNow(),
                    Status = results.Any(r => r.StartsWith("Needs attention", StringComparison.Ordinal)) ? ActivityStatus.Failed : ActivityStatus.Filed,
                    Release = "Interrupted moves",
                    Summary = string.Create(CultureInfo.InvariantCulture, $"Recovered {results.Count} move(s) interrupted by a restart."),
                    Details = results,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMarkerMigrationFailed(_logger, ex);
        }
    }

    private void TrimActionLog()
    {
        try
        {
            var removed = ActionLog.TrimFile(_paths.ActionLog, _clock.GetUtcNow());
            if (removed > 0)
            {
                LogActionLogTrimmed(_logger, removed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogActionLogTrimFailed(_logger, ex);
        }
    }

    // Dated quarantine folders created before markers existed are marked if the action log shows Ingest filled them
    private void MigrateQuarantineMarkers(PluginConfiguration config, IReadOnlyList<FolderProblem> problems)
    {
        var log = _paths.ActionLog;
        if (!File.Exists(log))
        {
            return;
        }

        var roots = config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path))
            .Select(w => FolderPolicy.QuarantineFor(config, w))
            .Where(q => !problems.Any(p => PathGuard.SamePath(p.Folder, q)))
            .Select(PathGuard.Normalise)
            .Distinct(PathGuard.Comparer)
            .ToList();
        try
        {
            IReadOnlySet<string>? logged = null;
            foreach (var (root, dated) in QuarantineMarkers.FromActionLog(File.ReadLines(log), roots))
            {
                if (!Directory.Exists(dated) || QuarantineMarkers.IsMarked(dated))
                {
                    continue;
                }

                // Only a folder holding nothing but files Ingest logged moving there: marking it lets the purge delete it
                logged ??= QuarantineMarkers.QuarantinedFiles(File.ReadLines(log));
                if (QuarantineMarkers.HoldsOnlyLoggedFiles(dated, logged))
                {
                    File.WriteAllText(Path.Combine(dated, QuarantineMarkers.DatedMarker), "Created by the Jellyfin Ingest plugin (marked from its action log).\n");
                    LogMarkedQuarantine(_logger, dated);
                }
                else
                {
                    LogNotMarkedQuarantine(_logger, dated);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMarkerMigrationFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest quarantine: {Path} holds files Ingest didn't put there, so it isn't marked and will never be purged")]
    private static partial void LogNotMarkedQuarantine(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: marked existing quarantine folder {Folder} as Ingest's own (from the action log)")]
    private static partial void LogMarkedQuarantine(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest: couldn't mark existing quarantine folders")]
    private static partial void LogMarkerMigrationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest recovery: {Result}")]
    private static partial void LogRecovery(ILogger logger, string result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: removed {Count} old line(s) from the action log")]
    private static partial void LogActionLogTrimmed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest: couldn't trim the action log")]
    private static partial void LogActionLogTrimFailed(ILogger logger, Exception exception);
}
