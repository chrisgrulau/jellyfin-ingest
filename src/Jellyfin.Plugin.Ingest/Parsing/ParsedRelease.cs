using Jellyfin.Plugin.Ingest.Naming;

namespace Jellyfin.Plugin.Ingest.Parsing;

/// <summary>
/// What kind of media a release name appears to describe.
/// </summary>
public enum MediaKind
{
    /// <summary>Could not tell; identification must decide.</summary>
    Unknown = 0,

    /// <summary>A film.</summary>
    Movie,

    /// <summary>A TV episode (or several).</summary>
    Episode,
}

/// <summary>
/// Everything that could be read from a release's file/folder name, before any provider lookup.
/// </summary>
public sealed record ParsedRelease
{
    /// <summary>Gets the likely media kind.</summary>
    public MediaKind Kind { get; init; }

    /// <summary>Gets the cleaned title (series title for episodes); empty if none could be found.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the release or first-air year, if present.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the season number, if present; 0 for specials (whose number may then be unknown).</summary>
    public int? Season { get; init; }

    /// <summary>Gets the (first) episode number, if present. Null for specials named like <c>S13SP4</c>: match on <see cref="EpisodeTitle"/> instead.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets the last episode number of a multi-episode file.</summary>
    public int? EndingEpisode { get; init; }

    /// <summary>Gets an episode title found after the episode code, if any.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets an edition label (e.g. <c>Director's Cut</c>), if present.</summary>
    public string? Edition { get; init; }

    /// <summary>Gets the extra type suggested by the name (<see cref="ExtraType.None"/> for main content).</summary>
    public ExtraType Extra { get; init; }
}
