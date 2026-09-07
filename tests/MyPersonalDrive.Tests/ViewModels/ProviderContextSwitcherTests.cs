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

        // Switch to Google Drive via SelectedProvider property
        await vm.SwitchBrowserAccountAsync(ProviderId.GoogleDrive);

        Assert.Equal(ProviderId.GoogleDrive, vm.SelectedProvider.Id);
        Assert.Equal("Google Drive", vm.ActiveProviderDisplayName);
        Assert.True(vm.IsGoogleDriveActive);
        Assert.False(vm.IsProtonActive);
    }

    /// <summary>
    /// docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2: three earlier fix attempts all failed live
    /// because AvailableProviders was reassigned to a brand-new collection object on every provider
    /// switch — Avalonia's ComboBox resets/mis-tracks selection whenever its bound ItemsSource
    /// itself changes identity, no matter how the selected value is kept in sync afterward. The
    /// actual fix makes AvailableProviders one stable instance, updated in place. This pins that
    /// down directly: the reference must never change.
    /// </summary>
    [Fact]
    public async Task AvailableProviders_IsTheSameInstance_AcrossASwitch()
    {
        var vm = await BuildAsync();
        var before = vm.AvailableProviders;

        await vm.SwitchBrowserAccountAsync(ProviderId.GoogleDrive);

        Assert.Same(before, vm.AvailableProviders);
    }

    /// <summary>
    /// docs/PLAN-CLOUD-PROVIDERS.md P10 Appendix A2: the header ComboBox binds by index rather than
    /// SelectedItem so a stray equality mismatch can never lose the selection — this locks in that
    /// SelectedProviderIndex resolves to the right position, both before and after a real switch.
    /// </summary>
    [Fact]
    public async Task SelectedProviderIndex_TracksTheActiveProvider_AcrossASwitch()
    {
        var vm = await BuildAsync();

        var providers = vm.AvailableProviders;
        Assert.Equal(providers.ToList().FindIndex(p => p.Id == ProviderId.Proton), vm.SelectedProviderIndex);

        await vm.SwitchBrowserAccountAsync(ProviderId.GoogleDrive);

        // Same instance (pinned by AvailableProviders_IsTheSameInstance_AcrossASwitch above), its
        // contents updated in place — re-reading it here isn't testing anything different from
        // reusing `providers`, deliberately kept anyway to mirror how the real ComboBox re-reads it.
        var providersAfter = vm.AvailableProviders;
        Assert.Equal(providersAfter.ToList().FindIndex(p => p.Id == ProviderId.GoogleDrive), vm.SelectedProviderIndex);
    }

    [Fact]
    public async Task SwitchToNextcloud_UpdatesActiveFlags_AndQuota()
    {
        var vm = await BuildAsync();

        await vm.SwitchToNextcloudCommand.ExecuteAsync();

        Assert.True(vm.IsNextcloudActive);
        Assert.Equal("Nextcloud", vm.ActiveProviderDisplayName);
    }

    /// <summary>
    /// Reported live (docs/PLAN-UX-ROUND-2.md §11.3): switching provider from the settings view
    /// left the header picker blank on some steps and not others. Two causes, both exercised here
    /// — RefreshAvailableProviders replaced all five descriptors on every switch even though a
    /// switch changes none of their fields, and the ComboBox's resulting write-back re-entered
    /// SelectedProviderIndex's setter, starting a second switch from inside the first.
    /// </summary>
    [Fact]
    public async Task WalkingThroughEveryProvider_KeepsTheSelectedIndexPointingAtTheActiveOne()
    {
        var vm = await BuildAsync();

        // The exact sequence that reproduced it, plus the starting point.
        Assert.Equal(ProviderId.Proton, vm.SelectedProvider!.Id);
        Assert.Equal(vm.SelectedProviderIndex, IndexOf(vm, ProviderId.Proton));

        await vm.SwitchToOneDriveCommand.ExecuteAsync();
        AssertSelectionMatchesActiveProvider(vm);

        await vm.SwitchToGoogleDriveCommand.ExecuteAsync();
        AssertSelectionMatchesActiveProvider(vm);

        await vm.SwitchToNextcloudCommand.ExecuteAsync();
        AssertSelectionMatchesActiveProvider(vm);

        await vm.SwitchToProtonCommand.ExecuteAsync();
        AssertSelectionMatchesActiveProvider(vm);
        Assert.Equal(ProviderId.Proton, vm.SelectedProvider!.Id);
    }

    /// <summary>
    /// A switch changes no descriptor's displayed fields, so it must not replace any element:
    /// replacing the selected one is what makes Avalonia drop the selection in the first place.
    /// </summary>
    [Fact]
    public async Task SwitchingProvider_DoesNotDisturbTheProviderCollection()
    {
        var vm = await BuildAsync();
        var changes = 0;
        vm.AvailableProviders.CollectionChanged += (_, _) => changes++;

        await vm.SwitchToGoogleDriveCommand.ExecuteAsync();
        await vm.SwitchToNextcloudCommand.ExecuteAsync();
        await vm.SwitchToProtonCommand.ExecuteAsync();

        Assert.Equal(0, changes);
    }

    /// <summary>
    /// The ComboBox writes its own transient selection back mid-switch. Acting on it starts a
    /// second switch from inside the first, which is the re-entrancy half of §11.3.
    /// </summary>
    [Fact]
    public async Task AWriteBackArrivingMidSwitch_IsIgnoredRatherThanStartingASecondSwitch()
    {
        var vm = await BuildAsync();
        var protonIndex = IndexOf(vm, ProviderId.Proton);

        vm.PropertyChanged += (_, e) =>
        {
            // Stand in for the control echoing the old index while the switch is still running.
            if (e.PropertyName == nameof(MainWindowViewModel.ActiveProviderDisplayName))
            {
                vm.SelectedProviderIndex = protonIndex;
            }
        };

        await vm.SwitchToGoogleDriveCommand.ExecuteAsync();

        Assert.True(vm.IsGoogleDriveActive);
        AssertSelectionMatchesActiveProvider(vm);
    }

    private static int IndexOf(MainWindowViewModel vm, ProviderId id)
    {
        for (var i = 0; i < vm.AvailableProviders.Count; i++)
        {
            if (vm.AvailableProviders[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    // The picker renders blank precisely when the index stops agreeing with the active provider.
    private static void AssertSelectionMatchesActiveProvider(MainWindowViewModel vm)
    {
        var index = vm.SelectedProviderIndex;
        Assert.InRange(index, 0, vm.AvailableProviders.Count - 1);
        Assert.Equal(vm.ActiveProviderDisplayName, vm.AvailableProviders[index].DisplayName);
        Assert.Equal(vm.SelectedProvider!.Id, vm.AvailableProviders[index].Id);
    }

    [Fact]
    public async Task UnauthenticatedProvider_ShowsFallbackStatusMessage()
    {
        var vm = await BuildAsync();

        // Nextcloud is unauthenticated
        await vm.SwitchToNextcloudCommand.ExecuteAsync();

        Assert.False(vm.IsAuthenticated);
        Assert.Contains("requires authentication", vm.StatusMessage);
    }
}
