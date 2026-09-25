using System.Collections.Generic;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// What's already on the server: used so new episodes join their show, and so a copy of something already in a
/// library is never filed twice.
/// </summary>
public interface IExistingMedia
{
    /// <summary>
    /// Finds the folder of a series that is already in a library.
    /// </summary>
    /// <param name="seriesProviderIds">The identified series' provider ids (<c>Tvdb</c>, <c>Tmdb</c> …).</param>
    /// <returns>The series folder, or <c>null</c> if the series isn't in any library (or is in more than one).</returns>
    string? FindSeriesFolder(IReadOnlyDictionary<string, string> seriesProviderIds);

    /// <summary>
    /// Finds an episode that is already on the server.
    /// </summary>
    /// <param name="seriesProviderIds">The series' provider ids.</param>
    /// <param name="seriesFolder">The folder the episode would be filed under (checked on disk too, for files not yet scanned).</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>The existing file, or <c>null</c>.</returns>
    string? FindEpisode(IReadOnlyDictionary<string, string> seriesProviderIds, string seriesFolder, int season, int episode);

    /// <summary>
    /// Finds a film that is already on the server (the same edition, when the new copy names one).
    /// </summary>
    /// <param name="movieProviderIds">The film's provider ids (<c>Tmdb</c>, <c>Imdb</c>).</param>
    /// <param name="edition">The new copy's edition label (e.g. <c>Director's Cut</c>), if any.</param>
    /// <param name="plannedPath">Where the new copy would be filed (its folder is checked on disk too).</param>
    /// <returns>The existing file, or <c>null</c>.</returns>
    string? FindMovie(IReadOnlyDictionary<string, string> movieProviderIds, string? edition, string plannedPath);

    /// <summary>
    /// Finds the folder an existing show already uses for a season (it may be named <c>Season 1</c>, <c>S01</c> or
    /// <c>Specials</c> rather than the standard <c>Season 01</c>).
    /// </summary>
    /// <param name="seriesFolder">The show's folder.</param>
    /// <param name="season">Season number.</param>
    /// <returns>The folder, or <c>null</c> if the show has none for that season yet.</returns>
    string? FindSeasonFolder(string seriesFolder, int season);
}
