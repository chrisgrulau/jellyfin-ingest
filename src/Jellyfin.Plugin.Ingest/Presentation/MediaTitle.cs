using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ingest.Naming;
using Jellyfin.Plugin.Ingest.Parsing;

namespace Jellyfin.Plugin.Ingest.Presentation;

/// <summary>
/// What a file or release is, in words for a headline: a film (<c>Harbour Lights (2024)</c>), an episode
/// (<c>Lantern S01E04</c>) or a season (<c>Lantern — Season 1</c>). Read from the names Ingest gives filed files, or
/// failing that from a release name.
/// </summary>
public sealed partial record MediaTitle
{
    /// <summary>Gets the film or series title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the film's year, if known (not used for series).</summary>
    public int? Year { get; init; }

    /// <summary>Gets the season, for an episode or a season pack.</summary>
    public int? Season { get; init; }

    /// <summary>Gets the (first) episode.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets the last episode of a multi-episode file.</summary>
    public int? LastEpisode { get; init; }

    /// <summary>Gets the episode's title, if known.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets the film's edition (<c>Director's Cut</c>), if any.</summary>
    public string? Edition { get; init; }

    /// <summary>Gets a value indicating whether this is a series (an episode or a season).</summary>
    public bool IsSeries { get; init; }

    /// <summary>Gets how many episodes it covers (1 for a single episode, 0 for a film or a season without numbers).</summary>
    public int EpisodeCount => Episode is { } first ? Math.Max(1, (LastEpisode ?? first) - first + 1) : 0;

    /// <summary>
    /// Reads a file Ingest filed (or replaced) from its name and folder: <c>…/Series (2001) [ids]/Season 01/Series S01E04 - Title.mkv</c>
    /// or <c>…/Film (2024) [tmdbid-1]/Film (2024) [tmdbid-1] - Edition.mkv</c>.
    /// </summary>
    /// <param name="path">The file's path (either separator).</param>
    /// <returns>The title, or <c>null</c> when the name isn't one Ingest gives.</returns>
    public static MediaTitle? FromFiledPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var parts = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        var stem = StemOf(parts[^1]);
        if (EpisodeStem().Match(stem) is { Success: true } ep)
        {
            return new MediaTitle
            {
                Title = ep.Groups["t"].Value,
                IsSeries = true,
                Season = Number(ep.Groups["s"].Value),
                Episode = Number(ep.Groups["e"].Value),
                LastEpisode = ep.Groups["e2"].Success ? Number(ep.Groups["e2"].Value) : null,
                EpisodeTitle = ep.Groups["et"].Success ? ep.Groups["et"].Value : null,
            };
        }

        if (parts.Length < 2)
        {
            return null;
        }

        // A film folder always carries a year or a provider id when Ingest names it
        var folder = parts[^2];
        if (FilmFolder().Match(folder) is not { Success: true } film || (!film.Groups["y"].Success && film.Groups["ids"].Length == 0))
        {
            return null;
        }

        string? edition = null;
        if (!string.Equals(stem, folder, StringComparison.Ordinal))
        {
            if (!stem.StartsWith(folder + " - ", StringComparison.Ordinal))
            {
                return null;
            }

            edition = stem[(folder.Length + 3)..];
        }

        return new MediaTitle
        {
            Title = film.Groups["t"].Value,
            Year = film.Groups["y"].Success ? Number(film.Groups["y"].Value) : null,
            Edition = edition,
        };
    }

    /// <summary>
    /// Reads a release name (a folder, or a file with its extension) as the parser does.
    /// </summary>
    /// <param name="name">The release name.</param>
    /// <returns>The title, or <c>null</c> when no title can be read.</returns>
    public static MediaTitle? FromReleaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var last = name.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last is null)
        {
            return null;
        }

        ParsedRelease parsed;
        try
        {
            parsed = ReleaseNameParser.Parse(last);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (parsed.Title.Length == 0)
        {
            return null;
        }

        // A season pack's name ("Show S03", "Show Season 3") leaves the season on the title
        if (parsed.Season is null && parsed.Episode is null && TrailingSeason().Match(parsed.Title) is { Success: true } pack)
        {
            parsed = parsed with { Title = parsed.Title[..pack.Index].TrimEnd(), Season = Number(pack.Groups["s"].Value), Year = null };
        }

        var series = parsed.Kind == MediaKind.Episode || parsed.Season is not null;
        return new MediaTitle
        {
            Title = parsed.Title,
            IsSeries = series,
            Year = series ? null : parsed.Year,
            Season = parsed.Season,
            Episode = parsed.Episode,
            LastEpisode = parsed.EndingEpisode,
            // What follows the code in a release name is as often the release group as the episode's title
            Edition = parsed.Edition,
        };
    }

    /// <summary>
    /// The headline and smaller line for one or more titles: one episode (<c>Lantern S01E04</c> / its title), a pack
    /// (<c>Lantern — Season 1, 10 episodes</c>), a film (<c>Harbour Lights (2024)</c> / its edition), or the first of
    /// several different titles (<c>… and 2 more</c>).
    /// </summary>
    /// <param name="titles">The titles (none gives <c>null</c>).</param>
    /// <returns>The headline and the smaller line (possibly empty), or <c>null</c>.</returns>
    public static (string Headline, string Subline)? Describe(IReadOnlyList<MediaTitle> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        if (titles.Count == 0)
        {
            return null;
        }

        var first = titles[0];
        var same = titles.All(t => t.IsSeries == first.IsSeries && string.Equals(t.Title, first.Title, StringComparison.OrdinalIgnoreCase)
            && (t.IsSeries || t.Year == first.Year));
        if (!same)
        {
            var others = titles.Select(t => t.Title.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() - 1;
            var single = Describe([first])!.Value;
            return others > 0 ? (single.Headline + " and " + Plural(others, "other title"), string.Empty) : (single.Headline, string.Empty);
        }

        if (!first.IsSeries)
        {
            var head = first.Year is { } y ? string.Create(CultureInfo.InvariantCulture, $"{first.Title} ({y})") : first.Title;
            var editions = titles.Select(t => t.Edition).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            return titles.Count == 1 ? (head, first.Edition ?? string.Empty)
                : (head, Plural(titles.Count, "version") + (editions.Count > 0 ? ": " + string.Join(", ", editions) : string.Empty));
        }

        if (titles.Count == 1)
        {
            if (first.Season is { } s && first.Episode is { } e)
            {
                return (first.Title + " " + MediaNamer.EpisodeCode(s, e, first.LastEpisode), first.EpisodeTitle ?? string.Empty);
            }

            return first.Season is { } only ? (first.Title + " — " + SeasonName(only), string.Empty) : (first.Title, string.Empty);
        }

        var seasons = titles.Select(t => t.Season).OfType<int>().Distinct().Order().ToList();
        var episodes = titles.Sum(t => t.EpisodeCount);
        var label = seasons.Count switch
        {
            0 => string.Empty,
            1 => SeasonName(seasons[0]),
            _ => IsRun(seasons) ? string.Create(CultureInfo.InvariantCulture, $"Seasons {seasons[0]}–{seasons[^1]}") : "Seasons " + string.Join(", ", seasons),
        };
        var count = Plural(episodes > 0 ? episodes : titles.Count, "episode");
        return (first.Title + " — " + (label.Length > 0 ? label + ", " + count : count), string.Empty);
    }

    /// <summary>
    /// <c>1 file</c>, <c>2 files</c>.
    /// </summary>
    /// <param name="n">The count.</param>
    /// <param name="noun">The singular noun.</param>
    /// <returns>The phrase.</returns>
    public static string Plural(int n, string noun)
    {
        ArgumentNullException.ThrowIfNull(noun);
        var plural = noun switch
        {
            "copy" => "copies",
            "other title" => "other titles",
            _ when noun.EndsWith("ch", StringComparison.Ordinal) || noun.EndsWith("sh", StringComparison.Ordinal) || noun.EndsWith('s') || noun.EndsWith('x') => noun + "es",
            _ when noun.EndsWith('y') && !noun.EndsWith("ey", StringComparison.Ordinal) => noun[..^1] + "ies",
            _ => noun + "s",
        };
        return n == 1 ? "1 " + noun : n.ToString(CultureInfo.InvariantCulture) + " " + plural;
    }

    private static string SeasonName(int season) => season == 0 ? "Specials" : "Season " + season.ToString(CultureInfo.InvariantCulture);

    private static bool IsRun(List<int> sorted) => sorted[^1] - sorted[0] == sorted.Count - 1;

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string StemOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 && fileName.Length - dot <= 6 && !fileName[(dot + 1)..].Contains(' ', StringComparison.Ordinal) ? fileName[..dot] : fileName;
    }

    // "Series S01E04 - Title", "Series S01E01-E02", as MediaNamer.EpisodeFileName writes them
    [GeneratedRegex(@"^(?<t>.+?) S(?<s>\d{2,4})E(?<e>\d{2,4})(?:-E(?<e2>\d{2,4}))?(?: - (?<et>.+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeStem();

    // "Show S03", "Show Season 3" at the end of a title
    [GeneratedRegex(@"(?<=\S)\s+(?:S|Season\s*)(?<s>\d{1,4})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSeason();

    // "Film (2024) [tmdbid-1]", as MediaNamer.MovieFolderName writes it
    [GeneratedRegex(@"^(?<t>.+?)(?: \((?<y>\d{4})\))?(?<ids>(?: \[[a-z]+id-[^\]]+\])*)$", RegexOptions.CultureInvariant)]
    private static partial Regex FilmFolder();
}
