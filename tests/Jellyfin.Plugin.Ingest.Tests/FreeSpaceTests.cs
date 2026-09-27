using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ingest.Identification;
using Jellyfin.Plugin.Ingest.Planning;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// FEAT-05: a library with several folders takes new titles in the one with the most free space. Invented titles.
public class FreeSpaceTests
{
    private static readonly MediaLibrary Movies = new("m", "Movies", LibraryKind.Films, ["/lib/A", "/lib/B", "/lib/C"]);

    private static readonly Dictionary<string, long?> Free = new(StringComparer.Ordinal) { ["/lib/A"] = 10, ["/lib/B"] = 500, ["/lib/C"] = null };

    [Fact]
    public void The_folder_with_the_most_free_space_is_chosen()
        => Assert.Equal(new LibraryTargets(null, new LibraryTarget("/lib/B", false)), LibraryRouting.TargetsOf(Movies, null, p => Free[p]));

    [Fact]
    public void A_folder_named_in_the_settings_wins()
        => Assert.Equal("/lib/A", LibraryRouting.TargetsOf(Movies, "/lib/A", p => Free[p]).Films!.Root);

    [Fact]
    public void Without_free_space_the_first_folder_is_used()
    {
        Assert.Equal("/lib/A", LibraryRouting.TargetsOf(Movies, null).Films!.Root);
        Assert.Equal("/lib/A", LibraryRouting.RoomiestOf(Movies.Locations, _ => null));
        Assert.Equal("/lib/A", LibraryRouting.RoomiestOf(Movies.Locations, _ => 7)); // a tie keeps the first
        Assert.Null(LibraryRouting.RoomiestOf([], _ => 1));
    }

    [Fact]
    public void Routing_passes_the_free_space_on()
    {
        var r = LibraryRouting.Route([new DestinationSetting("m", null)], [Movies], p => Free[p]);

        Assert.Equal("/lib/B", r.Targets.Films!.Root);
    }

    private sealed class Lookup : IMetadataLookup
    {
        public Task<IReadOnlyList<MetadataCandidate>> SearchSeriesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);

        public Task<IReadOnlyList<MetadataCandidate>> SearchMoviesAsync(string name, int? year, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataCandidate>>([new MetadataCandidate { Name = "Rocket Club", Year = 2019, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } }]);

        public Task<string?> GetEpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, int episode, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<EpisodeListing>> ListSeasonAsync(IReadOnlyDictionary<string, string> seriesProviderIds, int season, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EpisodeListing>>([]);
    }

    private static Task<IngestPlan> Plan(Func<string, bool> exists)
        => new IngestPlanner(new MediaIdentifier(new Lookup()), exists, _ => null, TimeProvider.System, null, p => PathGuard.IsUnder(p, "/lib"))
        {
            Libraries = [Movies],
        }.PlanAsync("/drop", "r", [new ReleaseFile("r/Rocket.Club.2019.1080p.mkv", 900_000_000)], LibraryTargets.Of(new LibraryTarget("/lib/B", false)), "/drop/.ingest-quarantine", null, CancellationToken.None);

    [Fact]
    public async Task A_new_film_goes_into_the_chosen_folder()
    {
        var plan = await Plan(p => p is "/lib/A" or "/lib/B" or "/lib/C");

        Assert.True(plan.IsReady, string.Join("; ", plan.Review));
        Assert.StartsWith("/lib/B/Rocket Club (2019) [tmdbid-123]/", Assert.Single(plan.Operations).Destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_film_whose_folder_is_already_in_another_folder_of_the_library_joins_it()
    {
        var plan = await Plan(p => p is "/lib/A" or "/lib/B" or "/lib/C" or "/lib/C/Rocket Club (2019) [tmdbid-123]");

        Assert.True(plan.IsReady, string.Join("; ", plan.Review));
        Assert.StartsWith("/lib/C/Rocket Club (2019) [tmdbid-123]/", Assert.Single(plan.Operations).Destination, StringComparison.Ordinal);
        Assert.Contains("/lib/C", plan.RequiredFolders);
    }
}
