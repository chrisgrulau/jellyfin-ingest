using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// Keeps every file operation inside the folders it belongs to. Paths are compared after normalisation (absolute, no
/// trailing separator, <c>..</c> resolved), case-insensitively where the platform's file system usually is.
/// </summary>
public static class PathGuard
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Gets the comparer for normalised paths: case-insensitive where the platform's file system usually is.
    /// </summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Whether two paths name the same folder or file (<c>/in</c> and <c>/in/</c> do).
    /// </summary>
    /// <param name="a">First path.</param>
    /// <param name="b">Second path.</param>
    /// <returns><c>true</c> if they are the same after normalisation; <c>false</c> if either is empty.</returns>
    public static bool SamePath(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && Normalise(a).Equals(Normalise(b), Comparison);

    /// <summary>
    /// Tidies a configured folder: trimmed, and for a full path, normalised (so <c>/in/</c> is stored as <c>/in</c>).
    /// Anything else is returned trimmed, for the folder rules to report.
    /// </summary>
    /// <param name="path">The folder as entered.</param>
    /// <returns>The tidied folder.</returns>
    public static string Tidy(string? path)
    {
        var p = (path ?? string.Empty).Trim();
        return p.Length > 0 && Path.IsPathFullyQualified(p) ? Normalise(p) : p;
    }

    /// <summary>
    /// Normalises a path for comparison.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>Absolute, without a trailing separator, with <c>.</c> and <c>..</c> resolved.</returns>
    public static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="root"/> itself or inside it.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="root">The folder.</param>
    /// <returns><c>true</c> if it is the same folder or below it.</returns>
    public static bool IsSameOrUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        var p = Normalise(path);
        var r = Normalise(root);

        // A drive or file-system root keeps its separator after normalising (E:\, /)
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.Equals(r, Comparison) || p.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// The real location of a path: every existing folder along it that is a symbolic link or junction is replaced by
    /// its final target. Parts that don't exist are kept as written.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The resolved, normalised path.</returns>
    public static string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Normalise(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Normalise(target.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: compared as written
            }
        }

        return Normalise(current);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is strictly inside <paramref name="root"/> (not the folder itself).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="root">The folder.</param>
    /// <returns><c>true</c> if it is below the folder.</returns>
    public static bool IsUnder(string path, string root)
        => IsSameOrUnder(path, root) && !Normalise(path).Equals(Normalise(root), Comparison);

    /// <summary>
    /// Whether <paramref name="path"/> is strictly inside any of <paramref name="roots"/>.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="roots">Allowed folders.</param>
    /// <returns><c>true</c> if it is below one of them.</returns>
    public static bool IsUnderAny(string path, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return roots.Any(r => IsUnder(path, r));
    }
}
