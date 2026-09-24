using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Ingest.Naming;
using Jellyfin.Plugin.Ingest.Parsing;
using Jellyfin.Plugin.Ingest.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// <see cref="IExistingMedia"/> backed by Jellyfin's library (by TheTVDB / TMDb / IMDb id, across every library) plus a
/// look at the destination folder on disk, which also catches files filed moments ago and not yet scanned.
/// </summary>
public sealed class JellyfinExistingMedia : IExistingMedia
{
    private static readonly string[] SeriesProviders = ["Tvdb", "Tmdb"];
    private static readonly string[] MovieProviders = ["Tmdb", "Imdb"];

    // One level only, skipping symbolic links and unreadable entries
    private static readonly EnumerationOptions ShallowWalk = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinExistingMedia"/> class.
    /// </summary>
    /// <param name="library">Jellyfin's library manager.</param>
    public JellyfinExistingMedia(ILibraryManager library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <inheritdoc />
    public string? FindSeriesFolder(IReadOnlyDictionary<string, string> seriesProviderIds)
    {
        var folders = Find(BaseItemKind.Series, seriesProviderIds, SeriesProviders)
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return folders.Count == 1 ? folders[0] : null;
    }

    /// <inheritdoc />
    public string? FindEpisode(IReadOnlyDictionary<string, string> seriesProviderIds, string seriesFolder, int season, int episode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesFolder);

        // On disk first, in every season folder of the show: covers files filed moments ago that the library hasn't
        // scanned yet, and shows that use "Season 1", "S01" or "Specials"
        if (ExistingFiles.FindEpisode(seriesFolder, season, episode, Subdirectories, Files) is { } onDisk)
        {
            return onDisk;
        }

        var series = Find(BaseItemKind.Series, seriesProviderIds, SeriesProviders).Select(s => s.Id).ToArray();
        if (series.Length == 0)
        {
            return null;
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            AncestorIds = series,
        };
        return _library.GetItemList(query)
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber == season && e.IndexNumber is { } n && n <= episode && episode <= (e.IndexNumberEnd ?? n))
            .Select(e => e.Path)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    /// <inheritdoc />
    public string? FindMovie(IReadOnlyDictionary<string, string> movieProviderIds, string? edition, string plannedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plannedPath);

        // Same name with another extension in the planned folder (not yet scanned, or a different container)
        var folder = Path.GetDirectoryName(plannedPath);
        var stem = Path.GetFileNameWithoutExtension(plannedPath);
        if (folder is not null && Directory.Exists(folder))
        {
            var sameName = Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                    && ReleaseClassifier.Classify(new ReleaseFile(Path.GetFileName(f), long.MaxValue)) == FileRole.Video);
            if (sameName is not null)
            {
                return sameName;
            }
        }

        // A different edition of a film you have is a second version, not a duplicate
        return Find(BaseItemKind.Movie, movieProviderIds, MovieProviders)
            .Select(m => m.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .FirstOrDefault(p => SameEdition(p, edition));
    }

    /// <inheritdoc />
    public string? FindSeasonFolder(string seriesFolder, int season)
        => ExistingFiles.FindSeasonFolder(seriesFolder, season, Subdirectories, Files);

    // Same edition when the existing file's edition can't be told from its name either: better a review than a duplicate
    private static bool SameEdition(string path, string? edition)
    {
        var (known, existing) = ExistingFiles.MovieEdition(path);
        return !known || string.Equals(existing, edition, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Subdirectories(string folder)
        => Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, "*", ShallowWalk) : [];

    private static IEnumerable<string> Files(string folder)
        => Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", ShallowWalk) : [];

    private List<BaseItem> Find(BaseItemKind kind, IReadOnlyDictionary<string, string> providerIds, string[] providers)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        var ids = providerIds
            .Where(p => providers.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return [];
        }

        return [.. _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            IsVirtualItem = false,
            HasAnyProviderId = ids,
        })];
    }
}
