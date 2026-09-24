using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Ingest.Naming;

/// <summary>
/// Makes titles safe for use in file and folder names, following Jellyfin's naming guidance.
/// </summary>
public static partial class FileNameSanitizer
{
    /// <summary>
    /// Removes characters Jellyfin reserves (<c>&lt; &gt; : " / \ | ? *</c>) and control characters, turns
    /// <c>:</c> into <c> -</c>, collapses whitespace and strips trailing dots and spaces.
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
        return collapsed.Trim().TrimEnd('.', ' ');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
