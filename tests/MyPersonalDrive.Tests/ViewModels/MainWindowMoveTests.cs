using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// "Move to..." exists so relocating something inside one provider costs one server-side call
/// instead of a download plus an upload. These tests pin that down where it matters: the call the
/// provider actually receives (<c>filesystem move</c>, never a download/upload pair), the two
/// refusals made locally, and the failure path.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowMoveTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.Move").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-move-{Guid.NewGuid():N}.db");

    public MainWindowMoveTests()
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
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private (MainWindowViewModel ViewModel, FakeCliExecutor Cli) Build()
    {
        var cli = new FakeCliExecutor();
        var provider = new ProtonDriveProvider(new ProtonDriveService(cli));
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        var viewModel = new MainWindowViewModel(provider, new DriveCacheService(Path.Combine(_tempAppData, "cache.db")), new AppSettingsService(), panel);
        return (viewModel, cli);
    }

    private static DriveItem FileItem(string path) => new(path, path[(path.LastIndexOf('/') + 1)..], IsFolder: false, Size: 10);

    private static DriveItem Folder(string path) => new(path, path[(path.LastIndexOf('/') + 1)..], IsFolder: true);

    /// <summary>The whole point: one `filesystem move`, and nothing that transfers bytes.</summary>
    [Fact]
    public async Task MovingAFile_IssuesOneServerSideMove_AndNoDownloadOrUpload()
    {
        var (viewModel, cli) = Build();
        viewModel.RequestMoveTargetAsync = (_, _) => Task.FromResult<string?>("/my-files/archive");
        cli.EnqueueOutput(string.Empty);
        // The background refresh a successful move kicks off, so it has an answer waiting rather
        // than racing the assertions below. Its own status text is why this test asserts on the
        // calls and not on StatusMessage — the refusal tests below own that, and never refresh.
        cli.EnqueueOutput("[]");

        await viewModel.MoveItemAsync(FileItem("/my-files/report.pdf"));

        var move = Assert.Single(cli.Calls, c => c.Arguments.Contains("move"));
        Assert.Equal(["filesystem", "move", "/my-files/report.pdf", "/my-files/archive"], move.Arguments);
        Assert.DoesNotContain(cli.Calls, c => c.Arguments.Contains("download") || c.Arguments.Contains("upload"));
    }

    /// <summary>The picker's start path is the account's root, so the user can reach any folder rather than only ones below where they happen to be standing.</summary>
    [Fact]
    public async Task ThePickerStartsAtTheAccountRoot_AndIsToldWhatIsBeingMoved()
    {
        var (viewModel, cli) = Build();
        string? what = null;
        string? startPath = null;
        viewModel.RequestMoveTargetAsync = (w, s) => { what = w; startPath = s; return Task.FromResult<string?>("/my-files/archive"); };
        cli.EnqueueOutput(string.Empty);
        cli.EnqueueOutput("[]");

        await viewModel.MoveItemAsync(FileItem("/my-files/report.pdf"));

        Assert.Equal("report.pdf", what);
        Assert.Equal(viewModel.RootPath, startPath);
    }

    [Fact]
    public async Task CancellingThePicker_CallsNothing()
    {
        var (viewModel, cli) = Build();
        viewModel.RequestMoveTargetAsync = (_, _) => Task.FromResult<string?>(null);

        await viewModel.MoveItemAsync(FileItem("/my-files/report.pdf"));

        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task WithNoPickerWired_WarnsInsteadOfMoving()
    {
        var (viewModel, cli) = Build();

        await viewModel.MoveItemAsync(FileItem("/my-files/report.pdf"));

        Assert.Empty(cli.Calls);
        Assert.True(viewModel.IsWarning);
    }

    /// <summary>Both refusals are made locally: each would otherwise cost a round trip and an error card to learn.</summary>
    [Fact]
    public async Task MovingIntoTheFolderItIsAlreadyIn_IsRefusedWithoutCallingTheProvider()
    {
        var (viewModel, cli) = Build();
        viewModel.RequestMoveTargetAsync = (_, _) => Task.FromResult<string?>("/my-files");

        await viewModel.MoveItemAsync(FileItem("/my-files/report.pdf"));

        Assert.Empty(cli.Calls);
        Assert.True(viewModel.IsWarning);
        Assert.Contains("already", viewModel.StatusMessage);
    }

    [Fact]
    public async Task MovingAFolderIntoItsOwnSubtree_IsRefusedWithoutCallingTheProvider()
    {
        var (viewModel, cli) = Build();
        viewModel.RequestMoveTargetAsync = (_, _) => Task.FromResult<string?>("/my-files/photos/2024");

        await viewModel.MoveItemAsync(Folder("/my-files/photos"));

        Assert.Empty(cli.Calls);
        Assert.True(viewModel.IsWarning);
        Assert.Contains("itself", viewModel.StatusMessage);
    }

    /// <summary>A sibling whose path merely starts with the same characters ("/my-files/photos-old") is not inside it.</summary>
    [Fact]
    public async Task ASiblingWithASharedNamePrefix_IsNotTreatedAsItsOwnSubtree()
    {
        var (viewModel, cli) = Build();
        viewModel.RequestMoveTargetAsync = (_, _) => Task.FromResult<string?>("/my-files/photos-old");
        cli.EnqueueOutput(string.Empty);

        cli.EnqueueOutput("[]");

        await viewModel.MoveItemAsync(Folder("/my-files/photos"));

        Assert.Single(cli.Calls, c => c.Arguments.Contains("move"));
    }

    [Fact]
    public async Task WhenTheProviderRefuses_TheFailureIsSurfacedInsteadOfThrowing()
    {
        var (viewModel, cli) = Build();
        viewModel.RequestMoveTargetAsync = (_, _) => Task.FromResult<string?>("/my-files/archive");
        cli.EnqueueFailure(new DriveException("filesystem move", 1, "", "permission denied", "permission denied", DriveErrorKind.PermissionDenied));

        await viewModel.MoveItemAsync(FileItem("/my-files/report.pdf"));

        Assert.True(viewModel.IsWarning);
        Assert.False(viewModel.IsLoading);
    }

    /// <summary>The multi-select bar's counterpart: one call carrying every selected path, not one call per row.</summary>
    [Fact]
    public async Task MovingASelection_SendsEveryPathInASingleCall()
    {
        var (viewModel, cli) = Build();
        viewModel.RequestMoveTargetAsync = (_, _) => Task.FromResult<string?>("/my-files/archive");
        viewModel.DisplayItems([FileItem("/my-files/a.txt"), FileItem("/my-files/b.txt")]);
        viewModel.ToggleSelection(viewModel.RootItems[0]);
        viewModel.ToggleSelection(viewModel.RootItems[1]);
        cli.EnqueueOutput(string.Empty);
        cli.EnqueueOutput("[]");

        await viewModel.MoveSelectedCommand.ExecuteAsync();

        var move = Assert.Single(cli.Calls, c => c.Arguments.Contains("move"));
        Assert.Equal(["filesystem", "move", "/my-files/a.txt", "/my-files/b.txt", "/my-files/archive"], move.Arguments);
    }

    /// <summary>The row-level gate a right-click actually goes through, mirroring "Share Link"'s.</summary>
    [Fact]
    public void DriveNodeViewModel_CanMove_ReflectsTheProviderCapability()
    {
        var supported = new DriveNodeViewModel(
            FileItem("/my-files/report.pdf"),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            syncActions: new DriveNodeSyncActions { SupportsMove = true, MoveItemAsync = _ => Task.CompletedTask });
        var unsupported = new DriveNodeViewModel(
            FileItem("/my-files/report.pdf"),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            syncActions: new DriveNodeSyncActions { SupportsMove = false });

        Assert.True(supported.CanMove);
        Assert.True(supported.MoveCommand.CanExecute(null));

        Assert.False(unsupported.CanMove);
        Assert.False(unsupported.MoveCommand.CanExecute(null));
        Assert.Contains("not available", unsupported.MoveTooltip);
    }

    [Fact]
    public void EveryProviderShippedTodayCanMoveServerSide()
    {
        var (viewModel, _) = Build();

        Assert.True(viewModel.CanMoveItems);
    }
}
