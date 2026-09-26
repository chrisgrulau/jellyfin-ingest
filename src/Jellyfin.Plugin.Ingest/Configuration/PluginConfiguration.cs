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

    /// <summary>
    /// Gets or sets a value indicating whether close matches are settled by the family's AI plugin, when it is installed
    /// and allowed to help Ingest (its own settings page decides that, and its spending limits apply). Its pick must be
    /// one of the candidates found; otherwise the release waits for review as before.
    /// </summary>
    public bool UseAiTiebreak { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an episode whose name doesn't say which episode it is may be identified from
    /// a short transcript: the family's Subtitles plugin transcribes two minutes of it (only if its settings allow Ingest
    /// to ask; built-in speech-to-text by default) and the AI plugin compares that with the episode synopses. Needs
    /// <see cref="UseAiTiebreak"/>.
    /// </summary>
    public bool UseTranscripts { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether releases waiting for a decision, failures, filings and quarantines are
    /// also written to Jellyfin's Activity log (Dashboard → Activity), at most once a day per release and outcome.
    /// </summary>
    public bool WriteToActivityLog { get; set; } = true;
}
