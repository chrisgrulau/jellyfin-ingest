namespace Jellyfin.Plugin.Ingest.Configuration;

/// <summary>
/// A drop folder and the library its media is filed into.
/// </summary>
public class WatchFolder
{
    /// <summary>
    /// Gets or sets the absolute path of the folder to watch.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the Jellyfin library new media is filed into.
    /// </summary>
    public string TargetLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this folder is being watched.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
