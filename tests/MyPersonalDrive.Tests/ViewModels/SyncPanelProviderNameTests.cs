using Microsoft.Data.Sqlite;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// docs/PLAN-CLOUD-PROVIDERS.md P6/§5 item 3: the panel's Proton-named strings interpolate the
/// active provider's display name instead of hardcoding "Proton Drive" — so the composition root
/// can pass "OneDrive" once that provider is active. The default keeps every other call site
/// (including every other test in this suite) working unchanged.
/// </summary>
public class SyncPanelProviderNameTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-provider-name-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private SyncPanelViewModel Build(string? providerDisplayName)
    {
        var store = new SyncStateStore(_dbPath);
        var service = new ProtonDriveService(new FakeCliExecutor());
        var provider = new ProtonDriveProvider(service);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        return providerDisplayName is null
            ? new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store))
            : new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store), providerDisplayName: providerDisplayName);
    }

    [Fact]
    public void WithNoProviderNameGiven_DefaultsToProtonDrive()
    {
        var sut = Build(providerDisplayName: null);

        Assert.Equal("Add a folder to start syncing it from Proton Drive.", sut.StatusMessage);
    }

    [Fact]
    public void WithAProviderNameGiven_UsesItInTheInitialStatusMessage()
    {
        var sut = Build("OneDrive");

        Assert.Equal("Add a folder to start syncing it from OneDrive.", sut.StatusMessage);
    }
}
