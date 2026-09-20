using System.Globalization;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Providers.Proton;
using MyPersonalDrive.Services.Sync;
using MyPersonalDrive.Tests.Fakes;
using MyPersonalDrive.ViewModels.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// S5 of docs/PLAN-SYNC-VISIBILITY.md: the per-provider section of the Properties dialog. The point
/// of the feature is the providers an item is <em>not</em> synced with, so that is what most of
/// this pins.
/// </summary>
public class SyncStatusPropertiesTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-props-{Guid.NewGuid():N}.db");
    private readonly string _otherDbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-props2-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        File.Delete(_otherDbPath);
    }

    private static (SyncExecutor Executor, SyncCrashRecovery Recovery) MachineryFor(SyncStateStore store)
    {
        var provider = new ProtonDriveProvider(new ProtonDriveService(new FakeCliExecutor()));
        return (new SyncExecutor(provider.Operations, store, new LocalScanner(), new RemoteScanner(provider)), new SyncCrashRecovery(store));
    }

    private SyncPanelViewModel PanelWith(SyncStateStore first, string firstName, SyncStateStore? second = null, string? secondName = null)
    {
        var (executor, recovery) = MachineryFor(first);
        var panel = new SyncPanelViewModel(first, executor, recovery, providerDisplayName: firstName);
        if (second is not null)
        {
            var (otherExecutor, otherRecovery) = MachineryFor(second);
            panel.AddAccount(second, otherExecutor, otherRecovery, null, secondName!);
        }

        return panel;
    }

    [Fact]
    public async Task A_provider_with_no_pair_still_gets_a_row_saying_so()
    {
        var store = new SyncStateStore(_dbPath);
        var other = new SyncStateStore(_otherDbPath, "onedrive:default");
        var panel = PanelWith(store, "Proton Drive", other, "OneDrive");
        await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);

        var rows = await panel.DescribeSyncStatusAsync("/my-files/Docs/a.txt", "/home/user/Docs/a.txt");

        Assert.Equal(2, rows.Count);
        var onedrive = Assert.Single(rows, row => row.Account == "OneDrive");
        Assert.Equal(SyncNodeStatus.NotInPair, onedrive.Status);
        Assert.Null(onedrive.Detail);
    }

    [Fact]
    public async Task The_covering_pair_is_named_with_both_of_its_folders()
    {
        var store = new SyncStateStore(_dbPath);
        var panel = PanelWith(store, "Proton Drive");
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        await store.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false, null, null), T0);
        await store.UpdatePairStatusAsync(pair.Id, T0, SyncPairStatus.Ok, null);

        var rows = await panel.DescribeSyncStatusAsync("/my-files/Docs/a.txt", null);

        var row = Assert.Single(rows);
        Assert.Equal(SyncNodeStatus.Synced, row.Status);
        Assert.Contains("/my-files/Docs", row.Detail);
        Assert.Contains("/home/user/Docs", row.Detail);
        Assert.NotNull(row.LastSyncText);
    }

    [Fact]
    public async Task A_queued_transfer_beats_the_last_cycles_verdict()
    {
        var store = new SyncStateStore(_dbPath);
        var panel = PanelWith(store, "Proton Drive");
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        await store.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false, null, null), T0);
        await store.UpdatePairStatusAsync(pair.Id, T0, SyncPairStatus.Ok, null);
        await store.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 10, 1000)], T0);

        var rows = await panel.DescribeSyncStatusAsync("/my-files/Docs/a.txt", null);

        Assert.Equal(SyncNodeStatus.Pending, Assert.Single(rows).Status);
    }

    [Fact]
    public async Task Two_pairs_sharing_one_local_folder_each_get_their_own_row()
    {
        var store = new SyncStateStore(_dbPath);
        var other = new SyncStateStore(_otherDbPath, "onedrive:default");
        var panel = PanelWith(store, "Proton Drive", other, "OneDrive");
        await store.CreatePairAsync("/my-files/Docs", "/home/user/Shared", SyncDirection.RemoteToLocal, ConflictPolicy.KeepBoth);
        await other.CreatePairAsync("/Documents", "/home/user/Shared", SyncDirection.RemoteToLocal, ConflictPolicy.KeepBoth);

        var rows = await panel.DescribeSyncStatusAsync(null, Path.Combine("/home/user/Shared", "a.txt"));

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Account == "Proton Drive" && row.Detail!.Contains("/my-files/Docs"));
        Assert.Contains(rows, row => row.Account == "OneDrive" && row.Detail!.Contains("/Documents"));
    }

    [Fact]
    public async Task The_same_pair_matched_from_both_sides_is_not_listed_twice()
    {
        var store = new SyncStateStore(_dbPath);
        var panel = PanelWith(store, "Proton Drive");
        await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);

        var rows = await panel.DescribeSyncStatusAsync("/my-files/Docs/a.txt", Path.Combine("/home/user/Docs", "a.txt"));

        Assert.Single(rows);
    }
}
