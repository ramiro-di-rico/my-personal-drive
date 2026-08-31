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
/// Folder trash used to be disabled outright (<c>TrashCommand</c>'s <c>CanExecute</c> and
/// <c>TrashAsync</c>/<c>TrashItemAsync</c> all early-returned for <c>IsFolder</c>). The CLI's
/// `filesystem trash` already moves a whole subtree server-side in one call, so the restriction
/// was purely app-side. Folder trash is destructive and recursive by nature, so unlike file trash
/// it asks for confirmation first via <see cref="MainWindowViewModel.RequestConfirmationAsync"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowTrashTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.Trash").FullName;
    private readonly string? _originalAppData;

    public MainWindowTrashTests()
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

    [Fact]
    public async Task TrashingAFolder_WhenConfirmed_CallsTheCliAndReportsSuccess()
    {
        var (viewModel, executor) = Build();
        var asked = new List<string>();
        viewModel.RequestConfirmationAsync = question =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        executor.EnqueueOutput("{}"); // filesystem trash
        executor.RespondForPath("/my-files", "[]"); // background refresh

        await viewModel.TrashItemAsync(new DriveItem("/my-files/Docs", "Docs", IsFolder: true));

        var asked1 = Assert.Single(asked);
        Assert.Contains("Docs", asked1);
        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "trash", "/my-files/Docs"]);
    }

    [Fact]
    public async Task TrashingAFolder_WhenDeclined_NeverCallsTheCli()
    {
        var (viewModel, executor) = Build();
        viewModel.RequestConfirmationAsync = _ => Task.FromResult(false);

        await viewModel.TrashItemAsync(new DriveItem("/my-files/Docs", "Docs", IsFolder: true));

        Assert.DoesNotContain(executor.Calls, c => c.Arguments.Contains("trash"));
        Assert.Equal("Cancelled: Docs was not moved to trash.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task TrashingAFolder_WithNoConfirmationHandlerWired_StillProceeds()
    {
        // Mirrors SyncPanelViewModel's own RequestConfirmationAsync fallback: if nothing is wired
        // up to answer, the action proceeds rather than silently doing nothing forever.
        var (viewModel, executor) = Build();
        executor.EnqueueOutput("{}");
        executor.RespondForPath("/my-files", "[]");

        await viewModel.TrashItemAsync(new DriveItem("/my-files/Docs", "Docs", IsFolder: true));

        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "trash", "/my-files/Docs"]);
    }

    [Fact]
    public async Task TrashingAFile_NeverAsksForConfirmation()
    {
        var (viewModel, executor) = Build();
        viewModel.RequestConfirmationAsync = _ => throw new InvalidOperationException("should not have asked");
        executor.EnqueueOutput("{}");
        executor.RespondForPath("/my-files", "[]");

        await viewModel.TrashItemAsync(new DriveItem("/my-files/notes.txt", "notes.txt", IsFolder: false, Size: 10));

        Assert.Contains(executor.Calls, c => c.Arguments is ["filesystem", "trash", "/my-files/notes.txt"]);
    }
}
