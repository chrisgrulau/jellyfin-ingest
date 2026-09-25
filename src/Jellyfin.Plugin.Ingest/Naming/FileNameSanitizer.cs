using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Ingest.Naming;

/// <summary>
/// Makes titles safe for use in file and folder names, following Jellyfin's naming guidance.
/// </summary>
public static partial class FileNameSanitizer
{
    /// <summary>
    /// The most UTF-8 bytes a title may take in a file or folder name. Names are built from a title plus a year, ids,
    /// an episode code, subtitle flags and an extension, and most file systems allow 255 bytes per name.
    /// </summary>
    public const int MaxTitleBytes = 100;

    /// <summary>The most UTF-8 bytes a file name may take before its subtitle flags and extension are added.</summary>
    public const int MaxStemBytes = 180;

    /// <summary>
    /// Removes characters Jellyfin reserves (<c>&lt; &gt; : " / \ | ? *</c>) and control characters, turns
    /// <c>:</c> into <c> -</c>, collapses whitespace and strips leading and trailing dots and spaces (a leading dot
    /// would make a hidden file or folder, which Jellyfin ignores).
    /// </summary>
    /// <param name="value">A title or name fragment.</param>
    /// <returns>The sanitised text; empty if nothing printable remains.</returns>
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case ':':
                    builder.Append(" -");
                    break;
                case '/' or '\\':
                    builder.Append('-');
                    break;
                case '<' or '>' or '"' or '|' or '?' or '*':
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        var collapsed = Whitespace().Replace(builder.ToString(), " ");
        return collapsed.Trim().Trim('.', ' ');
    }

    /// <summary>
    /// Shortens text to at most <paramref name="maxBytes"/> UTF-8 bytes, cutting between whole characters (never inside a
    /// surrogate pair or combining sequence), then strips trailing dots and spaces.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="maxBytes">The limit in UTF-8 bytes.</param>
    /// <returns>The text, shortened if needed.</returns>
    public static string Truncate(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var result = new StringBuilder();
        var bytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > maxBytes)
            {
                break;
            }

            result.Append(element);
            bytes += size;
        }

        return result.ToString().TrimEnd('.', ' ');
    }

    /// <summary>
    /// Whether a name is one Windows reserves for devices (<c>CON</c>, <c>NUL</c>, <c>COM1</c> …, with or without an
    /// extension), which can't be used as a file or folder name there.
    /// </summary>
    /// <param name="name">A file or folder name.</param>
    /// <returns><c>true</c> if reserved.</returns>
    public static bool IsReservedOnWindows(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return ReservedName().IsMatch((dot >= 0 ? name[..dot] : name).TrimEnd(' '));
    }

    /// <summary>
    /// Makes a complete file or folder name safe: a name Windows reserves gets a trailing underscore.
    /// </summary>
    /// <param name="name">The name (without extension).</param>
    /// <returns>The name, changed only if reserved.</returns>
    public static string AvoidReserved(string name)
        => IsReservedOnWindows(name ?? throw new ArgumentNullException(nameof(name))) ? name + "_" : name;

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
