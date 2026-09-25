using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>The <see cref="MetadataCandidate.Source"/> value used for library hits.</summary>
    public const string LibrarySource = "Library";

    private readonly IMetadataLookup _lookup;
    private readonly ILibraryIndex? _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaIdentifier"/> class.
    /// </summary>
    /// <param name="lookup">Metadata provider access.</param>
    /// <param name="library">What's already in the destination library (optional).</param>
    public MediaIdentifier(IMetadataLookup lookup, ILibraryIndex? library = null)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _library = library;
    }

    /// <summary>
    /// Identifies a parsed release.
    /// </summary>
    /// <param name="release">The parsed release name.</param>
    /// <param name="preferTv">Whether the destination library holds shows (used when the name alone is ambiguous).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identification result.</returns>
    public async Task<IdentificationResult> IdentifyAsync(ParsedRelease release, bool preferTv, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (release.Title.Length == 0)
        {
            return new IdentificationResult { Status = IdentificationStatus.NotFound, Reason = "No title could be read from the name." };
        }

        return release.Kind == MediaKind.Episode || (release.Kind == MediaKind.Unknown && preferTv)
            ? await IdentifyEpisodeAsync(release, cancellationToken).ConfigureAwait(false)
            : await IdentifyMovieAsync(release, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Identifies a parsed release as a title a person has already chosen (from a review), skipping the search. Only
    /// the episode title is still looked up.
    /// </summary>
    /// <param name="release">The parsed release name.</param>
    /// <param name="chosen">The chosen candidate.</param>
    /// <param name="isTv">Whether the chosen title is a series.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identification result.</returns>
    public async Task<IdentificationResult> IdentifyAsChosenAsync(ParsedRelease release, MetadataCandidate chosen, bool isTv, CancellationToken cancellationToken)
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

        if (release.Season is not { } season || release.Episode is not { } episode)
        {
            return new IdentificationResult
            {
                Status = IdentificationStatus.NeedsReview,
                Reason = $"Series is '{chosen.Name}', but the season or episode number can't be read from the file name.",
            };
        }

        var series = new SeriesIdentity { Title = chosen.Name, Year = chosen.Year, TvdbId = Id(chosen, "Tvdb"), TmdbId = Id(chosen, "Tmdb") };
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

        var score = TitleMatcher.Similarity(title, candidate.Name);
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
        score -= NumberMismatchPenalty(title, candidate.Name);
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
        if (year is not null && !hits.Any(h => TitleMatcher.Similarity(title, h.Name) >= 0.95))
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

    private async Task<IdentificationResult> IdentifyEpisodeAsync(ParsedRelease release, CancellationToken ct)
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
            };
        }

        var c = best.Candidate;
        var series = new SeriesIdentity { Title = c.Name, Year = c.Year, TvdbId = Id(c, "Tvdb"), TmdbId = Id(c, "Tmdb") };

        if (release.Season is not { } season || release.Episode is not { } episode)
        {
            return new IdentificationResult
            {
                Status = IdentificationStatus.NeedsReview,
                Confidence = Math.Min(1, best.Score),
                Candidates = ranked,
                Reason = string.Create(CultureInfo.InvariantCulture, $"Series is '{c.Name}', but the episode number is unknown (special named '{release.EpisodeTitle}')."),
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
}
