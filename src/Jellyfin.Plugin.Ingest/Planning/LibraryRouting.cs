using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// What a library can hold, as far as Ingest is concerned.
/// </summary>
public enum LibraryKind
{
    /// <summary>A library Ingest doesn't file into (music, books, home videos …).</summary>
    Other = 0,

    /// <summary>A Movies library.</summary>
    Films,

    /// <summary>A Shows library.</summary>
    Shows,

    /// <summary>A Mixed Movies and Shows library.</summary>
    Mixed,
}

/// <summary>
/// A Jellyfin library, reduced to what routing needs.
/// </summary>
/// <param name="Id">Library id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Locations">Its folders.</param>
public sealed record MediaLibrary(string Id, string Name, LibraryKind Kind, IReadOnlyList<string> Locations);

/// <summary>
/// A configured destination: a library and optionally one of its folders.
/// </summary>
/// <param name="LibraryId">Library id.</param>
/// <param name="Path">One of the library's folders; empty for its first.</param>
public sealed record DestinationSetting(string LibraryId, string? Path);

/// <summary>
/// The outcome of routing a watch folder's destinations.
/// </summary>
/// <param name="Targets">Where shows and films go.</param>
/// <param name="Problems">Destinations that were ignored, and why.</param>
public sealed record RoutingResult(LibraryTargets Targets, IReadOnlyList<string> Problems);

/// <summary>
/// Maps configured destinations to filing targets. Pure: no Jellyfin types, so it is shared by the service, the API
/// and the tests.
/// </summary>
public static class LibraryRouting
{
    /// <summary>
    /// Maps Jellyfin's collection type to a <see cref="LibraryKind"/>.
    /// </summary>
    /// <param name="collectionType">The collection type (<c>movies</c>, <c>tvshows</c>, <c>mixed</c> …), or <c>null</c> for a mixed library.</param>
    /// <returns>The kind.</returns>
    public static LibraryKind KindOf(string? collectionType) => collectionType switch
    {
        "movies" => LibraryKind.Films,
        "tvshows" => LibraryKind.Shows,
        null or "" or "mixed" => LibraryKind.Mixed,
        _ => LibraryKind.Other,
    };

    /// <summary>
    /// The targets one library provides: a Movies library takes films, a Shows library shows, a mixed library both.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="path">One of its folders; anything else (or empty) means the folder with the most free space when
    /// <paramref name="freeBytes"/> is given, else its first folder.</param>
    /// <param name="freeBytes">The free space on a folder's drive (<c>null</c> if unknown), for libraries with several
    /// folders; <c>null</c> to always use the first.</param>
    /// <returns>The targets; both <c>null</c> for other library types or a library without folders.</returns>
    public static LibraryTargets TargetsOf(MediaLibrary library, string? path, Func<string, long?>? freeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        var root = library.Locations.FirstOrDefault(l => PathGuard.SamePath(l, path)) ?? RoomiestOf(library.Locations, freeBytes);
        if (string.IsNullOrWhiteSpace(root))
        {
            return new LibraryTargets(null, null);
        }

        return library.Kind switch
        {
            LibraryKind.Films => new LibraryTargets(null, new LibraryTarget(root, IsTv: false)),
            LibraryKind.Shows => new LibraryTargets(new LibraryTarget(root, IsTv: true), null),
            LibraryKind.Mixed => new LibraryTargets(new LibraryTarget(root, IsTv: true), new LibraryTarget(root, IsTv: false)),
            _ => new LibraryTargets(null, null),
        };
    }

    /// <summary>
    /// The folder with the most free space, for new titles in a library with several folders (existing film and show
    /// folders are still used wherever they are). The first folder wins ties, and when no folder's space is known.
    /// </summary>
    /// <param name="locations">The library's folders.</param>
    /// <param name="freeBytes">The free space on a folder's drive, or <c>null</c> if unknown; <c>null</c> to take the first.</param>
    /// <returns>The folder, or <c>null</c> if there are none.</returns>
    public static string? RoomiestOf(IReadOnlyList<string> locations, Func<string, long?>? freeBytes)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (locations.Count == 0)
        {
            return null;
        }

        if (locations.Count == 1 || freeBytes is null)
        {
            return locations[0];
        }

        string? best = null;
        long most = -1;
        foreach (var location in locations.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            if (freeBytes(location) is { } free && free > most)
            {
                (best, most) = (location, free);
            }
        }

        return best ?? locations[0];
    }

    /// <summary>
    /// Routes a watch folder's destinations. At most one destination may take each kind; a destination that overlaps an
    /// earlier one (e.g. a Movies library after a mixed one) is ambiguous and ignored, and reported.
    /// </summary>
    /// <param name="destinations">The configured destinations, in order.</param>
    /// <param name="libraries">The server's libraries.</param>
    /// <param name="freeBytes">The free space on a folder's drive, to choose between a library's folders when a
    /// destination names none (see <see cref="TargetsOf"/>); <c>null</c> for the first folder.</param>
    /// <returns>The targets and any problems.</returns>
    public static RoutingResult Route(IEnumerable<DestinationSetting> destinations, IReadOnlyCollection<MediaLibrary> libraries, Func<string, long?>? freeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentNullException.ThrowIfNull(libraries);

        LibraryTarget? tv = null, films = null;
        var problems = new List<string>();
        foreach (var d in destinations.Where(d => !string.IsNullOrWhiteSpace(d.LibraryId)))
        {
            var library = libraries.FirstOrDefault(l => string.Equals(l.Id, d.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                problems.Add($"Library {d.LibraryId} no longer exists.");
                continue;
            }

            var t = TargetsOf(library, d.Path, freeBytes);
            if (t.Tv is null && t.Films is null)
            {
                problems.Add($"'{library.Name}' isn't a Movies, Shows or mixed library.");
                continue;
            }

            if ((t.Tv is not null && tv is not null) || (t.Films is not null && films is not null))
            {
                problems.Add($"'{library.Name}' overlaps another destination (only one library per kind); ignored.");
                continue;
            }

            tv ??= t.Tv;
            films ??= t.Films;
        }

        return new RoutingResult(new LibraryTargets(tv, films), problems);
    }

    /// <summary>
    /// A fingerprint of the settings that decide what happens to a watch folder's releases (dry run and destinations);
    /// when it changes, releases already handled are planned again.
    /// </summary>
    /// <param name="dryRun">Whether dry run is on.</param>
    /// <param name="destinations">The watch folder's destinations.</param>
    /// <returns>The fingerprint.</returns>
    public static string SettingsFingerprint(bool dryRun, IEnumerable<DestinationSetting> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        return string.Join('\u0001', destinations.Select(d => d.LibraryId + "\u0002" + d.Path).Prepend(dryRun ? "dry" : "live"));
    }
}
