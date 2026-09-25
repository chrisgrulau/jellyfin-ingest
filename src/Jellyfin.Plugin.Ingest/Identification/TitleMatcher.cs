using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// Fuzzy title comparison tolerant of the ways release names differ from provider titles: punctuation, "&amp;"/"and",
/// a leading "The", accents, roman numerals, typos (edit distance, including transpositions) and extra words. Letters and
/// digits of every script count, so Cyrillic, Greek, CJK, Hebrew, Arabic or Thai titles compare as themselves.
/// </summary>
public static partial class TitleMatcher
{
    private static readonly Dictionary<string, string> Roman = new(StringComparer.Ordinal)
    {
        ["II"] = "2", ["III"] = "3", ["IV"] = "4", ["V"] = "5", ["VI"] = "6", ["VII"] = "7", ["VIII"] = "8", ["IX"] = "9", ["X"] = "10",
    };

    /// <summary>
    /// Normalises a title for comparison: compatibility forms folded (<c>³</c> → <c>3</c>), upper case, accents removed
    /// from Latin, Greek and Cyrillic letters (in other scripts combining marks are part of the spelling and kept),
    /// "&amp;" → "AND", punctuation removed, leading "THE" dropped, roman numerals II–X → digits, single spaces.
    /// </summary>
    /// <param name="title">A title.</param>
    /// <returns>The normalised form.</returns>
    public static string Normalise(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        // FormKD: compatibility decomposition, so "Alien³" compares as "Alien 3" and ligatures split
        var decomposed = title.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            // Accents on Latin, Greek and Cyrillic letters (below U+0530) are dropped: "Amélie" = "Amelie"
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark || sb.Length == 0 || sb[^1] >= '\u0530')
            {
                sb.Append(c);
            }
        }

        var s = sb.ToString().ToUpperInvariant().Replace("&", " AND ", StringComparison.Ordinal);
        s = NonAlphanumeric().Replace(s, " ");
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => Roman.TryGetValue(t, out var d) ? d : t).ToList();
        if (tokens.Count > 1 && tokens[0] == "THE")
        {
            tokens.RemoveAt(0);
        }

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Scores how alike two titles are, from 0 (unrelated) to 1 (same after normalisation).
    /// The better of an edit-distance ratio and a word-overlap score is used, so both typos and extra words are tolerated.
    /// </summary>
    /// <param name="a">First title.</param>
    /// <param name="b">Second title.</param>
    /// <returns>Similarity in [0, 1].</returns>
    public static double Similarity(string a, string b)
    {
        var x = Normalise(a);
        var y = Normalise(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return 0;
        }

        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return 1;
        }

        var edit = 1.0 - ((double)EditDistance(x, y) / Math.Max(x.Length, y.Length));

        var ta = x.Split(' ').ToHashSet(StringComparer.Ordinal);
        var tb = y.Split(' ').ToHashSet(StringComparer.Ordinal);
        var common = ta.Count(tb.Contains);
        var dice = 2.0 * common / (ta.Count + tb.Count);
        var containment = (double)common / Math.Min(ta.Count, tb.Count);
        var words = (0.4 * dice) + (0.5 * containment);

        return Math.Clamp(Math.Max(edit, words), 0, 1);
    }

    /// <summary>Optimal-string-alignment distance: insertions, deletions, substitutions and adjacent transpositions.</summary>
    private static int EditDistance(string s, string t)
    {
        // Three rolling rows: the transposition check needs the row before the previous one
        var prev2 = new int[t.Length + 1];
        var prev = new int[t.Length + 1];
        var cur = new int[t.Length + 1];
        for (var j = 0; j <= t.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= s.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                {
                    cur[j] = Math.Min(cur[j], prev2[j - 2] + 1);
                }
            }

            (prev2, prev, cur) = (prev, cur, prev2);
        }

        return prev[t.Length];
    }

    [GeneratedRegex(@"[^\p{L}\p{M}\p{Nd} ]+")]
    private static partial Regex NonAlphanumeric();
}
