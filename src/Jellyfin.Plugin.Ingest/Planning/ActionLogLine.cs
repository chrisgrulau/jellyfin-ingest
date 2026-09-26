using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// One line of the action log (<c>actions.jsonl</c>). Lines written before phases existed have no <see cref="Phase"/>
/// and count as done; lines written before runs existed have no <see cref="Run"/>, <see cref="Modified"/> or
/// <see cref="Kept"/>.
/// </summary>
public sealed record ActionLogLine
{
    /// <summary>Gets when it was written (ISO 8601).</summary>
    [JsonPropertyName("time")]
    public string Time { get; init; } = string.Empty;

    /// <summary>Gets the release's name.</summary>
    [JsonPropertyName("release")]
    public string Release { get; init; } = string.Empty;

    /// <summary>Gets the operation's kind (<see cref="OperationKind"/> as text).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets where the file was.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>Gets where it went.</summary>
    [JsonPropertyName("destination")]
    public string Destination { get; init; } = string.Empty;

    /// <summary>Gets its size.</summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    /// <summary>
    /// Gets the step: <c>intent</c>, <c>done</c>, <c>rolled-back</c>, <c>recovered</c>, <c>discarded</c> or
    /// <c>deleted</c> (a library copy removed by an undo).
    /// </summary>
    [JsonPropertyName("phase")]
    public string? Phase { get; init; }

    /// <summary>Gets the hidden temporary name the file passed through.</summary>
    [JsonPropertyName("temp")]
    public string? Temp { get; init; }

    /// <summary>Gets the id of the execution (one filing, undo or restore) the move belongs to.</summary>
    [JsonPropertyName("run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Run { get; init; }

    /// <summary>Gets the destination's last-write time once the move was done (ISO 8601, UTC), to tell whether it has changed since.</summary>
    [JsonPropertyName("modified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Modified { get; init; }

    /// <summary>Gets whether the source stayed where it was (a copy or hard link, for seeding).</summary>
    [JsonPropertyName("kept")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Kept { get; init; }

    /// <summary>Gets a value indicating whether the line records a completed move (<c>done</c>, a recovered one, or an old line without a phase).</summary>
    [JsonIgnore]
    public bool IsCompleted => Phase is null or "done" or "recovered";

    /// <summary>Gets the operation's kind, or <see cref="OperationKind.Video"/> when it can't be read.</summary>
    [JsonIgnore]
    public OperationKind OperationKind => Enum.TryParse<OperationKind>(Kind, out var k) ? k : OperationKind.Video;

    /// <summary>Gets <see cref="Modified"/> as a time, if it was recorded and can be read.</summary>
    [JsonIgnore]
    public DateTime? ModifiedUtc => Modified is { Length: > 0 } m
        && DateTime.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;

    /// <summary>
    /// Reads a line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The entry, or <c>null</c> if it can't be read.</returns>
    public static ActionLogLine? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ActionLogLine>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The moves an execution completed and didn't undo, in the order they were made.
    /// </summary>
    /// <param name="lines">The action log's lines, oldest first.</param>
    /// <param name="run">The execution's id.</param>
    /// <returns>The completed moves.</returns>
    public static IReadOnlyList<ActionLogLine> CompletedMoves(IEnumerable<string> lines, string run)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);
        var ofRun = lines.Select(TryParse).OfType<ActionLogLine>().Where(l => string.Equals(l.Run, run, StringComparison.Ordinal)).ToList();
        var rolledBack = ofRun.Where(l => l.Phase == "rolled-back" && l.Temp is not null).Select(l => l.Temp!).ToHashSet(StringComparer.Ordinal);
        return [.. ofRun.Where(l => l.IsCompleted && (l.Temp is null || !rolledBack.Contains(l.Temp)))];
    }
}
