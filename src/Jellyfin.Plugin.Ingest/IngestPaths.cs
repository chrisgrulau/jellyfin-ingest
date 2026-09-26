using System;
using System.IO;

namespace Jellyfin.Plugin.Ingest;

/// <summary>
/// Where Ingest keeps its own files: one place that works out the plugin's data folder, registered with dependency
/// injection so nothing needs the plugin instance to find it.
/// </summary>
public sealed class IngestPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IngestPaths"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    public IngestPaths(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        DataFolder = dataFolder;
    }

    /// <summary>Gets the plugin's data folder.</summary>
    public string DataFolder { get; }

    /// <summary>Gets the reviews and activity shown on the dashboard.</summary>
    public string StateFile => Path.Combine(DataFolder, "state.json");

    /// <summary>Gets the action log: one line per file move, so each can be reversed or recovered.</summary>
    public string ActionLog => Path.Combine(DataFolder, "actions.jsonl");

    /// <summary>Gets the remembered provider searches.</summary>
    public string SearchCache => Path.Combine(DataFolder, "search-cache.json");

    /// <summary>
    /// The data folder under Jellyfin's plugins folder: the same folder as the plugin's <c>DataFolderPath</c>, worked out
    /// without needing the plugin instance to exist yet.
    /// </summary>
    /// <param name="pluginsPath">Jellyfin's plugins folder.</param>
    /// <returns>The paths.</returns>
    public static IngestPaths UnderPlugins(string pluginsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsPath);
        return new IngestPaths(Path.Combine(pluginsPath, typeof(IngestPaths).Assembly.GetName().Name!));
    }
}
