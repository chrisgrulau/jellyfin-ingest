using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Parsing;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// A tie-breaker's answer.
/// </summary>
/// <param name="Index">The chosen option's position, or <c>null</c> if none was chosen (none fits, or no answer).</param>
/// <param name="Note">Why, in words for the activity panel.</param>
/// <param name="By">Who decided (for example the model), if anyone did.</param>
public sealed record TiebreakPick(int? Index, string Note, string? By);

/// <summary>
/// Settles a match identification couldn't decide on its own (for example with the AI plugin). It only ever picks one
/// of the options it is given, or none.
/// </summary>
public interface ITiebreaker
{
    /// <summary>
    /// Picks one of the options.
    /// </summary>
    /// <param name="release">The parsed release name.</param>
    /// <param name="fileName">The video's file name (no folders).</param>
    /// <param name="options">The candidates, best first.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pick; never throws for an unavailable or failed tie-breaker.</returns>
    Task<TiebreakPick> PickAsync(ParsedRelease release, string fileName, IReadOnlyList<ScoredCandidate> options, CancellationToken cancellationToken);
}
