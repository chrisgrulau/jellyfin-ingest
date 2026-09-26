using System.Collections.Generic;
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
}
