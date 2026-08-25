using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyPersonalDrive.Models;

namespace MyPersonalDrive.Services;

/// <summary>
/// Persistence for recursive folder metrics (docs/PLAN-BROWSER-VIEWS.md M4). Deep, complete results
/// only — see the migration comment for why shallow ones aren't stored.
///
/// Follows the same rules as <see cref="DriveCacheService"/>: the shared migration list, work off
/// the caller's thread via <see cref="SqliteOffThread"/>, parameterized commands, and tolerant
/// parsing on read — a row written by an older format must degrade to "no metric", never throw and
/// take the folder listing down with it.
/// </summary>
public sealed class FolderMetricsStore
{
    private readonly string _connectionString;

    public FolderMetricsStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = SqliteOffThread.ConnectionStringFor(dbPath);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqliteMigrationRunner.Apply(connection, DriveDatabaseMigrations.All);
    }

    public Task SaveAsync(FolderMetrics metrics, CancellationToken cancellationToken = default)
    {
        if (!metrics.IsDeep || !metrics.IsComplete)
        {
            // A partial scan is shown, never stored: persisted, it would be indistinguishable from
            // a finished one on the next launch and would under-report forever.
            return Task.CompletedTask;
        }

        return SqliteOffThread.RunAsync(async () =>
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO FolderMetrics (Path, FileCount, FolderCount, TotalSize, UnknownSizeCount,
                                           ScannedFolderCount, NewestModifiedAt, OldestModifiedAt,
                                           BucketsJson, ComputedAt)
                VALUES (@Path, @FileCount, @FolderCount, @TotalSize, @UnknownSizeCount,
                        @ScannedFolderCount, @NewestModifiedAt, @OldestModifiedAt,
                        @BucketsJson, @ComputedAt)
                ON CONFLICT(Path) DO UPDATE SET
                    FileCount = excluded.FileCount,
                    FolderCount = excluded.FolderCount,
                    TotalSize = excluded.TotalSize,
                    UnknownSizeCount = excluded.UnknownSizeCount,
                    ScannedFolderCount = excluded.ScannedFolderCount,
                    NewestModifiedAt = excluded.NewestModifiedAt,
                    OldestModifiedAt = excluded.OldestModifiedAt,
                    BucketsJson = excluded.BucketsJson,
                    ComputedAt = excluded.ComputedAt;
                """;
            command.Parameters.AddWithValue("@Path", metrics.Path);
            command.Parameters.AddWithValue("@FileCount", metrics.FileCount);
            command.Parameters.AddWithValue("@FolderCount", metrics.FolderCount);
            command.Parameters.AddWithValue("@TotalSize", metrics.TotalSize);
            command.Parameters.AddWithValue("@UnknownSizeCount", metrics.UnknownSizeCount);
            command.Parameters.AddWithValue("@ScannedFolderCount", metrics.ScannedFolderCount);
            command.Parameters.AddWithValue("@NewestModifiedAt", (object?)Format(metrics.NewestModifiedAt) ?? DBNull.Value);
            command.Parameters.AddWithValue("@OldestModifiedAt", (object?)Format(metrics.OldestModifiedAt) ?? DBNull.Value);
            command.Parameters.AddWithValue("@BucketsJson", JsonSerializer.Serialize(metrics.Buckets.ToList(), AppJsonContext.Default.ListFolderKindBucket));
            command.Parameters.AddWithValue("@ComputedAt", Format(metrics.ComputedAt)!);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task<FolderMetrics?> GetAsync(string path, CancellationToken cancellationToken = default)
        => SqliteOffThread.RunAsync<FolderMetrics?>(async () =>
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE Path = @Path";
        command.Parameters.AddWithValue("@Path", path);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }, cancellationToken);

    /// <summary>
    /// The stored metric for each of <paramref name="paths"/> that has one. One query for the whole
    /// listing rather than one per row: a folder with 200 subfolders would otherwise open 200
    /// connections while the user waits for the rows to paint.
    /// </summary>
    public Task<Dictionary<string, FolderMetrics>> GetManyAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return Task.FromResult(new Dictionary<string, FolderMetrics>());
        }

        return SqliteOffThread.RunAsync(async () =>
        {
            var result = new Dictionary<string, FolderMetrics>();
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();

            var parameterNames = new List<string>(paths.Count);
            for (var i = 0; i < paths.Count; i++)
            {
                var name = $"@p{i}";
                parameterNames.Add(name);
                command.Parameters.AddWithValue(name, paths[i]);
            }

            command.CommandText = $"{SelectSql} WHERE Path IN ({string.Join(", ", parameterNames)})";

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var metrics = Read(reader);
                if (metrics is not null)
                {
                    result[metrics.Path] = metrics;
                }
            }

            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// Drops the metric for <paramref name="path"/> and for every folder that contains it. Both
    /// directions matter: deleting a 2 GB file invalidates the totals of every ancestor, and moving
    /// or trashing a folder invalidates the metrics of everything inside it too.
    /// </summary>
    public Task InvalidateForChangeAtAsync(string path, CancellationToken cancellationToken = default)
        => SqliteOffThread.RunAsync(async () =>
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM FolderMetrics
            WHERE Path = @Path
               OR Path LIKE @Descendants
               OR @Path LIKE Path || '/%';
            """;
        command.Parameters.AddWithValue("@Path", path);
        command.Parameters.AddWithValue("@Descendants", path + "/%");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken);

    private const string SelectSql = """
        SELECT Path, FileCount, FolderCount, TotalSize, UnknownSizeCount, ScannedFolderCount,
               NewestModifiedAt, OldestModifiedAt, BucketsJson, ComputedAt
        FROM FolderMetrics
        """;

    private static FolderMetrics? Read(SqliteDataReader reader)
    {
        var computedAt = Parse(reader.GetString(9));
        if (computedAt is null)
        {
            // Without a timestamp the UI cannot say how old the number is, and an undated total the
            // user can't judge is worse than no total. Treat the row as absent.
            return null;
        }

        List<FolderKindBucket> buckets;
        try
        {
            buckets = JsonSerializer.Deserialize(reader.GetString(8), AppJsonContext.Default.ListFolderKindBucket) ?? [];
        }
        catch (JsonException)
        {
            buckets = [];
        }

        return new FolderMetrics(
            Path: reader.GetString(0),
            IsDeep: true,
            IsComplete: true,
            FileCount: reader.GetInt32(1),
            FolderCount: reader.GetInt32(2),
            TotalSize: reader.GetInt64(3),
            UnknownSizeCount: reader.GetInt32(4),
            Buckets: buckets,
            // Not stored: the top-5 would need its own table, and a name/size pair goes stale the
            // moment anything in the subtree changes. A reopened metric shows the totals it can
            // still vouch for.
            LargestItems: [],
            NewestModifiedAt: reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
            OldestModifiedAt: reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
            ScannedFolderCount: reader.GetInt32(5),
            ComputedAt: computedAt.Value);
    }

    private static string? Format(DateTimeOffset? value)
        => value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? value)
        => value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
