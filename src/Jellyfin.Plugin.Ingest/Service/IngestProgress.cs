using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// A release seen in a watch folder but not yet handled (ING-36).
/// </summary>
public sealed record WaitingRelease
{
    /// <summary>Gets the release's id (the same as its review's, see <see cref="IngestStateStore.ReviewId"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Gets the watch folder.</summary>
    public required string WatchFolder { get; init; }

    /// <summary>Gets the release name inside the watch folder.</summary>
    public required string Release { get; init; }

    /// <summary>
    /// Gets when it will have stopped changing for the settle time, if nothing changes before then; <c>null</c> while it
    /// still holds in-progress download files (<c>.part</c> and the like), which never settle.
    /// </summary>
    public DateTimeOffset? SettlesAt { get; init; }

    /// <summary>Gets a value indicating whether "Process now" was asked for and the next sweep will skip the wait.</summary>
    public bool ProcessNow { get; init; }
}

/// <summary>
/// The release being identified or filed right now (ING-36).
/// </summary>
public sealed record WorkInProgress
{
    /// <summary>Gets the release's id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the watch folder.</summary>
    public required string WatchFolder { get; init; }

    /// <summary>Gets the release name inside the watch folder.</summary>
    public required string Release { get; init; }

    /// <summary>Gets what is happening: <c>Identifying</c> or <c>Filing</c>.</summary>
    public required string Stage { get; init; }

    /// <summary>Gets which file is being filed (from 1), while filing.</summary>
    public int File { get; init; }

    /// <summary>Gets how many files are being filed, while filing.</summary>
    public int Files { get; init; }

    /// <summary>Gets when this stage started.</summary>
    public DateTimeOffset Since { get; init; }
}

/// <summary>
/// What an administrator asked to be put back.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<QueuedActionKind>))]
public enum QueuedActionKind
{
    /// <summary>Undo a filing (<see cref="QueuedAction.Run"/>).</summary>
    Undo = 0,

    /// <summary>Restore a quarantined release (<see cref="QueuedAction.Root"/>, <see cref="QueuedAction.Folder"/>, <see cref="QueuedAction.Name"/>).</summary>
    Restore,
}

/// <summary>
/// An undo or restore asked for on the page, carried out by the sweep (between releases, so it never races a filing
/// or the watch-folder scan).
/// </summary>
public sealed record QueuedAction
{
    /// <summary>Gets what is asked for.</summary>
    public required QueuedActionKind Kind { get; init; }

    /// <summary>Gets the filing to undo (its <see cref="ActivityEntry.Run"/>).</summary>
    public string? Run { get; init; }

    /// <summary>Gets the quarantine folder, for a restore.</summary>
    public string? Root { get; init; }

    /// <summary>Gets the dated folder's name, for a restore.</summary>
    public string? Folder { get; init; }

    /// <summary>Gets the release's name in the dated folder, for a restore.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// What the sweep is waiting for and working on, shared between the sweep service and the API, plus the "Process now"
/// requests going the other way. Held in memory only (after a restart releases settle again). Thread-safe.
/// </summary>
public sealed class IngestProgress
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IReadOnlyList<WaitingRelease>> _waiting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processNow = new(StringComparer.Ordinal);
    private readonly List<QueuedAction> _queued = [];
    private WorkInProgress? _working;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the lock held while files are moved or deleted (filing, quarantining, undo, restore, "delete now"), so a
    /// deletion asked for on the page never runs while the sweep is moving files into the same folder.
    /// </summary>
    public Lock FileGate { get; } = new();

    /// <summary>
    /// Gets the release being worked on, if any.
    /// </summary>
    public WorkInProgress? Working
    {
        get
        {
            lock (_lock)
            {
                return _working;
            }
        }
    }

    /// <summary>
    /// The releases waiting, across watch folders, soonest first (those still downloading last).
    /// </summary>
    /// <returns>A copy.</returns>
    public IReadOnlyList<WaitingRelease> Waiting()
    {
        lock (_lock)
        {
            return [.. _waiting.Values.SelectMany(w => w)
                .Select(w => _processNow.Contains(w.Id) ? w with { ProcessNow = true } : w)
                .OrderBy(w => w.SettlesAt ?? DateTimeOffset.MaxValue)
                .ThenBy(w => w.Release, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Replaces what a watch folder is waiting for.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="waiting">Its waiting releases.</param>
    public void SetWaiting(string watchFolder, IReadOnlyList<WaitingRelease> waiting)
    {
        ArgumentNullException.ThrowIfNull(watchFolder);
        ArgumentNullException.ThrowIfNull(waiting);
        lock (_lock)
        {
            _waiting[watchFolder] = [.. waiting];
        }
    }

    /// <summary>
    /// Forgets watch folders that are no longer swept (removed or switched off).
    /// </summary>
    /// <param name="watchFolders">The folders being swept.</param>
    public void KeepOnly(IReadOnlyCollection<string> watchFolders)
    {
        ArgumentNullException.ThrowIfNull(watchFolders);
        lock (_lock)
        {
            foreach (var gone in _waiting.Keys.Where(k => !watchFolders.Contains(k)).ToList())
            {
                _waiting.Remove(gone);
            }

            var ids = _waiting.Values.SelectMany(w => w).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            _processNow.RemoveWhere(id => !ids.Contains(id));
        }
    }

    /// <summary>
    /// Says which release is being worked on now, and how far it has got.
    /// </summary>
    /// <param name="working">The release and stage; <c>null</c> when done.</param>
    public void SetWorking(WorkInProgress? working)
    {
        lock (_lock)
        {
            _working = working;
        }
    }

    /// <summary>
    /// Asks for a waiting release to skip the rest of its settle wait, and wakes the sweep.
    /// </summary>
    /// <param name="id">The release's id.</param>
    /// <returns>Whether it is waiting (and not still downloading).</returns>
    public bool RequestProcessNow(string id)
    {
        lock (_lock)
        {
            if (!_waiting.Values.SelectMany(w => w).Any(w => w.Id == id && w.SettlesAt is not null))
            {
                return false;
            }

            _processNow.Add(id);
            _wake.TrySetResult();
            return true;
        }
    }

    /// <summary>
    /// Asks the sweep to carry out an undo or restore, and wakes it. Held in memory: after a restart it is asked for again.
    /// </summary>
    /// <param name="action">What to do.</param>
    /// <returns><c>false</c> if the same was already asked for.</returns>
    public bool Queue(QueuedAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
        {
            if (_queued.Contains(action))
            {
                return false;
            }

            _queued.Add(action);
            _wake.TrySetResult();
            return true;
        }
    }

    /// <summary>
    /// The undos and restores asked for and not yet finished, oldest first.
    /// </summary>
    /// <returns>A copy.</returns>
    public IReadOnlyList<QueuedAction> Queued()
    {
        lock (_lock)
        {
            return [.. _queued];
        }
    }

    /// <summary>
    /// Removes an undo or restore once it has been carried out (or refused).
    /// </summary>
    /// <param name="action">The action.</param>
    public void Complete(QueuedAction action)
    {
        lock (_lock)
        {
            _queued.Remove(action);
        }
    }

    /// <summary>
    /// Wakes the sweep now (after Ingest is resumed).
    /// </summary>
    public void Wake()
    {
        lock (_lock)
        {
            _wake.TrySetResult();
        }
    }

    /// <summary>
    /// Takes a "Process now" request, if one was made for this release.
    /// </summary>
    /// <param name="id">The release's id.</param>
    /// <returns>Whether one was.</returns>
    public bool TakeProcessNow(string id)
    {
        lock (_lock)
        {
            return _processNow.Remove(id);
        }
    }

    /// <summary>
    /// Waits until the next sweep is due, or sooner if "Process now" is asked for.
    /// </summary>
    /// <param name="interval">The usual wait.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes when the next sweep should start.</returns>
    public async Task WaitForNextSweepAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        Task wake;
        lock (_lock)
        {
            wake = _wake.Task;
        }

        await Task.WhenAny(wake, Task.Delay(interval, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
