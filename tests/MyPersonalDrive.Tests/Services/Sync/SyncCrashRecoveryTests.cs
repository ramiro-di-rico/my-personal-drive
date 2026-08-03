using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services.Sync;

public class SyncCrashRecoveryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-recovery-tests-{Guid.NewGuid():N}.db");
    private readonly string _localRoot = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-recovery-local-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        if (Directory.Exists(_localRoot))
        {
            Directory.Delete(_localRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_RequeuesRunningActionsAndClearsTheTempFolder()
    {
        var store = new SyncStateStore(_dbPath);
        var pair = await store.CreatePairAsync("/my-files/A", _localRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await store.EnqueueActionsAsync(pair.Id, [new SyncAction(SyncOperation.DownloadFile, "a.txt", null, 1, 1000)], T0);
        var queued = Assert.Single(await store.GetPendingActionsAsync(pair.Id));
        await store.MarkRunningAsync(queued.Id);

        // A half-finished download from the killed run.
        var tempRoot = Path.Combine(_localRoot, ".mypersonaldrive-tmp");
        var partialDirectory = Path.Combine(tempRoot, "deadbeef");
        Directory.CreateDirectory(partialDirectory);
        await File.WriteAllTextAsync(Path.Combine(partialDirectory, "a.txt"), "half");

        var cleared = await new SyncCrashRecovery(store).RecoverAsync();

        Assert.Equal(1, cleared);
        Assert.False(Directory.Exists(tempRoot));
        Assert.Single(await store.GetPendingActionsAsync(pair.Id));
        Assert.Contains(await store.GetRecentLogsAsync(pair.Id, 10), log => log.Message.Contains("temp folder"));
    }

    [Fact]
    public async Task Recover_WithNothingToClean_IsANoOp()
    {
        var store = new SyncStateStore(_dbPath);
        var pair = await store.CreatePairAsync("/my-files/A", _localRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        Directory.CreateDirectory(_localRoot);

        Assert.Equal(0, await new SyncCrashRecovery(store).RecoverAsync());
        Assert.Empty(await store.GetRecentLogsAsync(pair.Id, 10));
    }

    [Fact]
    public async Task Recover_WithAMissingLocalFolder_DoesNotThrow()
    {
        var store = new SyncStateStore(_dbPath);
        await store.CreatePairAsync("/my-files/A", Path.Combine(_localRoot, "never-created"), SyncDirection.RemoteToLocal, ConflictPolicy.Ask);

        // An unmounted drive or a folder the user deleted must not stop the app from starting.
        Assert.Equal(0, await new SyncCrashRecovery(store).RecoverAsync());
    }

    [Fact]
    public async Task Recover_ClearsTheTempFolderOfEveryPair()
    {
        var store = new SyncStateStore(_dbPath);
        var firstRoot = Path.Combine(_localRoot, "one");
        var secondRoot = Path.Combine(_localRoot, "two");
        await store.CreatePairAsync("/my-files/A", firstRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        await store.CreatePairAsync("/my-files/B", secondRoot, SyncDirection.RemoteToLocal, ConflictPolicy.Ask);
        Directory.CreateDirectory(Path.Combine(firstRoot, ".mypersonaldrive-tmp", "x"));
        Directory.CreateDirectory(Path.Combine(secondRoot, ".mypersonaldrive-tmp", "y"));

        Assert.Equal(2, await new SyncCrashRecovery(store).RecoverAsync());
        Assert.False(Directory.Exists(Path.Combine(firstRoot, ".mypersonaldrive-tmp")));
        Assert.False(Directory.Exists(Path.Combine(secondRoot, ".mypersonaldrive-tmp")));
    }
}
