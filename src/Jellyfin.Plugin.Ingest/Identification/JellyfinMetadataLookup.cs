using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// <see cref="IMetadataLookup"/> backed by Jellyfin's provider manager: searches go to whichever metadata providers the
/// server has enabled (TMDb, TheTVDB, OMDb …) using the server's own keys, so the plugin needs no credentials.
/// </summary>
public sealed class JellyfinMetadataLookup : IMetadataLookup
{
    private readonly IProviderManager _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinMetadataLookup"/> class.
    /// </summary>
    /// <param name="providers">Jellyfin's provider manager.</param>
    public JellyfinMetadataLookup(IProviderManager providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
    {
        var query = new RemoteSearchQuery<SeriesInfo> { SearchInfo = new SeriesInfo { Name = name, Year = year } };
        var results = await _providers.GetRemoteSearchResults<Series, SeriesInfo>(query, cancellationToken).ConfigureAwait(false);
        return ToCandidates(results);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
    {
        var query = new RemoteSearchQuery<MovieInfo> { SearchInfo = new MovieInfo { Name = name, Year = year } };
        var results = await _providers.GetRemoteSearchResults<Movie, MovieInfo>(query, cancellationToken).ConfigureAwait(false);
        return ToCandidates(results);
    }

    /// <inheritdoc />
    public async Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seriesProviderIds);

        var info = new EpisodeInfo { ParentIndexNumber = season, IndexNumber = episode };
        foreach (var (provider, id) in seriesProviderIds)
        {
            info.SeriesProviderIds[provider] = id;
        }

        var query = new RemoteSearchQuery<EpisodeInfo> { SearchInfo = info };
        var results = await _providers.GetRemoteSearchResults<Episode, EpisodeInfo>(query, cancellationToken).ConfigureAwait(false);
        return results.Select(r => r.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
    }

    private static List<MetadataCandidate> ToCandidates(IEnumerable<RemoteSearchResult> results)
        => [.. results
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select((r, i) => new MetadataCandidate
            {
                Name = r.Name ?? string.Empty,
                Year = r.ProductionYear,
                ProviderIds = ProviderIdRules.Clean(r.ProviderIds),
                Source = r.SearchProviderName,
                ProviderRank = i,
            })];
}
