using System;
using System.Linq;
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
    /// A candidate's identity as the page shows it: name, year and provider ids. The page sends it with a choice, so a
    /// list that changed in the meantime (another tab or administrator searched the same review) is noticed instead of
    /// filing whatever is now at that position. The page builds the same string (see <c>candidateKey</c> in configPage.html).
    /// </summary>
    /// <param name="c">The candidate.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(MetadataCandidate c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Name + "|" + (c.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty) + "|"
            + string.Join(',', c.ProviderIds.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
    }

    /// <summary>
    /// The key of the candidate at a position, as stored (before the provider ids are cleaned).
    /// </summary>
    /// <param name="review">The review.</param>
    /// <param name="list"><see cref="Suggested"/> or <see cref="Search"/>.</param>
    /// <param name="index">Position in that list.</param>
    /// <returns>The key, or <c>null</c> if the list or position doesn't exist.</returns>
    public static string? KeyAt(PendingReview review, string? list, int index)
    {
        ArgumentNullException.ThrowIfNull(review);
        var candidates = list switch { Suggested => review.Candidates, Search => review.SearchResults, _ => null };
        return candidates is null || index < 0 || index >= candidates.Count ? null : KeyOf(candidates[index].Candidate);
    }

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
