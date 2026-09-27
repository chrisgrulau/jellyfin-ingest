using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Ingest.Presentation;

/// <summary>
/// Short, plain wording for the reasons and messages the sweep records (the full text stays in the details).
/// </summary>
public static class Friendly
{
    /// <summary>The longest sentence shown before it is shortened with an ellipsis.</summary>
    public const int MaxSentence = 160;

    // First match wins: a phrase in the recorded reason, and what the page says instead
    private static readonly (string Contains, string Say)[] Phrases =
    [
        ("asks before filing what the AI decided", "the AI made a suggestion for you to approve"),
        ("too close to call", "two matches look alike"),
        ("close candidates", "two matches look alike"),
        ("is already on the server", "it's already on the server"),
        ("appears more than once", "the same episode is in it twice"),
        ("has no TV library", "there's no Shows library to file it into"),
        ("has no film library", "there's no Movies library to file it into"),
        ("season or episode number can't be read", "which episode is it?"),
        ("episode number is unknown", "which episode is it?"),
        ("No title could be read", "the name doesn't say what it is"),
        ("scored", "no confident match"),
        ("No main video", "there's no video in it"),
        ("library folder isn't available", "the library folder is offline; trying again by itself"),
        ("Destination already exists", "something is already where it would go"),
        ("Refused:", "a safety check stopped it"),
        ("Extra can't be tied", "an extra that doesn't belong to one title"),
        ("symbolic link", "it's a link, and links aren't followed"),
        ("can't read this", "Jellyfin can't read it"),
        ("Undone by an administrator", "it was undone; choose what to do"),
        ("Restored from quarantine", "it was restored; choose what to do"),
        ("found nothing", "the providers found nothing; trying again by itself"),
    ];

    /// <summary>
    /// A review reason in a few plain words, e.g. <c>two matches look alike</c>.
    /// </summary>
    /// <param name="reason">The recorded reason.</param>
    /// <returns>The short form, or the reason's first sentence when none fits.</returns>
    public static string Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "it needs a decision";
        }

        foreach (var (contains, say) in Phrases)
        {
            if (reason.Contains(contains, StringComparison.OrdinalIgnoreCase))
            {
                return say;
            }
        }

        var first = FirstSentence(reason).TrimEnd('.');
        return first.Length > 1 ? char.ToLowerInvariant(first[0]) + first[1..] : first;
    }

    /// <summary>
    /// What several files' reasons come to: the one reason when they agree, else how many files need a decision.
    /// </summary>
    /// <param name="reasons">The recorded reasons.</param>
    /// <returns>The short form.</returns>
    public static string Reasons(IReadOnlyCollection<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        var distinct = reasons.Select(Reason).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count switch
        {
            0 => "it needs a decision",
            1 => distinct[0],
            _ => string.Create(CultureInfo.InvariantCulture, $"{reasons.Count} files need a decision"),
        };
    }

    /// <summary>
    /// The first sentence of a message, shortened to <see cref="MaxSentence"/> characters.
    /// </summary>
    /// <param name="text">The message.</param>
    /// <returns>The sentence, ending with a full stop (or an ellipsis when shortened).</returns>
    public static string FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Trim();
        var end = -1;
        for (var i = 0; i < text.Length - 1; i++)
        {
            // A full stop followed by a space and a capital ends a sentence; "e.g. x" and "1.5 GB" don't
            if (text[i] is '.' or '!' or '?' && text[i + 1] == ' ' && i + 2 < text.Length && char.IsUpper(text[i + 2]))
            {
                end = i;
                break;
            }
        }

        var sentence = end >= 0 ? text[..(end + 1)] : text;
        if (sentence.Length > MaxSentence)
        {
            var cut = sentence.LastIndexOf(' ', MaxSentence - 1);
            return sentence[..(cut > MaxSentence / 2 ? cut : MaxSentence - 1)].TrimEnd(',', ';', ':', ' ') + "…";
        }

        return EndSentence(sentence);
    }

    /// <summary>
    /// Ends a sentence with a full stop unless it already ends with <c>.</c>, <c>?</c>, <c>!</c> or <c>…</c>
    /// (looking past a closing bracket or quote), so "which episode is it?" never becomes "it?.".
    /// </summary>
    /// <param name="sentence">The sentence.</param>
    /// <returns>The sentence with exactly one closing mark.</returns>
    public static string EndSentence(string? sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return string.Empty;
        }

        sentence = sentence.TrimEnd();
        var bare = sentence.TrimEnd(')', ']', '\'', '"', '’', '”');
        return bare.Length > 0 && bare[^1] is '.' or '?' or '!' or '…' ? sentence : sentence + ".";
    }

    /// <summary>
    /// A size in words, e.g. <c>1.2 MB</c> (1024-based, as the page always showed it).
    /// </summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The size in words.</returns>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes")
            : (value < 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }
}
