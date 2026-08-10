using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using MyPersonalDrive.Services.Sync;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Pins the fix for the UI freeze: the stores must not run their SQLite work on the thread that
/// called them.
///
/// <b>Why the assertion is shaped this way.</b> `Microsoft.Data.Sqlite` has no true async I/O — its
/// `…Async` methods run to completion on the calling thread and return an already-finished task.
/// Measured before the fix: a write whose lock was held elsewhere blocked its caller for 30.0
/// seconds and *then* handed back `IsCompleted == true`. Reached from a view model, that thread is
/// the UI thread, and since every command's `CanExecute` is gated on `!IsLoading`, the window froze
/// with dead buttons.
///
/// So the property that matters is not "it eventually succeeds" but "the caller keeps running". Each
/// test holds the write lock from a second connection, which guarantees the operation cannot finish
/// promptly, and then asserts the call handed back an *unfinished* task instead of blocking. Before
/// the fix these assertions fail; the old code could only return a completed one.
/// </summary>
public class SqliteDoesNotBlockTheCallerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-offthread-{Guid.NewGuid():N}.db");

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

    /// <summary>
    /// Opens a second connection and holds SQLite's single write lock until disposed, so any other
    /// writer has to wait out the busy timeout.
    /// </summary>
    private async Task<(SqliteConnection Connection, SqliteTransaction Transaction)> HoldTheWriteLockAsync()
    {
        var holder = new SqliteConnection($"Data Source={_dbPath}");
        await holder.OpenAsync();
        var tx = (SqliteTransaction)await holder.BeginTransactionAsync();
        using var cmd = holder.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO DriveItems (Path, ParentPath, Name, IsFolder) VALUES ('/lock','/p','lock',0);";
        await cmd.ExecuteNonQueryAsync();
        return (holder, tx);
    }

    [Fact]
    public async Task DriveCacheService_DoesNotBlockTheCaller_WhenTheWriteLockIsHeld()
    {
        var sut = new DriveCacheService(_dbPath);
        var (holder, tx) = await HoldTheWriteLockAsync();

        try
        {
            var write = sut.SyncItemsAsync("/my-files", []);

            // The whole point: control came straight back. Blocking here is what froze the window.
            Assert.False(write.IsCompleted);

            await Assert.ThrowsAnyAsync<SqliteException>(() => write);
        }
        finally
        {
            await tx.RollbackAsync();
            await holder.DisposeAsync();
        }
    }

    [Fact]
    public async Task SyncStateStore_DoesNotBlockTheCaller_WhenTheWriteLockIsHeld()
    {
        // The sync panel reaches this store from the UI thread in a dozen places, over the same
        // cache.db the browser writes, so it has exactly the same exposure.
        var sut = new SyncStateStore(_dbPath);
        var (holder, tx) = await HoldTheWriteLockAsync();

        try
        {
            var write = sut.CreatePairAsync("/my-files/x", "/tmp/x", SyncDirection.RemoteToLocal, ConflictPolicy.PreferRemote);

            Assert.False(write.IsCompleted);
            await Assert.ThrowsAnyAsync<SqliteException>(() => write);
        }
        finally
        {
            await tx.RollbackAsync();
            await holder.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheBusyTimeout_FailsInSeconds_NotHalfAMinute()
    {
        // The provider defaults to 30s. That is the difference between "slow" and "hung": contention
        // here is transient, so a bounded wait that surfaces an error beats holding on for 30s.
        var sut = new DriveCacheService(_dbPath);
        var (holder, tx) = await HoldTheWriteLockAsync();

        try
        {
            var elapsed = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<SqliteException>(() => sut.SyncItemsAsync("/my-files", []));

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15),
                $"waited {elapsed.Elapsed.TotalSeconds:F1}s, which suggests the 30s default is back");
        }
        finally
        {
            await tx.RollbackAsync();
            await holder.DisposeAsync();
        }
    }

    [Fact]
    public void ReadsAreNotBlockedByAWriter_SoWalIsActuallyOn()
    {
        // WAL is what lets the browser read while the sync engine writes. Without it the read below
        // would contend, and the freeze would be reachable even from a listing.
        using var connection = new SqliteConnection(SqliteOffThread.ConnectionStringFor(_dbPath));
        _ = new DriveCacheService(_dbPath);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)command.ExecuteScalar(), ignoreCase: true);
    }
}
