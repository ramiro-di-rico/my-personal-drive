using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Local;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels.Local;

/// <summary>The local pane's sort: its own state, persisted apart from the cloud pane's.</summary>
[Collection(AppDataCollection.Name)]
public class LocalExplorerSortTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.LocalSort.AppData").FullName;
    private readonly string _root = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.LocalSort.Root").FullName;
    private readonly string? _originalAppData;

    public LocalExplorerSortTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<LocalExplorerViewModel> BuildAsync()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "xxxxxx");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "xxx");
        Directory.CreateDirectory(Path.Combine(_root, "zdir"));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "a.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "b.txt"), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "c.txt"), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var sut = new LocalExplorerViewModel(new FakeHomeLocalFileSystemService(_root), new AppSettingsService());
        await sut.NavigateAsync(_root);
        return sut;
    }

    private static string[] Names(LocalExplorerViewModel sut) => [.. sut.Items.Select(i => i.Item.Name)];

    [Fact]
    public async Task DefaultsToNameAscending_FoldersFirst()
    {
        var sut = await BuildAsync();

        Assert.Equal(["zdir", "a.txt", "b.txt", "c.txt"], Names(sut));
    }

    [Fact]
    public async Task SortBySize_StartsDescending_ThenFlips()
    {
        var sut = await BuildAsync();

        await sut.SortBySizeCommand.ExecuteAsync();
        Assert.Equal(["zdir", "a.txt", "c.txt", "b.txt"], Names(sut));

        await sut.SortBySizeCommand.ExecuteAsync();
        Assert.Equal(["zdir", "b.txt", "c.txt", "a.txt"], Names(sut));
    }

    [Fact]
    public async Task SortByModified_StartsNewestFirst()
    {
        var sut = await BuildAsync();

        await sut.SortByModifiedCommand.ExecuteAsync();

        Assert.Equal(["zdir", "b.txt", "c.txt", "a.txt"], Names(sut));
        Assert.True(sut.IsSortedByModified);
    }

    [Fact]
    public async Task SortByName_FlipsWhenAlreadyActive()
    {
        var sut = await BuildAsync();

        await sut.SortByNameCommand.ExecuteAsync();

        Assert.Equal(["zdir", "c.txt", "b.txt", "a.txt"], Names(sut));
    }

    [Fact]
    public async Task Sort_IsPersisted_AndRestoredByANewViewModel()
    {
        var sut = await BuildAsync();
        await sut.SortBySizeCommand.ExecuteAsync();

        var restored = new LocalExplorerViewModel(new FakeHomeLocalFileSystemService(_root), new AppSettingsService());
        await restored.NavigateAsync(_root);

        Assert.Equal(DriveSortKey.Size, restored.SortKey);
        Assert.True(restored.SortDescending);
        Assert.Equal(["zdir", "a.txt", "c.txt", "b.txt"], Names(restored));
    }

    [Fact]
    public async Task Sort_KeepsTheSearchFilter()
    {
        var sut = await BuildAsync();
        sut.SearchText = "a.";

        await sut.SortBySizeCommand.ExecuteAsync();

        Assert.Equal(["a.txt"], Names(sut));
    }

    /// <summary>Points the home directory at this test's temp root, like the equivalent in <see cref="LocalExplorerPdfMergeTests"/>.</summary>
    private sealed class FakeHomeLocalFileSystemService(string home) : LocalFileSystemService
    {
        public override string GetHomeDirectory() => home;
    }
}
