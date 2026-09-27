using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Ingest.Presentation;

/// <summary>
/// One row of a list on the plugin page, ready to show: a headline, a smaller line under it, one friendly sentence,
/// icon chips, and the technical details for the row's expandable part.
/// </summary>
public sealed record ItemView
{
    /// <summary>Gets a stable key for the row (kept across refreshes, so an expanded row stays expanded).</summary>
    public required string Key { get; init; }

    /// <summary>Gets the headline, e.g. <c>Lantern S01E04</c> or <c>Harbour Lights (2024)</c>.</summary>
    public required string Headline { get; init; }

    /// <summary>Gets the smaller line under the headline (episode title, library …); empty when there is nothing to add.</summary>
    public string Subline { get; init; } = string.Empty;

    /// <summary>Gets one friendly sentence saying what happened or what is needed.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Gets the status icon (see <see cref="Icons"/>); empty for none.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Gets the status in words (the icon's tooltip and accessible name).</summary>
    public string StatusLabel { get; init; } = string.Empty;

    /// <summary>Gets the row's tone for colour: <c>ok</c>, <c>info</c>, <c>warn</c>, <c>error</c> or <c>neutral</c>.</summary>
    public string Tone { get; init; } = "neutral";

    /// <summary>Gets the icon chips with counts (what was filed, quarantined, replaced …).</summary>
    public IReadOnlyList<Chip> Chips { get; init; } = [];

    /// <summary>Gets the technical details shown when the row is expanded, in groups.</summary>
    public IReadOnlyList<DetailRow> Details { get; init; } = [];

    /// <summary>Gets, for a Needs review card, the title in use and how each candidate relates to it; <c>null</c> elsewhere.</summary>
    public ReviewMatchView? Match { get; init; }
}

/// <summary>
/// How a candidate on a review card relates to the title the review is using.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CandidateState>))]
public enum CandidateState
{
    /// <summary>No title is in use yet: one of these may be it ("Use this").</summary>
    Option = 0,

    /// <summary>The title Ingest is using ("Using this").</summary>
    InUse,

    /// <summary>Another title, offered in case the one in use is wrong ("Use this instead").</summary>
    Alternative,
}

/// <summary>
/// The decision part of a Needs review card: which title Ingest is using (if any) and what it found for it, and the
/// state of each candidate in the card's lists (same order as the review's candidates and search results).
/// </summary>
public sealed record ReviewMatchView
{
    /// <summary>Gets the key of the title in use (<see cref="Service.ReviewChoice.CurrentMatchKey"/>), sent back with a replace; <c>null</c> when none is.</summary>
    public string? CurrentKey { get; init; }

    /// <summary>Gets the title in use, e.g. <c>Lantern (2001)</c>; empty when none is.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the decision sentence, e.g. <c>Matched to Lantern (2001). 4 of these episodes are already in Shows.</c>; empty when no title is in use.</summary>
    public string Headline { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether a title was chosen but not yet checked against the server (the next sweep does).</summary>
    public bool Checking { get; init; }

    /// <summary>Gets the heading over the candidates: <c>Is it one of these?</c>, or <c>Wrong show? Pick another</c> when a title is in use.</summary>
    public string PickHeading { get; init; } = "Is it one of these?";

    /// <summary>Gets each suggested candidate's state.</summary>
    public IReadOnlyList<CandidateState> Candidates { get; init; } = [];

    /// <summary>Gets each search result's state.</summary>
    public IReadOnlyList<CandidateState> SearchResults { get; init; } = [];
}

/// <summary>
/// An icon with a count and what it means.
/// </summary>
/// <param name="Icon">The icon (see <see cref="Icons"/>).</param>
/// <param name="Count">The count; 0 shows the icon alone.</param>
/// <param name="Label">What it means, in words (tooltip and accessible name), e.g. <c>3 subtitles filed</c>.</param>
public sealed record Chip(string Icon, int Count, string Label);

/// <summary>
/// A labelled value in a row's details.
/// </summary>
/// <param name="Group">The group it is shown under, e.g. <c>Release</c> or <c>Files</c>.</param>
/// <param name="Label">The label.</param>
/// <param name="Value">The value.</param>
public sealed record DetailRow(string Group, string Label, string Value);

/// <summary>
/// A Recent activity row.
/// </summary>
public sealed record ActivityView
{
    /// <summary>Gets what to show.</summary>
    public required ItemView Item { get; init; }

    /// <summary>Gets when it happened.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Gets the entry's status (as stored), for filtering.</summary>
    public required string Status { get; init; }

    /// <summary>Gets the filing's run, for Undo; <c>null</c> for anything that isn't a filing.</summary>
    public string? Run { get; init; }

    /// <summary>Gets a value indicating whether Undo can be offered (a filing with a run, not undone, within 90 days).</summary>
    public bool CanUndo { get; init; }

    /// <summary>Gets when the filing was undone, if it was.</summary>
    public DateTimeOffset? UndoneAt { get; init; }
}

/// <summary>
/// A page of Recent activity.
/// </summary>
public sealed record ActivityPage
{
    /// <summary>Gets the rows on this page, newest first.</summary>
    public IReadOnlyList<ActivityView> Items { get; init; } = [];

    /// <summary>Gets how many rows match the filter.</summary>
    public int Total { get; init; }

    /// <summary>Gets where this page starts.</summary>
    public int Offset { get; init; }

    /// <summary>Gets the page size asked for (after clamping).</summary>
    public int Limit { get; init; }

    /// <summary>Gets a value indicating whether there are rows after this page.</summary>
    public bool HasMore => Offset + Items.Count < Total;

    /// <summary>Gets how many entries there are of each status (and <c>All</c>), for the filter buttons.</summary>
    public IReadOnlyDictionary<string, int> StatusCounts { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>
/// A quarantined release (or single file), as a row of the Quarantine list.
/// </summary>
public sealed record QuarantineView
{
    /// <summary>Gets what to show.</summary>
    public required ItemView Item { get; init; }

    /// <summary>Gets the quarantine folder it is in.</summary>
    public required string Root { get; init; }

    /// <summary>Gets the dated folder's name.</summary>
    public required string Folder { get; init; }

    /// <summary>Gets the release's name in the dated folder (what Restore and Delete now are given).</summary>
    public required string Name { get; init; }

    /// <summary>Gets the date it was quarantined (<c>yyyy-MM-dd</c>).</summary>
    public required string Date { get; init; }

    /// <summary>Gets a value indicating whether Ingest created the dated folder (only then can it be restored or deleted).</summary>
    public bool Managed { get; init; }

    /// <summary>Gets how many files it holds.</summary>
    public int FileCount { get; init; }

    /// <summary>Gets its size in bytes.</summary>
    public long Bytes { get; init; }
}

/// <summary>
/// A dated quarantine folder's totals, for the Quarantine list's day headings.
/// </summary>
public sealed record QuarantineDay
{
    /// <summary>Gets the quarantine folder it is in.</summary>
    public required string Root { get; init; }

    /// <summary>Gets the dated folder's name.</summary>
    public required string Folder { get; init; }

    /// <summary>Gets the date (<c>yyyy-MM-dd</c>).</summary>
    public required string Date { get; init; }

    /// <summary>Gets a value indicating whether Ingest created it.</summary>
    public bool Managed { get; init; }

    /// <summary>Gets the first day the purge deletes it, or <c>null</c> if it never will.</summary>
    public DateOnly? DeletesOn { get; init; }

    /// <summary>Gets how many releases it holds.</summary>
    public int Releases { get; init; }

    /// <summary>Gets how many files it holds.</summary>
    public int FileCount { get; init; }

    /// <summary>Gets its size in bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets its size in words, e.g. <c>1.2 MB</c>.</summary>
    public string Size { get; init; } = string.Empty;
}

/// <summary>
/// A page of the Quarantine list: releases across the dated folders, newest day first.
/// </summary>
public sealed record QuarantinePage
{
    /// <summary>Gets the rows on this page.</summary>
    public IReadOnlyList<QuarantineView> Items { get; init; } = [];

    /// <summary>Gets how many rows there are in all.</summary>
    public int Total { get; init; }

    /// <summary>Gets where this page starts.</summary>
    public int Offset { get; init; }

    /// <summary>Gets the page size asked for (after clamping).</summary>
    public int Limit { get; init; }

    /// <summary>Gets a value indicating whether there are rows after this page.</summary>
    public bool HasMore => Offset + Items.Count < Total;

    /// <summary>Gets every dated folder (not only this page's), newest first.</summary>
    public IReadOnlyList<QuarantineDay> Days { get; init; } = [];

    /// <summary>Gets how many files the whole quarantine holds.</summary>
    public int FileCount { get; init; }

    /// <summary>Gets the whole quarantine's size in words.</summary>
    public string Size { get; init; } = string.Empty;
}

/// <summary>
/// Paging for the page's lists: an offset and a page size, both clamped.
/// </summary>
public static class Paging
{
    /// <summary>The page size used when none is given.</summary>
    public const int DefaultLimit = 15;

    /// <summary>The largest page size.</summary>
    public const int MaxLimit = 300;

    /// <summary>
    /// Takes one page of a list.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="rows">All rows, in order.</param>
    /// <param name="offset">Rows to skip (negative counts as 0).</param>
    /// <param name="limit">Rows to take (clamped to 1 … <see cref="MaxLimit"/>).</param>
    /// <returns>The page's rows, the offset and the limit used.</returns>
    public static (IReadOnlyList<T> Rows, int Offset, int Limit) Slice<T>(IReadOnlyList<T> rows, int offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var from = Math.Max(0, offset);
        var size = Math.Clamp(limit, 1, MaxLimit);
        return ([.. rows.Skip(from).Take(size)], from, size);
    }
}
