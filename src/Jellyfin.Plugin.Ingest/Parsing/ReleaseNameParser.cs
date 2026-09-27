using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Naming;

namespace Jellyfin.Plugin.Ingest.Parsing;

/// <summary>
/// Reads title, year, season/episode, edition and extra type out of scene/web release names such as
/// <c>n00b-Lantern1E4.mp4</c>, <c>Show.Name.S02E05E06.1080p.WEB-DL.x264-GRP.mkv</c> or
/// <c>Movie Title (2019) [1080p] [BluRay] [5.1] [YTS.MX]</c>. Pure string work: no I/O, no provider calls.
/// </summary>
public static partial class ReleaseNameParser
{
    private static readonly (Regex Pattern, string Label)[] Editions =
    [
        (DirectorsCut(), "Director's Cut"),
        (ExtendedCut(), "Extended"),
        (AssemblyCut(), "Assembly Cut"),
        (SpecialCut(), "Special Cut"),
        (SpecialEdition(), "Special Edition"),
        (Theatrical(), "Theatrical"),
        (Unrated(), "Unrated"),
        (Uncut(), "Uncut"),
        (Redux(), "Redux"),
        (FinalCut(), "Final Cut"),
        (UltimateEdition(), "Ultimate Edition"),
        (Imax(), "IMAX"),
    ];

    private static readonly (Regex Pattern, ExtraType Type)[] Extras =
    [
        (SampleWord(), ExtraType.Sample),
        (TrailerWord(), ExtraType.Trailer),
        (DeletedWord(), ExtraType.DeletedScene),
        (BehindWord(), ExtraType.BehindTheScenes),
        (InterviewWord(), ExtraType.Interview),
        (FeaturetteWord(), ExtraType.Featurette),
        (ShortWord(), ExtraType.ShortFilm),
    ];

    /// <summary>How closely a folder's title must match the file's for the folder's year to be used.</summary>
    public const double FolderTitleSimilarity = 0.90;

    /// <summary>
    /// Parses a file name or path. When the file name alone has no usable title (e.g. <c>S01E04.mkv</c>) or says
    /// nothing at all (<c>episode.mkv</c>, <c>01.mkv</c>, <c>VTS_01_1.mkv</c>), the release folder supplies it, and a
    /// season in a folder name (<c>Show Season 2/01.mkv</c>) makes a bare number the episode number.
    /// </summary>
    /// <param name="path">A file name or full path.</param>
    /// <returns>The parsed release.</returns>
    public static ParsedRelease Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fileName = Path.GetFileName(path);
        var stem = HasVideoOrSubtitleExtension(fileName) ? Path.GetFileNameWithoutExtension(fileName) : fileName;
        var parsed = ParseName(stem);

        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        var titleFolder = TitleFolder(path);

        // A name that says nothing ("episode.mkv", "01.mkv", "VTS_01_1.mkv") names no title: the release folder does
        var generic = IsGenericName(stem);
        if (generic)
        {
            parsed = new ParsedRelease { Extra = parsed.Extra, Edition = parsed.Edition };
        }

        if (parsed.Title.Length == 0 && titleFolder.Length > 0)
        {
            var fromParent = ParseName(titleFolder);
            fromParent = fromParent with { Title = TrailingSeason().Replace(fromParent.Title, string.Empty).Trim() };
            var season = parsed.Season ?? SeasonFromFolders(path) ?? fromParent.Season;
            parsed = parsed with
            {
                Title = fromParent.Title,
                Year = parsed.Year ?? fromParent.Year,
                Season = season,
                Kind = season is not null ? MediaKind.Episode : parsed.Kind == MediaKind.Unknown ? fromParent.Kind : parsed.Kind,
            };

            // Inside a season folder, a bare number ("01.mkv", "E01.mkv", "Episode 1.mkv") is the episode number
            if (generic && parsed.Season is not null && parsed.Episode is null && EpisodeNumberOnly().Match(Normalise(stem)) is { Success: true } n)
            {
                parsed = parsed with { Episode = int.Parse(n.Groups["e"].Value, CultureInfo.InvariantCulture) };
            }
        }
        else if (parsed.Year is null && titleFolder.Length > 0)
        {
            // Packs often carry the year on the folder only ("Show (2022) Season 1/Show.S01E01.mkv"). Take it when
            // the folder names the same title, never from an unrelated folder the file happens to sit in.
            var fromParent = ParseName(titleFolder);
            var folderTitle = TrailingSeason().Replace(fromParent.Title, string.Empty).Trim();
            if (fromParent.Year is { } folderYear && TitleMatcher.Similarity(parsed.Title, folderTitle) >= FolderTitleSimilarity)
            {
                parsed = parsed with
                {
                    Year = folderYear,
                    Kind = parsed.Kind == MediaKind.Unknown ? fromParent.Kind : parsed.Kind,
                };
            }
        }

        if (parsed.Extra == ExtraType.None && parent.Length > 0 && ExtraFromFolder(parent) is { } folderExtra)
        {
            parsed = parsed with { Extra = folderExtra };
        }

        return parsed;
    }

    /// <summary>
    /// Parses a single name (no directory part, no extension).
    /// </summary>
    /// <param name="name">A release file stem or folder name.</param>
    /// <returns>The parsed release.</returns>
    public static ParsedRelease ParseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var text = Normalise(name);
        var edition = Editions.Where(e => e.Pattern.IsMatch(text)).Select(e => e.Label).FirstOrDefault();
        var extra = Extras.Where(e => e.Pattern.IsMatch(text)).Select(e => e.Type).FirstOrDefault();

        if (SpecialCode().Match(text) is { Success: true } sp)
        {
            var (spTitle, spYear) = SplitTitleAndYear(CutAtJunk(text[..sp.Index]));
            var spEpisodeTitle = CleanTitle(WeakJunkToken().Replace(CutAtJunk(text[(sp.Index + sp.Length)..]), " "));
            return new ParsedRelease
            {
                Kind = MediaKind.Episode,
                Title = spTitle,
                Year = spYear,
                Season = 0,
                EpisodeTitle = spEpisodeTitle.Length > 0 ? spEpisodeTitle : null,
                Edition = edition,
                Extra = extra,
            };
        }

        if (MatchEpisode(text) is { } ep)
        {
            var before = text[..ep.Index];
            var (title, year) = SplitTitleAndYear(CutAtJunk(before));
            var after = CutAtJunk(text[(ep.Index + ep.Length)..]);
            var epTitle = CleanTitle(WeakJunkToken().Replace(after, " "));
            return new ParsedRelease
            {
                Kind = MediaKind.Episode,
                Title = title,
                Year = year,
                Season = ep.Season,
                Episode = ep.Episode,
                EndingEpisode = ep.EndingEpisode,
                EpisodeTitle = epTitle.Length > 0 ? epTitle : null,
                Edition = edition,
                Extra = extra,
            };
        }

        // A season without an episode ("Show S02", "Show - Season 2 - Untitled", "Show 2nd Season"): a series whose
        // episode is unknown. At the very start it is only taken when nothing but generic words follow, so a title
        // that begins with "Season" or "Series" stays a title.
        if (SeasonOnly().Match(CutAtJunk(text)) is { Success: true } so)
        {
            var afterSeason = CleanTitle(WeakJunkToken().Replace(CutAtJunk(text[(so.Index + so.Length)..]), " "));
            if (so.Index > 0 || afterSeason.Length == 0 || IsGenericName(afterSeason))
            {
                var (soTitle, soYear) = SplitTitleAndYear(CutAtJunk(text[..so.Index]));
                return new ParsedRelease
                {
                    Kind = MediaKind.Episode,
                    Title = soTitle,
                    Year = soYear,
                    Season = so.Groups["end"].Success ? null : int.Parse(so.Groups["s"].Value, CultureInfo.InvariantCulture),
                    EpisodeTitle = afterSeason.Length > 0 && !IsGenericName(afterSeason) ? afterSeason : null,
                    Edition = edition,
                    Extra = extra,
                };
            }
        }

        var (movieTitle, movieYear) = SplitTitleAndYear(CutAtJunk(text));
        return new ParsedRelease
        {
            Kind = movieYear is null ? MediaKind.Unknown : MediaKind.Movie,
            Title = movieTitle,
            Year = movieYear,
            Edition = edition,
            Extra = extra,
        };
    }

    /// <summary>
    /// Maps a folder name such as <c>Featurettes</c> or <c>Deleted Scenes</c> to an extra type.
    /// </summary>
    /// <param name="folderName">The folder name.</param>
    /// <returns>The extra type, or <c>null</c> if the folder is not an extras folder.</returns>
    public static ExtraType? ExtraFromFolder(string folderName)
    {
        ArgumentNullException.ThrowIfNull(folderName);

        var f = folderName.Trim();
        if (SampleFolder().IsMatch(f))
        {
            return ExtraType.Sample;
        }

        return Extras.Where(e => e.Type != ExtraType.Sample && e.Pattern.IsMatch(f)).Select(e => (ExtraType?)e.Type).FirstOrDefault()
            ?? (ExtrasFolder().IsMatch(f) ? ExtraType.Other : null);
    }

    /// <summary>
    /// Whether a name says nothing about what it is: <c>episode</c>, <c>video</c>, <c>untitled</c>, <c>01</c>,
    /// <c>track01</c>, <c>title_t00</c>, <c>VTS_01_1</c>, <c>BDMV</c> and the like (a year after the first word makes
    /// it a name: <c>Movie Title 2019</c>).
    /// </summary>
    /// <param name="name">A file stem, folder name or part of a name.</param>
    /// <returns><c>true</c> when the name can't identify anything.</returns>
    public static bool IsGenericName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = Normalise(name);
        return text.Length > 0 && GenericName().IsMatch(text) && !YearToken().Matches(text).Any(y => y.Index > 0);
    }

    // The season a folder above the file gives: "Season 2", "S02", or a release folder such as "Show Season 2"
    private static int? SeasonFromFolders(string path)
    {
        var dir = Path.GetDirectoryName(path);
        for (var depth = 0; depth < 3 && !string.IsNullOrEmpty(dir); depth++)
        {
            var name = Path.GetFileName(dir);
            if (SeasonFolderNumber().Match(name) is { Success: true } m)
            {
                return int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            }

            if (name.Length > 0 && ParseName(name) is { Kind: MediaKind.Episode, Season: { } season, Episode: null })
            {
                return season;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>The nearest ancestor folder that isn't a season or extras folder (e.g. <c>Show/Season 1/x.mkv</c> → <c>Show</c>).</summary>
    private static string TitleFolder(string path)
    {
        var dir = Path.GetDirectoryName(path);
        for (var depth = 0; depth < 3 && !string.IsNullOrEmpty(dir); depth++)
        {
            var name = Path.GetFileName(dir);
            if (name.Length > 0 && !SeasonFolder().IsMatch(name) && ExtraFromFolder(name) is null)
            {
                return name;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return string.Empty;
    }

    private static bool HasVideoOrSubtitleExtension(string fileName)
        => KnownExtension().IsMatch(fileName);

    /// <summary>Separators to spaces, bracketed tags removed except a bracketed year, release-group prefix removed.</summary>
    private static string Normalise(string name)
    {
        var s = GroupPrefix().Replace(name, string.Empty, 1);
        s = GenreBeforeYear().Replace(s, " ");
        s = BracketedYear().Replace(s, " $1 ");
        s = BracketedTag().Replace(s, " ");
        s = Separators().Replace(s, " ");
        return MultiSpace().Replace(s, " ").Trim();
    }

    private static EpisodeMatch? MatchEpisode(string text)
    {
        foreach (var pattern in new[] { SxxEyy(), NxNN(), SeasonEpisodeWords(), CompactSE() })
        {
            var m = pattern.Match(text);
            if (!m.Success)
            {
                continue;
            }

            var season = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            var episodes = m.Groups["e"].Captures.Select(c => int.Parse(c.Value, CultureInfo.InvariantCulture)).ToList();
            if (m.Groups["end"].Success)
            {
                episodes.Add(int.Parse(m.Groups["end"].Value, CultureInfo.InvariantCulture));
            }

            var first = episodes[0];
            var last = episodes.Max();
            return new EpisodeMatch(m.Index, m.Length, season, first, last > first ? last : null);
        }

        return null;
    }

    /// <summary>Everything from the first strong release tag (not at the very start) onwards is dropped.</summary>
    private static string CutAtJunk(string text)
    {
        var m = JunkToken().Match(text);
        while (m.Success && m.Index == 0)
        {
            m = m.NextMatch();
        }

        return m.Success ? text[..m.Index] : text;
    }

    /// <summary>The last plausible year that isn't the very first word is the release year; text before it is the title.</summary>
    private static (string Title, int? Year) SplitTitleAndYear(string text)
    {
        var years = YearToken().Matches(text).Where(y => y.Index > 0).ToList();
        if (years.Count == 0)
        {
            return (CleanTitle(text), null);
        }

        var last = years[^1];
        return (CleanTitle(text[..last.Index]), int.Parse(last.Value, CultureInfo.InvariantCulture));
    }

    private static string CleanTitle(string text)
    {
        var t = EditionWords().Replace(text, " ");
        t = MultiSpace().Replace(t, " ").Trim(' ', '-', '(', ')', ',', '~', '_');
        return t;
    }

    private sealed record EpisodeMatch(int Index, int Length, int Season, int Episode, int? EndingEpisode);

    // ---- episode patterns (tried in this order) --------------------------------------------------------------
    [GeneratedRegex(@"(?<![a-z0-9])s(?<s>[0-9]{1,2})\s?e(?<e>[0-9]{1,3})(?:\s?-?\s?e(?<e>[0-9]{1,3}))*(?:-(?<end>[0-9]{1,3})(?![0-9p]))?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyy();

    [GeneratedRegex(@"(?<![a-z0-9])(?<s>[0-9]{1,2})x(?<e>[0-9]{2,3})(?:[-x](?<e>[0-9]{2,3}))*(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex NxNN();

    [GeneratedRegex(@"\bseason\s?(?<s>[0-9]{1,2})\s?(?:-\s?)?episodes?\s?(?<e>[0-9]{1,3})(?:\s?(?:,|&|-|and)\s?(?<e>[0-9]{1,3}))*", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeWords();

    // "S13SP4": a special aired during season 13. Provider special numbering differs, so the number is not kept.
    [GeneratedRegex(@"(?<![a-z0-9])s[0-9]{1,2}\s?sp\s?[0-9]{1,2}(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialCode();

    // "Lantern1E4": season digits glued to the title, then E + episode
    [GeneratedRegex(@"(?<=[a-z])(?<s>[0-9]{1,2})e(?<e>[0-9]{1,3})(?![0-9a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex CompactSE();

    [GeneratedRegex(@"^(?:season|series|s)\s?(?<s>[0-9]{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderNumber();

    // A season with no episode: "S02", "Season 2", "Series 2", "2nd Season" (a range such as "Season 1-3" gives none)
    [GeneratedRegex(@"(?<![a-z0-9])(?:s(?<s>[0-9]{1,2})(?![0-9a-z])|(?:season|series)\s?(?<s>[0-9]{1,2})(?:\s?-\s?(?<end>[0-9]{1,2}))?(?![0-9a-z])|(?<s>[0-9]{1,2})(?:st|nd|rd|th)\s(?:season|series)(?![a-z]))", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonOnly();

    // Names that say nothing: generic words, optionally numbered, or numbers alone
    [GeneratedRegex(@"^(?:(?:unknown|untitled|episode|ep|e|video|movie|film|clip|title|track|chapter|feature|main|vts|bdmv|stream|output|file|disc|disk)(?![a-z])\s?(?:t?[0-9]+(?![a-z])\s?)*)+$|^[0-9]+(?:\s[0-9]+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericName();

    // A bare episode number: "01", "E01", "Ep 1", "Episode 1"
    [GeneratedRegex(@"^(?:e|ep|episode)?\s?(?<e>[0-9]{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumberOnly();

    [GeneratedRegex(@"^(?:season|series|s)\s?[0-9]{1,2}$|^specials$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"\s+(?:s[0-9]{1,2}|season\s?[0-9]{1,2}(?:\s?-\s?[0-9]{1,2})?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSeason();

    // ---- normalisation ---------------------------------------------------------------------------------------
    // "n00b-Title...": short lowercase/digit group glued to a capitalised title
    [GeneratedRegex(@"^[a-z0-9]{2,12}-(?=[A-Z])")]
    private static partial Regex GroupPrefix();

    // Some uploaders write "Title - Action 1993" / "Title - Family Comedy 1990": drop the genre, keep the year
    [GeneratedRegex(@"\s-\s(?:(?:action|adventure|animation|comedy|crime|drama|family|fantasy|horror|musical|mystery|romance|sci-?fi|thriller|war|western|documentary)\s?){1,3}(?=\s*[\[(]?(?:19|20)[0-9][0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex GenreBeforeYear();

    [GeneratedRegex(@"[\[(]((?:19|20)[0-9][0-9])[\])]")]
    private static partial Regex BracketedYear();

    [GeneratedRegex(@"[\[{][^\]}]*[\]}]")]
    private static partial Regex BracketedTag();

    [GeneratedRegex(@"[._]+|\s+-\s+(?=\S)|(?<=\S)-(?=(?:19|20)[0-9][0-9]\b)")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"(?<![0-9])(?:19[2-9][0-9]|20[0-4][0-9])(?![0-9])")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"\.(?:mkv|mp4|m4v|avi|mov|wmv|ts|mpg|mpeg|webm|srt|ass|ssa|sub|idx|vtt|sup)$", RegexOptions.IgnoreCase)]
    private static partial Regex KnownExtension();

    // Strong tokens only ever appear in release tags: the title ends at the first one.
    [GeneratedRegex(@"(?<![a-z0-9])(?:[0-9]{3,4}[pi]|4k|uhd|hdr(?:10\+?)?|sdr|blu-?ray|bdrip|brrip|web-?dl|web-?rip|web|hdtv|pdtv|dvdrip|hdrip|remux|amzn|dsnp|hmax|atvp|pcok|hulu|x ?26[45]|h ?26[45]|hevc|avc|xvid|divx|av1|10 ?bit|aac[0-9]?|ac3|e-?ac-?3|dts(?:-?hd)?|ddp?[0-9]?|truehd|atmos|flac|opus|eng(?:lish)? subs?|(?:ita|eng|hin|spa|fre|ger|rus)(?: (?:ita|eng|hin|spa|fre|ger|rus))* (?:multi-?)?subs?|(?:ita|eng|hin|spa|fre|ger|rus)(?: (?:ita|eng|hin|spa|fre|ger|rus))+|sub(?: (?:ita|eng|hin|spa|fre|ger|rus))+|multi-?subs?|esubs?|msubs?|tsv|yts|yify|rarbg|eztv|tgx|galaxyrg)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex JunkToken();

    // Weak tokens are also ordinary words ("Internal Affairs", "NF"): only removed from episode titles.
    [GeneratedRegex(@"(?<![a-z0-9])(?:proper|repack|internal|limited|multi|dual|subbed|dubbed|dvd|dv|bd|nf|mp3)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex WeakJunkToken();

    // ---- editions ---------------------------------------------------------------------------------------------
    [GeneratedRegex(@"\bdirector'?s cut\b|\bDC\b", RegexOptions.IgnoreCase)]
    private static partial Regex DirectorsCut();

    [GeneratedRegex(@"\bextended(?: (?:cut|edition))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExtendedCut();

    [GeneratedRegex(@"\bassembly cut\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyCut();

    [GeneratedRegex(@"\bspecial cut\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialCut();

    [GeneratedRegex(@"\bspecial edition\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialEdition();

    [GeneratedRegex(@"\btheatrical(?: cut)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Theatrical();

    [GeneratedRegex(@"\bunrated\b", RegexOptions.IgnoreCase)]
    private static partial Regex Unrated();

    [GeneratedRegex(@"\buncut\b", RegexOptions.IgnoreCase)]
    private static partial Regex Uncut();

    [GeneratedRegex(@"\bredux\b", RegexOptions.IgnoreCase)]
    private static partial Regex Redux();

    [GeneratedRegex(@"\bfinal cut\b", RegexOptions.IgnoreCase)]
    private static partial Regex FinalCut();

    [GeneratedRegex(@"\bultimate edition\b", RegexOptions.IgnoreCase)]
    private static partial Regex UltimateEdition();

    [GeneratedRegex(@"\bimax\b", RegexOptions.IgnoreCase)]
    private static partial Regex Imax();

    [GeneratedRegex(@"\b(?:director'?s cut|extended(?: (?:cut|edition))?|assembly cut|special (?:cut|edition)|theatrical(?: cut)?|unrated|uncut|redux|final cut|ultimate edition|imax|remastered)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EditionWords();

    // ---- extras -----------------------------------------------------------------------------------------------
    [GeneratedRegex(@"(?<![a-z])sample(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex SampleWord();

    [GeneratedRegex(@"^samples?$", RegexOptions.IgnoreCase)]
    private static partial Regex SampleFolder();

    [GeneratedRegex(@"\b(?:trailers?|teasers?|promos?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrailerWord();

    [GeneratedRegex(@"\b(?:deleted|extended) scenes?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeletedWord();

    [GeneratedRegex(@"\b(?:behind the scenes|making of)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BehindWord();

    [GeneratedRegex(@"\binterviews?\b", RegexOptions.IgnoreCase)]
    private static partial Regex InterviewWord();

    [GeneratedRegex(@"\bfea+turettes?\b", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturetteWord();

    [GeneratedRegex(@"\b(?:shorts?|webisodes?|minisodes?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShortWord();

    [GeneratedRegex(@"^(?:extras?|bonus(?: disc)?|specials? features?|other)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtrasFolder();
}
