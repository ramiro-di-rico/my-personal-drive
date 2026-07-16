using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MyPersonalDrive.Services;

public class DriveCacheService
{
    private readonly string _connectionString;

    public DriveCacheService(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = 
        @"
            CREATE TABLE IF NOT EXISTS DriveItems (
                Path TEXT PRIMARY KEY,
                ParentPath TEXT,
                Name TEXT,
                IsFolder INTEGER,
                Size INTEGER,
                ModifiedAt TEXT,
                Owner TEXT,
                IsShared INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_ParentPath ON DriveItems(ParentPath);
        ";
        command.ExecuteNonQuery();
    }

    public async Task<List<DriveItem>> GetCachedItemsAsync(string parentPath)
    {
        var items = new List<DriveItem>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Path, Name, IsFolder, Size, ModifiedAt, Owner, IsShared FROM DriveItems WHERE ParentPath = @ParentPath";
        command.Parameters.AddWithValue("@ParentPath", parentPath);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new DriveItem(
                Path: reader.GetString(0),
                Name: reader.GetString(1),
                IsFolder: reader.GetInt32(2) != 0,
                Size: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ModifiedAt: reader.IsDBNull(4) ? null : reader.GetString(4),
                Owner: reader.IsDBNull(5) ? null : reader.GetString(5),
                IsShared: reader.GetInt32(6) != 0
            ));
        }
        return items;
    }

    public async Task SyncItemsAsync(string parentPath, IReadOnlyList<DriveItem> remoteItems)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            // Remove items that are in DB but not in remoteItems
            var remotePaths = new HashSet<string>(remoteItems.Select(i => i.Path));
            var cachedItems = await GetCachedItemsAsync(parentPath);
            foreach (var cached in cachedItems)
            {
                if (!remotePaths.Contains(cached.Path))
                {
                    var deleteCmd = connection.CreateCommand();
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM DriveItems WHERE Path = @Path";
                    deleteCmd.Parameters.AddWithValue("@Path", cached.Path);
                    await deleteCmd.ExecuteNonQueryAsync();
                }
            }

            // Insert or Update remote items
            foreach (var item in remoteItems)
            {
                var upsertCmd = connection.CreateCommand();
                upsertCmd.Transaction = transaction;
                upsertCmd.CommandText = 
                @"
                    INSERT INTO DriveItems (Path, ParentPath, Name, IsFolder, Size, ModifiedAt, Owner, IsShared)
                    VALUES (@Path, @ParentPath, @Name, @IsFolder, @Size, @ModifiedAt, @Owner, @IsShared)
                    ON CONFLICT(Path) DO UPDATE SET
                        ParentPath = excluded.ParentPath,
                        Name = excluded.Name,
                        IsFolder = excluded.IsFolder,
                        Size = excluded.Size,
                        ModifiedAt = excluded.ModifiedAt,
                        Owner = excluded.Owner,
                        IsShared = excluded.IsShared;
                ";
                upsertCmd.Parameters.AddWithValue("@Path", item.Path);
                upsertCmd.Parameters.AddWithValue("@ParentPath", parentPath);
                upsertCmd.Parameters.AddWithValue("@Name", item.Name);
                upsertCmd.Parameters.AddWithValue("@IsFolder", item.IsFolder ? 1 : 0);
                upsertCmd.Parameters.AddWithValue("@Size", (object?)item.Size ?? System.DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@ModifiedAt", (object?)item.ModifiedAt ?? System.DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@Owner", (object?)item.Owner ?? System.DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@IsShared", item.IsShared ? 1 : 0);
                await upsertCmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task RemoveItemAsync(string path)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DriveItems WHERE Path = @Path OR Path LIKE @PathPrefix";
        command.Parameters.AddWithValue("@Path", path);
        command.Parameters.AddWithValue("@PathPrefix", path + "/%");
        await command.ExecuteNonQueryAsync();
    }

    public async Task AddOrUpdateItemAsync(string parentPath, DriveItem item)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = 
        @"
            INSERT INTO DriveItems (Path, ParentPath, Name, IsFolder, Size, ModifiedAt, Owner, IsShared)
            VALUES (@Path, @ParentPath, @Name, @IsFolder, @Size, @ModifiedAt, @Owner, @IsShared)
            ON CONFLICT(Path) DO UPDATE SET
                ParentPath = excluded.ParentPath,
                Name = excluded.Name,
                IsFolder = excluded.IsFolder,
                Size = excluded.Size,
                ModifiedAt = excluded.ModifiedAt,
                Owner = excluded.Owner,
                IsShared = excluded.IsShared;
        ";
        command.Parameters.AddWithValue("@Path", item.Path);
        command.Parameters.AddWithValue("@ParentPath", parentPath);
        command.Parameters.AddWithValue("@Name", item.Name);
        command.Parameters.AddWithValue("@IsFolder", item.IsFolder ? 1 : 0);
        command.Parameters.AddWithValue("@Size", (object?)item.Size ?? System.DBNull.Value);
        command.Parameters.AddWithValue("@ModifiedAt", (object?)item.ModifiedAt ?? System.DBNull.Value);
        command.Parameters.AddWithValue("@Owner", (object?)item.Owner ?? System.DBNull.Value);
        command.Parameters.AddWithValue("@IsShared", item.IsShared ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }
}
