using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Parses a file name or path. When the file name alone has no usable title (e.g. <c>S01E04.mkv</c>),
    /// the parent folder name supplies it.
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
        if (parsed.Title.Length == 0 && titleFolder.Length > 0)
        {
            var fromParent = ParseName(titleFolder);
            fromParent = fromParent with { Title = TrailingSeason().Replace(fromParent.Title, string.Empty).Trim() };
            parsed = parsed with
            {
                Title = fromParent.Title,
                Year = parsed.Year ?? fromParent.Year,
                Season = parsed.Season ?? fromParent.Season,
                Kind = parsed.Kind == MediaKind.Unknown ? fromParent.Kind : parsed.Kind,
            };
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
    [GeneratedRegex(@"(?<![a-z0-9])s(?<s>\d{1,2})\s?e(?<e>\d{1,3})(?:\s?-?\s?e(?<e>\d{1,3}))*(?:-(?<end>\d{1,3})(?![0-9p]))?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyy();

    [GeneratedRegex(@"(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{2,3})(?:[-x](?<e>\d{2,3}))*(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex NxNN();

    [GeneratedRegex(@"\bseason\s?(?<s>\d{1,2})\s?(?:-\s?)?episodes?\s?(?<e>\d{1,3})(?:\s?(?:,|&|-|and)\s?(?<e>\d{1,3}))*", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeWords();

    // "S13SP4": a special aired during season 13. Provider special numbering differs, so the number is not kept.
    [GeneratedRegex(@"(?<![a-z0-9])s\d{1,2}\s?sp\s?\d{1,2}(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialCode();

    // "Lantern1E4": season digits glued to the title, then E + episode
    [GeneratedRegex(@"(?<=[a-z])(?<s>\d{1,2})e(?<e>\d{1,3})(?![0-9a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex CompactSE();

    [GeneratedRegex(@"^(?:season|series|s)\s?\d{1,2}$|^specials$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"\s+(?:s\d{1,2}|season\s?\d{1,2}(?:\s?-\s?\d{1,2})?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSeason();

    // ---- normalisation ---------------------------------------------------------------------------------------
    // "n00b-Title...": short lowercase/digit group glued to a capitalised title
    [GeneratedRegex(@"^[a-z0-9]{2,12}-(?=[A-Z])")]
    private static partial Regex GroupPrefix();

    // Some uploaders write "Title - Action 1993" / "Title - Family Comedy 1990": drop the genre, keep the year
    [GeneratedRegex(@"\s-\s(?:(?:action|adventure|animation|comedy|crime|drama|family|fantasy|horror|musical|mystery|romance|sci-?fi|thriller|war|western|documentary)\s?){1,3}(?=\s*[\[(]?(?:19|20)\d\d)", RegexOptions.IgnoreCase)]
    private static partial Regex GenreBeforeYear();

    [GeneratedRegex(@"[\[(]((?:19|20)\d\d)[\])]")]
    private static partial Regex BracketedYear();

    [GeneratedRegex(@"[\[{][^\]}]*[\]}]")]
    private static partial Regex BracketedTag();

    [GeneratedRegex(@"[._]+|\s+-\s+(?=\S)|(?<=\S)-(?=(?:19|20)\d\d\b)")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"(?<!\d)(?:19[2-9]\d|20[0-4]\d)(?!\d)")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"\.(?:mkv|mp4|m4v|avi|mov|wmv|ts|mpg|mpeg|webm|srt|ass|ssa|sub|idx|vtt|sup)$", RegexOptions.IgnoreCase)]
    private static partial Regex KnownExtension();

    // Strong tokens only ever appear in release tags: the title ends at the first one.
    [GeneratedRegex(@"(?<![a-z0-9])(?:\d{3,4}[pi]|4k|uhd|hdr(?:10\+?)?|sdr|blu-?ray|bdrip|brrip|web-?dl|web-?rip|web|hdtv|pdtv|dvdrip|hdrip|remux|amzn|dsnp|hmax|atvp|pcok|hulu|x ?26[45]|h ?26[45]|hevc|avc|xvid|divx|av1|10 ?bit|aac\d?|ac3|e-?ac-?3|dts(?:-?hd)?|ddp?\d?|truehd|atmos|flac|opus|eng(?:lish)? subs?|(?:ita|eng|hin|spa|fre|ger|rus)(?: (?:ita|eng|hin|spa|fre|ger|rus))* (?:multi-?)?subs?|(?:ita|eng|hin|spa|fre|ger|rus)(?: (?:ita|eng|hin|spa|fre|ger|rus))+|sub(?: (?:ita|eng|hin|spa|fre|ger|rus))+|multi-?subs?|esubs?|msubs?|tsv|yts|yify|rarbg|eztv|tgx|galaxyrg)(?![a-z0-9])", RegexOptions.IgnoreCase)]
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
