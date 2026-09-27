using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Quarantine;
using Jellyfin.Plugin.Ingest.Service;

namespace Jellyfin.Plugin.Ingest.Presentation;

/// <summary>
/// Turns what Ingest records into rows for the plugin page's lists: what it is (headline and smaller line), one
/// friendly sentence, icon chips, and the technical details for the row's expandable part. Plain C#, no Jellyfin
/// types, so every list reads the same and is unit-tested; the page only lays the rows out.
/// </summary>
public sealed partial class IngestPresenter
{
    private const string ReleaseGroup = "Release";
    private const string FilesGroup = "Files";

    private readonly IReadOnlyList<MediaLibrary> _libraries;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestPresenter"/> class.
    /// </summary>
    /// <param name="libraries">The server's libraries, to say which library a file went to.</param>
    public IngestPresenter(IReadOnlyList<MediaLibrary> libraries)
    {
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
    }

    /// <summary>
    /// A page of Recent activity, optionally only one status.
    /// </summary>
    /// <param name="activity">All entries, newest first.</param>
    /// <param name="status">A status name to keep only those (<c>All</c>, empty or <c>null</c> for all).</param>
    /// <param name="offset">Rows to skip.</param>
    /// <param name="limit">Rows to take.</param>
    /// <param name="now">Now.</param>
    /// <returns>The page.</returns>
    public ActivityPage ActivityPage(IReadOnlyList<ActivityEntry> activity, string? status, int offset, int limit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var all = string.IsNullOrEmpty(status) || string.Equals(status, "All", StringComparison.OrdinalIgnoreCase);
        List<ActivityEntry> matching = all ? [.. activity] : [.. activity.Where(a => string.Equals(a.Status.ToString(), status, StringComparison.OrdinalIgnoreCase))];
        var (rows, from, size) = Paging.Slice(matching, offset, limit);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["All"] = activity.Count };
        foreach (var group in activity.GroupBy(a => a.Status.ToString()))
        {
            counts[group.Key] = group.Count();
        }

        return new ActivityPage
        {
            Items = [.. rows.Select(a => Activity(a, now, activity))],
            Total = matching.Count,
            Offset = from,
            Limit = size,
            StatusCounts = counts,
        };
    }

    /// <summary>
    /// A Recent activity row.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="now">Now (for whether Undo is still possible).</param>
    /// <param name="activity">All of Recent activity (for whether an older filing's library was chosen in review), or
    /// <c>null</c> to go by the entry alone.</param>
    /// <returns>The row.</returns>
    public ActivityView Activity(ActivityEntry entry, DateTimeOffset now, IReadOnlyList<ActivityEntry>? activity = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var lines = Lines.Read(entry);
        // Where the videos went (an undo's moves run the other way, so either end may be the library's)
        var videos = entry.Videos ?? [.. lines.Moves.Where(m => m.Kind == "Video").Select(m => MediaTitle.FromFiledPath(m.To) is null && MediaTitle.FromFiledPath(m.From) is not null ? m.From : m.To)];
        var titles = videos.Select(MediaTitle.FromFiledPath).OfType<MediaTitle>().ToList();
        var library = videos.Select(LibraryOf).FirstOrDefault(l => l is not null);
        var (headline, subline) = Identity(titles, entry.Release);
        var counts = entry.Counts ?? lines.Count();
        var summary = SummaryOf(entry, lines, counts, library);
        var details = new List<DetailRow>();
        if (!string.Equals(summary, entry.Summary, StringComparison.Ordinal) && entry.Summary.Length > 0)
        {
            details.Add(new DetailRow("Message", "In full", entry.Summary));
        }

        details.Add(new DetailRow(ReleaseGroup, IsPath(entry.Release) ? "Folder" : "Release", entry.Release));
        if (!string.IsNullOrEmpty(entry.WatchFolder) && !string.Equals(entry.WatchFolder, entry.Release, StringComparison.Ordinal))
        {
            details.Add(new DetailRow(ReleaseGroup, "Watch folder", entry.WatchFolder));
        }

        if (entry.Run is not null)
        {
            details.Add(new DetailRow(ReleaseGroup, "Action id", entry.Run));
        }

        if (counts is not null && entry.Status is ActivityStatus.Filed or ActivityStatus.DryRun or ActivityStatus.Quarantined)
        {
            details.Add(new DetailRow("Counts", "Files", string.Create(CultureInfo.InvariantCulture, $"{counts.Videos} video, {counts.Subtitles} subtitle, {counts.Extras} extra, {counts.Clutter} clutter, {counts.Replaced} replaced, {counts.SetAside} set aside")));
        }

        details.AddRange(lines.Details(entry.Status));

        return new ActivityView
        {
            Time = entry.Time,
            Status = entry.Status.ToString(),
            Run = entry.Run,
            UndoneAt = entry.UndoneAt,
            CanUndo = entry.Status == ActivityStatus.Filed && entry.Run is not null && entry.UndoneAt is null && now - entry.Time <= ActionLog.MaxAge,
            CanMove = LibraryMove.Ineligible(entry, activity ?? [entry], now) is null,
            MovedTo = entry.MovedTo,
            Item = new ItemView
            {
                Key = KeyOf(entry.Run ?? string.Create(CultureInfo.InvariantCulture, $"{entry.Time.UtcTicks}|{entry.Status}|{entry.WatchFolder}|{entry.Release}")),
                Headline = headline,
                Subline = subline,
                Summary = summary,
                Icon = IconOf(entry.Status),
                StatusLabel = LabelOf(entry.Status) + (entry.UndoneAt is null ? string.Empty : ", then undone"),
                Tone = entry.UndoneAt is not null ? "neutral" : ToneOf(entry.Status),
                Chips = ChipsOf(entry, counts, lines),
                Details = details,
            },
        };
    }

    /// <summary>
    /// A Needs review card's heading, sentence, chips and details (the card keeps its own choices and buttons).
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns>The row.</returns>
    public ItemView Review(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var fromName = MediaTitle.FromReleaseName(review.Release);
        string headline;
        var subline = string.Empty;
        if (review.Chosen is { } chosen)
        {
            headline = Named(chosen.Candidate) + (fromName is { IsSeries: true, Season: { } s, Episode: { } e } && chosen.Candidate.IsSeries
                ? " " + Naming.MediaNamer.EpisodeCode(s, e, fromName.LastEpisode) : string.Empty);
            subline = "Chosen · into " + (LibraryAt(chosen.Target.Root) ?? "the chosen library");
        }
        else
        {
            (headline, subline) = Identity(fromName is null ? [] : [fromName], review.Release);
            if ((review.Matched ?? (review.Candidates.Count > 0 ? review.Candidates[0].Candidate : null)) is { } likely)
            {
                var top = Named(likely);
                if (!string.Equals(top, headline, StringComparison.OrdinalIgnoreCase))
                {
                    subline = (subline.Length > 0 ? subline + " · " : string.Empty) + "Looks like " + top;
                }
            }
        }

        var reasons = review.Items.Select(i => i.Reason).ToList();
        var summary = review.Request switch
        {
            ReviewRequest.Quarantine => "Quarantining it on the next sweep.",
            ReviewRequest.Replace => "Replacing what's on the server on the next sweep.",
            ReviewRequest.Retry => "Trying again on the next sweep.",
            _ when AiApproval.IsOnlyAi(review) => Friendly.EndSentence("The AI suggests " + AiApproval.Describe(review)) + " Approve it, or pick another.",
            _ => Friendly.EndSentence("Waiting for you: " + Friendly.Reasons(reasons)),
        };
        if (review.RetryAt is not null && review.Request == ReviewRequest.None && !summary.Contains("by itself", StringComparison.Ordinal))
        {
            summary += " Ingest also tries again by itself.";
        }

        var chips = new List<Chip>();
        if (review.Items.Any(i => i.Suggestion is not null))
        {
            chips.Add(new Chip(Icons.Ai, 0, "Suggested by the AI; waiting for your approval"));
        }

        if (review.Items.Count > 1)
        {
            chips.Add(new Chip(Icons.Review, review.Items.Count, MediaTitle.Plural(review.Items.Count, "file") + " waiting for a decision"));
        }

        var existing = review.Items.Sum(i => i.Existing.Length == 0 ? 0 : i.Existing.Split('\n').Length);
        if (existing > 0)
        {
            chips.Add(new Chip(Icons.Replaced, existing, MediaTitle.Plural(existing, "copy") + " already on the server"));
        }

        if (review.Candidates.Count > 0)
        {
            chips.Add(new Chip(Icons.Video, review.Candidates.Count, MediaTitle.Plural(review.Candidates.Count, "possible match")));
        }

        var details = new List<DetailRow>
        {
            new(ReleaseGroup, "Release", review.Release),
            new(ReleaseGroup, "Watch folder", review.WatchFolder),
        };
        details.AddRange(review.Items.Select(i => new DetailRow("Why it waits", LastSegment(i.Source), i.Reason)));
        details.AddRange(review.Items.Where(i => i.Existing.Length > 0).SelectMany(i => i.Existing.Split('\n')).Select(p => new DetailRow("Already on the server", LibraryOf(p) ?? "Library", p)));
        details.AddRange(review.Items.Where(i => i.Suggestion is not null).SelectMany(i => SuggestionDetails(i.Source, i.Suggestion!)));
        details.AddRange(review.Candidates.Select(c => new DetailRow("Candidates", Named(c.Candidate), CandidateStats(c))));
        return new ItemView
        {
            Key = review.Id,
            Headline = headline,
            Subline = subline,
            Summary = summary,
            Icon = Icons.Review,
            StatusLabel = "Needs review",
            Tone = "warn",
            Chips = chips,
            Details = details,
            Match = Match(review),
        };
    }

    /// <summary>
    /// The decision part of a Needs review card: the title in use (chosen, or matched by the last plan) and what was
    /// found for it, and whether each candidate is that title, another one to switch to, or (with none in use) an option.
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns>The view.</returns>
    public ReviewMatchView Match(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var current = ReviewChoice.CurrentMatch(review);
        IReadOnlyList<CandidateState> States(IReadOnlyList<ScoredCandidate> list)
            => [.. list.Select(c => current is null ? CandidateState.Option : ReviewChoice.SameTitle(c.Candidate, current) ? CandidateState.InUse : CandidateState.Alternative)];
        var ai = Suggestion(review);
        if (current is null)
        {
            return new ReviewMatchView { Candidates = States(review.Candidates), SearchResults = States(review.SearchResults), Ai = ai, PickHeading = ai is null ? "Is it one of these?" : "Wrong? Pick another" };
        }

        var title = Named(current);
        var checking = !ReviewChoice.IsAssessed(review);
        var headline = ai is null ? "Matched to " + title + "." : "Suggested by the AI: " + ai.Title + ".";
        if (checking)
        {
            headline += " Ingest looks for copies of it on the server on the next sweep.";
        }
        else if (review.Items.Count(i => i.Existing.Length > 0) is var held and > 0)
        {
            var libraries = review.Items.Where(i => i.Existing.Length > 0)
                .SelectMany(i => i.Existing.Split('\n'))
                .Select(p => LibraryOf(p) ?? "a library")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var where = libraries.Count == 1 ? libraries[0] : string.Join(", ", libraries.Take(libraries.Count - 1)) + " and " + libraries[^1];
            var noun = current.IsSeries ? "episode" : "file";
            headline += " " + (held == 1 && review.Items.Count == 1
                ? (current.IsSeries ? "This episode is" : "It is") + " already in " + where + "."
                : held == review.Items.Count
                    ? string.Create(CultureInfo.InvariantCulture, $"{held} of these {noun}s are already in {where}.")
                    : string.Create(CultureInfo.InvariantCulture, $"{held} of these {review.Items.Count} {noun}s are already in {where}."));
        }

        return new ReviewMatchView
        {
            CurrentKey = ReviewChoice.CurrentMatchKey(review),
            Title = title,
            Headline = headline,
            Checking = checking,
            PickHeading = ai is not null ? "Wrong? Pick another" : current.IsSeries ? "Wrong show? Pick another" : "Wrong film? Pick another",
            Candidates = States(review.Candidates),
            SearchResults = States(review.SearchResults),
            Ai = ai,
        };
    }

    /// <summary>
    /// The AI plugin's suggestion waiting for approval on a review, as the card shows it: the first suggested file's
    /// reason, what it chose from and how closely the name matched (the others are in the details).
    /// </summary>
    /// <param name="review">The review.</param>
    /// <returns>The view, or <c>null</c> when the review holds no suggestion.</returns>
    public static AiSuggestionView? Suggestion(PendingReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var suggested = review.Items.Where(i => i.Suggestion is not null).Select(i => i.Suggestion!).ToList();
        if (AiApproval.KeyOf(review) is not { } key || suggested.Count == 0)
        {
            return null;
        }

        var first = suggested[0];
        var decision = first.Decisions.FirstOrDefault(d => d.Kind == AiDecisionKind.Transcript) ?? (first.Decisions.Count > 0 ? first.Decisions[0] : null);
        return new AiSuggestionView
        {
            Key = key,
            Title = AiApproval.Describe(review),
            By = decision?.By ?? string.Empty,
            Reason = string.Join(" ", first.Decisions.Select(d => d.Reason).Where(r => r.Length > 0)),
            Basis = string.Join(" ", first.Decisions.Select(Basis)),
            Confidence = decision is null ? string.Empty : ConfidenceOf(decision),
            Episode = first.Decisions.All(d => d.Kind != AiDecisionKind.Match),
            More = suggested.Count - 1,
            OnlyAi = AiApproval.IsOnlyAi(review),
            Replaces = review.Items.All(i => i.Existing.Length > 0),
        };
    }

    // What the AI chose from, in words: "Picked from 3 close candidates." or, for a transcript, how much was heard where
    private static string Basis(AiDecision d) => d.Kind switch
    {
        AiDecisionKind.Match => string.Create(CultureInfo.InvariantCulture, $"Picked {d.Picked} from {MediaTitle.Plural(d.Options, "close candidate")}."),
        AiDecisionKind.EpisodeByTitle => string.Create(CultureInfo.InvariantCulture, $"Picked {d.Picked} from {MediaTitle.Plural(d.Options, "episode")} of the season, by the episode title in the name."),
        _ => string.Create(CultureInfo.InvariantCulture, $"Picked {d.Picked} from {MediaTitle.Plural(d.Options, "episode")} by comparing {TranscriptWords(d)} with their synopses."),
    };

    private static string TranscriptWords(AiDecision d)
    {
        var size = d.TranscriptCharacters is { } n ? string.Create(CultureInfo.InvariantCulture, $"{n:N0} characters of transcript") : "a short transcript";
        return d.TranscriptFrom is { } from ? size + string.Create(CultureInfo.InvariantCulture, $" (from {(int)from.TotalMinutes}:{from.Seconds:00} in)") : size;
    }

    private static string ConfidenceOf(AiDecision d)
        => d.Confidence is { } c
            ? string.Create(CultureInfo.InvariantCulture, $"{(d.Kind == AiDecisionKind.EpisodeByTitle ? "Episode title match" : "Name match")} {Math.Round(Math.Min(1, c) * 100):0}%")
            : "The name says nothing about the episode; the AI went by what is said in it.";

    // One file's suggestion in the card's details: what, by whom, why, from what; a transcript only as its size, where
    // it starts and its first words
    private static IEnumerable<DetailRow> SuggestionDetails(string source, AiSuggestion suggestion)
    {
        const string Group = "AI suggestion";
        yield return new DetailRow(Group, LastSegment(source), suggestion.Describe());
        foreach (var d in suggestion.Decisions)
        {
            yield return new DetailRow(Group, "Decided by", d.By);
            if (d.Reason.Length > 0)
            {
                yield return new DetailRow(Group, "Why", d.Reason);
            }

            yield return new DetailRow(Group, "Chose from", Basis(d));
            yield return new DetailRow(Group, "Name match", ConfidenceOf(d));
            if (d.TranscriptSnippet is { Length: > 0 } snippet)
            {
                yield return new DetailRow(Group, "Transcript begins", "“" + snippet + "”");
            }
        }
    }

    /// <summary>
    /// A Waiting row (the page adds when it settles, which changes by the minute).
    /// </summary>
    /// <param name="waiting">The waiting release.</param>
    /// <returns>The row.</returns>
    public static ItemView Waiting(WaitingRelease waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        var title = MediaTitle.FromReleaseName(waiting.Release);
        var (headline, subline) = Identity(title is null ? [] : [title], waiting.Release);
        return new ItemView
        {
            Key = waiting.Id,
            Headline = headline,
            Subline = subline,
            Summary = waiting.SettlesAt is null ? "Still downloading." : waiting.ProcessNow ? "Processing on the next sweep." : "Waiting until nothing in it changes.",
            Icon = Icons.Waiting,
            StatusLabel = waiting.SettlesAt is null ? "Downloading" : "Settling",
            Tone = "info",
            Details = [new(ReleaseGroup, "Release", waiting.Release), new(ReleaseGroup, "Watch folder", waiting.WatchFolder)],
        };
    }

    /// <summary>
    /// The row for the release being identified or filed now.
    /// </summary>
    /// <param name="working">The work in progress.</param>
    /// <returns>The row.</returns>
    public static ItemView Working(WorkInProgress working)
    {
        ArgumentNullException.ThrowIfNull(working);
        var title = MediaTitle.FromReleaseName(working.Release);
        var (headline, subline) = Identity(title is null ? [] : [title], working.Release);
        return new ItemView
        {
            Key = working.Id,
            Headline = headline,
            Subline = subline,
            Summary = working.Stage == "Filing" && working.Files > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Filing file {working.File} of {working.Files}…")
                : "Identifying…",
            Icon = Icons.Working,
            StatusLabel = "Working on it now",
            Tone = "info",
            Details = [new(ReleaseGroup, "Release", working.Release), new(ReleaseGroup, "Watch folder", working.WatchFolder)],
        };
    }

    /// <summary>
    /// A page of the Quarantine list: its releases across the dated folders, newest day first.
    /// </summary>
    /// <param name="folders">The dated folders, newest first (see <see cref="QuarantineListing"/>).</param>
    /// <param name="offset">Rows to skip.</param>
    /// <param name="limit">Rows to take.</param>
    /// <returns>The page.</returns>
    public static QuarantinePage QuarantinePage(IReadOnlyList<QuarantineFolder> folders, int offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(folders);
        List<(QuarantineFolder Folder, QuarantinedRelease Release)> all = [.. folders.SelectMany(f => f.Releases.Select(r => (f, r)))];
        var (rows, from, size) = Paging.Slice(all, offset, limit);
        return new QuarantinePage
        {
            Items = [.. rows.Select(r => Quarantined(r.Folder, r.Release))],
            Total = all.Count,
            Offset = from,
            Limit = size,
            Days = [.. folders.Select(f => new QuarantineDay
            {
                Root = f.Root,
                Folder = f.Folder.Length > 0 ? f.Folder : f.Date,
                Date = f.Date,
                Managed = f.Managed,
                DeletesOn = f.DeletesOn,
                Releases = f.Releases.Count,
                FileCount = f.FileCount,
                Bytes = f.Bytes,
                Size = Friendly.Size(f.Bytes),
            })],
            FileCount = folders.Sum(f => f.FileCount),
            Size = Friendly.Size(folders.Sum(f => f.Bytes)),
        };
    }

    /// <summary>
    /// A quarantined release's row.
    /// </summary>
    /// <param name="folder">Its dated folder.</param>
    /// <param name="release">The release.</param>
    /// <returns>The row.</returns>
    public static QuarantineView Quarantined(QuarantineFolder folder, QuarantinedRelease release)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(release);
        var roles = release.Files.Select(f => ReleaseClassifier.Classify(new ReleaseFile(f.Path, f.Bytes))).ToList();
        var videos = roles.Count(r => r == FileRole.Video);
        var subtitles = roles.Count(r => r == FileRole.Subtitle);
        var replaced = string.Equals(release.Name, "Replaced", StringComparison.Ordinal);
        string headline, subline;
        if (replaced)
        {
            var titles = release.Files.Select(f => MediaTitle.FromFiledPath(f.Path)).OfType<MediaTitle>().ToList();
            headline = "Replaced copies";
            subline = MediaTitle.Describe(titles)?.Headline ?? string.Empty;
        }
        else
        {
            var title = MediaTitle.FromReleaseName(release.Name);
            (headline, subline) = Identity(title is null ? [] : [title], release.Name);
        }

        var size = Friendly.Size(release.Bytes);
        var chips = new List<Chip> { new(Icons.Quarantine, release.FileCount, MediaTitle.Plural(release.FileCount, "file") + ", " + size) };
        if (videos > 0)
        {
            chips.Add(new Chip(Icons.Video, videos, MediaTitle.Plural(videos, "video") + (replaced ? " replaced by a new copy" : " among them")));
        }

        if (subtitles > 0)
        {
            chips.Add(new Chip(Icons.Subtitles, subtitles, MediaTitle.Plural(subtitles, "subtitle")));
        }

        var details = new List<DetailRow>
        {
            new(ReleaseGroup, replaced ? "Folder" : "Release", release.Name),
            new(ReleaseGroup, "Quarantine folder", folder.Root),
            new(ReleaseGroup, "Dated folder", folder.Folder.Length > 0 ? folder.Folder : folder.Date),
        };
        details.AddRange(release.Files.Select(f => new DetailRow(FilesGroup, f.Path, Friendly.Size(f.Bytes))));
        if (release.FileCount > release.Files.Count)
        {
            details.Add(new DetailRow(FilesGroup, "…", "and " + MediaTitle.Plural(release.FileCount - release.Files.Count, "more file")));
        }

        return new QuarantineView
        {
            Root = folder.Root,
            Folder = folder.Folder.Length > 0 ? folder.Folder : folder.Date,
            Name = release.Name,
            Date = folder.Date,
            Managed = folder.Managed,
            FileCount = release.FileCount,
            Bytes = release.Bytes,
            Item = new ItemView
            {
                Key = KeyOf(folder.Root + "|" + folder.Folder + "|" + release.Name),
                Headline = headline,
                Subline = subline,
                Summary = replaced
                    ? "Copies that were on the server before a new one replaced them."
                    : videos > 0 ? "The whole release, set aside." : "Clutter set aside when the release was filed.",
                Icon = replaced ? Icons.Replaced : Icons.Quarantine,
                StatusLabel = replaced ? "Replaced copies" : "Quarantined",
                Chips = chips,
                Details = details,
            },
        };
    }

    /// <summary>
    /// Which of the server's libraries a path is in, by name (the deepest library folder holding it).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The library's name, or <c>null</c>.</returns>
    public string? LibraryOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return _libraries
            .SelectMany(l => l.Locations.Select(loc => (l.Name, Location: loc)))
            .Where(x => x.Location.Length > 0 && PathGuard.IsUnder(path, x.Location))
            .OrderByDescending(x => x.Location.Length)
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    private string? LibraryAt(string folder)
        => _libraries.FirstOrDefault(l => l.Locations.Any(loc => PathGuard.SamePath(loc, folder)))?.Name ?? LibraryOf(folder);

    private static (string Headline, string Subline) Identity(IReadOnlyList<MediaTitle> titles, string release)
    {
        if (MediaTitle.Describe(titles) is { } described)
        {
            return described;
        }

        if (IsPath(release))
        {
            return (LastSegment(release), string.Empty);
        }

        return MediaTitle.FromReleaseName(release) is { } parsed ? MediaTitle.Describe([parsed])!.Value : (release, string.Empty);
    }

    private static string SummaryOf(ActivityEntry entry, Lines lines, ActivityCounts? counts, string? library)
    {
        var into = library is null ? "the library" : library;
        switch (entry.Status)
        {
            case ActivityStatus.Filed when entry.Run is null && entry.Videos is null && lines.Moves.Count == 0:
                return Friendly.FirstSentence(entry.Summary);
            case ActivityStatus.Filed:
                var text = Friendly.EndSentence("Filed into " + into);
                if (counts is { Replaced: > 0 })
                {
                    text += counts.Replaced == 1 ? " It replaced the copy that was there." : " It replaced the copies that were there.";
                }

                if (counts is { SetAside: > 0 })
                {
                    text += " Some files were set aside, as you chose.";
                }

                if (entry.ChosenBy == FilingChoice.AiApproved)
                {
                    text += " You approved the AI's suggestion.";
                }
                else if (lines.Notes.Count > 0)
                {
                    text += " The AI plugin helped decide.";
                }

                if (entry.MovedTo is not null)
                {
                    text += " Later moved to " + entry.MovedTo + ".";
                }

                return entry.UndoneAt is null ? text : text + " Later undone.";
            case ActivityStatus.DryRun:
                return entry.Summary.StartsWith("Would quarantine", StringComparison.Ordinal)
                    ? "Dry run: the whole release would be quarantined. Nothing was moved."
                    : Friendly.EndSentence("Dry run: would be filed into " + into) + " Nothing was moved.";
            case ActivityStatus.NeedsReview:
                return Friendly.EndSentence("Waiting for you: " + (lines.Reasons.Count > 0 ? Friendly.Reasons(lines.Reasons) : Friendly.Reason(entry.Summary)));
            case ActivityStatus.Quarantined:
                var files = counts?.Clutter ?? lines.Moves.Count;
                return string.Create(CultureInfo.InvariantCulture, $"Moved the whole release to quarantine ({MediaTitle.Plural(files, "file")}).");
            case ActivityStatus.Undone:
                return entry.Summary.Contains("deleted", StringComparison.Ordinal)
                    ? "Undone: the library copies were removed, and it waits under Needs review."
                    : "Undone: it's back in the watch folder, waiting under Needs review.";
            case ActivityStatus.Moved:
                return Friendly.FirstSentence(entry.Summary).Replace(" by an administrator:", ":", StringComparison.Ordinal);
            case ActivityStatus.Restored:
                return "Restored from quarantine" + (entry.WatchFolder is null ? "." : "; it waits under Needs review.");
            default:
                return Friendly.FirstSentence(entry.Summary);
        }
    }

    private static List<Chip> ChipsOf(ActivityEntry entry, ActivityCounts? counts, Lines lines)
    {
        var chips = new List<Chip>();
        var verb = entry.Status == ActivityStatus.DryRun ? "would be filed" : "filed";
        if (counts is not null && entry.Status is ActivityStatus.Filed or ActivityStatus.DryRun or ActivityStatus.Quarantined)
        {
            Add(Icons.Video, counts.Videos, "video", verb);
            Add(Icons.Subtitles, counts.Subtitles, "subtitle", verb);
            Add(Icons.Extras, counts.Extras, "extra", verb);
            var quarantined = counts.Clutter + counts.SetAside;
            if (quarantined > 0)
            {
                chips.Add(new Chip(Icons.Quarantine, quarantined, MediaTitle.Plural(quarantined, "file") + (entry.Status == ActivityStatus.DryRun ? " would be" : string.Empty) + " quarantined"
                    + (counts.SetAside > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({counts.SetAside} as you chose)") : string.Empty)));
            }

            if (counts.Replaced > 0)
            {
                chips.Add(new Chip(Icons.Replaced, counts.Replaced, MediaTitle.Plural(counts.Replaced, "copy") + " on the server replaced"));
            }
        }

        if (entry.Status is ActivityStatus.Undone or ActivityStatus.Restored && lines.Moves.Count > 0)
        {
            chips.Add(new Chip(Icons.PutBack, lines.Moves.Count, MediaTitle.Plural(lines.Moves.Count, "file") + " put back"));
        }

        if (lines.Notes.Count > 0 && entry.Status is ActivityStatus.Filed or ActivityStatus.DryRun)
        {
            chips.Add(new Chip(Icons.Ai, 0, entry.ChosenBy == FilingChoice.AiApproved ? "Approved AI suggestion (see the details)" : "The AI plugin helped decide (see the details)"));
        }

        if (lines.Attention.Count > 0)
        {
            chips.Add(new Chip(Icons.Review, lines.Attention.Count, MediaTitle.Plural(lines.Attention.Count, "thing") + " need attention"));
        }

        if (entry.Status == ActivityStatus.Moved && lines.Moves.Count > 0)
        {
            chips.Add(new Chip(Icons.Moved, lines.Moves.Count, MediaTitle.Plural(lines.Moves.Count, "file") + " moved"));
        }

        if (entry.MovedTo is not null)
        {
            chips.Add(new Chip(Icons.Moved, 0, "Moved to " + entry.MovedTo));
        }

        if (entry.UndoneAt is not null)
        {
            chips.Add(new Chip(Icons.PutBack, 0, "Undone"));
        }

        return chips;

        void Add(string icon, int n, string noun, string how)
        {
            if (n > 0)
            {
                chips.Add(new Chip(icon, n, MediaTitle.Plural(n, noun) + " " + how));
            }
        }
    }

    private static string IconOf(ActivityStatus status) => status switch
    {
        ActivityStatus.Filed => Icons.Filed,
        ActivityStatus.DryRun => Icons.DryRun,
        ActivityStatus.NeedsReview => Icons.Review,
        ActivityStatus.Decision => Icons.Decision,
        ActivityStatus.Failed => Icons.Failed,
        ActivityStatus.Quarantined => Icons.Quarantine,
        ActivityStatus.Purged => Icons.Purged,
        ActivityStatus.ReviewRemoved => Icons.ReviewRemoved,
        ActivityStatus.Undone or ActivityStatus.Restored => Icons.PutBack,
        ActivityStatus.Moved => Icons.Moved,
        _ => string.Empty,
    };

    /// <summary>
    /// A status in words, as the filter buttons name it.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>The label.</returns>
    public static string LabelOf(ActivityStatus status) => status switch
    {
        ActivityStatus.DryRun => "Dry run",
        ActivityStatus.NeedsReview => "Needs review",
        ActivityStatus.Purged => "Deleted from quarantine",
        ActivityStatus.ReviewRemoved => "Review removed",
        _ => status.ToString(),
    };

    private static string ToneOf(ActivityStatus status) => status switch
    {
        ActivityStatus.Filed => "ok",
        ActivityStatus.DryRun => "info",
        ActivityStatus.NeedsReview => "warn",
        ActivityStatus.Failed => "error",
        _ => "neutral",
    };

    private static string Named(MetadataCandidate c) => c.Year is { } y ? string.Create(CultureInfo.InvariantCulture, $"{c.Name} ({y})") : c.Name;

    private static string CandidateStats(ScoredCandidate c)
    {
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"score {Math.Min(1, c.Score):0.00}"), c.Candidate.IsSeries ? "show" : "film" };
        parts.AddRange(c.Candidate.ProviderIds.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + " " + p.Value));
        if (!string.IsNullOrEmpty(c.Candidate.Source))
        {
            parts.Add("from " + c.Candidate.Source);
        }

        return string.Join(" · ", parts);
    }

    private static bool IsPath(string text) => text.StartsWith('/') || text.StartsWith(@"\\", StringComparison.Ordinal) || (text.Length > 2 && text[1] == ':' && text[2] is '\\' or '/');

    private static string LastSegment(string path) => path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;

    private static string KeyOf(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    // "Video: Release/a.mkv → /library/…/Show S01E04.mkv", as ActivityReport.Describe writes it
    [GeneratedRegex(@"^(?<k>Video|Subtitle|Extra|Quarantine): (?<from>.+?) → (?<to>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex KindMove();

    // "a → b", as a restore writes it
    [GeneratedRegex(@"^(?<from>.+?) → (?<to>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainMove();

    // "… and 12 more", added when an entry's details are capped
    [GeneratedRegex(@"^… and \d+ more$", RegexOptions.CultureInvariant)]
    private static partial Regex CappedLine();

    /// <summary>
    /// An activity entry's detail lines, sorted by what they are.
    /// </summary>
    private sealed class Lines
    {
        public List<(string Kind, string From, string To)> Moves { get; } = [];

        public List<string> Replaced { get; } = [];

        public List<string> SetAside { get; } = [];

        public List<string> Attention { get; } = [];

        public List<string> Notes { get; } = [];

        public List<(string File, string Reason)> ReviewItems { get; } = [];

        public List<string> Reasons => [.. ReviewItems.Select(r => r.Reason)];

        public List<string> Other { get; } = [];

        public static Lines Read(ActivityEntry entry)
        {
            var lines = new Lines();
            foreach (var line in entry.Details)
            {
                if (entry.Status == ActivityStatus.NeedsReview)
                {
                    // "file: reason"; the file is before the first ": " (never a Windows drive: it is relative)
                    var split = line.IndexOf(": ", StringComparison.Ordinal);
                    if (split > 0)
                    {
                        lines.ReviewItems.Add((line[..split], line[(split + 2)..]));
                        continue;
                    }
                }

                if (KindMove().Match(line) is { Success: true } km)
                {
                    lines.Moves.Add((km.Groups["k"].Value, km.Groups["from"].Value, km.Groups["to"].Value));
                }
                else if (line.StartsWith("Replaced (moved to quarantine): ", StringComparison.Ordinal))
                {
                    lines.Replaced.Add(line["Replaced (moved to quarantine): ".Length..]);
                }
                else if (line.StartsWith("Quarantined as chosen: ", StringComparison.Ordinal))
                {
                    lines.SetAside.Add(line["Quarantined as chosen: ".Length..]);
                }
                else if (line.StartsWith("Needs attention: ", StringComparison.Ordinal))
                {
                    lines.Attention.Add(line["Needs attention: ".Length..]);
                }
                else if (CappedLine().IsMatch(line))
                {
                    lines.Other.Add(line);
                }
                else if (PlainMove().Match(line) is { Success: true } pm)
                {
                    lines.Moves.Add(("File", pm.Groups["from"].Value, pm.Groups["to"].Value));
                }
                else if (entry.Status is ActivityStatus.Filed or ActivityStatus.DryRun)
                {
                    // Before the moves: the AI plugin's decisions and where replacements went
                    lines.Notes.Add(line);
                }
                else
                {
                    lines.Other.Add(line);
                }
            }

            return lines;
        }

        // For entries recorded before the counts were: what the (possibly capped) details show
        public ActivityCounts? Count()
            => Moves.Count == 0 ? null : new ActivityCounts
            {
                Videos = Moves.Count(m => m.Kind == "Video"),
                Subtitles = Moves.Count(m => m.Kind == "Subtitle"),
                Extras = Moves.Count(m => m.Kind == "Extra"),
                Clutter = Moves.Count(m => m.Kind is "Quarantine" or "File") - Replaced.Count - SetAside.Count,
                Replaced = Replaced.Count,
                SetAside = SetAside.Count,
            };

        public IEnumerable<DetailRow> Details(ActivityStatus status)
        {
            foreach (var note in Notes)
            {
                var colon = note.IndexOf(": ", StringComparison.Ordinal);
                yield return colon > 0 && colon < 120 ? new DetailRow("Decisions", note[..colon], note[(colon + 2)..]) : new DetailRow("Decisions", "Note", note);
            }

            foreach (var (file, reason) in ReviewItems)
            {
                yield return new DetailRow("Why it waits", LastSegment(file), reason);
            }

            foreach (var a in Attention)
            {
                yield return new DetailRow("Needs attention", "Problem", a);
            }

            foreach (var r in Replaced)
            {
                yield return new DetailRow("Replaced", "Moved to quarantine", r);
            }

            foreach (var s in SetAside)
            {
                yield return new DetailRow("Set aside", "Quarantined as chosen", s);
            }

            foreach (var (kind, from, to) in Moves)
            {
                yield return new DetailRow(FilesGroup, kind, from + " → " + to);
            }

            foreach (var o in Other)
            {
                yield return new DetailRow(status is ActivityStatus.Purged ? "Deleted" : "More", status is ActivityStatus.Purged ? "Folder" : "Line", o);
            }
        }
    }
}
