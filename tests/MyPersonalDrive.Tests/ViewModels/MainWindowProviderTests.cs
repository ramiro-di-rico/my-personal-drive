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
using MyPersonalDrive.Tests;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// docs/PLAN-CLOUD-PROVIDERS.md P5/P6: the settings view surfaces which provider is active and,
/// since P6, lets the user switch to the other one.
/// </summary>
[Collection(AppDataCollection.Name)]
public class MainWindowProviderTests : IDisposable
{
    private readonly string _tempAppData = Directory.CreateTempSubdirectory("MyPersonalDrive.Tests.Provider").FullName;
    private readonly string? _originalAppData;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-provider-{Guid.NewGuid():N}.db");

    public MainWindowProviderTests()
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

    private MainWindowViewModel Build(IProviderCatalog? catalog = null)
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
            providerCatalog: catalog);
    }

    [Fact]
    public void ActiveProviderDisplayName_ReflectsTheInjectedProvider()
    {
        var sut = Build();

        Assert.Equal("Proton Drive", sut.ActiveProviderDisplayName);
    }

    [Fact]
    public void AvailableProviders_WithNoCatalogGiven_DefaultsToTheRealCatalog()
    {
        var sut = Build();

        Assert.Equal([ProviderId.Proton, ProviderId.OneDrive], sut.AvailableProviders.Select(descriptor => descriptor.Id));
    }

    [Fact]
    public void AvailableProviders_UsesTheInjectedCatalog()
    {
        var sut = Build(new StubCatalog());

        var descriptor = Assert.Single(sut.AvailableProviders);
        Assert.Equal(ProviderId.OneDrive, descriptor.Id);
    }

    /// <summary>
    /// Regression test: "/my-files" is Proton's own root folder name, not a generic convention —
    /// hardcoding it as the app-wide root broke browsing on OneDrive (a real "/my-files no longer
    /// exists" warning on first launch, caught by hand after this phase's live-verification
    /// session — see docs/PLAN-CLOUD-PROVIDERS.md Appendix A). OneDrive roots at "/".
    /// </summary>
    [Fact]
    public void RootPath_ForProton_IsMyFiles()
    {
        var sut = Build();

        Assert.Equal("/my-files", sut.RootPath);
        Assert.Equal("/my-files", sut.CurrentPath);
    }

    [Fact]
    public void RootPath_ForOneDrive_IsSlash()
    {
        var authenticator = new MyPersonalDrive.Services.Providers.OneDrive.GraphAuthenticator(
            "client-id",
            new MyPersonalDrive.Services.Providers.OneDrive.OneDriveTokenStore(_tempAppData),
            new HttpClient(new FakeHttpMessageHandler()));
        var oneDriveProvider = new MyPersonalDrive.Services.Providers.OneDrive.OneDriveProvider(
            authenticator,
            new MyPersonalDrive.Services.Providers.OneDrive.GraphHttpClient(authenticator, new HttpClient(new FakeHttpMessageHandler())));
        var store = new SyncStateStore(_dbPath);
        var syncExecutor = new SyncExecutor(oneDriveProvider.Operations, store, new LocalScanner(), new RemoteScanner(oneDriveProvider));
        var panel = new SyncPanelViewModel(store, syncExecutor, new SyncCrashRecovery(store));
        var sut = new MainWindowViewModel(
            oneDriveProvider,
            new DriveCacheService(Path.Combine(_tempAppData, "cache.db")),
            new AppSettingsService(),
            panel);

        Assert.Equal("/", sut.RootPath);
        Assert.Equal("/", sut.CurrentPath);
    }

    /// <summary>
    /// Builds a Proton-primary view model with a second, OneDrive browsing session registered via
    /// <see cref="MainWindowViewModel.AddBrowsableAccount"/> — the P7 Phase B setup
    /// (docs/PLAN-CLOUD-PROVIDERS.md) needed to exercise a live account switch.
    ///
    /// Both accounts' caches are pre-seeded at their own root path. This isn't just tidiness: an
    /// empty cache makes <c>LoadFolderAsync</c> *await* the CLI/Graph fetch inline, whose success
    /// path (not just its error path) marshals through <c>Dispatcher.UIThread.InvokeAsync</c>
    /// (<c>MainWindowViewModel.FetchFromCliAndUpdateCacheAsync</c>) — which never completes without
    /// a running Avalonia dispatcher and hangs the test forever (the same headless-host limitation
    /// <c>DisplayItems</c>'s own doc comment already flags). A non-empty cache makes that same
    /// fetch fire-and-forget instead, so <c>SwitchBrowserAccountAsync</c>'s own <c>GoToRootAsync</c>
    /// call returns without ever touching the dispatcher.
    /// </summary>
    private async Task<(MainWindowViewModel Sut, AppSettingsService Settings)> BuildWithBothAccounts()
    {
        var protonCache = new DriveCacheService(Path.Combine(_tempAppData, "proton-cache.db"));
        await protonCache.SyncItemsAsync("/my-files", [new DriveItem("/my-files/seed.txt", "seed.txt", IsFolder: false)]);

        var protonCli = new FakeCliExecutor();
        protonCli.RespondForPath("/my-files", """
            [{"uid":"u1","parentUid":"parent","name":{"ok":true,"value":"seed.txt"},"ownedBy":{"email":"a@b.com"},"type":"file","isShared":false,"modificationTime":"2026-01-01T00:00:00.000Z"}]
            """);
        var protonService = new ProtonDriveService(protonCli);
        var protonProvider = new ProtonDriveProvider(protonService);
        var protonStore = new SyncStateStore(_dbPath, "proton:default");
        var protonExecutor = new SyncExecutor(protonProvider.Operations, protonStore, new LocalScanner(), new RemoteScanner(protonProvider));
        var panel = new SyncPanelViewModel(protonStore, protonExecutor, new SyncCrashRecovery(protonStore))
        {
            GetRemoteFolderChildren = protonProvider.Operations.ListFolderAsync, // App.axaml.cs's own composition-root wiring for the primary
        };
        var settings = new AppSettingsService();

        var sut = new MainWindowViewModel(protonProvider, protonCache, settings, panel);

        var tokenStore = new MyPersonalDrive.Services.Providers.OneDrive.OneDriveTokenStore(_tempAppData);
        tokenStore.Save(new MyPersonalDrive.Services.Providers.OneDrive.StoredOneDriveToken
        {
            AccessToken = "token",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        var authenticator = new MyPersonalDrive.Services.Providers.OneDrive.GraphAuthenticator(
            "client-id", tokenStore, new HttpClient(new FakeHttpMessageHandler()));
        var oneDriveProvider = new MyPersonalDrive.Services.Providers.OneDrive.OneDriveProvider(
            authenticator,
            new MyPersonalDrive.Services.Providers.OneDrive.GraphHttpClient(authenticator, new HttpClient(new FakeHttpMessageHandler())));

        var oneDriveCache = new DriveCacheService(Path.Combine(_tempAppData, "onedrive-cache.db"));
        await oneDriveCache.SyncItemsAsync("/", [new DriveItem("/seed.txt", "seed.txt", IsFolder: false)]);
        sut.AddBrowsableAccount(oneDriveProvider, oneDriveCache);

        return (sut, settings);
    }

    [Fact]
    public async Task SwitchToOneDrive_ChangesTheActiveProviderLive_NoConfirmationNeeded()
    {
        var (sut, _) = await BuildWithBothAccounts();
        Assert.True(sut.IsProtonActive);

        await sut.SwitchToOneDriveCommand.ExecuteAsync();

        Assert.True(sut.IsOneDriveActive);
        Assert.False(sut.IsProtonActive);
        Assert.Equal("OneDrive", sut.ActiveProviderDisplayName);
        Assert.Equal("/", sut.RootPath);
        Assert.Equal("/", sut.CurrentPath);
    }

    [Fact]
    public async Task SwitchBack_ReturnsToTheOriginalAccountsOwnRoot()
    {
        var (sut, _) = await BuildWithBothAccounts();

        await sut.SwitchToOneDriveCommand.ExecuteAsync();
        await sut.SwitchToProtonCommand.ExecuteAsync();

        Assert.True(sut.IsProtonActive);
        Assert.Equal("/my-files", sut.RootPath);
    }

    [Fact]
    public async Task Switching_ReReadsIsAuthenticatedForTheTargetProvider_NotTheStaleValue()
    {
        var (sut, settings) = await BuildWithBothAccounts();
        Assert.False(sut.IsAuthenticated); // Proton, never configured in this test

        settings.Update(s => s.IsOneDriveAuthenticated = true);
        await sut.SwitchToOneDriveCommand.ExecuteAsync();

        Assert.True(sut.IsAuthenticated);
    }

    /// <summary>
    /// Regression: <c>SyncPanel.GetRemoteFolderChildren</c> ("Add pair"'s remote folder browser)
    /// used to be wired once at startup and never revisited. Left stale after a live switch, it
    /// would list the *previous* account's remote tree starting from the *new* account's root
    /// path — a real bug reported live: navigate OneDrive, switch to Proton, then browse for a
    /// remote folder to sync, and it showed OneDrive's listing under a Proton-shaped path.
    /// </summary>
    [Fact]
    public async Task SwitchToOneDrive_RePointsGetRemoteFolderChildrenAtTheNewProvider()
    {
        var (sut, _) = await BuildWithBothAccounts();
        var protonChildren = await sut.SyncPanel.GetRemoteFolderChildren!("/my-files", CancellationToken.None);
        Assert.Equal("seed.txt", Assert.Single(protonChildren).Name); // sanity: starts on Proton's own cache-seeded tree

        await sut.SwitchToOneDriveCommand.ExecuteAsync();

        // Still points at "/my-files" on purpose — proving it now resolves against OneDrive's
        // operations (a Graph HTTP 404, since no route was registered for that path), not
        // Proton's (which would fail a completely different way — no CLI response queued).
        var ex = await Assert.ThrowsAsync<DriveException>(() => sut.SyncPanel.GetRemoteFolderChildren!("/my-files", CancellationToken.None));
        Assert.Contains("OneDrive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingToAnUnregisteredAccount_IsANoOp()
    {
        var sut = Build(); // only Proton is ever registered here

        await sut.SwitchToOneDriveCommand.ExecuteAsync();

        Assert.True(sut.IsProtonActive);
        Assert.False(sut.IsOneDriveActive);
    }

    /// <summary>Proves the constructor reads from whatever catalog it's given, not a hardcoded one.</summary>
    private sealed class StubCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Available { get; } = [new(ProviderId.OneDrive, "OneDrive (stub)")];

        public ICloudDriveProvider Create(ProviderId id, AppSettingsService settings)
            => throw new NotSupportedException();

        public ProviderId ResolveOrDefault(ProviderId requested) => ProviderId.OneDrive;
    }
}
