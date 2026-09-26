using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ingest.Planning;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Decides when a dropped release has finished arriving. A release is the top-level file or folder in a watch folder;
/// it is "settled" once its file list and sizes have stayed unchanged for the settle time. A release that was already
/// handled (e.g. left for review) is not offered again until its contents change.
/// </summary>
public sealed partial class ReleaseTracker
{
    // File-system, NAS and OS housekeeping entries that are never media releases
    private static readonly HashSet<string> SystemEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "lost+found", "$RECYCLE.BIN", "RECYCLER", "System Volume Information", "@eaDir", "@Recycle", "@Recently-Snapshot",
        "#recycle", "#snapshot", "Thumbs.db", "desktop.ini", "Network Trash Folder", "Temporary Items",
    };

    private readonly Dictionary<string, (string Signature, DateTimeOffset Since)> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _handled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _downloading = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns whether a top-level entry of a watch folder should be ignored entirely (hidden files and folders such as
    /// <c>.Trash-1000</c>, file-system / NAS / OS housekeeping such as <c>lost+found</c>, <c>$RECYCLE.BIN</c> or
    /// <c>@eaDir</c>, and in-progress downloads).
    /// </summary>
    /// <param name="name">The entry's name.</param>
    /// <returns><c>true</c> to ignore it.</returns>
    public static bool IsIgnored(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith('.') || SystemEntries.Contains(name) || PartialDownload().IsMatch(name);
    }

    /// <summary>
    /// A stable fingerprint of a release's contents: paths, sizes and last-write times. Any write changes it, so a release
    /// only settles once nothing has been written for the whole settle time, even when files were created at full size
    /// (pre-allocation, sparse files). Times are compared only with their own earlier values, never with the server's
    /// clock, so clock differences on network shares don't matter.
    /// </summary>
    /// <param name="files">The release's files.</param>
    /// <returns>The signature.</returns>
    public static string Signature(IEnumerable<ReleaseFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            sb.Append(f.RelativePath).Append('\u0001').Append(f.Size.ToString(CultureInfo.InvariantCulture))
                .Append('\u0001').Append((f.LastWriteUtc?.Ticks ?? 0).ToString(CultureInfo.InvariantCulture)).Append('\u0002');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Records the current contents of the watch folder and returns the releases that are ready to process.
    /// </summary>
    /// <param name="snapshot">Release name → its files, as found by this sweep.</param>
    /// <param name="now">Current time.</param>
    /// <param name="settle">How long contents must stay unchanged.</param>
    /// <returns>Names of settled, not-yet-handled releases.</returns>
    public IReadOnlyList<string> Observe(IReadOnlyDictionary<string, IReadOnlyList<ReleaseFile>> snapshot, DateTimeOffset now, TimeSpan settle)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // forget releases that disappeared (moved away, deleted)
        foreach (var gone in _seen.Keys.Where(k => !snapshot.ContainsKey(k)).ToList())
        {
            _seen.Remove(gone);
            _handled.Remove(gone);
        }

        _downloading.RemoveWhere(d => !snapshot.ContainsKey(d));

        var ready = new List<string>();
        foreach (var (name, files) in snapshot)
        {
            if (files.Count == 0 || files.Any(f => PartialDownload().IsMatch(f.RelativePath)))
            {
                _seen.Remove(name);
                if (files.Count > 0)
                {
                    _downloading.Add(name);
                }

                continue;
            }

            _downloading.Remove(name);

            var sig = Signature(files);
            if (!_seen.TryGetValue(name, out var prev) || !string.Equals(prev.Signature, sig, StringComparison.Ordinal))
            {
                _seen[name] = (sig, now);
                continue;
            }

            if (now - prev.Since >= settle && !(_handled.TryGetValue(name, out var done) && string.Equals(done, sig, StringComparison.Ordinal)))
            {
                ready.Add(name);
            }
        }

        return ready;
    }

    /// <summary>
    /// The releases seen but not yet handled, as of the last <see cref="Observe"/>: still arriving, settling, or settled
    /// and about to be processed (ING-36).
    /// </summary>
    /// <param name="settle">How long contents must stay unchanged.</param>
    /// <returns>Each release and when it settles (<c>null</c> while it still holds in-progress download files).</returns>
    public IReadOnlyList<(string Release, DateTimeOffset? SettlesAt)> Pending(TimeSpan settle)
    {
        var pending = _seen
            .Where(s => !(_handled.TryGetValue(s.Key, out var done) && string.Equals(done, s.Value.Signature, StringComparison.Ordinal)))
            .Select(s => (s.Key, (DateTimeOffset?)(s.Value.Since == DateTimeOffset.MinValue ? DateTimeOffset.MinValue : s.Value.Since + settle)))
            .Concat(_downloading.Select(d => (d, (DateTimeOffset?)null)));
        return [.. pending.OrderBy(p => p.Item1, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Skips the rest of a release's settle wait ("Process now"): it is offered on the next <see cref="Observe"/> if
    /// nothing in it has changed since the last one.
    /// </summary>
    /// <param name="name">Release name.</param>
    /// <returns>Whether the release is being watched (a release still holding in-progress download files isn't).</returns>
    public bool SettleNow(string name)
    {
        if (!_seen.TryGetValue(name, out var s))
        {
            return false;
        }

        _seen[name] = (s.Signature, DateTimeOffset.MinValue);
        return true;
    }

    /// <summary>
    /// Marks a release as handled in its current state (it won't be offered again unless it changes).
    /// </summary>
    /// <param name="name">Release name.</param>
    public void MarkHandled(string name)
    {
        if (_seen.TryGetValue(name, out var s))
        {
            _handled[name] = s.Signature;
        }
    }

    /// <summary>
    /// Offers a handled release again on the next sweep even though it hasn't changed (e.g. after a review decision).
    /// </summary>
    /// <param name="name">Release name.</param>
    public void Forget(string name) => _handled.Remove(name);

    /// <summary>
    /// Offers every handled release again on the next sweep (e.g. after dry run is turned off or destinations change).
    /// </summary>
    public void ForgetAll() => _handled.Clear();

    [GeneratedRegex(@"\.(?:part|partial|!qb|!ut|crdownload|tmp|temp|download)$|^~\$", RegexOptions.IgnoreCase)]
    private static partial Regex PartialDownload();
}
