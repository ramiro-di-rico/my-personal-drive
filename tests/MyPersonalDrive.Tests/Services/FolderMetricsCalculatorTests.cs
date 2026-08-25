using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// docs/PLAN-BROWSER-VIEWS.md M2. The load-bearing assertions here are the honesty ones: a file
/// whose <c>activeRevision</c> gave no size must be counted in <c>UnknownSizeCount</c> rather than
/// silently treated as 0 bytes, and subfolders must be reported separately so the UI can never
/// present a shallow total as if it were recursive.
/// </summary>
public class FolderMetricsCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static DriveItem File(string name, long? size = 100, DateTimeOffset? modifiedAt = null)
        => new($"/my-files/{name}", name, IsFolder: false, Size: size, ModifiedAt: modifiedAt);

    private static DriveItem Folder(string name, DateTimeOffset? modifiedAt = null)
        => new($"/my-files/{name}", name, IsFolder: true, ModifiedAt: modifiedAt);

    [Fact]
    public void AnEmptyFolder_HasEmptyMetrics()
    {
        var metrics = FolderMetricsCalculator.FromChildren("/my-files", [], Now);

        Assert.True(metrics.IsEmpty);
        Assert.Equal(0, metrics.TotalSize);
        Assert.Empty(metrics.Buckets);
        Assert.Empty(metrics.LargestItems);
        Assert.Null(metrics.NewestModifiedAt);
        Assert.Equal(Now, metrics.ComputedAt);
    }

    [Fact]
    public void ShallowMetrics_AreNeverMarkedDeep()
    {
        var metrics = FolderMetricsCalculator.FromChildren("/my-files", [File("a.txt")], Now);

        Assert.False(metrics.IsDeep);
        Assert.True(metrics.IsComplete);
        Assert.Equal(1, metrics.ScannedFolderCount);
    }

    [Fact]
    public void FoldersAndFiles_AreCountedSeparately_AndFoldersAddNothingToTheTotal()
    {
        var metrics = FolderMetricsCalculator.FromChildren(
            "/my-files",
            [Folder("Fotos"), Folder("Libros"), File("a.pdf", 1000), File("b.pdf", 500)],
            Now);

        Assert.Equal(2, metrics.FolderCount);
        Assert.Equal(2, metrics.FileCount);
        Assert.Equal(1500, metrics.TotalSize);
    }

    [Fact]
    public void AFileWithNoSize_IsReportedAsUnknown_NotAsZero()
    {
        var metrics = FolderMetricsCalculator.FromChildren(
            "/my-files",
            [File("known.pdf", 1000), File("unknown.pdf", null), File("also-unknown", null)],
            Now);

        Assert.Equal(1000, metrics.TotalSize);
        Assert.Equal(2, metrics.UnknownSizeCount);
        Assert.Equal(3, metrics.FileCount);
    }

    [Fact]
    public void Buckets_AreOrderedByTotalSizeDescending()
    {
        var metrics = FolderMetricsCalculator.FromChildren(
            "/my-files",
            [File("clip.webm", 5000), File("doc.pdf", 200), File("photo.jpg", 900)],
            Now);

        Assert.Equal([FileKind.Video, FileKind.Image, FileKind.Pdf], metrics.Buckets.Select(b => b.Kind));
        Assert.Equal(5000, metrics.Buckets[0].TotalSize);
    }

    [Fact]
    public void Buckets_BreakTiesDeterministically()
    {
        var first = FolderMetricsCalculator.FromChildren("/my-files", [File("a.jpg", 100), File("b.pdf", 100)], Now);
        var second = FolderMetricsCalculator.FromChildren("/my-files", [File("b.pdf", 100), File("a.jpg", 100)], Now);

        Assert.Equal(first.Buckets.Select(b => b.Kind), second.Buckets.Select(b => b.Kind));
    }

    [Fact]
    public void Buckets_IncludeFolders_SoTheCountsAddUp()
    {
        var metrics = FolderMetricsCalculator.FromChildren("/my-files", [Folder("Fotos"), File("a.jpg", 10)], Now);

        var folderBucket = Assert.Single(metrics.Buckets, b => b.Kind == FileKind.Folder);
        Assert.Equal(1, folderBucket.Count);
        Assert.Equal(0, folderBucket.TotalSize);
        Assert.Equal(metrics.FileCount + metrics.FolderCount, metrics.Buckets.Sum(b => b.Count));
    }

    [Fact]
    public void LargestItems_AreCappedAndExcludeFolders()
    {
        var children = Enumerable.Range(1, 8).Select(i => File($"f{i}.bin", i * 100)).Append(Folder("Fotos")).ToList();

        var metrics = FolderMetricsCalculator.FromChildren("/my-files", children, Now);

        Assert.Equal(FolderMetricsCalculator.LargestItemCount, metrics.LargestItems.Count);
        Assert.Equal("f8.bin", metrics.LargestItems[0].Name);
        Assert.DoesNotContain(metrics.LargestItems, item => item.IsFolder);
    }

    [Fact]
    public void LargestItems_WithFewerFilesThanTheCap_ReturnsWhatThereIs()
    {
        var metrics = FolderMetricsCalculator.FromChildren("/my-files", [File("a.bin", 10), File("b.bin", 20)], Now);

        Assert.Equal(2, metrics.LargestItems.Count);
    }

    [Fact]
    public void LargestItems_SkipFilesWithNoKnownSize()
    {
        var metrics = FolderMetricsCalculator.FromChildren("/my-files", [File("known.bin", 10), File("unknown.bin", null)], Now);

        Assert.Single(metrics.LargestItems);
        Assert.Equal("known.bin", metrics.LargestItems[0].Name);
    }

    [Fact]
    public void NewestAndOldest_IgnoreItemsWithNoTimestamp()
    {
        var early = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var metrics = FolderMetricsCalculator.FromChildren(
            "/my-files",
            [File("a.bin", 10, early), File("b.bin", 10, null), File("c.bin", 10, late)],
            Now);

        Assert.Equal(late, metrics.NewestModifiedAt);
        Assert.Equal(early, metrics.OldestModifiedAt);
    }

    [Fact]
    public void WithNoTimestampsAtAll_NewestAndOldestAreNull()
    {
        var metrics = FolderMetricsCalculator.FromChildren("/my-files", [File("a.bin", 10, null)], Now);

        Assert.Null(metrics.NewestModifiedAt);
        Assert.Null(metrics.OldestModifiedAt);
    }
}
