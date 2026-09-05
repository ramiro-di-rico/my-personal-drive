using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// P9 (docs/PLAN-CLOUD-PROVIDERS.md): the Sync window's "filter by account" chips, and
/// <see cref="SyncPanelViewModel.VisiblePairs"/>, the filtered view of <see cref="SyncPanelViewModel.Pairs"/>
/// they drive. Same two-fake-Proton-accounts setup as <c>SyncPanelMultiAccountTests</c> — the
/// filtering logic doesn't care which provider a slot's store/executor came from.
/// </summary>
public class SyncPanelProviderFilterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-providerfilter-{Guid.NewGuid():N}.db");
    private readonly string _localRootA = Directory.CreateTempSubdirectory("mypersonaldrive-providerfilter-a").FullName;
    private readonly string _localRootB = Directory.CreateTempSubdirectory("mypersonaldrive-providerfilter-b").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
            Directory.Delete(_localRootA, recursive: true);
            Directory.Delete(_localRootB, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed record Account(SyncStateStore Store, SyncExecutor Executor);

    private Account BuildAccount(string accountKey)
    {
        var cli = new FakeCliExecutor();
        cli.RespondForPath("/", "[]");
        var service = new ProtonDriveService(cli);
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath, accountKey);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        return new Account(store, executor);
    }

    // U5 (docs/PLAN-UX-ROUND-2.md §5): the sync tab carries a badge so a pair that has been
    // failing since last week is discoverable without opening the view. It reads every pair, not
    // just the ones the active filter chip lets through.
    [Fact]
    public async Task HasFailingPairs_IsTrue_WhenAnyPairFailed_EvenIfTheFilterHidesIt()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var failing = await accountB.Store.CreatePairAsync("/remote-b", _localRootB, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await accountB.Store.UpdatePairStatusAsync(failing.Id, DateTimeOffset.UtcNow, SyncPairStatus.Error, "2 acción(es) fallaron");

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), null, "Account B");
        await panel.InitializeAsync();

        Assert.True(panel.HasFailingPairs);

        // Filtering to the healthy account hides the failing row from the list, but not the badge:
        // a failure you filtered out of view is still a failure.
        await panel.ProviderFilters.Single(chip => chip.Label == "Account A").ApplyCommand.ExecuteAsync();
        Assert.DoesNotContain(panel.VisiblePairs, pair => pair.Id == failing.Id);
        Assert.True(panel.HasFailingPairs);
    }

    [Fact]
    public async Task HasFailingPairs_IsFalse_WhenEveryPairIsHealthy()
    {
        var accountA = BuildAccount("account-a");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        await panel.InitializeAsync();

        Assert.False(panel.HasFailingPairs);
    }

    // U7 (docs/PLAN-UX-ROUND-2.md §7): the account toggles and the filter chips sat adjacent,
    // same size, naming the same providers, while doing entirely different things. The toggle's
    // label no longer bakes the state and the action glyph into one string — the row is labelled
    // "Sincronización automática:" and the state has its own property.
    [Fact]
    public async Task TheAccountToggle_SeparatesTheAccountName_FromItsState()
    {
        var accountA = BuildAccount("account-a");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        await panel.InitializeAsync();

        var toggle = Assert.Single(panel.AccountSyncToggles);
        Assert.Equal("Account A", toggle.Label);
        Assert.DoesNotContain(":", toggle.Label);
        // No scheduler was passed, so automatic sync is not running.
        Assert.Equal("pausada", toggle.StateText);
        Assert.Contains("Activar", toggle.ActionTooltip);
        Assert.Contains("Account A", toggle.ActionTooltip);
    }

    // Regression, reported live (docs/PLAN-UX-ROUND-2.md §11): every configured provider gets a
    // scheduler at startup and every scheduler is started, so the panel showed five accounts as
    // "activada" when only one was signed in. The loop was indeed running; it just skipped every
    // cycle, which is not what "activada" says to a user.
    [Fact]
    public async Task OnlySignedInAccounts_GetAnAutomaticSyncToggle()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var signedIn = new SyncScheduler(accountA.Store, accountA.Executor, new SyncEchoSuppressor(), isAuthenticated: () => true);
        var neverConfigured = new SyncScheduler(accountB.Store, accountB.Executor, new SyncEchoSuppressor(), isAuthenticated: () => false);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), signedIn, "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), neverConfigured, "Account B");

        // Both slots exist and keep their toggle — the unfiltered collection stays the source of
        // truth every other caller reads.
        Assert.Equal(2, panel.AccountSyncToggles.Count);

        // ...but only the signed-in one is offered to the user.
        var shown = Assert.Single(panel.VisibleAccountSyncToggles);
        Assert.Equal("Account A", shown.Label);
        Assert.True(panel.HasVisibleAccountSyncToggles);

        await signedIn.DisposeAsync();
        await neverConfigured.DisposeAsync();
    }

    [Fact]
    public async Task WithASingleAccount_NoFilterChipsAreOffered()
    {
        var accountA = BuildAccount("account-a");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        await panel.InitializeAsync();

        Assert.Empty(panel.ProviderFilters);
        Assert.False(panel.HasProviderFilters);
        Assert.Equal(panel.Pairs, panel.VisiblePairs);
    }

    [Fact]
    public async Task WithTwoAccounts_OffersATodosChipPlusOnePerAccount_WithCounts()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        await accountA.Store.CreatePairAsync("/remote-a1", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await accountA.Store.CreatePairAsync("/remote-a2", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await accountB.Store.CreatePairAsync("/remote-b", _localRootB, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), null, "Account B");
        await panel.InitializeAsync();

        Assert.True(panel.HasProviderFilters);
        Assert.Equal(3, panel.ProviderFilters.Count);
        Assert.Null(panel.ProviderFilters[0].AccountLabel);
        Assert.Equal(3, panel.ProviderFilters[0].Count);
        Assert.True(panel.ProviderFilters[0].IsActive); // "Todos" starts active
        Assert.Equal("Account A", panel.ProviderFilters[1].AccountLabel);
        Assert.Equal(2, panel.ProviderFilters[1].Count);
        Assert.Equal("Account B", panel.ProviderFilters[2].AccountLabel);
        Assert.Equal(1, panel.ProviderFilters[2].Count);
    }

    [Fact]
    public async Task ApplyingAChip_NarrowsVisiblePairsWithoutTouchingPairs()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await accountB.Store.CreatePairAsync("/remote-b", _localRootB, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), null, "Account B");
        await panel.InitializeAsync();

        await panel.ProviderFilters[1].ApplyCommand.ExecuteAsync(); // "Account A" chip

        Assert.Equal(2, panel.Pairs.Count); // the source collection is untouched
        var visible = Assert.Single(panel.VisiblePairs);
        Assert.Equal("Account A", visible.AccountLabel);
        Assert.True(panel.ProviderFilters[1].IsActive);
        Assert.False(panel.ProviderFilters[0].IsActive);
    }

    [Fact]
    public async Task ClickingTheActiveChipAgain_ClearsTheFilter()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await accountB.Store.CreatePairAsync("/remote-b", _localRootB, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), null, "Account B");
        await panel.InitializeAsync();

        await panel.ProviderFilters[1].ApplyCommand.ExecuteAsync();
        await panel.ProviderFilters[1].ApplyCommand.ExecuteAsync();

        Assert.Equal(2, panel.VisiblePairs.Count());
        Assert.True(panel.ProviderFilters[0].IsActive);
    }

    [Fact]
    public async Task RemovingTheFilteredAccountsLastPair_FallsBackToTodosInsteadOfGoingEmpty()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        var pairA = await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await accountB.Store.CreatePairAsync("/remote-b", _localRootB, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), null, "Account B");
        await panel.InitializeAsync();
        await panel.ProviderFilters[1].ApplyCommand.ExecuteAsync(); // filter to "Account A"

        await accountA.Store.DeletePairAsync(pairA.Id);
        await panel.RefreshCommand.ExecuteAsync();

        // Only Account B still holds pairs, so the row collapses entirely rather than offering a
        // choice between two identical lists (docs/PLAN-UX-ROUND-2.md §11.4). The point this test
        // has always guarded is unchanged and is the assertion below: the stale filter must not
        // survive and leave the list empty.
        Assert.Empty(panel.ProviderFilters);
        Assert.False(panel.HasProviderFilters);
        Assert.Single(panel.VisiblePairs);
    }

    // The chips exist to narrow a mixed list. An account with no pairs offers a filter whose only
    // outcome is an empty list, and every provider in the catalog gets a slot whether or not it is
    // configured — which is how "OneDrive (0)" and "Google Drive (0)" ended up on screen
    // (docs/PLAN-UX-ROUND-2.md §11.4).
    [Fact]
    public async Task AccountsWithNoPairs_GetNoFilterChip()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        var accountC = BuildAccount("account-c");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await accountB.Store.CreatePairAsync("/remote-b", _localRootB, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), providerDisplayName: "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), null, "Account B");
        panel.AddAccount(accountC.Store, accountC.Executor, new SyncCrashRecovery(accountC.Store), null, "Account C");
        await panel.InitializeAsync();

        // Todos + the two accounts that actually have pairs. Account C is configured but empty.
        Assert.Equal(new[] { "Todos", "Account A", "Account B" }, panel.ProviderFilters.Select(chip => chip.Label));
        Assert.DoesNotContain(panel.ProviderFilters, chip => chip.Count == 0);
    }
}
