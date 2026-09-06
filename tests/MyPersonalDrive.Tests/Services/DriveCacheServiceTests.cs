using System.Globalization;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using MyPersonalDrive.Services;
using Xunit;

namespace MyPersonalDrive.Tests.Services;

/// <summary>
/// Covers docs/PLAN-TECH-DEBT.md B4.1 (schema versioning) and the NodeId/ContentHash/typed
/// ModifiedAt round-trip added for sync (docs/PLAN-LOCAL-SYNC.md Appendix A).
/// </summary>
public class DriveCacheServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mypersonaldrive-tests-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void InitializingTwice_IsIdempotent_MigrationsRunOnlyOnce()
    {
        _ = new DriveCacheService(_dbPath);
        var sut = new DriveCacheService(_dbPath); // second open must not re-run or fail migrations

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        Assert.True(version >= 3, "Expected at least the DriveItems+NodeId/ContentHash+sync-tables migrations to have applied.");
    }

    [Fact]
    public void SyncTablesFromMigration3_Exist()
    {
        _ = new DriveCacheService(_dbPath);

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('SyncPairs','SyncState','SyncQueue','SyncLog')";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(4, tables.Count);
    }

    [Fact]
    public async Task AddOrUpdateItem_ThenGetCachedItems_RoundTripsNodeIdContentHashAndModifiedAt()
    {
        var sut = new DriveCacheService(_dbPath);
        var modifiedAt = DateTimeOffset.Parse("2026-06-06T14:02:28.502Z", CultureInfo.InvariantCulture);
        var item = new DriveItem(
            Path: "/my-files/report.pdf",
            Name: "report.pdf",
            IsFolder: false,
            Size: 6196055,
            ModifiedAt: modifiedAt,
            Owner: "ramiro.di.rico@proton.me",
            IsShared: true,
            NodeId: "rHChrZ...~file==",
            ContentHash: "a2abbf57e75de3b7da1312f64080090b5a0514f0");

        await sut.AddOrUpdateItemAsync("/my-files", item);
        var cached = Assert.Single(await sut.GetCachedItemsAsync("/my-files"));

        Assert.Equal(item.Path, cached.Path);
        Assert.Equal(item.NodeId, cached.NodeId);
        Assert.Equal(item.ContentHash, cached.ContentHash);
        Assert.Equal(modifiedAt, cached.ModifiedAt);
    }

    [Fact]
    public async Task GetCachedItems_WithUnparseableLegacyModifiedAt_DegradesToNull_DoesNotThrow()
    {
        var sut = new DriveCacheService(_dbPath);

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO DriveItems (Path, ParentPath, Name, IsFolder, Size, ModifiedAt, Owner, IsShared, NodeId, ContentHash)
                VALUES ('/my-files/old.txt', '/my-files', 'old.txt', 0, 10, 'not-a-real-date', NULL, 0, NULL, NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var cached = Assert.Single(await sut.GetCachedItemsAsync("/my-files"));

        Assert.Null(cached.ModifiedAt);
    }

    [Fact]
    public async Task SyncItemsAsync_RemovesStaleEntriesAndUpsertsCurrentOnes()
    {
        var sut = new DriveCacheService(_dbPath);
        await sut.AddOrUpdateItemAsync("/my-files", new DriveItem("/my-files/stale.txt", "stale.txt", false));

        await sut.SyncItemsAsync("/my-files", [new DriveItem("/my-files/fresh.txt", "fresh.txt", false)]);

        var cached = await sut.GetCachedItemsAsync("/my-files");
        Assert.Single(cached);
        Assert.Equal("fresh.txt", cached[0].Name);
    }

    [Fact]
    public async Task RemoveItemAsync_RemovesTheNodeAndItsSubtree()
    {
        var sut = new DriveCacheService(_dbPath);
        await sut.AddOrUpdateItemAsync("/my-files", new DriveItem("/my-files/folder", "folder", true));
        await sut.AddOrUpdateItemAsync("/my-files/folder", new DriveItem("/my-files/folder/child.txt", "child.txt", false));

        await sut.RemoveItemAsync("/my-files/folder");

        Assert.Empty(await sut.GetCachedItemsAsync("/my-files/folder"));
        Assert.DoesNotContain(await sut.GetCachedItemsAsync("/my-files"), i => i.Path == "/my-files/folder");
    }
}
