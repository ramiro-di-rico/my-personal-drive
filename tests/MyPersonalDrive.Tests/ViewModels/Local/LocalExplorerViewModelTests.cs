using MyPersonalDrive.Models;
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

    [Fact]
    public async Task DeleteItemAsync_Confirmed_RemovesTheItem_AndRefreshes()
    {
        File.WriteAllText(Path.Combine(_root, "doomed.txt"), "x");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        sut.RequestConfirmationAsync = _ => Task.FromResult(true);

        await sut.Items.Single(i => i.DisplayName == "doomed.txt").DeleteCommand.ExecuteAsync();

        Assert.False(File.Exists(Path.Combine(_root, "doomed.txt")));
        Assert.DoesNotContain(sut.Items, i => i.DisplayName == "doomed.txt");
    }

    [Fact]
    public async Task DeleteItemAsync_Declined_LeavesTheItemInPlace()
    {
        File.WriteAllText(Path.Combine(_root, "spared.txt"), "x");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        sut.RequestConfirmationAsync = _ => Task.FromResult(false);

        await sut.Items.Single(i => i.DisplayName == "spared.txt").DeleteCommand.ExecuteAsync();

        Assert.True(File.Exists(Path.Combine(_root, "spared.txt")));
    }

    [Fact]
    public async Task RenameItemAsync_RenamesOnDisk_AndRefreshes()
    {
        File.WriteAllText(Path.Combine(_root, "old.txt"), "x");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        sut.RequestRenameAsync = _ => Task.FromResult<string?>("new.txt");

        await sut.Items.Single(i => i.DisplayName == "old.txt").RenameCommand.ExecuteAsync();

        Assert.False(File.Exists(Path.Combine(_root, "old.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "new.txt")));
        Assert.Contains(sut.Items, i => i.DisplayName == "new.txt");
    }

    [Fact]
    public async Task CopyPathAsync_SendsTheFullPathToTheClipboardHandler()
    {
        File.WriteAllText(Path.Combine(_root, "note.txt"), "x");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        string? copied = null;
        sut.RequestCopyToClipboardAsync = text => { copied = text; return Task.CompletedTask; };

        await sut.Items.Single(i => i.DisplayName == "note.txt").CopyPathCommand.ExecuteAsync();

        Assert.Equal(Path.Combine(_root, "note.txt"), copied);
    }

    [Fact]
    public async Task SyncSelectedPathAsync_OnAFolder_OpensTheWizardWithThatLocalPath()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        string? requestedPath = null;
        sut.RequestSyncSelectedPathAsync = path => { requestedPath = path; return Task.CompletedTask; };

        await sut.Items.Single(i => i.DisplayName == "sub").SyncSelectedPathCommand.ExecuteAsync();

        Assert.Equal(folder, requestedPath);
    }

    [Fact]
    public async Task SyncSelectedPathAsync_OnAFile_DoesNothing()
    {
        File.WriteAllText(Path.Combine(_root, "note.txt"), "x");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        var called = false;
        sut.RequestSyncSelectedPathAsync = _ => { called = true; return Task.CompletedTask; };

        // A file row has no SyncSelectedPathCommand available (CanCreateSyncPair is folder-only),
        // so this exercises the guard directly rather than through the disabled command.
        await sut.SyncSelectedPathAsync(sut.Items.Single(i => i.DisplayName == "note.txt").Item);

        Assert.False(called);
    }

    [Fact]
    public async Task ShowPropertiesAsync_IncludesNamePathTypeAndSize()
    {
        File.WriteAllText(Path.Combine(_root, "note.txt"), "hello");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        IReadOnlyList<PropertyField>? shownFields = null;
        sut.RequestShowPropertiesAsync = (_, fields) => { shownFields = fields; return Task.CompletedTask; };

        await sut.Items.Single(i => i.DisplayName == "note.txt").PropertiesCommand.ExecuteAsync();

        Assert.NotNull(shownFields);
        Assert.Contains(shownFields!, f => f.Label == "Name" && f.Value == "note.txt");
        Assert.Contains(shownFields!, f => f.Label == "Type" && f.Value == "File");
        Assert.Contains(shownFields!, f => f.Label == "Size");
    }

    /// <summary>Points <see cref="LocalFileSystemService.GetHomeDirectory"/> at a temp folder rather than the real OS home, so tests stay hermetic.</summary>
    private sealed class FakeHomeLocalFileSystemService(string home) : LocalFileSystemService
    {
        public override string GetHomeDirectory() => home;
    }
}
