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
/// docs/PLAN-UX-ROUND-3.md X2. A plain click used to both select and open, which is why the icons
/// and gallery modes could not select at all: their only gesture already meant "navigate", so the
/// batch action bar and the details panel were unreachable from two of the three view modes.
///
/// Click and open are two commands now — <c>SelectCommand</c> and <c>ActivateCommand</c> — and the
/// view binds the first to a click and the second to a double click, in every mode. These tests pin
/// the split at the view-model boundary; which container carries which gesture is the view's half,
/// verified by hand.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowRowActivationTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.RowActivation").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-activation-{Guid.NewGuid():N}.db");

    public MainWindowRowActivationTests()
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

    /// <summary>
    /// Answers any text preview with the same content. Activation only needs the viewer to open;
    /// what it renders is <see cref="MainWindowTextViewerTests"/>'s subject, not this file's.
    /// </summary>
    private sealed class StubTextLoader : ITextFilePreviewLoader
    {
        public Task<TextFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default)
            => Task.FromResult(new TextFilePreview(item.Path, item.Name, "hello\n", 1, 6, IsTruncated: false, IsBinary: false, "UTF-8"));
    }

    private MainWindowViewModel Build()
    {
        new AppSettingsService().Save(new AppSettings { CliPath = "/usr/bin/proton-drive", IsAuthenticated = true });

        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));
        var syncStore = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, syncStore, new LocalScanner(), new RemoteScanner(provider));

        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            new SyncPanelViewModel(syncStore, syncExecutor, new SyncCrashRecovery(syncStore)),
            previewLoader: new StubTextLoader());
    }

    private static DriveNodeViewModel Row(MainWindowViewModel viewModel, string name)
        => viewModel.RootItems.Single(node => node.DisplayName == name);

    private static MainWindowViewModel LoadAFolderAndAFile(MainWindowViewModel viewModel)
    {
        viewModel.DisplayItems([
            new DriveItem("/my-files/Photos", "Photos", IsFolder: true),
            new DriveItem("/my-files/notes.txt", "notes.txt", IsFolder: false, Size: 12),
        ]);
        return viewModel;
    }

    [Fact]
    public async Task ClickingAFolder_SelectsIt_WithoutNavigatingIntoIt()
    {
        var viewModel = LoadAFolderAndAFile(Build());
        var before = viewModel.CurrentPath;

        await Row(viewModel, "Photos").SelectCommand.ExecuteAsync();

        Assert.True(Row(viewModel, "Photos").IsSelected);
        // The whole point: in the tile modes this was the only gesture available, so a folder could
        // never be selected — clicking it navigated instead.
        Assert.Equal(before, viewModel.CurrentPath);
    }

    [Fact]
    public async Task ClickingARow_LeavesTheSelectionAtOne()
    {
        var viewModel = LoadAFolderAndAFile(Build());
        await viewModel.SelectAllRowsCommand.ExecuteAsync();

        await Row(viewModel, "notes.txt").SelectCommand.ExecuteAsync();

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(viewModel.IsSingleSelected);
    }

    [Fact]
    public async Task SelectingARow_FillsTheDetailsPanel()
    {
        var viewModel = LoadAFolderAndAFile(Build());

        await Row(viewModel, "notes.txt").SelectCommand.ExecuteAsync();

        // Reachable from the tile modes now, where nothing could set a selection before.
        Assert.Equal("notes.txt", viewModel.SelectedName);
        Assert.Equal("/my-files/notes.txt", viewModel.SelectedPath);
    }

    /// <summary>
    /// This test used to assert that activating a file navigates nowhere, and stopped there — which
    /// described the defect rather than the intent: double-clicking a previewable file did nothing
    /// at all, while X2's plan and commit message both said it previewed (docs/PLAN-UX-ROUND-4.md
    /// Y1). A test that pins a gap as if it were a decision is worse than no test.
    /// </summary>
    [Fact]
    public async Task ActivatingAPreviewableFile_OpensTheViewer()
    {
        var viewModel = LoadAFolderAndAFile(Build());
        var before = viewModel.CurrentPath;

        await Row(viewModel, "notes.txt").ActivateCommand.ExecuteAsync();

        Assert.True(Row(viewModel, "notes.txt").IsSelected);
        Assert.Equal(before, viewModel.CurrentPath);
        Assert.True(viewModel.Preview.IsViewerVisible);
    }

    /// <summary>A file the app cannot show still just selects — there is nothing else to do with it.</summary>
    [Fact]
    public async Task ActivatingAFileWithNoPreview_JustSelectsIt()
    {
        var viewModel = Build();
        viewModel.DisplayItems([new DriveItem("/my-files/clip.webm", "clip.webm", IsFolder: false, Size: 10)]);

        await Row(viewModel, "clip.webm").ActivateCommand.ExecuteAsync();

        Assert.True(Row(viewModel, "clip.webm").IsSelected);
        Assert.False(viewModel.Preview.IsViewerVisible);
    }
}
