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
        _connectionString = SqliteOffThread.ConnectionStringFor(dbPath);

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

    // ---------------------------------------------------------------- AppSettings

    private const string AutomaticSyncEnabledKey = "AutomaticSyncEnabled";

    /// <summary>
    /// Whether the automatic sync loop should be running. Defaults to true for a database that
    /// has never recorded a choice, so existing users keep the behaviour they had before this
    /// setting existed.
    /// </summary>
    public async Task<bool> GetAutomaticSyncEnabledAsync(CancellationToken ct = default)
        => await GetSettingAsync(AutomaticSyncEnabledKey, ct) is not "0";

    public async Task SetAutomaticSyncEnabledAsync(bool isEnabled, CancellationToken ct = default)
        => await SetSettingAsync(AutomaticSyncEnabledKey, isEnabled ? "1" : "0", ct);

    private Task<string?> GetSettingAsync(string key, CancellationToken ct)
        => SqliteOffThread.RunAsync<string?>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSettings WHERE Key = @Key";
        command.Parameters.AddWithValue("@Key", key);
        return await command.ExecuteScalarAsync(ct) as string;
    });

    private Task SetSettingAsync(string key, string value, CancellationToken ct)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("@Key", key);
        command.Parameters.AddWithValue("@Value", value);
        await command.ExecuteNonQueryAsync(ct);
    });

    // ---------------------------------------------------------------- SyncPairs

    public Task<SyncPair> CreatePairAsync(
        string remotePath, string localPath, SyncDirection direction, ConflictPolicy conflictPolicy,
        IReadOnlyList<string>? excludeGlobs = null, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<SyncPair>(async () =>
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
    });

    public Task<IReadOnlyList<SyncPair>> GetPairsAsync(CancellationToken ct = default)
        => SqliteOffThread.RunAsync<IReadOnlyList<SyncPair>>(async () =>
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
    });

    public Task<SyncPair?> GetPairAsync(int id, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<SyncPair?>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, RemotePath, LocalPath, Direction, ConflictPolicy, IsEnabled, IsPaused, ExcludeGlobs, LastSyncAt, LastSyncStatus, LastError FROM SyncPairs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPair(reader) : null;
    });

    public Task UpdatePairStatusAsync(int id, DateTimeOffset syncedAt, SyncPairStatus status, string? error, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncPairs SET LastSyncAt = @LastSyncAt, LastSyncStatus = @Status, LastError = @Error WHERE Id = @Id";
        command.Parameters.AddWithValue("@LastSyncAt", FormatTimestamp(syncedAt));
        command.Parameters.AddWithValue("@Status", status.ToString());
        command.Parameters.AddWithValue("@Error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync(ct);
    });

    public async Task SetPairEnabledAsync(int id, bool isEnabled, CancellationToken ct = default)
        => await SetPairFlagAsync(id, "IsEnabled", isEnabled, ct);

    public async Task SetPairPausedAsync(int id, bool isPaused, CancellationToken ct = default)
        => await SetPairFlagAsync(id, "IsPaused", isPaused, ct);

    private Task SetPairFlagAsync(int id, string column, bool value, CancellationToken ct)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        // `column` is one of two hardcoded literals above, never user input.
        command.CommandText = $"UPDATE SyncPairs SET {column} = @Value WHERE Id = @Id";
        command.Parameters.AddWithValue("@Value", value ? 1 : 0);
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync(ct);
    });

    public Task DeletePairAsync(int id, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        // SyncState/SyncQueue rows cascade via the FK declared in DriveDatabaseMigrations,
        // now that OpenConnection() turns PRAGMA foreign_keys on.
        command.CommandText = "DELETE FROM SyncPairs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync(ct);
    });

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

    public Task<IReadOnlyDictionary<string, SyncBaselineEntry>> GetBaselineAsync(int pairId, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<IReadOnlyDictionary<string, SyncBaselineEntry>>(async () =>
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
    });

    public Task UpsertBaselineAsync(int pairId, SyncBaselineEntry entry, DateTimeOffset syncedAt, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
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
    });

    public Task RemoveBaselineAsync(int pairId, string relativePath, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncState WHERE PairId = @PairId AND RelativePath = @RelativePath";
        command.Parameters.AddWithValue("@PairId", pairId);
        command.Parameters.AddWithValue("@RelativePath", relativePath);
        await command.ExecuteNonQueryAsync(ct);
    });

    // ---------------------------------------------------------------- SyncQueue

    /// <summary>
    /// Enqueues a plan's actions, <b>at most one live row per (pair, path, operation)</b>.
    ///
    /// This used to insert blindly, which was quadratic and non-convergent once sync became
    /// automatic. A file that keeps failing is re-proposed by every run, so run N left N rows for
    /// the same path, each with its own fresh retry budget: measured CLI attempts went
    /// 1, 3, 6, 10, 15 — triangular — and the queue never drained. At F3's 5-minute cadence that is
    /// ~288 runs a day for one bad file.
    ///
    /// So each action either revives the existing terminal row or is skipped as already queued:
    /// <list type="bullet">
    /// <item>A row still <c>Pending</c>/<c>Running</c>/<c>Conflict</c> means the work is already
    /// scheduled — skip, and let its own retry budget play out.</item>
    /// <item>A <c>Failed</c> or <c>Skipped</c> row is revived with a clean attempt count. The plan
    /// re-proposed the action, so the difference still exists and whatever broke may have been
    /// fixed; this is also the only way a row that exhausted its retries recovers before F4's UI
    /// exists. Worst case for a permanently broken file is one attempt per cycle.</item>
    /// <item>A <c>Done</c> row never blocks: the action being proposed again means it is genuinely
    /// needed again (the file changed once more). Those rows are history, cleared by
    /// <see cref="PruneCompletedAsync"/>.</item>
    /// </list>
    /// </summary>
    public Task EnqueueActionsAsync(int pairId, IReadOnlyList<SyncAction> actions, DateTimeOffset enqueuedAt, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
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
                var revive = connection.CreateCommand();
                revive.Transaction = transaction;
                revive.CommandText = """
                    UPDATE SyncQueue
                    SET State = 'Pending', AttemptCount = 0, NextAttemptAt = NULL, LastError = NULL,
                        CompletedAt = NULL, EnqueuedAt = @EnqueuedAt, Payload = @Payload, Priority = @Priority
                    WHERE PairId = @PairId AND RelativePath = @RelativePath AND Operation = @Operation
                      AND State IN ('Failed', 'Skipped');
                    """;
                AddActionParameters(revive, pairId, action, enqueuedAt);
                await revive.ExecuteNonQueryAsync(ct);

                // Conditional insert: skipped when the revive above just made a row Pending, or
                // when one was already live.
                var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO SyncQueue (PairId, RelativePath, Operation, Payload, Priority, State, EnqueuedAt)
                    SELECT @PairId, @RelativePath, @Operation, @Payload, @Priority, 'Pending', @EnqueuedAt
                    WHERE NOT EXISTS (
                        SELECT 1 FROM SyncQueue
                        WHERE PairId = @PairId AND RelativePath = @RelativePath AND Operation = @Operation
                          AND State IN ('Pending', 'Running', 'Conflict')
                    );
                    """;
                AddActionParameters(insert, pairId, action, enqueuedAt);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    });

    private static void AddActionParameters(SqliteCommand command, int pairId, SyncAction action, DateTimeOffset enqueuedAt)
    {
        command.Parameters.AddWithValue("@PairId", pairId);
        command.Parameters.AddWithValue("@RelativePath", action.RelativePath);
        command.Parameters.AddWithValue("@Operation", action.Operation.ToString());
        command.Parameters.AddWithValue("@Payload", (object?)SerializePayload(action) ?? DBNull.Value);
        command.Parameters.AddWithValue("@Priority", action.Priority);
        command.Parameters.AddWithValue("@EnqueuedAt", FormatTimestamp(enqueuedAt));
    }

    /// <summary>
    /// Clears completed history. <c>Done</c> rows have no recovery value — the queue is durable so a
    /// crash can resume unfinished work, and finished work has nothing to resume — while
    /// <c>SyncLog</c> keeps the narrative. Without this they accumulate for every file of every
    /// automatic cycle, forever.
    /// </summary>
    public Task<int> PruneCompletedAsync(DateTimeOffset completedBefore, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<int>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncQueue WHERE State = 'Done' AND (CompletedAt IS NULL OR CompletedAt < @Before)";
        command.Parameters.AddWithValue("@Before", FormatTimestamp(completedBefore));
        return await command.ExecuteNonQueryAsync(ct);
    });

    /// <summary>
    /// Pending rows in execution order. Pass <paramref name="now"/> to honour the backoff set by
    /// <see cref="MarkFailedAsync"/> — a row waiting out its <c>NextAttemptAt</c> is still
    /// 'Pending' but must not be picked up yet. Omitting it returns every pending row regardless
    /// of backoff, which is what a "run everything now" caller wants.
    /// </summary>
    public Task<IReadOnlyList<QueuedSyncAction>> GetPendingActionsAsync(int pairId, DateTimeOffset? now = null, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<IReadOnlyList<QueuedSyncAction>>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = now is null
            ? """
              SELECT Id, PairId, RelativePath, Operation, Payload, Priority, AttemptCount, State, LastError, EnqueuedAt
              FROM SyncQueue WHERE PairId = @PairId AND State = 'Pending'
              ORDER BY Priority, Id
              """
            : """
              SELECT Id, PairId, RelativePath, Operation, Payload, Priority, AttemptCount, State, LastError, EnqueuedAt
              FROM SyncQueue
              WHERE PairId = @PairId AND State = 'Pending' AND (NextAttemptAt IS NULL OR NextAttemptAt <= @Now)
              ORDER BY Priority, Id
              """;
        command.Parameters.AddWithValue("@PairId", pairId);
        if (now is not null)
        {
            command.Parameters.AddWithValue("@Now", FormatTimestamp(now.Value));
        }

        var actions = new List<QueuedSyncAction>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            actions.Add(ReadQueuedAction(reader));
        }

        return actions;
    });

    /// <summary>
    /// Parks unresolved conflicts as 'Conflict' rows — docs/PLAN-LOCAL-SYNC.md §5.6's `Ask`
    /// policy, where the reconciler deliberately emits no action and the user decides later.
    /// They are never picked up by <see cref="GetPendingActionsAsync"/>; the conflicts panel
    /// (F4) reads them back via <see cref="GetConflictActionsAsync"/>.
    /// </summary>
    public Task EnqueueConflictsAsync(int pairId, IReadOnlyList<SyncConflict> conflicts, DateTimeOffset enqueuedAt, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
    {
        if (conflicts.Count == 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var conflict in conflicts)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                // One row per conflicting path, not one per run. An unresolved `Ask` conflict is
                // re-reported by every cycle — and since resolving them needs F4's panel, they
                // cannot be cleared yet, so a blind insert grew the queue every 5 minutes forever.
                command.CommandText = """
                    INSERT INTO SyncQueue (PairId, RelativePath, Operation, Priority, State, LastError, EnqueuedAt)
                    SELECT @PairId, @RelativePath, 'ResolveConflictKeepBoth', @Priority, 'Conflict', @Reason, @EnqueuedAt
                    WHERE NOT EXISTS (
                        SELECT 1 FROM SyncQueue
                        WHERE PairId = @PairId AND RelativePath = @RelativePath AND State = 'Conflict'
                    );
                    """;
                command.Parameters.AddWithValue("@PairId", pairId);
                command.Parameters.AddWithValue("@RelativePath", conflict.RelativePath);
                command.Parameters.AddWithValue("@Priority", 1000);
                command.Parameters.AddWithValue("@Reason", conflict.Reason.ToString());
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
    });

    /// <summary>
    /// Deletes parked conflicts that are no longer conflicts. Called with the paths the *current*
    /// reconciliation still finds in conflict, so a difference resolved by any means — the panel,
    /// the user editing files by hand, another client — stops being reported.
    ///
    /// Without this a `Conflict` row outlives the situation that created it forever: nothing else
    /// ever clears one, so the conflict count would only ever grow and would eventually be pure
    /// fiction.
    /// </summary>
    public Task<int> ClearStaleConflictsAsync(int pairId, IReadOnlyCollection<string> stillConflicting, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<int>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();

        if (stillConflicting.Count == 0)
        {
            command.CommandText = "DELETE FROM SyncQueue WHERE PairId = @PairId AND State = 'Conflict'";
            command.Parameters.AddWithValue("@PairId", pairId);
            return await command.ExecuteNonQueryAsync(ct);
        }

        // Parameterized IN list — never string-concatenated, since these are file names.
        var placeholders = stillConflicting.Select((_, index) => $"@p{index}").ToList();
        command.CommandText =
            $"DELETE FROM SyncQueue WHERE PairId = @PairId AND State = 'Conflict' AND RelativePath NOT IN ({string.Join(", ", placeholders)})";
        command.Parameters.AddWithValue("@PairId", pairId);
        foreach (var (path, index) in stillConflicting.Select((path, index) => (path, index)))
        {
            command.Parameters.AddWithValue($"@p{index}", path);
        }

        return await command.ExecuteNonQueryAsync(ct);
    });

    /// <summary>Marks a parked conflict as dealt with, once its resolution has actually been carried out.</summary>
    public Task MarkConflictResolvedAsync(long queueId, ConflictResolution resolution, DateTimeOffset resolvedAt, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SyncQueue
            SET State = 'Done', CompletedAt = @CompletedAt, LastError = @Resolution
            WHERE Id = @Id AND State = 'Conflict'
            """;
        command.Parameters.AddWithValue("@CompletedAt", FormatTimestamp(resolvedAt));
        command.Parameters.AddWithValue("@Resolution", $"Resolved: {resolution}");
        command.Parameters.AddWithValue("@Id", queueId);
        await command.ExecuteNonQueryAsync(ct);
    });

    /// <summary>
    /// Puts every permanently-failed row for a pair back in the queue with a clean slate — the
    /// "try that again" the UI needs for rows whose retries ran out. Returns how many were revived.
    /// </summary>
    public Task<int> RetryFailedAsync(int pairId, DateTimeOffset now, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<int>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SyncQueue
            SET State = 'Pending', AttemptCount = 0, NextAttemptAt = NULL, LastError = NULL, EnqueuedAt = @Now
            WHERE PairId = @PairId AND State = 'Failed'
            """;
        command.Parameters.AddWithValue("@PairId", pairId);
        command.Parameters.AddWithValue("@Now", FormatTimestamp(now));
        return await command.ExecuteNonQueryAsync(ct);
    });

    /// <summary>
    /// Deletes <c>Failed</c> rows the current plan no longer proposes. Mirrors
    /// <see cref="ClearStaleConflictsAsync"/>: <see cref="EnqueueActionsAsync"/> only revives a
    /// <c>Failed</c> row when the fresh plan re-proposes the exact same (path, operation) pair, so a
    /// failure whose difference disappeared by any other means — a manual fix, another client, the
    /// file simply being deleted — was never being cleared. It just sat in <c>SyncQueue</c> forever,
    /// which is what let the "Retry failed actions" badge disagree with a fresh preview that finds
    /// nothing to do.
    /// </summary>
    public Task<int> ClearStaleFailedActionsAsync(int pairId, IReadOnlyList<SyncAction> currentPlan, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<int>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();

        if (currentPlan.Count == 0)
        {
            command.CommandText = "DELETE FROM SyncQueue WHERE PairId = @PairId AND State = 'Failed'";
            command.Parameters.AddWithValue("@PairId", pairId);
            return await command.ExecuteNonQueryAsync(ct);
        }

        // Parameterized (path, operation) pairs — never string-concatenated, since these are file names.
        var clauses = new List<string>();
        for (var i = 0; i < currentPlan.Count; i++)
        {
            clauses.Add($"(RelativePath = @path{i} AND Operation = @op{i})");
        }

        command.CommandText =
            $"DELETE FROM SyncQueue WHERE PairId = @PairId AND State = 'Failed' AND NOT ({string.Join(" OR ", clauses)})";
        command.Parameters.AddWithValue("@PairId", pairId);
        for (var i = 0; i < currentPlan.Count; i++)
        {
            command.Parameters.AddWithValue($"@path{i}", currentPlan[i].RelativePath);
            command.Parameters.AddWithValue($"@op{i}", currentPlan[i].Operation.ToString());
        }

        return await command.ExecuteNonQueryAsync(ct);
    });

    public Task<IReadOnlyList<QueuedSyncAction>> GetFailedActionsAsync(int pairId, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<IReadOnlyList<QueuedSyncAction>>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, PairId, RelativePath, Operation, Payload, Priority, AttemptCount, State, LastError, EnqueuedAt
            FROM SyncQueue WHERE PairId = @PairId AND State = 'Failed'
            ORDER BY RelativePath
            """;
        command.Parameters.AddWithValue("@PairId", pairId);

        var actions = new List<QueuedSyncAction>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            actions.Add(ReadQueuedAction(reader));
        }

        return actions;
    });

    public Task<IReadOnlyList<QueuedSyncAction>> GetConflictActionsAsync(int pairId, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<IReadOnlyList<QueuedSyncAction>>(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, PairId, RelativePath, Operation, Payload, Priority, AttemptCount, State, LastError, EnqueuedAt
            FROM SyncQueue WHERE PairId = @PairId AND State = 'Conflict'
            ORDER BY RelativePath
            """;
        command.Parameters.AddWithValue("@PairId", pairId);

        var actions = new List<QueuedSyncAction>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            actions.Add(ReadQueuedAction(reader));
        }

        return actions;
    });

    public async Task MarkRunningAsync(long queueId, CancellationToken ct = default)
        => await UpdateQueueStateAsync(queueId, SyncQueueState.Running, ct);

    public Task MarkDoneAsync(long queueId, DateTimeOffset completedAt, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncQueue SET State = 'Done', CompletedAt = @CompletedAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@CompletedAt", FormatTimestamp(completedAt));
        command.Parameters.AddWithValue("@Id", queueId);
        await command.ExecuteNonQueryAsync(ct);
    });

    public Task MarkFailedAsync(long queueId, string error, DateTimeOffset? nextAttemptAt, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
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
    });

    /// <summary>
    /// Crash safety per docs/PLAN-LOCAL-SYNC.md §7: a row left 'Running' means the app was
    /// killed mid-transfer, not that the transfer is stuck — call this once at startup, before
    /// anything else touches the queue.
    /// </summary>
    public Task ResetRunningToPendingAsync(CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncQueue SET State = 'Pending' WHERE State = 'Running'";
        await command.ExecuteNonQueryAsync(ct);
    });

    private Task UpdateQueueStateAsync(long queueId, SyncQueueState state, CancellationToken ct)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncQueue SET State = @State WHERE Id = @Id";
        command.Parameters.AddWithValue("@State", state.ToString());
        command.Parameters.AddWithValue("@Id", queueId);
        await command.ExecuteNonQueryAsync(ct);
    });

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

    public Task LogAsync(int? pairId, SyncLogLevel level, string? relativePath, string message, DateTimeOffset timestamp, CancellationToken ct = default)
        => SqliteOffThread.RunAsync(async () =>
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
    });

    /// <summary>
    /// Bounds the log. Two limits, because either alone leaves a hole: an age limit doesn't stop a
    /// chatty pair producing an enormous log inside the retention window, and a count limit alone
    /// keeps stale noise around forever on a pair that has gone quiet.
    ///
    /// This matters more than it did when the log was only written by button presses. Automatic sync
    /// writes an <c>Info</c> row per action per cycle, so the table now grows with uptime rather than
    /// with use, and nothing else ever removed a row.
    ///
    /// The count limit is per pair, so one busy pair can't push another's history out — including
    /// the scheduler's own rows, which carry a null <c>PairId</c> and form their own group.
    /// </summary>
    public Task<int> PruneLogsAsync(DateTimeOffset olderThan, int maxPerPair, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<int>(async () =>
    {
        using var connection = OpenConnection();

        var byAge = connection.CreateCommand();
        byAge.CommandText = "DELETE FROM SyncLog WHERE Timestamp < @Before";
        byAge.Parameters.AddWithValue("@Before", FormatTimestamp(olderThan));
        var removed = await byAge.ExecuteNonQueryAsync(ct);

        var byCount = connection.CreateCommand();
        byCount.CommandText = """
            DELETE FROM SyncLog WHERE Id IN (
                SELECT Id FROM (
                    SELECT Id, ROW_NUMBER() OVER (PARTITION BY PairId ORDER BY Id DESC) AS RowNumber
                    FROM SyncLog
                )
                WHERE RowNumber > @MaxPerPair
            )
            """;
        byCount.Parameters.AddWithValue("@MaxPerPair", maxPerPair);
        removed += await byCount.ExecuteNonQueryAsync(ct);

        return removed;
    });

    public Task<IReadOnlyList<SyncLogEntry>> GetRecentLogsAsync(int? pairId, int limit, CancellationToken ct = default)
        => SqliteOffThread.RunAsync<IReadOnlyList<SyncLogEntry>>(async () =>
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
    });

    // ---------------------------------------------------------------- Timestamp helpers

    /// <summary>
    /// Always normalized to UTC before formatting, per docs/PLAN-LOCAL-SYNC.md §5.5's "always
    /// compare in UTC". Not just tidiness: `NextAttemptAt &lt;= @Now` in
    /// <see cref="GetPendingActionsAsync"/> is a *string* comparison, and round-tripping the
    /// caller's original offset would make `...T10:00:00-03:00` sort before
    /// `...T09:00:00+00:00` despite being the later instant. Normalizing makes the textual and
    /// chronological orders agree. Parsing is unaffected — the instant is preserved either way.
    /// </summary>
    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatTimestamp(DateTimeOffset? timestamp)
        => timestamp is null ? null : FormatTimestamp(timestamp.Value);

    private static DateTimeOffset? ParseTimestamp(string? value)
        => value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
