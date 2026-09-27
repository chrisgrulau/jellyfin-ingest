using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// An entry for Jellyfin's Activity log (Dashboard → Activity), built from an Ingest activity entry.
/// </summary>
/// <param name="Name">The headline.</param>
/// <param name="ShortOverview">One line.</param>
/// <param name="Overview">The details.</param>
/// <param name="Severity">How it is shown.</param>
public sealed record ActivityNote(string Name, string ShortOverview, string Overview, LogLevel Severity);

/// <summary>
/// Copies what needs attention (a release waiting for a decision, a failure) and what was done (filed, quarantined) to
/// Jellyfin's Activity log, so it is seen without opening the plugin page (FAM-05). The same release and outcome is
/// written at most once a day, however often it is retried.
/// </summary>
public sealed class ActivityNotifier
{
    /// <summary>The Activity log type of Ingest's entries.</summary>
    public const string Type = "ShoalIngest";

    /// <summary>How long the same release and outcome isn't written again.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromDays(1);

    private readonly Func<ActivityNote, Task> _write;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, DateTimeOffset> _written = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityNotifier"/> class.
    /// </summary>
    /// <param name="write">Writes an entry to Jellyfin's Activity log.</param>
    /// <param name="clock">Clock.</param>
    public ActivityNotifier(Func<ActivityNote, Task> write, TimeProvider clock)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// The Activity log entry for an Ingest activity entry, if it is one worth writing.
    /// </summary>
    /// <param name="entry">The Ingest activity entry.</param>
    /// <returns>The note, or <c>null</c> (dry runs, decisions and purges stay on the plugin page).</returns>
    public static ActivityNote? NoteFor(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var (headline, severity) = entry.Status switch
        {
            ActivityStatus.NeedsReview => ("Ingest needs a decision: ", LogLevel.Warning),
            ActivityStatus.Failed => ("Ingest couldn't file: ", LogLevel.Error),
            ActivityStatus.Filed => ("Ingest filed ", LogLevel.Information),
            ActivityStatus.Quarantined => ("Ingest quarantined ", LogLevel.Information),
            ActivityStatus.Undone => ("Ingest undid the filing of ", LogLevel.Information),
            ActivityStatus.Restored => ("Ingest restored from quarantine: ", LogLevel.Information),
            _ => ((string?)null, LogLevel.None),
        };
        if (headline is null)
        {
            return null;
        }

        var summary = entry.Summary.Length > 250 ? entry.Summary[..250] + "…" : entry.Summary;
        return new ActivityNote(headline + entry.Release, summary, string.Join('\n', entry.Details.Take(40)), severity);
    }

    /// <summary>
    /// Writes an entry to Jellyfin's Activity log unless it isn't one worth writing or was written in the last day.
    /// Never throws: the Activity log is a convenience.
    /// </summary>
    /// <param name="entry">The Ingest activity entry.</param>
    /// <returns>Whether it was written.</returns>
    public async Task<bool> NotifyAsync(ActivityEntry entry)
    {
        if (NoteFor(entry) is not { } note)
        {
            return false;
        }

        var key = entry.WatchFolder + "|" + entry.Release + "|" + entry.Status + "|" + entry.Summary;
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (_written.TryGetValue(key, out var at) && now - at < RepeatAfter)
            {
                return false;
            }

            _written[key] = now;
            if (_written.Count > 2000)
            {
                foreach (var old in _written.Where(w => now - w.Value >= RepeatAfter).Select(w => w.Key).ToList())
                {
                    _written.Remove(old);
                }
            }
        }

        try
        {
            await _write(note).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // A failure to write the Activity log mustn't affect filing
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
