using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ingest.Naming;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// A subtitle file tied to a video, with what could be read about it.
/// </summary>
/// <param name="RelativePath">The subtitle's path relative to the watch folder.</param>
/// <param name="Track">Language / flags / title for naming the sidecar.</param>
public sealed record PairedSubtitle(string RelativePath, SubtitleTrack Track);

/// <summary>
/// Ties subtitle files in a release to its videos, however the release named them:
/// same stem (<c>Video.en.srt</c>), a per-video subtitle folder (<c>Subs/Video/2_English.srt</c>, matched by name or
/// <c>SxxEyy</c>), a flat <c>Subs/</c> folder, or — when the release has a single video — anything in the release.
/// </summary>
public static partial class SubtitlePairer
{
    /// <summary>
    /// Pairs subtitles with videos.
    /// </summary>
    /// <param name="videos">Relative paths of the release's main videos.</param>
    /// <param name="subtitles">Relative paths of its subtitle files.</param>
    /// <param name="sniffLanguage">Reads a subtitle file and guesses its language (ISO 639-1), used when the name doesn't say; may return <c>null</c>.</param>
    /// <param name="unpaired">Subtitles that could not be tied to exactly one video.</param>
    /// <returns>Subtitles per video.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<PairedSubtitle>> Pair(
        IReadOnlyCollection<string> videos,
        IReadOnlyCollection<string> subtitles,
        Func<string, string?> sniffLanguage,
        out IReadOnlyList<string> unpaired)
    {
        ArgumentNullException.ThrowIfNull(videos);
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(sniffLanguage);

        var result = videos.ToDictionary(v => v, _ => new List<PairedSubtitle>(), StringComparer.Ordinal);
        var left = new List<string>();

        foreach (var sub in subtitles)
        {
            var video = MatchVideo(sub, videos);
            if (video is null)
            {
                left.Add(sub);
                continue;
            }

            result[video].Add(new PairedSubtitle(sub, Describe(sub, video, sniffLanguage)));
        }

        unpaired = left;
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PairedSubtitle>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads language, SDH / forced flags and a commentary title from a subtitle's name (the part after the video's
    /// stem, or its own name when it lives in a subtitle folder).
    /// </summary>
    /// <param name="subtitle">Subtitle relative path.</param>
    /// <param name="video">The video it belongs to.</param>
    /// <param name="sniffLanguage">Content-based language fallback.</param>
    /// <returns>The track description.</returns>
    public static SubtitleTrack Describe(string subtitle, string video, Func<string, string?> sniffLanguage)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(sniffLanguage);

        var stem = Path.GetFileNameWithoutExtension(subtitle);
        var videoStem = Path.GetFileNameWithoutExtension(video);
        var tail = stem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase) ? stem[videoStem.Length..] : stem;
        tail = LeadingIndex().Replace(tail, string.Empty); // "2_English" -> "English"

        var tokens = TokenSplit().Split(tail).Where(t => t.Length > 0).ToList();
        var language = tokens.Select(SubtitleNamer.NormaliseLanguage).LastOrDefault(l => l is not null) ?? sniffLanguage(subtitle);
        return new SubtitleTrack
        {
            Language = language,
            HearingImpaired = tokens.Any(t => HearingImpairedToken().IsMatch(t)),
            Forced = tokens.Any(t => ForcedToken().IsMatch(t)),
            Title = CommentaryToken().IsMatch(tail) ? "Commentary" : null,
        };
    }

    private static string? MatchVideo(string sub, IReadOnlyCollection<string> videos)
    {
        var subStem = Path.GetFileNameWithoutExtension(sub);
        var subDir = Path.GetDirectoryName(sub) ?? string.Empty;

        // 1. same folder, name starts with the video's stem
        var byStem = videos.Where(v => string.Equals(Path.GetDirectoryName(v) ?? string.Empty, subDir, StringComparison.Ordinal)
            && subStem.StartsWith(Path.GetFileNameWithoutExtension(v), StringComparison.OrdinalIgnoreCase)).ToList();
        if (byStem.Count >= 1)
        {
            return byStem.OrderByDescending(v => Path.GetFileNameWithoutExtension(v).Length).First();
        }

        // 2. per-video subtitle folder: Subs/<video stem or SxxEyy>/file
        var folder = Path.GetFileName(subDir);
        var byFolder = videos.Where(v => string.Equals(Path.GetFileNameWithoutExtension(v), folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byFolder.Count == 0 && EpisodeCode().Match(folder) is { Success: true } code)
        {
            byFolder = [.. videos.Where(v => Path.GetFileName(v).Contains(code.Value, StringComparison.OrdinalIgnoreCase))];
        }

        if (byFolder.Count == 1)
        {
            return byFolder[0];
        }

        // 3. by episode code in the subtitle's own name
        if (EpisodeCode().Match(subStem) is { Success: true } own)
        {
            var byCode = videos.Where(v => Path.GetFileName(v).Contains(own.Value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byCode.Count == 1)
            {
                return byCode[0];
            }
        }

        // 4. single-video release: everything belongs to it
        return videos.Count == 1 ? videos.First() : null;
    }

    [GeneratedRegex(@"^[\s._-]*\d{1,2}_")]
    private static partial Regex LeadingIndex();

    [GeneratedRegex(@"[\s._\-\[\]()]+")]
    private static partial Regex TokenSplit();

    [GeneratedRegex(@"^(?:sdh|hi|cc|hoh)$", RegexOptions.IgnoreCase)]
    private static partial Regex HearingImpairedToken();

    [GeneratedRegex(@"^(?:forced|foreign)$", RegexOptions.IgnoreCase)]
    private static partial Regex ForcedToken();

    [GeneratedRegex(@"comment", RegexOptions.IgnoreCase)]
    private static partial Regex CommentaryToken();

    [GeneratedRegex(@"s\d{1,2}e\d{1,3}", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeCode();
}
