using System;
using Jellyfin.Plugin.Ingest.Identification;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Resolves a choice made on the review screen to a candidate the server itself produced.
/// </summary>
public static class ReviewChoice
{
    /// <summary>The review's own candidates (from identification).</summary>
    public const string Suggested = "suggested";

    /// <summary>The results of the last title search made for the review.</summary>
    public const string Search = "search";

    /// <summary>
    /// Picks a candidate by list and position. The candidate's provider ids are re-checked, so even a tampered state
    /// file can't smuggle an unsafe id into a folder name.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <param name="list"><see cref="Suggested"/> or <see cref="Search"/>.</param>
    /// <param name="index">Position in that list.</param>
    /// <returns>The candidate, or <c>null</c> if the list or position doesn't exist.</returns>
    public static MetadataCandidate? Pick(PendingReview review, string? list, int index)
    {
        ArgumentNullException.ThrowIfNull(review);

        var candidates = list switch
        {
            Suggested => review.Candidates,
            Search => review.SearchResults,
            _ => null,
        };
        if (candidates is null || index < 0 || index >= candidates.Count)
        {
            return null;
        }

        var c = candidates[index].Candidate;
        return c with { ProviderIds = ProviderIdRules.Clean(c.ProviderIds) };
    }
}
