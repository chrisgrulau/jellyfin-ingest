using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.Common.Storage;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// What happened to a release (or the quarantine), as shown in the activity panel.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActivityStatus>))]
public enum ActivityStatus
{
    /// <summary>Filed into the library.</summary>
    Filed = 0,

    /// <summary>Planned in dry-run mode; nothing moved.</summary>
    DryRun,

    /// <summary>Left in the watch folder for a person to decide.</summary>
    NeedsReview,

    /// <summary>Something went wrong while planning or moving.</summary>
    Failed,

    /// <summary>The whole release was moved to quarantine on request.</summary>
    Quarantined,

    /// <summary>Expired quarantine was deleted.</summary>
    Purged,

    /// <summary>A person made a review decision (chose a title, asked for a retry or quarantine).</summary>
    Decision,

    /// <summary>A filed release was undone by an administrator: its files went back to where they came from.</summary>
    Undone,

    /// <summary>Quarantined files were put back where they came from by an administrator.</summary>
    Restored,

    /// <summary>A review was dropped because its release (or its watch folder) is gone.</summary>
    ReviewRemoved,

    /// <summary>A filed release was moved to another library by an administrator.</summary>
    Moved,
}

/// <summary>
/// How a filing's library was decided.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FilingChoice>))]
public enum FilingChoice
{
    /// <summary>Ingest chose it: the watch folder's destinations, or the library of a copy being replaced.</summary>
    Automatic = 0,

    /// <summary>An administrator chose the library in review.</summary>
    Review,

    /// <summary>
    /// An administrator approved what the AI plugin decided the release is (a watch folder that asks first); the library
    /// was still chosen by Ingest (the watch folder's destinations, or where a replaced copy lives), so it can be moved
    /// like an automatic filing.
    /// </summary>
    AiApproved,
}

/// <summary>
/// What a person asked to happen to a release that is waiting for review.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewRequest>))]
public enum ReviewRequest
{
    /// <summary>Nothing yet.</summary>
    None = 0,

    /// <summary>Plan the release again on the next sweep (using the chosen title, if any).</summary>
    Retry,

    /// <summary>Move the whole release to quarantine on the next sweep.</summary>
    Quarantine,

    /// <summary>Plan the release again, replacing the copies already on the server (they go to quarantine).</summary>
    Replace,
}

/// <summary>
/// One entry in the activity panel.
/// </summary>
public sealed record ActivityEntry
{
    /// <summary>Gets when it happened.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Gets the outcome.</summary>
    public required ActivityStatus Status { get; init; }

    /// <summary>Gets the release name (or the quarantine folder, for purges).</summary>
    public required string Release { get; init; }

    /// <summary>Gets the watch folder concerned, if any.</summary>
    public string? WatchFolder { get; init; }

    /// <summary>Gets a one-line summary.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Gets detail lines (what went where, or why not).</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>
    /// Gets the id the filing's moves carry in the action log, so it can be undone; <c>null</c> for anything that
    /// isn't a filing (or one made before undo existed).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Run { get; init; }

    /// <summary>Gets when this filing was undone, if it was.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? UndoneAt { get; init; }

    /// <summary>
    /// Gets how many files of each kind a filing or quarantine moved (or would move, in dry run), for the page's
    /// icons; <c>null</c> for other entries and ones recorded before it existed (the page then counts the details).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActivityCounts? Counts { get; init; }

    /// <summary>
    /// Gets where a filing's videos went (absolute paths, named by Ingest), so the page can say what was filed
    /// (<c>Show S01E05</c>) without reading file names; <c>null</c> when there were none, or for older entries.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Videos { get; init; }

    /// <summary>
    /// Gets how a filing's library was decided; <c>null</c> for other entries and filings recorded before it existed
    /// (see <see cref="LibraryMove.IsAutomatic"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FilingChoice? ChosenBy { get; init; }

    /// <summary>
    /// Gets the run of the move of this filing's files to another library, set before the first file moves (so an undo
    /// can follow the files even after a crash part-way); <c>null</c> if it was never moved.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MoveRun { get; init; }

    /// <summary>Gets when this filing was moved to another library, once the move has succeeded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? MovedAt { get; init; }

    /// <summary>Gets the library this filing was moved to (its name), once the move has succeeded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MovedTo { get; init; }
}

/// <summary>
/// How many files of each kind an activity entry moved.
/// </summary>
public sealed record ActivityCounts
{
    /// <summary>Gets the main videos filed.</summary>
    public int Videos { get; init; }

    /// <summary>Gets the subtitle files filed.</summary>
    public int Subtitles { get; init; }

    /// <summary>Gets the extras filed.</summary>
    public int Extras { get; init; }

    /// <summary>Gets the release clutter (and whole releases' files) moved to quarantine.</summary>
    public int Clutter { get; init; }

    /// <summary>Gets the copies already on the server that were replaced (moved to quarantine).</summary>
    public int Replaced { get; init; }

    /// <summary>Gets the files quarantined because a person chose not to file them.</summary>
    public int SetAside { get; init; }
}

/// <summary>
/// A release waiting in a watch folder for a person to decide.
/// </summary>
public sealed record PendingReview
{
    /// <summary>Gets the stable id (see <see cref="IngestStateStore.ReviewId"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Gets the watch folder.</summary>
    public required string WatchFolder { get; init; }

    /// <summary>Gets the release name inside the watch folder.</summary>
    public required string Release { get; init; }

    /// <summary>Gets when the release was last planned.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Gets why it wasn't filed, per file.</summary>
    public IReadOnlyList<PendingReviewItem> Items { get; init; } = [];

    /// <summary>Gets the distinct titles considered across the release's files, best first.</summary>
    public IReadOnlyList<ScoredCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Gets the results of the last title search made for this review. Choices are made by position in this list or in
    /// <see cref="Candidates"/>, both produced by the server, never from a candidate sent by the browser.
    /// </summary>
    public IReadOnlyList<ScoredCandidate> SearchResults { get; init; } = [];

    /// <summary>Gets when the release will be tried again automatically (an offline library, or providers that found nothing), if it will.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>Gets the title and library chosen in review, if any; used instead of searching when the release is planned again.</summary>
    public ChosenMatch? Chosen { get; init; }

    /// <summary>
    /// Gets the title the last plan matched every waiting file to (the chosen one, or the one identification found when
    /// something else, such as a copy already on the server, held the release back); <c>null</c> when identification is
    /// what the release waits for, or its files matched different titles. <see cref="PendingReviewItem.Existing"/> was
    /// found for this title.
    /// </summary>
    public Identification.MetadataCandidate? Matched { get; init; }

    /// <summary>Gets the pending request.</summary>
    public ReviewRequest Request { get; init; }

    /// <summary>
    /// Gets the decisions made file by file (by file, relative to the watch folder), kept when the release is planned
    /// again; files without one wait as before.
    /// </summary>
    public IReadOnlyDictionary<string, FileDecision> FileDecisions { get; init; } = new Dictionary<string, FileDecision>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the season and episode numbers given in review (by video, relative to the watch folder), for videos whose
    /// numbering can't be read from their names (ING-30). Used instead of what the name says when planning again.
    /// </summary>
    public IReadOnlyDictionary<string, EpisodeNumber> FileEpisodes { get; init; } = new Dictionary<string, EpisodeNumber>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the AI suggestions approved in review (by video, relative to the watch folder), kept when the release is
    /// planned again: such a video is filed as the suggestion says, without asking the AI again.
    /// </summary>
    public IReadOnlyDictionary<string, AiSuggestion> Approved { get; init; } = new Dictionary<string, AiSuggestion>(StringComparer.Ordinal);

    /// <summary>
    /// Gets a counter that goes up with every request, so planning (which takes a while) only clears the request it
    /// started from and never one made while it was running.
    /// </summary>
    public int RequestVersion { get; init; }

    /// <summary>
    /// Gets a value indicating whether the release waits for a person even if it could now be filed by itself: it was
    /// undone, or restored from quarantine, by an administrator. The sweep doesn't plan it until a decision is made
    /// (choose, retry, file by file or quarantine); planning it again clears this.
    /// </summary>
    public bool Held { get; init; }
}

/// <summary>
/// What a person decided for one file of a release waiting for review.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileDecision>))]
public enum FileDecision
{
    /// <summary>No decision (decide later).</summary>
    None = 0,

    /// <summary>Replace the copies already on the server (they go to quarantine) with this file.</summary>
    Replace,

    /// <summary>Don't file this file: move it (and its subtitles) to quarantine, and file the rest.</summary>
    Quarantine,
}

/// <summary>
/// A season and episode number given to a video in review.
/// </summary>
/// <param name="Season">The season (0 for specials).</param>
/// <param name="Episode">The episode within the season.</param>
public sealed record EpisodeNumber(int Season, int Episode)
{
    /// <summary>The highest season accepted (shows numbered by year have seasons such as 2024).</summary>
    public const int MaxSeason = 2999;

    /// <summary>The highest episode accepted.</summary>
    public const int MaxEpisode = 9999;

    /// <summary>
    /// Gets a value indicating whether both numbers are in range.
    /// </summary>
    [JsonIgnore]
    public bool IsValid => Season is >= 0 and <= MaxSeason && Episode is >= 1 and <= MaxEpisode;

    /// <summary>
    /// Reads the season and episode sent for a file in review, checking them.
    /// </summary>
    /// <param name="season">The season sent, if any.</param>
    /// <param name="episode">The episode sent, if any.</param>
    /// <param name="item">The review item they are for.</param>
    /// <param name="action">The file's decision as sent (a file being quarantined can't be numbered).</param>
    /// <param name="number">The numbers, or <c>null</c> when none were given (which clears any given before).</param>
    /// <returns>What's wrong with them, or <c>null</c> when they can be used.</returns>
    public static string? Read(int? season, int? episode, PendingReviewItem item, string? action, out EpisodeNumber? number)
    {
        ArgumentNullException.ThrowIfNull(item);
        number = null;
        if (season is null && episode is null)
        {
            return null;
        }

        if (season is not { } s || episode is not { } e)
        {
            return "Give both a season and an episode number, or neither.";
        }

        if (!item.IsVideo)
        {
            return "Only a video can be given a season and episode.";
        }

        if (string.Equals(action, "quarantine", StringComparison.Ordinal))
        {
            return "A file being quarantined can't also be given a season and episode.";
        }

        var candidate = new EpisodeNumber(s, e);
        if (!candidate.IsValid)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The season must be 0 to {MaxSeason} (0 for specials) and the episode 1 to {MaxEpisode}.");
        }

        number = candidate;
        return null;
    }
}

/// <summary>
/// One file's review reason.
/// </summary>
/// <param name="Source">The file (path relative to the watch folder).</param>
/// <param name="Reason">Why it can't be filed automatically.</param>
public sealed record PendingReviewItem(string Source, string Reason)
{
    /// <summary>Gets the copies already on the server that hold this file back (one per line), or empty.</summary>
    public string Existing { get; init; } = string.Empty;

    /// <summary>Gets what the AI plugin decided this file is, waiting for approval (a watch folder that asks first).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiSuggestion? Suggestion { get; init; }

    /// <summary>Gets a value indicating whether the file waits only for its <see cref="Suggestion"/> to be approved.</summary>
    public bool ApprovalOnly { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file is a video, so it can be given a season and episode in review.
    /// </summary>
    public bool IsVideo => Planning.ReleaseClassifier.Classify(new Planning.ReleaseFile(Source ?? string.Empty, long.MaxValue)) == Planning.FileRole.Video;
}

/// <summary>
/// A release filed by copy or hard link, which stays in its watch folder.
/// </summary>
/// <param name="WatchFolder">The watch folder.</param>
/// <param name="Release">The release (folder or file name in it).</param>
/// <param name="Signature">Its files' names and sizes, so a changed release is filed again.</param>
public sealed record CopiedRelease(string WatchFolder, string Release, string Signature);

/// <summary>
/// Everything the dashboard shows, persisted as JSON in the plugin's data folder so it survives restarts.
/// </summary>
public sealed record IngestState
{
    /// <summary>Gets releases waiting for review.</summary>
    public IReadOnlyList<PendingReview> Reviews { get; init; } = [];

    /// <summary>Gets recent activity, newest first.</summary>
    public IReadOnlyList<ActivityEntry> Activity { get; init; } = [];

    /// <summary>Gets releases seen but not handled yet, soonest first (only in the Status API; never saved).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WaitingRelease>? Waiting { get; init; }

    /// <summary>Gets the release being identified or filed right now (only in the Status API; never saved).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkInProgress? Working { get; init; }

    /// <summary>Gets a value indicating whether Ingest is paused: sweeps file nothing until it is resumed.</summary>
    public bool Paused { get; init; }

    /// <summary>Gets the undos and restores asked for and not yet carried out (only in the Status API; never saved).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QueuedAction>? Queued { get; init; }

    /// <summary>Gets how the page shows each review, by review id (only in the Status API; never saved).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, Presentation.ItemView>? ReviewViews { get; init; }

    /// <summary>Gets how the page shows each waiting release, by its id (only in the Status API; never saved).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, Presentation.ItemView>? WaitingViews { get; init; }

    /// <summary>Gets how the page shows the release being worked on (only in the Status API; never saved).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Presentation.ItemView? WorkingView { get; init; }
}

/// <summary>
/// Thread-safe access to the <see cref="IngestState"/>, shared by the sweep service, the purge task and the API.
/// Every change is written straight to disk (write to a temporary file, then replace). The file is small (activity and
/// candidates are capped). If it can't be read or written, the dashboard carries on from memory: a damaged file is set
/// aside rather than overwritten, and an unreadable one is never replaced.
/// </summary>
public sealed partial class IngestStateStore
{
    /// <summary>How many activity entries are kept.</summary>
    public const int MaxActivity = 300;

    /// <summary>How many detail lines an activity entry keeps.</summary>
    public const int MaxDetails = 60;

    /// <summary>How many candidates a review keeps.</summary>
    public const int MaxCandidates = 8;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly Lock _lock = new();
    private StateFile? _state;
    private bool _memoryOnly;
    private string? _lastSaveError;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestStateStore"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the JSON state file.</param>
    /// <param name="logger">Logger for problems with the file (optional).</param>
    public IngestStateStore(string path, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// The stable id of a release in a watch folder.
    /// </summary>
    /// <param name="watchFolder">Watch folder path.</param>
    /// <param name="release">Release name.</param>
    /// <returns>A short hex id.</returns>
    public static string ReviewId(string watchFolder, string release)
    {
        ArgumentNullException.ThrowIfNull(watchFolder);
        ArgumentNullException.ThrowIfNull(release);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(watchFolder + "\0" + release)))[..16];
    }

    /// <summary>
    /// Returns a copy of the current state.
    /// </summary>
    /// <returns>The reviews and activity.</returns>
    public IngestState Snapshot()
    {
        lock (_lock)
        {
            var s = Load();
            return new IngestState { Reviews = [.. s.Reviews], Activity = [.. s.Activity], Paused = s.Paused };
        }
    }

    /// <summary>
    /// Gets or sets what else happens when an activity entry is recorded (copying it to Jellyfin's Activity log).
    /// </summary>
    public Action<ActivityEntry>? Recorded { get; set; }

    /// <summary>
    /// Records an activity entry (newest first, capped).
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void Record(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var s = Load();
            s.Activity.Insert(0, entry.Details.Count > MaxDetails
                ? entry with { Details = [.. entry.Details.Take(MaxDetails), $"… and {entry.Details.Count - MaxDetails} more"] }
                : entry);
            if (s.Activity.Count > MaxActivity)
            {
                s.Activity.RemoveRange(MaxActivity, s.Activity.Count - MaxActivity);
            }

            Save(s);
            Recorded?.Invoke(entry);
        }
    }

    /// <summary>
    /// Records an activity entry unless the latest entry for the same release says exactly the same (a restart plans
    /// every waiting release again; repeating identical dry-run results is noise). The outcome of a review decision is
    /// always recorded, so the person who decided sees what it led to.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns><c>true</c> if it was recorded.</returns>
    public bool RecordUnlessRepeat(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var last = Load().Activity.FirstOrDefault(a => a.Release == entry.Release && a.WatchFolder == entry.WatchFolder);
            if (last is not null && last.Status == entry.Status && last.Summary == entry.Summary && last.Details.SequenceEqual(entry.Details))
            {
                return false;
            }

            Record(entry);
            return true;
        }
    }

    /// <summary>
    /// Adds or refreshes a release's review. A title chosen earlier is kept. The request that was acted on is cleared;
    /// one made since (a newer <see cref="PendingReview.RequestVersion"/>) is kept, so it is acted on next.
    /// </summary>
    /// <param name="review">The review (its <see cref="PendingReview.Chosen"/>, <see cref="PendingReview.Request"/> and
    /// <see cref="PendingReview.RequestVersion"/> are ignored).</param>
    /// <param name="actedOnVersion">The request version the caller read before acting; <c>null</c> clears any request.</param>
    public void PutReview(PendingReview review, int? actedOnVersion = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        lock (_lock)
        {
            var s = Load();
            var i = s.Reviews.FindIndex(r => r.Id == review.Id);
            var existing = i >= 0 ? s.Reviews[i] : null;
            var newer = existing is not null && actedOnVersion is { } v && existing.RequestVersion != v;
            // Planning a chosen title searches for nothing, so the titles considered before stay on offer (to pick another)
            var candidates = (review.Candidates.Count == 0 && existing?.Chosen is not null ? existing.Candidates : review.Candidates).Take(MaxCandidates).ToList();
            var updated = review with
            {
                Candidates = candidates,
                Chosen = existing?.Chosen,
                FileDecisions = existing?.FileDecisions ?? new Dictionary<string, FileDecision>(StringComparer.Ordinal),
                FileEpisodes = existing?.FileEpisodes ?? new Dictionary<string, EpisodeNumber>(StringComparer.Ordinal),
                Approved = existing?.Approved ?? new Dictionary<string, AiSuggestion>(StringComparer.Ordinal),
                SearchResults = existing?.SearchResults ?? [],
                Request = newer ? existing!.Request : ReviewRequest.None,
                RequestVersion = existing?.RequestVersion ?? 0,
            };
            if (i >= 0)
            {
                s.Reviews[i] = updated;
            }
            else
            {
                s.Reviews.Add(updated);
            }

            Save(s);
        }
    }

    /// <summary>
    /// Finds a review.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <returns>The review, or <c>null</c>.</returns>
    public PendingReview? GetReview(string id)
    {
        lock (_lock)
        {
            return Load().Reviews.FirstOrDefault(r => r.Id == id);
        }
    }

    /// <summary>
    /// Asks for a release to be planned again, optionally as a chosen title.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="chosen">The chosen title and library, or <c>null</c> to keep searching (clears an earlier choice).</param>
    /// <returns><c>false</c> if there is no such review.</returns>
    public bool RequestRetry(string id, ChosenMatch? chosen)
        => Update(id, r => r with
        {
            Chosen = chosen,

            // "Clear choice and retry" starts over, file decisions included; choosing a title keeps them, except that
            // "replace what's on the server" was decided for the copies of the title in use, so choosing a different
            // title drops it (the new title's copies, if any, are looked for and shown before anything is replaced)
            FileDecisions = chosen is null ? new Dictionary<string, FileDecision>(StringComparer.Ordinal)
                : ReviewChoice.SameTitle(chosen.Candidate, ReviewChoice.CurrentMatch(r)) ? r.FileDecisions
                : r.FileDecisions.Where(d => d.Value != FileDecision.Replace).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
            FileEpisodes = chosen is null ? new Dictionary<string, EpisodeNumber>(StringComparer.Ordinal) : r.FileEpisodes,

            // Another title (or a clean retry) starts again from that choice: an approved AI suggestion no longer applies
            Approved = new Dictionary<string, AiSuggestion>(StringComparer.Ordinal),
            Request = ReviewRequest.Retry,
            RequestVersion = r.RequestVersion + 1,
        });

    /// <summary>
    /// Approves the AI suggestions a review shows, as the page showed them (<see cref="AiApproval.KeyOf"/>), and plans
    /// the release again on the next sweep, filing each video exactly as suggested (without asking the AI again).
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="shownKey">The suggestions' key as the page showed it.</param>
    /// <param name="replace">Whether to also replace the copies already on the server that hold the release back.</param>
    /// <returns>Whether it was approved, or why not.</returns>
    public AiApprovalOutcome RequestApprove(string id, string? shownKey, bool replace = false)
    {
        lock (_lock)
        {
            var s = Load();
            var i = s.Reviews.FindIndex(r => r.Id == id);
            if (i < 0)
            {
                return AiApprovalOutcome.NotFound;
            }

            var outcome = AiApproval.Check(s.Reviews[i], shownKey);
            if (outcome != AiApprovalOutcome.Approved)
            {
                return outcome;
            }

            s.Reviews[i] = AiApproval.Approve(s.Reviews[i], replace);
            Save(s);
            return outcome;
        }
    }

    /// <summary>
    /// Approves many reviews' AI suggestions at once (Approve all), in one save: only reviews that wait for nothing but
    /// an AI suggestion (<see cref="AiApproval.IsOnlyAi"/>), and only as the page showed them. Others are skipped.
    /// </summary>
    /// <param name="approvals">The reviews and the suggestion keys the page showed.</param>
    /// <returns>The reviews approved.</returns>
    public IReadOnlyList<PendingReview> ApproveAll(IEnumerable<ReviewApproval> approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        lock (_lock)
        {
            var s = Load();
            var done = new List<PendingReview>();
            foreach (var approval in approvals.Where(a => a is not null).DistinctBy(a => a.Id))
            {
                var i = s.Reviews.FindIndex(r => r.Id == approval.Id);
                if (i < 0 || !AiApproval.IsOnlyAi(s.Reviews[i]) || AiApproval.Check(s.Reviews[i], approval.Key) != AiApprovalOutcome.Approved)
                {
                    continue;
                }

                s.Reviews[i] = AiApproval.Approve(s.Reviews[i], replace: false);
                done.Add(s.Reviews[i]);
            }

            if (done.Count > 0)
            {
                Save(s);
            }

            return done;
        }
    }

    /// <summary>
    /// Records decisions made file by file and plans the release again on the next sweep. A file given no decision
    /// ("decide later") loses any earlier one.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="decisions">Decisions by file (relative to the watch folder); <c>null</c> clears that file's.</param>
    /// <param name="episodes">Season and episode numbers by video; <c>null</c> clears that video's. Videos not listed keep theirs.</param>
    /// <returns>Whether the review exists.</returns>
    public bool RequestFiles(string id, IReadOnlyDictionary<string, FileDecision?> decisions, IReadOnlyDictionary<string, EpisodeNumber?>? episodes = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        return Update(id, r =>
        {
            var numbers = new Dictionary<string, EpisodeNumber>(r.FileEpisodes, StringComparer.Ordinal);
            foreach (var (source, number) in episodes ?? new Dictionary<string, EpisodeNumber?>())
            {
                if (number is not null)
                {
                    numbers[source] = number;
                }
                else
                {
                    numbers.Remove(source);
                }
            }

            var merged = new Dictionary<string, FileDecision>(r.FileDecisions, StringComparer.Ordinal);
            foreach (var (source, decision) in decisions)
            {
                if (decision is { } d)
                {
                    merged[source] = d;
                }
                else
                {
                    merged.Remove(source);
                }
            }

            return r with { FileDecisions = merged, FileEpisodes = numbers, Request = ReviewRequest.Retry, RequestVersion = r.RequestVersion + 1 };
        });
    }

    /// <summary>
    /// Stores the results of a title search made for a review, replacing any earlier ones.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="results">The results, best first.</param>
    /// <returns><c>false</c> if there is no such review.</returns>
    public bool SetSearchResults(string id, IReadOnlyList<ScoredCandidate> results)
        => Update(id, r => r with { SearchResults = [.. (results ?? []).Take(MaxCandidates * 2)] });

    /// <summary>
    /// Asks for a whole release to be quarantined.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <returns><c>false</c> if there is no such review.</returns>
    public bool RequestQuarantine(string id)
        => Update(id, r => r with { Request = ReviewRequest.Quarantine, RequestVersion = r.RequestVersion + 1 });

    /// <summary>
    /// Asks for a release to be filed on the next sweep, replacing the copies already on the server (they go to
    /// quarantine, so they can be restored until it is purged).
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <returns>Whether the review exists.</returns>
    public bool RequestReplace(string id)
        => Update(id, r => r with { Request = ReviewRequest.Replace, RequestVersion = r.RequestVersion + 1 });

    /// <summary>
    /// Clears a review's pending request, keeping everything else, unless a newer request has been made since.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="actedOnVersion">The request version that was acted on.</param>
    public void ClearRequest(string id, int actedOnVersion)
        => Update(id, r => r.RequestVersion == actedOnVersion ? r with { Request = ReviewRequest.None } : r);

    /// <summary>
    /// Marks a review as planned in dry run: its old reasons no longer apply, so they are replaced by a note and the
    /// request is cleared. The choice is kept so the release files the same way once dry run is off.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="note">What was planned.</param>
    /// <param name="actedOnVersion">The request version that was acted on (a newer request is kept).</param>
    public void MarkPlannedInDryRun(string id, string note, int actedOnVersion)
        => Update(id, r => r with { Request = r.RequestVersion == actedOnVersion ? ReviewRequest.None : r.Request, Items = [new PendingReviewItem(r.Release, note)] });

    /// <summary>
    /// Removes a review (the release was filed, quarantined or has gone from the watch folder).
    /// </summary>
    /// <param name="id">Review id.</param>
    public void RemoveReview(string id)
    {
        lock (_lock)
        {
            var s = Load();
            if (s.Reviews.RemoveAll(r => r.Id == id) > 0)
            {
                Save(s);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether Ingest is paused (sweeps file nothing). Kept in the state file, so it survives a
    /// restart, and not in the plugin settings, which the settings page saves as a whole.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return Load().Paused;
            }
        }
    }

    /// <summary>
    /// Pauses or resumes Ingest.
    /// </summary>
    /// <param name="paused">Whether to pause.</param>
    /// <returns>Whether that changed anything.</returns>
    public bool SetPaused(bool paused)
    {
        lock (_lock)
        {
            var s = Load();
            if (s.Paused == paused)
            {
                return false;
            }

            s.Paused = paused;
            Save(s);
            return true;
        }
    }

    /// <summary>
    /// Finds the activity entry of a filing by its run (see <see cref="ActivityEntry.Run"/>).
    /// </summary>
    /// <param name="run">The run's id.</param>
    /// <returns>The entry, or <c>null</c>.</returns>
    public ActivityEntry? FindFiling(string run)
    {
        lock (_lock)
        {
            return string.IsNullOrEmpty(run) ? null : Load().Activity.FirstOrDefault(a => a.Status == ActivityStatus.Filed && string.Equals(a.Run, run, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Marks a filing as undone, so it shows as such and can't be undone twice.
    /// </summary>
    /// <param name="run">The run's id.</param>
    /// <param name="time">When it was undone.</param>
    /// <returns>Whether the entry was found.</returns>
    public bool MarkUndone(string run, DateTimeOffset time)
    {
        lock (_lock)
        {
            var s = Load();
            var i = s.Activity.FindIndex(a => a.Status == ActivityStatus.Filed && string.Equals(a.Run, run, StringComparison.Ordinal));
            if (i < 0)
            {
                return false;
            }

            s.Activity[i] = s.Activity[i] with { UndoneAt = time };
            Save(s);
            return true;
        }
    }

    /// <summary>
    /// Changes a filing's activity entry (found by its run), for a move to another library.
    /// </summary>
    /// <param name="run">The filing's run.</param>
    /// <param name="change">The change.</param>
    /// <returns>Whether the entry was found.</returns>
    public bool UpdateFiling(string run, Func<ActivityEntry, ActivityEntry> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var s = Load();
            var i = s.Activity.FindIndex(a => a.Status == ActivityStatus.Filed && string.Equals(a.Run, run, StringComparison.Ordinal));
            if (i < 0)
            {
                return false;
            }

            s.Activity[i] = change(s.Activity[i]);
            Save(s);
            return true;
        }
    }

    /// <summary>
    /// Forgets that a release was filed by copy or hard link (after an undo, so the review sees it).
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="release">The release.</param>
    /// <returns>What was remembered, so it can be put back if the undo fails; or <c>null</c>.</returns>
    public CopiedRelease? ForgetCopied(string watchFolder, string release)
    {
        lock (_lock)
        {
            var s = Load();
            var found = s.Copied.FirstOrDefault(c => PathGuard.SamePath(c.WatchFolder, watchFolder) && c.Release == release);
            if (found is not null)
            {
                s.Copied.Remove(found);
                Save(s);
            }

            return found;
        }
    }

    /// <summary>
    /// Whether a release was already filed by copy or hard link and hasn't changed since.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="release">The release.</param>
    /// <param name="signature">Its files now (see <see cref="CopySignature"/>).</param>
    /// <returns>Whether it was already filed.</returns>
    public bool WasCopied(string watchFolder, string release, string signature)
    {
        lock (_lock)
        {
            return Load().Copied.Any(c => c.WatchFolder == watchFolder && c.Release == release && c.Signature == signature);
        }
    }

    /// <summary>
    /// Remembers a release filed by copy or hard link.
    /// </summary>
    /// <param name="copied">The release.</param>
    public void MarkCopied(CopiedRelease copied)
    {
        ArgumentNullException.ThrowIfNull(copied);
        lock (_lock)
        {
            var s = Load();
            s.Copied.RemoveAll(c => c.WatchFolder == copied.WatchFolder && c.Release == copied.Release);
            s.Copied.Add(copied);
            Save(s);
        }
    }

    /// <summary>
    /// Forgets copied releases that have left their watch folder.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="present">The releases in it now.</param>
    public void PruneCopied(string watchFolder, IReadOnlyCollection<string> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        lock (_lock)
        {
            var s = Load();
            if (s.Copied.RemoveAll(c => c.WatchFolder == watchFolder && !present.Contains(c.Release)) > 0)
            {
                Save(s);
            }
        }
    }

    /// <summary>
    /// A release's signature: its files' names and sizes.
    /// </summary>
    /// <param name="files">The release's files.</param>
    /// <returns>The signature.</returns>
    public static string CopySignature(IEnumerable<Planning.ReleaseFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var text = string.Join('\n', files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => f.RelativePath + "|" + f.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Drops reviews for releases of a watch folder that are no longer there.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="present">Release names currently in it.</param>
    /// <returns>The reviews dropped.</returns>
    public IReadOnlyList<PendingReview> PruneReviews(string watchFolder, IReadOnlyCollection<string> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        return RemoveReviews(r => r.WatchFolder == watchFolder && !present.Contains(r.Release));
    }

    /// <summary>
    /// Drops every review that can never be acted on: its watch folder is no longer configured, or the watch folder is
    /// there but the release isn't. Unlike <see cref="PruneReviews"/>, this also covers watch folders that aren't swept
    /// (switched off, unsafe settings, no library). A watch folder that isn't there at all (an unmounted share) keeps
    /// its reviews until it is back or removed from the settings.
    /// </summary>
    /// <param name="watchFolders">The configured watch folders.</param>
    /// <param name="folderExists">Whether a watch folder is there.</param>
    /// <param name="entryExists">Whether a release (file, folder or link) is there.</param>
    /// <returns>The reviews dropped, each with why.</returns>
    public IReadOnlyList<(PendingReview Review, string Why)> PruneGone(IReadOnlyCollection<string> watchFolders, Func<string, bool> folderExists, Func<string, bool> entryExists)
    {
        ArgumentNullException.ThrowIfNull(watchFolders);
        ArgumentNullException.ThrowIfNull(folderExists);
        ArgumentNullException.ThrowIfNull(entryExists);

        string? Why(PendingReview r)
            => !watchFolders.Any(w => PathGuard.SamePath(w, r.WatchFolder)) ? "Its watch folder is no longer set up."
                : folderExists(r.WatchFolder) && !entryExists(Path.Combine(r.WatchFolder, r.Release)) ? "It is no longer in the watch folder."
                : null;

        lock (_lock)
        {
            var s = Load();
            var gone = s.Reviews.Select(r => (Review: r, Why: Why(r))).Where(g => g.Why is not null).Select(g => (g.Review, g.Why!)).ToList();
            if (gone.Count > 0)
            {
                s.Reviews.RemoveAll(r => gone.Any(g => ReferenceEquals(g.Review, r)));
                Save(s);
            }

            return gone;
        }
    }

    private List<PendingReview> RemoveReviews(Predicate<PendingReview> match)
    {
        lock (_lock)
        {
            var s = Load();
            var removed = s.Reviews.FindAll(match);
            if (removed.Count > 0)
            {
                s.Reviews.RemoveAll(match);
                Save(s);
            }

            return removed;
        }
    }

    private bool Update(string id, Func<PendingReview, PendingReview> change)
    {
        lock (_lock)
        {
            var s = Load();
            var i = s.Reviews.FindIndex(r => r.Id == id);
            if (i < 0)
            {
                return false;
            }

            s.Reviews[i] = change(s.Reviews[i]);
            Save(s);
            return true;
        }
    }

    private StateFile Load()
    {
        if (_state is not null)
        {
            return _state;
        }

        // Policy: missing starts empty; damaged is set aside (never silently overwritten) and the dashboard starts
        // empty; unreadable (permissions, an offline disk) runs from memory and never replaces what's there
        StateFile? loaded = null;
        var read = JsonFile.Read<StateFile>(_path, JsonOptions);
        switch (read.State)
        {
            case JsonFileState.Loaded:
                loaded = read.Value;
                break;
            case JsonFileState.Damaged:
                // A damaged state file only loses dashboard history; the releases themselves are untouched. It is
                // kept to one side (not silently overwritten) so it can be inspected.
                var aside = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    File.Move(_path, aside, overwrite: true);
                    if (_logger is not null)
                    {
                        LogCorrupt(_logger, aside, read.Error!);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Couldn't move it: carry on in memory rather than overwrite it
                    MemoryOnly(ex);
                }

                break;
            case JsonFileState.Unreadable:
                // Can't read it (permissions, offline disk): carry on in memory and never overwrite what's there
                MemoryOnly(read.Error!);
                break;
        }

        _state = loaded ?? new StateFile();
        _state.Reviews ??= [];
        _state.Activity ??= [];
        _state.Copied ??= [];
        _state.Copied.RemoveAll(c => c is null || c.WatchFolder is null || c.Release is null || c.Signature is null);
        _state.Activity.RemoveAll(a => a is null || a.Release is null);
        _state.Reviews.RemoveAll(r => r is null || r.Id is null || r.WatchFolder is null || r.Release is null);

        for (var i = 0; i < _state.Reviews.Count; i++)
        {
            // Lists written as null come back as null; a choice saved in an older format can come back incomplete
            // (dropped rather than planned with)
            var r = _state.Reviews[i];
            _state.Reviews[i] = r with
            {
                // A suggestion saved without its title can't be shown or approved
                Items = [.. (r.Items ?? []).Where(it => it is not null).Select(it => it.Suggestion is { } sg ? it with { Suggestion = sg.Title is null ? null : sg with { Decisions = sg.Decisions ?? [] } } : it)],
                Candidates = r.Candidates ?? [],
                SearchResults = r.SearchResults ?? [],
                FileDecisions = r.FileDecisions ?? new Dictionary<string, FileDecision>(StringComparer.Ordinal),
                FileEpisodes = r.FileEpisodes is null
                    ? new Dictionary<string, EpisodeNumber>(StringComparer.Ordinal)
                    : r.FileEpisodes.Where(e => e.Value is { IsValid: true }).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
                Approved = r.Approved is null
                    ? new Dictionary<string, AiSuggestion>(StringComparer.Ordinal)
                    : r.Approved.Where(a => a.Value is { Title: not null, IsComplete: true }).ToDictionary(a => a.Key, a => a.Value with { Decisions = a.Value.Decisions ?? [] }, StringComparer.Ordinal),
                Chosen = r.Chosen is { Candidate: not null, Target: not null } c ? c : null,
            };
        }

        for (var i = 0; i < _state.Activity.Count; i++)
        {
            _state.Activity[i] = _state.Activity[i] with { Details = _state.Activity[i].Details ?? [], Summary = _state.Activity[i].Summary ?? string.Empty };
        }

        return _state;
    }

    private void Save(StateFile state)
    {
        if (_memoryOnly)
        {
            return;
        }

        try
        {
            JsonFile.WriteAtomic(_path, state, JsonOptions);
            _lastSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The dashboard keeps working from memory; the problem is logged once until it changes
            if (!string.Equals(_lastSaveError, ex.Message, StringComparison.Ordinal))
            {
                _lastSaveError = ex.Message;
                if (_logger is not null)
                {
                    LogUnwritable(_logger, _path, ex);
                }
            }
        }
    }

    private void MemoryOnly(Exception ex)
    {
        _memoryOnly = true;
        if (_logger is not null)
        {
            LogUnreadable(_logger, _path, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ingest: the state file was damaged and has been set aside as {Path}; the dashboard starts empty")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest: can't read the state file {Path}; the dashboard runs from memory until restart")]
    private static partial void LogUnreadable(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingest: can't save the state file {Path}; changes are kept in memory")]
    private static partial void LogUnwritable(ILogger logger, string path, Exception exception);

    private sealed class StateFile
    {
        public List<PendingReview> Reviews { get; set; } = [];

        public List<ActivityEntry> Activity { get; set; } = [];

        // Releases filed by copy or hard link (still in their watch folder), so they aren't filed again
        public List<CopiedRelease> Copied { get; set; } = [];

        // Sweeps file nothing while paused
        public bool Paused { get; set; }
    }
}
