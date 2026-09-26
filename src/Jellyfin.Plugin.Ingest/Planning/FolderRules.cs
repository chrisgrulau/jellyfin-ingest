using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// A configured folder that isn't safe to use.
/// </summary>
/// <param name="Folder">The folder as configured (a watch folder, or the quarantine folder).</param>
/// <param name="Problem">Why it can't be used, in plain language.</param>
public sealed record FolderProblem(string Folder, string Problem)
{
    /// <summary>Gets a value indicating whether this is only a warning (saving is allowed).</summary>
    public bool Warning { get; init; }
}

/// <summary>
/// Which watch and quarantine folders are safe. Everything in a watch folder gets renamed, moved or quarantined (and
/// quarantined files are deleted later), so a watch folder must never be, contain or sit inside a library, Jellyfin's own
/// folders, or another watch folder; the quarantine must never overlap a library or Jellyfin's folders.
/// </summary>
public static class FolderRules
{
    /// <summary>
    /// Checks a set of folder settings.
    /// </summary>
    /// <param name="watchFolders">The watch folder paths.</param>
    /// <param name="quarantine">The custom quarantine folder, or empty for the default (inside each watch folder).</param>
    /// <param name="libraryFolders">Every folder of every library on the server.</param>
    /// <param name="protectedFolders">Jellyfin's own folders (data, configuration, cache, logs, program files …).</param>
    /// <returns>The problems found; empty when everything is safe.</returns>
    public static IReadOnlyList<FolderProblem> Check(IReadOnlyList<string> watchFolders, string? quarantine, IReadOnlyCollection<string> libraryFolders, IReadOnlyCollection<string> protectedFolders)
    {
        ArgumentNullException.ThrowIfNull(watchFolders);
        ArgumentNullException.ThrowIfNull(libraryFolders);
        ArgumentNullException.ThrowIfNull(protectedFolders);

        var problems = new List<FolderProblem>();
        for (var i = 0; i < watchFolders.Count; i++)
        {
            var w = watchFolders[i];
            if (Basic(w) is { } basic)
            {
                problems.Add(new FolderProblem(w, basic));
                continue;
            }

            if (libraryFolders.FirstOrDefault(l => Overlap(w, l)) is { } lib)
            {
                problems.Add(new FolderProblem(w, $"It overlaps the library folder {lib}. A watch folder must be separate from every library, or filed media would be processed again."));
            }
            else if (protectedFolders.FirstOrDefault(p => Overlap(w, p)) is { } prot)
            {
                problems.Add(new FolderProblem(w, $"It overlaps Jellyfin's own folder {prot}."));
            }
            else if (watchFolders.Where((other, j) => j != i && Basic(other) is null).FirstOrDefault(o => Overlap(w, o)) is { } other)
            {
                problems.Add(new FolderProblem(w, $"It overlaps another watch folder, {other}."));
            }
        }

        if (!string.IsNullOrWhiteSpace(quarantine))
        {
            if (Basic(quarantine) is { } basic)
            {
                problems.Add(new FolderProblem(quarantine, basic));
            }
            else if (libraryFolders.FirstOrDefault(l => Overlap(quarantine, l)) is { } lib)
            {
                problems.Add(new FolderProblem(quarantine, $"The quarantine overlaps the library folder {lib}; quarantined files are deleted after the retention period."));
            }
            else if (protectedFolders.FirstOrDefault(p => Overlap(quarantine, p)) is { } prot)
            {
                problems.Add(new FolderProblem(quarantine, $"The quarantine overlaps Jellyfin's own folder {prot}."));
            }
            else if (watchFolders.FirstOrDefault(w => Basic(w) is null && PathGuard.IsSameOrUnder(w, quarantine)) is { } watch)
            {
                problems.Add(new FolderProblem(quarantine, $"The quarantine is the watch folder {watch} or contains it (inside a watch folder is fine)."));
            }
        }

        return problems;
    }

    private static string? Basic(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return "Use a full path, e.g. /media/incoming.";
        }

        var full = PathGuard.Normalise(path);
        return string.Equals(full, PathGuard.Normalise(Path.GetPathRoot(full) ?? full), StringComparison.Ordinal)
            ? "A whole drive or the root folder can't be used."
            : null;
    }

    // Compared as written and after following folder links, so a path that reaches a library through a link is caught
    private static bool Overlap(string a, string b)
        => !string.IsNullOrWhiteSpace(b) && Path.IsPathFullyQualified(b)
            && (Lexical(a, b) || Lexical(PathGuard.Resolve(a), PathGuard.Resolve(b)));

    private static bool Lexical(string a, string b) => PathGuard.IsSameOrUnder(a, b) || PathGuard.IsSameOrUnder(b, a);
}
