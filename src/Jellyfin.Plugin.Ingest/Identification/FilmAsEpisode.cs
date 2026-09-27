using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// An episode that is already in a library (with its file), as far as telling a film from it needs.
/// </summary>
public sealed record ExistingEpisode
{
    /// <summary>Gets the episode's file.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the show's name.</summary>
    public required string SeriesName { get; init; }

    /// <summary>Gets the year the show started, if known.</summary>
    public int? SeriesYear { get; init; }

    /// <summary>Gets the show's provider ids (<c>Tvdb</c>, <c>Tmdb</c> …).</summary>
    public IReadOnlyDictionary<string, string> SeriesProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the season number; 0 for specials.</summary>
    public required int Season { get; init; }

    /// <summary>Gets the episode number.</summary>
    public required int Episode { get; init; }

    /// <summary>Gets the episode's title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the year it was first shown, if known.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the episode's own provider ids (an IMDb id is shared with a film of the same title).</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// How sure Ingest is that a release identified as a film is an episode already in a library.
/// </summary>
public enum EpisodeMatchStrength
{
    /// <summary>Not that episode.</summary>
    None = 0,

    /// <summary>The titles fit (the show's name and the episode's title), but nothing else agrees: a person decides.</summary>
    Possible,

    /// <summary>The titles fit and the year agrees, or the IMDb id is the same.</summary>
    Confident,
}

/// <summary>
/// A film that is really an episode already in a library: typically a TV film or special kept as a show's Season 0
/// episode (<c>24 S00E09 - Redemption</c>) that the providers also list as a film (<c>24: Redemption</c>, 2008).
/// Filed as a film, it would be a second copy in another library; as that episode it replaces (or joins) the one there.
/// </summary>
public static class FilmAsEpisode
{
    /// <summary>How alike the rest of the film's title and the episode's title must be.</summary>
    public const double TitleMatch = 0.9;

    /// <summary>
    /// The rest of a title after a show's name at its start, on a word boundary: <c>24 Redemption</c> and
    /// <c>24: Redemption</c> after <c>24</c> give <c>REDEMPTION</c> (normalised).
    /// </summary>
    /// <param name="title">A film's title, or the title read from a release name.</param>
    /// <param name="seriesName">A show's name.</param>
    /// <returns>The rest, normalised; <c>null</c> when the title doesn't start with the show's name followed by more words.</returns>
    public static string? Rest(string title, string seriesName)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(seriesName);
        var t = TitleMatcher.Normalise(title);
        var s = TitleMatcher.Normalise(seriesName);
        return s.Length > 0 && t.Length > s.Length + 1 && t.StartsWith(s + " ", StringComparison.Ordinal) ? t[(s.Length + 1)..] : null;
    }

    /// <summary>
    /// Whether an episode's show and title together make up one of the titles (the show's name first).
    /// </summary>
    /// <param name="titles">The film's titles (its provider title, the title read from the release name).</param>
    /// <param name="seriesName">The show's name.</param>
    /// <param name="episodeTitle">The episode's title.</param>
    /// <returns><c>true</c> if they fit.</returns>
    public static bool TitleFits(IEnumerable<string> titles, string seriesName, string episodeTitle)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return !string.IsNullOrWhiteSpace(episodeTitle)
            && titles.Any(t => !string.IsNullOrWhiteSpace(t) && Rest(t, seriesName) is { } rest && TitleMatcher.Similarity(rest, episodeTitle) >= TitleMatch);
    }

    /// <summary>
    /// How sure it is that a film is an episode: the same IMDb id is enough; otherwise the titles must fit, and the year
    /// must agree with the year the episode was first shown for it to be more than possible.
    /// </summary>
    /// <param name="titles">The film's titles.</param>
    /// <param name="years">The film's years (the provider's, the release name's).</param>
    /// <param name="imdbId">The film's IMDb id, if known.</param>
    /// <param name="episode">The episode.</param>
    /// <returns>The strength.</returns>
    public static EpisodeMatchStrength Assess(IReadOnlyCollection<string> titles, IReadOnlyCollection<int> years, string? imdbId, ExistingEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(years);
        ArgumentNullException.ThrowIfNull(episode);
        if (!string.IsNullOrWhiteSpace(imdbId) && episode.ProviderIds.TryGetValue("Imdb", out var id) && string.Equals(id, imdbId, StringComparison.OrdinalIgnoreCase))
        {
            return EpisodeMatchStrength.Confident;
        }

        if (!TitleFits(titles, episode.SeriesName, episode.Title))
        {
            return EpisodeMatchStrength.None;
        }

        return episode.Year is { } y && years.Contains(y) ? EpisodeMatchStrength.Confident : EpisodeMatchStrength.Possible;
    }

    /// <summary>
    /// The episode a film most likely is, of those found. Two different episodes that fit equally well are only possible
    /// (a person picks).
    /// </summary>
    /// <param name="titles">The film's titles.</param>
    /// <param name="years">The film's years.</param>
    /// <param name="imdbId">The film's IMDb id, if known.</param>
    /// <param name="episodes">Episodes already in a library.</param>
    /// <returns>The episode and how sure; <c>null</c> when none fits.</returns>
    public static (ExistingEpisode Episode, EpisodeMatchStrength Strength)? Best(IReadOnlyCollection<string> titles, IReadOnlyCollection<int> years, string? imdbId, IEnumerable<ExistingEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var fits = episodes
            .Select(e => (Episode: e, Strength: Assess(titles, years, imdbId, e)))
            .Where(x => x.Strength != EpisodeMatchStrength.None)
            .ToList();
        if (fits.Count == 0)
        {
            return null;
        }

        var strongest = fits.Max(x => x.Strength);
        var best = fits.Where(x => x.Strength == strongest).DistinctBy(x => x.Episode.Path, StringComparer.Ordinal).ToList();
        return best.Count == 1 ? best[0] : (best[0].Episode, EpisodeMatchStrength.Possible);
    }
}
