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
    public async Task Queue_ReEnqueuingAPendingAction_DoesNotCreateASecondRow()
    {
        // Regression for a quadratic-work bug: every run re-proposes actions that haven't happened
        // yet, so blind inserts left N rows for one path after N runs, each with a fresh retry
        // budget. Measured CLI attempts went 1, 3, 6, 10, 15 and the queue never drained.
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var action = new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000);

        for (var run = 0; run < 5; run++)
        {
            await sut.EnqueueActionsAsync(pair.Id, [action], T0.AddMinutes(run * 5));
        }

        Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
    }

    [Fact]
    public async Task Queue_ReEnqueuingAFailedAction_RevivesTheRowWithAFreshAttemptCount()
    {
        // The only route back for a row that exhausted its retries, until F4 offers a retry button.
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var action = new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000);
        await sut.EnqueueActionsAsync(pair.Id, [action], T0);
        var queued = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
        await sut.MarkFailedAsync(queued.Id, "permanent", nextAttemptAt: null);
        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id));

        await sut.EnqueueActionsAsync(pair.Id, [action], T0.AddMinutes(5));

        var revived = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
        Assert.Equal(queued.Id, revived.Id); // the same row, not a second one
        Assert.Equal(0, revived.AttemptCount);
        Assert.Null(revived.LastError);
    }

    [Fact]
    public async Task Queue_ADoneActionNeverBlocksARepeat_BecauseTheFileMayHaveChangedAgain()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var action = new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000);
        await sut.EnqueueActionsAsync(pair.Id, [action], T0);
        var first = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
        await sut.MarkDoneAsync(first.Id, T0.AddSeconds(5));

        await sut.EnqueueActionsAsync(pair.Id, [action], T0.AddMinutes(5));

        var second = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Queue_DifferentOperationsOnTheSamePath_AreIndependentRows()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);

        await sut.EnqueueActionsAsync(pair.Id, [
            new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000),
            new SyncAction(SyncOperation.UpdateBaselineOnly, "a.txt", null, null, 3000),
        ], T0);

        Assert.Equal(2, (await sut.GetPendingActionsAsync(pair.Id)).Count);
    }

    [Fact]
    public async Task Queue_PruneCompleted_ClearsFinishedRowsButKeepsOutstandingWork()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [
            new SyncAction(SyncOperation.DownloadFile, "done.txt", null, 1, 1000),
            new SyncAction(SyncOperation.DownloadFile, "pending.txt", null, 1, 1000),
        ], T0);
        var rows = await sut.GetPendingActionsAsync(pair.Id);
        await sut.MarkDoneAsync(rows.Single(r => r.RelativePath == "done.txt").Id, T0);

        // Too recent to prune yet...
        Assert.Equal(0, await sut.PruneCompletedAsync(T0.AddDays(-1)));

        // ...then old enough.
        Assert.Equal(1, await sut.PruneCompletedAsync(T0.AddDays(1)));
        Assert.Equal("pending.txt", Assert.Single(await sut.GetPendingActionsAsync(pair.Id)).RelativePath);
    }

    [Fact]
    public async Task Conflicts_AreReportedOncePerPath_NotOncePerRun()
    {
        // An `Ask` conflict is re-reported by every cycle and cannot be resolved until F4's panel
        // exists, so a blind insert grew the queue every 5 minutes indefinitely.
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);
        var conflict = new SyncConflict("a.txt", ConflictReason.BothChanged);

        for (var run = 0; run < 5; run++)
        {
            await sut.EnqueueConflictsAsync(pair.Id, [conflict], T0.AddMinutes(run * 5));
        }

        Assert.Single(await sut.GetConflictActionsAsync(pair.Id));
    }

    [Fact]
    public async Task Queue_RowAwaitingItsBackoff_IsNotHandedOutUntilTheTimePasses()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);
        var queued = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));

        await sut.MarkFailedAsync(queued.Id, "connection reset", nextAttemptAt: T0.AddSeconds(5));

        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id, T0.AddSeconds(4)));
        Assert.Single(await sut.GetPendingActionsAsync(pair.Id, T0.AddSeconds(5)));

        // Omitting `now` deliberately ignores the backoff — the "run everything now" caller.
        Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
    }

    [Fact]
    public async Task Queue_BackoffComparison_HoldsAcrossTimeZoneOffsets()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);
        var queued = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));

        // Written as 10:00-03:00 (= 13:00Z). Naive string ordering would place it before 09:00Z
        // even though it is four hours later, which would hand the row out far too early.
        await sut.MarkFailedAsync(queued.Id, "connection reset", nextAttemptAt: new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(-3)));

        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id, DateTimeOffset.Parse("2026-01-01T09:00:00Z")));
        Assert.Single(await sut.GetPendingActionsAsync(pair.Id, DateTimeOffset.Parse("2026-01-01T13:00:00Z")));
    }

    [Fact]
    public async Task Conflicts_AreParkedSeparatelyFromPendingWork()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);

        await sut.EnqueueConflictsAsync(pair.Id, [new SyncConflict("a.txt", ConflictReason.BothChanged)], T0);

        // A parked conflict is never executed as pending work...
        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id));

        // ...but is readable for the UI to resolve, with the reason preserved.
        var parked = Assert.Single(await sut.GetConflictActionsAsync(pair.Id));
        Assert.Equal("a.txt", parked.RelativePath);
        Assert.Equal(SyncQueueState.Conflict, parked.State);
        Assert.Equal(nameof(ConflictReason.BothChanged), parked.LastError);
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
