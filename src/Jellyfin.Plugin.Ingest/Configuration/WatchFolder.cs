using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

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
    /// Gets or sets a value indicating whether this folder is being watched.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how files reach the library: moved (the default, right for Usenet), copied, or hard-linked (same
    /// drive only; no extra space). Copy and hard link leave the release where it is, so a torrent client keeps seeding.
    /// </summary>
    public TransferMode Transfer { get; set; } = TransferMode.Move;

    /// <summary>
    /// Gets or sets a value indicating whether this folder only records what it would do, moving nothing (ING-32). The
    /// settings page turns it on for a new folder. <c>null</c> in a configuration saved before dry run was per folder:
    /// such a folder takes the old global setting (<see cref="PluginConfiguration.DryRun"/>) when the plugin loads.
    /// </summary>
    public bool? DryRun { get; set; }

    /// <summary>
    /// Whether this folder is in dry run: its own setting, or <paramref name="fallback"/> when it has none.
    /// </summary>
    /// <param name="fallback">The global setting (<see cref="PluginConfiguration.DryRun"/>).</param>
    /// <returns><c>true</c> to move nothing.</returns>
    public bool IsDryRun(bool fallback) => DryRun ?? fallback;

    /// <summary>
    /// Gives every folder saved without its own dry-run setting the old global one, so upgrading changes nothing.
    /// </summary>
    /// <param name="folders">The watch folders.</param>
    /// <param name="globalDryRun">The global setting they had until now.</param>
    /// <returns>Whether any folder changed (the configuration should be saved).</returns>
    public static bool MigrateDryRun(IEnumerable<WatchFolder> folders, bool globalDryRun)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var changed = false;
        foreach (var folder in folders.Where(f => f is not null && f.DryRun is null))
        {
            folder.DryRun = globalDryRun;
            changed = true;
        }

        return changed;
    }
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
    /// Gets or sets the library folder to file into, for libraries with several folders. Empty = the folder with the most
    /// free space, for new films and shows (those already on the server are added to wherever they are).
    /// </summary>
    public string Path { get; set; } = string.Empty;
}
