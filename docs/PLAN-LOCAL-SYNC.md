# Technical Plan — Local Directory Synchronization

> Goal: allow a remote Proton Drive folder (e.g. `/my-files/Documents`) to be kept in sync
> with a local directory on disk (e.g. `~/ProtonDrive/Documents`).
> Design document to attack in the future. See [ARCHITECTURE.md](ARCHITECTURE.md) for the
> current state.

---

## 0. Executive summary

Today the app is a *stateless* client of the CLI: every action is a single command, and the
SQLite cache only speeds up rendering. Synchronization requires three things that **don't exist
today**:

1. A **persistent synchronization state** (what version of each file we last saw, on both
   sides) — without this, there's no way to distinguish "created over there" from "deleted over here."
2. A **three-way reconciliation engine** (local / remote / baseline) that produces a plan of
   actions, instead of just comparing two listings.
3. A **transfer executor with a queue, retries, and progress**, because sync produces bursts of
   N CLI commands where today there's one per click.

And an underlying obstacle:

> **Proton Drive, as exposed by this CLI, gives us no stable ID or per-file hash.**
> The identifier is the path. A remote rename is indistinguishable from a delete+create.
> The entire design below assumes this and mitigates it; if the CLI ever exposes IDs or
> revisions, §11 explains what simplifies.

Proposed phasing: **F0 CLI investigation → F1 manual one-way mirror → F2 bidirectional with
baseline → F3 watcher + automatic → F4 polish (conflicts, pause, multiple pairs)**.
Each phase is shippable and usable on its own.

---

## 1. Scope

### In scope

- Sync pairs (**sync pair**): `{ remote folder ↔ local folder }`, N pairs.
- Modes: `RemoteToLocal` (download mirror), `LocalToRemote` (upload mirror), `TwoWay`.
- Local change detection via filesystem watcher + periodic scan.
- Remote change detection via polling (recursive listing).
- Conflict resolution with configurable policy + fallback to "keep both."
- UI: sync panel with per-pair status, progress, activity log, and manual resolution.
- State persisted in the same SQLite database, with a versioned schema.

### Out of scope (explicitly, for now)

- Instant bidirectional real-time sync (there's no push from the remote side).
- Selective/"on-demand" placeholder-style file sync.
- Content-based deduplication or block-level transfer (the CLI uploads whole files).
- Content merge resolution (it's always "one side wins" or "keep both").
- A custom local trash (we use the system's, or a `.mypersonaldrive-trash` folder).

---

## 2. Phase 0 — CLI investigation (blocking, ~half a day)

Nothing else can be properly sized without this. It needs to be run against the real CLI and
documented in an appendix at the end of this file.

Checklist of questions to answer:

| # | Question | How to verify | Why it matters |
|---|---|---|---|
| 1 | Exact format of `filesystem list --json`? Real fields, not guessed aliases | `proton-drive filesystem list --json "/my-files" \| jq` | Today `ProtonDriveService` guesses among 3-4 aliases per field |
| 2 | Is `ModifiedAt` ISO-8601 and UTC? Is it content mtime or metadata mtime? | Upload a file, list it, compare | It's the axis of change detection |
| 3 | Is there an ID/UUID/revision per node? | Inspect the raw JSON | Determines whether we can detect renames (§11) |
| 4 | Is `list` recursive? Is there a `--recursive` / `--depth` flag? | `proton-drive filesystem list --help` | A recursive scan via N calls is O(folders) processes |
| 5 | Is there a `filesystem download` for a whole folder? | `--help` + test | Would massively speed up the initial download |
| 6 | Does `download` preserve mtime? Can the destination name be chosen? | `stat` after downloading | If it doesn't preserve it, the remote mtime has to be stored in the state |
| 7 | Does `upload` allow preserving/forcing mtime? | `--help` | If not, the mtime assigned by the server must be re-read after upload |
| 8 | Is there `filesystem move`? Or only `rename` + `copy` + `trash`? | `--help` | Without `move`, a remote rename = copy+trash (expensive) |
| 9 | Is there a permanent `filesystem delete` or only `trash`? | `--help` | Defines the semantics of "delete" |
| 10 | Distinct exit codes per error type? Stable messages? | Trigger: no auth, nonexistent path, quota full, network down | Today we classify by English substring |
| 11 | What happens with four concurrent `proton-drive` processes? Is there a session lock? | Launch 4 in parallel | Defines the queue's degree of parallelism |
| 12 | Do `upload`/`download` emit parseable progress on stdout? | Upload a large file and capture output | Determines whether progress is real or just "in progress" |
| 13 | Limits: max file size, rate limiting, quota | Proton docs + testing | Backoff and validations |
| 14 | Is there a hash/checksum exposed by any command? | `--help` on everything | If it exists, it changes change detection |

**F0 deliverable**: an "Appendix A — Verified CLI behavior" section at the end of this document,
plus a record of the decisions made. If the answer to #3 or #14 is yes, **revisit §5 before implementing**.

---

## 3. Data model

### 3.1 New SQLite tables

These go into `cache.db`, alongside `DriveItems`. **Schema versioning must be introduced first**,
which doesn't exist today:

```sql
-- Migration 1: versioning infrastructure (retro-applicable to the current schema)
PRAGMA user_version;  -- 0 = current baseline, bumped to 1 on the first migration
```

`DriveCacheService` gets a `MigrationRunner` with an ordered list of scripts; on open, it reads
`PRAGMA user_version` and applies pending ones in a transaction.

```sql
-- A sync pair configured by the user
CREATE TABLE SyncPairs (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    RemotePath    TEXT NOT NULL,          -- '/my-files/Documents'
    LocalPath     TEXT NOT NULL,          -- '/home/ramiro/ProtonDrive/Documents'
    Direction     TEXT NOT NULL,          -- 'TwoWay' | 'RemoteToLocal' | 'LocalToRemote'
    ConflictPolicy TEXT NOT NULL,         -- 'KeepBoth' | 'PreferLocal' | 'PreferRemote' | 'Ask'
    IsEnabled     INTEGER NOT NULL DEFAULT 1,
    IsPaused      INTEGER NOT NULL DEFAULT 0,
    ExcludeGlobs  TEXT,                   -- JSON array of patterns
    LastSyncAt    TEXT,                   -- ISO-8601 UTC
    LastSyncStatus TEXT,                  -- 'Ok' | 'PartialFailure' | 'Error' | 'Never'
    LastError     TEXT,
    UNIQUE(RemotePath, LocalPath)
);

-- THE BASELINE: what we saw the last time both sides agreed.
-- Without this table there is no possible bidirectional sync, only mirroring.
CREATE TABLE SyncState (
    PairId          INTEGER NOT NULL REFERENCES SyncPairs(Id) ON DELETE CASCADE,
    RelativePath    TEXT NOT NULL,        -- 'subfolder/file.pdf', always '/' separated
    IsFolder        INTEGER NOT NULL,
    -- remote fingerprint at the time of the last successful sync
    RemoteSize      INTEGER,
    RemoteModifiedAt TEXT,                -- normalized to ISO-8601 UTC
    RemoteNodeId    TEXT,                 -- if the CLI exposes it (F0 #3); NULL otherwise
    -- local fingerprint at the same moment
    LocalSize       INTEGER,
    LocalModifiedAt TEXT,                 -- ISO-8601 UTC of the mtime
    LocalInode      TEXT,                 -- st_ino on Unix; helps detect local renames
    ContentHash     TEXT,                 -- SHA-256 of local content; computed lazily
    SyncedAt        TEXT NOT NULL,
    PRIMARY KEY (PairId, RelativePath)
);
CREATE INDEX idx_SyncState_Pair ON SyncState(PairId);

-- Durable queue of pending operations; survives app restarts
CREATE TABLE SyncQueue (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    PairId       INTEGER NOT NULL REFERENCES SyncPairs(Id) ON DELETE CASCADE,
    RelativePath TEXT NOT NULL,
    Operation    TEXT NOT NULL,           -- see §5.3
    Payload      TEXT,                    -- JSON with extra data (rename target, etc.)
    Priority     INTEGER NOT NULL DEFAULT 100,
    AttemptCount INTEGER NOT NULL DEFAULT 0,
    NextAttemptAt TEXT,
    State        TEXT NOT NULL,           -- 'Pending'|'Running'|'Done'|'Failed'|'Conflict'|'Skipped'
    LastError    TEXT,
    EnqueuedAt   TEXT NOT NULL,
    CompletedAt  TEXT
);
CREATE INDEX idx_SyncQueue_Pending ON SyncQueue(PairId, State, Priority);

-- History for the UI and diagnostics (with pruning)
CREATE TABLE SyncLog (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    PairId       INTEGER,
    Timestamp    TEXT NOT NULL,
    Level        TEXT NOT NULL,           -- 'Info'|'Warning'|'Error'
    RelativePath TEXT,
    Message      TEXT NOT NULL
);
```

### 3.2 New types in `Models/`

```csharp
public sealed record SyncPair(int Id, string RemotePath, string LocalPath,
    SyncDirection Direction, ConflictPolicy ConflictPolicy, bool IsEnabled, bool IsPaused,
    IReadOnlyList<string> ExcludeGlobs, DateTimeOffset? LastSyncAt, SyncPairStatus LastStatus, string? LastError);

public enum SyncDirection { TwoWay, RemoteToLocal, LocalToRemote }
public enum ConflictPolicy { Ask, KeepBoth, PreferLocal, PreferRemote }

/// Fingerprint of a node on one of the two sides. Comparable across snapshots.
public sealed record NodeFingerprint(string RelativePath, bool IsFolder, long? Size,
    DateTimeOffset? ModifiedAt, string? NodeId, string? ContentHash);

/// A plan is the pure result of reconciliation: it touches nothing.
public sealed record SyncPlan(int PairId, IReadOnlyList<SyncAction> Actions,
    IReadOnlyList<SyncConflict> Conflicts, SyncPlanStats Stats);

public sealed record SyncAction(SyncOperation Operation, string RelativePath,
    string? SecondaryPath, long? Bytes, int Priority);

public enum SyncOperation {
    DownloadFile, UploadFile, CreateLocalFolder, CreateRemoteFolder,
    DeleteLocal, TrashRemote, RenameLocal, RenameRemote,
    UpdateBaselineOnly, ResolveConflictKeepBoth
}
```

**Golden rule**: `NodeFingerprint` and everything that goes into SQLite uses `RelativePath` with
`/` separators, no leading slash, exactly as it comes in, case-sensitive. Conversion to an OS
path happens in a single helper (`PathMapper`), never ad-hoc.

---

## 4. Service architecture

New files, all under `Services/Sync/`:

```
Services/Sync/
  ISyncStateStore.cs / SyncStateStore.cs      # CRUD for SyncPairs/SyncState/SyncQueue/SyncLog
  ILocalScanner.cs   / LocalScanner.cs        # enumerates the local dir -> NodeFingerprint[]
  IRemoteScanner.cs  / RemoteScanner.cs       # recursive listing via ProtonDriveService
  PathMapper.cs                               # relative <-> local absolute <-> remote absolute
  SyncReconciler.cs                           # PURE FUNCTION: (local, remote, baseline) -> SyncPlan
  SyncExecutor.cs                             # executes the plan against CLI + FS, updates baseline
  SyncScheduler.cs                            # orchestrates: when to scan, when to execute
  LocalFileWatcher.cs                         # FileSystemWatcher with debounce
  SyncEngine.cs                               # public facade consumed by the ViewModel
  ExclusionMatcher.cs                         # globs + default rules
  Transfer/TransferQueue.cs                   # queue with bounded concurrency and backoff
```

Flow diagram:

```
                 ┌──────────────┐
                 │ SyncScheduler│◄── timer / watcher / "Sync now" button
                 └──────┬───────┘
                        │ for each enabled SyncPair
        ┌───────────────┼────────────────┐
        ▼               ▼                ▼
 LocalScanner    RemoteScanner    SyncStateStore
   (disk)       (proton-drive       (baseline)
                filesystem list)
        └───────────────┼────────────────┘
                        ▼
                 SyncReconciler        ← PURE, testable without IO
                        │
                        ▼
                    SyncPlan  ──────────► UI (dry-run / preview)
                        │
                        ▼
                  SyncExecutor
                   ├─ TransferQueue ──► ProtonDriveService ──► CLI
                   ├─ Local file IO
                   └─ SyncStateStore.UpdateBaseline()
```

**Key decision**: `SyncReconciler` does no IO. It receives three lists and returns a plan.
This turns the hard part of the problem into something coverable with unit tests, without a CLI
or a disk — exactly what the repo currently lacks.

---

## 5. The reconciliation engine

### 5.1 Inputs

For a given pair, three dictionaries indexed by `RelativePath`:

- `L` = current local fingerprints
- `R` = current remote fingerprints
- `B` = baseline (`SyncState`), what we saw last time

### 5.2 Decision table (TwoWay mode)

For every path in `L ∪ R ∪ B`. `changed(X, B)` = size or mtime differ (with tolerance, §5.5).

| in L | in R | in B | L changed | R changed | Action |
|:---:|:---:|:---:|:---:|:---:|---|
| ✓ | ✓ | ✗ | — | — | Both appeared with no baseline → if identical: `UpdateBaselineOnly`; otherwise: **conflict** |
| ✓ | ✗ | ✗ | — | — | New locally → `UploadFile` / `CreateRemoteFolder` |
| ✗ | ✓ | ✗ | — | — | New remotely → `DownloadFile` / `CreateLocalFolder` |
| ✓ | ✓ | ✓ | no | no | Nothing |
| ✓ | ✓ | ✓ | yes | no | `UploadFile` (local wins) |
| ✓ | ✓ | ✓ | no | yes | `DownloadFile` (remote wins) |
| ✓ | ✓ | ✓ | yes | yes | **conflict** → policy |
| ✓ | ✗ | ✓ | no | — | Deleted remotely, local untouched → `DeleteLocal` |
| ✓ | ✗ | ✓ | yes | — | Deleted remotely but local was modified → **conflict** (default: re-upload) |
| ✗ | ✓ | ✓ | — | no | Deleted locally, remote untouched → `TrashRemote` |
| ✗ | ✓ | ✓ | — | yes | Deleted locally but remote was modified → **conflict** (default: re-download) |
| ✗ | ✗ | ✓ | — | — | Deleted on both sides → clear baseline |

In one-way modes the table collapses: `RemoteToLocal` only emits `Download*`/`DeleteLocal` and
never touches the remote side; diverging local changes are **overwritten** (with a prior warning
in the preview).

### 5.3 Plan execution order

Not optional; the order matters:

1. `CreateLocalFolder` / `CreateRemoteFolder` — **sorted by ascending depth**.
2. `DownloadFile` / `UploadFile` / `ResolveConflictKeepBoth`.
3. `RenameLocal` / `RenameRemote` (if detected, §11).
4. `DeleteLocal` / `TrashRemote` — **sorted by descending depth**.
5. `UpdateBaselineOnly`.

The `SyncPlan` already comes out sorted from the reconciler; `Priority` in `SyncQueue` encodes
these 5 bands.

### 5.4 Change detection without hashing

Default criterion (cheap): `Size` differs **or** `ModifiedAt` differs beyond a tolerance.

Tie-breaking criterion (expensive, optional per pair — "content verification"):
SHA-256 of the local file, cached in `SyncState.ContentHash` and invalidated whenever
`(size, mtime, inode)` changes. Only used when the cheap criterion says "changed" but we want
to avoid a useless transfer, and **only makes sense if F0 #14 finds a comparable remote hash**.
If the remote doesn't expose a hash, the local hash is only useful for detecting renames and
"false changes" (mtime touched without content changing, typical of `touch` or some editors).

### 5.5 Time tolerance

- Always compare in **UTC**.
- Default tolerance of **2 seconds** (FAT/exFAT have 2s resolution; some backends truncate
  to the second).
- If the CLI doesn't preserve mtime on transfer (F0 #6/#7), **the remote mtime after the
  transfer is re-read and stored in the baseline**; it's never assumed to match the local one.
  The baseline stores both fingerprints separately for exactly this reason.

### 5.6 Conflicts

Default strategy **KeepBoth**, the only one that never loses data:

- The local file gets renamed to `name (local conflict 2026-07-31 14-22-05).ext`.
- The remote version is downloaded under the original name.
- The renamed file is uploaded.
- Logged to `SyncLog` at `Warning` level, and the pair is marked `PartialFailure` until the
  user acknowledges it.

With the `Ask` policy, the plan item stays in `SyncQueue.State = 'Conflict'` and the UI offers
per-file resolution. **No automatic resolution should ever delete unsynced content.**

---

## 6. Scanning

### 6.1 Local — `LocalScanner`

- Recursive `Directory.EnumerateFileSystemEntries` with explicit handling of
  `UnauthorizedAccessException` per entry (log + skip, never abort the whole scan).
- **Do not follow symlinks** by default (avoids cycles); configurable per pair.
- Ignored by default: `.git/`, `node_modules/`, `.DS_Store`, `Thumbs.db`, `*.tmp`, `*.swp`,
  `~$*`, `.mypersonaldrive-*`, and the `.mypersonaldrive-sync.json` marker file if we decide to add one.
- Detect **open/in-progress writes**: if a file's mtime is less than N seconds old, postpone it
  to the next cycle (avoids uploading half-written files).
- Run off the UI thread; report progress every X entries.

### 6.2 Remote — `RemoteScanner`

Depends on F0 #4:

- **If `list` is recursive**: one call per pair. Ideal.
- **If not**: BFS emitting `filesystem list --json` per folder, with bounded concurrency
  (start at 2-3 processes, tune based on F0 #11) and reusing `DriveItems` as a cache for
  folders whose parent hasn't changed. A full scan of a drive with 500 folders would be
  500 processes: **it must be shown as progress and be cancelable**.

Optimization: store a synthetic `RemoteFolderEtag` (a hash of the children listing) in
`SyncState` to skip unchanged subtrees between cycles. Only valid if the remote listing is
order-stable.

### 6.3 Local watcher — `LocalFileWatcher`

- One `FileSystemWatcher` per pair, `IncludeSubdirectories = true`,
  `NotifyFilter = FileName | DirectoryName | LastWrite | Size`.
- **Mandatory debounce**: accumulate events in a `Dictionary<string, DateTimeOffset>` and
  process the ones that have been quiet for ≥ 2 s. A single editor save generates 3-6 events.
- Raise `InternalBufferSize` (e.g. 64 KB) and handle the `Error` event (buffer overflow):
  on overflow, mark the pair as "needs full scan" and enqueue it.
- On Linux, `inotify` has a watch limit (`fs.inotify.max_user_watches`); on failure, degrade to
  periodic scanning and warn in the UI with the command to raise the limit.
- The watcher **never triggers a sync directly**: it marks the pair dirty and wakes the scheduler.

### 6.4 Remote polling — `SyncScheduler`

- Configurable interval, default **5 minutes**, with exponential backoff up to 30 min after
  consecutive errors.
- Manual sync always available ("Sync now" per pair and globally).
- Never two cycles of the same pair in parallel (per-pair lock).
- Automatic global pause if `IsAuthenticated == false`.

---

## 7. Transfer execution

`TransferQueue`:

- Consumes `SyncQueue` ordered by `(Priority, Id)`.
- Bounded concurrency via `SemaphoreSlim`, default value **2**, configurable (depends on F0 #11
  — if the CLI has a session lock, it's 1).
- Exponential backoff + jitter retries: 5 s, 15 s, 45 s, 2 min, 5 min; max 5 attempts, then
  `State = 'Failed'` and visible in the UI.
- **Error classification** (requires F0 #10) to decide whether to retry:
  `Transient` (network, timeout, rate limit) → retries; `Auth` → pauses everything and asks
  to log in; `Quota` → pauses the pair with a clear message; `NotFound` → invalidates and
  re-scans; `Permanent` (invalid name, size exceeded) → `Failed`, no retry.
  Start with a `SyncErrorClassifier` based on substrings, **isolated in a single file**, so
  that the day the CLI provides exit codes, the change happens in one place.
- Cancellation: its own `CancellationToken` per pair, independent from the navigation `_cts`
  (today the VM has a single shared one — these need to be separated, see §9).

**Download atomicity**: download to `<destination>/.mypersonaldrive-tmp/<guid>` and then
`File.Move` to the final destination. If the CLI doesn't allow choosing the output name,
download into a dedicated temp directory and move from there. Never leave a partial file under
the final name.

**Baseline update**: only after confirmed success, and by re-reading the real fingerprint of
both sides (local `FileInfo` + the entry from the latest remote listing). Write the baseline in
the same transaction that marks the `SyncQueue` row as `Done`.

**Crash safety**: since `SyncQueue` is durable, on startup the app runs
`UPDATE SyncQueue SET State='Pending' WHERE State='Running'` and clears `.mypersonaldrive-tmp`.

---

## 8. Required changes to existing code

This is what needs to change in what already exists, beyond adding new files.

| File | Change | Reason |
|---|---|---|
| `DriveCacheService.cs` | Introduce `PRAGMA user_version` + migration runner; extract connection handling | Today the schema is created with `CREATE TABLE IF NOT EXISTS` with no version; adding tables without migrations is a time bomb |
| `ProtonDriveService.cs` | Normalize `ModifiedAt` to `DateTimeOffset?` during parsing; expose `NodeId` if it exists; add `MoveItemAsync` if the CLI supports it | Sync needs comparable dates, not strings |
| `Models/DriveItem.cs` | `ModifiedAt` changes from `string?` to `DateTimeOffset?`; add `NodeId` | Same reason. Breaks `DriveNodeViewModel.ModifiedText` and the cache (column migration) |
| `ProtonDriveCliExecutor.cs` | Add a configurable timeout; expose `ExitCode` in the exception (`CliException : Exception { int ExitCode; string Stderr; }`) | Classifying errors by substring is unsustainable with retries |
| `AppSettings.cs` + `AppJsonContext.cs` | New global sync settings (interval, concurrency, tolerance); **register every new type in the JsonContext** | Native AOT |
| `MainWindowViewModel.cs` | Separate the navigation `_cts` from the sync one; the file is already ~950 lines → extract a `SyncPanelViewModel` instead of growing it further | Maintainability |
| `MainWindow.axaml(.cs)` | New sync tab/panel; new dialogs following the `Request*Async` pattern | Consistency with existing code |
| `App.axaml.cs` | Wiring of the new services; consider moving to `Microsoft.Extensions.DependencyInjection` if the graph grows much further | The manual composition root starts to hurt with ~8 more services |
| **new** `tests/MyPersonalDrive.Tests` | xUnit project | See §10 |

### Recommended preliminary refactors (cheap, enable everything else)

1. **`CliException` with `ExitCode` + `Stderr`** instead of `InvalidOperationException` with
   text. Keep it backward-compatible by inheriting from `InvalidOperationException` so existing
   `catch` blocks don't break.
2. **Fix `TryParseJsonListing`**: distinguish "empty folder" from "failed to parse."
   Today 0 items falls back to the text parser, which produces garbage on top of JSON. With
   sync in the picture this becomes destructive: a misinterpreted empty folder can look like
   "everything was deleted." **This is the most dangerous bug in the current code with respect to sync.**
3. **`AsyncCommand`**: catch exceptions inside `Execute` and route them to a handler, instead of
   letting an `async void` crash the app.

---

## 9. Concurrency and consistency

- One lock per pair (`SemaphoreSlim` in `SyncScheduler`) — never two cycles of the same pair.
- The file browser and sync engine **share the CLI**: add a global semaphore over
  `proton-drive` processes so a large sync doesn't make browsing unresponsive.
  **Give priority to interactive operations** (the browser's queue is served first).
- When sync modifies the remote side, invalidate the `DriveItems` for the affected folder so
  the browser doesn't show stale data.
- When sync writes locally, **suppress the watcher events it generates itself**: keep a set of
  "written by the engine" paths with a short TTL, and discard matching events. Without this,
  an infinite sync loop occurs. **This is the classic bug for this feature.**
- Anything that updates the UI goes through `Dispatcher.UIThread`.

---

## 10. Testing

The repo currently has no tests. Synchronization is not a feature that can be validated by hand.
Bare minimum:

- **`SyncReconciler` — exhaustive unit tests.** It's pure: 3 lists in, a plan out.
  One test per row of the table in §5.2, per direction mode. ~40 tests, no IO. **Non-negotiable.**
- **`PathMapper`** — round-trip, separators, unicode, escaped `/` in names, case-sensitivity.
- **`ExclusionMatcher`** — globs.
- **`LocalScanner`** against a real temp directory.
- **`SyncExecutor`** with a fake `IProtonDriveCliExecutor` that records emitted commands and
  simulates failures/exit codes. The interface already exists and is the perfect seam.
- **Loop test**: write locally via the executor and verify the watcher doesn't re-enqueue.
- Manual/integration test against a real Proton account: a folder with ~200 files, a rename,
  a delete, a conflict, killing the app mid-transfer.

---

## 11. The rename / identity problem

With the path as the only identity, moving `a/x.pdf` → `b/x.pdf` remotely looks like
`delete a/x.pdf` + `create b/x.pdf`, and sync **downloads the file again** (correct but
expensive) or, worse, in TwoWay mode may **re-upload the original from the local baseline**.

Mitigations, in order of preference:

1. **If F0 #3 finds a stable ID**: `SyncState.RemoteNodeId` solves the problem completely.
   Reconciliation is indexed by ID and rename becomes a first-class action (`RenameRemote` /
   `RenameLocal`), cheap. **Ask this question first.**
2. **Rename heuristic**: if in the same cycle `p1` disappears and `p2` appears with identical
   `(size, mtime)` and the same base file name, treat it as a rename. On the local side,
   `st_ino` confirms it with certainty. On the remote side it's a gamble: only apply it if the
   base name also matches, and **fall back to delete+create when there's ambiguity**
   (more than one candidate) — never guess when there's more than one match.
3. **Without any of the above**: accept delete+create and document it. With `TrashRemote`
   (not a permanent delete), the cost of getting it wrong is recoverable, which is exactly why
   **the engine must always use `trash`, never a permanent delete**.

Cross-cutting safety rule: **never delete locally without a trash**. Move to
`<LocalPath>/.mypersonaldrive-trash/<date>/` or use the system trash
(on Linux, the freedesktop spec: `~/.local/share/Trash`).

---

## 12. Proposed UI

New **"Sync"** tab next to the current browser (or a side panel).

- **Pair list**: for each one — remote folder, local folder, direction (icon),
  status (`✓ Up to date` / `⟳ Syncing 12/48` / `⚠ 3 conflicts` / `✗ Error`),
  last sync time, and pause / sync now / edit / remove buttons.
- **"New pair" dialog**: remote folder picker (reuses the existing tree), local folder picker
  (`StorageProvider.OpenFolderPickerAsync`, already used), direction selector and conflict
  policy selector, exclusions field. Following the VM's `Request*Async` pattern.
- **Preview / dry-run before the first sync** — essential: show the `SyncPlan`
  ("340 files will be downloaded (1.2 GB), 12 uploaded, 0 deleted locally") and ask for
  confirmation. Especially critical in one-way modes, where mass deletions can occur.
- **Conflicts panel**: list with "keep local / keep remote / keep both," per file.
- **Progress**: global bar + current item + throughput if F0 #12 allows it.
- Reuse the existing CLI console for raw detail; the sync log lives in `SyncLog` and is shown
  filtered by pair.

### Validations when creating a pair

- The local folder exists and is writable.
- It's not nested inside (nor contains) another existing pair.
- It's not `$HOME`, `/`, or a system directory.
- Warn if it already contains many files (it will trigger a mass upload).
- Sufficient free space for a `RemoteToLocal` (estimated from the sizes in the remote listing).

---

## 13. Phasing and estimate

| Phase | Content | Usable deliverable | Estimate |
|---|---|---|---|
| **F0** | CLI investigation (§2) + Appendix A | Decisions document | 0.5 d |
| **F0.5** | Preliminary refactors (§8): `CliException`, fix the listing parser, SQLite migrations, typed `ModifiedAt`, test project | Sound baseline, no functional change | 1.5 d |
| **F1** | `PathMapper`, `LocalScanner`, `RemoteScanner`, `SyncStateStore`, `SyncReconciler` (`RemoteToLocal` only), download `SyncExecutor`, minimal UI with dry-run and manual button | **Manual download mirror**: already useful as a local backup | 3 d |
| **F2** | Full baseline, the whole §5.2 table, uploads, deletes with trash, `KeepBoth` conflicts, durable `SyncQueue` with retries | **Manual bidirectional sync** | 4 d |
| **F3** | `LocalFileWatcher` with debounce and echo suppression, `SyncScheduler` with polling and backoff, automatic startup | **Automatic sync** | 2.5 d |
| **F4** | Multiple pairs, exclusions, conflicts panel, `Ask` policy, fine-grained progress, pause/resume, log pruning | Complete product | 3 d |
| **F5** (optional) | Rename detection (§11), hash-based verification, selective sync | Optimizations | 2 d |

Total approximate: **~14 days** of effective work through F4.
The shortest path to something useful is **F0 + F0.5 + F1 ≈ 5 days**.

---

## 14. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **Data loss from a reconciliation bug** | Critical | Never permanent delete (always local and remote trash); mandatory dry-run on first sync; exhaustive reconciler tests; "download only" as the suggested default |
| **Infinite loop between watcher and executor** | High | Echo suppression with TTL (§9); dedicated test |
| **The CLI doesn't expose what's needed** (not recursive, no reliable mtime, no IDs) | High | F0 is blocking and drives the real design; the reconciler is isolated so the fingerprint source can be swapped |
| **Process cost**: a scan = N `proton-drive` processes | Medium | Subtree caching, bounded concurrency, incremental scan, cancelable progress |
| **mtime not preserved on transfers** | Medium | The baseline stores local and remote fingerprints separately; they're never compared directly against each other |
| **inotify limit on Linux** | Low | Degrade to polling and warn |
| **`MainWindowViewModel` becomes unmanageable** | Medium | A separate `SyncPanelViewModel` from day one |
| **Migrating existing users' `cache.db`** | Low | Migration runner in F0.5, before anything else is touched |

---

## 15. Decisions made (and why)

- **Three-way baseline instead of comparing two listings.** Comparing only local vs. remote
  can't distinguish "created on one side" from "deleted on the other"; that's the design flaw
  that makes a sync "lose files." The cost is one more table and discipline to update it only
  after success.
- **Pure reconciler.** It's the only way to test this without a real account and without disk access.
- **Durable queue in SQLite** instead of in-memory: this is a desktop app, it gets closed
  mid-transfer all the time.
- **Trash, never delete.** The cost of a false positive from the engine has to be recoverable.
- **Start with `RemoteToLocal`.** It's the mode that can't destroy data in the cloud, delivers
  immediate value (local backup), and exercises the whole infrastructure.

---

## Appendix A — Verified CLI behavior

> **Pending: fill in during F0.** Without this, §5.4, §5.5, §6.2, §7, and §11 are written on
> assumptions.

```
proton-drive --version           →
proton-drive filesystem --help   →
proton-drive filesystem list --json "/my-files"  →
```
