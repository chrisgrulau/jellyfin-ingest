using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Ingest.Service;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Ingest.Api;

/// <summary>
/// The review decisions that replace what's on the server, kept apart from <see cref="IngestController"/> (which needs
/// Jellyfin's services) so their checks are unit-tested.
/// </summary>
internal static class ReviewDecisions
{
    /// <summary>
    /// See <see cref="IngestController.Replace"/>.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="id">Review id.</param>
    /// <param name="request">The copies and the title the page showed.</param>
    /// <returns>No content, or why not.</returns>
    public static ActionResult Replace(IngestStateStore state, string id, ReplaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var review = state.GetReview(id);
        if (review is null)
        {
            return new NotFoundResult();
        }

        // Only a release held back by copies already on the server, and only the copies the page showed
        if (review.Items.Count == 0 || review.Items.Any(i => i.Existing.Length == 0))
        {
            return new BadRequestObjectResult("Only a release held back because it is already on the server can replace what's there.");
        }

        if (!string.Equals(ExistingOf(review), request.Existing, StringComparison.Ordinal))
        {
            return new ConflictObjectResult("What's on the server changed since the page was drawn; look again before replacing.");
        }

        // The copies were found for the title in use when the page was drawn; a title chosen since replaces nothing
        // until its own copies have been looked for and shown
        if (ReviewChoice.StaleReplace(review, request.Key) is { } stale)
        {
            return new ConflictObjectResult(stale);
        }

        if (!state.RequestReplace(id))
        {
            return new NotFoundResult();
        }

        Record(state, review, "Asked to replace the copies already on the server (they go to quarantine).");
        return new NoContentResult();
    }

    /// <summary>
    /// See <see cref="IngestController.Approve"/>.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="id">Review id.</param>
    /// <param name="request">The suggestions the page showed, and whether to replace what's on the server.</param>
    /// <returns>No content, or why not.</returns>
    public static ActionResult Approve(IngestStateStore state, string id, ApproveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var review = state.GetReview(id);
        if (review is null)
        {
            return new NotFoundResult();
        }

        if (request.Replace)
        {
            // As for Replace: only a release held back by copies already on the server, and only the copies shown
            if (review.Items.Count == 0 || review.Items.Any(i => i.Existing.Length == 0))
            {
                return new BadRequestObjectResult("Only a release held back because it is already on the server can replace what's there.");
            }

            if (!string.Equals(ExistingOf(review), request.Existing, StringComparison.Ordinal))
            {
                return new ConflictObjectResult("What's on the server changed since the page was drawn; look again before replacing.");
            }
        }

        var outcome = state.RequestApprove(id, request.Key, request.Replace);
        switch (outcome)
        {
            case AiApprovalOutcome.NotFound:
                return new NotFoundResult();
            case AiApprovalOutcome.NoSuggestion:
                return new BadRequestObjectResult("This release has no AI suggestion to approve.");
            case AiApprovalOutcome.Pending:
                return new ConflictObjectResult("A decision for this release is already waiting for the next sweep; look again after it.");
            case AiApprovalOutcome.Stale:
                return new ConflictObjectResult("The AI's suggestion for this release changed since the page was drawn; look again before approving.");
        }

        Record(state, review, "Approved AI suggestion: " + AiApproval.Describe(review) + (request.Replace ? ", replacing the copies already on the server (they go to quarantine)." : "."));
        return new NoContentResult();
    }

    /// <summary>
    /// Carries out Approve all (queued by <see cref="IngestController.ApproveAll"/>, run by the sweep): approves each
    /// review that waits only for its AI suggestion, as the page showed it, and records one decision for them all.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="approvals">The reviews and the keys the page showed.</param>
    /// <returns>What was done, for the log.</returns>
    public static string ApproveAll(IngestStateStore state, IReadOnlyList<ReviewApproval> approvals)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(approvals);
        var done = state.ApproveAll(approvals);
        var skipped = approvals.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() - done.Count;
        var summary = string.Create(CultureInfo.InvariantCulture, $"Approved {done.Count} AI suggestion{(done.Count == 1 ? string.Empty : "s")} (Approve all); {(done.Count == 1 ? "it is" : "they are")} filed on the next sweep.")
            + (skipped > 0 ? string.Create(CultureInfo.InvariantCulture, $" {skipped} skipped: changed since the page was drawn, or no longer waiting only for approval.") : string.Empty);
        state.Record(new ActivityEntry
        {
            Time = DateTimeOffset.UtcNow,
            Status = ActivityStatus.Decision,
            Release = "Needs review",
            Summary = summary,
            Details = [.. done.Select(r => r.Release + ": Approved AI suggestion: " + AiApproval.Describe(r))],
        });
        return summary;
    }

    /// <summary>
    /// See <see cref="IngestController.Files"/>.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="id">Review id.</param>
    /// <param name="request">The decisions, and the title the page showed.</param>
    /// <returns>No content, or why not.</returns>
    public static ActionResult Files(IngestStateStore state, string id, FilesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var review = state.GetReview(id);
        if (review is null)
        {
            return new NotFoundResult();
        }

        var decisions = new Dictionary<string, FileDecision?>(StringComparer.Ordinal);
        var episodes = new Dictionary<string, EpisodeNumber?>(StringComparer.Ordinal);
        foreach (var choice in request.Decisions ?? [])
        {
            var item = review.Items.FirstOrDefault(i => string.Equals(i.Source, choice.Source, StringComparison.Ordinal));
            if (item is null)
            {
                return new BadRequestObjectResult("That file isn't waiting in this review.");
            }

            // A season and episode (ING-30): both or neither, for a video, in range
            if (EpisodeNumber.Read(choice.Season, choice.Episode, item, choice.Action, out var number) is { } problem)
            {
                return new BadRequestObjectResult(problem);
            }

            episodes[item.Source] = number;

            switch (choice.Action)
            {
                case "replace":
                    if (item.Existing.Length == 0)
                    {
                        return new BadRequestObjectResult("Only a file that is already on the server can replace what's there.");
                    }

                    if (!string.Equals(item.Existing, choice.Existing, StringComparison.Ordinal))
                    {
                        return new ConflictObjectResult("What's on the server changed since the page was drawn; look again before replacing.");
                    }

                    if (ReviewChoice.StaleReplace(review, request.Key) is { } stale)
                    {
                        return new ConflictObjectResult(stale);
                    }

                    decisions[item.Source] = FileDecision.Replace;
                    break;
                case "quarantine":
                    decisions[item.Source] = FileDecision.Quarantine;
                    break;
                case "later":
                    decisions[item.Source] = null;
                    break;
                default:
                    return new BadRequestObjectResult("Unknown choice.");
            }
        }

        // Two videos given the same episode would only wait again
        var repeated = episodes.Values.OfType<EpisodeNumber>().GroupBy(n => n).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (repeated is not null)
        {
            return new BadRequestObjectResult(string.Create(CultureInfo.InvariantCulture, $"Two files are given season {repeated.Season}, episode {repeated.Episode}; each file needs its own episode."));
        }

        if (!state.RequestFiles(id, decisions, episodes))
        {
            return new NotFoundResult();
        }

        var replace = decisions.Values.Count(d => d == FileDecision.Replace);
        var quarantine = decisions.Values.Count(d => d == FileDecision.Quarantine);
        var numbered = episodes.Values.Count(n => n is not null);
        Record(state, review, string.Create(CultureInfo.InvariantCulture, $"Chose file by file: {replace} to replace what's on the server, {quarantine} to quarantine, {numbered} given a season and episode."));
        return new NoContentResult();
    }

    /// <summary>
    /// The copies holding a review back, as the page sends them back: every item's, in order, one per line.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns>The copies.</returns>
    public static string ExistingOf(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return string.Join('\n', review.Items.Select(i => i.Existing));
    }

    /// <summary>
    /// Records a review decision in Recent activity. Who decided isn't stored: only administrators can decide, and the
    /// state file shouldn't collect user names.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="review">The review.</param>
    /// <param name="summary">What was decided.</param>
    public static void Record(IngestStateStore state, PendingReview review, string summary)
        => state.Record(new ActivityEntry
        {
            Time = DateTimeOffset.UtcNow,
            Status = ActivityStatus.Decision,
            Release = review.Release,
            WatchFolder = review.WatchFolder,
            Summary = summary,
        });
}
