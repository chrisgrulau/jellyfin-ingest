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
    /// <summary>The most episodes <see cref="ListSeasonAsync"/> asks for in one season.</summary>
    public const int MaxListed = 300;

    private readonly IProviderManager _providers;
    private readonly Func<string?> _metadataLanguage;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinMetadataLookup"/> class.
    /// </summary>
    /// <param name="providers">Jellyfin's provider manager.</param>
    /// <param name="metadataLanguage">The server's preferred metadata language (when not English, English names are
    /// searched too).</param>
    public JellyfinMetadataLookup(IProviderManager providers, Func<string?>? metadataLanguage = null)
    {
        _metadataLanguage = metadataLanguage ?? (() => null);
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
        => WithEnglishAsync(async language => await _providers.GetRemoteSearchResults<Series, SeriesInfo>(new RemoteSearchQuery<SeriesInfo> { SearchInfo = new SeriesInfo { Name = name, Year = year, MetadataLanguage = language } }, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
        => WithEnglishAsync(async language => await _providers.GetRemoteSearchResults<Movie, MovieInfo>(new RemoteSearchQuery<MovieInfo> { SearchInfo = new MovieInfo { Name = name, Year = year, MetadataLanguage = language } }, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Adds the English names of a search in the server's metadata language to the matching hits (same provider id),
    /// and English-only hits after them. Release names are mostly English, so this is how "Money.Heist" finds
    /// "Haus des Geldes" on a German server.
    /// </summary>
    /// <param name="local">Hits in the server's metadata language.</param>
    /// <param name="english">Hits for the same search in English.</param>
    /// <returns>The merged hits.</returns>
    public static IReadOnlyList<MetadataCandidate> Merge(IReadOnlyList<MetadataCandidate> local, IReadOnlyList<MetadataCandidate> english)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(english);
        static bool Same(MetadataCandidate a, MetadataCandidate b)
            => a.ProviderIds.Any(p => b.ProviderIds.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.OrdinalIgnoreCase));

        var used = new HashSet<MetadataCandidate>(ReferenceEqualityComparer.Instance);
        var merged = new List<MetadataCandidate>();
        foreach (var l in local)
        {
            var e = english.FirstOrDefault(x => !used.Contains(x) && Same(l, x));
            if (e is not null)
            {
                used.Add(e);
            }

            merged.Add(e is not null && !string.Equals(e.Name, l.Name, StringComparison.Ordinal) ? l with { AlternativeNames = [.. l.AlternativeNames, e.Name] } : l);
        }

        merged.AddRange(english.Where(x => !used.Contains(x)));
        return merged;
    }

    // One search in the server's metadata language and, when that isn't English, one in English
    private async Task<IReadOnlyList<MetadataCandidate>> WithEnglishAsync(Func<string?, Task<IEnumerable<RemoteSearchResult>>> search)
    {
        var local = ToCandidates(await search(null).ConfigureAwait(false));
        var language = _metadataLanguage();
        if (string.IsNullOrWhiteSpace(language) || language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return local;
        }

        return Merge(local, ToCandidates(await search("en").ConfigureAwait(false)));
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

    /// <inheritdoc />
    /// <remarks>
    /// The providers answer one episode at a time, so this asks for 1, 2, 3 … until three in a row are missing (at most
    /// <see cref="MaxListed"/>). The providers keep whole seasons in their own caches, so after the first answer the
    /// rest are cheap.
    /// </remarks>
    public async Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seriesProviderIds);

        var list = new List<EpisodeListing>();
        var missing = 0;
        for (var episode = 1; episode <= MaxListed && missing < 3; episode++)
        {
            var info = new EpisodeInfo { ParentIndexNumber = season, IndexNumber = episode };
            foreach (var (provider, id) in seriesProviderIds)
            {
                info.SeriesProviderIds[provider] = id;
            }

            var results = await _providers.GetRemoteSearchResults<Episode, EpisodeInfo>(new RemoteSearchQuery<EpisodeInfo> { SearchInfo = info }, cancellationToken).ConfigureAwait(false);
            var hit = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Name));
            if (hit is null)
            {
                missing++;
                continue;
            }

            missing = 0;
            var overview = results.Select(r => r.Overview).FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));
            list.Add(new EpisodeListing(season, episode, hit.Name!, hit.ProductionYear ?? hit.PremiereDate?.Year, overview));
        }

        return list;
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
