using System.Globalization;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncStateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-sync-tests-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

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
        Assert.True(pair.MirrorDeletes);
    }

    [Fact]
    public async Task CreatePair_WithMirrorDeletesOff_RoundTrips()
    {
        var sut = CreateSut();

        var created = await sut.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.LocalToRemote, ConflictPolicy.Ask, mirrorDeletes: false);
        Assert.False(created.MirrorDeletes);

        var pair = Assert.Single(await sut.GetPairsAsync());
        Assert.False(pair.MirrorDeletes);
        Assert.False((await sut.GetPairAsync(pair.Id))!.MirrorDeletes);
    }

    [Fact]
    public async Task UpdatePairSettings_ChangesMirrorDeletes()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.LocalToRemote, ConflictPolicy.Ask);
        Assert.True(pair.MirrorDeletes);

        await sut.UpdatePairSettingsAsync(pair.Id, SyncDirection.LocalToRemote, ConflictPolicy.Ask, mirrorDeletes: false);

        Assert.False((await sut.GetPairAsync(pair.Id))!.MirrorDeletes);
    }

    [Fact]
    public async Task AutomaticSyncEnabled_DefaultsToTrue()
    {
        Assert.True(await CreateSut().GetAutomaticSyncEnabledAsync());
    }

    [Fact]
    public async Task AutomaticSyncEnabled_SurvivesANewStoreOverTheSameDatabase()
    {
        await CreateSut().SetAutomaticSyncEnabledAsync(false);

        Assert.False(await CreateSut().GetAutomaticSyncEnabledAsync());

        await CreateSut().SetAutomaticSyncEnabledAsync(true);
        Assert.True(await CreateSut().GetAutomaticSyncEnabledAsync());
    }

    /// <summary>
    /// P7 Phase A regression (docs/PLAN-CLOUD-PROVIDERS.md): the flag used to be one unscoped row
    /// in the shared `cache.db`, so two accounts sharing that file (e.g. Proton + OneDrive) would
    /// silently share one on/off choice — turning OneDrive's automatic sync off also turned
    /// Proton's off. Caught by <c>SyncPanelMultiAccountTests</c>.
    /// </summary>
    [Fact]
    public async Task AutomaticSyncEnabled_IsScopedPerAccountKey_NotSharedAcrossAccountsInTheSameDatabase()
    {
        var accountA = new SyncStateStore(_dbPath, "account-a");
        var accountB = new SyncStateStore(_dbPath, "account-b");

        await accountA.SetAutomaticSyncEnabledAsync(false);
        await accountB.SetAutomaticSyncEnabledAsync(true);

        Assert.False(await accountA.GetAutomaticSyncEnabledAsync());
        Assert.True(await accountB.GetAutomaticSyncEnabledAsync());
    }

    /// <summary>
    /// An existing single-Proton-account install wrote this flag under the old unscoped key,
    /// before P7 introduced per-account scoping. That choice must survive the upgrade rather than
    /// silently resetting to the default (on) the first time it's read under the new scoped key —
    /// only for "proton:default", the sentinel every pre-P7 row was backfilled to (P4).
    /// </summary>
    [Fact]
    public async Task AutomaticSyncEnabled_FallsBackToTheLegacyUnscopedKey_ForTheDefaultProtonAccountOnly()
    {
        var legacyStore = new SyncStateStore(_dbPath); // defaults to "proton:default"
        await legacyStore.GetPairsAsync(); // ensure migrations have run before writing to AppSettings by hand

        // Simulate a pre-P7 row: written directly under the old unscoped key, not through
        // SetAutomaticSyncEnabledAsync (which now always writes the scoped key).
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO AppSettings (Key, Value) VALUES ('AutomaticSyncEnabled', '0')";
            await command.ExecuteNonQueryAsync();
        }

        Assert.False(await legacyStore.GetAutomaticSyncEnabledAsync());

        // A non-default account key must NOT see the legacy Proton row — it has no legacy data of
        // its own, so it should just get the ordinary "never recorded a choice" default (on).
        var oneDriveStore = new SyncStateStore(_dbPath, "onedrive:default");
        Assert.True(await oneDriveStore.GetAutomaticSyncEnabledAsync());
    }

    [Fact]
    public async Task DeltaToken_DefaultsToNull()
    {
        var sut = CreateSut();

        Assert.Null(await sut.GetDeltaTokenAsync(pairId: 1));
    }

    [Fact]
    public async Task DeltaToken_SetThenGet_RoundTrips()
    {
        var sut = CreateSut();

        await sut.SetDeltaTokenAsync(1, "https://graph.microsoft.com/v1.0/cursor-abc");

        Assert.Equal("https://graph.microsoft.com/v1.0/cursor-abc", await sut.GetDeltaTokenAsync(1));
    }

    [Fact]
    public async Task DeltaToken_SetToNull_ClearsAPreviouslyStoredToken()
    {
        var sut = CreateSut();
        await sut.SetDeltaTokenAsync(1, "https://graph.microsoft.com/v1.0/cursor-abc");

        await sut.SetDeltaTokenAsync(1, null);

        Assert.Null(await sut.GetDeltaTokenAsync(1));
    }

    /// <summary>
    /// A token is scoped per *pair*, not per account: two pairs on the same account sharing one
    /// token would mean whichever pair's sync runs first in a cycle "consumes" the diff and
    /// advances the cursor, so a second pair sharing it would then silently miss changes to its own
    /// subtree from before that (docs/PLAN-CLOUD-PROVIDERS.md P8).
    /// </summary>
    [Fact]
    public async Task DeltaToken_IsScopedPerPair_NotSharedAcrossPairsOnTheSameAccount()
    {
        var sut = CreateSut();

        await sut.SetDeltaTokenAsync(1, "cursor-for-pair-1");
        await sut.SetDeltaTokenAsync(2, "cursor-for-pair-2");

        Assert.Equal("cursor-for-pair-1", await sut.GetDeltaTokenAsync(1));
        Assert.Equal("cursor-for-pair-2", await sut.GetDeltaTokenAsync(2));
    }

    [Fact]
    public async Task DeltaToken_IsScopedPerAccountKey_NotSharedAcrossAccountsInTheSameDatabase()
    {
        var accountA = new SyncStateStore(_dbPath, "account-a");
        var accountB = new SyncStateStore(_dbPath, "account-b");

        await accountA.SetDeltaTokenAsync(1, "cursor-for-account-a");
        await accountB.SetDeltaTokenAsync(1, "cursor-for-account-b");

        Assert.Equal("cursor-for-account-a", await accountA.GetDeltaTokenAsync(1));
        Assert.Equal("cursor-for-account-b", await accountB.GetDeltaTokenAsync(1));
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
    public async Task UpdatePairSettings_ChangesDirectionAndConflictPolicy()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        await sut.UpdatePairSettingsAsync(pair.Id, SyncDirection.TwoWay, ConflictPolicy.PreferLocal);

        var updated = await sut.GetPairAsync(pair.Id);
        Assert.Equal(SyncDirection.TwoWay, updated!.Direction);
        Assert.Equal(ConflictPolicy.PreferLocal, updated.ConflictPolicy);
    }

    [Fact]
    public async Task UpdatePairSettings_LeavesEverythingElseUnchanged()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask, ["*.tmp"]);
        await sut.UpdatePairStatusAsync(pair.Id, T0, SyncPairStatus.Ok, null);

        await sut.UpdatePairSettingsAsync(pair.Id, SyncDirection.LocalToRemote, ConflictPolicy.KeepBoth);

        var updated = await sut.GetPairAsync(pair.Id);
        Assert.Equal("/my-files/A", updated!.RemotePath);
        Assert.Equal("/home/user/A", updated.LocalPath);
        Assert.Equal(["*.tmp"], updated.ExcludeGlobs);
        Assert.Equal(SyncPairStatus.Ok, updated.LastStatus);
        Assert.Equal(T0, updated.LastSyncAt);
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
    public async Task Conflicts_StaleOnes_AreClearedWhenTheyStopBeingConflicts()
    {
        // Nothing else ever removes a Conflict row, so without this the count only grows and
        // eventually reports conflicts that no longer exist.
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);
        await sut.EnqueueConflictsAsync(pair.Id, [
            new SyncConflict("still.txt", ConflictReason.BothChanged),
            new SyncConflict("sorted-out.txt", ConflictReason.BothChanged),
        ], T0);

        var cleared = await sut.ClearStaleConflictsAsync(pair.Id, ["still.txt"]);

        Assert.Equal(1, cleared);
        Assert.Equal("still.txt", Assert.Single(await sut.GetConflictActionsAsync(pair.Id)).RelativePath);
    }

    [Fact]
    public async Task Conflicts_AllCleared_WhenNothingConflictsAnyMore()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);
        await sut.EnqueueConflictsAsync(pair.Id, [new SyncConflict("a.txt", ConflictReason.BothChanged)], T0);

        Assert.Equal(1, await sut.ClearStaleConflictsAsync(pair.Id, []));
        Assert.Empty(await sut.GetConflictActionsAsync(pair.Id));
    }

    [Fact]
    public async Task Conflicts_ClearingStaleOnes_LeavesOtherPairsAndOtherStatesAlone()
    {
        var sut = CreateSut();
        var pairA = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);
        var pairB = await sut.CreatePairAsync("/my-files/B", "/home/user/B", SyncDirection.TwoWay, ConflictPolicy.Ask);
        await sut.EnqueueConflictsAsync(pairA.Id, [new SyncConflict("a.txt", ConflictReason.BothChanged)], T0);
        await sut.EnqueueConflictsAsync(pairB.Id, [new SyncConflict("a.txt", ConflictReason.BothChanged)], T0);
        await sut.EnqueueActionsAsync(pairA.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);

        await sut.ClearStaleConflictsAsync(pairA.Id, []);

        Assert.Empty(await sut.GetConflictActionsAsync(pairA.Id));
        Assert.Single(await sut.GetConflictActionsAsync(pairB.Id));      // other pair untouched
        Assert.Single(await sut.GetPendingActionsAsync(pairA.Id));       // pending work untouched
    }

    [Fact]
    public async Task FailedActions_StaleOnes_AreClearedWhenThePlanStopsProposingThem()
    {
        // EnqueueActionsAsync only revives a Failed row when the plan re-proposes the exact same
        // (path, operation) pair; this covers the rest, so a failure whose difference disappeared
        // some other way doesn't sit in the queue forever.
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var stillFailing = new SyncAction(SyncOperation.DownloadFile, "still.txt", null, 1, 1000);
        var resolved = new SyncAction(SyncOperation.DownloadFile, "sorted-out.txt", null, 1, 1000);
        await sut.EnqueueActionsAsync(pair.Id, [stillFailing, resolved], T0);
        foreach (var queued in await sut.GetPendingActionsAsync(pair.Id))
        {
            await sut.MarkFailedAsync(queued.Id, "gave up", nextAttemptAt: null);
        }

        var cleared = await sut.ClearStaleFailedActionsAsync(pair.Id, [stillFailing]);

        Assert.Equal(1, cleared);
        Assert.Equal("still.txt", Assert.Single(await sut.GetFailedActionsAsync(pair.Id)).RelativePath);
    }

    [Fact]
    public async Task FailedActions_AllCleared_WhenTheCurrentPlanHasNoActions()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var action = new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000);
        await sut.EnqueueActionsAsync(pair.Id, [action], T0);
        var queued = Assert.Single(await sut.GetPendingActionsAsync(pair.Id));
        await sut.MarkFailedAsync(queued.Id, "gave up", nextAttemptAt: null);

        Assert.Equal(1, await sut.ClearStaleFailedActionsAsync(pair.Id, []));
        Assert.Empty(await sut.GetFailedActionsAsync(pair.Id));
    }

    [Fact]
    public async Task FailedActions_ClearingStaleOnes_LeavesOtherPairsAndOtherStatesAlone()
    {
        var sut = CreateSut();
        var pairA = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var pairB = await sut.CreatePairAsync("/my-files/B", "/home/user/B", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var actionA = new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000);
        var actionB = new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000);
        var pendingA = new SyncAction(SyncOperation.DownloadFile, "b.txt", null, 1, 1000);
        await sut.EnqueueActionsAsync(pairA.Id, [actionA], T0);
        await sut.EnqueueActionsAsync(pairB.Id, [actionB], T0);
        var queuedA = Assert.Single(await sut.GetPendingActionsAsync(pairA.Id));
        await sut.MarkFailedAsync(queuedA.Id, "gave up", nextAttemptAt: null);
        var queuedB = Assert.Single(await sut.GetPendingActionsAsync(pairB.Id));
        await sut.MarkFailedAsync(queuedB.Id, "gave up", nextAttemptAt: null);
        await sut.EnqueueActionsAsync(pairA.Id, [pendingA], T0);

        await sut.ClearStaleFailedActionsAsync(pairA.Id, []);

        Assert.Empty(await sut.GetFailedActionsAsync(pairA.Id));
        Assert.Single(await sut.GetFailedActionsAsync(pairB.Id));        // other pair untouched
        Assert.Single(await sut.GetPendingActionsAsync(pairA.Id));       // pending work untouched
    }

    [Fact]
    public async Task Conflicts_MarkResolved_TakesTheRowOutOfTheConflictList()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.TwoWay, ConflictPolicy.Ask);
        await sut.EnqueueConflictsAsync(pair.Id, [new SyncConflict("a.txt", ConflictReason.BothChanged)], T0);
        var conflict = Assert.Single(await sut.GetConflictActionsAsync(pair.Id));

        await sut.MarkConflictResolvedAsync(conflict.Id, ConflictResolution.KeepBoth, T0.AddMinutes(1));

        Assert.Empty(await sut.GetConflictActionsAsync(pair.Id));
        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id)); // resolved, not re-queued as work
    }

    [Fact]
    public async Task Queue_RetryFailed_RevivesDeadRowsWithACleanSlate()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [
            new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000),
            new SyncAction(SyncOperation.DownloadFile, "b.txt", null, 1, 1000),
        ], T0);
        foreach (var row in await sut.GetPendingActionsAsync(pair.Id))
        {
            await sut.MarkFailedAsync(row.Id, "gave up", nextAttemptAt: null);
        }

        Assert.Equal(2, (await sut.GetFailedActionsAsync(pair.Id)).Count);

        Assert.Equal(2, await sut.RetryFailedAsync(pair.Id, T0.AddHours(1)));

        Assert.Empty(await sut.GetFailedActionsAsync(pair.Id));
        var revived = await sut.GetPendingActionsAsync(pair.Id, T0.AddHours(1));
        Assert.Equal(2, revived.Count);
        Assert.All(revived, r => Assert.Equal(0, r.AttemptCount));
        Assert.All(revived, r => Assert.Null(r.LastError));
    }

    [Fact]
    public async Task Queue_RetryFailed_RevivesBackedOffPendingRowsWithACleanSlate()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.EnqueueActionsAsync(pair.Id, [
            new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000),
            new SyncAction(SyncOperation.DownloadFile, "b.txt", null, 1, 1000),
        ], T0);

        var pending = await sut.GetPendingActionsAsync(pair.Id);
        await sut.MarkFailedAsync(pending[0].Id, "transient error", nextAttemptAt: T0.AddMinutes(5));
        await sut.MarkFailedAsync(pending[1].Id, "gave up", nextAttemptAt: null);

        Assert.Equal(2, (await sut.GetFailedActionsAsync(pair.Id)).Count);

        Assert.Equal(2, await sut.RetryFailedAsync(pair.Id, T0.AddHours(1)));

        Assert.Empty(await sut.GetFailedActionsAsync(pair.Id));
        var revived = await sut.GetPendingActionsAsync(pair.Id, T0.AddHours(1));
        Assert.Equal(2, revived.Count);
        Assert.All(revived, r => Assert.Equal(0, r.AttemptCount));
        Assert.All(revived, r => Assert.Null(r.LastError));
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

        Assert.Empty(await sut.GetPendingActionsAsync(pair.Id, DateTimeOffset.Parse("2026-01-01T09:00:00Z", CultureInfo.InvariantCulture)));
        Assert.Single(await sut.GetPendingActionsAsync(pair.Id, DateTimeOffset.Parse("2026-01-01T13:00:00Z", CultureInfo.InvariantCulture)));
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
    public async Task Log_PruneByAge_DropsOldEntriesAndKeepsRecentOnes()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await sut.LogAsync(pair.Id, SyncLogLevel.Info, "old.txt", "ancient history", T0);
        await sut.LogAsync(pair.Id, SyncLogLevel.Info, "new.txt", "recent", T0.AddDays(29));

        var removed = await sut.PruneLogsAsync(T0.AddDays(28), maxPerPair: 1000);

        Assert.Equal(1, removed);
        Assert.Equal("recent", Assert.Single(await sut.GetRecentLogsAsync(pair.Id, 100)).Message);
    }

    [Fact]
    public async Task Log_PruneByCount_KeepsTheNewestAndDropsTheRest()
    {
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        for (var i = 0; i < 20; i++)
        {
            await sut.LogAsync(pair.Id, SyncLogLevel.Info, $"f{i}.txt", $"entry {i}", T0.AddSeconds(i));
        }

        var removed = await sut.PruneLogsAsync(T0.AddDays(-1), maxPerPair: 5);

        Assert.Equal(15, removed);
        var kept = await sut.GetRecentLogsAsync(pair.Id, 100);
        Assert.Equal(5, kept.Count);
        Assert.Equal("entry 19", kept[0].Message);   // newest survives
        Assert.Equal("entry 15", kept[^1].Message);  // and exactly the newest five
    }

    [Fact]
    public async Task Log_PruneByCount_IsPerPair_SoABusyPairCannotEraseAnothersHistory()
    {
        var sut = CreateSut();
        var chatty = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        var quiet = await sut.CreatePairAsync("/my-files/B", "/home/user/B", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        for (var i = 0; i < 30; i++)
        {
            await sut.LogAsync(chatty.Id, SyncLogLevel.Info, $"f{i}.txt", $"chatty {i}", T0.AddSeconds(i));
        }

        await sut.LogAsync(quiet.Id, SyncLogLevel.Warning, null, "the one thing that happened here", T0);

        await sut.PruneLogsAsync(T0.AddDays(-1), maxPerPair: 5);

        Assert.Equal(5, (await sut.GetRecentLogsAsync(chatty.Id, 100)).Count);
        Assert.Equal("the one thing that happened here", Assert.Single(await sut.GetRecentLogsAsync(quiet.Id, 100)).Message);
    }

    [Fact]
    public async Task Log_PruneByCount_TreatsTheSchedulersOwnEntriesAsTheirOwnGroup()
    {
        // The scheduler logs with a null PairId. Those rows must not compete with a pair's history
        // for the same allowance.
        var sut = CreateSut();
        var pair = await sut.CreatePairAsync("/my-files/A", "/home/user/A", SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        for (var i = 0; i < 10; i++)
        {
            await sut.LogAsync(pair.Id, SyncLogLevel.Info, null, $"pair {i}", T0.AddSeconds(i));
            await sut.LogAsync(null, SyncLogLevel.Error, null, $"scheduler {i}", T0.AddSeconds(i));
        }

        await sut.PruneLogsAsync(T0.AddDays(-1), maxPerPair: 3);

        Assert.Equal(3, (await sut.GetRecentLogsAsync(pair.Id, 100)).Count);
        Assert.Equal(6, (await sut.GetRecentLogsAsync(null, 100)).Count); // 3 per group, both groups
    }

    [Fact]
    public async Task Log_PruneOnAnEmptyTable_IsHarmless()
    {
        var sut = CreateSut();

        Assert.Equal(0, await sut.PruneLogsAsync(T0, maxPerPair: 10));
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
