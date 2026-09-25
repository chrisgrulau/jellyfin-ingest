using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ingest.Service;
using Xunit;

namespace Jellyfin.Plugin.Ingest.Tests;

// Invented release names throughout.
public sealed class QuarantineListingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ingest-qlist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Lists_dated_folders_newest_first_with_releases_and_files()
    {
        var older = Path.Combine(_root, "2026-09-01");
        var newer = Path.Combine(_root, "2026-09-20");
        QuarantineMarkers.Mark(_root, older);
        QuarantineMarkers.Mark(_root, newer);
        Write(older, "Lantern.S01.1080p-GRP/info.nfo", 10);
        Write(older, "Lantern.S01.1080p-GRP/Screens/01.jpg", 200);
        Write(newer, "Harbour.2024.720p-GRP/readme.txt", 5);
        Write(newer, "stray.url", 3);

        var list = QuarantineListing.List(_root, new DateOnly(2026, 9, 25), 30);

        Assert.Equal(["2026-09-20", "2026-09-01"], list.Select(f => f.Date));
        var first = list[1];
        Assert.True(first.Managed);
        Assert.Equal(new DateOnly(2026, 10, 2), first.DeletesOn);
        var release = Assert.Single(first.Releases);
        Assert.Equal("Lantern.S01.1080p-GRP", release.Name);
        Assert.Equal(2, release.FileCount);
        Assert.Equal(210, release.Bytes);
        Assert.Equal(["info.nfo", Path.Combine("Screens", "01.jpg")], release.Files.Select(f => f.Path));
        Assert.Equal(["Harbour.2024.720p-GRP", "stray.url"], list[0].Releases.Select(r => r.Name));
        Assert.Equal(8, list[0].Bytes);
    }

    [Fact]
    public void Deletion_date_matches_the_purge()
    {
        var dated = Path.Combine(_root, "2026-09-01");
        QuarantineMarkers.Mark(_root, dated);
        Write(dated, "Lantern.S01-GRP/a.nfo", 1);
        var deletesOn = QuarantineListing.List(_root, new DateOnly(2026, 9, 25), 30)[0].DeletesOn!.Value;

        Assert.Empty(QuarantinePurger.Expired(["2026-09-01"], deletesOn.AddDays(-1), 30));
        Assert.Single(QuarantinePurger.Expired(["2026-09-01"], deletesOn, 30));
    }

    [Fact]
    public void Unmarked_folders_are_shown_as_never_deleted_and_other_folders_ignored()
    {
        Write(Path.Combine(_root, "2026-09-01"), "Someone.Elses/file.txt", 1);
        Write(Path.Combine(_root, "not-a-date"), "x.txt", 1);

        var folder = Assert.Single(QuarantineListing.List(_root, new DateOnly(2026, 9, 25), 30));
        Assert.False(folder.Managed);
        Assert.Null(folder.DeletesOn);
    }

    [Fact]
    public void The_file_list_is_capped_but_everything_is_counted()
    {
        var dated = Path.Combine(_root, "2026-09-01");
        QuarantineMarkers.Mark(_root, dated);
        for (var i = 0; i < 5; i++)
        {
            Write(dated, $"Lantern.S01-GRP/{i}.jpg", 1);
        }

        var release = Assert.Single(QuarantineListing.List(_root, new DateOnly(2026, 9, 25), 30, maxFiles: 2)[0].Releases);
        Assert.Equal(5, release.FileCount);
        Assert.Equal(2, release.Files.Count);
    }

    [Fact]
    public void Links_are_not_followed()
    {
        var outside = Path.Combine(_root, "outside");
        Write(outside, "secret.txt", 1);
        var dated = Path.Combine(_root, "q", "2026-09-01");
        QuarantineMarkers.Mark(Path.Combine(_root, "q"), dated);
        Directory.CreateSymbolicLink(Path.Combine(dated, "linked"), outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "q", "2026-09-02"), outside);

        var list = QuarantineListing.List(Path.Combine(_root, "q"), new DateOnly(2026, 9, 25), 30);
        var folder = Assert.Single(list);
        Assert.Empty(folder.Releases);
    }

    [Fact]
    public void A_missing_quarantine_lists_nothing()
        => Assert.Empty(QuarantineListing.List(Path.Combine(_root, "none"), new DateOnly(2026, 9, 25), 30));

    private static void Write(string folder, string relative, int bytes)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }
}
