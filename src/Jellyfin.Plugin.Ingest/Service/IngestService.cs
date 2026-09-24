using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Background service: sweeps every enabled watch folder, and for each release that has finished arriving, plans and
/// (unless in dry-run) executes the ingest, then queues a library scan. A periodic sweep is used rather than file-system
/// notifications, which are unreliable on network and virtualised shares.
/// </summary>
public sealed partial class IngestService : IHostedService, IDisposable
{
    /// <summary>How often watch folders are swept.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private const long MaxSubtitleBytesToRead = 4L * 1024 * 1024;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<IngestService> _logger;
    private readonly Dictionary<string, ReleaseTracker> _trackers = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = TimeProvider.System;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="providerManager">Jellyfin provider manager.</param>
    /// <param name="logger">Logger.</param>
    public IngestService(ILibraryManager libraryManager, IProviderManager providerManager, ILogger<IngestService> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _providerManager = providerManager ?? throw new ArgumentNullException(nameof(providerManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null || _loop is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    /// <summary>
    /// Resolves where a watch folder's quarantine lives.
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="watchFolder">The watch folder.</param>
    /// <returns>Absolute quarantine path.</returns>
    public static string QuarantineFor(PluginConfiguration configuration, WatchFolder watchFolder)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(watchFolder);
        return string.IsNullOrWhiteSpace(configuration.QuarantinePath)
            ? Path.Combine(watchFolder.Path, ".ingest-quarantine")
            : configuration.QuarantinePath;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The service must survive any single failed sweep; the error is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSweepFailed(_logger, ex);
            }

            try
            {
                await Task.Delay(SweepInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var plugin = IngestPlugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var config = plugin.Configuration;
        var libraries = _libraryManager.GetVirtualFolders();
        foreach (var watch in config.WatchFolders.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Path)))
        {
            if (!Directory.Exists(watch.Path))
            {
                LogMissingWatchFolder(_logger, watch.Path);
                continue;
            }

            var library = libraries.FirstOrDefault(l => string.Equals(l.ItemId, watch.TargetLibraryId, StringComparison.OrdinalIgnoreCase));
            var root = !string.IsNullOrWhiteSpace(watch.TargetPath) ? watch.TargetPath : library?.Locations?.FirstOrDefault();
            if (library is null || string.IsNullOrWhiteSpace(root))
            {
                LogNoLibrary(_logger, watch.Path);
                continue;
            }

            var isTv = string.Equals(library.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase);
            var quarantine = QuarantineFor(config, watch);
            var snapshot = Snapshot(watch.Path, quarantine);
            if (!_trackers.TryGetValue(watch.Path, out var tracker))
            {
                tracker = new ReleaseTracker();
                _trackers[watch.Path] = tracker;
            }

            foreach (var release in tracker.Observe(snapshot, _clock.GetUtcNow(), TimeSpan.FromSeconds(Math.Max(5, config.SettleSeconds))))
            {
                ct.ThrowIfCancellationRequested();
                await IngestAsync(plugin.DataFolderPath, config, watch, release, snapshot[release], new LibraryTarget(root, isTv), library.ItemId, quarantine, ct).ConfigureAwait(false);
                tracker.MarkHandled(release);
            }
        }
    }

    private static Dictionary<string, IReadOnlyList<ReleaseFile>> Snapshot(string watchFolder, string quarantine)
    {
        var snapshot = new Dictionary<string, IReadOnlyList<ReleaseFile>>(StringComparer.Ordinal);
        var quarantineFull = Path.GetFullPath(quarantine);
        foreach (var entry in Directory.EnumerateFileSystemEntries(watchFolder))
        {
            var name = Path.GetFileName(entry);
            if (ReleaseTracker.IsIgnored(name) || string.Equals(Path.GetFullPath(entry), quarantineFull, StringComparison.Ordinal))
            {
                continue;
            }

            snapshot[name] = File.Exists(entry)
                ? [new ReleaseFile(name, new FileInfo(entry).Length)]
                : [.. Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories)
                    .Select(f => new ReleaseFile(Path.GetRelativePath(watchFolder, f), new FileInfo(f).Length))];
        }

        return snapshot;
    }

    private async Task IngestAsync(
        string dataFolder,
        PluginConfiguration config,
        WatchFolder watch,
        string release,
        IReadOnlyList<ReleaseFile> files,
        LibraryTarget target,
        string libraryId,
        string quarantine,
        CancellationToken ct)
    {
        var identifier = new MediaIdentifier(
            new JellyfinMetadataLookup(_providerManager),
            new JellyfinLibraryIndex(_libraryManager, Guid.TryParse(libraryId, out var id) ? id : null));
        var planner = new IngestPlanner(identifier, p => File.Exists(p) || Directory.Exists(p), ReadSmallText, _clock);
        var plan = await planner.PlanAsync(watch.Path, release, files, target, quarantine, ct).ConfigureAwait(false);

        Directory.CreateDirectory(dataFolder);
        if (!plan.IsReady)
        {
            foreach (var item in plan.Review)
            {
                LogNeedsReview(_logger, release, item.Source, item.Reason);
            }

            await File.AppendAllLinesAsync(
                Path.Combine(dataFolder, "review.jsonl"),
                [JsonSerializer.Serialize(new { time = _clock.GetUtcNow(), watchFolder = watch.Path, release, items = plan.Review })],
                ct).ConfigureAwait(false);
            return;
        }

        var report = new PlanExecutor(new PhysicalFileOperations(), _clock)
            .Execute(plan, Path.Combine(watch.Path, release), Path.Combine(dataFolder, "actions.jsonl"), config.DryRun);
        var prefix = config.DryRun ? "[dry run] would" : "Did";
        foreach (var op in report.Completed)
        {
            LogOperation(_logger, prefix, op.Kind, op.Source, op.Destination);
        }

        if (!report.Succeeded)
        {
            LogExecutionFailed(_logger, release, report.Error ?? "unknown error");
            return;
        }

        if (!config.DryRun && config.ScanLibraryAfterIngest)
        {
            _libraryManager.QueueLibraryScan();
        }
    }

    private static string? ReadSmallText(string path)
    {
        try
        {
            return new FileInfo(path).Length <= MaxSubtitleBytesToRead ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest watch folder does not exist: {Path}")]
    private static partial void LogMissingWatchFolder(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest watch folder {Path} has no valid target library")]
    private static partial void LogNoLibrary(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: {Release} needs review - {Source}: {Reason}")]
    private static partial void LogNeedsReview(ILogger logger, string release, string source, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingest: {Prefix} {Kind} {Source} -> {Destination}")]
    private static partial void LogOperation(ILogger logger, string prefix, OperationKind kind, string source, string destination);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest of {Release} stopped: {Error}")]
    private static partial void LogExecutionFailed(ILogger logger, string release, string error);
}
