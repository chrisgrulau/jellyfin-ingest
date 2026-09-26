using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ingest.Parsing;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// Looks at what's already on disk for a film or show, whatever naming it uses. Pure: directory listings are passed in,
/// so it can be tested without a file system.
/// </summary>
public static partial class ExistingFiles
{
    /// <summary>
    /// Finds a video file for an episode anywhere in a series folder: in any season folder (<c>Season 01</c>,
    /// <c>Season 1</c>, <c>S01</c>, <c>Specials</c> …) or loose in the series folder, including multi-episode files that
    /// cover it.
    /// </summary>
    /// <param name="seriesFolder">The series folder.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="listDirectories">Lists the sub-folders of a folder (empty if it doesn't exist).</param>
    /// <param name="listFiles">Lists the files in a folder (empty if it doesn't exist).</param>
    /// <returns>The existing file, or <c>null</c>.</returns>
    public static string? FindEpisode(string seriesFolder, int season, int episode, Func<string, IEnumerable<string>> listDirectories, Func<string, IEnumerable<string>> listFiles)
    {
        ArgumentNullException.ThrowIfNull(listDirectories);
        ArgumentNullException.ThrowIfNull(listFiles);

        return EpisodeFiles(seriesFolder, listDirectories, listFiles)
            .FirstOrDefault(f => f.Season == season && f.First <= episode && episode <= f.Last).Path;
    }

    /// <summary>
    /// Finds the folder a show already uses for a season: the folder holding that season's episodes, or else a folder
    /// named for it (<c>Season 1</c>, <c>S01</c>, <c>Specials</c> for season 0).
    /// </summary>
    /// <param name="seriesFolder">The series folder.</param>
    /// <param name="season">Season number.</param>
    /// <param name="listDirectories">Lists the sub-folders of a folder.</param>
    /// <param name="listFiles">Lists the files in a folder.</param>
    /// <returns>The folder, or <c>null</c> if the show has none for that season yet.</returns>
    public static string? FindSeasonFolder(string seriesFolder, int season, Func<string, IEnumerable<string>> listDirectories, Func<string, IEnumerable<string>> listFiles)
    {
        ArgumentNullException.ThrowIfNull(listDirectories);
        ArgumentNullException.ThrowIfNull(listFiles);

        var dirs = listDirectories(seriesFolder).ToList();
        var holding = dirs.FirstOrDefault(d => listFiles(d).Any(f => IsVideo(f) && ReleaseNameParser.Parse(Path.GetFileName(f)).Season == season));
        if (holding is not null)
        {
            return holding;
        }

        return dirs.FirstOrDefault(d =>
        {
            var name = Path.GetFileName(d);
            if (season == 0 && name.Equals("Specials", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var m = SeasonName().Match(name);
            return m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == season;
        });
    }

    /// <summary>
    /// Works out the edition of an existing film file from its name, by comparing it with its folder: the file named
    /// exactly like its folder is the standard version; <c>&lt;folder&gt; - Label</c> is edition "Label". Titles with a
    /// colon (which sanitises to " - ") are therefore not mistaken for editions.
    /// </summary>
    /// <param name="path">The existing film file.</param>
    /// <returns><c>Known</c> is false when the file doesn't follow the folder naming, so its edition can't be told.</returns>
    public static (bool Known, string? Edition) MovieEdition(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stem = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        if (stem.Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        return stem.StartsWith(folder + " - ", StringComparison.OrdinalIgnoreCase)
            ? (true, stem[(folder.Length + 3)..])
            : (false, null);
    }

    private static IEnumerable<(string Path, int? Season, int First, int Last)> EpisodeFiles(string seriesFolder, Func<string, IEnumerable<string>> listDirectories, Func<string, IEnumerable<string>> listFiles)
    {
        foreach (var file in listFiles(seriesFolder).Concat(listDirectories(seriesFolder).SelectMany(listFiles)))
        {
            if (!IsVideo(file))
            {
                continue;
            }

            var p = ReleaseNameParser.Parse(Path.GetFileName(file));
            if (p.Episode is { } first)
            {
                yield return (file, p.Season, first, p.EndingEpisode ?? first);
            }
        }
    }

    private static bool IsVideo(string path) => ReleaseClassifier.Classify(new ReleaseFile(Path.GetFileName(path), long.MaxValue)) == FileRole.Video;

    [GeneratedRegex(@"^(?:Season|Series|S)[\s._-]*([0-9]{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonName();
}
