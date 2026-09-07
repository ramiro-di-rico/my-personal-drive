using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// Batch selection over the cloud pane's listing (docs/INTERFACE_IMPROVEMENT_PLAN.md §2.2):
/// Ctrl/Cmd+Click toggles one row, Shift+Click selects a contiguous range from the last-touched
/// row (the "anchor"), Ctrl/Cmd+A selects everything, and a plain click always resets back down to
/// one row — the same rules real file managers use. <c>MainWindowViewModel.ToggleSelection</c>/
/// <c>SelectRange</c>/<c>SelectAllRowsCommand</c> are what the view's Ctrl/Shift-click pointer
/// handlers and Ctrl+A key handler actually call; this exercises them directly, the same way
/// <see cref="MainWindowKindFilterTests"/> drives filtering without a real ListBox.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowMultiSelectTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.MultiSelect").FullName;
    private readonly string? _originalAppData;

    public MainWindowMultiSelectTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DriveItem Item(string name, bool isFolder = false, long size = 100)
        => new($"/my-files/{name}", name, IsFolder: isFolder, Size: isFolder ? null : size);

    private (MainWindowViewModel ViewModel, FakeCliExecutor Executor) Build()
    {
        var executor = new FakeCliExecutor();
        var service = new ProtonDriveService(executor);
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(Path.Combine(_tempAppData, "sync.db"));
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        var viewModel = new MainWindowViewModel(provider, new DriveCacheService(Path.Combine(_tempAppData, "cache.db")), new AppSettingsService(), panel);
        return (viewModel, executor);
    }

    /// <summary>
    /// Four files, alphabetically named so the default name sort leaves them in this exact order —
    /// the range-selection tests below depend on knowing each row's index.
    /// </summary>
    private MainWindowViewModel LoadFourRows()
    {
        var (viewModel, _) = Build();
        viewModel.DisplayItems([Item("a.txt"), Item("b.txt"), Item("c.txt"), Item("d.txt")]);
        return viewModel;
    }

    private static DriveNodeViewModel Row(MainWindowViewModel viewModel, string name)
        => viewModel.RootItems.Single(n => n.DisplayName == name);

    [Fact]
    public void ToggleSelection_AddsARowWithoutClearingOthers()
    {
        var viewModel = LoadFourRows();

        viewModel.ToggleSelection(Row(viewModel, "a.txt"));
        viewModel.ToggleSelection(Row(viewModel, "d.txt"));

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.True(Row(viewModel, "a.txt").IsSelected);
        Assert.True(Row(viewModel, "d.txt").IsSelected);
        Assert.False(Row(viewModel, "b.txt").IsSelected);
    }

    [Fact]
    public void ToggleSelection_TwiceOnTheSameRow_DeselectsIt()
    {
        var viewModel = LoadFourRows();
        var row = Row(viewModel, "a.txt");

        viewModel.ToggleSelection(row);
        viewModel.ToggleSelection(row);

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(row.IsSelected);
    }

    [Fact]
    public void SelectRange_SelectsEveryRowBetweenTheAnchorAndTheTarget_Inclusive()
    {
        var viewModel = LoadFourRows();
        viewModel.ToggleSelection(Row(viewModel, "a.txt")); // anchor = a.txt (index 0)

        viewModel.SelectRange(Row(viewModel, "d.txt")); // index 3

        Assert.Equal(4, viewModel.SelectedCount);
        Assert.All(viewModel.RootItems, node => Assert.True(node.IsSelected));
    }

    [Fact]
    public void SelectRange_ReplacesWhateverWasSelectedBefore()
    {
        var viewModel = LoadFourRows();
        viewModel.ToggleSelection(Row(viewModel, "d.txt")); // selected but not the anchor for the range below
        viewModel.ToggleSelection(Row(viewModel, "a.txt")); // anchor = a.txt (index 0), d.txt still selected

        viewModel.SelectRange(Row(viewModel, "b.txt")); // range a.txt..b.txt (indices 0..1)

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.True(Row(viewModel, "a.txt").IsSelected);
        Assert.True(Row(viewModel, "b.txt").IsSelected);
        Assert.False(Row(viewModel, "d.txt").IsSelected); // outside the range — no longer selected
    }

    [Fact]
    public void SelectRange_WithNoAnchorYet_JustSelectsTheTarget()
    {
        var viewModel = LoadFourRows();

        viewModel.SelectRange(Row(viewModel, "b.txt"));

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(Row(viewModel, "b.txt").IsSelected);
    }

    [Fact]
    public async Task SelectAllRowsCommand_SelectsEveryRow()
    {
        var viewModel = LoadFourRows();

        await viewModel.SelectAllRowsCommand.ExecuteAsync();

        Assert.Equal(4, viewModel.SelectedCount);
    }

    [Fact]
    public async Task APlainClick_ResetsAMultiSelectionDownToOneRow()
    {
        var viewModel = LoadFourRows();
        await viewModel.SelectAllRowsCommand.ExecuteAsync();

        await Row(viewModel, "b.txt").SelectCommand.ExecuteAsync();

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(Row(viewModel, "b.txt").IsSelected);
    }

    [Fact]
    public void SelectedCountProperties_ReflectHowManyRowsAreMarked()
    {
        var viewModel = LoadFourRows();
        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HasMultipleSelected);
        Assert.False(viewModel.IsSingleSelected);

        viewModel.ToggleSelection(Row(viewModel, "a.txt"));
        Assert.True(viewModel.IsSingleSelected);
        Assert.False(viewModel.HasMultipleSelected);

        viewModel.ToggleSelection(Row(viewModel, "b.txt"));
        Assert.False(viewModel.IsSingleSelected);
        Assert.True(viewModel.HasMultipleSelected);
        Assert.Contains("2", viewModel.SelectionSummaryText);
    }

    [Fact]
    public async Task DownloadSelectedCommand_DownloadsOnlyTheSelectedFiles_SkippingFolders()
    {
        var (viewModel, executor) = Build();
        viewModel.DisplayItems([Item("a.txt"), Item("b.txt"), Item("Folder", isFolder: true)]);
        viewModel.RequestDownloadFolderAsync = () => Task.FromResult<string?>("/home/user/Downloads");
        await viewModel.SelectAllRowsCommand.ExecuteAsync();
        executor.EnqueueOutput("{}");
        executor.EnqueueOutput("{}");

        await viewModel.DownloadSelectedCommand.ExecuteAsync();

        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "download", "/my-files/a.txt", "/home/user/Downloads"]);
        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "download", "/my-files/b.txt", "/home/user/Downloads"]);
        Assert.DoesNotContain(executor.Calls, c => c.Arguments.Contains("/my-files/Folder"));
    }

    [Fact]
    public async Task DownloadSelectedCommand_WithOnlyFoldersSelected_WarnsInsteadOfCallingTheCli()
    {
        var (viewModel, executor) = Build();
        viewModel.DisplayItems([Item("Folder", isFolder: true)]);
        viewModel.RequestDownloadFolderAsync = () => throw new InvalidOperationException("should not have been asked");
        await viewModel.SelectAllRowsCommand.ExecuteAsync();

        await viewModel.DownloadSelectedCommand.ExecuteAsync();

        Assert.DoesNotContain(executor.Calls, c => c.Arguments.Contains("download"));
        Assert.True(viewModel.IsWarning);
    }

    [Fact]
    public async Task TrashSelectedCommand_AsksOnceWhenTheSelectionIncludesAFolder_ThenTrashesEveryItem()
    {
        var (viewModel, executor) = Build();
        viewModel.DisplayItems([Item("a.txt"), Item("Folder", isFolder: true)]);
        var asked = new List<string>();
        viewModel.RequestConfirmationAsync = question =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        await viewModel.SelectAllRowsCommand.ExecuteAsync();
        executor.EnqueueOutput("{}");
        executor.EnqueueOutput("{}");
        executor.RespondForPath("/my-files", "[]"); // background refresh

        await viewModel.TrashSelectedCommand.ExecuteAsync();

        Assert.Single(asked);
        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "trash", "/my-files/a.txt"]);
        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "trash", "/my-files/Folder"]);
    }

    [Fact]
    public async Task TrashSelectedCommand_NeverAsksWhenOnlyFilesAreSelected()
    {
        var (viewModel, executor) = Build();
        viewModel.DisplayItems([Item("a.txt"), Item("b.txt")]);
        viewModel.RequestConfirmationAsync = _ => throw new InvalidOperationException("should not have asked");
        await viewModel.SelectAllRowsCommand.ExecuteAsync();
        executor.EnqueueOutput("{}");
        executor.EnqueueOutput("{}");
        executor.RespondForPath("/my-files", "[]");

        await viewModel.TrashSelectedCommand.ExecuteAsync();

        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "trash", "/my-files/a.txt"]);
        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "trash", "/my-files/b.txt"]);
    }

    [Fact]
    public async Task TrashSelectedCommand_WhenDeclined_NeverCallsTheCli()
    {
        var (viewModel, executor) = Build();
        viewModel.DisplayItems([Item("a.txt"), Item("Folder", isFolder: true)]);
        viewModel.RequestConfirmationAsync = _ => Task.FromResult(false);
        await viewModel.SelectAllRowsCommand.ExecuteAsync();

        await viewModel.TrashSelectedCommand.ExecuteAsync();

        Assert.DoesNotContain(executor.Calls, c => c.Arguments.Contains("trash"));
    }

    [Fact]
    public void RefreshingTheListing_PreservesAMultiSelection_ForRowsThatStillExist()
    {
        var viewModel = LoadFourRows();
        viewModel.ToggleSelection(Row(viewModel, "a.txt"));
        viewModel.ToggleSelection(Row(viewModel, "d.txt"));

        // Re-displaying the same items (a plain refresh, not a folder change) rebuilds every row's
        // view-model — the selection has to be carried forward by path, not by reference.
        viewModel.DisplayItems([Item("a.txt"), Item("b.txt"), Item("c.txt"), Item("d.txt")]);

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.True(Row(viewModel, "a.txt").IsSelected);
        Assert.True(Row(viewModel, "d.txt").IsSelected);
    }
}
