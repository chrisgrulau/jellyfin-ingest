using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// Guesses a subtitle's language from its text using very common function words. Good enough to label an untagged
/// <c>.srt</c>; returns <c>null</c> rather than guess when the text is short or ambiguous.
/// </summary>
public static partial class SubtitleLanguageSniffer
{
    private static readonly Dictionary<string, HashSet<string>> StopWords = new(StringComparer.Ordinal)
    {
        ["en"] = ["THE", "YOU", "AND", "THAT", "TO", "IT", "WHAT", "IS", "OF", "IN", "THIS", "HAVE", "NOT", "FOR", "YOUR"],
        ["es"] = ["QUE", "DE", "NO", "EL", "LA", "LOS", "ES", "POR", "UN", "UNA", "ME", "LO"],
        ["fr"] = ["JE", "DE", "PAS", "LE", "LA", "LES", "EST", "VOUS", "QUE", "ET", "UN", "UNE"],
        ["de"] = ["ICH", "DIE", "UND", "DER", "NICHT", "DAS", "IST", "SIE", "DU", "ES"],
        ["it"] = ["CHE", "NON", "DI", "IL", "LA", "PER", "SONO", "E", "UN", "MI", "UNA", "TI"],
        ["pt"] = ["QUE", "NÃO", "DE", "O", "A", "VOCÊ", "É", "UM", "UMA", "EU", "SE"],
    };

    /// <summary>
    /// Guesses the language of subtitle text.
    /// </summary>
    /// <param name="text">Subtitle file content.</param>
    /// <returns>An ISO 639-1 code, or <c>null</c> when unsure.</returns>
    public static string? Guess(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = Word().Matches(Markup().Replace(text, " ")).Select(m => m.Value.ToUpperInvariant()).ToList();
        if (words.Count < 200)
        {
            return null;
        }

        var scores = StopWords.Select(kv => (Lang: kv.Key, Share: (double)words.Count(kv.Value.Contains) / words.Count))
            .OrderByDescending(s => s.Share)
            .ToList();
        return scores[0].Share > 0.08 && scores[0].Share - scores[1].Share > 0.03 ? scores[0].Lang : null;
    }

    [GeneratedRegex(@"<[^>]+>|\{[^}]*\}|-->|\d+:\d+:\d+[,.]\d+")]
    private static partial Regex Markup();

    [GeneratedRegex(@"[\p{L}']+")]
    private static partial Regex Word();
}
