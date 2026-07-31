using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncStateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-sync-tests-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private SyncStateStore CreateSut() => new(_dbPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task CreatePair_ThenGetPairs_RoundTrips()
    {
        var sut = CreateSut();

        var created = await sut.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.RemoteToLocal, ConflictPolicy.KeepBoth, ["*.tmp", ".git/"]);

        var pairs = await sut.GetPairsAsync();
        var pair = Assert.Single(pairs);
        Assert.Equal(created.Id, pair.Id);
        Assert.Equal("/my-files/Docs", pair.RemotePath);
        Assert.Equal("/home/user/Docs", pair.LocalPath);
        Assert.Equal(SyncDirection.RemoteToLocal, pair.Direction);
        Assert.Equal(ConflictPolicy.KeepBoth, pair.ConflictPolicy);
        Assert.True(pair.IsEnabled);
        Assert.False(pair.IsPaused);
        Assert.Equal(["*.tmp", ".git/"], pair.ExcludeGlobs);
        Assert.Equal(SyncPairStatus.Never, pair.LastStatus);
        Assert.Null(pair.LastSyncAt);
    }

    [Fact]
    public async Task GetPair_ById_ReturnsMatchingPair()
    {
        var sut = CreateSut();
        var created = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);

        var fetched = await sut.GetPairAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal("/my-files/A", fetched!.RemotePath);
    }

    [Fact]
    public async Task GetPair_UnknownId_ReturnsNull()
    {
        var sut = CreateSut();

        Assert.Null(await sut.GetPairAsync(999));
    }

    [Fact]
    public async Task UpdatePairStatus_PersistsStatusAndError()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);

        await sut.UpdatePairStatusAsync(pair.Id, T0, SyncPairStatus.PartialFailure, "one conflict pending");

        var updated = await sut.GetPairAsync(pair.Id);
        Assert.Equal(SyncPairStatus.PartialFailure, updated!.LastStatus);
        Assert.Equal("one conflict pending", updated.LastError);
        Assert.Equal(T0, updated.LastSyncAt);
    }

    [Fact]
    public async Task SetPairEnabledAndPaused_Toggle()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);

        await sut.SetPairEnabledAsync(pair.Id, false);
        await sut.SetPairPausedAsync(pair.Id, true);

        var updated = await sut.GetPairAsync(pair.Id);
        Assert.False(updated!.IsEnabled);
        Assert.True(updated.IsPaused);
    }

    [Fact]
    public async Task DeletePair_CascadesToStateAndQueue()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false, null, new NodeFingerprint("a.txt", false, 1, T0, "uid", "hash")), T0);
        await sut.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);

        await sut.DeletePairAsync(pair.Id);

        Assert.Null(await sut.GetPairAsync(pair.Id));
        Assert.Empty(await sut.GetBaselineAsync(pair.Id));
        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id));
    }

    [Fact]
    public async Task Baseline_UpsertThenGet_RoundTripsBothSides()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);
        var remote = new NodeFingerprint("a.txt", false, 100, T0, "uid-a", "sha1-a");
        var local = new NodeFingerprint("a.txt", false, 100, T0, null, "sha1-a");

        await sut.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false, local, remote), T0);

        var baseline = await sut.GetBaselineAsync(pair.Id);
        var entry = Assert.Single(baseline).Value;
        Assert.Equal("uid-a", entry.RemoteAtSync!.NodeId);
        Assert.Equal("sha1-a", entry.RemoteAtSync.ContentHash);
        Assert.Equal(100, entry.LocalAtSync!.Size);
        Assert.Equal(T0, entry.LocalAtSync.ModifiedAt);
    }

    [Fact]
    public async Task Baseline_Upsert_OverwritesPreviousEntryForSamePath()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);
        await sut.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false, null, new NodeFingerprint("a.txt", false, 1, T0, "uid", "hash-old")), T0);

        await sut.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false, null, new NodeFingerprint("a.txt", false, 2, T0, "uid", "hash-new")), T0);

        var baseline = await sut.GetBaselineAsync(pair.Id);
        Assert.Single(baseline);
        Assert.Equal("hash-new", baseline["a.txt"].RemoteAtSync!.ContentHash);
    }

    [Fact]
    public async Task Baseline_Remove_DeletesTheEntry()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);
        await sut.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false, null, new NodeFingerprint("a.txt", false, 1, T0, "uid", "hash")), T0);

        await sut.RemoveBaselineAsync(pair.Id, "a.txt");

        Assert.Empty(await sut.GetBaselineAsync(pair.Id));
    }

    [Fact]
    public async Task Baseline_Folders_HaveNullSizeAndHash_RoundTripAsNull()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var folderFp = new NodeFingerprint("Photos", true, null, T0, "uid-folder", null);

        await sut.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("Photos", true, null, folderFp), T0);

        var entry = (await sut.GetBaselineAsync(pair.Id))["Photos"];
        Assert.True(entry.IsFolder);
        Assert.Null(entry.RemoteAtSync!.Size);
        Assert.Null(entry.RemoteAtSync.ContentHash);
        Assert.Equal("uid-folder", entry.RemoteAtSync.NodeId);
    }

    [Fact]
    public async Task Queue_EnqueueThenGetPending_OrdersByPriority()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        await sut.EnqueueActionsAsync(pair.Id,
        [
            new SyncAction(SyncOperation.DownloadFile, "b.txt", null, 10, 1000),
            new SyncAction(SyncOperation.CreateLocalFolder, "a", null, null, 0),
        ], T0);

        var pending = await sut.GetPendingActionsAsync(pair.Id);
        Assert.Equal(2, pending.Count);
        Assert.Equal(SyncOperation.CreateLocalFolder, pending[0].Operation);
        Assert.Equal(SyncOperation.DownloadFile, pending[1].Operation);
        Assert.All(pending, a => Assert.Equal(SyncQueueState.Pending, a.State));
    }

    [Fact]
    public async Task Queue_SecondaryPathAndBytes_RoundTripThroughPayload()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);

        await sut.EnqueueActionsAsync(pair.Id,
            [new SyncAction(SyncOperation.ResolveConflictKeepBoth, "a.txt", "a (local conflict 2026-01-01 00-00-00).txt", 42, 1000)], T0);

        var action = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
        Assert.Equal("a (local conflict 2026-01-01 00-00-00).txt", action.SecondaryPath);
        Assert.Equal(42, action.Bytes);
    }

    [Fact]
    public async Task Queue_MarkDone_RemovesItFromPending()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);
        var queued = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));

        await sut.MarkRunningAsync(queued.Id);
        await sut.MarkDoneAsync(queued.Id, T0.AddSeconds(1));

        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id));
    }

    [Fact]
    public async Task Queue_MarkFailed_WithRetry_StaysPending()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);
        var queued = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));

        await sut.MarkFailedAsync(queued.Id, "network blip", T0.AddSeconds(5));

        var stillPending = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
        Assert.Equal(1, stillPending.AttemptCount);
        Assert.Equal("network blip", stillPending.LastError);
    }

    [Fact]
    public async Task Queue_MarkFailed_WithoutRetry_LeavesPendingListEmpty()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);
        var queued = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));

        await sut.MarkFailedAsync(queued.Id, "permanent error", nextAttemptAt: null);

        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id));
    }

    [Fact]
    public async Task Queue_ResetRunningToPending_RecoversFromACrashMidTransfer()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);
        var queued = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
        await sut.MarkRunningAsync(queued.Id);
        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id)); // now 'Running', not pending

        await sut.ResetRunningToPendingAsync();

        Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
    }

    [Fact]
    public async Task Log_ThenGetRecent_ReturnsNewestFirst()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        await sut.LogAsync(pair.Id, SyncLogLevel.Info, "a.txt", "downloaded", T0);
        await sut.LogAsync(pair.Id, SyncLogLevel.Warning, null, "used text fallback parsing", T0.AddSeconds(1));

        var logs = await sut.GetRecentLogsAsync(pair.Id, 10);
        Assert.Equal(2, logs.Count);
        Assert.Equal(SyncLogLevel.Warning, logs[0].Level); // newest first
        Assert.Equal(SyncLogLevel.Info, logs[1].Level);
    }

    [Fact]
    public async Task Log_GetRecent_RespectsLimit()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        for (var i = 0; i < 5; i++)
        {
            await sut.LogAsync(pair.Id, SyncLogLevel.Info, null, $"entry {i}", T0.AddSeconds(i));
        }

        var logs = await sut.GetRecentLogsAsync(pair.Id, 2);

        Assert.Equal(2, logs.Count);
    }
}
