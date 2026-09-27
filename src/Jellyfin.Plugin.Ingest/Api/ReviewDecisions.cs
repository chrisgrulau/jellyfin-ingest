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
