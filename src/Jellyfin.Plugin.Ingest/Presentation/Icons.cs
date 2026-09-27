namespace Jellyfin.Plugin.Ingest.Presentation;

/// <summary>
/// The page's small set of icons. Each is always shown with a tooltip and an accessible name saying what it means.
/// </summary>
public static class Icons
{
    /// <summary>Filed into the library.</summary>
    public const string Filed = "✅";

    /// <summary>A dry run: planned, nothing moved.</summary>
    public const string DryRun = "🧪";

    /// <summary>Needs a person's decision, or attention.</summary>
    public const string Review = "⚠️";

    /// <summary>A person's decision.</summary>
    public const string Decision = "👤";

    /// <summary>Something went wrong.</summary>
    public const string Failed = "❌";

    /// <summary>Put back: undone, or restored from quarantine.</summary>
    public const string PutBack = "↩️";

    /// <summary>Videos.</summary>
    public const string Video = "🎬";

    /// <summary>Subtitles.</summary>
    public const string Subtitles = "💬";

    /// <summary>Extras (trailers, featurettes …).</summary>
    public const string Extras = "📦";

    /// <summary>Quarantined files (release clutter, or a whole release).</summary>
    public const string Quarantine = "🗑️";

    /// <summary>Copies already on the server that were replaced.</summary>
    public const string Replaced = "♻️";

    /// <summary>Decided with help from the AI plugin.</summary>
    public const string Ai = "🤖";

    /// <summary>Expired quarantine deleted.</summary>
    public const string Purged = "🧹";

    /// <summary>Waiting for a release to finish arriving.</summary>
    public const string Waiting = "⏳";

    /// <summary>Being identified or filed right now.</summary>
    public const string Working = "⚙️";
}
