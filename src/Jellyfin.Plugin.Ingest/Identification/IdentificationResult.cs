using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Ingest.Naming;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// Outcome of identifying a release.
/// </summary>
public enum IdentificationStatus
{
    /// <summary>Confidently matched; safe to file automatically.</summary>
    Identified = 0,

    /// <summary>Plausible candidates exist but none is clearly right; a person should decide.</summary>
    NeedsReview,

    /// <summary>Nothing usable was found.</summary>
    NotFound,
}

/// <summary>
/// A scored candidate, kept for review screens and logs.
/// </summary>
/// <param name="Candidate">The provider hit (merged across providers).</param>
/// <param name="Score">Confidence in [0, 1].</param>
public sealed record ScoredCandidate(MetadataCandidate Candidate, double Score);

/// <summary>
/// The result of <see cref="MediaIdentifier"/>.
/// </summary>
public sealed record IdentificationResult
{
    /// <summary>Gets the outcome.</summary>
    public required IdentificationStatus Status { get; init; }

    /// <summary>Gets the identified movie, when the release is a film.</summary>
    public MovieIdentity? Movie { get; init; }

    /// <summary>Gets the identified episode, when the release is a TV episode.</summary>
    public EpisodeIdentity? Episode { get; init; }

    /// <summary>Gets the confidence of the chosen (or best) candidate.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets a short human-readable explanation.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets the ranked candidates that were considered.</summary>
    public IReadOnlyList<ScoredCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether every search came back empty (and nothing similar is in the library). An unknown
    /// title and a provider outage look the same, so this is worth trying again later.
    /// </summary>
    public bool NothingFound { get; init; }

    /// <summary>Gets who settled a close match (for example the AI plugin's model), when it wasn't the scores alone.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>
    /// Gets what the AI plugin decided on the way to this result (the title from close candidates, the episode by its
    /// title or from a transcript), so a watch folder that asks first can show it for approval; empty when the scores
    /// and the name decided everything.
    /// </summary>
    public IReadOnlyList<AiDecision> AiDecisions { get; init; } = [];
}

/// <summary>
/// What the AI plugin decided.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiDecisionKind>))]
public enum AiDecisionKind
{
    /// <summary>The title, from close candidates (the tie-breaker).</summary>
    Match = 0,

    /// <summary>The episode, from the season's episode titles (the name gives a title but no number).</summary>
    EpisodeByTitle,

    /// <summary>The episode, from a short transcript compared with the episode synopses.</summary>
    Transcript,
}

/// <summary>
/// One decision the AI plugin made while identifying a video: kept with a suggestion waiting for approval, so the review
/// card can say what it chose and why without asking it again.
/// </summary>
public sealed record AiDecision
{
    /// <summary>The most characters of the transcript kept to show (a snippet, never the whole transcript).</summary>
    public const int SnippetLength = 100;

    /// <summary>Gets what was decided.</summary>
    public required AiDecisionKind Kind { get; init; }

    /// <summary>Gets who decided, e.g. <c>AI (model)</c>.</summary>
    public required string By { get; init; }

    /// <summary>Gets what was picked, in words, e.g. <c>Harbour Lights (1998)</c> or <c>S02E03 'Glass Harbour'</c>.</summary>
    public string Picked { get; init; } = string.Empty;

    /// <summary>Gets the AI's short reason (one sentence, already trimmed for the activity panel).</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets how many candidates or episodes it chose among.</summary>
    public int Options { get; init; }

    /// <summary>
    /// Gets how closely the name matched what was picked (the candidate's score, or the episode title's similarity), in
    /// [0, 1]; <c>null</c> for a pick from a transcript, where the name says nothing. The AI plugin gives no confidence
    /// of its own.
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>Gets how many characters of transcript were heard, for a pick from a transcript.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TranscriptCharacters { get; init; }

    /// <summary>Gets where in the video the transcript starts, for a pick from a transcript (when known).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? TranscriptFrom { get; init; }

    /// <summary>Gets the first words of the transcript (at most <see cref="SnippetLength"/> characters), for the details.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranscriptSnippet { get; init; }

    /// <summary>
    /// The first words of a transcript, on one line, at most <see cref="SnippetLength"/> characters.
    /// </summary>
    /// <param name="transcript">The transcript.</param>
    /// <returns>The snippet.</returns>
    public static string Snippet(string transcript)
    {
        var t = (transcript ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return t.Length > SnippetLength ? t[..SnippetLength].TrimEnd() + "…" : t;
    }
}
