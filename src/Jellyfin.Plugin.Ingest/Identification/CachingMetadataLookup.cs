using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Ingest.Identification;

/// <summary>
/// Remembers provider searches so the same title isn't searched again and again: a season pack of 24 episodes searches
/// for its show once, and releases planned again after a restart, a settings change or a retry reuse earlier results.
/// These searches use the server's shared provider quotas (OMDb's daily limit, for example).
/// <list type="bullet">
/// <item>Results that found something are kept for <see cref="Lifetime"/>, on disk, so they survive restarts.</item>
/// <item>Empty results are only remembered until <see cref="BeginSweep"/>: nothing found may be an outage, which
/// Jellyfin reports the same way as an unknown title, so it is always asked again later.</item>
/// </list>
/// It also counts searches in a row that came back empty (<see cref="ConsecutiveEmpty"/>), so the service can pause
/// identification when the providers look down.
/// </summary>
public sealed class CachingMetadataLookup : IMetadataLookup
{
    /// <summary>How long a search that found something is reused.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>The most searches kept (the newest).</summary>
    public const int MaxEntries = 500;

    /// <summary>The most season listings kept in memory.</summary>
    public const int MaxSeasons = 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IMetadataLookup _inner;
    private readonly TimeProvider _clock;
    private readonly string? _path;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CacheEntry> _entries;
    private readonly Dictionary<string, (DateTimeOffset Time, IReadOnlyList<EpisodeListing> Episodes)> _seasons = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emptyThisSweep = new(StringComparer.Ordinal);
    private bool _dirty;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingMetadataLookup"/> class.
    /// </summary>
    /// <param name="inner">The real lookup.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="path">JSON file to keep results in across restarts, or <c>null</c> for memory only.</param>
    public CachingMetadataLookup(IMetadataLookup inner, TimeProvider clock, string? path = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _path = path;
        _entries = Load(path);
    }

    /// <summary>Gets how many searches in a row returned nothing (reset by any search that finds something).</summary>
    public int ConsecutiveEmpty { get; private set; }

    /// <summary>Gets how many searches were answered from memory (for tests and diagnostics).</summary>
    public int Hits { get; private set; }

    /// <summary>
    /// Starts a sweep: empty results from earlier sweeps are forgotten, so those titles are searched again.
    /// </summary>
    public void BeginSweep()
    {
        lock (_lock)
        {
            _emptyThisSweep.Clear();
        }
    }

    /// <summary>Resets <see cref="ConsecutiveEmpty"/> (after the service has acted on it).</summary>
    public void ResetEmptyCount() => ConsecutiveEmpty = 0;

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
        => SearchAsync("S", name, year, () => _inner.SearchSeriesAsync(name, year, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
        => SearchAsync("M", name, year, () => _inner.SearchMoviesAsync(name, year, cancellationToken));

    /// <inheritdoc />
    public async Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seriesProviderIds);
        var key = "E|" + string.Join(',', seriesProviderIds.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value))
            + "|" + season.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + episode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (Fresh(key) is { Title: { } cached })
            {
                Hits++;
                return cached;
            }
        }

        var title = await _inner.GetEpisodeTitleAsync(seriesProviderIds, season, episode, cancellationToken).ConfigureAwait(false);
        if (title is not null)
        {
            lock (_lock)
            {
                _entries[key] = new CacheEntry { Time = _clock.GetUtcNow(), Title = title };
                _dirty = true;
            }
        }

        return title;
    }

    /// <inheritdoc />
    /// <remarks>Season listings are kept in memory only (they carry synopses), for <see cref="Lifetime"/>.</remarks>
    public async Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seriesProviderIds);
        var key = string.Join(',', seriesProviderIds.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value))
            + "|" + season.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (_seasons.TryGetValue(key, out var cached) && _clock.GetUtcNow() - cached.Time < Lifetime)
            {
                Hits++;
                return cached.Episodes;
            }
        }

        var episodes = await _inner.ListSeasonAsync(seriesProviderIds, season, cancellationToken).ConfigureAwait(false);
        if (episodes.Count > 0)
        {
            lock (_lock)
            {
                if (_seasons.Count >= MaxSeasons)
                {
                    _seasons.Remove(_seasons.MinBy(e => e.Value.Time).Key);
                }

                _seasons[key] = (_clock.GetUtcNow(), episodes);
            }
        }

        return episodes;
    }

    /// <summary>
    /// Writes the remembered searches to disk if anything changed (expired ones are dropped; at most
    /// <see cref="MaxEntries"/>, newest first). Failures only lose the cache.
    /// </summary>
    public void Save()
    {
        if (_path is null)
        {
            return;
        }

        Dictionary<string, CacheEntry> keep;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            keep = _entries.Where(e => now - e.Value.Time < Lifetime).OrderByDescending(e => e.Value.Time).Take(MaxEntries).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            _entries.Clear();
            foreach (var (k, v) in keep)
            {
                _entries[k] = v;
            }

            _dirty = false;
        }

        try
        {
            JsonFile.WriteAtomic(_path, keep, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a cache: searching again is the worst that happens
        }
    }

    private static Dictionary<string, CacheEntry> Load(string? path)
    {
        // Policy: only a cache, so missing, damaged and unreadable all start empty (nothing is logged, and a damaged
        // file is simply replaced by the next save)
        if (path is not null
            && JsonFile.Read<Dictionary<string, CacheEntry>>(path, JsonOptions) is { IsLoaded: true, Value: { } loaded })
        {
            return new Dictionary<string, CacheEntry>(loaded.Where(e => e.Value is not null), StringComparer.Ordinal);
        }

        return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
    }

    private CacheEntry? Fresh(string key)
        => _entries.TryGetValue(key, out var e) && _clock.GetUtcNow() - e.Time < Lifetime ? e : null;

    private async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(string kind, string name, int? year, Func<Task<IReadOnlyList<MetadataCandidate>>> search)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = kind + "|" + name.Trim().ToUpperInvariant() + "|" + (year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        lock (_lock)
        {
            if (Fresh(key) is { Results: { Count: > 0 } cached })
            {
                Hits++;
                return cached;
            }

            if (_emptyThisSweep.Contains(key))
            {
                Hits++;
                return [];
            }
        }

        var results = await search().ConfigureAwait(false);
        lock (_lock)
        {
            if (results.Count > 0)
            {
                _entries[key] = new CacheEntry { Time = _clock.GetUtcNow(), Results = [.. results] };
                _dirty = true;
                ConsecutiveEmpty = 0;
            }
            else
            {
                _emptyThisSweep.Add(key);
                ConsecutiveEmpty++;
            }
        }

        return results;
    }

    private sealed record CacheEntry
    {
        public DateTimeOffset Time { get; init; }

        public List<MetadataCandidate>? Results { get; init; }

        public string? Title { get; init; }
    }
}
