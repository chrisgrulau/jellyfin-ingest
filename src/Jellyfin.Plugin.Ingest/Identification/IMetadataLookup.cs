using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// A search hit from a metadata provider.
/// </summary>
public sealed record MetadataCandidate
{
    /// <summary>Gets the title as the provider spells it.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the release / first-air year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets provider ids keyed by provider name (<c>Tmdb</c>, <c>Tvdb</c>, <c>Imdb</c>).</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the provider that returned the hit (informational).</summary>
    public string? Source { get; init; }

    /// <summary>Gets a value indicating whether the hit is a series (otherwise a film).</summary>
    public bool IsSeries { get; init; }

    /// <summary>Gets the position in the provider's own result list (0 = its best match), if known.</summary>
    public int? ProviderRank { get; init; }

    /// <summary>
    /// Gets other names for the same title, such as the English one when the server's metadata language isn't English.
    /// Release names are scored against each; folders are named after <see cref="Name"/>.
    /// </summary>
    public IReadOnlyList<string> AlternativeNames { get; init; } = [];
}

/// <summary>
/// One episode of a season, as listed by the metadata providers.
/// </summary>
/// <param name="Season">Season number (0 for specials).</param>
/// <param name="Episode">Episode number.</param>
/// <param name="Title">Episode title.</param>
/// <param name="Year">Year first aired, if known.</param>
/// <param name="Overview">Short synopsis, if known.</param>
public sealed record EpisodeListing(int Season, int Episode, string Title, int? Year, string? Overview);

/// <summary>
/// The provider operations identification needs. In the plugin this is backed by Jellyfin's provider manager, so it
/// uses whatever metadata providers (and keys) the server already has configured.
/// </summary>
public interface IMetadataLookup
{
    /// <summary>Searches for series by name.</summary>
    /// <param name="name">Series name.</param>
    /// <param name="year">First-air year, if known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidates, best first as the provider ranks them.</returns>
    Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken);

    /// <summary>Searches for movies by name.</summary>
    /// <param name="name">Movie title.</param>
    /// <param name="year">Release year, if known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidates, best first as the provider ranks them.</returns>
    Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken);

    /// <summary>Looks up an episode's title.</summary>
    /// <param name="seriesProviderIds">Provider ids of the identified series.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episode title, or <c>null</c> if the provider doesn't know the episode.</returns>
    Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken);

    /// <summary>
    /// Lists one season's episodes (numbers, titles, first-aired year and a short synopsis), for files that name an
    /// episode by title only. Lookups that can't list return nothing.
    /// </summary>
    /// <param name="seriesProviderIds">The series' provider ids.</param>
    /// <param name="season">The season (0 for specials).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes found, in order.</returns>
    Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken);
}
