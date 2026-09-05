using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.ViewModels;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// docs/PLAN-BROWSER-VIEWS.md M5, shallow half. What these tests actually protect is the honesty of
/// the copy: the panel shows one big number next to a folder listing, and a user reads that as
/// "this is what the folder weighs". It isn't — it's this folder's own files — so the scope note has
/// to say so whenever there are subfolders or files with no known size.
/// </summary>
public class FolderMetricsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static FolderMetricsViewModel Build(out List<DriveItem> selected)
    {
        var captured = new List<DriveItem>();
        selected = captured;
        return new FolderMetricsViewModel(item =>
        {
            captured.Add(item);
            return Task.CompletedTask;
        });
    }

    private static DriveItem File(string name, long? size, DateTimeOffset? modifiedAt = null)
        => new($"/my-files/{name}", name, IsFolder: false, Size: size, ModifiedAt: modifiedAt);

    private static DriveItem Folder(string name)
        => new($"/my-files/{name}", name, IsFolder: true);

    private static void Load(FolderMetricsViewModel sut, params DriveItem[] children)
        => sut.Update(FolderMetricsCalculator.FromChildren("/my-files", children, Now));

    [Fact]
    public void AnEmptyFolder_SaysSo_AndShowsNoSections()
    {
        var sut = Build(out _);

        Load(sut);

        Assert.False(sut.HasItems);
        Assert.Equal("This folder is empty.", sut.Headline);
        Assert.Empty(sut.Buckets);
        Assert.False(sut.HasLargestItems);
        Assert.Equal(string.Empty, sut.ScopeNote);
    }

    [Fact]
    public void Headline_CountsFilesAndFolders()
    {
        var sut = Build(out _);

        Load(sut, Folder("Fotos"), File("a.pdf", 100), File("b.pdf", 100));

        Assert.Equal("2 files · 1 folder", sut.Headline);
    }

    [Fact]
    public void WithSubfolders_TheScopeNote_SaysTheTotalIsNotRecursive()
    {
        var sut = Build(out _);

        Load(sut, Folder("Fotos"), Folder("Libros"), File("a.pdf", 1024));

        Assert.Equal("1.0 KB", sut.TotalSizeText);
        Assert.Equal("Does not include the contents of 2 subfolders.", sut.ScopeNote);
    }

    [Fact]
    public void WithUnknownSizes_TheScopeNote_SaysHowMany()
    {
        var sut = Build(out _);

        Load(sut, File("a.pdf", 1024), File("b.pdf", null));

        Assert.Equal("1 file with no known size.", sut.ScopeNote);
    }

    [Fact]
    public void WithNothingMissing_TheScopeNote_StillQualifiesTheSize()
    {
        var sut = Build(out _);

        Load(sut, File("a.pdf", 1024));

        // The sizes are the CLI's claimed (pre-encryption) sizes, so they won't match Proton's
        // own quota figure. Saying nothing at all here would be the dishonest option.
        Assert.Equal("Size as declared when the files were uploaded.", sut.ScopeNote);
    }

    [Fact]
    public void Buckets_AreLabelledAndScaledAgainstTheLargest()
    {
        var sut = Build(out _);

        Load(sut, File("clip.webm", 1000), File("photo.jpg", 500));

        Assert.Equal(2, sut.Buckets.Count);
        Assert.Equal("Vídeos", sut.Buckets[0].Label);
        Assert.Equal(FolderMetricBucketViewModel.BarMaxWidth, sut.Buckets[0].BarWidth);
        Assert.Equal(FolderMetricBucketViewModel.BarMaxWidth / 2, sut.Buckets[1].BarWidth);
    }

    [Fact]
    public void AZeroSizeBucket_StillGetsAVisibleBar()
    {
        var sut = Build(out _);

        Load(sut, Folder("Fotos"), File("a.pdf", 1000));

        var folderBucket = Assert.Single(sut.Buckets, bucket => bucket.Kind == FileKind.Folder);
        Assert.True(folderBucket.BarWidth >= 2);
        Assert.Equal("—", folderBucket.SizeText);
    }

    [Fact]
    public async Task ALargestItemRow_SelectsThatItem()
    {
        var sut = Build(out var selected);
        Load(sut, File("big.bin", 5000), File("small.bin", 10));

        await sut.LargestItems[0].SelectCommand.ExecuteAsync();

        Assert.Equal("big.bin", Assert.Single(selected).Name);
    }

    [Fact]
    public void Update_ReplacesThePreviousFolderEntirely()
    {
        var sut = Build(out _);
        Load(sut, File("clip.webm", 5000), File("photo.jpg", 100));

        Load(sut, File("only.pdf", 20));

        Assert.Single(sut.Buckets);
        Assert.Equal(FileKind.Pdf, sut.Buckets[0].Kind);
        Assert.Single(sut.LargestItems);
    }

    [Fact]
    public void Timestamps_FallBackToADash_WhenTheCliGaveNone()
    {
        var sut = Build(out _);

        Load(sut, File("a.pdf", 10, null));

        Assert.Equal("—", sut.NewestText);
        Assert.Equal("—", sut.OldestText);
    }
}
