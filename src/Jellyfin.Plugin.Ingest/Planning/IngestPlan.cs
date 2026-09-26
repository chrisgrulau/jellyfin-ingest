using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Ingest.Identification;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// Why a planned operation exists.
/// </summary>
public enum OperationKind
{
    /// <summary>A main video filed into the library.</summary>
    Video = 0,

    /// <summary>A subtitle sidecar filed next to its video.</summary>
    Subtitle,

    /// <summary>An extra (featurette, trailer …) filed under its movie or series.</summary>
    Extra,

    /// <summary>Release clutter or a sample moved to quarantine.</summary>
    Quarantine,
}

/// <summary>
/// One file operation. Sources and destinations are absolute paths.
/// </summary>
/// <param name="Kind">What the operation is for.</param>
/// <param name="Source">Current path.</param>
/// <param name="Destination">Target path; never overwritten.</param>
public sealed record PlannedOperation(OperationKind Kind, string Source, string Destination);

/// <summary>
/// Why a release that couldn't be planned may succeed later without anyone doing anything.
/// </summary>
public enum RetryKind
{
    /// <summary>Only a person can resolve it.</summary>
    None = 0,

    /// <summary>A library folder isn't there (an unmounted share): tried again until it is.</summary>
    FolderUnavailable,

    /// <summary>
    /// No metadata provider returned anything. Jellyfin reports a provider outage the same way as an unknown title,
    /// so it is tried again a few times before being left for review.
    /// </summary>
    NothingFound,
}

/// <summary>
/// Something a person has to decide before the release can be filed.
/// </summary>
/// <param name="Source">The file concerned (absolute path).</param>
/// <param name="Reason">Why it can't be filed automatically.</param>
public sealed record ReviewItem(string Source, string Reason)
{
    /// <summary>Gets the titles identification considered, best first (empty when identification isn't the problem).</summary>
    public IReadOnlyList<ScoredCandidate> Candidates { get; init; } = [];

    /// <summary>Gets whether (and why) the problem may clear up by itself, so the release is tried again automatically.</summary>
    public RetryKind Retry { get; init; }
}

/// <summary>
/// Everything that will happen to one dropped release. A release is all-or-nothing: if anything needs review,
/// <see cref="Operations"/> is empty and the release is left exactly as it was dropped.
/// </summary>
public sealed record IngestPlan
{
    /// <summary>Gets the release's name (its top-level file or folder in the watch folder).</summary>
    public required string ReleaseName { get; init; }

    /// <summary>Gets the operations to perform, in order.</summary>
    public IReadOnlyList<PlannedOperation> Operations { get; init; } = [];

    /// <summary>
    /// Gets the folders the operations may write into (the film or show folders and the dated quarantine folder). The
    /// executor refuses any operation whose destination falls outside them.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// Gets folders that must already exist when the plan is executed (the library folder, or an existing show's folder).
    /// They are never created: a missing one means an offline share, and creating it would write to the local disk
    /// under the mount point.
    /// </summary>
    public IReadOnlyList<string> RequiredFolders { get; init; } = [];

    /// <summary>Gets notes for the activity panel (for example that a close match was settled by the AI plugin, and why).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Gets the items that need a decision; when non-empty nothing is moved.</summary>
    public IReadOnlyList<ReviewItem> Review { get; init; } = [];

    /// <summary>
    /// Gets why the plan may succeed if tried again later: <see cref="RetryKind.FolderUnavailable"/> wins over
    /// <see cref="RetryKind.NothingFound"/>; <see cref="RetryKind.None"/> when any item needs a person.
    /// </summary>
    public RetryKind Retry => Review.Count == 0 || Review.Any(r => r.Retry == RetryKind.None) ? RetryKind.None
        : Review.Any(r => r.Retry == RetryKind.FolderUnavailable) ? RetryKind.FolderUnavailable
        : RetryKind.NothingFound;

    /// <summary>Gets a value indicating whether the plan can be executed as is.</summary>
    public bool IsReady => Review.Count == 0
        && (Operations.Any(o => o.Kind is OperationKind.Video)
            || (WholeReleaseQuarantine && Operations.Count > 0 && Operations.All(o => o.Kind is OperationKind.Quarantine)));

    /// <summary>
    /// Gets a value indicating whether this plan quarantines a whole release on request (every operation a quarantine,
    /// no video filed). Only such a plan may run without a video.
    /// </summary>
    public bool WholeReleaseQuarantine { get; init; }
}
