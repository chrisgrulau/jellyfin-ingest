using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// The outcome of approving a review's AI suggestions.
/// </summary>
public enum AiApprovalOutcome
{
    /// <summary>Approved: the release is filed as suggested on the next sweep.</summary>
    Approved = 0,

    /// <summary>There is no such review.</summary>
    NotFound,

    /// <summary>The review holds no AI suggestion.</summary>
    NoSuggestion,

    /// <summary>A decision for the release is already waiting for the next sweep.</summary>
    Pending,

    /// <summary>The suggestions changed since the page was drawn.</summary>
    Stale,
}

/// <summary>
/// A review's AI suggestions as a page showed them, for Approve all.
/// </summary>
/// <param name="Id">The review's id.</param>
/// <param name="Key">The suggestions' key the page showed (<see cref="AiApproval.KeyOf"/>).</param>
public sealed record ReviewApproval(string Id, string Key);

/// <summary>
/// Approving what the AI plugin decided a release is, for a watch folder that asks first: which reviews hold
/// suggestions, their key (so only what the page showed is approved), and what approving changes.
/// </summary>
public static class AiApproval
{
    /// <summary>
    /// The key of the suggestions a review shows: each suggested file with its title, season and episode. The page
    /// sends it back with an approval, so a suggestion that changed since the page was drawn isn't approved.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns>The key, or <c>null</c> when the review holds no suggestion.</returns>
    public static string? KeyOf(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var parts = review.Items.Where(i => i.Suggestion is not null).Select(i => i.Source + "=" + i.Suggestion!.Key()).ToList();
        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    /// <summary>
    /// Whether a review waits for nothing but approving AI suggestions: every file is held only for that (no copy
    /// already on the server, no other problem), no decision is waiting, and it isn't held after an undo or restore.
    /// Only such reviews are taken by Approve all.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns><c>true</c> if approving is all it needs.</returns>
    public static bool IsOnlyAi(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return review.Items.Count > 0
            && review.Request == ReviewRequest.None
            && !review.Held
            && review.Items.All(i => i.ApprovalOnly && i.Suggestion is not null && i.Existing.Length == 0);
    }

    /// <summary>
    /// Whether a review's suggestions can be approved as the page showed them.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <param name="shownKey">The key the page showed.</param>
    /// <returns><see cref="AiApprovalOutcome.Approved"/> when they can; otherwise why not.</returns>
    public static AiApprovalOutcome Check(PendingReview review, string? shownKey)
    {
        ArgumentNullException.ThrowIfNull(review);
        var key = KeyOf(review);
        if (key is null)
        {
            return AiApprovalOutcome.NoSuggestion;
        }

        if (review.Request != ReviewRequest.None)
        {
            return AiApprovalOutcome.Pending;
        }

        return string.Equals(key, shownKey, StringComparison.Ordinal) ? AiApprovalOutcome.Approved : AiApprovalOutcome.Stale;
    }

    /// <summary>
    /// The review with its suggestions approved and a request to plan it again (replacing what's on the server, if asked).
    /// </summary>
    /// <param name="review">The review.</param>
    /// <param name="replace">Whether to replace the copies already on the server that hold it back.</param>
    /// <returns>The changed review.</returns>
    public static PendingReview Approve(PendingReview review, bool replace)
    {
        ArgumentNullException.ThrowIfNull(review);
        var approved = new Dictionary<string, AiSuggestion>(review.Approved, StringComparer.Ordinal);
        foreach (var item in review.Items.Where(i => i.Suggestion is not null))
        {
            approved[item.Source] = item.Suggestion!;
        }

        return review with
        {
            Approved = approved,
            Request = replace ? ReviewRequest.Replace : ReviewRequest.Retry,
            RequestVersion = review.RequestVersion + 1,
        };
    }

    /// <summary>
    /// What the review's suggestions are, in words (for the decision recorded in Recent activity).
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns>E.g. <c>Harbour Lights (1998)</c>, or <c>Harbour Watch (2004) S02E03 'Glass Harbour' and 1 more</c>.</returns>
    public static string Describe(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var all = review.Items.Where(i => i.Suggestion is not null).Select(i => i.Suggestion!.Describe()).Distinct(StringComparer.Ordinal).ToList();
        return all.Count switch
        {
            0 => "nothing",
            1 => all[0],
            _ => all[0] + " and " + (all.Count - 1) + " more",
        };
    }
}
