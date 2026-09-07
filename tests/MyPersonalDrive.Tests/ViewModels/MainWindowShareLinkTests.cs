using System.Net;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.OneDrive;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// "Share Link" only makes sense for a backend that actually offers one — Proton's CLI doesn't,
/// Microsoft Graph does (<c>OneDriveOperations.CreateShareLinkAsync</c>). This exercises
/// <see cref="MainWindowViewModel.CreateShareLinkAsync"/>'s own gate on
/// <c>ProviderCapabilities.SupportsShareLinks</c>, on both sides of it — the row-level gate
/// (<c>DriveNodeViewModel.CanShareLink</c>) is what actually keeps the menu entry from being
/// clicked in the app, so this method's own check is the defensive fallback, but it still has to
/// behave correctly if ever reached.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowShareLinkTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.ShareLink").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-sharelink-{Guid.NewGuid():N}.db");

    public MainWindowShareLinkTests()
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

    private MainWindowViewModel BuildProton()
    {
        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        return new MainWindowViewModel(provider, new DriveCacheService(Path.Combine(_tempAppData, "cache.db")), new AppSettingsService(), panel);
    }

    private (MainWindowViewModel ViewModel, FakeHttpMessageHandler Handler) BuildOneDrive()
    {
        var handler = new FakeHttpMessageHandler();
        var tokenStore = new OneDriveTokenStore(_tempAppData);
        tokenStore.Save(new StoredOneDriveToken { AccessToken = "token", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var authenticator = new GraphAuthenticator("client-id", tokenStore, new HttpClient(new FakeHttpMessageHandler()));
        var provider = new OneDriveProvider(authenticator, new GraphHttpClient(authenticator, new HttpClient(handler)));
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        var viewModel = new MainWindowViewModel(provider, new DriveCacheService(Path.Combine(_tempAppData, "cache.db")), new AppSettingsService(), panel);
        return (viewModel, handler);
    }

    [Fact]
    public async Task OnProton_WarnsInsteadOfCallingAnything_SinceTheCapabilityIsAbsent()
    {
        var viewModel = BuildProton();
        var copyCalled = false;
        viewModel.RequestCopyToClipboardAsync = _ => { copyCalled = true; return Task.CompletedTask; };

        await viewModel.CreateShareLinkAsync(new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false, Size: 10));

        Assert.False(copyCalled);
        Assert.True(viewModel.IsWarning);
        Assert.Contains("Proton Drive", viewModel.StatusMessage);
    }

    [Fact]
    public async Task OnOneDrive_CopiesTheReturnedUrlToTheClipboard()
    {
        var (viewModel, handler) = BuildOneDrive();
        handler.When(HttpMethod.Post, "createLink", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":"perm1","link":{"webUrl":"https://1drv.ms/x/s!abc123"}}
            """));
        string? copied = null;
        viewModel.RequestCopyToClipboardAsync = url => { copied = url; return Task.CompletedTask; };

        await viewModel.CreateShareLinkAsync(new DriveItem("/report.pdf", "report.pdf", IsFolder: false, Size: 10));

        Assert.Equal("https://1drv.ms/x/s!abc123", copied);
        Assert.False(viewModel.IsWarning);
        Assert.Contains("https://1drv.ms/x/s!abc123", viewModel.StatusMessage);
    }

    [Fact]
    public async Task OnOneDrive_WithNoClipboardHandlerWired_StillReportsTheUrlInStatus()
    {
        var (viewModel, handler) = BuildOneDrive();
        handler.When(HttpMethod.Post, "createLink", _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":"perm1","link":{"webUrl":"https://1drv.ms/x/s!abc123"}}
            """));

        await viewModel.CreateShareLinkAsync(new DriveItem("/report.pdf", "report.pdf", IsFolder: false, Size: 10));

        Assert.Contains("https://1drv.ms/x/s!abc123", viewModel.StatusMessage);
    }

    /// <summary>The row-level gate a right-click actually goes through — this is what keeps "Share Link" from ever being clickable in the app for a provider that doesn't support it.</summary>
    [Fact]
    public void DriveNodeViewModel_CanShareLink_ReflectsTheProviderCapability()
    {
        var supported = new DriveNodeViewModel(
            new DriveItem("/report.pdf", "report.pdf", IsFolder: false, Size: 10),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            syncActions: new DriveNodeSyncActions { SupportsShareLinks = true, CreateShareLinkAsync = _ => Task.CompletedTask });
        var unsupported = new DriveNodeViewModel(
            new DriveItem("/my-files/report.pdf", "report.pdf", IsFolder: false, Size: 10),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            syncActions: new DriveNodeSyncActions { SupportsShareLinks = false });

        Assert.True(supported.CanShareLink);
        Assert.True(supported.ShareLinkCommand.CanExecute(null));

        Assert.False(unsupported.CanShareLink);
        Assert.False(unsupported.ShareLinkCommand.CanExecute(null));
        Assert.Contains("not available", unsupported.ShareLinkTooltip);
    }

    /// <summary>
    /// docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2, live-verification finding: a Google-native Doc
    /// has no binary content, so Drive rejects a plain download/preview read with a 403 — before
    /// this fix the buttons were live and the user hit that error instead of the button simply not
    /// being clickable. `IsRemoteOnlyDocument` has no extension either, so without the explicit
    /// check TextPreviewPolicy's "no extension at all" fallback would offer to preview it as text.
    /// </summary>
    [Fact]
    public void DriveNodeViewModel_ARemoteOnlyDocument_CannotBeDownloadedOrPreviewed()
    {
        var googleDoc = new DriveNodeViewModel(
            new DriveItem("/Meeting Notes", "Meeting Notes", IsFolder: false, IsRemoteOnlyDocument: true),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            previewItemAsync: _ => Task.CompletedTask);
        var ordinaryFile = new DriveNodeViewModel(
            new DriveItem("/notes.txt", "notes.txt", IsFolder: false, Size: 10),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            previewItemAsync: _ => Task.CompletedTask);

        Assert.False(googleDoc.CanDownload);
        Assert.False(googleDoc.DownloadCommand.CanExecute(null));
        Assert.Contains("cannot be downloaded", googleDoc.DownloadTooltip);
        Assert.False(googleDoc.CanPreview);

        Assert.True(ordinaryFile.CanDownload);
        Assert.True(ordinaryFile.DownloadCommand.CanExecute(null));
        Assert.True(ordinaryFile.CanPreview);
    }
}
