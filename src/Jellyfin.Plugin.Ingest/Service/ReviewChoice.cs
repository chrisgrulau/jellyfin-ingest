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
    /// The title a review is using: the one chosen in review, else the one its last plan matched every file to.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns>The title, or <c>null</c> when none is in use yet (identification is what it waits for).</returns>
    public static MetadataCandidate? CurrentMatch(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return review.Chosen?.Candidate ?? review.Matched;
    }

    /// <summary>
    /// The key (<see cref="KeyOf"/>) of the title a review is using, as the page shows it and sends it back with a
    /// replace, so a replace asked for on a page drawn before the title changed is refused.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns>The key, or <c>null</c> when no title is in use.</returns>
    public static string? CurrentMatchKey(PendingReview review)
        => CurrentMatch(review) is { } c ? KeyOf(c) : null;

    /// <summary>
    /// Whether the review's files were last planned with the title it is using now: a title chosen since hasn't been
    /// checked against what's on the server yet, so the copies listed belong to the previous one.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns><c>true</c> when the copies listed were found for the current title.</returns>
    public static bool IsAssessed(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return review.Chosen is null || SameTitle(review.Chosen.Candidate, review.Matched);
    }

    /// <summary>
    /// Whether two titles are the same (same key; provider ids are compared as stored and as cleaned).
    /// </summary>
    /// <param name="a">One title.</param>
    /// <param name="b">The other.</param>
    /// <returns>Whether they are the same title.</returns>
    public static bool SameTitle(MetadataCandidate? a, MetadataCandidate? b)
        => a is not null && b is not null
            && (string.Equals(KeyOf(a), KeyOf(b), StringComparison.Ordinal)
                || string.Equals(KeyOf(a with { ProviderIds = ProviderIdRules.Clean(a.ProviderIds) }), KeyOf(b with { ProviderIds = ProviderIdRules.Clean(b.ProviderIds) }), StringComparison.Ordinal));

    /// <summary>
    /// Why replacing the copies on the server must be refused for a page that showed the given title key: the review
    /// now uses another title, or a title chosen since hasn't been checked against the server yet.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <param name="shownKey">The <see cref="CurrentMatchKey"/> the page showed (<c>null</c> or empty when it showed none).</param>
    /// <returns>The refusal, or <c>null</c> when the replace can go ahead.</returns>
    public static string? StaleReplace(PendingReview review, string? shownKey)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!IsAssessed(review))
        {
            return "A different title was chosen and Ingest hasn't checked the server for it yet; refresh after the next sweep before replacing anything.";
        }

        var current = CurrentMatchKey(review);
        return string.Equals(current ?? string.Empty, shownKey ?? string.Empty, StringComparison.Ordinal)
            ? null
            : "The title this release is matched to changed since the page was drawn; look again before replacing.";
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
