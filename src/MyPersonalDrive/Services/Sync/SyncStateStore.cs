using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services.Sync;

/// <summary>
/// CRUD for the four sync tables (<c>SyncPairs</c>, <c>SyncState</c>, <c>SyncQueue</c>,
/// <c>SyncLog</c>) from docs/PLAN-LOCAL-SYNC.md §3.1. Applies the same
/// <see cref="DriveDatabaseMigrations"/> as <see cref="DriveCacheService"/> so it can be
/// constructed independently (own db file in tests; same file, after
/// <see cref="DriveCacheService"/>, in the composition root) without either class needing to
/// know about the other.
/// </summary>
public sealed class SyncStateStore
{
    private readonly string _connectionString;

    public SyncStateStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";

        using var connection = OpenConnection();
        SqliteMigrationRunner.Apply(connection, DriveDatabaseMigrations.All);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        // SQLite doesn't enforce declared foreign keys (SyncState/SyncQueue -> SyncPairs)
        // unless this is set per-connection; without it, ON DELETE CASCADE is a no-op.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    // ---------------------------------------------------------------- SyncPairs

    public async Task<SyncPair> CreatePairAsync(
        string remotePath, string localPath, SyncDirection direction, ConflictPolicy conflictPolicy,
        IReadOnlyList<string>? excludeGlobs = null, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncPairs (RemotePath, LocalPath, Direction, ConflictPolicy, ExcludeGlobs, LastSyncStatus)
            VALUES (@RemotePath, @LocalPath, @Direction, @ConflictPolicy, @ExcludeGlobs, 'Never');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@RemotePath", remotePath);
        command.Parameters.AddWithValue("@LocalPath", localPath);
        command.Parameters.AddWithValue("@Direction", direction.ToString());
        command.Parameters.AddWithValue("@ConflictPolicy", conflictPolicy.ToString());
        command.Parameters.AddWithValue("@ExcludeGlobs", (object?)JoinGlobs(excludeGlobs) ?? DBNull.Value);
        var id = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

        return new SyncPair(id, remotePath, localPath, direction, conflictPolicy, IsEnabled: true, IsPaused: false,
            excludeGlobs ?? [], LastSyncAt: null, SyncPairStatus.Never, LastError: null);
    }

    public async Task<IReadOnlyList<SyncPair>> GetPairsAsync(CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, RemotePath, LocalPath, Direction, ConflictPolicy, IsEnabled, IsPaused, ExcludeGlobs, LastSyncAt, LastSyncStatus, LastError FROM SyncPairs ORDER BY Id";
        var pairs = new List<SyncPair>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pairs.Add(ReadPair(reader));
        }

        return pairs;
    }

    public async Task<SyncPair?> GetPairAsync(int id, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, RemotePath, LocalPath, Direction, ConflictPolicy, IsEnabled, IsPaused, ExcludeGlobs, LastSyncAt, LastSyncStatus, LastError FROM SyncPairs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPair(reader) : null;
    }

    public async Task UpdatePairStatusAsync(int id, DateTimeOffset syncedAt, SyncPairStatus status, string? error, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncPairs SET LastSyncAt = @LastSyncAt, LastSyncStatus = @Status, LastError = @Error WHERE Id = @Id";
        command.Parameters.AddWithValue("@LastSyncAt", FormatTimestamp(syncedAt));
        command.Parameters.AddWithValue("@Status", status.ToString());
        command.Parameters.AddWithValue("@Error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetPairEnabledAsync(int id, bool isEnabled, CancellationToken ct = default)
        => await SetPairFlagAsync(id, "IsEnabled", isEnabled, ct);

    public async Task SetPairPausedAsync(int id, bool isPaused, CancellationToken ct = default)
        => await SetPairFlagAsync(id, "IsPaused", isPaused, ct);

    private async Task SetPairFlagAsync(int id, string column, bool value, CancellationToken ct)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        // `column` is one of two hardcoded literals above, never user input.
        command.CommandText = $"UPDATE SyncPairs SET {column} = @Value WHERE Id = @Id";
        command.Parameters.AddWithValue("@Value", value ? 1 : 0);
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeletePairAsync(int id, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        // SyncState/SyncQueue rows cascade via the FK declared in DriveDatabaseMigrations,
        // now that OpenConnection() turns PRAGMA foreign_keys on.
        command.CommandText = "DELETE FROM SyncPairs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static SyncPair ReadPair(SqliteDataReader reader)
        => new(
            Id: reader.GetInt32(0),
            RemotePath: reader.GetString(1),
            LocalPath: reader.GetString(2),
            Direction: Enum.Parse<SyncDirection>(reader.GetString(3)),
            ConflictPolicy: Enum.Parse<ConflictPolicy>(reader.GetString(4)),
            IsEnabled: reader.GetInt32(5) != 0,
            IsPaused: reader.GetInt32(6) != 0,
            ExcludeGlobs: SplitGlobs(reader.IsDBNull(7) ? null : reader.GetString(7)),
            LastSyncAt: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
            LastStatus: Enum.Parse<SyncPairStatus>(reader.GetString(9)),
            LastError: reader.IsDBNull(10) ? null : reader.GetString(10));

    private static string? JoinGlobs(IReadOnlyList<string>? globs)
        => globs is null || globs.Count == 0 ? null : string.Join('\n', globs);

    private static IReadOnlyList<string> SplitGlobs(string? joined)
        => string.IsNullOrEmpty(joined) ? [] : joined.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ---------------------------------------------------------------- SyncState (baseline)

    public async Task<IReadOnlyDictionary<string, SyncBaselineEntry>> GetBaselineAsync(int pairId, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RelativePath, IsFolder, RemoteSize, RemoteModifiedAt, RemoteNodeId, RemoteHash,
                   LocalSize, LocalModifiedAt, LocalInode, ContentHash
            FROM SyncState WHERE PairId = @PairId
            """;
        command.Parameters.AddWithValue("@PairId", pairId);

        var result = new Dictionary<string, SyncBaselineEntry>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var relativePath = reader.GetString(0);
            var isFolder = reader.GetInt32(1) != 0;

            NodeFingerprint? remoteAtSync = reader.IsDBNull(4) && reader.IsDBNull(2) && reader.IsDBNull(3) && reader.IsDBNull(5)
                ? null
                : new NodeFingerprint(relativePath, isFolder,
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));

            NodeFingerprint? localAtSync = reader.IsDBNull(6) && reader.IsDBNull(7) && reader.IsDBNull(8) && reader.IsDBNull(9)
                ? null
                : new NodeFingerprint(relativePath, isFolder,
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9));

            result[relativePath] = new SyncBaselineEntry(relativePath, isFolder, localAtSync, remoteAtSync);
        }

        return result;
    }

    public async Task UpsertBaselineAsync(int pairId, SyncBaselineEntry entry, DateTimeOffset syncedAt, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncState (PairId, RelativePath, IsFolder, RemoteSize, RemoteModifiedAt, RemoteNodeId, RemoteHash,
                                    LocalSize, LocalModifiedAt, LocalInode, ContentHash, SyncedAt)
            VALUES (@PairId, @RelativePath, @IsFolder, @RemoteSize, @RemoteModifiedAt, @RemoteNodeId, @RemoteHash,
                    @LocalSize, @LocalModifiedAt, @LocalInode, @ContentHash, @SyncedAt)
            ON CONFLICT(PairId, RelativePath) DO UPDATE SET
                IsFolder = excluded.IsFolder,
                RemoteSize = excluded.RemoteSize,
                RemoteModifiedAt = excluded.RemoteModifiedAt,
                RemoteNodeId = excluded.RemoteNodeId,
                RemoteHash = excluded.RemoteHash,
                LocalSize = excluded.LocalSize,
                LocalModifiedAt = excluded.LocalModifiedAt,
                LocalInode = excluded.LocalInode,
                ContentHash = excluded.ContentHash,
                SyncedAt = excluded.SyncedAt;
            """;
        command.Parameters.AddWithValue("@PairId", pairId);
        command.Parameters.AddWithValue("@RelativePath", entry.RelativePath);
        command.Parameters.AddWithValue("@IsFolder", entry.IsFolder ? 1 : 0);
        command.Parameters.AddWithValue("@RemoteSize", (object?)entry.RemoteAtSync?.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("@RemoteModifiedAt", (object?)FormatTimestamp(entry.RemoteAtSync?.ModifiedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("@RemoteNodeId", (object?)entry.RemoteAtSync?.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@RemoteHash", (object?)entry.RemoteAtSync?.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@LocalSize", (object?)entry.LocalAtSync?.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("@LocalModifiedAt", (object?)FormatTimestamp(entry.LocalAtSync?.ModifiedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("@LocalInode", (object?)entry.LocalAtSync?.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ContentHash", (object?)entry.LocalAtSync?.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@SyncedAt", FormatTimestamp(syncedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveBaselineAsync(int pairId, string relativePath, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncState WHERE PairId = @PairId AND RelativePath = @RelativePath";
        command.Parameters.AddWithValue("@PairId", pairId);
        command.Parameters.AddWithValue("@RelativePath", relativePath);
        await command.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- SyncQueue

    public async Task EnqueueActionsAsync(int pairId, IReadOnlyList<SyncAction> actions, DateTimeOffset enqueuedAt, CancellationToken ct = default)
    {
        if (actions.Count == 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var action in actions)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO SyncQueue (PairId, RelativePath, Operation, Payload, Priority, State, EnqueuedAt)
                    VALUES (@PairId, @RelativePath, @Operation, @Payload, @Priority, 'Pending', @EnqueuedAt);
                    """;
                command.Parameters.AddWithValue("@PairId", pairId);
                command.Parameters.AddWithValue("@RelativePath", action.RelativePath);
                command.Parameters.AddWithValue("@Operation", action.Operation.ToString());
                command.Parameters.AddWithValue("@Payload", (object?)SerializePayload(action) ?? DBNull.Value);
                command.Parameters.AddWithValue("@Priority", action.Priority);
                command.Parameters.AddWithValue("@EnqueuedAt", FormatTimestamp(enqueuedAt));
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<QueuedSyncAction>> GetPendingActionsAsync(int pairId, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, PairId, RelativePath, Operation, Payload, Priority, AttemptCount, State, LastError, EnqueuedAt
            FROM SyncQueue WHERE PairId = @PairId AND State = 'Pending'
            ORDER BY Priority, Id
            """;
        command.Parameters.AddWithValue("@PairId", pairId);

        var actions = new List<QueuedSyncAction>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            actions.Add(ReadQueuedAction(reader));
        }

        return actions;
    }

    public async Task MarkRunningAsync(long queueId, CancellationToken ct = default)
        => await UpdateQueueStateAsync(queueId, SyncQueueState.Running, ct);

    public async Task MarkDoneAsync(long queueId, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncQueue SET State = 'Done', CompletedAt = @CompletedAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@CompletedAt", FormatTimestamp(completedAt));
        command.Parameters.AddWithValue("@Id", queueId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(long queueId, string error, DateTimeOffset? nextAttemptAt, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SyncQueue
            SET State = CASE WHEN @NextAttemptAt IS NULL THEN 'Failed' ELSE 'Pending' END,
                LastError = @Error,
                AttemptCount = AttemptCount + 1,
                NextAttemptAt = @NextAttemptAt
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Error", error);
        command.Parameters.AddWithValue("@NextAttemptAt", (object?)FormatTimestamp(nextAttemptAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("@Id", queueId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Crash safety per docs/PLAN-LOCAL-SYNC.md §7: a row left 'Running' means the app was
    /// killed mid-transfer, not that the transfer is stuck — call this once at startup, before
    /// anything else touches the queue.
    /// </summary>
    public async Task ResetRunningToPendingAsync(CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncQueue SET State = 'Pending' WHERE State = 'Running'";
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateQueueStateAsync(long queueId, SyncQueueState state, CancellationToken ct)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncQueue SET State = @State WHERE Id = @Id";
        command.Parameters.AddWithValue("@State", state.ToString());
        command.Parameters.AddWithValue("@Id", queueId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static QueuedSyncAction ReadQueuedAction(SqliteDataReader reader)
    {
        var payload = DeserializePayload(reader.IsDBNull(4) ? null : reader.GetString(4));
        return new QueuedSyncAction(
            Id: reader.GetInt64(0),
            PairId: reader.GetInt32(1),
            RelativePath: reader.GetString(2),
            Operation: Enum.Parse<SyncOperation>(reader.GetString(3)),
            SecondaryPath: payload?.SecondaryPath,
            Bytes: payload?.Bytes,
            Priority: reader.GetInt32(5),
            AttemptCount: reader.GetInt32(6),
            State: Enum.Parse<SyncQueueState>(reader.GetString(7)),
            LastError: reader.IsDBNull(8) ? null : reader.GetString(8),
            EnqueuedAt: ParseTimestamp(reader.GetString(9)) ?? default);
    }

    private static string? SerializePayload(SyncAction action)
    {
        if (action.SecondaryPath is null && action.Bytes is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(new SyncActionPayload { SecondaryPath = action.SecondaryPath, Bytes = action.Bytes }, AppJsonContext.Default.SyncActionPayload);
    }

    private static SyncActionPayload? DeserializePayload(string? json)
        => json is null ? null : JsonSerializer.Deserialize(json, AppJsonContext.Default.SyncActionPayload);

    // ---------------------------------------------------------------- SyncLog

    public async Task LogAsync(int? pairId, SyncLogLevel level, string? relativePath, string message, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncLog (PairId, Timestamp, Level, RelativePath, Message)
            VALUES (@PairId, @Timestamp, @Level, @RelativePath, @Message);
            """;
        command.Parameters.AddWithValue("@PairId", (object?)pairId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Timestamp", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("@Level", level.ToString());
        command.Parameters.AddWithValue("@RelativePath", (object?)relativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("@Message", message);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SyncLogEntry>> GetRecentLogsAsync(int? pairId, int limit, CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = pairId is null
            ? "SELECT Id, PairId, Timestamp, Level, RelativePath, Message FROM SyncLog ORDER BY Id DESC LIMIT @Limit"
            : "SELECT Id, PairId, Timestamp, Level, RelativePath, Message FROM SyncLog WHERE PairId = @PairId ORDER BY Id DESC LIMIT @Limit";
        if (pairId is not null)
        {
            command.Parameters.AddWithValue("@PairId", pairId.Value);
        }

        command.Parameters.AddWithValue("@Limit", limit);

        var entries = new List<SyncLogEntry>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new SyncLogEntry(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                ParseTimestamp(reader.GetString(2)) ?? default,
                Enum.Parse<SyncLogLevel>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }

        return entries;
    }

    // ---------------------------------------------------------------- Timestamp helpers

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatTimestamp(DateTimeOffset? timestamp)
        => timestamp?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTimestamp(string? value)
        => value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
