using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ingest.Configuration;

/// <summary>
/// Plugin settings, persisted by Jellyfin as XML in the plugin configurations folder.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets the folders to watch for new media.
    /// </summary>
    public Collection<WatchFolder> WatchFolders { get; } = [];

    /// <summary>
    /// Gets or sets the quarantine folder for release clutter. Empty = <c>&lt;watch folder&gt;/.ingest-quarantine</c>.
    /// </summary>
    public string QuarantinePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many days quarantined items are kept before the scheduled purge removes them.
    /// </summary>
    public int QuarantineRetentionDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether to only log planned actions without touching any files.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Gets or sets how long (seconds) every file in a release must keep the same size before it is processed.
    /// </summary>
    public int SettleSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether to scan the target library after an ingest.
    /// </summary>
    public bool ScanLibraryAfterIngest { get; set; } = true;
}
