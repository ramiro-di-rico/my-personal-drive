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

        foreach (var migration in migrations.Where(m => m.Version > currentVersion).OrderBy(m => m.Version))
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

    private static int GetUserVersion(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
