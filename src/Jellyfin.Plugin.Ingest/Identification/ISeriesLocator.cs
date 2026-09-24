using System.Collections.Generic;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// Finds where a series already lives on the server, so new episodes join it instead of starting a second copy.
/// </summary>
public interface ISeriesLocator
{
    /// <summary>
    /// Finds the folder of a series that is already in a library.
    /// </summary>
    /// <param name="seriesProviderIds">The identified series' provider ids (<c>Tvdb</c>, <c>Tmdb</c> …).</param>
    /// <returns>The series folder, or <c>null</c> if the series isn't in any library (or is in more than one).</returns>
    string? FindSeriesFolder(IReadOnlyDictionary<string, string> seriesProviderIds);
}
