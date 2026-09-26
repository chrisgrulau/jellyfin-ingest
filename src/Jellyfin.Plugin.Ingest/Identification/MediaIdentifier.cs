using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Naming;
using Jellyfin.Plugin.Ingest.Parsing;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// Turns a <see cref="ParsedRelease"/> into provider ids by searching the configured metadata providers and scoring the
/// candidates. It only commits to a match that is both good and clearly ahead of the runner-up; anything else is
/// returned for review rather than guessed.
/// </summary>
public sealed class MediaIdentifier
{
    /// <summary>Minimum score for an automatic match.</summary>
    public const double AcceptThreshold = 0.80;

    /// <summary>Minimum lead over the second-best distinct candidate for an automatic match.</summary>
    public const double RequiredMargin = 0.08;

    /// <summary>Minimum lead when the release name has no year: without it, same-named titles are easy to confuse.</summary>
    public const double RequiredMarginWithoutYear = 0.15;

    /// <summary>Bonus for a candidate that is already in the destination library (strong evidence: new episodes of shows you have).</summary>
    public const double InLibraryBonus = 0.20;

    /// <summary>
    /// Penalty for a candidate known only by an IMDb id (typically an OMDb hit). Jellyfin names and fetches metadata by
    /// TMDb/TheTVDB ids, and OMDb's IMDb search often returns obscure same-named titles that would otherwise tie with
    /// the real match.
    /// </summary>
    public const double ImdbOnlyPenalty = 0.20;

    /// <summary>The lowest best score a tie-breaker is asked about (below it, nothing found is close enough to settle).</summary>
    public const double TiebreakFloor = 0.60;

    /// <summary>The most candidates a tie-breaker chooses between.</summary>
    public const int TiebreakOptions = 5;

    /// <summary>How close an episode title must be to count as a match on its own.</summary>
    public const double EpisodeTitleMatch = 0.85;

    /// <summary>How far ahead of the next episode title a match must be.</summary>
    public const double EpisodeTitleLead = 0.10;

    /// <summary>The most episodes offered to an <see cref="IEpisodePicker"/>.</summary>
    public const int EpisodeOptions = 40;

    /// <summary>The most episodes compared with a transcript (too many and the question is too big to be useful).</summary>
    public const int TranscriptOptions = 150;

    /// <summary>The most seasons listed when the name gives no season.</summary>
    public const int MaxSeasons = 40;

    /// <summary>The <see cref="MetadataCandidate.Source"/> value used for library hits.</summary>
    public const string LibrarySource = "Library";

    private readonly IMetadataLookup _lookup;
    private readonly ILibraryIndex? _library;
    private readonly ITiebreaker? _tiebreaker;
    private readonly ITranscriber? _transcriber;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaIdentifier"/> class.
    /// </summary>
    /// <param name="lookup">Metadata provider access.</param>
    /// <param name="library">What's already in the destination library (optional).</param>
    /// <param name="tiebreaker">Settles close matches (optional; without it they wait for review).</param>
    /// <param name="transcriber">Transcribes a stretch of a video whose name doesn't say which episode it is (optional;
    /// used only with a tie-breaker that is also an <see cref="IEpisodePicker"/>).</param>
    public MediaIdentifier(IMetadataLookup lookup, ILibraryIndex? library = null, ITiebreaker? tiebreaker = null, ITranscriber? transcriber = null)
    {
        _tiebreaker = tiebreaker;
        _transcriber = transcriber;
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _library = library;
    }

    /// <summary>
    /// Identifies a parsed release.
    /// </summary>
    /// <param name="release">The parsed release name.</param>
    /// <param name="preferTv">Whether the destination library holds shows (used when the name alone is ambiguous).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="videoPath">The video's path (or just its file name). Only the file name is ever sent to a
    /// tie-breaker; the full path is used to transcribe the video when its name doesn't say which episode it is.</param>
    /// <returns>The identification result.</returns>
    public async Task<IdentificationResult> IdentifyAsync(ParsedRelease release, bool preferTv, CancellationToken cancellationToken, string? videoPath = null)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (release.Title.Length == 0)
        {
            return new IdentificationResult { Status = IdentificationStatus.NotFound, Reason = "No title could be read from the name." };
        }

        var result = release.Kind == MediaKind.Episode || (release.Kind == MediaKind.Unknown && preferTv)
            ? await IdentifyEpisodeAsync(release, videoPath, cancellationToken).ConfigureAwait(false)
            : await IdentifyMovieAsync(release, cancellationToken).ConfigureAwait(false);
        return await SettleAsync(release, videoPath, result, cancellationToken).ConfigureAwait(false);
    }

    // A close call between candidates (not "nothing found", not "no episode number") goes to the tie-breaker, if any. Its
    // pick is filed exactly as if a person had chosen it in review; no pick leaves the review as it was.
    private async Task<IdentificationResult> SettleAsync(ParsedRelease release, string? videoPath, IdentificationResult result, CancellationToken ct)
    {
        if (_tiebreaker is null || result.Status != IdentificationStatus.NeedsReview || result.Candidates.Count < 2
            || result.Candidates[0].Score < TiebreakFloor
            || (release.Kind == MediaKind.Episode && (release.Season is null || release.Episode is null)))
        {
            return result;
        }

        var options = result.Candidates.Take(TiebreakOptions).ToList();
        var pick = await _tiebreaker.PickAsync(release, FileName(videoPath) ?? release.Title, options, ct).ConfigureAwait(false);
        if (pick.Index is not { } i || i < 0 || i >= options.Count)
        {
            return result with { Reason = result.Reason + " " + pick.Note };
        }

        var chosen = options[i].Candidate;
        var settled = await IdentifyAsChosenAsync(release, chosen, chosen.IsSeries, ct, videoPath).ConfigureAwait(false);
        return settled.Status == IdentificationStatus.Identified
            ? settled with
            {
                Reason = $"Chosen by {pick.By ?? "the tie-breaker"} from {options.Count} close candidates: '{chosen.Name}'{(chosen.Year is { } y ? $" ({y})" : string.Empty)}. {pick.Note}",
                Candidates = result.Candidates,
                DecidedBy = pick.By ?? "tie-breaker",
            }
            : result;
    }

    /// <summary>
    /// Identifies a parsed release as a title a person has already chosen (from a review), skipping the search. Only
    /// the episode title is still looked up.
    /// </summary>
    /// <param name="release">The parsed release name.</param>
    /// <param name="chosen">The chosen candidate.</param>
    /// <param name="isTv">Whether the chosen title is a series.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="videoPath">The video's path (or just its file name), for an episode picker and transcript.</param>
    /// <returns>The identification result.</returns>
    public async Task<IdentificationResult> IdentifyAsChosenAsync(ParsedRelease release, MetadataCandidate chosen, bool isTv, CancellationToken cancellationToken, string? videoPath = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(chosen);

        var reason = $"Chosen in review: '{chosen.Name}'.";
        if (!isTv)
        {
            return new IdentificationResult
            {
                Status = IdentificationStatus.Identified,
                Confidence = 1,
                Reason = reason,
                Movie = new MovieIdentity { Title = chosen.Name, Year = chosen.Year, TmdbId = Id(chosen, "Tmdb"), ImdbId = Id(chosen, "Imdb"), Edition = release.Edition },
            };
        }

        var series = new SeriesIdentity { Title = chosen.Name, Year = chosen.Year, TvdbId = Id(chosen, "Tvdb"), TmdbId = Id(chosen, "Tmdb") };
        if (release.Season is not { } season || release.Episode is not { } episode)
        {
            var byTitle = await FindEpisodeAsync(release, chosen, videoPath, cancellationToken).ConfigureAwait(false);
            return byTitle.Hit is { } hit
                ? ByTitle(series, hit, 1, [], reason + " " + byTitle.Note, byTitle.By)
                : new IdentificationResult
                {
                    Status = IdentificationStatus.NeedsReview,
                    Reason = $"Series is '{chosen.Name}', but the season or episode number can't be read from the file name." + byTitle.Note,
                };
        }

        var title = await _lookup.GetEpisodeTitleAsync(chosen.ProviderIds, season, episode, cancellationToken).ConfigureAwait(false);
        return new IdentificationResult
        {
            Status = IdentificationStatus.Identified,
            Confidence = 1,
            Reason = reason,
            Episode = new EpisodeIdentity { Series = series, Season = season, Episode = episode, EndingEpisode = release.EndingEpisode, Title = title ?? release.EpisodeTitle },
        };
    }

    /// <summary>
    /// Scores one candidate against the parsed title and year. Scores can exceed 1 (bonuses stack on an exact title) so
    /// that ties between same-named titles are broken; confidence is reported capped at 1.
    /// </summary>
    /// <param name="title">Parsed title.</param>
    /// <param name="year">Parsed year, if any.</param>
    /// <param name="candidate">Provider hit.</param>
    /// <returns>Ranking score (0 upwards; about 1 for an exact title).</returns>
    public static double Score(string title, int? year, MetadataCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // Scored against the name that fits best (the server's language, or English)
        var name = candidate.AlternativeNames.Prepend(candidate.Name).MaxBy(n => TitleMatcher.Similarity(title, n)) ?? candidate.Name;
        var score = TitleMatcher.Similarity(title, name);
        if (string.Equals(candidate.Source, LibrarySource, StringComparison.Ordinal))
        {
            score += InLibraryBonus;
        }

        if (year is { } y && candidate.Year is { } cy)
        {
            score += Math.Abs(y - cy) switch
            {
                0 => 0.10,
                1 => 0.05,
                _ => -0.25,
            };
        }

        // Providers rank by relevance and popularity: a small nudge stops an obscure exact-name title beating the famous one
        score += candidate.ProviderRank switch { 0 => 0.05, 1 => 0.025, _ => 0 };
        score -= NumberMismatchPenalty(title, name);
        if (IsImdbOnly(candidate))
        {
            score -= ImdbOnlyPenalty;
        }

        return Math.Max(0, score);
    }

    /// <summary>
    /// Sequel numbers must agree: "Night Train 4" is not "Night Train 3", and "Iron Kite" is not "Iron Kite 2".
    /// </summary>
    /// <param name="a">First title.</param>
    /// <param name="b">Second title.</param>
    /// <returns>Penalty to subtract (0 when the numbers agree).</returns>
    public static double NumberMismatchPenalty(string a, string b)
    {
        var na = Numbers(a);
        var nb = Numbers(b);
        if (na.SetEquals(nb))
        {
            return 0;
        }

        return na.Count > 0 && nb.Count > 0 ? 0.30 : 0.15;
    }

    /// <summary>
    /// Whether a candidate lacks both a TMDb and a TheTVDB id (and isn't already in the library).
    /// </summary>
    /// <param name="candidate">Provider hit.</param>
    /// <returns><c>true</c> when only IMDb (or nothing) identifies it.</returns>
    public static bool IsImdbOnly(MetadataCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return !string.Equals(candidate.Source, LibrarySource, StringComparison.Ordinal)
            && !candidate.ProviderIds.ContainsKey("Tmdb")
            && !candidate.ProviderIds.ContainsKey("Tvdb");
    }

    private static HashSet<string> Numbers(string title)
        => [.. TitleMatcher.Normalise(title).Split(' ').Where(t => t.Length > 0 && t.All(char.IsAsciiDigit) && t != "1")];

    /// <summary>
    /// Merges hits that describe the same title (shared provider id, or same normalised name and year) so that the same
    /// show returned by two providers doesn't compete with itself, and combines their ids.
    /// </summary>
    /// <param name="candidates">Raw hits.</param>
    /// <returns>Merged hits.</returns>
    public static IReadOnlyList<MetadataCandidate> Merge(IEnumerable<MetadataCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var merged = new List<MetadataCandidate>();
        foreach (var c in candidates)
        {
            var i = merged.FindIndex(m => SameTitle(m, c));
            if (i < 0)
            {
                merged.Add(c);
                continue;
            }

            var ids = new Dictionary<string, string>(merged[i].ProviderIds, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in c.ProviderIds)
            {
                ids.TryAdd(k, v);
            }

            var source = string.Equals(c.Source, LibrarySource, StringComparison.Ordinal) ? LibrarySource : merged[i].Source;
            var rank = (merged[i].ProviderRank, c.ProviderRank) switch
            {
                (int x, int y) => Math.Min(x, y),
                (var x, var y) => x ?? y,
            };
            merged[i] = merged[i] with { ProviderIds = ids, Year = merged[i].Year ?? c.Year, Source = source, ProviderRank = rank };
        }

        return merged;
    }

    private static bool SameTitle(MetadataCandidate a, MetadataCandidate b)
    {
        foreach (var (k, v) in a.ProviderIds)
        {
            if (b.ProviderIds.TryGetValue(k, out var w) && string.Equals(v, w, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Names that normalise to nothing say nothing about each other: never merge on them
        var x = TitleMatcher.Normalise(a.Name);
        return x.Length > 0 && a.Year == b.Year && string.Equals(x, TitleMatcher.Normalise(b.Name), StringComparison.Ordinal);
    }

    private static (ScoredCandidate? Best, IReadOnlyList<ScoredCandidate> Ranked, string Reason) Choose(string title, int? year, IReadOnlyList<MetadataCandidate> hits)
    {
        var ranked = Merge(hits).Select(c => new ScoredCandidate(c, Score(title, year, c))).OrderByDescending(s => s.Score).ToList();
        if (ranked.Count == 0)
        {
            return (null, ranked, "No provider results.");
        }

        var best = ranked[0];
        var margin = year is null ? RequiredMarginWithoutYear : RequiredMargin;
        if (best.Score < AcceptThreshold)
        {
            return (null, ranked, string.Create(CultureInfo.InvariantCulture, $"Best match '{best.Candidate.Name}' scored {best.Score:0.00}, below {AcceptThreshold:0.00}."));
        }

        if (ranked.Count > 1 && best.Score - ranked[1].Score < margin)
        {
            return (null, ranked, string.Create(CultureInfo.InvariantCulture, $"'{best.Candidate.Name}' and '{ranked[1].Candidate.Name}' are too close to call."));
        }

        return (best, ranked, string.Create(CultureInfo.InvariantCulture, $"Matched '{best.Candidate.Name}' ({best.Score:0.00})."));
    }

    private static string? Id(MetadataCandidate c, string provider)
        => c.ProviderIds.TryGetValue(provider, out var v) ? v : null;

    private static IReadOnlyList<MetadataCandidate> Ranked(IReadOnlyList<MetadataCandidate> hits)
        => [.. hits.Select((h, i) => h.ProviderRank is null ? h with { ProviderRank = i } : h)];

    private async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(bool series, string title, int? year, CancellationToken ct)
    {
        IReadOnlyList<MetadataCandidate> local = _library is null ? []
            : series ? await _library.FindSeriesAsync(title, ct).ConfigureAwait(false)
            : await _library.FindMoviesAsync(title, ct).ConfigureAwait(false);
        local = [.. local.Select(l => l with { Source = LibrarySource })];

        var hits = Ranked(series
            ? await _lookup.SearchSeriesAsync(title, year, ct).ConfigureAwait(false)
            : await _lookup.SearchMoviesAsync(title, year, ct).ConfigureAwait(false));
        hits = [.. local, .. hits];

        // A wrong or missing year in the release name shouldn't hide the right title: unless there is a near-exact
        // title hit, also search without the year.
        if (year is not null && !hits.Any(h => h.AlternativeNames.Prepend(h.Name).Any(n => TitleMatcher.Similarity(title, n) >= 0.95)))
        {
            var more = Ranked(series
                ? await _lookup.SearchSeriesAsync(title, null, ct).ConfigureAwait(false)
                : await _lookup.SearchMoviesAsync(title, null, ct).ConfigureAwait(false));
            hits = [.. hits, .. more];
        }

        // Nothing at all: stray words in the name can break provider search; retry with shorter titles.
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var n = words.Length - 1; hits.Count == local.Count && n >= 1 && n >= words.Length - 3; n--)
        {
            var shorter = string.Join(' ', words.Take(n));
            var more = Ranked(series
                ? await _lookup.SearchSeriesAsync(shorter, year, ct).ConfigureAwait(false)
                : await _lookup.SearchMoviesAsync(shorter, year, ct).ConfigureAwait(false));
            hits = [.. hits, .. more];
        }

        return [.. hits.Select(h => h with { IsSeries = series })];
    }

    private async Task<IdentificationResult> IdentifyMovieAsync(ParsedRelease release, CancellationToken ct)
    {
        var hits = await SearchAsync(false, release.Title, release.Year, ct).ConfigureAwait(false);
        var (best, ranked, reason) = Choose(release.Title, release.Year, hits);
        if (best is null)
        {
            return new IdentificationResult
            {
                Status = ranked.Count == 0 ? IdentificationStatus.NotFound : IdentificationStatus.NeedsReview,
                Confidence = ranked.Count > 0 ? Math.Min(1, ranked[0].Score) : 0,
                Reason = reason,
                Candidates = ranked,
                NothingFound = ranked.Count == 0,
            };
        }

        var c = best.Candidate;
        return new IdentificationResult
        {
            Status = IdentificationStatus.Identified,
            Confidence = Math.Min(1, best.Score),
            Reason = reason,
            Candidates = ranked,
            Movie = new MovieIdentity { Title = c.Name, Year = c.Year, TmdbId = Id(c, "Tmdb"), ImdbId = Id(c, "Imdb"), Edition = release.Edition },
        };
    }

    private async Task<IdentificationResult> IdentifyEpisodeAsync(ParsedRelease release, string? videoPath, CancellationToken ct)
    {
        var hits = await SearchAsync(true, release.Title, release.Year, ct).ConfigureAwait(false);
        var (best, ranked, reason) = Choose(release.Title, release.Year, hits);
        if (best is null)
        {
            return new IdentificationResult
            {
                Status = ranked.Count == 0 ? IdentificationStatus.NotFound : IdentificationStatus.NeedsReview,
                Confidence = ranked.Count > 0 ? Math.Min(1, ranked[0].Score) : 0,
                Reason = reason,
                Candidates = ranked,
                NothingFound = ranked.Count == 0,
            };
        }

        var c = best.Candidate;
        var series = new SeriesIdentity { Title = c.Name, Year = c.Year, TvdbId = Id(c, "Tvdb"), TmdbId = Id(c, "Tmdb") };

        if (release.Season is not { } season || release.Episode is not { } episode)
        {
            var byTitle = await FindEpisodeAsync(release, c, videoPath, ct).ConfigureAwait(false);
            return byTitle.Hit is { } hit
                ? ByTitle(series, hit, Math.Min(1, best.Score), ranked, reason + " " + byTitle.Note, byTitle.By)
                : new IdentificationResult
                {
                    Status = IdentificationStatus.NeedsReview,
                    Confidence = Math.Min(1, best.Score),
                    Candidates = ranked,
                    Reason = string.Create(CultureInfo.InvariantCulture, $"Series is '{c.Name}', but the episode number is unknown (special named '{release.EpisodeTitle}').") + byTitle.Note,
                };
        }

        var title = await _lookup.GetEpisodeTitleAsync(c.ProviderIds, season, episode, ct).ConfigureAwait(false);
        return new IdentificationResult
        {
            Status = IdentificationStatus.Identified,
            Confidence = Math.Min(1, best.Score),
            Candidates = ranked,
            Reason = title is null ? reason + " Episode title not found at the provider." : reason,
            Episode = new EpisodeIdentity { Series = series, Season = season, Episode = episode, EndingEpisode = release.EndingEpisode, Title = title ?? release.EpisodeTitle },
        };
    }

    private static IdentificationResult ByTitle(SeriesIdentity series, EpisodeListing hit, double confidence, IReadOnlyList<ScoredCandidate> ranked, string reason, string? by) => new()
    {
        Status = IdentificationStatus.Identified,
        Confidence = confidence,
        Candidates = ranked,
        Reason = reason.Trim(),
        DecidedBy = by,
        Episode = new EpisodeIdentity { Series = series, Season = hit.Season, Episode = hit.Episode, Title = hit.Title },
    };

    // A file that names its episode by title only (typically a special, "S13SP2 A Special Title"): find that title in
    // the season's episode list. A clear match is taken; otherwise an episode picker (the AI plugin) may choose one of
    // the listed episodes. The note is appended to the reason either way.
    private async Task<(EpisodeListing? Hit, string Note, string? By)> FindByTitleAsync(ParsedRelease release, MetadataCandidate series, string? videoPath, CancellationToken ct)
    {
        if (release.Season is not { } season || release.Episode is not null || string.IsNullOrWhiteSpace(release.EpisodeTitle))
        {
            return (null, string.Empty, null);
        }

        var title = release.EpisodeTitle;
        var list = await _lookup.ListSeasonAsync(series.ProviderIds, season, ct).ConfigureAwait(false);
        if (list.Count == 0)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $" The providers have no episode list for season {season}."), null);
        }

        var scored = list.Select(l => (Listing: l, Score: TitleMatcher.Similarity(title, l.Title))).OrderByDescending(x => x.Score).ToList();
        var top = scored[0];
        if (top.Score >= EpisodeTitleMatch && (scored.Count == 1 || scored[1].Score <= top.Score - EpisodeTitleLead))
        {
            return (top.Listing, string.Create(CultureInfo.InvariantCulture, $"Episode S{top.Listing.Season:00}E{top.Listing.Episode:00} '{top.Listing.Title}' found by its title."), null);
        }

        if (_tiebreaker is not IEpisodePicker picker)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $" No episode of season {season} clearly has that title."), null);
        }

        // Likeliest first: title similarity, then the year the name gives
        var options = scored
            .OrderByDescending(x => x.Score + (release.Year is { } y && x.Listing.Year == y ? 0.2 : 0))
            .Take(EpisodeOptions)
            .Select(x => x.Listing)
            .ToList();
        var pick = await picker.PickEpisodeAsync(FileName(videoPath) ?? title, series.Name, title, release.Year, options, ct).ConfigureAwait(false);
        if (pick.Index is not { } i || i < 0 || i >= options.Count)
        {
            return (null, string.IsNullOrEmpty(pick.Note) ? string.Create(CultureInfo.InvariantCulture, $" No episode of season {season} clearly has that title.") : " " + pick.Note, null);
        }

        var chosen = options[i];
        return (chosen, string.Create(CultureInfo.InvariantCulture, $"Episode S{chosen.Season:00}E{chosen.Episode:00} '{chosen.Title}' chosen by {pick.By ?? "the episode picker"} from {options.Count} listed episodes. {pick.Note}"), pick.By ?? "episode picker");
    }

    private static string? FileName(string? videoPath) => string.IsNullOrEmpty(videoPath) ? null : Path.GetFileName(videoPath);

    // The episode number is missing: first by the episode title in the name, then (with a transcriber and an episode
    // picker) by comparing a short transcript with the episode synopses
    private async Task<(EpisodeListing? Hit, string Note, string? By)> FindEpisodeAsync(ParsedRelease release, MetadataCandidate series, string? videoPath, CancellationToken ct)
    {
        var byTitle = await FindByTitleAsync(release, series, videoPath, ct).ConfigureAwait(false);
        if (byTitle.Hit is not null || release.Episode is not null || _transcriber is null || _tiebreaker is not IEpisodePicker picker
            || videoPath is null || !Path.IsPathFullyQualified(videoPath))
        {
            return byTitle;
        }

        var byTranscript = await FindByTranscriptAsync(release, series, videoPath, picker, ct).ConfigureAwait(false);
        return byTranscript.Hit is not null ? byTranscript : (null, byTitle.Note + byTranscript.Note, null);
    }

    private async Task<(EpisodeListing? Hit, string Note, string? By)> FindByTranscriptAsync(ParsedRelease release, MetadataCandidate series, string videoPath, IEpisodePicker picker, CancellationToken ct)
    {
        // The transcript first (ING-31): it answers at once when the Subtitles plugin is missing or doesn't allow Ingest,
        // so the seasons are only listed when there is something to compare
        var heard = await _transcriber!.TranscribeAsync(videoPath, ct).ConfigureAwait(false);
        if (heard.Text is not { } text)
        {
            return (null, heard.Note.Length > 0 ? " " + heard.Note : string.Empty, null);
        }

        // The season the name gives, or every season until one is empty (specials aren't guessed this way)
        var episodes = new List<EpisodeListing>();
        if (release.Season is { } known)
        {
            episodes.AddRange(await _lookup.ListSeasonAsync(series.ProviderIds, known, ct).ConfigureAwait(false));
        }
        else
        {
            for (var season = 1; season <= MaxSeasons && episodes.Count <= TranscriptOptions; season++)
            {
                var list = await _lookup.ListSeasonAsync(series.ProviderIds, season, ct).ConfigureAwait(false);
                if (list.Count == 0)
                {
                    break;
                }

                episodes.AddRange(list);
            }
        }

        if (episodes.Count == 0)
        {
            return (null, string.Empty, null);
        }

        if (episodes.Count > TranscriptOptions)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $" '{series.Name}' has too many episodes to compare a transcript with; a season number in the name would narrow it down."), null);
        }

        var pick = await picker.PickFromTranscriptAsync(Path.GetFileName(videoPath), series.Name, text, episodes, ct).ConfigureAwait(false);
        if (pick.Index is not { } i || i < 0 || i >= episodes.Count)
        {
            return (null, string.IsNullOrEmpty(pick.Note) ? string.Empty : " From a transcript: " + pick.Note, null);
        }

        var chosen = episodes[i];
        return (chosen, string.Create(CultureInfo.InvariantCulture, $"Episode S{chosen.Season:00}E{chosen.Episode:00} '{chosen.Title}' identified from a transcript by {pick.By ?? "the episode picker"}, among {episodes.Count} episodes. {pick.Note}"), pick.By ?? "episode picker");
    }
}
