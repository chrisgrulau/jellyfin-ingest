using System;
using System.Globalization;
using System.IO;
using System.Text;
using Jellyfin.Plugin.Ingest.Identification;

namespace Jellyfin.Plugin.Ingest.Naming;

/// <summary>
/// Produces Jellyfin-standard folder and file names.
/// See https://jellyfin.org/docs/general/server/media/movies/ and https://jellyfin.org/docs/general/server/media/shows/.
/// </summary>
public static class MediaNamer
{
    /// <summary>
    /// Builds a movie folder name: <c>Title (Year) [tmdbid-N]</c>, falling back to <c>[imdbid-tt…]</c>.
    /// </summary>
    /// <param name="movie">The identified movie.</param>
    /// <returns>The folder name.</returns>
    public static string MovieFolderName(MovieIdentity movie)
    {
        ArgumentNullException.ThrowIfNull(movie);

        // Only well-formed ids reach a folder name (defence in depth: they are also cleaned where they enter)
        var id = ProviderIdRules.IsValid("Tmdb", movie.TmdbId) ? $"[tmdbid-{movie.TmdbId}]"
            : ProviderIdRules.IsValid("Imdb", movie.ImdbId) ? $"[imdbid-{movie.ImdbId}]"
            : null;
        return Compose(movie.Title, movie.Year, id);
    }

    /// <summary>
    /// Builds a movie file name. The file starts with the exact folder name (including provider ids) so Jellyfin
    /// can group multiple versions; an edition is appended as <c> - Label</c>.
    /// </summary>
    /// <param name="movie">The identified movie.</param>
    /// <param name="extension">File extension including the dot, e.g. <c>.mkv</c>.</param>
    /// <returns>The file name.</returns>
    public static string MovieFileName(MovieIdentity movie, string extension)
    {
        ArgumentNullException.ThrowIfNull(movie);

        var edition = FileNameSanitizer.Truncate(FileNameSanitizer.Sanitize(movie.Edition ?? string.Empty), 30);
        var stem = edition.Length > 0 ? $"{MovieFolderName(movie)} - {edition}" : MovieFolderName(movie);
        return stem + NormaliseExtension(extension);
    }

    /// <summary>
    /// Builds a movie's path relative to the library root.
    /// </summary>
    /// <param name="movie">The identified movie.</param>
    /// <param name="extension">File extension including the dot.</param>
    /// <returns><c>Folder/File.ext</c>.</returns>
    public static string MovieRelativePath(MovieIdentity movie, string extension)
        => Path.Combine(MovieFolderName(movie), MovieFileName(movie, extension));

    /// <summary>
    /// Builds a series folder name: <c>Title (Year) [tvdbid-N] [tmdbid-N]</c> (every known id, TheTVDB first).
    /// </summary>
    /// <param name="series">The identified series.</param>
    /// <returns>The folder name.</returns>
    public static string SeriesFolderName(SeriesIdentity series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var ids = new StringBuilder();
        if (ProviderIdRules.IsValid("Tvdb", series.TvdbId))
        {
            ids.Append(CultureInfo.InvariantCulture, $"[tvdbid-{series.TvdbId}]");
        }

        if (ProviderIdRules.IsValid("Tmdb", series.TmdbId))
        {
            ids.Append(ids.Length > 0 ? " " : string.Empty).Append(CultureInfo.InvariantCulture, $"[tmdbid-{series.TmdbId}]");
        }

        return Compose(series.Title, series.Year, ids.Length > 0 ? ids.ToString() : null);
    }

    /// <summary>
    /// Builds a season folder name: <c>Season 01</c>; specials are <c>Season 00</c>.
    /// </summary>
    /// <param name="season">Season number.</param>
    /// <returns>The folder name.</returns>
    public static string SeasonFolderName(int season)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(season);
        return string.Create(CultureInfo.InvariantCulture, $"Season {season:00}");
    }

    /// <summary>
    /// Builds an episode code: <c>S01E04</c>, or <c>S01E01-E02</c> for multi-episode files.
    /// </summary>
    /// <param name="season">Season number.</param>
    /// <param name="episode">First episode number.</param>
    /// <param name="endingEpisode">Last episode number, if the file spans several.</param>
    /// <returns>The episode code.</returns>
    public static string EpisodeCode(int season, int episode, int? endingEpisode = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(season);
        ArgumentOutOfRangeException.ThrowIfNegative(episode);

        var code = string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}");
        return endingEpisode is { } end && end > episode
            ? code + string.Create(CultureInfo.InvariantCulture, $"-E{end:00}")
            : code;
    }

    /// <summary>
    /// Builds an episode file name: <c>Series S01E04 - Title.ext</c> (title omitted when unknown).
    /// </summary>
    /// <param name="episode">The identified episode.</param>
    /// <param name="extension">File extension including the dot.</param>
    /// <returns>The file name.</returns>
    public static string EpisodeFileName(EpisodeIdentity episode, string extension)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var series = Title(episode.Series.Title);
        var code = EpisodeCode(episode.Season, episode.Episode, episode.EndingEpisode);

        // Long episode titles (common in CJK) are shortened so the whole name fits the file system's limit
        var prefix = $"{series} {code} - ";
        var title = FileNameSanitizer.Truncate(FileNameSanitizer.Sanitize(episode.Title ?? string.Empty), FileNameSanitizer.MaxStemBytes - Encoding.UTF8.GetByteCount(prefix));
        var stem = title.Length > 0 ? prefix + title : $"{series} {code}";
        return stem + NormaliseExtension(extension);
    }

    /// <summary>
    /// Gets the Jellyfin extras folder name for an extra type.
    /// </summary>
    /// <param name="type">The extra type.</param>
    /// <returns>The folder name, e.g. <c>featurettes</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">For <see cref="ExtraType.None"/> and <see cref="ExtraType.Sample"/>, which are not filed as extras.</exception>
    public static string ExtrasFolderName(ExtraType type) => type switch
    {
        ExtraType.Trailer => "trailers",
        ExtraType.Featurette => "featurettes",
        ExtraType.DeletedScene => "deleted scenes",
        ExtraType.BehindTheScenes => "behind the scenes",
        ExtraType.Interview => "interviews",
        ExtraType.ShortFilm => "shorts",
        ExtraType.Clip => "clips",
        ExtraType.Scene => "scenes",
        ExtraType.Other => "extras",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not filed as an extra."),
    };

    /// <summary>
    /// A title ready for a file or folder name: sanitised, shortened to <see cref="FileNameSanitizer.MaxTitleBytes"/>,
    /// and <c>Untitled</c> when nothing printable is left (a film called "?").
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The safe title; never empty.</returns>
    public static string Title(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var name = FileNameSanitizer.Truncate(FileNameSanitizer.Sanitize(title), FileNameSanitizer.MaxTitleBytes);
        return name.Length > 0 ? name : "Untitled";
    }

    private static string Compose(string title, int? year, string? ids)
    {
        var name = Title(title);
        if (year is { } y)
        {
            name += string.Create(CultureInfo.InvariantCulture, $" ({y})");
        }

        return FileNameSanitizer.AvoidReserved(ids is null ? name : $"{name} {ids}");
    }

    private static string NormaliseExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return extension.StartsWith('.') ? extension : "." + extension;
    }
}
