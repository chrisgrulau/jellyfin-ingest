using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Configuration;
using Jellyfin.Plugin.Ingest.Planning;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// The folder-safety rules as the service, the API and the purge task apply them: where each watch folder's quarantine
/// is, which of Jellyfin's folders are off limits, and which configured folders break <see cref="FolderRules"/>.
/// </summary>
public static class FolderPolicy
{
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

    /// <summary>
    /// Jellyfin's own folders, which no watch or quarantine folder may overlap.
    /// </summary>
    /// <param name="paths">Jellyfin's application paths.</param>
    /// <returns>The folders.</returns>
    /// <param name="configuration">Jellyfin's configuration (for a transcode folder moved elsewhere), if available.</param>
    public static IReadOnlyList<string> ProtectedFolders(IApplicationPaths paths, IConfigurationManager? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // The metadata and transcode folders can be moved out of the data folder in Jellyfin's settings
        string? metadata = (paths as IServerApplicationPaths)?.InternalMetadataPath;
        string? transcode = null;
        try
        {
            transcode = configuration?.GetTranscodePath();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Not configured yet: the default lives in the cache folder, which is already listed
        }

        return [.. new[] { paths.ProgramDataPath, paths.ProgramSystemPath, paths.DataPath, paths.ConfigurationDirectoryPath, paths.CachePath, paths.LogDirectoryPath, paths.PluginsPath, paths.TempDirectory, metadata, transcode }
            .OfType<string>().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Checks the configured watch and quarantine folders against the rules in <see cref="FolderRules"/>.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="libraries">The server's libraries.</param>
    /// <param name="paths">Jellyfin's application paths.</param>
    /// <param name="configuration">Jellyfin's configuration, if available.</param>
    /// <returns>The problems found.</returns>
    public static IReadOnlyList<FolderProblem> FolderProblems(PluginConfiguration config, IEnumerable<MediaLibrary> libraries, IApplicationPaths paths, IConfigurationManager? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return FolderRules.Check(
            [.. config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path)).Select(w => w.Path)],
            config.QuarantinePath,
            [.. libraries.SelectMany(l => l.Locations)],
            ProtectedFolders(paths, configuration));
    }

    /// <summary>
    /// The quarantine folders in use that pass the folder rules (one pointed at a library, say, is never purged or listed).
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="problems">Problems from <see cref="FolderProblems"/>.</param>
    /// <returns>The quarantine folders, each once.</returns>
    public static IReadOnlyList<string> SafeQuarantineRoots(PluginConfiguration config, IReadOnlyList<FolderProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(problems);
        return [.. config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path))
            .Where(w => !problems.Any(p => PathGuard.SamePath(p.Folder, w.Path) || PathGuard.SamePath(p.Folder, config.QuarantinePath)))
            .Select(w => PathGuard.Normalise(QuarantineFor(config, w)))
            .Distinct(PathGuard.Comparer)];
    }

    /// <summary>
    /// The folders an undo or restore may put files back into or take them from: the watch folders and quarantines that
    /// pass the folder rules, and the server's library folders.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="libraries">The server's libraries.</param>
    /// <param name="problems">Problems from <see cref="FolderProblems"/>.</param>
    /// <returns>The scope.</returns>
    public static ReturnScope ReturnScopeOf(PluginConfiguration config, IEnumerable<MediaLibrary> libraries, IReadOnlyList<FolderProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(problems);
        return new ReturnScope(
            [.. config.WatchFolders.Where(w => !string.IsNullOrWhiteSpace(w.Path) && Path.IsPathFullyQualified(w.Path) && !problems.Any(p => PathGuard.SamePath(p.Folder, w.Path))).Select(w => w.Path)],
            [.. libraries.SelectMany(l => l.Locations).Where(l => !string.IsNullOrWhiteSpace(l) && Path.IsPathFullyQualified(l))],
            SafeQuarantineRoots(config, problems));
    }

    /// <summary>
    /// Reduces Jellyfin's libraries to what routing needs.
    /// </summary>
    /// <param name="libraries">The server's libraries.</param>
    /// <returns>The libraries.</returns>
    public static IReadOnlyList<MediaLibrary> Libraries(IEnumerable<VirtualFolderInfo> libraries)
        => [.. (libraries ?? throw new ArgumentNullException(nameof(libraries)))
            .Where(l => !string.IsNullOrEmpty(l.ItemId))
            .Select(l => new MediaLibrary(l.ItemId, l.Name ?? l.ItemId, LibraryRouting.KindOf(l.CollectionType?.ToString()), l.Locations ?? []))];

    /// <summary>
    /// A watch folder's configured destinations.
    /// </summary>
    /// <param name="watch">The watch folder.</param>
    /// <returns>The destinations, in order.</returns>
    public static IReadOnlyList<DestinationSetting> DestinationsOf(WatchFolder watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        return [.. watch.Destinations.Select(d => new DestinationSetting(d.LibraryId, d.Path))];
    }
}
