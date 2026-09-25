using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// <see cref="ILibraryIndex"/> backed by Jellyfin's library: series and movies already in the destination library. The
/// names are loaded once per kind and kept for the life of the index, so create one per sweep: a season pack then loads
/// the server's series once rather than once per episode.
/// </summary>
public sealed class JellyfinLibraryIndex : ILibraryIndex
{
    /// <summary>Minimum title similarity for a library item to be offered as a candidate.</summary>
    public const double MinimumSimilarity = 0.70;

    private readonly ILibraryManager _library;
    private readonly IReadOnlyList<Guid> _libraryIds;
    private readonly Dictionary<BaseItemKind, List<MetadataCandidate>> _loaded = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibraryIndex"/> class.
    /// </summary>
    /// <param name="library">Jellyfin's library manager.</param>
    /// <param name="libraryIds">Restrict to these libraries (collection folders); empty for all libraries.</param>
    public JellyfinLibraryIndex(ILibraryManager library, IReadOnlyList<Guid> libraryIds)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _libraryIds = libraryIds ?? throw new ArgumentNullException(nameof(libraryIds));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataCandidate>> FindSeriesAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult(Find(BaseItemKind.Series, name));

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataCandidate>> FindMoviesAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult(Find(BaseItemKind.Movie, name));

    private IReadOnlyList<MetadataCandidate> Find(BaseItemKind kind, string name)
        => [.. All(kind).Where(c => TitleMatcher.Similarity(name, c.Name) >= MinimumSimilarity)];

    private List<MetadataCandidate> All(BaseItemKind kind)
    {
        lock (_loaded)
        {
            if (_loaded.TryGetValue(kind, out var all))
            {
                return all;
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [kind],
                Recursive = true,
                IsVirtualItem = false,
            };
            if (_libraryIds.Count > 0)
            {
                query.TopParentIds = [.. _libraryIds];
            }

            // Only what matching needs is kept, not the items themselves
            all = [.. _library.GetItemList(query)
                .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                .Select(i => new MetadataCandidate
                {
                    Name = i.Name,
                    Year = i.ProductionYear,
                    ProviderIds = ProviderIdRules.Clean(i.ProviderIds),
                    Source = MediaIdentifier.LibrarySource,
                })];
            _loaded[kind] = all;
            return all;
        }
    }
}
