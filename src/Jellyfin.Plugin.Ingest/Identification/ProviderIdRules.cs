using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// Which provider ids are trusted. Ids end up inside folder names (<c>[tmdbid-123]</c>), and they come from metadata
/// plugins, NFO files and library edits, so anything that isn't the expected shape is dropped rather than used.
/// </summary>
public static partial class ProviderIdRules
{
    /// <summary>
    /// Whether an id is safe and well-formed for its provider: TMDb and TheTVDB are 1–10 digits, IMDb is <c>tt</c>
    /// plus 5–10 digits. Other providers' ids (only used for lookups, never in paths) must be short plain tokens.
    /// </summary>
    /// <param name="provider">Provider name (<c>Tmdb</c>, <c>Tvdb</c>, <c>Imdb</c> …).</param>
    /// <param name="value">The id.</param>
    /// <returns><c>true</c> if it can be used.</returns>
    public static bool IsValid(string provider, string? value)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return provider.ToUpperInvariant() switch
        {
            "TMDB" or "TVDB" => Numeric().IsMatch(value),
            "IMDB" => Imdb().IsMatch(value),
            _ => Token().IsMatch(value),
        };
    }

    /// <summary>
    /// Keeps only valid ids.
    /// </summary>
    /// <param name="ids">Ids as received.</param>
    /// <returns>The valid ones, keyed case-insensitively.</returns>
    public static Dictionary<string, string> Clean(IEnumerable<KeyValuePair<string, string>>? ids)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, value) in ids ?? [])
        {
            if (!string.IsNullOrEmpty(provider) && IsValid(provider, value))
            {
                clean[provider] = value;
            }
        }

        return clean;
    }

    [GeneratedRegex(@"^\d{1,10}$")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"^tt\d{5,10}$")]
    private static partial Regex Imdb();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,40}$")]
    private static partial Regex Token();
}
