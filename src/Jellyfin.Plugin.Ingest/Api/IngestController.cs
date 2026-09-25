using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Ingest.Api;

/// <summary>
/// Dashboard API for the plugin page: pending reviews, recent activity and review decisions. Administrators only.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Ingest")]
[Produces(MediaTypeNames.Application.Json)]
public class IngestController : ControllerBase
{
    private const int MaxSearchResults = 10;

    private readonly IngestStateStore _state;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestController"/> class.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="providerManager">Jellyfin provider manager.</param>
    public IngestController(IngestStateStore state, ILibraryManager libraryManager, IProviderManager providerManager)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _providerManager = providerManager ?? throw new ArgumentNullException(nameof(providerManager));
    }

    /// <summary>
    /// Gets pending reviews and recent activity.
    /// </summary>
    /// <param name="limit">Maximum activity entries to return.</param>
    /// <returns>The dashboard state.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IngestState> GetStatus([FromQuery] int limit = 100)
    {
        var s = _state.Snapshot();
        return new IngestState
        {
            Reviews = [.. s.Reviews.OrderByDescending(r => r.Time)],
            Activity = [.. s.Activity.Take(Math.Clamp(limit, 1, IngestStateStore.MaxActivity))],
        };
    }

    /// <summary>
    /// Searches the server's metadata providers for a title, for reviews where identification found nothing useful.
    /// </summary>
    /// <param name="name">Title to search for.</param>
    /// <param name="year">Year, if known.</param>
    /// <param name="series">Search for shows (otherwise films).</param>
    /// <param name="review">The review the search is for; its results are stored there so one can be chosen.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scored candidates, best first.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ScoredCandidate>>> Search([FromQuery, Required] string name, [FromQuery] int? year, [FromQuery] bool series, [FromQuery] string? review, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("A title is required.");
        }

        var lookup = new JellyfinMetadataLookup(_providerManager);
        var hits = series
            ? await lookup.SearchSeriesAsync(name.Trim(), year, cancellationToken).ConfigureAwait(false)
            : await lookup.SearchMoviesAsync(name.Trim(), year, cancellationToken).ConfigureAwait(false);
        List<ScoredCandidate> results = [.. MediaIdentifier.Merge(hits)
            .Select(c => new ScoredCandidate(c with { IsSeries = series }, MediaIdentifier.Score(name, year, c)))
            .OrderByDescending(c => c.Score)
            .Take(MaxSearchResults)];
        if (!string.IsNullOrEmpty(review))
        {
            _state.SetSearchResults(review, results);
        }

        return results;
    }

    /// <summary>
    /// Files a release as the given title, into the given library, on the next sweep.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="request">Which of the review's candidates (or stored search results) and which library.</param>
    /// <returns>No content.</returns>
    [HttpPost("Reviews/{id}/Choose")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Choose([FromRoute] string id, [FromBody] ChooseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var review = _state.GetReview(id);
        if (review is null)
        {
            return NotFound();
        }

        // Only a candidate the server produced for this review can be chosen, never one sent by the browser
        var candidate = ReviewChoice.Pick(review, request.List, request.Index);
        if (candidate is null)
        {
            return BadRequest("That candidate is no longer available; refresh and choose again.");
        }

        // A library the watch folder already files into keeps its configured folder; any other uses its first folder.
        var watch = IngestPlugin.Instance?.Configuration.WatchFolders.FirstOrDefault(w => w.Path == review.WatchFolder);
        var path = watch is null ? null
            : IngestService.DestinationsOf(watch).FirstOrDefault(d => string.Equals(d.LibraryId, request.LibraryId, StringComparison.OrdinalIgnoreCase))?.Path;
        var library = IngestService.Libraries(_libraryManager.GetVirtualFolders())
            .FirstOrDefault(l => string.Equals(l.Id, request.LibraryId, StringComparison.OrdinalIgnoreCase));
        var targets = library is null ? null : LibraryRouting.TargetsOf(library, path);
        var target = candidate.IsSeries ? targets?.Tv : targets?.Films;
        if (target is null)
        {
            return BadRequest(candidate.IsSeries ? "A show can only be filed into a Shows or mixed library." : "A film can only be filed into a Movies or mixed library.");
        }

        _state.RequestRetry(id, new ChosenMatch(candidate, target));
        RecordDecision(review, $"Chose {Describe(candidate)}, to be filed into {library!.Name}.");
        return NoContent();
    }

    /// <summary>
    /// Plans a release again on the next sweep, searching as usual (e.g. after renaming files or clearing a destination).
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Reviews/{id}/Retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Retry([FromRoute] string id)
    {
        var review = _state.GetReview(id);
        if (review is null || !_state.RequestRetry(id, null))
        {
            return NotFound();
        }

        RecordDecision(review, review.Chosen is null ? "Asked for a retry." : "Asked for a retry, clearing the earlier choice.");
        return NoContent();
    }

    /// <summary>
    /// Moves a whole release to quarantine on the next sweep (it is deleted after the retention period).
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Reviews/{id}/Quarantine")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Quarantine([FromRoute] string id)
    {
        var review = _state.GetReview(id);
        if (review is null || !_state.RequestQuarantine(id))
        {
            return NotFound();
        }

        RecordDecision(review, "Asked to quarantine the whole release.");
        return NoContent();
    }

    private static string Describe(MetadataCandidate c)
        => c.Year is { } y ? $"{c.Name} ({y})" : c.Name;

    private void RecordDecision(PendingReview review, string summary)
    {
        var who = User.Identity?.Name;
        _state.Record(new ActivityEntry
        {
            Time = DateTimeOffset.UtcNow,
            Status = ActivityStatus.Decision,
            Release = review.Release,
            WatchFolder = review.WatchFolder,
            Summary = string.IsNullOrEmpty(who) ? summary : $"{who}: {summary}",
        });
    }
}

/// <summary>
/// Body of <see cref="IngestController.Choose"/>.
/// </summary>
public sealed record ChooseRequest
{
    /// <summary>Gets which list the choice is from: <c>suggested</c> (the review's candidates) or <c>search</c>.</summary>
    public required string List { get; init; }

    /// <summary>Gets the position of the chosen candidate in that list.</summary>
    public required int Index { get; init; }

    /// <summary>Gets the id of the library to file into.</summary>
    public required string LibraryId { get; init; }
}
