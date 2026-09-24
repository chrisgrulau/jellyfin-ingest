using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// <see cref="ISeriesLocator"/> backed by Jellyfin's library: looks the series up by TheTVDB / TMDb id across every library.
/// </summary>
public sealed class JellyfinSeriesLocator : ISeriesLocator
{
    private static readonly string[] MatchProviders = ["Tvdb", "Tmdb"];

    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinSeriesLocator"/> class.
    /// </summary>
    /// <param name="library">Jellyfin's library manager.</param>
    public JellyfinSeriesLocator(ILibraryManager library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <inheritdoc />
    public string? FindSeriesFolder(IReadOnlyDictionary<string, string> seriesProviderIds)
    {
        ArgumentNullException.ThrowIfNull(seriesProviderIds);

        var ids = seriesProviderIds
            .Where(p => MatchProviders.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return null;
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            HasAnyProviderId = ids,
        };
        var folders = _library.GetItemList(query)
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return folders.Count == 1 ? folders[0] : null;
    }
}
