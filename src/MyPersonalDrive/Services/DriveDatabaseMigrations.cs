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

        // Key/value app settings that have to survive a restart. First user: whether the
        // automatic sync loop was left on or off (a user who turns it off doesn't expect it
        // back on the next launch).
        new SqliteMigration(4, """
            CREATE TABLE AppSettings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """),

        // Recursive folder metrics from docs/PLAN-BROWSER-VIEWS.md M4. Only *deep, complete*
        // results are stored: shallow ones are recomputed for free on every folder load, so
        // persisting them would just be a second source of truth to keep from going stale.
        // A row costs the user minutes of scanning, which is why it survives a restart rather
        // than living in memory.
        new SqliteMigration(5, """
            CREATE TABLE FolderMetrics (
                Path               TEXT PRIMARY KEY,
                FileCount          INTEGER NOT NULL,
                FolderCount        INTEGER NOT NULL,
                TotalSize          INTEGER NOT NULL,
                UnknownSizeCount   INTEGER NOT NULL,
                ScannedFolderCount INTEGER NOT NULL,
                NewestModifiedAt   TEXT,
                OldestModifiedAt   TEXT,
                BucketsJson        TEXT NOT NULL,
                ComputedAt         TEXT NOT NULL
            );
            """),

        // Account-scopes the cache for docs/PLAN-CLOUD-PROVIDERS.md P4: today "/my-files/Photos"
        // always means the one Proton account, but the moment a second provider (or a second
        // account) exists, the same string is a different folder. `AccountKey` is
        // "<providerId>:<accountId>" (see §2.3); every row here predates that idea, so every
        // existing row defaults to `'proton:default'` — the account the user already has,
        // making this a no-op on upgrade.
        //
        // SQLite has no ALTER TABLE for a primary key or a UNIQUE constraint, so DriveItems,
        // FolderMetrics and SyncPairs are rebuilt: create a new table under a temporary name,
        // copy the data in, drop the original, then rename the new table into the original's
        // place. This whole migration runs in one transaction (SqliteMigrationRunner), so a
        // failure partway rolls every table back together rather than leaving two of three
        // rebuilt.
        //
        // The order (new table first, drop-then-rename last) is not arbitrary — it dodges a real
        // SQLite behavior: `ALTER TABLE X RENAME TO Y` rewrites every OTHER table's schema that
        // references `X` in a foreign key (or trigger/view) to say `Y` instead
        // (https://sqlite.org/lang_altertable.html#altertabrename — "also updates all references
        // to the table"). `SyncQueue`/`SyncState` declare `PairId … REFERENCES SyncPairs(Id)`;
        // renaming `SyncPairs` away first (as an earlier version of this migration did) silently
        // rewrites both to `REFERENCES SyncPairs_pre6(Id)`, and the subsequent `DROP TABLE
        // SyncPairs_pre6` then leaves them referencing a table that no longer exists — reproduced
        // and confirmed: with `PRAGMA foreign_keys=ON` (which `SyncStateStore.OpenConnection`
        // always sets), the very next `INSERT INTO SyncQueue` failed with "no such table:
        // main.SyncPairs_pre6", a name that appears nowhere in that statement. Never renaming
        // `SyncPairs` itself — only the throwaway `_new` table, which nothing references — avoids
        // the rewrite entirely; `DriveItems`/`FolderMetrics` have no incoming foreign keys so this
        // ordering is not strictly required for them, but it costs nothing to keep all three
        // consistent.
        //
        // SyncPairs keeps its surrogate `Id` — explicitly carried over in the INSERT — so
        // SyncState/SyncQueue/SyncLog's `PairId` values need no change at all, and AUTOINCREMENT
        // continues to hand out ids after the highest one carried over (verified: an explicit
        // insert of a higher id advances SQLite's own sequence table, so the next auto-generated
        // pair id can't collide with one that already exists).
        //
        // SyncState.HashAlgorithm is the other half of P3's guard (docs/PLAN-CLOUD-PROVIDERS.md
        // P3): NodeFingerprint.HashAlgorithm is in-memory only until now. Null means "unknown",
        // which the reconciler already treats as "not a mismatch" — the honest state of every
        // row written before this column existed.
        new SqliteMigration(6, """
            CREATE TABLE DriveItems_new6 (
                AccountKey  TEXT NOT NULL DEFAULT 'proton:default',
                Path        TEXT NOT NULL,
                ParentPath  TEXT,
                Name        TEXT,
                IsFolder    INTEGER,
                Size        INTEGER,
                ModifiedAt  TEXT,
                Owner       TEXT,
                IsShared    INTEGER,
                NodeId      TEXT,
                ContentHash TEXT,
                PRIMARY KEY (AccountKey, Path)
            );
            INSERT INTO DriveItems_new6 (AccountKey, Path, ParentPath, Name, IsFolder, Size, ModifiedAt, Owner, IsShared, NodeId, ContentHash)
                SELECT 'proton:default', Path, ParentPath, Name, IsFolder, Size, ModifiedAt, Owner, IsShared, NodeId, ContentHash
                FROM DriveItems;
            DROP TABLE DriveItems;
            ALTER TABLE DriveItems_new6 RENAME TO DriveItems;
            CREATE INDEX IF NOT EXISTS idx_ParentPath ON DriveItems(AccountKey, ParentPath);

            CREATE TABLE FolderMetrics_new6 (
                AccountKey         TEXT NOT NULL DEFAULT 'proton:default',
                Path               TEXT NOT NULL,
                FileCount          INTEGER NOT NULL,
                FolderCount        INTEGER NOT NULL,
                TotalSize          INTEGER NOT NULL,
                UnknownSizeCount   INTEGER NOT NULL,
                ScannedFolderCount INTEGER NOT NULL,
                NewestModifiedAt   TEXT,
                OldestModifiedAt   TEXT,
                BucketsJson        TEXT NOT NULL,
                ComputedAt         TEXT NOT NULL,
                PRIMARY KEY (AccountKey, Path)
            );
            INSERT INTO FolderMetrics_new6 (AccountKey, Path, FileCount, FolderCount, TotalSize, UnknownSizeCount, ScannedFolderCount, NewestModifiedAt, OldestModifiedAt, BucketsJson, ComputedAt)
                SELECT 'proton:default', Path, FileCount, FolderCount, TotalSize, UnknownSizeCount, ScannedFolderCount, NewestModifiedAt, OldestModifiedAt, BucketsJson, ComputedAt
                FROM FolderMetrics;
            DROP TABLE FolderMetrics;
            ALTER TABLE FolderMetrics_new6 RENAME TO FolderMetrics;

            CREATE TABLE SyncPairs_new6 (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountKey     TEXT NOT NULL DEFAULT 'proton:default',
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
                UNIQUE(AccountKey, RemotePath, LocalPath)
            );
            INSERT INTO SyncPairs_new6 (Id, AccountKey, RemotePath, LocalPath, Direction, ConflictPolicy, IsEnabled, IsPaused, ExcludeGlobs, LastSyncAt, LastSyncStatus, LastError)
                SELECT Id, 'proton:default', RemotePath, LocalPath, Direction, ConflictPolicy, IsEnabled, IsPaused, ExcludeGlobs, LastSyncAt, LastSyncStatus, LastError
                FROM SyncPairs;
            DROP TABLE SyncPairs;
            ALTER TABLE SyncPairs_new6 RENAME TO SyncPairs;

            ALTER TABLE SyncState ADD COLUMN HashAlgorithm TEXT;
            """),
    ];
}
