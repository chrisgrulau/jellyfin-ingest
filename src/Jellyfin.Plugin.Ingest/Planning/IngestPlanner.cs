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
/// Where a copy being replaced lives.
/// </summary>
/// <param name="Library">Its library.</param>
/// <param name="Root">The library folder it is in.</param>
/// <param name="TitleFolder">The film or show folder directly inside <paramref name="Root"/>, if it is in one.</param>
internal sealed record ReplacedHome(MediaLibrary Library, string Root, string? TitleFolder);

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
    private readonly IExistingMedia? _existing;
    private readonly Func<string, bool> _isInsideLibrary;
    private readonly Func<string, IEnumerable<string>> _filesIn;

    /// <summary>The subtitle extensions moved with a replaced video (its sidecars).</summary>
    public static readonly IReadOnlySet<string> SidecarExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi" };

    /// <summary>
    /// Gets a value indicating whether copies already on the server replace nothing (the default: such files wait for
    /// review) or are replaced: when set (asked for in review), each existing copy inside a library, and its subtitle
    /// files, move to quarantine first and the new file is filed in its place.
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Gets how files reach the library. With copy or hard link the release stays where it is (for seeding), so its
    /// clutter isn't quarantined either.
    /// </summary>
    public Configuration.TransferMode Transfer { get; init; }

    /// <summary>
    /// Gets decisions made file by file in review (by file, relative to the watch folder): replace that file's copies on
    /// the server, or quarantine that file (with its subtitles) and file the rest.
    /// </summary>
    public IReadOnlyDictionary<string, Service.FileDecision> FileDecisions { get; init; } = new Dictionary<string, Service.FileDecision>(StringComparer.Ordinal);

    /// <summary>
    /// Gets season and episode numbers given in review (by video, relative to the watch folder), used instead of what
    /// the video's name says: the video is an episode with that number (ING-30).
    /// </summary>
    public IReadOnlyDictionary<string, Service.EpisodeNumber> EpisodeNumbers { get; init; } = new Dictionary<string, Service.EpisodeNumber>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the server's libraries. A video that replaces a copy already on the server is filed where that copy lives
    /// (its library folder, reusing its film or show folder) when that folder belongs to one of these libraries of the
    /// right kind, unless a library was chosen in review; otherwise it is routed as usual and the plan says why.
    /// </summary>
    public IReadOnlyList<MediaLibrary> Libraries { get; init; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestPlanner"/> class.
    /// </summary>
    /// <param name="identifier">Identifies main videos.</param>
    /// <param name="exists">Returns whether an absolute path already exists (destinations are never overwritten).</param>
    /// <param name="readText">Reads a subtitle file's text for language detection; may return <c>null</c>.</param>
    /// <param name="clock">Clock for dating quarantine folders.</param>
    /// <param name="existing">What's already on the server, so new episodes join their show and nothing is filed twice (optional).</param>
    /// <param name="isInsideLibrary">Whether a folder is inside one of the server's library folders; an existing show's
    /// <param name="filesIn">Lists the files in a folder (for the subtitle files of a copy being replaced).</param>
    /// folder must be, before anything is filed into it (optional; without it, existing shows are not joined).</param>
    public IngestPlanner(MediaIdentifier identifier, Func<string, bool> exists, Func<string, string?> readText, TimeProvider clock, IExistingMedia? existing = null, Func<string, bool>? isInsideLibrary = null, Func<string, IEnumerable<string>>? filesIn = null)
    {
        _filesIn = filesIn ?? (_ => []);
        _isInsideLibrary = isInsideLibrary ?? (_ => false);
        _existing = existing;
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
        var replacing = new List<string>();
        var replacingSet = new HashSet<string>(StringComparer.Ordinal);

        // A path being replaced is free for the new files (its old file moves to quarantine first)
        bool Taken(string path) => (_exists(path) && !replacingSet.Contains(path)) || planned.Contains(path);

        // With "replace existing", a copy inside a library (and its subtitle files) is replaced; otherwise it holds the file back
        bool Replace(IReadOnlyList<string> existing, string video)
        {
            var asked = ReplaceExisting || (FileDecisions.TryGetValue(video, out var d) && d == Service.FileDecision.Replace);
            if (!asked || existing.Count == 0 || !existing.All(_isInsideLibrary))
            {
                return false;
            }

            foreach (var old in existing)
            {
                var stem = Path.GetFileNameWithoutExtension(old) + ".";
                var sidecars = Path.GetDirectoryName(old) is { } dir
                    ? _filesIn(dir).Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.Ordinal) && SidecarExtensions.Contains(Path.GetExtension(f)) && !string.Equals(f, old, StringComparison.Ordinal))
                    : [];
                foreach (var file in sidecars.Prepend(old).Where(replacingSet.Add))
                {
                    replacing.Add(file);
                }
            }

            return true;
        }

        var roles = files.ToDictionary(f => f.RelativePath, ReleaseClassifier.Classify, StringComparer.Ordinal);
        var videos = roles.Where(r => r.Value == FileRole.Video).Select(r => r.Key).ToList();
        var parsed = videos.ToDictionary(v => v, ReleaseNameParser.Parse, StringComparer.Ordinal);

        // Numbers given in review make the video that episode, whatever its name says
        foreach (var (video, number) in EpisodeNumbers.Where(e => parsed.ContainsKey(e.Key) && e.Value.IsValid))
        {
            parsed[video] = parsed[video] with { Kind = MediaKind.Episode, Season = number.Season, Episode = number.Episode, EndingEpisode = null, Extra = ExtraType.None };
        }
        var mains = videos.Where(v => parsed[v].Extra == ExtraType.None).ToList();
        var extras = videos.Where(v => parsed[v].Extra is not (ExtraType.None or ExtraType.Sample)).ToList();

        if (mains.Count == 0)
        {
            return new IngestPlan { ReleaseName = releaseName, Review = [new ReviewItem(Abs(releaseName), "No main video found in the release.")] };
        }

        // 1. main videos
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var requiredFolders = new HashSet<string>(StringComparer.Ordinal);
        var plannedEpisodes = new HashSet<(string Series, int Season, int Episode)>();
        var notes = new List<string>();
        var replacementNotes = new List<string>();
        var replacedIn = new List<string>();
        var tidy = new List<EmptiedFolder>();

        // Where a replacement went, in plain language; and the replaced copy's folders, if the new copy went elsewhere
        // (they are removed after filing if nothing is left in them)
        void Replaced(string video, string old, bool isTv, ReplacedHome? home, string owner, string? libraryRoot)
        {
            var library = home?.Library.Name ?? LibraryNameOf(owner, libraryRoot);
            replacedIn.Add(library);
            var line = $"{Path.GetFileName(video)}: replacing {Path.GetFileName(old)} in {library}";
            if (home is null && !PathGuard.IsSameOrUnder(old, owner))
            {
                line += chosen is not null
                    ? $" (the library chosen in review; the old copy was in {LibraryNameOf(old, null)})"
                    : $" (the old copy's folder, {Path.GetDirectoryName(old)}, isn't in one of the server's {(isTv ? "Shows" : "Movies")} libraries, so the new copy is filed as usual)";
                if (ContainingLocation(old) is { } oldRoot)
                {
                    for (var dir = Path.GetDirectoryName(old); dir is not null && PathGuard.IsUnder(dir, oldRoot) && _isInsideLibrary(dir); dir = Path.GetDirectoryName(dir))
                    {
                        if (!tidy.Any(t => PathGuard.SamePath(t.Folder, dir)))
                        {
                            tidy.Add(new EmptiedFolder(dir, oldRoot));
                        }
                    }
                }
            }

            replacementNotes.Add(line + ".");
        }

        // Files a person chose not to file are quarantined with the leftovers (their subtitles too, as unpaired)
        var skipped = mains.Where(v => FileDecisions.TryGetValue(v, out var d) && d == Service.FileDecision.Quarantine).ToList();
        foreach (var video in mains.Except(skipped))
        {
            // Only a TV library to file into: a name without an episode code is most likely a show; otherwise a film
            var preferTv = targets.Tv is not null && targets.Films is null;
            var result = chosen is null
                ? await _identifier.IdentifyAsync(parsed[video], preferTv, cancellationToken, video).ConfigureAwait(false)
                : await _identifier.IdentifyAsChosenAsync(parsed[video], chosen.Candidate, chosen.Target.IsTv, cancellationToken, video).ConfigureAwait(false);
            if (result.DecidedBy is not null)
            {
                notes.Add(Path.GetFileName(video) + ": " + result.Reason);
            }

            if (result.Status != IdentificationStatus.Identified)
            {
                review.Add(new ReviewItem(Abs(video), result.Reason) { Candidates = result.Candidates, Retry = result.NothingFound ? RetryKind.NothingFound : RetryKind.None });
                continue;
            }

            var ext = Path.GetExtension(video);
            string destination, owner;
            string? libraryRoot;
            if (result.Episode is { } ep)
            {
                // A new episode of a show that's already on the server joins it, whichever library it's in (unless a
                // person chose the library in review).
                var existingSeries = chosen is null ? _existing?.FindSeriesFolder(ProviderIds(ep.Series)) : null;
                var target = existingSeries is null ? chosen?.Target ?? targets.Tv : null;
                if (existingSeries is null && target is null)
                {
                    review.Add(new ReviewItem(Abs(video), $"Identified as an episode of '{ep.Series.Title}', but this watch folder has no TV library. Choose a library below.") { Candidates = result.Candidates });
                    continue;
                }

                var seriesRoot = existingSeries is null ? RootHolding(target!.Root, MediaNamer.SeriesFolderName(ep.Series)) : null;
                owner = existingSeries ?? Path.Combine(seriesRoot!, MediaNamer.SeriesFolderName(ep.Series));
                libraryRoot = seriesRoot;

                // An existing show keeps its own season folder naming rather than getting a second "Season NN"
                var seasonFolder = (existingSeries is null ? null : _existing?.FindSeasonFolder(existingSeries, ep.Season))
                    ?? Path.Combine(owner, MediaNamer.SeasonFolderName(ep.Season));
                destination = Path.Combine(seasonFolder, MediaNamer.EpisodeFileName(ep, ext));

                // The same episode already on the server (any library, any name or container) or twice in this release
                var keys = Enumerable.Range(ep.Episode, (ep.EndingEpisode ?? ep.Episode) - ep.Episode + 1).Select(n => (owner, ep.Season, n)).ToList();
                var duplicates = keys.Select(k => _existing?.FindEpisode(ProviderIds(ep.Series), owner, ep.Season, k.n)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                var code = MediaNamer.EpisodeCode(ep.Season, ep.Episode, ep.EndingEpisode);
                if (keys.Any(plannedEpisodes.Contains))
                {
                    review.Add(new ReviewItem(Abs(video), $"{ep.Series.Title} {code} appears more than once in this release.") { Candidates = result.Candidates });
                    continue;
                }

                if (duplicates.Count > 0 && !Replace(duplicates, video))
                {
                    review.Add(new ReviewItem(Abs(video), $"{ep.Series.Title} {code} is already on the server: {string.Join(", ", duplicates)}") { Candidates = result.Candidates, Existing = string.Join('\n', duplicates) });
                    continue;
                }

                if (duplicates.Count > 0)
                {
                    // Filed where the copy it replaces lives (a library chosen in review wins)
                    var old = duplicates[0];
                    var home = chosen is null ? HomeOf(old, isTv: true) : null;
                    if (home is not null)
                    {
                        owner = home.TitleFolder ?? Path.Combine(home.Root, MediaNamer.SeriesFolderName(ep.Series));
                        libraryRoot = home.Root;
                        var oldFolder = Path.GetDirectoryName(old)!;
                        var season = home.TitleFolder is not null && PathGuard.IsSameOrUnder(oldFolder, owner) ? oldFolder : Path.Combine(owner, MediaNamer.SeasonFolderName(ep.Season));
                        destination = Path.Combine(season, MediaNamer.EpisodeFileName(ep, ext));
                    }

                    Replaced(video, old, isTv: true, home, owner, libraryRoot);
                }

                keys.ForEach(k => plannedEpisodes.Add(k));
            }
            else
            {
                var movie = result.Movie!;
                var target = chosen?.Target ?? targets.Films;
                if (target is null)
                {
                    review.Add(new ReviewItem(Abs(video), $"Identified as the film '{movie.Title}', but this watch folder has no film library. Choose a library below.") { Candidates = result.Candidates });
                    continue;
                }

                var movieRoot = RootHolding(target.Root, MediaNamer.MovieFolderName(movie));
                owner = Path.Combine(movieRoot, MediaNamer.MovieFolderName(movie));
                libraryRoot = movieRoot;
                destination = Path.Combine(movieRoot, MediaNamer.MovieRelativePath(movie, ext));
                var duplicate = _existing?.FindMovie(MovieIds(movie), movie.Edition, destination);
                if (duplicate is not null && !Replace([duplicate], video))
                {
                    review.Add(new ReviewItem(Abs(video), $"{movie.Title}{(movie.Edition is null ? string.Empty : " (" + movie.Edition + ")")} is already on the server: {duplicate}") { Candidates = result.Candidates, Existing = duplicate });
                    continue;
                }

                if (duplicate is not null)
                {
                    // Filed where the copy it replaces lives, reusing its film folder (a library chosen in review wins)
                    var home = chosen is null ? HomeOf(duplicate, isTv: false) : null;
                    if (home is not null)
                    {
                        owner = home.TitleFolder ?? Path.Combine(home.Root, MediaNamer.MovieFolderName(movie));
                        libraryRoot = home.Root;
                        destination = Path.Combine(owner, MediaNamer.MovieFileName(movie, ext));
                    }

                    Replaced(video, duplicate, isTv: false, home, owner, libraryRoot);
                }
            }

            // The library folder (or the existing show's folder) must be there: if a share isn't mounted, creating the
            // folders would put the media on the local disk under the mount point, hidden once the share is back
            var required = libraryRoot ?? owner;
            if (!_exists(required))
            {
                review.Add(new ReviewItem(Abs(video), $"The library folder isn't available (is the share mounted?): {required}. Trying again automatically.") { Candidates = result.Candidates, Retry = RetryKind.FolderUnavailable });
                continue;
            }

            requiredFolders.Add(required);

            // Containment: the film or show folder must be inside its library (an existing show's folder inside one of
            // the server's libraries), and the file inside that folder, whatever the names and ids contain
            var ownerInside = libraryRoot is null ? _isInsideLibrary(owner) : PathGuard.IsUnder(owner, libraryRoot);
            if (!ownerInside || !PathGuard.IsUnder(destination, owner))
            {
                review.Add(new ReviewItem(Abs(video), $"Refused: the destination would be outside the library: {destination}") { Candidates = result.Candidates });
                continue;
            }

            if (Taken(destination))
            {
                review.Add(new ReviewItem(Abs(video), $"Destination already exists: {destination}") { Candidates = result.Candidates });
                continue;
            }

            planned.Add(destination);
            destinations[video] = destination;
            owners.Add(owner);
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
                var name = FileNameSanitizer.Truncate(FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(extra)), FileNameSanitizer.MaxStemBytes);
                name = FileNameSanitizer.AvoidReserved(name.Length > 0 ? name : parsed[extra].Extra.ToString());
                var folder = Path.Combine(owners.First(), MediaNamer.ExtrasFolderName(parsed[extra].Extra));
                var destination = Path.Combine(folder, name + Path.GetExtension(extra));
                for (var n = 2; Taken(destination); n++)
                {
                    destination = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name} ({n}){Path.GetExtension(extra)}"));
                }

                if (!PathGuard.IsUnder(destination, owners.First()))
                {
                    review.Add(new ReviewItem(Abs(extra), $"Refused: the destination would be outside the library: {destination}"));
                    continue;
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
        // Paired against every video, so a set-aside video's subtitles go to quarantine with it (not to another episode)
        var paired = SubtitlePairer.Pair(mains, subtitles, rel => _readText(Abs(rel)) is { } text ? SubtitleLanguageSniffer.Guess(text) : null, out var unpaired);
        var skippedSubtitles = paired.Where(p => skipped.Contains(p.Key)).SelectMany(p => p.Value.Select(t => t.RelativePath)).ToList();
        paired = paired.Where(p => !skipped.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (video, subs) in paired)
        {
            var dir = Path.GetDirectoryName(destinations[video])!;
            var stem = Path.GetFileNameWithoutExtension(destinations[video]);
            foreach (var sub in WithMainTrackDefault(subs))
            {
                var name = SubtitleNamer.SidecarName(stem, sub.Track, Path.GetExtension(sub.RelativePath), n => Taken(Path.Combine(dir, n)));
                var destination = Path.Combine(dir, name);
                if (!PathGuard.IsUnder(destination, dir))
                {
                    return new IngestPlan { ReleaseName = releaseName, Review = [new ReviewItem(Abs(sub.RelativePath), $"Refused: the subtitle destination would be outside the library: {destination}")] };
                }

                planned.Add(destination);
                ops.Add(new PlannedOperation(OperationKind.Subtitle, Abs(sub.RelativePath), destination));
            }
        }

        // 4. everything else is quarantined, dated so the retention purge is a simple folder check
        var quarantine = Quarantine.QuarantineMarkers.DatedFolderFor(quarantineRoot, DateOnly.FromDateTime(_clock.GetLocalNow().DateTime));
        var leftovers = Transfer != Configuration.TransferMode.Move
            ? []
            : roles.Where(r => r.Value is FileRole.Clutter or FileRole.Sample).Select(r => r.Key).Concat(unpaired).Concat(skipped).Concat(skippedSubtitles);
        foreach (var rel in leftovers)
        {
            var destination = Path.Combine(quarantine, rel);
            for (var n = 2; Taken(destination); n++)
            {
                destination = Path.Combine(quarantine, string.Create(CultureInfo.InvariantCulture, $"{rel} ({n})"));
            }

            if (!PathGuard.IsUnder(destination, quarantine))
            {
                return new IngestPlan { ReleaseName = releaseName, Review = [new ReviewItem(Abs(rel), $"Refused: the quarantine destination would be outside the quarantine folder: {destination}")] };
            }

            planned.Add(destination);
            ops.Add(new PlannedOperation(OperationKind.Quarantine, Abs(rel), destination));
        }

        // 5. copies being replaced go to quarantine first, so their names are free when the new files arrive
        var replacements = new List<PlannedOperation>();
        foreach (var old in replacing)
        {
            var name = Path.GetFileName(old);
            var destination = Path.Combine(quarantine, "Replaced", name);
            for (var n = 2; Taken(destination); n++)
            {
                destination = Path.Combine(quarantine, "Replaced", string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}"));
            }

            planned.Add(destination);
            replacements.Add(new PlannedOperation(OperationKind.Quarantine, old, destination));
        }

        ops.InsertRange(0, replacements);
        return new IngestPlan
        {
            ReleaseName = releaseName,
            Operations = ops,
            AllowedRoots = [.. owners, quarantine],
            RequiredFolders = [.. requiredFolders],
            Notes = notes,
            Replacing = replacing,
            ReplacementNotes = replacementNotes,
            ReplacedIn = replacedIn,
            TidyIfEmpty = tidy,
            Skipped = [.. skipped.Select(Abs)],
            Transfer = Transfer,

            // Every video set aside by choice: the release is quarantined as a whole
            WholeReleaseQuarantine = skipped.Count == mains.Count,
        };
    }

    // The library folder of the server's libraries (any kind) a path is inside, if any (the innermost, should they nest)
    private string? ContainingLocation(string path)
        => Libraries.SelectMany(l => l.Locations)
            .Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r) && PathGuard.IsUnder(path, r))
            .OrderByDescending(r => PathGuard.Normalise(r).Length)
            .FirstOrDefault();

    // The name of the library a path is in; otherwise the given folder, or the path's own folder
    private string LibraryNameOf(string path, string? otherwise)
        => ContainingLocation(path) is { } root
            ? Libraries.First(l => l.Locations.Any(r => PathGuard.SamePath(r, root))).Name
            : otherwise ?? Path.GetDirectoryName(path) ?? path;

    // A library with several folders: a film or show folder of this name already in one of its other folders is used
    // there (the target folder is chosen for new titles, e.g. by free space), rather than starting a second one
    private string RootHolding(string root, string titleFolder)
    {
        if (_exists(Path.Combine(root, titleFolder)))
        {
            return root;
        }

        var siblings = Libraries.FirstOrDefault(l => l.Locations.Any(r => PathGuard.SamePath(r, root)))?.Locations ?? [];
        return siblings.FirstOrDefault(r => !PathGuard.SamePath(r, root) && !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r) && _exists(Path.Combine(r, titleFolder))) ?? root;
    }

    // Where a copy being replaced lives: a folder of one of the server's libraries that takes this kind of media, and
    // the film or show folder directly inside it (none when the copy sits in the library folder itself)
    private ReplacedHome? HomeOf(string old, bool isTv)
    {
        var match = Libraries
            .Where(l => isTv ? l.Kind is LibraryKind.Shows or LibraryKind.Mixed : l.Kind is LibraryKind.Films or LibraryKind.Mixed)
            .SelectMany(l => l.Locations.Select(r => (Library: l, Root: r)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Root) && Path.IsPathFullyQualified(x.Root) && PathGuard.IsUnder(old, x.Root) && _isInsideLibrary(old))
            .OrderByDescending(x => PathGuard.Normalise(x.Root).Length)
            .FirstOrDefault();
        if (match.Library is null || !PathGuard.SamePath(ContainingLocation(old), match.Root))
        {
            return null;
        }

        var root = PathGuard.Normalise(match.Root);
        var parts = Path.GetRelativePath(root, PathGuard.Normalise(old)).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return new ReplacedHome(match.Library, root, parts.Length > 1 ? Path.Combine(root, parts[0]) : null);
    }

    private static Dictionary<string, string> MovieIds(MovieIdentity movie)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(movie.TmdbId))
        {
            ids["Tmdb"] = movie.TmdbId;
        }

        if (!string.IsNullOrEmpty(movie.ImdbId))
        {
            ids["Imdb"] = movie.ImdbId;
        }

        return ids;
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
