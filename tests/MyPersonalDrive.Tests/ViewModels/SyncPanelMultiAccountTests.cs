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
/// P7 Phase A (docs/PLAN-CLOUD-PROVIDERS.md): two accounts' sync pairs merged into one
/// <see cref="SyncPanelViewModel.Pairs"/> list via <see cref="SyncPanelViewModel.AddAccount"/>,
/// each labeled, with independent per-account automatic-sync toggles. Both accounts here are
/// Proton-backed (two fake CLI executors) purely for test convenience — the merge logic itself
/// doesn't care which provider a slot's store/executor came from.
/// </summary>
public class SyncPanelMultiAccountTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-multiaccount-{Guid.NewGuid():N}.db");
    private readonly string _localRootA = Directory.CreateTempSubdirectory("mypersonaldrive-multiaccount-a").FullName;
    private readonly string _localRootB = Directory.CreateTempSubdirectory("mypersonaldrive-multiaccount-b").FullName;

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

    private sealed record Account(SyncStateStore Store, SyncExecutor Executor, SyncScheduler Scheduler);

    private Account BuildAccount(string accountKey)
    {
        var cli = new FakeCliExecutor();
        cli.RespondForPath("/", "[]");
        var service = new ProtonDriveService(cli);
        var provider = new ProtonDriveProvider(service);
        var store = new SyncStateStore(_dbPath, accountKey);
        var executor = new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider));
        var scheduler = new SyncScheduler(store, executor, new SyncEchoSuppressor(), isAuthenticated: () => true);
        return new Account(store, executor, scheduler);
    }

    [Fact]
    public async Task AddAccount_MergesTheSecondAccountsPairsIntoTheSameList()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await accountB.Store.CreatePairAsync("/remote-b", _localRootB, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), accountA.Scheduler, "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), accountB.Scheduler, "Account B");
        await panel.InitializeAsync();

        Assert.Equal(2, panel.Pairs.Count);
        Assert.Contains(panel.Pairs, pair => pair.RemotePath == "/remote-a" && pair.AccountLabel == "Account A");
        Assert.Contains(panel.Pairs, pair => pair.RemotePath == "/remote-b" && pair.AccountLabel == "Account B");
    }

    [Fact]
    public async Task WithOnlyOneAccount_PairsCarryNoAccountLabel()
    {
        var accountA = BuildAccount("account-a");
        await accountA.Store.CreatePairAsync("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), accountA.Scheduler, "Account A");
        await panel.InitializeAsync();

        var pair = Assert.Single(panel.Pairs);
        Assert.False(pair.HasAccountLabel);
        Assert.Equal(string.Empty, pair.AccountLabel);
    }

    [Fact]
    public void AddAccount_AddsItsOwnIndependentToggle()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), accountA.Scheduler, "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), accountB.Scheduler, "Account B");

        Assert.Equal(2, panel.AccountSyncToggles.Count);
        Assert.Equal("Account A", panel.AccountSyncToggles[0].DisplayName);
        Assert.Equal("Account B", panel.AccountSyncToggles[1].DisplayName);
    }

    [Fact]
    public async Task TogglingOneAccount_DoesNotStartOrStopTheOther()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), accountA.Scheduler, "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), accountB.Scheduler, "Account B");

        Assert.False(accountA.Scheduler.IsRunning);
        Assert.False(accountB.Scheduler.IsRunning);

        // Toggle only Account B (the second slot's own toggle), not the panel's primary command.
        await panel.AccountSyncToggles[1].ToggleCommand.ExecuteAsync();

        Assert.False(accountA.Scheduler.IsRunning);
        Assert.True(accountB.Scheduler.IsRunning);

        await accountB.Scheduler.StopAsync();
    }

    [Fact]
    public async Task ThePrimaryToggleCommand_OnlyAffectsThePrimaryAccount()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), accountA.Scheduler, "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), accountB.Scheduler, "Account B");

        await panel.ToggleAutomaticSyncCommand.ExecuteAsync();

        Assert.True(accountA.Scheduler.IsRunning);
        Assert.False(accountB.Scheduler.IsRunning);
        // The primary slot's own toggle in the merged list reflects the same change.
        Assert.True(panel.AccountSyncToggles[0].IsRunning);

        await accountA.Scheduler.StopAsync();
    }

    [Fact]
    public async Task RecoverFromPreviousRunAsync_RecoversEveryAccount_NotJustThePrimary()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        // Account A explicitly left off from a previous run; Account B explicitly left on — the
        // opposite of the "never recorded a choice" default (which is "on"), so this only passes
        // if each account's persisted choice is read from its own scoped setting, not a value
        // shared across accounts sharing the same cache.db.
        await accountA.Store.SetAutomaticSyncEnabledAsync(false);
        await accountB.Store.SetAutomaticSyncEnabledAsync(true);

        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), accountA.Scheduler, "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), accountB.Scheduler, "Account B");

        await panel.RecoverFromPreviousRunAsync();

        Assert.False(accountA.Scheduler.IsRunning);
        Assert.True(accountB.Scheduler.IsRunning);

        await accountB.Scheduler.StopAsync();
    }

    /// <summary>
    /// P7 Phase B (docs/PLAN-CLOUD-PROVIDERS.md): once the browsed account can change live,
    /// "Add pair" must target whichever account the user is actually looking at, not always
    /// whichever was primary at startup — <c>MainWindowViewModel.SwitchBrowserAccountAsync</c>
    /// calls <see cref="SyncPanelViewModel.SetActiveAccount"/> on every switch to keep this true.
    /// </summary>
    [Fact]
    public async Task SetActiveAccount_MakesANewPairTargetThatAccountInstead_OfThePrimary()
    {
        var accountA = BuildAccount("account-a");
        var accountB = BuildAccount("account-b");
        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), accountA.Scheduler, "Account A");
        panel.AddAccount(accountB.Store, accountB.Executor, new SyncCrashRecovery(accountB.Store), accountB.Scheduler, "Account B");
        panel.RequestNewPairAsync = _ => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest("/remote-b", _localRootB, SyncDirection.RemoteToLocal, ConflictPolicy.Ask));

        panel.SetActiveAccount("Account B");
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Empty(await accountA.Store.GetPairsAsync());
        Assert.Single(await accountB.Store.GetPairsAsync());
    }

    /// <summary>A label that isn't (or is no longer) a registered account must not crash "Add pair" — it just keeps targeting the primary.</summary>
    [Fact]
    public async Task SetActiveAccount_WithAnUnknownLabel_FallsBackToThePrimary()
    {
        var accountA = BuildAccount("account-a");
        var panel = new SyncPanelViewModel(accountA.Store, accountA.Executor, new SyncCrashRecovery(accountA.Store), accountA.Scheduler, "Account A");
        panel.RequestNewPairAsync = _ => Task.FromResult<NewSyncPairRequest?>(
            new NewSyncPairRequest("/remote-a", _localRootA, SyncDirection.RemoteToLocal, ConflictPolicy.Ask));

        panel.SetActiveAccount("Some Account That Was Removed");
        await panel.AddPairCommand.ExecuteAsync();

        Assert.Single(await accountA.Store.GetPairsAsync());
    }
}
