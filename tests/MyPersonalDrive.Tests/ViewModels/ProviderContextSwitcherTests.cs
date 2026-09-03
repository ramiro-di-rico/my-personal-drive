using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers;
using MyPersonalDrive.Services.Providers.Generic;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

[Collection(AppDataCollection.Name)]
public class ProviderContextSwitcherTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.ProviderContextSwitcher").FullName;
    private readonly string _dbPath;
    private readonly string? _originalAppData;

    public ProviderContextSwitcherTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempAppData);
        _dbPath = Path.Combine(_tempAppData, "sync.db");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalAppData);
        try
        {
            Directory.Delete(_tempAppData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<MainWindowViewModel> BuildAsync(AppSettingsService? settings = null)
    {
        settings ??= new AppSettingsService();
        var cli = new FakeCliExecutor();
        cli.RespondForPath("/my-files", """
            [{"uid":"u1","parentUid":"parent","name":{"ok":true,"value":"seed.txt"},"ownedBy":{"email":"a@b.com"},"type":"file","isShared":false,"modificationTime":"2026-01-01T00:00:00.000Z"}]
            """);
        var protonService = new ProtonDriveService(cli);
        var protonProvider = new ProtonDriveProvider(protonService);
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(protonProvider.Operations, store, new LocalScanner(), new RemoteScanner(protonProvider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        var cache = new DriveCacheService(Path.Combine(_tempAppData, "cache.db"));
        await cache.SyncItemsAsync("/my-files", [new DriveItem("/my-files/seed.txt", "seed.txt", IsFolder: false)]);

        var vm = new MainWindowViewModel(protonProvider, cache, settings, panel);

        // Add additional browsable providers with pre-seeded caches
        var google = new GenericCloudDriveProvider(ProviderId.GoogleDrive, "Google Drive", "user@gmail.com", isAuthenticated: true);
        var googleCache = new DriveCacheService(Path.Combine(_tempAppData, "google-cache.db"));
        await googleCache.SyncItemsAsync("/", [new DriveItem("/gdrive.txt", "gdrive.txt", IsFolder: false)]);
        vm.AddBrowsableAccount(google, googleCache);

        var nextcloud = new GenericCloudDriveProvider(ProviderId.Nextcloud, "Nextcloud", "admin@nextcloud.local", isAuthenticated: false);
        var nextcloudCache = new DriveCacheService(Path.Combine(_tempAppData, "nextcloud-cache.db"));
        await nextcloudCache.SyncItemsAsync("/", [new DriveItem("/nc.txt", "nc.txt", IsFolder: false)]);
        vm.AddBrowsableAccount(nextcloud, nextcloudCache);

        return vm;
    }

    [Fact]
    public async Task AvailableProviders_PopulatesDynamicAccountIdentities_AndStatus()
    {
        var settings = new AppSettingsService();
        settings.Update(s =>
        {
            s.ProtonAccountLabel = "alice@proton.me";
            s.IsAuthenticated = true;
            s.GoogleDriveAccountLabel = "alice@gmail.com";
            s.IsGoogleDriveAuthenticated = true;
            s.IsNextcloudAuthenticated = false;
        });

        var vm = await BuildAsync(settings);
        var providers = vm.AvailableProviders;

        var proton = providers.First(p => p.Id == ProviderId.Proton);
        Assert.Equal("alice@proton.me", proton.AccountSummary);
        Assert.True(proton.IsAuthenticated);

        var google = providers.First(p => p.Id == ProviderId.GoogleDrive);
        Assert.Equal("alice@gmail.com", google.AccountSummary);
        Assert.True(google.IsAuthenticated);

        var nextcloud = providers.First(p => p.Id == ProviderId.Nextcloud);
        Assert.Equal("Not signed in", nextcloud.AccountSummary);
        Assert.False(nextcloud.IsAuthenticated);
    }

    [Fact]
    public async Task SelectedProvider_Change_SwitchesProviderLive_AndRecalculatesQuota()
    {
        var vm = await BuildAsync();

        Assert.Equal(ProviderId.Proton, vm.SelectedProvider!.Id);
        Assert.Equal(500L * 1024 * 1024 * 1024, vm.QuotaTotalBytes); // 500 GB for Proton

        // Switch to Google Drive via SelectedProvider property
        await vm.SwitchBrowserAccountAsync(ProviderId.GoogleDrive);

        Assert.Equal(ProviderId.GoogleDrive, vm.SelectedProvider.Id);
        Assert.Equal("Google Drive", vm.ActiveProviderDisplayName);
        Assert.True(vm.IsGoogleDriveActive);
        Assert.False(vm.IsProtonActive);
        Assert.Equal(15L * 1024 * 1024 * 1024, vm.QuotaTotalBytes); // 15 GB for Google Drive
    }

    [Fact]
    public async Task SwitchToNextcloud_UpdatesActiveFlags_AndQuota()
    {
        var vm = await BuildAsync();

        await vm.SwitchToNextcloudCommand.ExecuteAsync();

        Assert.True(vm.IsNextcloudActive);
        Assert.Equal("Nextcloud", vm.ActiveProviderDisplayName);
        Assert.Equal(100L * 1024 * 1024 * 1024, vm.QuotaTotalBytes); // 100 GB for Nextcloud
    }

    [Fact]
    public async Task UnauthenticatedProvider_ShowsFallbackStatusMessage()
    {
        var vm = await BuildAsync();

        // Nextcloud is unauthenticated
        await vm.SwitchToNextcloudCommand.ExecuteAsync();

        Assert.False(vm.IsAuthenticated);
        Assert.Contains("Authentication required", vm.StatusMessage);
    }
}
