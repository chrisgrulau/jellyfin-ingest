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
        return p.Equals(r, Comparison) || p.StartsWith(r + Path.DirectorySeparatorChar, Comparison);
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
