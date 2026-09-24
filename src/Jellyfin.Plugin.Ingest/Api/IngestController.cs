using System;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.Ingest.Service;
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
    private readonly IngestStateStore _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestController"/> class.
    /// </summary>
    /// <param name="state">Reviews and activity.</param>
    public IngestController(IngestStateStore state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
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
    /// Files a release as one of its candidate titles on the next sweep.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="candidate">Index into the review's candidates.</param>
    /// <returns>No content.</returns>
    [HttpPost("Reviews/{id}/Choose/{candidate:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Choose([FromRoute] string id, [FromRoute] int candidate)
    {
        var review = _state.GetReview(id);
        if (review is null || candidate < 0 || candidate >= review.Candidates.Count)
        {
            return NotFound();
        }

        _state.RequestRetry(id, review.Candidates[candidate].Candidate);
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
        => _state.RequestRetry(id, null) ? NoContent() : NotFound();

    /// <summary>
    /// Moves a whole release to quarantine on the next sweep (it is deleted after the retention period).
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Reviews/{id}/Quarantine")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Quarantine([FromRoute] string id)
        => _state.RequestQuarantine(id) ? NoContent() : NotFound();
}
