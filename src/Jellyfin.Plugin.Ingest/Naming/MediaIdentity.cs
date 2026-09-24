namespace Jellyfin.Plugin.Ingest.Naming;

/// <summary>
/// A movie as identified by a metadata provider.
/// </summary>
public sealed record MovieIdentity
{
    /// <summary>Gets the canonical title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the release year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the TMDb id.</summary>
    public string? TmdbId { get; init; }

    /// <summary>Gets the IMDb id (<c>tt…</c>).</summary>
    public string? ImdbId { get; init; }

    /// <summary>Gets the edition label, e.g. <c>Director's Cut</c>.</summary>
    public string? Edition { get; init; }
}

/// <summary>
/// A TV series as identified by a metadata provider.
/// </summary>
public sealed record SeriesIdentity
{
    /// <summary>Gets the canonical title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the year the series first aired.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the TheTVDB id.</summary>
    public string? TvdbId { get; init; }

    /// <summary>Gets the TMDb id.</summary>
    public string? TmdbId { get; init; }
}

/// <summary>
/// An episode (or multi-episode file) of a series.
/// </summary>
public sealed record EpisodeIdentity
{
    /// <summary>Gets the series the episode belongs to.</summary>
    public required SeriesIdentity Series { get; init; }

    /// <summary>Gets the season number; 0 for specials.</summary>
    public required int Season { get; init; }

    /// <summary>Gets the (first) episode number.</summary>
    public required int Episode { get; init; }

    /// <summary>Gets the last episode number for multi-episode files.</summary>
    public int? EndingEpisode { get; init; }

    /// <summary>Gets the episode title.</summary>
    public string? Title { get; init; }
}
