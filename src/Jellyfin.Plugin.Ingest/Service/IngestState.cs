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

    /// <summary>Gets when the release will be tried again automatically (an offline library, or providers that found nothing), if it will.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>Gets the title and library chosen in review, if any; used instead of searching when the release is planned again.</summary>
    public ChosenMatch? Chosen { get; init; }

    /// <summary>Gets the pending request.</summary>
    public ReviewRequest Request { get; init; }

    /// <summary>
    /// Gets a counter that goes up with every request, so planning (which takes a while) only clears the request it
    /// started from and never one made while it was running.
    /// </summary>
    public int RequestVersion { get; init; }
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
            var candidates = review.Candidates.Take(MaxCandidates).ToList();
            var updated = review with
            {
                Candidates = candidates,
                Chosen = existing?.Chosen,
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
        => Update(id, r => r with { Chosen = chosen, Request = ReviewRequest.Retry, RequestVersion = r.RequestVersion + 1 });

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

    /// <summary>
    /// Drops reviews of watch folders that are no longer configured.
    /// </summary>
    /// <param name="watchFolders">The configured watch folders.</param>
    public void PruneWatchFolders(IReadOnlyCollection<string> watchFolders)
    {
        ArgumentNullException.ThrowIfNull(watchFolders);
        lock (_lock)
        {
            var s = Load();
            if (s.Reviews.RemoveAll(r => !watchFolders.Any(w => PathGuard.SamePath(w, r.WatchFolder))) > 0)
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

        StateFile? loaded = null;
        try
        {
            if (File.Exists(_path))
            {
                try
                {
                    loaded = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_path), JsonOptions);
                }
                catch (JsonException ex)
                {
                    // A damaged state file only loses dashboard history; the releases themselves are untouched. It is
                    // kept to one side (not silently overwritten) so it can be inspected.
                    var aside = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    File.Move(_path, aside, overwrite: true);
                    if (_logger is not null)
                    {
                        LogCorrupt(_logger, aside, ex);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't read it (permissions, offline disk): carry on in memory and never overwrite what's there
            _memoryOnly = true;
            if (_logger is not null)
            {
                LogUnreadable(_logger, _path, ex);
            }
        }

        _state = loaded ?? new StateFile();
        _state.Reviews ??= [];
        _state.Activity ??= [];
        _state.Activity.RemoveAll(a => a is null || a.Release is null);
        _state.Reviews.RemoveAll(r => r is null || r.Id is null || r.WatchFolder is null || r.Release is null);

        for (var i = 0; i < _state.Reviews.Count; i++)
        {
            // Lists written as null come back as null; a choice saved in an older format can come back incomplete
            // (dropped rather than planned with)
            var r = _state.Reviews[i];
            _state.Reviews[i] = r with
            {
                Items = r.Items ?? [],
                Candidates = r.Candidates ?? [],
                SearchResults = r.SearchResults ?? [],
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
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _path, overwrite: true);
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
    }
}
