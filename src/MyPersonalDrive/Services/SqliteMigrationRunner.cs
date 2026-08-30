using Microsoft.Data.Sqlite;

namespace MyPersonalDrive.Services;

public sealed record SqliteMigration(int Version, string Sql);

/// <summary>
/// Applies ordered SQL migrations to a SQLite database, tracked via <c>PRAGMA user_version</c>.
/// Each migration runs at most once, ever, in its own transaction. See docs/PLAN-TECH-DEBT.md
/// B4.1 — before this, the schema was created with bare <c>CREATE TABLE IF NOT EXISTS</c> with
/// no version tracking, which made adding columns or tables to an existing user's database
/// unsafe to reason about.
/// </summary>
public static class SqliteMigrationRunner
{
    public static void Apply(SqliteConnection connection, IReadOnlyList<SqliteMigration> migrations)
    {
        var currentVersion = GetUserVersion(connection);
        var pending = migrations.Where(m => m.Version > currentVersion).OrderBy(m => m.Version).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        // Changing a primary key or unique constraint (docs/PLAN-CLOUD-PROVIDERS.md P4) is only
        // possible in SQLite by rebuilding the table — create a replacement, copy the data,
        // DROP the original, rename the replacement into place. If enforcement is on and another
        // table declares `… REFERENCES <this table>(Id) ON DELETE CASCADE`, that DROP is treated
        // as deleting every row of the parent first, which cascades and deletes every referencing
        // row too — reproduced against a real `cache.db`: dropping the rebuilt `SyncPairs` wiped
        // all 568 `SyncState` rows and both `SyncQueue` rows before the replacement table was even
        // renamed into place. Foreign keys exist to keep app-level writes honest, not to be
        // enforced against migration DDL, so they are off for every migration and restored to
        // whatever the caller had on the way out — never left silently disabled for the
        // connection's normal lifetime.
        var wasEnabled = GetForeignKeysEnabled(connection);
        if (wasEnabled)
        {
            SetForeignKeysEnabled(connection, false);
        }

        try
        {
            foreach (var migration in pending)
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = migration.Sql;
                    command.ExecuteNonQuery();

                    // PRAGMA statements don't accept bound parameters; `migration.Version` is our
                    // own int constant, never user input, so string interpolation here is safe.
                    var versionCommand = connection.CreateCommand();
                    versionCommand.Transaction = transaction;
                    versionCommand.CommandText = $"PRAGMA user_version = {migration.Version};";
                    versionCommand.ExecuteNonQuery();

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }
        finally
        {
            if (wasEnabled)
            {
                SetForeignKeysEnabled(connection, true);
            }
        }
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool GetForeignKeysEnabled(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    /// <summary>
    /// A no-op if called with an open transaction — SQLite only honors a change to this pragma
    /// outside one — which is why this is always called before the first migration's transaction
    /// begins and after the last one commits, never from within <see cref="Apply"/>'s loop.
    /// </summary>
    private static void SetForeignKeysEnabled(SqliteConnection connection, bool enabled)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys = {(enabled ? "ON" : "OFF")};";
        command.ExecuteNonQuery();
    }
}
