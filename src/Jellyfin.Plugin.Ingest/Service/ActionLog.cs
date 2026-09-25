using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Ingest.Service;

/// <summary>
/// Keeps the action log (<c>actions.jsonl</c>, one line per file move) from growing forever: it is a record of what was
/// filed from where, so only recent history is kept. Interrupted moves are recovered before it is ever trimmed.
/// </summary>
public static class ActionLog
{
    /// <summary>How long entries are kept.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    /// <summary>The largest the log may grow; the oldest entries go first.</summary>
    public const long MaxBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Chooses which lines to keep: those newer than <paramref name="maxAge"/>, then the newest that fit in
    /// <paramref name="maxBytes"/>. Lines that can't be read are dropped.
    /// </summary>
    /// <param name="lines">The log's lines, oldest first.</param>
    /// <param name="now">Current time.</param>
    /// <param name="maxAge">How long entries are kept.</param>
    /// <param name="maxBytes">Size limit in UTF-8 bytes.</param>
    /// <returns>The lines to keep, oldest first.</returns>
    public static IReadOnlyList<string> Trim(IEnumerable<string> lines, DateTimeOffset now, TimeSpan maxAge, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var recent = lines.Where(l => TimeOf(l) is { } t && now - t <= maxAge).ToList();
        var keep = new List<string>();
        long size = 0;
        for (var i = recent.Count - 1; i >= 0; i--)
        {
            size += Encoding.UTF8.GetByteCount(recent[i]) + 1;
            if (size > maxBytes)
            {
                break;
            }

            keep.Add(recent[i]);
        }

        keep.Reverse();
        return keep;
    }

    /// <summary>
    /// Trims the log file in place (write a temporary file, then replace) if anything is to go.
    /// </summary>
    /// <param name="path">The log file.</param>
    /// <param name="now">Current time.</param>
    /// <returns>How many lines were removed.</returns>
    public static int TrimFile(string path, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return 0;
        }

        var lines = File.ReadAllLines(path);
        var keep = Trim(lines, now, MaxAge, MaxBytes);
        if (keep.Count == lines.Length)
        {
            return 0;
        }

        var temp = path + ".tmp";
        File.WriteAllLines(temp, keep);
        File.Move(temp, path, overwrite: true);
        return lines.Length - keep.Count;
    }

    private static DateTimeOffset? TimeOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
                ? time
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
