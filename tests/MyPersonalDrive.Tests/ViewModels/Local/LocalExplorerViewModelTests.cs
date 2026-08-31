using MyPersonalDrive.Services;
using MyPersonalDrive.Tests;
using MyPersonalDrive.ViewModels.Local;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels.Local;

[Collection(AppDataCollection.Name)]
public class LocalExplorerViewModelTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.LocalExplorer.AppData").FullName;
    private readonly string _root = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.LocalExplorer.Root").FullName;
    private readonly string? _originalAppData;

    public LocalExplorerViewModelTests()
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

    private LocalExplorerViewModel Build(string homePath)
        => new(new FakeHomeLocalFileSystemService(homePath), new AppSettingsService());

    [Fact]
    public async Task NavigateAsync_ListsAndSortsWithFoldersFirst()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zzz-folder"));
        File.WriteAllText(Path.Combine(_root, "aaa-file.txt"), "x");

        var sut = Build(_root);
        await sut.NavigateAsync(_root);

        Assert.Equal(_root, sut.CurrentPath);
        Assert.Equal(2, sut.Items.Count);
        Assert.True(sut.Items[0].IsFolder); // folders first, even though "aaa" sorts before "zzz"
        Assert.Equal("zzz-folder", sut.Items[0].DisplayName);
        Assert.Equal("aaa-file.txt", sut.Items[1].DisplayName);
    }

    [Fact]
    public async Task NavigatingIntoAFolder_UpdatesBreadcrumbs()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;

        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        await sut.Items.Single(i => i.DisplayName == "sub").RowCommand.ExecuteAsync();

        Assert.Equal(sub, sut.CurrentPath);
        Assert.Contains(sut.BreadcrumbItems, b => b.Label == "sub" && b.IsCurrent);
    }

    [Fact]
    public async Task Back_ReturnsToTheParentFolder()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;

        var sut = Build(_root);
        await sut.NavigateAsync(sub);
        await sut.BackCommand.ExecuteAsync();

        Assert.Equal(_root, sut.CurrentPath);
    }

    [Fact]
    public async Task ToggleHiddenFiles_ChangesVisibleItems_AndPersists()
    {
        File.WriteAllText(Path.Combine(_root, ".hidden"), "x");
        var settings = new AppSettingsService();
        var sut = new LocalExplorerViewModel(new FakeHomeLocalFileSystemService(_root), settings);
        await sut.NavigateAsync(_root);

        Assert.DoesNotContain(sut.Items, i => i.DisplayName == ".hidden");

        await sut.ToggleHiddenFilesCommand.ExecuteAsync();

        Assert.True(sut.ShowHiddenFiles);
        Assert.Contains(sut.Items, i => i.DisplayName == ".hidden");
        Assert.True(settings.Load().ShowHiddenLocalFiles);
    }

    [Fact]
    public async Task NavigatingToAMissingFolder_SurfacesAStatusMessage_InsteadOfThrowing()
    {
        var sut = Build(_root);

        await sut.NavigateAsync(Path.Combine(_root, "does-not-exist"));

        Assert.NotNull(sut.StatusMessage);
    }

    [Fact]
    public async Task InitializeAsync_NavigatesToHome()
    {
        var sut = Build(_root);

        await sut.InitializeAsync();

        Assert.Equal(_root, sut.CurrentPath);
        Assert.Equal(_root, sut.HomePath);
    }

    /// <summary>Points <see cref="LocalFileSystemService.GetHomeDirectory"/> at a temp folder rather than the real OS home, so tests stay hermetic.</summary>
    private sealed class FakeHomeLocalFileSystemService(string home) : LocalFileSystemService
    {
        public override string GetHomeDirectory() => home;
    }
}
