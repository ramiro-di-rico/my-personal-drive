using Microsoft.Data.Sqlite;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// Regression: OneDrive's root is bare "/", which used to split into zero breadcrumb segments —
/// so at the root the breadcrumb bar was empty, and the first folder you navigated into showed up
/// as the *only* breadcrumb, making it look like the root itself. Proton's root is a real named
/// folder ("/my-files"), which always produced at least one segment, so this never showed up
/// there. Fixed by always giving the root its own leading, always-clickable segment.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowBreadcrumbTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.Breadcrumb").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-breadcrumb-{Guid.NewGuid():N}.db");

    public MainWindowBreadcrumbTests()
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

    [Fact]
    public void AtStartup_Proton_ShowsMyFilesAsTheRootBreadcrumb()
    {
        var service = new ProtonDriveService(new FakeCliExecutor());
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store));
        var sut = new MainWindowViewModel(provider, new DriveCacheService(Path.Combine(_tempAppData, "cache.db")), new AppSettingsService(), panel);

        var root = Assert.Single(sut.BreadcrumbItems);
        Assert.Equal("my-files", root.Label);
        Assert.Equal("/my-files", root.Path);
        Assert.False(root.CanNavigate); // already there
    }

    /// <summary>The actual regression: this used to be an empty collection.</summary>
    [Fact]
    public void AtStartup_OneDrive_ShowsAClickableRootBreadcrumb_NotAnEmptyBar()
    {
        var authenticator = new MyPersonalDrive.Services.Providers.OneDrive.GraphAuthenticator(
            "client-id",
            new MyPersonalDrive.Services.Providers.OneDrive.OneDriveTokenStore(_tempAppData),
            new HttpClient(new FakeHttpMessageHandler()));
        var provider = new MyPersonalDrive.Services.Providers.OneDrive.OneDriveProvider(
            authenticator,
            new MyPersonalDrive.Services.Providers.OneDrive.GraphHttpClient(authenticator, new HttpClient(new FakeHttpMessageHandler())));
        var store = new SyncStateStore(_dbPath);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var panel = new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store));
        var sut = new MainWindowViewModel(provider, new DriveCacheService(Path.Combine(_tempAppData, "cache.db")), new AppSettingsService(), panel);

        var root = Assert.Single(sut.BreadcrumbItems);
        Assert.Equal("OneDrive", root.Label);
        Assert.Equal("/", root.Path);
        Assert.False(root.CanNavigate); // already there
    }
}
