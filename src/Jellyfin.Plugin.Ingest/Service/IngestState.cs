using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;

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

    /// <summary>Gets the title and library chosen in review, if any; used instead of searching when the release is planned again.</summary>
    public ChosenMatch? Chosen { get; init; }

    /// <summary>Gets the pending request.</summary>
    public ReviewRequest Request { get; init; }
}

/// <summary>
/// One file's review reason.
/// </summary>
/// <param name="Source">The file (path relative to the watch folder).</param>
/// <param name="Reason">Why it can't be filed automatically.</param>
public sealed record PendingReviewItem(string Source, string Reason);

/// <summary>
/// Everything the dashboard shows, persisted as JSON in the plugin's data folder so it survives restarts.
/// </summary>
public sealed record IngestState
{
    /// <summary>Gets releases waiting for review.</summary>
    public IReadOnlyList<PendingReview> Reviews { get; init; } = [];

    /// <summary>Gets recent activity, newest first.</summary>
    public IReadOnlyList<ActivityEntry> Activity { get; init; } = [];
}

/// <summary>
/// Thread-safe access to the <see cref="IngestState"/>, shared by the sweep service, the purge task and the API.
/// Every change is written straight to disk (write to a temporary file, then replace).
/// </summary>
public sealed class IngestStateStore
{
    /// <summary>How many activity entries are kept.</summary>
    public const int MaxActivity = 300;

    /// <summary>How many detail lines an activity entry keeps.</summary>
    public const int MaxDetails = 60;

    /// <summary>How many candidates a review keeps.</summary>
    public const int MaxCandidates = 8;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _lock = new();
    private StateFile? _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestStateStore"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the JSON state file.</param>
    public IngestStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
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
            return new IngestState { Reviews = [.. s.Reviews], Activity = [.. s.Activity] };
        }
    }

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
    /// Adds or refreshes a release's review. A title chosen earlier is kept; any request is cleared (it has been acted on).
    /// </summary>
    /// <param name="review">The review (its <see cref="PendingReview.Chosen"/> and <see cref="PendingReview.Request"/> are ignored).</param>
    public void PutReview(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        lock (_lock)
        {
            var s = Load();
            var i = s.Reviews.FindIndex(r => r.Id == review.Id);
            var chosen = i >= 0 ? s.Reviews[i].Chosen : null;
            var searched = i >= 0 ? s.Reviews[i].SearchResults : [];
            var candidates = review.Candidates.Take(MaxCandidates).ToList();
            var updated = review with { Candidates = candidates, Chosen = chosen, SearchResults = searched, Request = ReviewRequest.None };
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
        => Update(id, r => r with { Chosen = chosen, Request = ReviewRequest.Retry });

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
        => Update(id, r => r with { Request = ReviewRequest.Quarantine });

    /// <summary>
    /// Clears a review's pending request, keeping everything else.
    /// </summary>
    /// <param name="id">Review id.</param>
    public void ClearRequest(string id)
        => Update(id, r => r with { Request = ReviewRequest.None });

    /// <summary>
    /// Marks a review as planned in dry run: its old reasons no longer apply, so they are replaced by a note and the
    /// request is cleared. The choice is kept so the release files the same way once dry run is off.
    /// </summary>
    /// <param name="id">Review id.</param>
    /// <param name="note">What was planned.</param>
    public void MarkPlannedInDryRun(string id, string note)
        => Update(id, r => r with { Request = ReviewRequest.None, Items = [new PendingReviewItem(r.Release, note)] });

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
    /// Drops reviews for releases of a watch folder that are no longer there.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="present">Release names currently in it.</param>
    public void PruneReviews(string watchFolder, IReadOnlyCollection<string> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        lock (_lock)
        {
            var s = Load();
            if (s.Reviews.RemoveAll(r => r.WatchFolder == watchFolder && !present.Contains(r.Release)) > 0)
            {
                Save(s);
            }
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

        try
        {
            _state = File.Exists(_path) ? JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path), JsonOptions) : null;
        }
        catch (JsonException)
        {
            // A damaged state file only loses dashboard history; the releases themselves are untouched.
            _state = null;
        }

        _state ??= new StateFile();

        // A choice saved in an older format can come back incomplete; drop it rather than plan with it.
        for (var i = 0; i < _state.Reviews.Count; i++)
        {
            if (_state.Reviews[i].Chosen is { } c && (c.Candidate is null || c.Target is null))
            {
                _state.Reviews[i] = _state.Reviews[i] with { Chosen = null };
            }
        }

        return _state;
    }

    private void Save(StateFile state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class StateFile
    {
        public List<PendingReview> Reviews { get; set; } = [];

        public List<ActivityEntry> Activity { get; set; } = [];
    }
}
