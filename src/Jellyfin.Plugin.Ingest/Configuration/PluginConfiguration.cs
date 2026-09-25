using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ingest.Configuration;

/// <summary>
/// Plugin settings, persisted by Jellyfin as XML in the plugin configurations folder.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the folders to watch for new media.
    /// </summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Jellyfin deserializes plugin configuration from JSON, which cannot populate a get-only collection.")]
    public Collection<WatchFolder> WatchFolders { get; set; } = [];

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
    /// Gets or sets how long (seconds) nothing in a release may change (no file added, resized or written) before it is
    /// processed. Five minutes by default: long enough for a pause in a copy or download to be told apart from its end.
    /// </summary>
    public int SettleSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin refreshes the film or show folders filed into after an ingest (only
    /// those folders, not whole libraries).
    /// </summary>
    public bool ScanLibraryAfterIngest { get; set; } = true;
}
