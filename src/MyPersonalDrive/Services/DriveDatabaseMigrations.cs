namespace MyPersonalDrive.Services;

/// <summary>
/// The single, shared list of migrations for <c>cache.db</c>. Both <see cref="DriveCacheService"/>
/// and <c>Services.Sync.SyncStateStore</c> apply this same list (via <see cref="SqliteMigrationRunner"/>)
/// so either can be constructed independently — in production they share one file and one
/// composition-root startup sequence; in tests each gets its own temp file — without the two
/// classes disagreeing about what the schema should look like.
/// </summary>
public static class DriveDatabaseMigrations
{
    public static readonly IReadOnlyList<SqliteMigration> All =
    [
        new SqliteMigration(1, """
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
            """),

        // Adds the CLI's stable node uid and content hash (see docs/PLAN-LOCAL-SYNC.md
        // Appendix A #3/#14), needed by the sync reconciler to correlate nodes across renames
        // and to detect content changes without relying on mtime tolerance.
        new SqliteMigration(2, """
            ALTER TABLE DriveItems ADD COLUMN NodeId TEXT;
            ALTER TABLE DriveItems ADD COLUMN ContentHash TEXT;
            """),

        // Sync tables from docs/PLAN-LOCAL-SYNC.md §3.1.
        new SqliteMigration(3, """
            CREATE TABLE SyncPairs (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                RemotePath     TEXT NOT NULL,
                LocalPath      TEXT NOT NULL,
                Direction      TEXT NOT NULL,
                ConflictPolicy TEXT NOT NULL,
                IsEnabled      INTEGER NOT NULL DEFAULT 1,
                IsPaused       INTEGER NOT NULL DEFAULT 0,
                ExcludeGlobs   TEXT,
                LastSyncAt     TEXT,
                LastSyncStatus TEXT NOT NULL DEFAULT 'Never',
                LastError      TEXT,
                UNIQUE(RemotePath, LocalPath)
            );

            CREATE TABLE SyncState (
                PairId           INTEGER NOT NULL REFERENCES SyncPairs(Id) ON DELETE CASCADE,
                RelativePath     TEXT NOT NULL,
                IsFolder         INTEGER NOT NULL,
                RemoteSize       INTEGER,
                RemoteModifiedAt TEXT,
                RemoteNodeId     TEXT,
                RemoteHash       TEXT,
                LocalSize        INTEGER,
                LocalModifiedAt  TEXT,
                LocalInode       TEXT,
                ContentHash      TEXT,
                SyncedAt         TEXT NOT NULL,
                PRIMARY KEY (PairId, RelativePath)
            );
            CREATE INDEX idx_SyncState_Pair ON SyncState(PairId);
            CREATE INDEX idx_SyncState_RemoteNodeId ON SyncState(PairId, RemoteNodeId);

            CREATE TABLE SyncQueue (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                PairId        INTEGER NOT NULL REFERENCES SyncPairs(Id) ON DELETE CASCADE,
                RelativePath  TEXT NOT NULL,
                Operation     TEXT NOT NULL,
                Payload       TEXT,
                Priority      INTEGER NOT NULL DEFAULT 100,
                AttemptCount  INTEGER NOT NULL DEFAULT 0,
                NextAttemptAt TEXT,
                State         TEXT NOT NULL DEFAULT 'Pending',
                LastError     TEXT,
                EnqueuedAt    TEXT NOT NULL,
                CompletedAt   TEXT
            );
            CREATE INDEX idx_SyncQueue_Pending ON SyncQueue(PairId, State, Priority);

            CREATE TABLE SyncLog (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                PairId       INTEGER,
                Timestamp    TEXT NOT NULL,
                Level        TEXT NOT NULL,
                RelativePath TEXT,
                Message      TEXT NOT NULL
            );
            """),
    ];
}
