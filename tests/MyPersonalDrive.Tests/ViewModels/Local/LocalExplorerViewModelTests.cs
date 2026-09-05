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

    /// <summary>
    /// The free-space line used to be a XAML StringFormat of "{0} free" — English that survived
    /// the Spanish-only round because a format string inside a binding does not read as a literal
    /// (docs/PLAN-I18N.md §5). It is a view-model property now, so it is testable at all.
    /// </summary>
    [Fact]
    public async Task FreeSpaceLabel_RendersThroughTheStringTable_AndFollowsFreeSpaceText()
    {
        var sut = Build(_root);
        await sut.NavigateAsync(_root);

        Assert.Contains(sut.FreeSpaceText, sut.FreeSpaceLabel, StringComparison.Ordinal);
        Assert.NotEqual(sut.FreeSpaceText, sut.FreeSpaceLabel);
    }

    /// <summary>The label is derived, so it has to be announced alongside the value it derives from.</summary>
    [Fact]
    public async Task FreeSpaceLabel_IsAnnouncedWheneverFreeSpaceTextChanges()
    {
        var service = new ChangingFreeSpaceService(_root, 1_000, 2_000);
        var sut = new LocalExplorerViewModel(service, new AppSettingsService());
        await sut.NavigateAsync(_root);

        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await sut.NavigateAsync(_root);

        Assert.Contains(nameof(sut.FreeSpaceText), raised);
        Assert.Contains(nameof(sut.FreeSpaceLabel), raised);
    }

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
    public async Task SearchText_NarrowsToMatchingNames_CaseInsensitively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Photos"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);

        sut.SearchText = "PHOTO";

        Assert.Equal("Photos", Assert.Single(sut.Items).DisplayName);
    }

    [Fact]
    public async Task ClearingSearchText_RestoresEveryItem()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Photos"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        sut.SearchText = "Photos";

        sut.SearchText = "";

        Assert.Equal(2, sut.Items.Count);
    }

    [Fact]
    public async Task NavigatingToTheNextFolder_DropsTheSearchText()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        sut.SearchText = "notes";

        await sut.NavigateAsync(sub);

        Assert.Equal(string.Empty, sut.SearchText);
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

    // ---------------------------------------------------------------- multi-select (docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2)

    /// <summary>Four files, alphabetically named so the default name sort leaves them in this exact order — folders sort first (see <see cref="NavigateAsync_ListsAndSortsWithFoldersFirst"/>), so mixing one in would make the indices these tests depend on unpredictable.</summary>
    private async Task<LocalExplorerViewModel> LoadFourFilesAsync()
    {
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt", "d.txt" })
        {
            File.WriteAllText(Path.Combine(_root, name), "x");
        }

        var sut = Build(_root);
        await sut.NavigateAsync(_root);
        return sut;
    }

    private static LocalNodeViewModel Row(LocalExplorerViewModel sut, string name)
        => sut.Items.Single(i => i.DisplayName == name);

    [Fact]
    public async Task ToggleSelection_AddsARowWithoutClearingOthers()
    {
        var sut = await LoadFourFilesAsync();

        sut.ToggleSelection(Row(sut, "a.txt"));
        sut.ToggleSelection(Row(sut, "d.txt"));

        Assert.Equal(2, sut.SelectedCount);
        Assert.True(Row(sut, "a.txt").IsSelected);
        Assert.True(Row(sut, "d.txt").IsSelected);
        Assert.False(Row(sut, "b.txt").IsSelected);
    }

    [Fact]
    public async Task ToggleSelection_TwiceOnTheSameRow_DeselectsIt()
    {
        var sut = await LoadFourFilesAsync();
        var row = Row(sut, "a.txt");

        sut.ToggleSelection(row);
        sut.ToggleSelection(row);

        Assert.Equal(0, sut.SelectedCount);
        Assert.False(row.IsSelected);
    }

    [Fact]
    public async Task SelectRange_SelectsEveryRowBetweenTheAnchorAndTheTarget_Inclusive()
    {
        var sut = await LoadFourFilesAsync();
        sut.ToggleSelection(Row(sut, "a.txt")); // anchor = a.txt (index 0)

        sut.SelectRange(Row(sut, "d.txt")); // index 3

        Assert.Equal(4, sut.SelectedCount);
        Assert.All(sut.Items, node => Assert.True(node.IsSelected));
    }

    [Fact]
    public async Task SelectAllCommand_SelectsEveryRow()
    {
        var sut = await LoadFourFilesAsync();

        await sut.SelectAllCommand.ExecuteAsync();

        Assert.Equal(4, sut.SelectedCount);
    }

    [Fact]
    public async Task APlainClick_ResetsAMultiSelectionDownToOneRow()
    {
        var sut = await LoadFourFilesAsync();
        await sut.SelectAllCommand.ExecuteAsync();

        await Row(sut, "b.txt").RowCommand.ExecuteAsync();

        Assert.Equal(1, sut.SelectedCount);
        Assert.True(Row(sut, "b.txt").IsSelected);
    }

    [Fact]
    public async Task NavigatingAway_ClearsTheSelection()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        var sut = await LoadFourFilesAsync();
        await sut.SelectAllCommand.ExecuteAsync();

        await sut.NavigateAsync(sub);

        Assert.Equal(0, sut.SelectedCount);
    }

    [Fact]
    public async Task DeleteSelectedCommand_DeletesEverySelectedItem_AfterOneConfirmation()
    {
        var sut = await LoadFourFilesAsync();
        var asked = new List<string>();
        sut.RequestConfirmationAsync = question =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        await sut.SelectAllCommand.ExecuteAsync();

        await sut.DeleteSelectedCommand.ExecuteAsync();

        Assert.Single(asked);
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "d.txt")));
        Assert.Empty(sut.Items);
    }

    [Fact]
    public async Task DeleteSelectedCommand_WhenDeclined_DeletesNothing()
    {
        var sut = await LoadFourFilesAsync();
        sut.RequestConfirmationAsync = _ => Task.FromResult(false);
        await sut.SelectAllCommand.ExecuteAsync();

        await sut.DeleteSelectedCommand.ExecuteAsync();

        Assert.True(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.Equal(4, sut.Items.Count);
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
        Assert.Contains(shownFields!, f => f.Label == "Nombre" && f.Value == "note.txt");
        Assert.Contains(shownFields!, f => f.Label == "Tipo" && f.Value == "Archivo");
        Assert.Contains(shownFields!, f => f.Label == "Tamaño");
    }

    /// <summary>Points <see cref="LocalFileSystemService.GetHomeDirectory"/> at a temp folder rather than the real OS home, so tests stay hermetic.</summary>
    private sealed class FakeHomeLocalFileSystemService(string home) : LocalFileSystemService
    {
        public override string GetHomeDirectory() => home;
    }

    /// <summary>Reports a different amount of free space on each call, so the derived label has
    /// something to actually change in response to.</summary>
    private sealed class ChangingFreeSpaceService(string home, params long[] freeBytes) : LocalFileSystemService
    {
        private int _call;

        public override string GetHomeDirectory() => home;

        public override long? AvailableFreeBytes(string path)
            => freeBytes[Math.Min(_call++, freeBytes.Length - 1)];
    }
}
