using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Ingest.Identification;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// What the AI plugin decided a video is, held for approval (a watch folder set to ask first): the title, and for an
/// episode its numbers. Approving files exactly this, without asking the AI again.
/// </summary>
public sealed record AiSuggestion
{
    /// <summary>Gets the title (film or show) the video is filed as.</summary>
    public required MetadataCandidate Title { get; init; }

    /// <summary>Gets the season, for an episode.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Season { get; init; }

    /// <summary>Gets the episode, for an episode.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Episode { get; init; }

    /// <summary>Gets the last episode, for a file holding several.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndingEpisode { get; init; }

    /// <summary>Gets the episode's title, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets what the AI decided on the way (and why), for the review card.</summary>
    public IReadOnlyList<AiDecision> Decisions { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the suggestion can be filed as it is: a film, or an episode with its numbers
    /// (an episode without them would need identifying again, and so asking again).
    /// </summary>
    [JsonIgnore]
    public bool IsComplete => Title is not null && (!Title.IsSeries || (Season is not null && Episode is not null));

    /// <summary>
    /// Makes the suggestion for an identification the AI plugin decided (part of).
    /// </summary>
    /// <param name="result">An identification that succeeded with the AI's help.</param>
    /// <param name="title">The title it was matched to (see <see cref="IngestPlanner.MatchedTitle"/>).</param>
    /// <returns>The suggestion, or <c>null</c> when the AI decided nothing or no title is known.</returns>
    public static AiSuggestion? From(IdentificationResult result, MetadataCandidate? title)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != IdentificationStatus.Identified || result.AiDecisions.Count == 0 || title is null)
        {
            return null;
        }

        return new AiSuggestion
        {
            Title = title with { IsSeries = result.Episode is not null },
            Season = result.Episode?.Season,
            Episode = result.Episode?.Episode,
            EndingEpisode = result.Episode?.EndingEpisode,
            EpisodeTitle = result.Episode?.Title,
            Decisions = result.AiDecisions,
        };
    }

    /// <summary>
    /// The suggestion in words: <c>Harbour Lights (1998)</c>, or <c>Harbour Watch (2004) S02E03 'Glass Harbour'</c>.
    /// </summary>
    /// <returns>The words.</returns>
    public string Describe()
    {
        var text = Title.Year is { } y ? string.Create(CultureInfo.InvariantCulture, $"{Title.Name} ({y})") : Title.Name;
        if (Season is { } s && Episode is { } e)
        {
            text += " " + Naming.MediaNamer.EpisodeCode(s, e, EndingEpisode);
            if (!string.IsNullOrWhiteSpace(EpisodeTitle))
            {
                text += " '" + EpisodeTitle + "'";
            }
        }

        return text;
    }

    /// <summary>
    /// The suggestion's identity as the page shows it (title key, season and episode), so an approval from a page drawn
    /// before the suggestion changed is refused.
    /// </summary>
    /// <returns>The key.</returns>
    public string Key()
        => Service.ReviewChoice.KeyOf(Title) + "|" + string.Create(CultureInfo.InvariantCulture, $"{Season}|{Episode}|{EndingEpisode}");

    /// <summary>
    /// The review item's reason for a video held only for approval.
    /// </summary>
    /// <returns>The reason.</returns>
    public string WaitingReason()
    {
        var by = Decisions.Select(d => d.By).FirstOrDefault(b => b.Length > 0) ?? "the AI plugin";
        return $"Suggested by {by}: {Describe()}. This watch folder asks before filing what the AI decided.";
    }
}
