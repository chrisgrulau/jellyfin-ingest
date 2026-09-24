using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Naming;
using Jellyfin.Plugin.Ingest.Parsing;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// A library folder media can be filed into.
/// </summary>
/// <param name="Root">Absolute path of the library folder (e.g. a Shows library's folder).</param>
/// <param name="IsTv">Whether the library holds shows (otherwise films).</param>
public sealed record LibraryTarget(string Root, bool IsTv);

/// <summary>
/// Where a watch folder's media goes: episodes to <see cref="Tv"/>, films to <see cref="Films"/>. Either may be
/// missing, in which case media of that kind is left for review.
/// </summary>
/// <param name="Tv">The TV library folder, if any.</param>
/// <param name="Films">The film library folder, if any.</param>
public sealed record LibraryTargets(LibraryTarget? Tv, LibraryTarget? Films)
{
    /// <summary>
    /// Wraps a single library.
    /// </summary>
    /// <param name="target">The library.</param>
    /// <returns>Targets with just that library.</returns>
    public static LibraryTargets Of(LibraryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.IsTv ? new(target, null) : new(null, target);
    }
}

/// <summary>
/// A decision made in review: file the release as this title, into this library.
/// </summary>
/// <param name="Candidate">The chosen title.</param>
/// <param name="Target">The chosen library folder (its kind must match the title's).</param>
public sealed record ChosenMatch(MetadataCandidate Candidate, LibraryTarget Target);

/// <summary>
/// Turns a dropped release into an <see cref="IngestPlan"/>. It reads nothing but names (plus, via callbacks, whether a
/// destination exists and a subtitle's text for language detection), so it can be exercised without touching disk.
/// </summary>
public sealed class IngestPlanner
{
    private readonly MediaIdentifier _identifier;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, string?> _readText;
    private readonly TimeProvider _clock;
    private readonly ISeriesLocator? _series;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestPlanner"/> class.
    /// </summary>
    /// <param name="identifier">Identifies main videos.</param>
    /// <param name="exists">Returns whether an absolute path already exists (destinations are never overwritten).</param>
    /// <param name="readText">Reads a subtitle file's text for language detection; may return <c>null</c>.</param>
    /// <param name="clock">Clock for dating quarantine folders.</param>
    /// <param name="series">Finds shows already on the server, so new episodes join them (optional).</param>
    public IngestPlanner(MediaIdentifier identifier, Func<string, bool> exists, Func<string, string?> readText, TimeProvider clock, ISeriesLocator? series = null)
    {
        _series = series;
        _identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
        _exists = exists ?? throw new ArgumentNullException(nameof(exists));
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Plans one release.
    /// </summary>
    /// <param name="watchFolder">Absolute path of the watch folder.</param>
    /// <param name="releaseName">The release's top-level file or folder name inside the watch folder.</param>
    /// <param name="files">Every file of the release, relative to the watch folder.</param>
    /// <param name="targets">Destination libraries.</param>
    /// <param name="quarantineRoot">Absolute quarantine folder.</param>
    /// <param name="chosen">A title and library a person picked for this release in review; used instead of searching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan; nothing in it has been done yet.</returns>
    public async Task<IngestPlan> PlanAsync(
        string watchFolder,
        string releaseName,
        IReadOnlyList<ReleaseFile> files,
        LibraryTargets targets,
        string quarantineRoot,
        ChosenMatch? chosen,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseName);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);

        string Abs(string rel) => Path.Combine(watchFolder, rel);
        var review = new List<ReviewItem>();
        var ops = new List<PlannedOperation>();
        var planned = new HashSet<string>(StringComparer.Ordinal);
        bool Taken(string path) => _exists(path) || planned.Contains(path);

        var roles = files.ToDictionary(f => f.RelativePath, ReleaseClassifier.Classify, StringComparer.Ordinal);
        var videos = roles.Where(r => r.Value == FileRole.Video).Select(r => r.Key).ToList();
        var parsed = videos.ToDictionary(v => v, ReleaseNameParser.Parse, StringComparer.Ordinal);
        var mains = videos.Where(v => parsed[v].Extra == ExtraType.None).ToList();
        var extras = videos.Where(v => parsed[v].Extra is not (ExtraType.None or ExtraType.Sample)).ToList();

        if (mains.Count == 0)
        {
            return new IngestPlan { ReleaseName = releaseName, Review = [new ReviewItem(Abs(releaseName), "No main video found in the release.")] };
        }

        // 1. main videos
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var video in mains)
        {
            // Only a TV library to file into: a name without an episode code is most likely a show; otherwise a film
            var preferTv = targets.Tv is not null && targets.Films is null;
            var result = chosen is null
                ? await _identifier.IdentifyAsync(parsed[video], preferTv, cancellationToken).ConfigureAwait(false)
                : await _identifier.IdentifyAsChosenAsync(parsed[video], chosen.Candidate, chosen.Target.IsTv, cancellationToken).ConfigureAwait(false);
            if (result.Status != IdentificationStatus.Identified)
            {
                review.Add(new ReviewItem(Abs(video), result.Reason) { Candidates = result.Candidates });
                continue;
            }

            var isEpisode = result.Episode is not null;

            // A new episode of a show that's already on the server joins it, whichever library it's in (unless a
            // person chose the library in review).
            var existingSeries = chosen is null && result.Episode is { } found && _series is not null
                ? _series.FindSeriesFolder(ProviderIds(found.Series))
                : null;
            if (existingSeries is not null)
            {
                var joinedEpisode = result.Episode!;
                var joined = Path.Combine(existingSeries, MediaNamer.SeasonFolderName(joinedEpisode.Season), MediaNamer.EpisodeFileName(joinedEpisode, Path.GetExtension(video)));
                if (Taken(joined))
                {
                    review.Add(new ReviewItem(Abs(video), $"Destination already exists: {joined}") { Candidates = result.Candidates });
                    continue;
                }

                planned.Add(joined);
                destinations[video] = joined;
                owners.Add(existingSeries);
                ops.Add(new PlannedOperation(OperationKind.Video, Abs(video), joined));
                continue;
            }

            var target = chosen?.Target ?? (isEpisode ? targets.Tv : targets.Films);
            if (target is null)
            {
                var reason = isEpisode
                    ? $"Identified as an episode of '{result.Episode!.Series.Title}', but this watch folder has no TV library. Choose a library below."
                    : $"Identified as the film '{result.Movie!.Title}', but this watch folder has no film library. Choose a library below.";
                review.Add(new ReviewItem(Abs(video), reason) { Candidates = result.Candidates });
                continue;
            }

            var ext = Path.GetExtension(video);
            var relative = result.Episode is { } ep ? MediaNamer.EpisodeRelativePath(ep, ext) : MediaNamer.MovieRelativePath(result.Movie!, ext);
            var destination = Path.Combine(target.Root, relative);
            if (Taken(destination))
            {
                review.Add(new ReviewItem(Abs(video), $"Destination already exists: {destination}"));
                continue;
            }

            planned.Add(destination);
            destinations[video] = destination;
            owners.Add(result.Episode is { } e
                ? Path.Combine(target.Root, MediaNamer.SeriesFolderName(e.Series))
                : Path.Combine(target.Root, MediaNamer.MovieFolderName(result.Movie!)));
            ops.Add(new PlannedOperation(OperationKind.Video, Abs(video), destination));
        }

        // 2. extras belong to the release's single owner (film or series)
        if (extras.Count > 0 && owners.Count != 1)
        {
            review.AddRange(extras.Select(x => new ReviewItem(Abs(x), "Extra can't be tied to a single film or series in this release.")));
        }
        else
        {
            foreach (var extra in extras)
            {
                var name = FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(extra));
                var folder = Path.Combine(owners.First(), MediaNamer.ExtrasFolderName(parsed[extra].Extra));
                var destination = Path.Combine(folder, name + Path.GetExtension(extra));
                for (var n = 2; Taken(destination); n++)
                {
                    destination = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name} ({n}){Path.GetExtension(extra)}"));
                }

                planned.Add(destination);
                ops.Add(new PlannedOperation(OperationKind.Extra, Abs(extra), destination));
            }
        }

        if (review.Count > 0)
        {
            return new IngestPlan { ReleaseName = releaseName, Review = review };
        }

        // 3. subtitles follow their video
        var subtitles = roles.Where(r => r.Value == FileRole.Subtitle).Select(r => r.Key).ToList();
        var paired = SubtitlePairer.Pair(mains, subtitles, rel => _readText(Abs(rel)) is { } text ? SubtitleLanguageSniffer.Guess(text) : null, out var unpaired);
        foreach (var (video, subs) in paired)
        {
            var dir = Path.GetDirectoryName(destinations[video])!;
            var stem = Path.GetFileNameWithoutExtension(destinations[video]);
            foreach (var sub in WithMainTrackDefault(subs))
            {
                var name = SubtitleNamer.SidecarName(stem, sub.Track, Path.GetExtension(sub.RelativePath), n => Taken(Path.Combine(dir, n)));
                var destination = Path.Combine(dir, name);
                planned.Add(destination);
                ops.Add(new PlannedOperation(OperationKind.Subtitle, Abs(sub.RelativePath), destination));
            }
        }

        // 4. everything else is quarantined, dated so the retention purge is a simple folder check
        var quarantine = Path.Combine(quarantineRoot, _clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var leftovers = roles.Where(r => r.Value is FileRole.Clutter or FileRole.Sample).Select(r => r.Key).Concat(unpaired);
        foreach (var rel in leftovers)
        {
            var destination = Path.Combine(quarantine, rel);
            for (var n = 2; Taken(destination); n++)
            {
                destination = Path.Combine(quarantine, string.Create(CultureInfo.InvariantCulture, $"{rel} ({n})"));
            }

            planned.Add(destination);
            ops.Add(new PlannedOperation(OperationKind.Quarantine, Abs(rel), destination));
        }

        return new IngestPlan { ReleaseName = releaseName, Operations = ops };
    }

    private static Dictionary<string, string> ProviderIds(SeriesIdentity series)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(series.TvdbId))
        {
            ids["Tvdb"] = series.TvdbId;
        }

        if (!string.IsNullOrEmpty(series.TmdbId))
        {
            ids["Tmdb"] = series.TmdbId;
        }

        return ids;
    }

    /// <summary>
    /// When a video has several subtitles in one language (main, SDH, commentary …) and none is marked default,
    /// marks the main one default. Otherwise Jellyfin picks whichever sorts first by file name, which is usually a
    /// titled extra such as "Alternate 2" or "Commentary".
    /// </summary>
    /// <param name="subs">A video's subtitles.</param>
    /// <returns>The same subtitles, with at most one default per language.</returns>
    public static IReadOnlyList<PairedSubtitle> WithMainTrackDefault(IReadOnlyList<PairedSubtitle> subs)
    {
        ArgumentNullException.ThrowIfNull(subs);

        var result = subs.ToList();
        foreach (var group in result.Where(s => !s.Track.Forced).GroupBy(s => s.Track.Language ?? string.Empty, StringComparer.Ordinal).ToList())
        {
            if (group.Count() < 2 || group.Any(s => s.Track.Default))
            {
                continue;
            }

            var main = group
                .OrderBy(s => !string.IsNullOrEmpty(s.Track.Title))
                .ThenBy(s => s.Track.HearingImpaired)
                .First();
            result[result.IndexOf(main)] = main with { Track = main.Track with { Default = true } };
        }

        return result;
    }
}
