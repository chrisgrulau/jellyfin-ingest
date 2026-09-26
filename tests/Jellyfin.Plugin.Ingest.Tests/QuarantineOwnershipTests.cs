using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Ingest.Planning;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// ING-18: a date-named folder Ingest didn't create is never used, marked or purged
public sealed class QuarantineOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ingest-qown-" + Guid.NewGuid().ToString("N"));

    public QuarantineOwnershipTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Someone_elses_dated_folder_survives_quarantine_and_purge()
    {
        var theirs = Path.Combine(_root, "2026-09-26");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "backup.tar"), "not Ingest's");

        var ours = QuarantineMarkers.DatedFolderFor(_root, new DateOnly(2026, 9, 26));
        Assert.Equal(Path.Combine(_root, "2026-09-26 (Ingest)"), ours);
        Assert.Throws<IOException>(() => QuarantineMarkers.Mark(_root, theirs));
        QuarantineMarkers.Mark(_root, ours);
        File.WriteAllText(Path.Combine(ours, "info.nfo"), "clutter");
        Assert.Equal(ours, QuarantineMarkers.DatedFolderFor(_root, new DateOnly(2026, 9, 26)));

        var deleted = QuarantinePurger.Purge(_root, new DateOnly(2026, 11, 30), 30);

        Assert.Equal([ours], deleted);
        Assert.True(File.Exists(Path.Combine(theirs, "backup.tar")));
    }

    [Fact]
    public void A_further_clash_gets_a_numbered_name()
    {
        Directory.CreateDirectory(Path.Combine(_root, "2026-09-26"));
        Directory.CreateDirectory(Path.Combine(_root, "2026-09-26 (Ingest)"));

        Assert.Equal(Path.Combine(_root, "2026-09-26 (Ingest 2)"), QuarantineMarkers.DatedFolderFor(_root, new DateOnly(2026, 9, 26)));
    }

    [Theory]
    [InlineData("2026-09-26", true)]
    [InlineData("2026-09-26 (Ingest)", true)]
    [InlineData("2026-09-26 (Ingest 3)", true)]
    [InlineData("2026-09-26 (Ingest x)", false)]
    [InlineData("2026-09-26 photos", false)]
    [InlineData("2026-13-40", false)]
    [InlineData("keep-me", false)]
    public void Only_dated_folder_names_are_recognised(string name, bool dated)
        => Assert.Equal(dated, QuarantineMarkers.DateOf(name) is not null);

    [Fact]
    public void Old_unmarked_folders_are_marked_only_if_the_log_accounts_for_everything_in_them()
    {
        var dated = Path.Combine(_root, "2026-09-01");
        Directory.CreateDirectory(Path.Combine(dated, "Release"));
        var logged = Path.Combine(dated, "Release", "info.nfo");
        File.WriteAllText(logged, "x");
        var set = new HashSet<string>(PathGuard.Comparer) { PathGuard.Normalise(logged) };

        Assert.True(QuarantineMarkers.HoldsOnlyLoggedFiles(dated, set));

        File.WriteAllText(Path.Combine(dated, "someone-elses.jpg"), "y");
        Assert.False(QuarantineMarkers.HoldsOnlyLoggedFiles(dated, set));
    }
}
