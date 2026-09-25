using System;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ingest.Naming;
using Jellyfin.Plugin.Ingest.Parsing;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// What a file in a dropped release is for.
/// </summary>
public enum FileRole
{
    /// <summary>Anything else: READMEs, .nfo, .txt, screenshots, torrents … (quarantined).</summary>
    Clutter = 0,

    /// <summary>A video file.</summary>
    Video,

    /// <summary>A subtitle file.</summary>
    Subtitle,

    /// <summary>A sample clip (quarantined, never filed).</summary>
    Sample,
}

/// <summary>
/// A file inside a dropped release.
/// </summary>
/// <param name="RelativePath">Path relative to the watch folder, e.g. <c>Show.S01E04/Show.S01E04.mkv</c>.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LastWriteUtc">When the file was last written, if known. Part of the settle check, because download clients
/// that pre-allocate files show their final size long before they finish writing them.</param>
public sealed record ReleaseFile(string RelativePath, long Size, DateTime? LastWriteUtc = null);

/// <summary>
/// Sorts release files into roles by extension, name and size.
/// </summary>
public static partial class ReleaseClassifier
{
    /// <summary>Videos smaller than this that are named "sample" are treated as samples.</summary>
    public const long SampleMaxBytes = 300L * 1024 * 1024;

    /// <summary>
    /// Classifies one file.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <returns>Its role.</returns>
    public static FileRole Classify(ReleaseFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var name = Path.GetFileName(file.RelativePath);
        if (VideoExtension().IsMatch(name))
        {
            var isSample = file.Size < SampleMaxBytes && ReleaseNameParser.Parse(file.RelativePath).Extra == ExtraType.Sample;
            return isSample ? FileRole.Sample : FileRole.Video;
        }

        return SubtitleExtension().IsMatch(name) ? FileRole.Subtitle : FileRole.Clutter;
    }

    [GeneratedRegex(@"\.(?:mkv|mp4|m4v|avi|mov|wmv|ts|m2ts|mpg|mpeg|webm)$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoExtension();

    [GeneratedRegex(@"\.(?:srt|ass|ssa|vtt|sub|idx|sup)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubtitleExtension();
}
