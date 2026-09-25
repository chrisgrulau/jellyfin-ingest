using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Ingest.Naming;

/// <summary>
/// Describes a subtitle file that will sit next to a video.
/// </summary>
public sealed record SubtitleTrack
{
    /// <summary>Gets the language as an ISO 639-1 code (e.g. <c>en</c>), if known.</summary>
    public string? Language { get; init; }

    /// <summary>Gets a value indicating whether this is an SDH / hearing-impaired track.</summary>
    public bool HearingImpaired { get; init; }

    /// <summary>Gets a value indicating whether this is a forced (foreign-parts-only) track.</summary>
    public bool Forced { get; init; }

    /// <summary>Gets a value indicating whether Jellyfin should select this track by default.</summary>
    public bool Default { get; init; }

    /// <summary>Gets an optional display title, e.g. <c>Commentary</c>.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// Names external subtitle files the way Jellyfin parses them: <c>&lt;video stem&gt;[.Title].&lt;lang&gt;[.default][.sdh][.forced].ext</c>.
/// Any token that is not a language or flag becomes the track title, so disambiguation uses a readable title
/// (<c>Alternate 2</c>) rather than a bare number.
/// </summary>
public static class SubtitleNamer
{
    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en", ["eng"] = "en", ["english"] = "en",
        ["es"] = "es", ["spa"] = "es", ["spanish"] = "es",
        ["fr"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["french"] = "fr",
        ["de"] = "de", ["ger"] = "de", ["deu"] = "de", ["german"] = "de",
        ["it"] = "it", ["ita"] = "it", ["italian"] = "it",
        ["pt"] = "pt", ["por"] = "pt", ["portuguese"] = "pt",
        ["nl"] = "nl", ["dut"] = "nl", ["nld"] = "nl", ["dutch"] = "nl",
        ["ja"] = "ja", ["jpn"] = "ja", ["japanese"] = "ja",
        ["ko"] = "ko", ["kor"] = "ko", ["korean"] = "ko",
        ["zh"] = "zh", ["chi"] = "zh", ["zho"] = "zh", ["chinese"] = "zh",
    };

    /// <summary>
    /// Normalises a language name or ISO 639-1/-2 code to a two-letter code.
    /// </summary>
    /// <param name="value">For example <c>English</c>, <c>eng</c> or <c>en</c>.</param>
    /// <returns>The ISO 639-1 code, or <c>null</c> if not recognised.</returns>
    public static string? NormaliseLanguage(string? value)
        => value is not null && LanguageAliases.TryGetValue(value.Trim(), out var code) ? code : null;

    /// <summary>
    /// Builds a sidecar subtitle file name that does not collide with existing names.
    /// </summary>
    /// <param name="videoStem">The video's file name without extension.</param>
    /// <param name="track">The subtitle description.</param>
    /// <param name="extension">Subtitle extension including the dot, e.g. <c>.srt</c>.</param>
    /// <param name="isTaken">Returns <c>true</c> if a candidate file name is already used.</param>
    /// <returns>The file name.</returns>
    public static string SidecarName(string videoStem, SubtitleTrack track, string extension, Func<string, bool> isTaken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoStem);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentNullException.ThrowIfNull(isTaken);

        var title = FileNameSanitizer.Truncate(FileNameSanitizer.Sanitize(track.Title ?? string.Empty).Replace(".", " ", StringComparison.Ordinal).Trim(), 40);
        var candidate = Build(videoStem, title, track, extension);
        for (var n = 2; isTaken(candidate); n++)
        {
            var alt = string.Create(CultureInfo.InvariantCulture, $"Alternate {n}");
            candidate = Build(videoStem, title.Length > 0 ? $"{title} {n}" : alt, track, extension);
        }

        return candidate;
    }

    private static string Build(string stem, string title, SubtitleTrack track, string extension)
    {
        var name = new StringBuilder(stem);
        if (title.Length > 0)
        {
            name.Append('.').Append(title);
        }

        if (NormaliseLanguage(track.Language) is { } lang)
        {
            name.Append('.').Append(lang);
        }

        if (track.Default)
        {
            name.Append(".default");
        }

        if (track.HearingImpaired)
        {
            name.Append(".sdh");
        }

        if (track.Forced)
        {
            name.Append(".forced");
        }

        return name.Append(extension.StartsWith('.') ? extension : "." + extension).ToString();
    }
}
