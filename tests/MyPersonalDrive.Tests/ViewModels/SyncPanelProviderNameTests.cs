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

    // The prompt moved off StatusMessage and onto EmptyStateMessage, which is shown only while
    // there are no pairs (docs/PLAN-UX-ROUND-2.md §13). The behaviour these two guard — that the
    // name is interpolated, not hardcoded — is unchanged.
    [Fact]
    public void WithNoProviderNameGiven_DefaultsToProtonDrive()
    {
        var sut = Build(providerDisplayName: null);

        Assert.Equal("Agregá una carpeta para empezar a sincronizarla desde Proton Drive.", sut.EmptyStateMessage);
        Assert.True(sut.HasNoPairs);
        Assert.Equal(string.Empty, sut.StatusMessage);
    }

    [Fact]
    public void WithAProviderNameGiven_UsesItInTheEmptyStatePrompt()
    {
        var sut = Build("OneDrive");

        Assert.Equal("Agregá una carpeta para empezar a sincronizarla desde OneDrive.", sut.EmptyStateMessage);
    }

    // The prompt names the account a new pair would actually target. Switching the browsed account
    // moves that target (SwitchBrowserAccountAsync -> SetActiveAccount -> AddPairAsync's
    // ActiveSlot), so a fixed name would promise one provider and create a pair on another.
    [Fact]
    public void SwitchingTheActiveAccount_MovesThePromptWithIt()
    {
        var store = new SyncStateStore(_dbPath);
        var service = new ProtonDriveService(new FakeCliExecutor());
        var provider = new ProtonDriveProvider(service);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var sut = new SyncPanelViewModel(store, executor, new SyncCrashRecovery(store), providerDisplayName: "Proton Drive");
        sut.AddAccount(store, executor, new SyncCrashRecovery(store), null, "Google Drive");

        Assert.Contains("Proton Drive", sut.EmptyStateMessage);

        sut.SetActiveAccount("Google Drive");

        Assert.Contains("Google Drive", sut.EmptyStateMessage);

        // An account that was never registered falls back to the primary rather than naming a
        // provider that cannot receive the pair.
        sut.SetActiveAccount("Dropbox");
        Assert.Contains("Proton Drive", sut.EmptyStateMessage);
    }
}
