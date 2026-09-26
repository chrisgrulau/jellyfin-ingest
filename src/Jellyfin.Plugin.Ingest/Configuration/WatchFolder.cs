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

    /// <summary>
    /// Gets or sets how files reach the library: moved (the default, right for Usenet), copied, or hard-linked (same
    /// drive only; no extra space). Copy and hard link leave the release where it is, so a torrent client keeps seeding.
    /// </summary>
    public TransferMode Transfer { get; set; } = TransferMode.Move;
}

/// <summary>
/// How a watch folder's files reach the library.
/// </summary>
public enum TransferMode
{
    /// <summary>Moved: the watch folder is emptied.</summary>
    Move = 0,

    /// <summary>Copied: the release stays where it is (for seeding); uses the space twice.</summary>
    Copy,

    /// <summary>Hard-linked: the release stays where it is and no extra space is used; copied instead when on another drive.</summary>
    HardLink,
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
