using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Ingest.Configuration;

/// <summary>
/// A drop folder and the libraries its media is filed into.
/// </summary>
public class WatchFolder
{
    /// <summary>
    /// Gets or sets the absolute path of the folder to watch.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where media from this folder is filed. At most one destination may take shows and at most one films,
    /// so a Mixed Movies and Shows library must be the only destination. Each release goes to the destination for its kind.
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Jellyfin deserializes plugin configuration from JSON, which cannot populate a get-only collection.")]
    public Collection<LibraryDestination> Destinations { get; set; } = [];

    /// <summary>
    /// Gets or sets the library of a configuration saved by 0.1.0-alpha.1 (single destination). Only read when
    /// <see cref="Destinations"/> is empty; the settings page converts it on the next save.
    /// </summary>
    public string TargetLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library folder that goes with <see cref="TargetLibraryId"/>.
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this folder is being watched.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A library (and optionally one of its folders) a watch folder files into.
/// </summary>
public class LibraryDestination
{
    /// <summary>
    /// Gets or sets the Jellyfin library id.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library folder to file into, for libraries with several folders. Empty = the library's first folder.
    /// </summary>
    public string Path { get; set; } = string.Empty;
}
