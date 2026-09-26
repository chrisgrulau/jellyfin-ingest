using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Identification;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented titles throughout.
public sealed class SearchCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ingest-cache-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_dir, "search-cache.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Counting : IMetadataLookup
    {
        public int Searches { get; private set; }

        public int EpisodeLookups { get; private set; }

        public bool Down { get; set; }

        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<MetadataCandidate>>(Down || name != "Lantern" ? [] : [new MetadataCandidate { Name = "Lantern", Year = 2001, ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "7" }, ProviderRank = 0 }]);
        }

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);
        }

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
        {
            EpisodeLookups++;
            return Task.FromResult<string?>(episode == 1 ? "Pilot" : null);
        }

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EpisodeListing>>([]);
    }

    [Fact]
    public async Task A_season_pack_searches_for_its_show_once()
    {
        var inner = new Counting();
        var cache = new CachingMetadataLookup(inner, new Clock());

        for (var i = 0; i < 24; i++)
        {
            Assert.Single(await cache.SearchSeriesAsync("Lantern", null, CancellationToken.None));
        }

        Assert.Equal(1, inner.Searches);
        Assert.Equal(23, cache.Hits);
    }

    [Fact]
    public async Task Results_survive_a_restart_until_they_expire()
    {
        var clock = new Clock();
        var first = new CachingMetadataLookup(new Counting(), clock, CachePath);
        await first.SearchSeriesAsync("Lantern", 2001, CancellationToken.None);
        await first.GetEpisodeTitleAsync(new Dictionary<string, string> { ["Tvdb"] = "7" }, 1, 1, CancellationToken.None);
        first.Save();

        var inner = new Counting();
        var second = new CachingMetadataLookup(inner, clock, CachePath);
        var hit = Assert.Single(await second.SearchSeriesAsync("Lantern", 2001, CancellationToken.None));
        Assert.Equal("7", hit.ProviderIds["Tvdb"]);
        Assert.Equal("Pilot", await second.GetEpisodeTitleAsync(new Dictionary<string, string> { ["Tvdb"] = "7" }, 1, 1, CancellationToken.None));
        Assert.Equal(0, inner.Searches + inner.EpisodeLookups);

        clock.Now += CachingMetadataLookup.Lifetime;
        await second.SearchSeriesAsync("Lantern", 2001, CancellationToken.None);
        Assert.Equal(1, inner.Searches);
    }

    [Fact]
    public async Task Nothing_found_is_asked_again_in_the_next_sweep_and_counted()
    {
        var inner = new Counting { Down = true };
        var cache = new CachingMetadataLookup(inner, new Clock());

        await cache.SearchSeriesAsync("Lantern", null, CancellationToken.None);
        await cache.SearchSeriesAsync("Lantern", null, CancellationToken.None);
        Assert.Equal(1, inner.Searches);
        Assert.Equal(1, cache.ConsecutiveEmpty);

        await cache.SearchMoviesAsync("Kite", null, CancellationToken.None);
        Assert.Equal(2, cache.ConsecutiveEmpty);

        cache.BeginSweep();
        inner.Down = false;
        Assert.Single(await cache.SearchSeriesAsync("Lantern", null, CancellationToken.None));
        Assert.Equal(3, inner.Searches);
        Assert.Equal(0, cache.ConsecutiveEmpty);
    }

    [Fact]
    public void A_damaged_cache_file_is_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, "{ nope");

        var cache = new CachingMetadataLookup(new Counting(), new Clock(), CachePath);

        Assert.Equal(0, cache.Hits);
    }
}
