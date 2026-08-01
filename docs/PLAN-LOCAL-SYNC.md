# Technical Plan — Local Directory Synchronization

> Goal: allow a remote Proton Drive folder (e.g. `/my-files/Documents`) to be kept in sync
> with a local directory on disk (e.g. `~/ProtonDrive/Documents`).
> Design document to attack in the future. See [ARCHITECTURE.md](ARCHITECTURE.md) for the
> current state. Implementation branch: `feature/local-sync`.

## Status

- [x] **F0 — CLI investigation.** Done against a real authenticated account; see Appendix A.
      Major findings that changed the design: the CLI exposes a stable `uid` that survives
      rename/move (resolves §11 outright), a client-computed SHA-1 per file
      (`activeRevision.value.claimedDigests.sha1`, upgrades §5.4 to hash-first), `filesystem
      move` exists, folder `download` is recursive, and a ~3.5s per-process cold start that
      raises the priority of the subtree-caching optimization in §6.2.
- [x] **F0.5 (partial) — preliminary refactors.** From docs/PLAN-TECH-DEBT.md: `CliException`
      + typed error kinds, the listing-parser empty-vs-unparseable fix, and the test project
      were already done on `main` before this branch. On this branch: `ProtonDriveService`'s
      entry parser was rewritten to the real field names from Appendix A (no more guessed
      aliases); `DriveItem.ModifiedAt` is now `DateTimeOffset?`, sourced from
      `activeRevision.value.claimedModificationTime`; `DriveItem` gained `NodeId` (`uid`) and
      `ContentHash` (`claimedDigests.sha1`); `DriveCacheService` gained a `PRAGMA user_version`
      migration runner (`SqliteMigrationRunner`) and WAL mode.
      **Not yet done:** `MoveItemAsync` on `ProtonDriveService` (the CLI supports `filesystem
      move`, not wired up yet — needed once rename/move actions are implemented in the
      executor).
- [x] **F1 (backend) — done.** All of `Services/Sync/` (note: this entry's own scoping was
      pessimistic — `SyncReconciler` landed with the *entire* §5.2 table, TwoWay included, which
      is why F2 needed no engine work):
      - `PathMapper` — relative/remote-absolute/local-absolute conversions.
      - `SyncReconciler` — pure engine, every decision-table row, both one-way modes.
      - `SyncStateStore` — CRUD for `SyncPairs`/`SyncState`/`SyncQueue`/`SyncLog`, including
        crash-safety's `ResetRunningToPendingAsync` (not yet called from anywhere — see below).
      - `ExclusionMatcher` — default ignores (`.git/`, `node_modules/`, `*.tmp`, etc.) plus
        per-pair extra globs.
      - `LocalScanner` — stat-only recursive walk to `NodeFingerprint`s; skips symlinks and
        files modified in the last 2s (still-being-written guard).
      - `LocalFileHasher` — SHA-1 helper, deliberately *not* called by `LocalScanner` on every
        scan (see its doc comment) — wired up only where `SyncExecutor` needs it.
      - `RemoteScanner` — BFS via `ProtonDriveService.LoadFolderAsync` (confirmed non-recursive,
        Appendix A #4), bounded concurrency (default 3), one wave per depth level.
      - `SyncExecutor` — scans both sides, reconciles, enqueues durably, then executes.
        **`RemoteToLocal` only**; throws `NotSupportedException` for `TwoWay`/`LocalToRemote`.
        Downloads go through a per-operation temp dir then `File.Move` (§7 atomicity), explicitly
        set the local mtime after (Appendix A #6: download doesn't preserve it), and deletions
        move to `<LocalPath>/.mypersonaldrive-trash/<yyyy-MM-dd>/...` — never a permanent delete.

      147 tests pass overall. Beyond the pure-core tests, this includes: `SyncStateStoreTests`
      (pair/baseline/queue/log CRUD, cascade delete, crash recovery), `LocalScannerTests`,
      `ExclusionMatcherTests`, `RemoteScannerTests` (including a concurrency-bound check),
      `SyncExecutorTests` (download+mtime, recursive folder creation, trash-not-delete,
      partial-failure handling, dry-run never touching disk/CLI).

      **Verified against the real CLI and a live account**, not just mocks: created
      `/my-files/f1-executor-test` with a file and a subfolder, ran `SyncExecutor` against a
      real local temp directory — preview and run both matched expectations, downloaded files
      had exactly the right content and the right restored mtime, the folder was created, and
      cleanup (local temp dir, remote test folder) left no trace.

      **Known gaps — two of three now closed:**
      - ~~`ResetRunningToPendingAsync` (crash safety) is implemented and tested but nothing calls
        it yet.~~ **Done.** New `SyncCrashRecovery` service does both halves of §7's startup step
        (requeue `Running` rows + delete each pair's leftover `.mypersonaldrive-tmp`), called
        once from `MainWindowViewModel.InitializeAsync` via
        `SyncPanelViewModel.RecoverFromPreviousRunAsync` — deliberately *not* from the Sync
        window's `InitializeAsync`, which runs on every window open and would requeue rows that
        genuinely are running. A failure there is logged to the activity console, never
        propagated (the browser must still start). 4 tests.
      - ~~`MoveItemAsync` on `ProtonDriveService` still isn't wired up.~~ **Done.**
        `MoveItemsAsync(paths, targetParentPath)` + a single-path convenience overload, matching
        Appendix A #8's verified argument order (sources first, target parent last). 3 tests.
        Not yet *called* by anything — the consumer is F2's `RenameRemote`/`MoveRemote`.
        Argument order re-verified against the live CLI's `--help` (see Appendix A #11b).
      - **Closed as not achievable:** `RemoteScanner` doesn't cache unchanged subtrees, and
        cannot. §6.2's proposed `RemoteFolderEtag` was **unsound as specified** (a folder's
        children-listing hash says nothing about its grandchildren, so a change inside `F/G/H`
        leaves `F`'s listing identical — a silent lost update), and Appendix A #11b then verified
        against the real account that **no propagating signal exists**: folder `modificationTime`
        doesn't move for a descendant change (not even for a direct child), and the CLI has no
        events/delta command. Every remote scan must BFS the whole tree at ~3.5s per folder. This
        doesn't hurt F1/F2's manual "Sync now", but it makes F3's fixed 5-minute polling default
        unworkable — see #11b for what F3 has to do instead.
- [x] **F1 (UI) — done, pending a manual click-through.** New `SyncPanelViewModel` /
      `SyncPairViewModel` (kept out of `MainWindowViewModel` per docs/PLAN-TECH-DEBT.md's
      recommendation), a new `SyncWindow` opened via a 🔁 button in `MainWindow`'s header
      (non-modal, reused on repeat opens rather than stacking windows), an "Add pair" dialog
      (remote path text box + local folder picker via `StorageProvider`, following the existing
      `Request*Async` pattern), and a preview dialog showing the dry-run plan's stats + up to
      50 action lines with a "Run now" button.

      Only lets the user create `RemoteToLocal` pairs (the only direction `SyncExecutor`
      implements); cheap validations at creation time (remote path must start with `/`, local
      path can't be empty/`/`/the home directory — the fuller list in §12, like nested-pair
      detection and free-space estimation, is deliberately not implemented yet).

      **Visually verified** by temporarily wiring an env-var debug switch (reverted before
      commit — see the diff history, not present on disk) that opened `SyncWindow` directly,
      since synthetic mouse input doesn't work in this machine's Wayland session (confirmed via
      `XTestFakeMotionEvent` — the pointer never actually moved). Screenshots confirmed: the
      empty-state window renders correctly, and a pair seeded directly into `cache.db` renders
      its remote/local paths, direction, and formatted "Up to date (<time>)" status correctly,
      with no Avalonia binding errors in the log.
      **UI click-through — attempted 2026-08-01, partially closed.**

      *Correcting this entry's earlier claim*: keyboard injection does **not** work either. The
      earlier note said `XTestFakeKeyEvent` moved focus successfully; re-testing shows it does not
      reach the app at all. Verified carefully rather than assumed: the app runs as an XWayland
      client (`xwininfo` shows an X window) and holds X input focus (`XGetInputFocus`), yet Tab and
      typed characters produced **zero** pixel change, while an `XResizeWindow` on the same window
      was captured immediately — so the capture pipeline is live and it's the input that's dropped.
      Nothing leaked into other windows (the app's own settings file was untouched).

      Tooling notes for the next attempt, all built and left in place under the session scratchpad:
      screenshots need `XGetImage` **on the window** (`ffmpeg -f x11grab` on the X root returns
      solid black under this compositor, and `org.gnome.Shell.Screenshot` answers
      `AccessDenied`). No input tool is installed (`xdotool`/`ydotool`/`wtype`), and no nested X
      server either (`Xvfb`/`Xephyr`) — **installing `xvfb` and running the app on a headless
      display is the most promising route to a fully automated click-through**, since XTest works
      normally there. That needs `sudo`, so it wasn't done unprompted.

      *What did get verified*, via `tests/MyPersonalDrive.Tests/Integration/RealCliSyncPanelTests.cs`
      (gated the same way as the F2 integration test): the panel's view-models driven exactly as
      the window drives them — same services, same real CLI, same `Request*Async` callbacks —
      through the whole flow. Empty state → both §12 validation rejections (non-absolute remote
      path, refusing `$HOME`) → dialog cancellation as a no-op → pair created with the right
      `DirectionText`/`StatusText` → duplicate pair surfacing the friendly UNIQUE message instead
      of a crash → Preview declined leaving the local folder genuinely untouched → Preview
      accepted actually downloading the file and creating the subfolder → status flipping to
      "Up to date" → surviving a panel reload → Remove clearing both the list and the database.
      `AsyncCommand` gained an awaitable `ExecuteAsync` for this (`ICommand.Execute` is
      `async void`, so nothing could tell when a command finished); `Execute` is now a wrapper
      around it and keeps the same crash-proofing.

      **So the remaining gap is narrow but real**: the Avalonia layer itself — XAML bindings, the
      dialogs' own controls, and the `StorageProvider` folder picker — is still only
      screenshot-verified for the panel window, never actually operated.

      ~~**Known-failing**: one of the two integration tests still fails after the concurrency fix;
      which and why is unknown.~~ **Diagnosed and fixed (2026-08-02).** It was
      `RealCliTwoWaySyncTests`, failing ~2 runs in 3, and it was *not* residual `SQLITE_BUSY`: a
      listing issued right after a `trash` still returns the trashed node about two thirds of the
      time (new Appendix A #15). That was hiding one real engine bug — the executor re-read the
      remote side after a deletion and recorded a baseline row claiming the trashed copy was alive
      — plus one over-strict test assertion, which now polls for convergence instead of assuming
      it. **Three consecutive green runs** after the fix, with the poll data confirming the cause
      (2 of the 3 needed a second listing attempt). Appendix A #15 also records a *still-open*
      consequence: a deleted file can transiently resurrect if you sync again inside the staleness
      window.
- [x] **F2 — done (manual bidirectional sync).** 184 unit tests pass plus one gated integration
      test against the real account; the AOT publish is clean.

      Worth recording up front: **the reconciler already implemented the whole of §5.2, TwoWay
      and all four conflict policies included, back in F1** — its status entry undersold it. So
      F2 turned out to be almost entirely executor and UI work, not engine work.

      - `SyncExecutor` no longer throws for `TwoWay`/`LocalToRemote`. New operations:
        `UploadFile` (with the CLI's `replace` strategy — the plan already decided this version
        wins, so letting the CLI make its own "keep both" copy would contradict it),
        `CreateRemoteFolder` (treats `AlreadyExists` as success, so a retried run is idempotent),
        `TrashRemote`, `ResolveConflictKeepBoth`, `UpdateBaselineOnly` and `ClearBaseline`.
      - `SyncBaselineWriter` — §7's "only after confirmed success, and by re-reading the real
        fingerprint of both sides". Re-reading is not pedantry: an upload mints a new remote
        revision whose `uid`/hash we cannot predict. It caches remote listings per parent folder
        and invalidates only folders the run wrote to, so a run uploading 40 files into one folder
        pays one extra ~3.5s listing, not 40.
      - `ResolveConflictKeepBoth` renames the local copy aside **before** downloading, not after:
        if the download then fails, the local version still exists under the conflict name.
        Downloading first would overwrite it — the one ordering that can lose data.
      - `SyncEchoSuppressor` — §9's echo suppression, built for the remote half first because
        Appendix A #15's stale-listing-after-trash made deleted files resurrect. Generic over
        `SyncSide` so F3's watcher reuses it rather than growing a second mechanism.
      - `SyncRetryPolicy` — §7's 5s/15s/45s/2min/5min schedule plus classification. Retryable:
        `Network`, `Timeout`, `Unknown`, local `IOException`. Not retryable: `NotAuthenticated`,
        `Quota`, `NotFound`, `PermissionDenied` (a retry cannot fix any of them; `NotFound` means
        the node moved, which the next scan resolves correctly). `NotAuthenticated`/`Quota`
        additionally **abort the run** rather than failing 400 rows identically at ~3.5s each.
      - Fixed while implementing: `SyncStateStore.FormatTimestamp` now normalizes to UTC.
        `GetPendingActionsAsync`'s new `NextAttemptAt <= @Now` filter is a *string* comparison, and
        round-tripping the caller's offset made `10:00-03:00` sort before `09:00Z` despite being
        four hours later — which would have handed retry rows out early. Test pins it.
      - `Ask` policy conflicts are parked as durable `SyncQueue` rows in `Conflict` state
        (`EnqueueConflictsAsync`/`GetConflictActionsAsync`) instead of being dropped.
      - UI: the add-pair dialog now offers direction and conflict policy (`RemoteToLocal` stays
        the default — §15's reasoning is unchanged), hiding the policy selector in one-way modes
        where no such decision exists. The preview dialog reports uploads and remote-trash counts,
        and no longer claims "already up to date" for a plan whose only content is conflicts.

      **Known gaps, deliberately left to later phases:**
      - **`Ask` conflicts can be parked but not yet resolved** — the conflicts panel is F4. Until
        then `Ask` means "keep noticing the conflict every run"; `KeepBoth` is the policy that
        actually resolves things unattended, and it's the one to recommend.
      - **Retries land on the next run, not within the current one.** The row goes back to
        `Pending` with a `NextAttemptAt`; nothing waits out a backoff mid-run. That's coherent for
        F2's manual "Sync now" and becomes automatic once F3's scheduler exists.
      - Rename/move detection is still F5, so `MoveItemsAsync` remains uncalled: a remote move is
        currently a download+trash pair. Correct, just more expensive than it needs to be.
      - The recursive folder `download` from Appendix A #5 is still unused; the first sync of a
        pair downloads file-by-file.
      - F1's UI click-through is still outstanding (see F1's entry) — F2 added the direction and
        conflict-policy selectors to that same untested dialog.

      **Verified end-to-end against the real CLI and a live account** (2026-08-01), not only mocks.
      `tests/MyPersonalDrive.Tests/Integration/RealCliTwoWaySyncTests.cs` drives a four-run TwoWay
      lifecycle inside a throwaway remote folder and trashes it afterward; it is skipped unless
      `MYPERSONALDRIVE_INTEGRATION=1` (it takes ~1m45s — ~30 CLI calls at ~3.5s each — and needs an
      interactive `auth login` first). Confirmed on the real account:
      - run 1: a remote-only file downloads, a local-only file uploads, a local-only folder is
        created remotely, and the uploaded file's `claimedModificationTime` comes back matching the
        local mtime we set (which is what keeps the baseline stable instead of drifting each run);
      - run 2: **zero actions** — the baseline genuinely works against real CLI fingerprints;
      - run 3: a local edit uploads with `-c replace` and leaves **exactly one** remote copy (no
        "keep both" sibling), at the new size;
      - run 4: a local delete trashes the remote copy; `filesystem delete` is never issued.

      **It also caught a real bug that every mock-based test missed**: `TrashRemote` fell through to
      the shared post-success baseline write, where both sides are now absent, and upserted a
      both-null row — a baseline entry claiming to know a path that exists nowhere. It self-healed
      (the next run emits `ClearBaseline`) but cost a wasted queue item and run per deletion.
      `SyncBaselineWriter.RecordAsync` now deletes the row when both sides are gone. A unit test
      pins it so the fix doesn't depend on the gated integration run.
- [x] **F3 — done (automatic sync).** 236 unit tests plus four gated real-account integration
      tests; AOT publish clean.

      - `ChangeDebouncer` — §6.3's mandatory debounce, kept pure and clock-injected so the "one
        editor save must not become six syncs" and "a long copy yields nothing until it stops"
        cases are tested without touching a disk or sleeping.
      - `LocalFileWatcher` — thin adapter over `FileSystemWatcher`: 64 KB buffer, both ends of a
        rename, exclusions applied before anything else, and the `Error` (overflow) event flagging
        a full scan and *discarding* the partial batch rather than acting on half a picture. An
        unwatchable folder (typically the inotify limit) degrades to polling with a message naming
        the sysctl, instead of failing the pair.
      - `SyncSchedulePolicy` — pure, and the piece Appendix A #11b forced a redesign of. The
        interval is **10× the pair's own last cycle duration, floored at 5 min and capped at 1 h**,
        so a small pair polls at the floor and a 50-folder tree backs itself off to ~30 min with no
        configuration. A dirty pair skips the wait (the debounce already waited) but still respects
        the error backoff, so a broken pair can't spin.
      - `SyncScheduler` — owns the watchers, runs due cycles, and **runs them strictly one at a
        time across all pairs**: per Appendix A #11 concurrent CLI processes corrupt each other, so
        the plan's per-pair lock is subsumed by a single global one. Global pause when
        unauthenticated, error backoff with the wait written into `SyncLog`, and pairs
        added/removed/paused in the UI picked up without a restart. Its `PumpOnceAsync` is the unit
        of work the timer loop wraps, which is what makes the scheduler deterministically testable.
      - §9's echo suppression completed: `SyncEchoSuppressor` gained a second register for the
        engine's own *writes*, kept separate from deletions because the two need opposite
        treatment — a deletion we performed must be filtered *out* of a scan, whereas a file we
        just downloaded really is on disk and must not be (filtering it would make the reconciler
        re-download it or think the user deleted it). The executor registers downloads, local
        folder creations and conflict renames **before** writing, since a suppression that arrives
        after the watcher event is no suppression at all.
      - Wiring: one shared suppressor for executor and watchers (two instances would defeat it),
        the scheduler starting only *after* crash recovery has requeued stale rows, a
        shutdown hook so a cycle isn't killed mid-transfer, and an "Automatic sync: on/off" toggle
        in the sync window. `ObservableObject` gained `OnPropertyChanged` for computed properties.

      **Verified end-to-end against the real account**: `RealCliAutoSyncTests` writes a local file
      and then only *ticks the scheduler* — it never calls `RunAsync` — so passing means the whole
      chain fired on its own: real inotify event → debounce → pair marked dirty → policy deeming it
      due → executor → upload. It reached Proton Drive on the first attempt. The clock is faked so
      the 2s debounce and 5-minute floor cost no real time, while the filesystem and CLI are real.

      **Known gaps:**
      - `NeedsFullScan` is plumbed from the overflow event but the executor always does a full scan
        anyway, so it's currently only informational. It becomes load-bearing if an incremental
        local scan is ever added.
      - The watcher marks the pair dirty without saying *what* changed, so a one-file edit still
        triggers a full remote scan (~3.5s per folder). Fine for the tree sizes this targets;
        revisit with F4's progress work if it bites.
      - The scheduler has no UI beyond the on/off toggle — no "next sync at", no per-pair pause
        button (the `IsPaused` column and `SetPairPausedAsync` exist and are respected, just not
        exposed). F4 owns that.
- [x] **Adversarial review of F2/F3 (2026-08-02).** A hostile pass over the sync code found three
      real defects, none of which the existing 236 tests caught. Two of them were latent before F3
      and became live the moment sync started running unattended. All three are fixed; 246 unit
      tests, four real-account integration tests, AOT publish clean.

      1. **No global gate on CLI processes — the worst of the three.** Appendix A #11 measured that
         concurrent `proton-drive` processes crash each other ~1 time in 3, and §9 required a global
         semaphore that was never built. Three independent producers existed — the scheduler's
         cycles, the panel's "Sync now"/"Preview", and the file browser — and the only
         serialization (`SyncScheduler._oneCycleAtATime`) covered just the first, since the UI calls
         `SyncExecutor` directly. Before F3 a collision needed deliberate timing; afterwards the
         scheduler fires on its own every five minutes, so ordinary browsing raced a sync routinely.
         Fixed in `ProtonDriveCliExecutor.ExecuteAsync`, the one place every invocation passes
         through. Held per *invocation* (~3.5s), never per cycle, so an interactive click waits
         behind at most one command — which is why §9's interactive-priority queue is still
         deferred rather than needed. Proven with a `mkdir`-lock probe against the real executor
         (6 concurrent calls; any overlap makes one fail).
      2. **The queue grew without bound and the work was quadratic.** `EnqueueActionsAsync` inserted
         blindly, and every run re-proposes actions that haven't succeeded yet — so run N left N
         rows for the same path, each with a *fresh* retry budget, and the queue never drained.
         Measured CLI attempts across five runs: 1, 3, 6, 10, 15 (triangular). At F3's cadence that
         is ~288 runs a day for one unfixable file. Now at most one live row per
         (pair, path, operation): a live row means "already scheduled" and is skipped, a `Failed`
         row is revived with a clean attempt count (the only recovery path until F4 has a retry
         button), and `Done` never blocks a genuine repeat. Plus `PruneCompletedAsync`, called each
         run, so the table tracks outstanding work rather than uptime. Regression test asserts the
         attempts-per-run sequence is flat.
      3. **`Ask` conflicts were re-reported every run**, same blind insert. Since resolving them
         needs F4's panel they can't be cleared, so the queue grew every five minutes forever. Now
         one row per conflicting path.

      **Left unfixed, deliberately** (all minor, all recorded here rather than in a comment):
      - `LocalFileWatcher.NeedsFullScan` is set on buffer overflow and never reset — only the
         scheduler's copy is. Once overflowed, every later batch re-flags it. Harmless today because
         the flag is purely informational (the executor always does a full scan anyway); a trap if
         an incremental local scan is ever added.
      - ~~`SyncLog` still has no pruning.~~ **Done** — see the F4 pruning entry above.
      - Two benign data races in `SyncScheduler`: `PairRuntime.IsDirty` is written from watcher
         threadpool threads and read from the loop, and `_pairs` is mutated by the loop while
         `PumpOnceAsync` is public. Correct today because only the loop calls it in the app; worth
         locking before anything else calls in.
      - ~~`RefreshPairsAsync` re-queries the pair list on every 2s tick while no pairs exist.~~
         **Done** alongside B2/B3: the guard is now purely time-based.
- [x] **F4 (conflicts panel) — done.** The sharpest gap F2/F3 left: the add-pair dialog offers
      `Ask` as the *default* policy, and an `Ask` conflict was a dead end — parked as a `Conflict`
      row with no way to act on it. 262 unit tests, four real-account integration tests, AOT clean.

      - `SyncExecutor.ResolveConflictAsync(pair, conflictRow, ConflictResolution)` carries out one
        decision: `KeepLocal` uploads, `KeepRemote` downloads, `KeepBoth` reuses the same
        rename-aside-then-download-then-upload path (and the same stamped copy name) as the
        automatic policy, so a hand-resolved file is indistinguishable from a policy-resolved one.
        Deliberately **not** a full cycle: only the conflicting file's own parent folder is
        re-listed, since a decision about one path shouldn't pay for a whole-tree walk at ~3.5s per
        folder. A test asserts sibling folders are never listed.
      - `RetryFailedAsync` + `GetFailedActionsAsync`, surfaced as "↻ Retry failed actions" per row —
        the manual recovery path for rows that exhausted their retries, which until now depended on
        the plan happening to re-propose them.
      - UI: a `⚠ N conflicts` button per pair opening a dialog that lists every conflicting file
        with its reason in plain language and a per-file choice. **Every file starts on "Decide
        later"** and dismissing the dialog resolves nothing — these are precisely the cases the
        engine refused to guess at, so the dialog must not guess either.
      - One file failing does not abandon the rest: each resolution is an independent decision the
        user already made, so failures are reported per path and the others still go through.

      **Found and fixed while building it:** nothing ever cleared a `Conflict` row once the
      underlying conflict was gone. Resolve the difference by any other means — editing files by
      hand, another client, the policy changing — and the row (and therefore the badge count) would
      have survived forever, so the conflict count could only grow and would eventually be pure
      fiction. `ClearStaleConflictsAsync` now runs each cycle against the paths the *current*
      reconciliation still finds in conflict.
- [x] **F4 (nested-pair detection) — done.** §12's overlap rule, as a hard error on *both* sides —
      the reasoning for each is in §12, and the remote-side one is the more interesting: echo
      suppression is keyed per pair, so two pairs over one remote subtree can undo each other's
      deletions, reintroducing across pairs exactly the resurrection bug Appendix A #15's fix
      removed. Extracted into a pure `SyncPairValidator` along with the checks that were previously
      private to the view-model, so all of them are now covered exhaustively (22 tests) instead of
      only incidentally through a gated integration run. Validated against the pairs in the
      *database*, not the panel's loaded list, since the scheduler and other windows share it.
      Overlap is compared on normalized paths, so alternate spellings of one folder match while a
      prefix-sharing sibling correctly doesn't.
- [x] **F4 (per-pair pause) — done.** The column, the store method and the policy check already
      existed; what was missing was exposing it and two judgement calls:
      - **Pause stops automatic cycles only.** Preview and "Sync now" stay available, because pausing
        expresses "stop doing this on your own", not "refuse my explicit instructions" — §12 lists
        pause and sync-now as separate controls on the same row.
      - **A paused pair leads its status with the pause**, ahead of the last result. "Up to date" on
        a frozen pair becomes a lie the moment anything changes, so the pause is the fact that
        decides whether the rest of the line is still being kept true.

      The scheduler picks a pause up because it re-reads pair state each refresh rather than trusting
      its startup snapshot — tested by pausing through the store while the scheduler is live, which
      is exactly how the UI does it, and by resuming again.
- [x] **F4 (`SyncLog` pruning) — done.** Two limits, since either alone leaves a hole: 30 days of
      age (which stops a pair that has gone quiet keeping stale history) and **1000 rows per pair**,
      which is the limit that actually bounds the table — an age cap alone does nothing about a
      chatty pair inside the window. The count is per pair so one busy pair can't push another's
      history out, and the scheduler's own rows (null `PairId`) form their own group rather than
      competing for a pair's allowance.

      Housekeeping moved to the **start** of a cycle, before anything that can throw, and it now
      covers the queue prune too. A pair whose scan fails every cycle is exactly the one generating
      the most log noise, and with pruning sitting after the scan it would have been the one pair
      that never got tidied. Best-effort by design: failing to tidy is not a reason to refuse to
      sync, and reporting it would mean writing to the table we just failed to write to.
- [x] **F5 (remote rename/move detection) — done.** A remote rename or move no longer costs a
      re-download. 317 unit tests, four real-account integration tests, AOT clean.

      Implemented as a **cross-path pre-pass** in the reconciler, deliberately separate from the
      per-path decision table: a move is the one situation §5.2 cannot express, because it is a
      statement about two paths at once. Paths the pre-pass claims are excluded from the main loop,
      which would otherwise see the source as "deleted remotely" and the destination as "new
      remotely" and answer with a delete-plus-download of content that never changed. Everything else
      goes through the table untouched — all 300-odd existing tests passed unchanged after the
      pre-pass landed, which was the point of that shape.

      Keyed on the CLI's `uid` (Appendix A #3 verified it survives both `rename` and `move`), so this
      is identity rather than a guess. **Every condition is a refusal to guess**, since falling back
      merely costs a download while a wrong move costs data:
      - a `uid` appearing more than once remotely is ambiguous → fall back;
      - content that changed as well as moved → fall back (a move plus an edit isn't worth a case);
      - the local file edited since the baseline → fall back, or moving it would discard the edit;
      - anything already at the destination locally → fall back, or the move would overwrite it.

      `SyncPlanStats` gained `FilesToMoveLocally`, counted separately because these cost no bytes:
      folding them into the download count would misreport the work, and omitting them would make a
      plan that only moves files look like it does nothing.

      **Verified against the real account**, which is where it matters — the whole optimization rests
      on the `uid` surviving a real `filesystem rename`. The integration test renames a file remotely,
      counts `download` invocations off the live command stream, and asserts the count is unchanged
      while the local file has moved and the baseline followed it to the new path.

      **The local→remote half is deliberately not done.** Detecting a *local* rename needs local
      identity, and §11.2's `st_ino` isn't reachable from .NET without a platform P/Invoke. The
      workable alternative is §11.3's content-hash match — a path that vanished locally and one that
      appeared with the baseline's recorded SHA-1, with exactly one candidate — which needs no native
      code and would then use the already-written-but-still-uncalled `MoveItemsAsync`. Also still
      absent for `RemoteToLocal` pairs, which keep no baseline by design and so have no identity to
      correlate against; making it work there would mean matching on (size, mtime) alone, which is
      the guess §11.3 says to refuse.
- [x] **F4 (remainder) — done. F4 complete.** 336 unit tests, four real-account integration tests,
      AOT clean.

      - **§12's IO validations**, in a new `LocalFolderInspector` kept apart from the pure
        `SyncPairValidator`. Writability is checked by *probing* rather than by inspecting
        permissions, since ACLs, mount options and read-only filesystems all give the same practical
        answer and only a write attempt covers all three; a missing folder is created rather than
        rejected, because the executor would create it on the first run anyway. A folder that already
        holds more than 100 items now asks for confirmation — but only for directions that would
        *send* those files, since a `RemoteToLocal` pair never uploads and its preview shows any local
        deletions first. The confirmation dialog defaults to Cancel: every question routed through it
        warns about something big, so a stray Enter should pick the safe answer.
      - **Free space is checked at preview time, not at pair creation**, which deviates from where
        §12 lists it. The byte total only exists once the remote side has been scanned, and scanning
        inside the add-pair dialog would cost a full tree walk at ~3.5s per folder before the user had
        confirmed anything. The preview is where the numbers are known and where the decision is made.
        Warnings travel with the plan into the same dialog, because "not enough disk space" only means
        something next to the byte count it refers to. 10% headroom, since a download also needs room
        for its temp copy before the move into place.
      - **Per-action progress** (§12's "⟳ Syncing 12/48"). Per action rather than per byte because
        Appendix A #12 never established whether the CLI emits parseable transfer progress — and the
        action count answers the question a user actually has, which is whether anything is happening.
        Reported *before* each action starts, since each can take seconds and a counter that only
        moved on completion would leave the slowest item invisible for exactly as long as it was the
        one being waited on. Failures count too: the counter measures progress through the queue, not
        successes. An idle cycle reports nothing at all, so the row doesn't flicker.

- [x] **B1 (node names containing `/`) — done.** Two distinct problems hid behind one backlog line.

      **The CLI argument.** `CombinePath` concatenated parent and name with `/` and never escaped the
      name's own slashes, so a node genuinely called `in/voice` produced a path indistinguishable from
      a folder `in` containing `voice`. Verified against the real account before writing anything:
      Proton happily accepts such a folder, the escaped path lists it (exit 0), and the unescaped one
      fails with **`Node not found: in`** — so every browser command aimed at such a node (download,
      rename, trash) was broken. `CombinePath` now escapes as the CLI documents.

      **The sync engine's identity model, which was the deeper half.** On Linux `/` is the one byte a
      filename may not contain, so such a node *cannot* be mirrored locally under its real name.
      `RemoteScanner` therefore skips it and reports it, and the executor logs a warning naming the
      file and what to do (rename it in Proton Drive). Skipping is the honest answer: the alternative
      is inventing a substitute name, which in a `TwoWay` pair would upload back as a second,
      differently-named copy. It also keeps every relative path in the engine unambiguously
      `/`-separated, so no path-splitting code had to learn about escapes. A folder with an unmappable
      name isn't descended into either, since its children are equally unrepresentable.

      Previously this produced a download aimed at an unresolvable path: a permanently failing queue
      row and an inscrutable error, once per such file, forever.

- [x] **B2 + B3 (scheduler thread safety) — done.**

      **B2, the `_pairs` dictionary.** Now guarded by a lock, never held across an `await`: each use
      takes a snapshot and works outside it, and watchers are started and disposed outside it too.
      **The fix is validated, but by a one-off experiment rather than a standing test.** A test that
      reproduced the race *was* written and did have teeth — with the locks removed it failed with
      `InvalidOperationException: Collection was modified`, which is exactly the bug — but it was
      deleted rather than kept. Reproducing the race needs several threads hammering the scheduler,
      and that starved the threadpool badly enough that `FileSystemWatcher` callbacks stopped being
      scheduled at all, breaking the watcher tests for reasons unrelated to the watcher. It also took
      the suite from 4s to 35s. A test that destabilizes its neighbours to guard a rare race is a bad
      trade; the experiment is recorded here instead.

      **B2, the two cross-thread flags.** `PairRuntime.IsDirty` and `NeedsFullScan` are written by
      watcher callbacks on threadpool threads and read by the loop, so both are now `volatile`.
      Without it the loop could keep reading a cached `false` and never notice a reported change.
      Also not unit-testable — memory visibility isn't observable from a test.

      **B3, the overflow latch.** `LocalFileWatcher.NeedsFullScan` is set once on buffer overflow and
      never resets itself; only the scheduler's copy was being cleared, so one overflow made every
      later batch re-raise "needs a full scan" for the rest of the process's life. The scheduler now
      clears the watcher's latch too, and the property is `volatile` for the same reason as above (the
      OS raises `Error` on a threadpool thread).

      Also fixed in passing: `RefreshPairsAsync`'s guard required `_pairs.Count > 0`, so with no pairs
      configured it never held and the loop re-queried the database every 2s forever, for nothing. It
      is now purely time-based.

      One test change was needed and is worth noting: `LocalFileWatcherTests` waited only for the
      *first* real event before releasing the debounce, but one action can produce several — a rename
      produces two — so a short grace after the first arrival was added. That fragility was pre-existing
      and only surfaced under the load the deleted test created.

- [x] **B4 (local→remote rename detection) — done. §11 is now complete in both directions.**
      361 unit tests, four real-account integration tests, AOT clean.

      This half has no stable local id to key on — §11.2's `st_ino` isn't reachable from .NET without a
      platform P/Invoke — so it uses §11.3's content match, in its strong form only: **size and SHA-1
      must both equal what the baseline recorded**, with exactly one candidate. Never mtime, and never
      size alone: a rename doesn't change either, but neither does an unrelated file that happens to be
      the same length, and matching on those would make this a guess instead of a match.

      **Where the hashes come from.** The reconciler is pure and `LocalScanner` is stat-only, so
      neither can hash. `SyncExecutor` fills in `ContentHash` before reconciling, narrowed twice so the
      one expensive step stays cheap: only files that are new since the baseline, and among those only
      the ones whose **size** matches something that vanished from the baseline. A move preserves size
      exactly, so a size matching nothing cannot be a move. A first sync hashes nothing at all — with
      an empty baseline nothing has disappeared.

      **Execution needs up to two CLI calls**, because each command holds one thing fixed: `move`
      keeps the name and changes the parent, `rename` keeps the parent and changes the name. Same
      parent → rename; same name → move; both changing → move *then* rename, so the node reaches its
      destination folder before taking its final name. A failure between the two leaves it in the right
      folder under the old name — nothing lost, and the next cycle plans from what it actually finds.

      Also suppresses the old remote path as a deletion, for the same reason a trash does (Appendix A
      #15): a stale listing still reporting it, with the baseline row already moved, reads as "new
      remotely" and would download the file back under its old name.

      **Verified against the real account in both directions in one run**: a remote rename produces
      `RenameLocal` with no downloads, and a local rename produces `RenameRemote` with no uploads —
      both counted off the live command stream rather than assumed.

Added during implementation, not in the original §3.2 model list: `SyncOperation.ClearBaseline`
(the "both sides deleted it" decision-table row needs a distinct effect — delete the stale
`SyncState` row — from `UpdateBaselineOnly`, which means "record the current state").

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
/// Per Appendix A #3/#14 (verified against the real CLI): on the remote side, `NodeId` is the
/// CLI's stable `uid` (survives rename/move) and `ContentHash` is `activeRevision.value
/// .claimedDigests.sha1` when present. On the local side, `NodeId` is left null (or `st_ino`,
/// see §11) and `ContentHash` is a locally-computed SHA-1, so the two sides are directly
/// comparable without conversion.
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

### 5.4 Change detection — hash-first (updated after F0 #14)

F0 confirmed the remote side exposes `activeRevision.value.claimedDigests.sha1`, a SHA-1 the
CLI's own client computed from the original local content at upload time, and it matched a
locally-computed `sha1sum` exactly in testing. That upgrades hashing from "expensive optional
tie-breaker" to the primary criterion:

- **When the remote fingerprint has a hash**: a local file is unchanged iff `(Size, SHA-1)` both
  match the baseline/remote fingerprint. Compute the local SHA-1 with one file read — cheap
  relative to the ~3.5s CLI round-trip per command (Appendix A #11a), so there's no real cost
  argument for skipping it. This eliminates mtime-tolerance edge cases entirely for these files:
  clock skew, `touch` without content change, and the empty-folder-style false positives all
  become non-issues because content identity is exact.
- **When it doesn't** (older revisions predating this field, or a non-standard upload path):
  fall back to `Size` differs **or** `ModifiedAt` differs beyond a tolerance (§5.5).
- `SyncState.ContentHash` caches the local SHA-1, invalidated whenever `(size, mtime, inode)`
  changes, so a file that hasn't been touched since the last scan never gets re-hashed.

This does not change the *shape* of the decision table in §5.2 (`changed(X, B)` is still one
predicate) — it changes what `changed` means: hash-equality when available, size+mtime-tolerance
otherwise.

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

Implemented, with two rules worth stating explicitly:
- **A parked conflict is cleared as soon as it stops being one**, whatever resolved it — the panel,
  an edit by hand, another client. Nothing else removes a `Conflict` row, so without this the
  conflict count only ever grows and eventually reports fiction.
- **The dialog defaults every file to "Decide later."** These are exactly the cases the engine
  refused to guess at; a pre-selected action would quietly reintroduce the guess, and dismissing the
  dialog must resolve nothing.

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

~~Optimization: store a synthetic `RemoteFolderEtag` (a hash of the children listing) in
`SyncState` to skip unchanged subtrees between cycles. Only valid if the remote listing is
order-stable.~~ **Retracted — this is not correct.** A folder's children-listing hash says
nothing about its *grandchildren*: a file changed inside `F/G/H` leaves `F`'s listing identical,
so skipping the `F` subtree on an unchanged etag silently misses the change. And since computing
the etag requires listing `F`, it saves no call at `F`'s own level either. Order-stability was
never the real problem.

What a correct subtree skip needs is a signal that **propagates upward** from a descendant change.
**Appendix A #11b tested for one and found none**: a folder's `modificationTime` does not move when
a descendant changes — not even when its own direct child does — and the CLI has no events/delta
command. So cross-cycle subtree caching is off the table entirely with this CLI, and the remaining
levers are: scale the polling interval to the pair's last observed scan duration, and prefer
user-triggered remote scans over periodic ones on large trees.

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

- ~~Configurable interval, default **5 minutes**~~ — **revised by Appendix A #11b.** A remote scan
  cannot be incremental (no propagating change signal exists, verified), so every cycle costs
  ~3.5s × folder count. A fixed 5-minute default would have a 50-folder pair scanning ~3 minutes
  out of every 5. The interval must be **derived from the pair's last observed scan duration**
  (e.g. at least 10× it, floor 5 min), and for large trees remote scanning should lean on the
  user's "Sync now" instead of a timer. Exponential backoff up to 30 min after consecutive errors
  still applies on top.
- Manual sync always available ("Sync now" per pair and globally).
- Never two cycles of the same pair in parallel (per-pair lock).
- Automatic global pause if `IsAuthenticated == false`.

---

## 7. Transfer execution

`TransferQueue`:

- Consumes `SyncQueue` ordered by `(Priority, Id)`.
- **At most one live row per `(PairId, RelativePath, Operation)`.** Not an optimization: every run
  re-proposes work that hasn't succeeded, so blind enqueueing made the workload quadratic and
  non-convergent (see the adversarial-review entry in Status). Completed rows are pruned each run.
- Bounded concurrency via `SemaphoreSlim`, default value **1** — **not** the 2 originally planned.
  Appendix A #11 (re-tested) found concurrent `proton-drive` processes crash on the CLI's own
  SQLite cache, so the queue must serialize CLI calls entirely.
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
- The file browser and sync engine **share the CLI**: a global semaphore over `proton-drive`
  processes is **mandatory, with exactly one slot** — not a tuning knob for responsiveness.
  Appendix A #11 (re-tested) found concurrent CLI processes crash each other on the CLI's internal
  SQLite cache, so two simultaneous invocations are a correctness problem, not just a slow one.
  **Give priority to interactive operations** (the browser's request jumps the sync queue), which
  matters more now that the slot count is 1.
- When sync modifies the remote side, invalidate the `DriveItems` for the affected folder so
  the browser doesn't show stale data.
- When sync writes locally, **suppress the watcher events it generates itself**: keep a set of
  "written by the engine" paths with a short TTL, and discard matching events. Without this,
  an infinite sync loop occurs. **This is the classic bug for this feature.**

  **Partly built already**, as `SyncEchoSuppressor` — F2 needed the *remote* half of exactly this
  mechanism, because a listing right after a `trash` comes back stale (Appendix A #15) and made
  deleted files resurrect. It is keyed by `(pairId, SyncSide, relativePath)` with a 60s TTL,
  suppresses whole subtrees for folder deletions, and releases an entry early once a scan agrees.
  The executor already reports its local deletions into it. **What F3 must add**: report the
  engine's local *writes* (downloads, conflict renames) into it too, and have `LocalFileWatcher`
  consult it before waking the scheduler. Do not build a second mechanism for this.
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

## 11. The rename / identity problem — resolved by F0

*(Original framing kept below for context; F0 §Appendix A #3/#8 resolved this outright rather
than requiring the heuristic mitigation this section originally proposed.)*

With the path as the only identity, moving `a/x.pdf` → `b/x.pdf` remotely would look like
`delete a/x.pdf` + `create b/x.pdf`, and sync would **download the file again** (correct but
expensive) or, worse, in TwoWay mode **re-upload the original from the local baseline**.

**F0 verified the CLI's `uid` is stable across both `filesystem rename` and `filesystem move`**,
and that `filesystem move` exists as a direct operation (not just rename+copy+trash). So:

**Status**: implemented for `TwoWay` pairs in both directions. Mechanism 1 (the `uid`) drives remote
moves; mechanism 3 (content match) drives local ones, because mechanism 2's `st_ino` isn't reachable
from .NET without a platform P/Invoke — so 3 turned out to be the *primary* local mechanism rather
than a fallback. One-way pairs still transfer, since they keep no baseline to correlate against.

1. **Primary mechanism (verified, use this)**: `SyncState.RemoteNodeId` = the CLI's `uid`.
   Reconciliation on the remote side is indexed by `uid`, not path. When a `uid` known from the
   baseline reappears at a different path with unchanged `(size, hash)`, emit `RenameRemote` /
   `MoveRemote` (a single `filesystem move` or `filesystem rename` call) instead of a
   delete+download pair.
2. **Local-side equivalent**: `st_ino` (Unix inode) plays the same role for detecting a local
   rename/move — a path that disappeared and one that appeared with the same inode and
   unchanged content is a rename, not a delete+create.
3. **Fallback, now only relevant if a future CLI version or edge case lacks a `uid`** (not
   observed in F0 testing): treat a disappeared/appeared pair with identical `(size, hash-or-mtime)`
   and matching base name as a probable rename, but **fall back to delete+create when there's
   ambiguity** (more than one candidate) — never guess when there's more than one match.

Cross-cutting safety rule, unchanged and still important regardless of which path above applies:
**the engine must always use `trash`, never a permanent delete**, so a wrong rename/delete
decision is recoverable.

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

Implemented in `SyncPairValidator` (pure, so the rules are covered exhaustively without IO):

- [x] It's not nested inside (nor contains) another existing pair — **on both sides**, see below.
- [x] It's not `$HOME` or `/`, and the remote path is absolute.
- [x] The local folder exists and is writable — `LocalFolderInspector`, checked by probing.
- [x] Warn if it already contains many files, for directions that would upload them.
- [x] Sufficient free space — evaluated at **preview** time, not here; see the F4 entry for why.

**Why overlap is a hard error rather than a warning**, on each side:

- **Overlapping local folders are destructive.** With `~/A ↔ /my-files/X` and
  `~/A/Sub ↔ /my-files/Y`, the first pair's scanner walks `~/A/Sub` as well; its own remote root has
  no `Sub`, so it reads the folder as deleted remotely and moves it to the local trash — which the
  second pair downloads again, forever.
- **Overlapping remote folders break echo suppression**, which is keyed per pair
  (`SyncEchoSuppressor`). Pair A cannot know pair B just trashed a node, so it sees it still listed
  (Appendix A #15's stale listing), reads that as "new remotely", and downloads back what the other
  pair deleted — the resurrection bug that fix removed, reappearing across pairs.

Overlap is compared on normalized paths, so `/home/user/Docs`, `/home/user/Docs/`,
`/home/user/./Docs` and `/home/user/Downloads/../Docs` all match, while `/home/user/Docs2` correctly
does not. Comparison is ordinal (case-sensitive) since Linux is; on a case-insensitive filesystem two
differently-cased spellings of one folder would slip through, which matters only if this ever ships
for Windows or macOS.

---

## 13. Phasing and estimate

| Phase | Content | Usable deliverable | Estimate |
|---|---|---|---|
| **F0** | CLI investigation (§2) + Appendix A | Decisions document | 0.5 d |
| **F0.5** | Preliminary refactors (§8): `CliException`, fix the listing parser, SQLite migrations, typed `ModifiedAt`, test project | Sound baseline, no functional change | 1.5 d |
| **F1** | `PathMapper`, `LocalScanner`, `RemoteScanner`, `SyncStateStore`, `SyncReconciler` (`RemoteToLocal` only), download `SyncExecutor`, minimal UI with dry-run and manual button | **Manual download mirror**: already useful as a local backup | 3 d |
| **F2** | Full baseline, the whole §5.2 table, uploads, deletes with trash, `KeepBoth` conflicts, durable `SyncQueue` with retries | **Manual bidirectional sync** | 4 d |
| **F3** | `LocalFileWatcher` with debounce and echo suppression, `SyncScheduler` with polling and backoff, automatic startup | **Automatic sync** | 2.5 d |
| | *Done. The fixed-interval polling in the original scope was replaced by an interval derived from each pair's measured cycle cost — Appendix A #11b ruled out incremental remote scans, which is what a fixed 5-minute default assumed.* | | |
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

## 16. Backlog — deferred, with enough detail to pick up cold

Everything below was left out on purpose, and the reason is recorded next to each item. Consolidated
here because it had accumulated across seven different Status entries, which is documented but not
actionable as a set. Roughly in the order worth doing.

### Correctness / data safety

| # | Item | Why it was deferred, and what to do |
|---|---|---|
| ~~B1~~ | ~~**A literal `/` inside a node name isn't escaped.**~~ **Done** — see the B1 entry in Status. Confirmed against the real account first: Proton accepts a folder named `in/voice`, the escaped path lists it (exit 0) and the unescaped one fails with `Node not found: in`. |
| ~~B2~~ | ~~**Two data races in `SyncScheduler`.**~~ **Done** — see the B2/B3 entry in Status. |
| ~~B3~~ | ~~**`LocalFileWatcher.NeedsFullScan` is never reset.**~~ **Done** — see the B2/B3 entry in Status. |

### Features

| # | Item | Why it was deferred, and what to do |
|---|---|---|
| ~~B4~~ | ~~**Local→remote rename detection** (§11's other half).~~ **Done** — see the B4 entry in Status. `MoveItemsAsync` is finally called. |
| B5 | **Rename detection for `RemoteToLocal` pairs.** | They keep no baseline by design, so there's no identity to correlate and a remote move still costs a delete+download. Making it work would mean matching on `(size, mtime)` with nothing to confirm it — the guess §11.3 says to refuse. Would require giving one-way pairs a baseline purely for identity. |
| B6 | **Fine-grained transfer progress.** | Appendix A #12 was never tested: whether `upload`/`download` emit parseable progress on stdout is still unknown. `CliCommandOutputEventArgs` already streams lines, so the plumbing exists. Per-*action* progress ("12 of 48") needs none of that and is the bigger win — see §12. |
| B7 | **Interactive priority over the CLI gate.** | §9 wants the browser's requests served ahead of sync's. Not built because the semaphore is held per *invocation* (~3.5s), so the worst case is already a short wait. Revisit only if that proves annoying in practice. |

### Verification gaps

| # | Item | Why it was deferred, and what to do |
|---|---|---|
| B8 | **The UI click-through last mile.** | The Avalonia layer itself — XAML bindings, the dialogs' own controls, the `StorageProvider` folder picker — has never been operated. Synthetic input reaches nothing in this machine's Wayland session (neither pointer nor keyboard; verified, see F1's entry). The promising route is installing `xvfb` and running the app headless, where XTest works normally; that needs `sudo`. Everything behind the dialogs is covered by `RealCliSyncPanelTests`. |
| B9 | **Appendix A #13: quota, rate limits, max file size.** | No safe way to trigger them against a real personal account. `CliErrorClassifier` already has a `Quota` rule waiting for real message text; revisit if a real run surfaces one. |
| B10 | **Appendix A #10: quota and network error wording.** | Same reason. Auth and not-found are verified; these two are still guesses. |

### Known dead code, kept on purpose

- §11.3's rename heuristic is unreachable for this CLI version (every node has a `uid`), kept only as
  a fallback for a hypothetical CLI that lacks one.
- `TryParseJsonListing`'s wrapper-key branches (`items`/`entries`/`children`) never match this CLI
  version's bare-array responses, kept because a future version could wrap.

---

## Appendix A — Verified CLI behavior

> **F0 complete** (2026-07-31), against `cli-drive@0.4.2+e41620d` / `sdk@0.0.0+e41620d`
> (`/home/ramiro/Apps/proton-drive`), a real authenticated account. Findings below are cited by
> question number from §2. Several change the design in ways noted inline; **§3.2, §5.4, §6.2,
> §7, and §11 should be read together with this appendix, not in isolation** — the old
> assumption is kept struck through where it matters so the diff is visible.

### #1 — Real JSON shape of `filesystem list --json`

Root is a bare JSON array (not `{ items: [...] }`). Two invocation quirks that are easy to get
wrong: **`--json`/`-j` must come after the verb and before positional args**
(`filesystem list --json <path>` works; `--json filesystem list <path>` does not — "Command not
found"), and this holds for every subcommand, not just `list`. Per-entry shape (file example;
folders omit `activeRevision`, `mediaType` is literally `"Folder"`):

```json
{
  "uid": "rHChrZ...~EJSA0C...",
  "parentUid": "rHChrZ...~_GRN2n...",
  "name": { "ok": true, "value": "10825139_1.pdf" },
  "ownedBy": { "email": "ramiro.di.rico@proton.me" },
  "type": "file",
  "mediaType": "application/pdf",
  "isShared": false,
  "isSharedPublicly": false,
  "creationTime": "2026-06-06T14:02:31.000Z",
  "modificationTime": "2026-06-06T14:02:46.000Z",
  "totalStorageSize": 6214012,
  "activeRevision": {
    "ok": true,
    "value": {
      "uid": "rHChrZ...~ZtRcE4...~zP7zq-...",
      "state": "active",
      "storageSize": 6214012,
      "claimedSize": 6196055,
      "claimedModificationTime": "2026-06-06T14:02:28.502Z",
      "claimedDigests": { "sha1": "a2abbf57e75de3b7da1312f64080090b5a0514f0", "sha1Verified": false }
    }
  },
  "treeEventScopeId": "rHChrZ..."
}
```

Consequences for the code:
- `name`/`keyAuthor`/`nameAuthor`/`contentAuthor` are `{ ok, value }` — already handled by the
  existing nested-value reader in `ProtonDriveService.ReadString`.
- `ownedBy` is `{ email }`, **not** `{ value }` — the current `ReadString(entry, "owner", "user",
  "createdBy")` alias guessing never matches this. Needs an explicit reader.
- `type` is directly `"file"`/`"folder"` (no `directory`/`dir` variants seen) — the substring
  matching in `TryParseJsonEntry` still works but is over-broad; can be simplified to an exact
  match.
- **File size lives at `activeRevision.value.claimedSize`, not a top-level `size`/`bytes`.**
  `totalStorageSize` is the *encrypted* size on Proton's server (always larger) — using it as
  "file size" would make every local/remote size comparison in the reconciler wrong. Folders
  have no `activeRevision` and no size at all.
- None of `items`/`entries`/`children` wrapper keys were observed — root is always a bare array
  in this CLI version. The wrapper-key branches in `TryParseJsonListing` are dead code for this
  CLI version; kept for now since a future CLI version could still wrap (defensive, not harmful).

### #2 — `ModifiedAt`: format and meaning ✅ resolved, and it's better than expected

~~Guessed alias: `modifiedAt`/`updatedAt`/`lastModified`~~ — **none of these exist.** There are
*three* different timestamps, and picking the wrong one breaks change detection:

| Field | What it is | Verified |
|---|---|---|
| `creationTime` | Proton-side node creation event | — |
| `modificationTime` (top-level) | Proton-side *revision* event time (e.g. upload-completed) | Was 18s **after** the file's real mtime in a test upload — not usable for content comparison |
| `activeRevision.value.claimedModificationTime` | **The local file's actual mtime at upload time**, client-claimed | Uploaded a file with local mtime `2026-07-31T18:52:12.943Z`; the CLI reported back `claimedModificationTime: "2026-07-31T18:52:12.943Z"` — **exact match to the millisecond** |

**Use `activeRevision.value.claimedModificationTime` as `DriveItem.ModifiedAt`** — it's ISO-8601
UTC with millisecond precision and is the actual local mtime, not a server timestamp. Folders
don't have this reliably (an empty `folder.claimedModificationTime` was seen once, absent other
times) — fall back to top-level `modificationTime` for folders, where exactness matters less
since folders aren't diffed by content.

### #3 — Stable ID ✅ yes, and it survives rename *and* move

Every node has a `uid` (e.g. `rHChrZ...~EJSA0C...`). Verified directly: uploaded a file, noted
its `uid`, ran `filesystem rename`, re-listed — **same `uid`**. Then `filesystem move`d it into a
freshly-created subfolder, re-listed — **still the same `uid`**, only `parentUid` changed.

This resolves §11 in full: **`SyncState.RemoteNodeId` should be the primary correlation key on
the remote side**, not the rename-heuristic fallback. Renames/moves become first-class,
cheap operations instead of a guessed delete+create. Revalidate on every CLI upgrade — this is
inferred from one CLI version's observed behavior, not a documented guarantee.

### #4 — Is `list` recursive? ❌ no, confirmed

`filesystem list [-t TYPE] path` takes one path, no `--recursive`/`--depth` flag exists in
`--help`. BFS per-folder (§6.2) stands as designed. See #11a below for a cost implication that
changes how aggressively that BFS needs to cache.

### #5 — Folder download ✅ yes, recursive, confirmed

`filesystem download <path> <localFolder>` on a **folder** path recreated the entire subtree
locally (`f0-sync-test/subfolder/f0-test-renamed.txt` and all), not just the top-level entry.
This means the initial full mirror for a `RemoteToLocal` pair can be one `download` call per
top-level pair folder instead of walking and downloading file-by-file — worth using for the
*first* sync of a pair, while still tracking individual `SyncAction`s per file for baseline
bookkeeping and incremental syncs afterward.

### #6 — Does download preserve mtime? ❌ no, confirmed — as the plan assumed

Downloaded the test file (remote `claimedModificationTime` = `18:52:12.943Z`); the local file's
mtime after download was the download wall-clock time (`18:54:15`), not the claimed time.
**Confirms §5.5/§7 as designed**: the executor must call `File.SetLastWriteTimeUtc(path,
claimedModificationTime)` explicitly after every download — never trust the OS mtime a download
leaves behind.

### #7 — Does upload preserve/claim mtime? ✅ yes — see #2

Answered by #2: the upload path captures the local file's real mtime as
`claimedModificationTime` with millisecond fidelity. No CLI flag needed; it's automatic.

### #8 — `move` vs rename+copy+trash ✅ `filesystem move` exists

Contradicts the plan's original worst-case assumption ("without `move`, a remote rename =
copy+trash"). `filesystem move <path>... <targetParentPath>` exists and was used directly in
the #3 test. Combined with the stable `uid`, §11's `RenameRemote`/`MoveRemote` actions are cheap
single calls, not a heuristic-guarded copy+trash fallback.

### #9 — Permanent delete vs trash ✅ both exist

`filesystem trash path...`, `filesystem restore path...`, `filesystem delete path...` (permanent,
untested — did not risk it), `filesystem empty-trash`. The engine should use `trash` exclusively,
per §11's safety rule; `delete`/`empty-trash` are for a future "empty trash" UI action, not sync.

### #10 — Distinct exit codes / stable messages ⚠️ mostly verified

Confirmed two messages:
- A nonexistent path produces `Node not found: <name>`, exit code 1 — matches
  `CliErrorClassifier`'s existing `"not found"` substring rule.
- **An unauthenticated invocation produces exactly `You need to login first`, on stderr, exit
  code 1** (verified 2026-07-31 in a session that happened to find the CLI logged out — no
  deliberate logout needed after all). `CliErrorClassifier`'s existing `"login first"` rule
  already matches it; a regression test now pins the verbatim string.

Still **not** verified: quota and network errors. Still exit code 1 for everything observed —
no distinct codes per failure type, so `CliErrorClassifier` stays substring-based.

### #11 — Concurrent processes ❌ NOT safe — this reverses the original finding

~~Ran 4 concurrent `filesystem list` processes: all 4 succeeded, no lock contention.
`TransferQueue`'s default concurrency of 2 is conservatively safe; could likely go higher.~~
**That conclusion came from a single trial, and re-testing (2026-08-01) shows the trial was
simply lucky.**

Concurrent `proton-drive` processes **intermittently crash on the CLI's own internal SQLite
cache**. Observed failure rate: ~1 in 3 calls in a three-way race, and it reproduced with plain
read-only `list` calls on *different* folders — the CLI writes its cache on every listing
(`setEntity`/`setShareKey`), so even "read-only" invocations contend. The crash is an unhandled
rejection, exit code 1:

```
code: "SQLITE_BUSY"
  at setEntity (src/cache/sqliteCache.ts:25:21)
  at setShareKey (../client/js/src/internal/shares/cryptoCache.ts:25:31)
  at subscribeToTreeEvents (../client/js/src/internal/events/index.ts:169:34)
Error details: { code: 'SQLITE_BUSY', errno: 5, byteOffset: -1 }
```

**How it was found:** the two real-CLI integration tests began failing differently on every run.
xUnit parallelizes across test classes, so the two were driving the CLI concurrently. They now
share one xUnit collection, and the *product* code changed as follows:

- **`RemoteScanner`'s default concurrency dropped from 3 to 1.** This was a live bug on the F1/F2
  path: a BFS wave of 3 parallel `list` calls could take the whole scan down.
- **New `CliErrorKind.Busy`**, classified from `SQLITE_BUSY`/`database is locked` and treated as
  retryable by `SyncRetryPolicy` — it is the textbook retry case.
- **`CliErrorClassifier` and `ProtonDriveCliExecutor` now read both streams.** This crash writes a
  bare `===============` banner to **stderr** and the actual diagnosis to **stdout**, so the old
  "prefer stderr, fall back to stdout only if empty" rule classified it as `Unknown` and produced
  a `CliException` whose entire message was `===============`.

Consequences elsewhere in this plan: **§7's `TransferQueue` default concurrency must be 1, not 2**,
and **§9's shared-CLI semaphore is a serializer, not a priority queue over N slots** — there is
only ever one slot. Revisit only against a CLI version verified to serialize its own cache access.

**#11a — unplanned but important finding: CLI cold-start cost.** A single `filesystem list`
call took **~3.5 seconds wall-clock**, almost entirely Node.js/SDK startup (the 4-parallel test
took ~4.2s total, barely more than one sequential call — the overhead is per-process, not
per-request). This changes the calculus in §6.2: BFS-per-folder for the remote scanner is
**far more expensive than "N processes"** suggested — it's **N × ~3.5s**. A drive with 50
folders is ~3 minutes just to scan, every polling cycle. This raises the priority of:
- a subtree-skip optimization in §6.2, from "nice to have" to "needed before F3's 5-minute
  polling default is usable on any non-trivial drive" — but see §6.2: the `RemoteFolderEtag`
  design originally proposed there is unsound, and a correct replacement depends on #11b below, and
- reconsidering whether the default poll interval (5 min) is even long enough headroom for a
  large tree — may need to scale the interval to the pair's last observed scan duration.

**#11b — does a descendant change propagate upward? ❌ NO, fully answered (2026-08-01).** This
was needed to make any cross-cycle subtree caching sound (see §6.2, whose etag design is
retracted). It isn't achievable with this CLI.

**Part 2 — is there a changes/events/delta command? ❌ no, confirmed.** The CLI's complete
command surface (from top-level `--help`, cli-drive@0.4.2) is: `auth login|logout`;
`filesystem list|info|create-folder|upload|download|rename|copy|move|trash|restore|delete|empty-trash`;
`sharing status|invite|leave|remove|set-url|remove-url`; `invitation list|accept|reject`. No
event stream, no delta query, nothing that exposes `treeEventScopeId`. So an incremental remote
scan can only be built out of `list`/`info` calls.

Two side notes from that same output: `filesystem move sourcePath... targetParentPath`
**re-confirms #8's argument order** (sources first, target parent last — matches
`ProtonDriveService.MoveItemsAsync`), and there is a **`filesystem info path`** command that
Appendix A never recorded — worth probing, since a single-node metadata call may be the cheapest
way to poll one folder's fingerprint (though still ~3.5s of process startup, per #11a).

**Part 1 — does a folder's `modificationTime` bump when a descendant changes? ❌ no.** Tested
directly: created `/my-files/f11b-test/sub`, recorded both folders' `modificationTime` (`…:53:31`
and `…:53:38`), uploaded a file into `sub`, and re-listed. **Neither timestamp moved** — not the
grandparent, and *not even `sub`, the file's direct parent*. Folder `modificationTime` tracks only
the folder's own metadata events (creation, rename), never its contents. `filesystem info` on the
folder returns the same fields as `list` — no version, no etag, no child count. Folders carry no
`activeRevision` at all.

**Consequence, and it's a hard one:** there is no signal at any level that a subtree changed
without listing that exact folder. Cross-cycle subtree caching is therefore **impossible with this
CLI**, not merely unimplemented — every scan must BFS the full tree at ~3.5s per folder. So:
- F3's polling interval cannot be a fixed 5 minutes. It has to **scale with the pair's last
  observed scan duration** (a 50-folder tree is ~3 minutes of scanning per cycle), or remote
  change detection has to become user-triggered rather than periodic.
- Local→remote sync is unaffected: the filesystem watcher gives real change events (§6.3). It's
  specifically *remote* change detection that has no efficient path here.
- Revisit only if the CLI gains an events/delta command, or if the SDK's `treeEventScopeId`
  becomes queryable.

### #12 — Parseable upload/download progress — not tested

Skipped: would have required a large file and careful stdout capture; the existing
`CliCommandOutputEventArgs` line-streaming infrastructure is designed to support this once
needed, so it's deferred without blocking F1/F2 (progress is a UI nicety, not correctness).

### #13 — Limits (size, rate limit, quota) — not tested

Skipped: no safe way to trigger a quota-full or rate-limit condition against a real personal
account without risking actual account impact. Revisit before F4 (progress/limits polish) or if
a real sync run surfaces one in the wild — `CliErrorClassifier.Classify` already has a `Quota`
substring rule ready to receive real message text.

### #14 — Hash/checksum exposed ✅ yes — exact match, changes §5.4 materially

`activeRevision.value.claimedDigests.sha1` is the **client-computed SHA-1 of the original local
file content at upload time**. Verified exactly: local file's `sha1sum` was
`97cc8ad38e1de95648240669b5e4ce975eb700a9`; the CLI reported back the identical hash after
upload. `sha1Verified: false` in both observed files — Proton doesn't seem to re-verify it
server-side (expected, given end-to-end encryption — the server can't read plaintext content to
hash it), so treat it as **client-claimed, not server-attested**, but still trustworthy for our
purposes since *our own* client produced both sides of the comparison.

**This upgrades §5.4 from "hash as an optional expensive tie-breaker" to "hash comparison is
cheap and exact — use it as the primary criterion, not a fallback."** Rationale: computing a
local SHA-1 is one file read (cheap relative to the ~3.5s CLI round-trip that dwarfs it either
way), and it eliminates every mtime-tolerance edge case (touch without content change, clock
skew, the "empty folder detection" class of bugs) for files that already have a
`claimedDigests.sha1` on the remote side. Practical policy: **compare `(size, sha1)` when the
remote side has a hash; fall back to `(size, ModifiedAt-with-tolerance)` only when it doesn't**
(e.g., very old revisions uploaded before this field existed, or non-standard upload paths).

### #15 — A listing right after a mutation can be stale ⚠️ new finding (2026-08-02)

**A `filesystem list` issued immediately after a `filesystem trash` still returns the trashed
node, roughly two times out of three.** Measured convergence was ~7s, which is the same order as
the ~3.5s a single CLI process takes to start — so "trash, then list in the next process" is
essentially a coin flip. Not investigated: whether the staleness is Proton's backend being
eventually consistent, or the CLI's own SQLite cache serving stale children (the cache exists —
see #11 — and nothing suggests `trash` invalidates it).

How it surfaced: the F2 integration test failed ~2 runs in 3, always on the same assertion, and
the failure moved *earlier* in the test once the product bug below was fixed — which is what
identified the remaining failure as the test's own verification racing, not the engine's.

**Product consequence, already fixed:** `SyncExecutor` no longer re-reads the remote side after a
deletion. `DeleteLocal`/`TrashRemote` only ever arise when the node is gone on *both* sides
(§5.2), so the baseline outcome is known by construction — asking was both unnecessary (it costs a
~3.5s call) and wrong (a stale answer recorded a baseline row claiming the remote copy was still
alive, moments after we trashed it).

**Second product consequence — a deleted file could transiently resurrect. Fixed.** After
`TrashRemote` cleared the baseline row, a *next* run whose remote scan was still stale saw
`L=absent, R=present, B=absent`, which §5.2 reads as "new remotely" and answers with
`DownloadFile` — re-downloading the file the user had just deleted, then deleting it again on the
following run. Nothing was lost, but it was churn and it looked alarming.

Fixed by `SyncEchoSuppressor` (§9), built as the single mechanism for both halves of the echo
problem rather than a remote-only patch: it remembers what this engine just deleted, per pair and
per side, and filters those paths out of the next scan. Three details that matter:
- **Folder deletions suppress the whole subtree.** A stale listing can report the trashed folder
  *and* its children; suppressing only the exact path let the children through as "new remotely" —
  the same bug one level down. Prefix matching respects path boundaries, so `Photos` does not
  suppress `PhotosElsewhere.txt`.
- **Suppression is released early**, as soon as a scan agrees the node is gone, rather than always
  running the full 60s. That keeps the window in which a genuine re-creation of the same path would
  be ignored as short as the facts allow.
- **It applies to the preview too**, not just the run — otherwise the dry-run would offer to
  download a file the engine had just deleted.

*Evidence, stated precisely*: the suppression logic is pinned deterministically by unit tests that
feed a deliberately stale listing; the real-account run confirms no resurrection across three runs,
but cannot distinguish "suppression fired" from "the listing had already converged", since both
produce an empty plan. The ~2-in-3 staleness rate above is the separately measured part.

---

### Net effect on the design

- §3.2 `NodeFingerprint` gains a real, verified meaning for `ContentHash` (SHA-1, not a vague
  "SHA-256, computed lazily") and `NodeId` (verified stable `uid`).
- §5.4 change detection: hash-first, not hash-as-tiebreaker (see #14).
- §6.2 remote scanning: the ~3.5s/process cost (see #11a) made subtree caching look load-bearing —
  but #11b then established that it's **impossible** with this CLI (no propagating change signal).
  F3 must adapt its polling interval to the measured scan duration instead of caching its way out.
- §7 transfer execution: the `File.SetLastWriteTimeUtc` step after download is now a confirmed
  requirement, not a hedge (see #6).
- §11 rename problem: solved outright by `uid` + `filesystem move`, not mitigated by heuristics.
  §11's heuristic (§11.2) becomes dead code for this CLI version — kept in the plan only as a
  fallback for a hypothetical CLI/account state where `uid` is ever absent, which was not
  observed.
- §8 "required changes to existing code": `ProtonDriveService.TryParseJsonEntry` needs a rewrite
  (not a tweak) to read the real shape above instead of the guessed aliases; `DriveItem` needs
  `NodeId` and `ContentHash` fields in addition to the already-planned `ModifiedAt` retype.
