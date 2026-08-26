using Microsoft.Data.Sqlite;
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
/// docs/PLAN-CLOUD-PROVIDERS.md P5: the settings view surfaces which provider is active. Only
/// Proton exists today, so there is nothing to switch to yet — see the doc comment on
/// <see cref="MainWindowViewModel.AvailableProviders"/> for why the switch flow is deferred.
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

        var descriptor = Assert.Single(sut.AvailableProviders);
        Assert.Equal(ProviderId.Proton, descriptor.Id);
    }

    [Fact]
    public void AvailableProviders_UsesTheInjectedCatalog()
    {
        var sut = Build(new StubCatalog());

        var descriptor = Assert.Single(sut.AvailableProviders);
        Assert.Equal(ProviderId.OneDrive, descriptor.Id);
    }

    /// <summary>Proves the constructor reads from whatever catalog it's given, not a hardcoded one.</summary>
    private sealed class StubCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Available { get; } = [new(ProviderId.OneDrive, "OneDrive (stub)")];

        public ICloudDriveProvider Create(ProviderId id, AppSettingsService settings)
            => throw new NotSupportedException();
    }
}
