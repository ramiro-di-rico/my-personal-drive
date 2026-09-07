using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;
using MyPersonalDrive.Tests;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// The in-app text viewer panel: opening it from a row or from the menu button, the loading state
/// while the (fake) download runs, and the failure and refusal paths. The real download-and-read
/// dance is <see cref="Services.TextFilePreviewServiceTests"/>'s job; this exercises only what the
/// view model does with whatever the loader hands back.
///
/// XDG_CONFIG_HOME is redirected for the reason described in <see cref="AppDataCollection"/>.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowTextViewerTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.TextViewer").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-text-viewer-{Guid.NewGuid():N}.db");

    public MainWindowTextViewerTests()
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

    private sealed class FakeLoader : ITextFilePreviewLoader
    {
        private readonly Func<DriveItem, Task<TextFilePreview>> _respond;

        public FakeLoader(Func<DriveItem, Task<TextFilePreview>> respond) => _respond = respond;

        public List<DriveItem> Requests { get; } = [];

        public Task<TextFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default)
        {
            Requests.Add(item);
            return _respond(item);
        }
    }

    private sealed class FakeImageLoader : IImageFilePreviewLoader
    {
        private readonly Func<DriveItem, Task<ImageFilePreview>> _respond;

        public FakeImageLoader(Func<DriveItem, Task<ImageFilePreview>> respond) => _respond = respond;

        public List<DriveItem> Requests { get; } = [];

        public Task<ImageFilePreview> LoadAsync(DriveItem item, CancellationToken cancellationToken = default)
        {
            Requests.Add(item);
            return _respond(item);
        }
    }

    private MainWindowViewModel Build(ITextFilePreviewLoader? loader, IImageFilePreviewLoader? imageLoader = null)
    {
        var service = new ProtonDriveService(new FakeCliExecutor());
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        return new MainWindowViewModel(
            provider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel,
            previewLoader: loader,
            imagePreviewLoader: imageLoader);
    }

    private static DriveItem TextItem(string name = "notes.txt") => new($"/my-files/{name}", name, IsFolder: false, Size: 32);

    private static DriveItem ImageItem(string name = "photo.jpg") => new($"/my-files/{name}", name, IsFolder: false, Size: 32);

    [Fact]
    public async Task PreviewItemAsync_OpensThePanelWithTheLoadedText()
    {
        var loader = new FakeLoader(item => Task.FromResult(
            new TextFilePreview(item.Path, item.Name, "hola\nmundo\n", 2, 11, IsTruncated: false, IsBinary: false, "UTF-8")));
        var sut = Build(loader);

        await sut.Preview.PreviewItemAsync(TextItem());

        Assert.True(sut.Preview.IsViewerVisible);
        Assert.False(sut.Preview.IsViewerLoading);
        Assert.Equal("notes.txt", sut.Preview.ViewerTitle);
        Assert.Equal("/my-files/notes.txt", sut.Preview.ViewerPath);
        Assert.Equal("hola\nmundo\n", sut.Preview.ViewerText);
        Assert.Contains("2", sut.Preview.ViewerNote);
        Assert.False(sut.IsWarning);
    }

    [Fact]
    public async Task PreviewItemAsync_OnABinaryFile_ShowsNoTextAndWarns()
    {
        var loader = new FakeLoader(item => Task.FromResult(
            new TextFilePreview(item.Path, item.Name, string.Empty, 0, 4096, IsTruncated: false, IsBinary: true, "binary")));
        var sut = Build(loader);

        await sut.Preview.PreviewItemAsync(TextItem("mystery"));

        Assert.True(sut.Preview.IsViewerVisible);
        Assert.Equal(string.Empty, sut.Preview.ViewerText);
        Assert.False(sut.Preview.HasViewerText);
        Assert.True(sut.IsWarning);
    }

    [Fact]
    public async Task PreviewItemAsync_WhenTheLoaderThrows_SurfacesTheErrorInsteadOfThrowing()
    {
        var loader = new FakeLoader(_ => throw new InvalidOperationException("boom"));
        var sut = Build(loader);

        await sut.Preview.PreviewItemAsync(TextItem());

        Assert.True(sut.IsWarning);
        Assert.Contains("boom", sut.StatusMessage);
    }

    [Fact]
    public async Task PreviewItemAsync_RefusesAFileThePolicyExcludes()
    {
        var loader = new FakeLoader(_ => throw new InvalidOperationException("should not be called"));
        var sut = Build(loader);

        await sut.Preview.PreviewItemAsync(new DriveItem("/my-files/movie.mp4", "movie.mp4", IsFolder: false, Size: 32));

        Assert.False(sut.Preview.IsViewerVisible);
        Assert.Empty(loader.Requests);
        Assert.True(sut.IsWarning);
    }

    [Fact]
    public async Task PreviewItemAsync_WithNoLoaderConfigured_DegradesInsteadOfThrowing()
    {
        var sut = Build(loader: null);

        await sut.Preview.PreviewItemAsync(TextItem());

        Assert.False(sut.Preview.IsViewerVisible);
        Assert.True(sut.IsWarning);
    }

    [Fact]
    public async Task CloseViewerCommand_HidesThePanelAndClearsTheText()
    {
        var loader = new FakeLoader(item => Task.FromResult(
            new TextFilePreview(item.Path, item.Name, "content", 1, 7, IsTruncated: false, IsBinary: false, "UTF-8")));
        var sut = Build(loader);
        await sut.Preview.PreviewItemAsync(TextItem());

        await sut.Preview.CloseViewerCommand.ExecuteAsync();

        Assert.False(sut.Preview.IsViewerVisible);
        Assert.Equal(string.Empty, sut.Preview.ViewerText);
    }

    [Fact]
    public void ViewSelectedFileCommand_WithNoSelection_CannotExecute()
    {
        var loader = new FakeLoader(item => Task.FromResult(
            new TextFilePreview(item.Path, item.Name, "x", 1, 1, IsTruncated: false, IsBinary: false, "UTF-8")));
        var sut = Build(loader);

        Assert.False(sut.ViewSelectedFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task PreviewItemAsync_OnAnImage_RoutesToTheImageLoaderAndFillsTheImagePanel()
    {
        byte[] bytes = [1, 2, 3, 4];
        var textLoader = new FakeLoader(_ => throw new InvalidOperationException("should not be called for an image"));
        var imageLoader = new FakeImageLoader(item => Task.FromResult(new ImageFilePreview(item.Path, item.Name, bytes, bytes.Length)));
        var sut = Build(textLoader, imageLoader);

        await sut.Preview.PreviewItemAsync(ImageItem());

        Assert.True(sut.Preview.IsViewerVisible);
        Assert.False(sut.Preview.IsViewerLoading);
        Assert.Equal(bytes, sut.Preview.ViewerImageBytes);
        Assert.True(sut.Preview.HasViewerImage);
        Assert.False(sut.Preview.HasViewerText);
        Assert.Empty(textLoader.Requests);
        Assert.False(sut.IsWarning);
    }

    [Fact]
    public async Task PreviewItemAsync_SwitchingFromImageToText_ClearsThePreviousImage()
    {
        byte[] bytes = [9, 9, 9];
        var textLoader = new FakeLoader(item => Task.FromResult(
            new TextFilePreview(item.Path, item.Name, "hola", 1, 4, IsTruncated: false, IsBinary: false, "UTF-8")));
        var imageLoader = new FakeImageLoader(item => Task.FromResult(new ImageFilePreview(item.Path, item.Name, bytes, bytes.Length)));
        var sut = Build(textLoader, imageLoader);

        await sut.Preview.PreviewItemAsync(ImageItem());
        Assert.True(sut.Preview.HasViewerImage);

        await sut.Preview.PreviewItemAsync(TextItem());

        Assert.False(sut.Preview.HasViewerImage);
        Assert.Null(sut.Preview.ViewerImageBytes);
        Assert.True(sut.Preview.HasViewerText);
    }

    [Fact]
    public async Task PreviewItemAsync_WithNoImageLoaderConfigured_DegradesInsteadOfThrowing()
    {
        var textLoader = new FakeLoader(_ => throw new InvalidOperationException("should not be called"));
        var sut = Build(textLoader, imageLoader: null);

        await sut.Preview.PreviewItemAsync(ImageItem());

        Assert.False(sut.Preview.IsViewerVisible);
        Assert.True(sut.IsWarning);
    }

    [Fact]
    public async Task PreviewItemAsync_RefusesAnImageThePolicyExcludes()
    {
        var textLoader = new FakeLoader(_ => throw new InvalidOperationException("should not be called"));
        var imageLoader = new FakeImageLoader(_ => throw new InvalidOperationException("should not be called"));
        var sut = Build(textLoader, imageLoader);

        // A RAW camera format: classified as FileKind.Image, but not something the bitmap viewer
        // can decode (ImagePreviewPolicyTests covers the policy itself).
        await sut.Preview.PreviewItemAsync(new DriveItem("/my-files/shot.cr2", "shot.cr2", IsFolder: false, Size: 32));

        Assert.False(sut.Preview.IsViewerVisible);
        Assert.Empty(imageLoader.Requests);
        Assert.True(sut.IsWarning);
    }

    [Fact]
    public async Task CloseViewerCommand_AlsoClearsAPreviewedImage()
    {
        byte[] bytes = [1, 2, 3];
        var imageLoader = new FakeImageLoader(item => Task.FromResult(new ImageFilePreview(item.Path, item.Name, bytes, bytes.Length)));
        var sut = Build(loader: null, imageLoader);
        await sut.Preview.PreviewItemAsync(ImageItem());

        await sut.Preview.CloseViewerCommand.ExecuteAsync();

        Assert.False(sut.Preview.IsViewerVisible);
        Assert.Null(sut.Preview.ViewerImageBytes);
        Assert.False(sut.Preview.HasViewerImage);
    }
}
