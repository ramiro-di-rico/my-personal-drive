using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace MyPersonalDrive.Services;

public class DriveCacheService
{
    private readonly string _connectionString;

    public DriveCacheService(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = SqliteOffThread.ConnectionStringFor(dbPath);
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // WAL lets the sync engine read the cache while the browser is mid-write (and vice
        // versa) without blocking; see docs/PLAN-TECH-DEBT.md B4.1.
        using (var walCommand = connection.CreateCommand())
        {
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            walCommand.ExecuteNonQuery();
        }

        SqliteMigrationRunner.Apply(connection, DriveDatabaseMigrations.All);
    }

    public Task<List<DriveItem>> GetCachedItemsAsync(string parentPath)
        => SqliteOffThread.RunAsync<List<DriveItem>>(async () =>
    {
        var items = new List<DriveItem>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Path, Name, IsFolder, Size, ModifiedAt, Owner, IsShared, NodeId, ContentHash FROM DriveItems WHERE ParentPath = @ParentPath";
        command.Parameters.AddWithValue("@ParentPath", parentPath);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new DriveItem(
                Path: reader.GetString(0),
                Name: reader.GetString(1),
                IsFolder: reader.GetInt32(2) != 0,
                Size: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ModifiedAt: ParseModifiedAt(reader.IsDBNull(4) ? null : reader.GetString(4)),
                Owner: reader.IsDBNull(5) ? null : reader.GetString(5),
                IsShared: reader.GetInt32(6) != 0,
                NodeId: reader.IsDBNull(7) ? null : reader.GetString(7),
                ContentHash: reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }
        return items;
    });

    public Task SyncItemsAsync(string parentPath, IReadOnlyList<DriveItem> remoteItems)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            // Remove items that are in DB but not in remoteItems. Read on the same connection
            // and transaction as the writes below — the previous version opened a second
            // connection here via GetCachedItemsAsync, which is a SQLITE_BUSY risk once
            // anything else (e.g. the sync engine) reads concurrently.
            var cachedPaths = new List<string>();
            var selectCmd = connection.CreateCommand();
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = "SELECT Path FROM DriveItems WHERE ParentPath = @ParentPath";
            selectCmd.Parameters.AddWithValue("@ParentPath", parentPath);
            using (var reader = await selectCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    cachedPaths.Add(reader.GetString(0));
                }
            }

            var remotePaths = new HashSet<string>(remoteItems.Select(i => i.Path));
            foreach (var cachedPath in cachedPaths)
            {
                if (!remotePaths.Contains(cachedPath))
                {
                    var deleteCmd = connection.CreateCommand();
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM DriveItems WHERE Path = @Path";
                    deleteCmd.Parameters.AddWithValue("@Path", cachedPath);
                    await deleteCmd.ExecuteNonQueryAsync();
                }
            }

            // Insert or Update remote items
            foreach (var item in remoteItems)
            {
                var upsertCmd = connection.CreateCommand();
                upsertCmd.Transaction = transaction;
                upsertCmd.CommandText = UpsertSql;
                BindUpsertParameters(upsertCmd, parentPath, item);
                await upsertCmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    });

    public Task RemoveItemAsync(string path)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DriveItems WHERE Path = @Path OR Path LIKE @PathPrefix";
        command.Parameters.AddWithValue("@Path", path);
        command.Parameters.AddWithValue("@PathPrefix", path + "/%");
        await command.ExecuteNonQueryAsync();
    });

    public Task AddOrUpdateItemAsync(string parentPath, DriveItem item)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        BindUpsertParameters(command, parentPath, item);
        await command.ExecuteNonQueryAsync();
    });

    private const string UpsertSql = """
        INSERT INTO DriveItems (Path, ParentPath, Name, IsFolder, Size, ModifiedAt, Owner, IsShared, NodeId, ContentHash)
        VALUES (@Path, @ParentPath, @Name, @IsFolder, @Size, @ModifiedAt, @Owner, @IsShared, @NodeId, @ContentHash)
        ON CONFLICT(Path) DO UPDATE SET
            ParentPath = excluded.ParentPath,
            Name = excluded.Name,
            IsFolder = excluded.IsFolder,
            Size = excluded.Size,
            ModifiedAt = excluded.ModifiedAt,
            Owner = excluded.Owner,
            IsShared = excluded.IsShared,
            NodeId = excluded.NodeId,
            ContentHash = excluded.ContentHash;
        """;

    private static void BindUpsertParameters(SqliteCommand command, string parentPath, DriveItem item)
    {
        command.Parameters.AddWithValue("@Path", item.Path);
        command.Parameters.AddWithValue("@ParentPath", parentPath);
        command.Parameters.AddWithValue("@Name", item.Name);
        command.Parameters.AddWithValue("@IsFolder", item.IsFolder ? 1 : 0);
        command.Parameters.AddWithValue("@Size", (object?)item.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("@ModifiedAt", (object?)FormatModifiedAt(item.ModifiedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("@Owner", (object?)item.Owner ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsShared", item.IsShared ? 1 : 0);
        command.Parameters.AddWithValue("@NodeId", (object?)item.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ContentHash", (object?)item.ContentHash ?? DBNull.Value);
    }

    private static string? FormatModifiedAt(DateTimeOffset? modifiedAt)
        => modifiedAt?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Tolerant on purpose: a cache row written before this column's format was ISO-8601
    /// round-trip (or corrupted some other way) should degrade to "unknown," never throw and
    /// break loading the whole folder.
    /// </summary>
    private static DateTimeOffset? ParseModifiedAt(string? value)
        => value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
