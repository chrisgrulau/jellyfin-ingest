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
/// Where a watch folder's media goes.
/// </summary>
/// <param name="Root">Absolute path of the library folder (e.g. the "TV Series" folder).</param>
/// <param name="IsTv">Whether the library holds shows (otherwise films).</param>
public sealed record LibraryTarget(string Root, bool IsTv);

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

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestPlanner"/> class.
    /// </summary>
    /// <param name="identifier">Identifies main videos.</param>
    /// <param name="exists">Returns whether an absolute path already exists (destinations are never overwritten).</param>
    /// <param name="readText">Reads a subtitle file's text for language detection; may return <c>null</c>.</param>
    /// <param name="clock">Clock for dating quarantine folders.</param>
    public IngestPlanner(MediaIdentifier identifier, Func<string, bool> exists, Func<string, string?> readText, TimeProvider clock)
    {
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
    /// <param name="target">Destination library.</param>
    /// <param name="quarantineRoot">Absolute quarantine folder.</param>
    /// <param name="chosen">A title a person picked for this release in review; used instead of searching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan; nothing in it has been done yet.</returns>
    public async Task<IngestPlan> PlanAsync(
        string watchFolder,
        string releaseName,
        IReadOnlyList<ReleaseFile> files,
        LibraryTarget target,
        string quarantineRoot,
        MetadataCandidate? chosen,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseName);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(target);
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
            var result = chosen is null
                ? await _identifier.IdentifyAsync(parsed[video], target.IsTv, cancellationToken).ConfigureAwait(false)
                : await _identifier.IdentifyAsChosenAsync(parsed[video], chosen, target.IsTv, cancellationToken).ConfigureAwait(false);
            if (result.Status != IdentificationStatus.Identified)
            {
                review.Add(new ReviewItem(Abs(video), result.Reason) { Candidates = result.Candidates });
                continue;
            }

            if (target.IsTv != (result.Episode is not null))
            {
                review.Add(new ReviewItem(Abs(video), target.IsTv ? "Identified as a film, but this watch folder files into a TV library." : "Identified as a TV episode, but this watch folder files into a film library."));
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
            foreach (var sub in subs)
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
}
