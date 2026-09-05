---
name: sync-change
description: Change the local-sync engine in Services/Sync — reconciler, executor, scanners, scheduler, echo suppression, crash recovery, state store. Use whenever sync behavior, the decision table, or the sync queue is touched, before writing any code.
---

# Change the local-sync engine

`Services/Sync/` is the largest and least forgiving part of the app: a wrong decision here does
not throw, it deletes a file or loops forever. The design and its decision table live in
`docs/PLAN-LOCAL-SYNC.md` — **§5.2 (decision table), §5.3 (execution bands), §5.4 (hash-first
change detection), §7 (atomicity and crash safety), §9 (echo), Appendix A (verified CLI
behavior)**. Read the relevant section before changing behavior; the code's doc comments cite it
and are meant to be kept in sync with it.

## The pipeline

```
LocalScanner ─┐
              ├→ SyncReconciler (pure) → SyncPlan → SyncExecutor → provider + SyncStateStore
RemoteScanner ┘        ↑ baseline                        ↓
   / DeltaRemoteScanner  SyncStateStore            SyncEchoSuppressor
```

`SyncScheduler` + `ChangeDebouncer` + `LocalFileWatcher` decide *when* a run happens;
`SyncSchedulePolicy` and `SyncRetryPolicy` decide *whether* and *how often*. `SyncCrashRecovery`
runs once at startup from `MainWindowViewModel.InitializeAsync` — not per window open.

## Invariants — do not break these

- **`SyncReconciler` stays pure.** Maps in, `SyncPlan` out. No IO, no `DateTime.Now` — the caller
  passes `conflictTimestamp` so the engine is deterministic. If you need new information, add it
  to `NodeFingerprint`/`SyncBaselineEntry` and have the *scanner* fill it in.
- **Nothing is ever permanently deleted.** Local deletions move to
  `<LocalPath>/.mypersonaldrive-trash/<yyyy-MM-dd>/…`; remote ones go through the provider's
  trash. A code path that calls `File.Delete` on user content is a bug.
- **Writes are atomic.** Downloads land in a per-operation temp dir, then `File.Move`. The local
  mtime is set explicitly afterwards (Appendix A #6: download does not preserve it).
- **Both echo registers exist for opposite reasons.** Deletions are *filtered out* of a scan
  (`SuppressDeletion` → `Filter`); writes are *not* — only the watcher event is ignored
  (`SuppressWrite` → `IsEcho`). Filtering a write out of a scan re-downloads or re-deletes the
  file. Read the class doc on `SyncEchoSuppressor` before touching either.
- **The queue is durable.** State transitions go through `SyncStateStore`; a row left `Running`
  must be recoverable by `ResetRunningToPendingAsync`. Never hold plan state only in memory.
- **Folders are compared by presence, never by content or mtime** — they have no hash and their
  mtime jitters on every child change.
- **Ordering comes from the execution bands** (`BandCreate`/`BandTransferOrRename`/`BandDelete`/
  `BandBaseline`), with per-item depth nudges. Don't sort the plan somewhere else.

## Steps

1. **Locate the decision.** Find the row in §5.2 (or the Appendix A finding) that governs the
   behavior you're changing. If the change contradicts the document, update the document in the
   same change — see the `plan-doc` skill. Silent divergence is how this engine rots.
2. **Change the smallest layer that can express it.** A new *decision* belongs in the reconciler;
   a new *observation* in a scanner; a new *side effect* in the executor. Pushing executor
   knowledge into the reconciler is the usual mistake.
3. **Never invent remote behavior.** What the Proton CLI actually does is recorded in Appendix A.
   If your change depends on behavior that isn't recorded there, capture it first — see the
   `debug-cli` skill — and add the finding to Appendix A.
4. **Multi-provider.** Sync runs over `ICloudDriveProvider`, not over `ProtonDriveService`. Check
   `ProviderCapabilities` before relying on a capability (delta, server-side hash, move); a
   provider without it must degrade, not throw.
5. **Tests** in `tests/MyPersonalDrive.Tests/Services/Sync/`, one file per class, mirroring the
   existing names.
   - Reconciler changes: add the case to `SyncReconcilerTests` as a table row — locals, remotes,
     baseline in, expected actions/conflicts out. No fakes needed; it's a pure function.
   - Executor/scanner changes: `FakeCliExecutor` (+ `RespondForPath` for BFS scanners, whose call
     order is not deterministic) and a real temp directory. `FakeTimeProvider` for anything timed.
   - Always cover: the conflict path, the partial-failure path (one action fails, the rest still
     commit and the queue reflects it), and dry-run/preview touching neither disk nor CLI.
6. **Verify**:

   ```bash
   ./scripts/run-tests.sh
   ```

   Then, for anything that moves real bytes, run the real-CLI pass — these are the only tests
   that prove the engine against Proton:

   ```bash
   MYPERSONALDRIVE_INTEGRATION=1 ./scripts/run-tests.sh
   ```

   And exercise sync by hand (`run-app`, then rows 17–22 of `smoke-test`).

## Checklist

- [ ] The governing §/Appendix A entry identified, and updated if behavior diverged
- [ ] Reconciler still pure; no IO or clock reads added to it
- [ ] No permanent deletion; temp-dir-then-`File.Move` preserved; mtime restored
- [ ] Echo suppression: deletions filtered, writes not
- [ ] Queue state durable and crash-recoverable
- [ ] Capability-gated instead of assuming Proton
- [ ] Unit tests incl. conflict, partial failure and dry-run; `run-tests.sh` green
- [ ] Real-CLI integration pass run, or explicitly reported as not run
