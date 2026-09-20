using System.Globalization;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

/// <summary>
/// S2 of docs/PLAN-SYNC-VISIBILITY.md: the queue-state query and the per-pair index cache, against
/// a real temporary database — the collapse of several rows for one path is a SQL-shaped question
/// that an in-memory fake would not answer.
/// </summary>
public class SyncStatusServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-status-tests-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

    private SyncStateStore CreateStore() => new(_dbPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private static NodeFingerprint FileFp(string path, string hash = "hash-a")
        => new(path, false, 100, T0, null, hash);

    [Fact]
    public async Task Queue_states_come_back_keyed_by_relative_path()
    {
        var store = CreateStore();
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        await store.EnqueueActionsAsync(pair.Id,
        [
            new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 10, 1000),
            new SyncAction(SyncOperation.DownloadFile, "sub/b.txt", null, 10, 1000),
        ], T0);

        var states = await store.GetQueueStatesByPathAsync(pair.Id);

        Assert.Equal(SyncQueueState.Pending, states["a.txt"]);
        Assert.Equal(SyncQueueState.Pending, states["sub/b.txt"]);
    }

    [Fact]
    public async Task Several_rows_for_one_path_collapse_to_the_most_urgent()
    {
        // One path can carry several rows when they are different operations — EnqueueActionsAsync
        // dedupes per (path, operation), not per path. A failed one and a pending one together
        // must read as failed, which is why the badge needs the precedence order and not "last
        // row wins".
        var store = CreateStore();
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        await store.EnqueueActionsAsync(pair.Id,
        [
            new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 10, 1000),
            new SyncAction(SyncOperation.UpdateBaselineOnly, "a.txt", null, null, 4000),
        ], T0);
        var download = (await store.GetPendingActionsAsync(pair.Id)).Single(action => action.Operation == SyncOperation.DownloadFile);
        await store.MarkFailedAsync(download.Id, "boom", null);

        var states = await store.GetQueueStatesByPathAsync(pair.Id);

        Assert.Equal(SyncQueueState.Failed, Assert.Single(states).Value);
    }

    [Fact]
    public async Task Finished_rows_are_left_out()
    {
        var store = CreateStore();
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        await store.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 10, 1000)], T0);
        var queued = Assert.Single(await store.GetPendingActionsAsync(pair.Id));
        await store.MarkDoneAsync(queued.Id, T0);

        Assert.Empty(await store.GetQueueStatesByPathAsync(pair.Id));
    }

    [Fact]
    public async Task Remote_lookup_finds_the_covering_pair_and_the_relative_path()
    {
        var store = CreateStore();
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        await store.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("sub/a.txt", false, FileFp("sub/a.txt"), FileFp("sub/a.txt")), T0);
        var sut = new SyncStatusService(store, "Proton Drive");

        var lookup = await sut.LookupRemoteAsync("/my-files/Docs/sub/a.txt");

        Assert.NotNull(lookup);
        Assert.Equal("sub/a.txt", lookup!.RelativePath);
        Assert.Equal("Proton Drive", lookup.AccountLabel);
        Assert.Equal(SyncNodeStatus.Synced, lookup.Index.StatusForFile(lookup.RelativePath, FileFp("sub/a.txt"), SyncSide.Remote));
    }

    [Fact]
    public async Task A_path_outside_every_pair_has_no_lookup()
    {
        var store = CreateStore();
        await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        var sut = new SyncStatusService(store, "Proton Drive");

        Assert.Null(await sut.LookupRemoteAsync("/my-files/Photos/x.jpg"));
    }

    [Fact]
    public async Task A_sibling_remote_folder_with_a_shared_prefix_is_not_covered()
    {
        var store = CreateStore();
        await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        var sut = new SyncStatusService(store, "Proton Drive");

        Assert.Null(await sut.LookupRemoteAsync("/my-files/Docs-old/x.txt"));
    }

    [Fact]
    public async Task Local_lookup_returns_every_pair_sharing_the_folder()
    {
        var store = CreateStore();
        await store.CreatePairAsync("/my-files/Docs", "/home/user/Shared", SyncDirection.RemoteToLocal, ConflictPolicy.KeepBoth);
        await store.CreatePairAsync("/my-files/Other", "/home/user/Shared", SyncDirection.LocalToRemote, ConflictPolicy.KeepBoth);
        var sut = new SyncStatusService(store, "Proton Drive");

        var lookups = await sut.LookupLocalAsync(Path.Combine("/home/user/Shared", "a.txt"));

        Assert.Equal(2, lookups.Count);
        Assert.All(lookups, lookup => Assert.Equal("a.txt", lookup.RelativePath));
    }

    [Fact]
    public async Task The_index_is_cached_until_it_is_invalidated()
    {
        var store = CreateStore();
        var pair = await store.CreatePairAsync("/my-files/Docs", "/home/user/Docs", SyncDirection.TwoWay, ConflictPolicy.KeepBoth);
        var sut = new SyncStatusService(store, "Proton Drive");

        var before = await sut.LookupRemoteAsync("/my-files/Docs/a.txt");
        await store.UpsertBaselineAsync(pair.Id, new SyncBaselineEntry("a.txt", false, FileFp("a.txt"), FileFp("a.txt")), T0);
        var stale = await sut.LookupRemoteAsync("/my-files/Docs/a.txt");
        Assert.Same(before!.Index, stale!.Index);

        var raised = 0;
        sut.Changed += (_, _) => raised++;
        sut.Invalidate();
        var fresh = await sut.LookupRemoteAsync("/my-files/Docs/a.txt");

        Assert.Equal(1, raised);
        Assert.NotSame(before.Index, fresh!.Index);
        Assert.Equal(SyncNodeStatus.Synced, fresh.Index.StatusForFile("a.txt", FileFp("a.txt"), SyncSide.Remote));
    }
}
