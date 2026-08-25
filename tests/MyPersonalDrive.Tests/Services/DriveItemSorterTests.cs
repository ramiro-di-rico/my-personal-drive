using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// docs/PLAN-BROWSER-VIEWS.md V4. Two rules here are worth more than the ordering itself: folders
/// stay first whatever the key (they have no size of their own, so a "biggest first" listing would
/// otherwise open with a block of 0-byte folders), and files whose size or timestamp the CLI never
/// reported sort last in *both* directions rather than posing as the extreme.
/// </summary>
public class DriveItemSorterTests
{
    private static DriveItem File(string name, long? size = 100, DateTimeOffset? modifiedAt = null)
        => new($"/my-files/{name}", name, IsFolder: false, Size: size, ModifiedAt: modifiedAt);

    private static DriveItem Folder(string name)
        => new($"/my-files/{name}", name, IsFolder: true);

    private static readonly DateTimeOffset Early = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Late = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(DriveSortKey.Name, false)]
    [InlineData(DriveSortKey.Name, true)]
    [InlineData(DriveSortKey.Size, false)]
    [InlineData(DriveSortKey.Size, true)]
    [InlineData(DriveSortKey.Modified, false)]
    [InlineData(DriveSortKey.Modified, true)]
    [InlineData(DriveSortKey.Kind, false)]
    [InlineData(DriveSortKey.Kind, true)]
    public void FoldersComeFirst_WhateverTheKeyAndDirection(DriveSortKey key, bool descending)
    {
        var sorted = DriveItemSorter.Sort([File("zzz.bin", 9999, Late), Folder("aaa"), File("aaa.bin", 1, Early)], key, descending);

        Assert.True(sorted[0].IsFolder);
    }

    [Fact]
    public void ByName_Ascending_IsCaseInsensitive()
    {
        var sorted = DriveItemSorter.Sort([File("Beta"), File("alpha"), File("Gamma")], DriveSortKey.Name, descending: false);

        Assert.Equal(["alpha", "Beta", "Gamma"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void ByName_Descending_Reverses()
    {
        var sorted = DriveItemSorter.Sort([File("alpha"), File("Gamma"), File("Beta")], DriveSortKey.Name, descending: true);

        Assert.Equal(["Gamma", "Beta", "alpha"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void BySize_Descending_PutsTheBiggestFirst()
    {
        var sorted = DriveItemSorter.Sort([File("small.bin", 10), File("big.bin", 9000), File("mid.bin", 500)], DriveSortKey.Size, descending: true);

        Assert.Equal(["big.bin", "mid.bin", "small.bin"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void BySize_UnknownSizes_SortLastAscending()
    {
        var sorted = DriveItemSorter.Sort([File("unknown.bin", null), File("small.bin", 10)], DriveSortKey.Size, descending: false);

        Assert.Equal(["small.bin", "unknown.bin"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void BySize_UnknownSizes_SortLastDescendingToo()
    {
        var sorted = DriveItemSorter.Sort([File("unknown.bin", null), File("small.bin", 10)], DriveSortKey.Size, descending: true);

        Assert.Equal(["small.bin", "unknown.bin"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void ByModified_Descending_PutsTheNewestFirst()
    {
        var sorted = DriveItemSorter.Sort([File("old.bin", 1, Early), File("new.bin", 1, Late)], DriveSortKey.Modified, descending: true);

        Assert.Equal(["new.bin", "old.bin"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void ByModified_UnknownTimestamps_SortLastInBothDirections()
    {
        var items = new[] { File("unknown.bin", 1, null), File("dated.bin", 1, Early) };

        Assert.Equal("unknown.bin", DriveItemSorter.Sort(items, DriveSortKey.Modified, descending: false)[^1].Name);
        Assert.Equal("unknown.bin", DriveItemSorter.Sort(items, DriveSortKey.Modified, descending: true)[^1].Name);
    }

    [Fact]
    public void ByKind_GroupsTheSameKindTogether()
    {
        var sorted = DriveItemSorter.Sort(
            [File("b.jpg"), File("a.pdf"), File("a.jpg"), File("b.pdf")],
            DriveSortKey.Kind,
            descending: false);

        var kinds = sorted.Select(item => FileKindClassifier.Classify(item.Name, item.IsFolder)).ToList();
        Assert.Equal(kinds.Distinct().Count(), kinds.Chunk(2).Count(chunk => chunk.Distinct().Count() == 1));
    }

    [Fact]
    public void TiesBreakByName_SoTheSameFolderNeverComesBackInADifferentOrder()
    {
        var items = new[] { File("charlie.bin", 100), File("alpha.bin", 100), File("bravo.bin", 100) };

        var first = DriveItemSorter.Sort(items, DriveSortKey.Size, descending: true);
        var second = DriveItemSorter.Sort(items.Reverse().ToList(), DriveSortKey.Size, descending: true);

        Assert.Equal(["alpha.bin", "bravo.bin", "charlie.bin"], first.Select(item => item.Name));
        Assert.Equal(first.Select(item => item.Name), second.Select(item => item.Name));
    }

    [Fact]
    public void AnEmptyListing_Sorts()
        => Assert.Empty(DriveItemSorter.Sort([], DriveSortKey.Name, descending: false));
}
