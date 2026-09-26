using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Ingest.Planning;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Automatic retries of releases that couldn't be planned or filed for a reason that may pass by itself (an offline
/// library, providers that found nothing). Kept in memory for the service's life.
/// </summary>
public sealed class RetrySchedule
{
    private readonly Dictionary<string, (string WatchFolder, string Release, DateTimeOffset At, int Attempt)> _retries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetrySchedule"/> class.
    /// </summary>
    /// <param name="clock">Clock.</param>
    public RetrySchedule(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// How long to wait before trying a release again by itself. An offline library is retried until it is back (5
    /// minutes, doubling to hourly); providers that found nothing are retried three times (after 10 minutes, 1 hour and
    /// 6 hours), since an outage and an unknown title look the same, then left for a person.
    /// </summary>
    /// <param name="kind">Why the release couldn't be planned.</param>
    /// <param name="attempt">Which retry this would be (1 for the first).</param>
    /// <returns>The delay, or <c>null</c> to stop retrying.</returns>
    public static TimeSpan? RetryDelay(RetryKind kind, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return kind switch
        {
            RetryKind.FolderUnavailable => TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Min(attempt - 1, 10)))),
            RetryKind.NothingFound => attempt switch
            {
                1 => TimeSpan.FromMinutes(10),
                2 => TimeSpan.FromHours(1),
                3 => TimeSpan.FromHours(6),
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>
    /// Schedules (or ends) automatic retries of a release.
    /// </summary>
    /// <param name="id">The release's review id.</param>
    /// <param name="watchFolder">Its watch folder.</param>
    /// <param name="release">The release.</param>
    /// <param name="kind">Why it couldn't be planned or filed.</param>
    /// <returns>When the next retry is due, or <c>null</c> if there is none.</returns>
    public DateTimeOffset? Schedule(string id, string watchFolder, string release, RetryKind kind)
    {
        ArgumentNullException.ThrowIfNull(id);
        var attempt = _retries.TryGetValue(id, out var r) ? r.Attempt + 1 : 1;
        if (RetryDelay(kind, attempt) is not { } delay)
        {
            _retries.Remove(id);
            return null;
        }

        var at = _clock.GetUtcNow() + delay;
        _retries[id] = (watchFolder, release, at, attempt);
        return at;
    }

    /// <summary>
    /// Ends a release's retries (it was planned or filed).
    /// </summary>
    /// <param name="id">The release's review id.</param>
    public void Clear(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        _retries.Remove(id);
    }

    /// <summary>
    /// The releases in a watch folder whose retry is due, to be offered again. Retries of releases that have gone are
    /// dropped.
    /// </summary>
    /// <param name="watchFolder">The watch folder.</param>
    /// <param name="present">Whether a release is still in the watch folder.</param>
    /// <returns>The releases due.</returns>
    public IReadOnlyList<string> Due(string watchFolder, Func<string, bool> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        var due = new List<string>();
        foreach (var (id, retry) in _retries.Where(r => r.Value.WatchFolder == watchFolder).ToList())
        {
            if (!present(retry.Release))
            {
                _retries.Remove(id);
            }
            else if (retry.At <= _clock.GetUtcNow())
            {
                due.Add(retry.Release);
            }
        }

        return due;
    }
}
