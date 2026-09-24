using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// What's already in the destination library. New episodes of a show you already have should land in that show,
/// so existing titles are preferred when a name is otherwise ambiguous (e.g. a series and its same-named revival).
/// </summary>
public interface ILibraryIndex
{
    /// <summary>Series already in the library whose names resemble <paramref name="name"/>.</summary>
    /// <param name="name">Series name from the release.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching library series with their provider ids.</returns>
    Task<IReadOnlyList<MetadataCandidate>> FindSeriesAsync(string name, CancellationToken cancellationToken);

    /// <summary>Movies already in the library whose titles resemble <paramref name="name"/>.</summary>
    /// <param name="name">Movie title from the release.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching library movies with their provider ids.</returns>
    Task<IReadOnlyList<MetadataCandidate>> FindMoviesAsync(string name, CancellationToken cancellationToken);
}
