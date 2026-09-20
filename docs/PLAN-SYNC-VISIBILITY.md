# Technical Plan — Sync status made visible

> Today the sync engine knows, per path, exactly what it has and hasn't synced — `SyncState` rows,
> `SyncQueue` rows, the pair's `LastStatus` — and the user can see none of it. The only visible
> signal is a badge on the *root* folder of a pair ("this folder is a sync pair, and it is
> paused/active"). Inside that folder, every file and subfolder looks identical whether it was
> downloaded an hour ago, is queued, failed three times, or was never touched. This plan puts a
> per-node status on each row, on **both** panes, and adds a per-provider breakdown to the
> Properties dialog for the multi-provider case.
>
> Companions: [PLAN-LOCAL-SYNC.md](PLAN-LOCAL-SYNC.md) (§3.1 the tables, §5.1 the three-way
> comparison, §12 the sync UI this extends), [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md)
> (P7 multi-account, §12's `SharesLocalFolder`), [INTERFACE_IMPROVEMENT_PLAN.md](INTERFACE_IMPROVEMENT_PLAN.md)
> (Task 6, the row context menu and the Properties dialog).
>
> Branch: not started.

## Status

> **Nothing here is implemented.** Six phases; S1–S3 are the feature as the user will first
> recognise it, S4–S6 complete it.

- [ ] **S1 — The status vocabulary and the pure rollup.** `SyncNodeStatus`, `SyncStatusIndex`.
- [ ] **S2 — Reading it out of SQLite.** The queue-state query and the per-pair index cache.
- [ ] **S3 — Badges on the cloud pane.** `DriveNodeViewModel.SyncStatus` + icon/colour/tooltip.
- [ ] **S4 — Badges on the local pane.** Same rows, but N pairs can cover one local folder.
- [ ] **S5 — The per-provider section in Properties.** Cross-account, both panes.
- [ ] **S6 — Keeping it live.** Refresh on sync events, plus the UI and a11y tests.

---

## 0. Executive summary

Everything this feature needs is already in the database. No new remote call, no new local scan,
no new hashing pass is introduced by any phase here — that is the central design constraint, and
the reason the whole thing is affordable. A node's status is derived from three things the app
already holds the moment it draws a row:

1. the `SyncState` baseline row for that relative path (what each side looked like when they were
   last confirmed equal — `SyncBaselineEntry`),
2. the `SyncQueue` rows for that pair (`Pending` / `Running` / `Failed` / `Conflict`), and
3. the listing the pane is already showing, which carries the *current* remote (or local)
   size/mtime/hash for that node.

Comparing (3) against (1) is the same `changed(R,B)` predicate `SyncReconciler` already computes —
this plan does not re-implement it, it reuses it. (2) is what turns "out of date" into "we already
know about it and it is in the queue".

The two hard parts are **folder rollup** (a folder has no baseline row of its own worth reading;
its status is an aggregate over its descendants) and **honesty about what we don't know** (the
cloud pane has listed one directory level, not the whole subtree). Section 1.2 resolves both with
an explicit `Unknown` that never gets silently rendered as "synced".

## 1. S1 — The status vocabulary and the pure rollup

### 1.1 `SyncNodeStatus` (new, `Models/`)

| Value | Meaning | Where it comes from |
|---|---|---|
| `NotInPair` | outside every sync pair | no pair covers the path |
| `Excluded` | inside a pair but matched by an `ExcludeGlobs` rule | `ExclusionMatcher` |
| `Synced` | baseline exists and this side still matches it | `SyncBaselineEntry` vs the listing |
| `Pending` | a queued action targets this path | `SyncQueue` row `Pending` |
| `Syncing` | an action for this path is running now | `SyncQueue` row `Running` |
| `OutOfDate` | changed since the baseline, nothing queued yet | baseline exists, fingerprints differ |
| `NotSynced` | inside a pair, no baseline, nothing queued | never transferred |
| `Conflict` | a `SyncConflict` row awaits a decision | `SyncQueue` row `Conflict` |
| `Failed` | the last action for this path failed | `SyncQueue` row `Failed` |
| `Paused` | the covering pair is paused or disabled | `SyncPair.IsPaused` / `IsEnabled` |
| `PartiallySynced` | **folders only** — descendants disagree | the rollup, §1.2 |
| `Unknown` | we genuinely cannot say | §1.2 |

Ordering matters and is fixed once, as a precedence list, because a folder rollup and a single file
with several queue rows both need it: `Failed` > `Conflict` > `Syncing` > `Pending` > `OutOfDate` >
`PartiallySynced` > `NotSynced` > `Synced` > `Excluded` > `Paused` > `NotInPair` > `Unknown`.
Anything demanding attention wins; "fine" loses. `Paused` is deliberately *low*: a paused pair whose
file is in conflict should still say conflict — the pane says "paused" at the pair level already.

### 1.2 `SyncStatusIndex` (new, `Services/Sync/`) — pure, no IO

Constructed from one pair's snapshot: the baseline dictionary (`GetBaselineAsync` already returns
`IReadOnlyDictionary<string, SyncBaselineEntry>`), the queue rows grouped by relative path, the
pair itself and its `ExclusionMatcher`. Exposes:

- `StatusForFile(string relativePath, NodeFingerprint current, SyncSide side)`
- `StatusForFolder(string relativePath, IReadOnlyList<(string name, NodeFingerprint fp, bool isFolder)> listedChildren)`

**The folder rule.** A folder's status is the precedence-max of:

- every queue row whose relative path is under the folder's prefix (this is what makes a collapsed
  folder show `Syncing` while a file six levels down uploads), and
- the per-child status of the children the caller actually listed, and
- `PartiallySynced` if those children disagree — some `Synced`, some not.

If the folder has no baseline descendants *and* no queue rows *and* the caller listed no children
(a collapsed folder in a tile view), the answer is `Unknown`, not `Synced`.

> **Refined during S3.** As first written this left *every* collapsed folder in a healthy pair at
> `Unknown`, which is most of what a cloud pane shows — a field of question marks that tells the
> user nothing. A collapsed folder that is baselined, carries no queue row under it, and whose pair
> completed its last cycle cleanly (`LastStatus == Ok`) now answers `Synced`: anything unequal at
> that cycle would have left a queue row, so this is a statement about the last cycle rather than a
> guess, and the tooltip says "as of the last sync". `PartialFailure` answers `PartiallySynced`,
> `Error` stays `Unknown`. `Unknown` renders as a
neutral hollow glyph with a tooltip saying so; it must never be styled like success. The one case
where a folder with no listed children may still answer confidently is when the baseline has *no*
entry under its prefix and the pair has completed at least one successful cycle
(`SyncPair.LastStatus == Success`) — then `NotSynced` is a fact, not a guess.

**The file rule.** No baseline → `NotSynced`. Baseline present → compare the current fingerprint for
*this side* against the baseline's side (`LocalAtSync` in the local pane, `RemoteAtSync` in the
cloud pane) with the same comparison `SyncReconciler` uses, honouring the `HashAlgorithm` guard
(PLAN-CLOUD-PROVIDERS P3: a hash from another provider's algorithm is not comparable — fall back to
size+mtime, and if even that is unavailable, `Unknown`).

Tests: this is where the bulk of the test value is, and it needs no fakes at all — a table of
(baseline, queue, listing) → expected status, including every precedence pair.

## 2. S2 — Reading it out of SQLite

`SyncStateStore` today can hand out the baseline (`GetBaselineAsync`) and the failed/conflict rows,
but nothing returns "the current state of every queue row for this pair, by relative path". Add:

- `Task<IReadOnlyDictionary<string, SyncQueueState>> GetQueueStatesByPathAsync(int pairId, ...)` —
  one `SELECT RelativePath, State FROM SyncQueue WHERE PairId = @p AND State <> 'Done'`, collapsed
  with the §1.1 precedence when a path has several rows. `idx_SyncState_Pair` has a `SyncQueue`
  sibling to check; add one if it is missing.

Then `SyncStatusService` (new, `Services/Sync/`) — the only stateful piece:

- builds and caches one `SyncStatusIndex` per pair id,
- invalidates a pair's index on the events S6 wires up (cycle finished, queue changed, pair edited),
- and is constructed **per account**, like `SyncStateStore`, because that is the scope of a pair id.

Two whole-pair reads per pane refresh is the cost, and both are already routine (`SyncExecutor`
does the same on every cycle). A pair with 100k baseline rows is the case to watch; if it bites,
the fix is a prefix-scoped query, not a different design — note it, don't pre-build it.

## 3. S3 — Badges on the cloud pane

`DriveNodeViewModel` already resolves `SyncPair` through `DriveNodeSyncActions.FindSyncPair`, and
pairs cannot nest (PLAN-LOCAL-SYNC §12 makes that a hard validation error), so **at most one pair
covers a given remote path for the browsed account** — a cloud row has exactly one status, which is
what the user asked for.

- New `DriveNodeSyncActions.StatusFor : Func<DriveItem, SyncNodeStatus>?`, resolved once per row
  like `FindSyncPair` is, so a row built without it (older tests) gets `NotInPair` rather than a
  crash.
- New `DriveNodeViewModel.SyncStatus` + `SyncStatusTooltip` (localised, one key per value), settable
  so S6 can update a row in place without rebuilding the pane.
- `Views/Converters/SyncStatusIconConverter` + `SyncStatusBrushConverter`, following
  `FileKindIconConverter`. Theme brushes only, never literal colours, and **never colour alone** —
  each state gets a distinct glyph, because a red dot and a green dot are the same dot to a
  colour-blind user (see the `a11y-theming` skill).
- The badge carries an accessible name; it is decoration for a sighted user and the only carrier of
  the information for a screen reader, so it must be nameable in all three list/tile templates.

An existing pair-root row shows both its pair badge and its rollup status; they answer different
questions ("this is a pair" / "its contents are up to date") and both are wanted.

## 4. S4 — Badges on the local pane

Same rows, one difference that changes the model: a local folder may be covered by **several** pairs,
possibly on different accounts (`SyncPair.SharesLocalFolder`, PLAN-LOCAL-SYNC §12). A local row's
badge is therefore an aggregate of N per-pair statuses:

- all `Synced` → `Synced`;
- any attention state → that state, by §1.1 precedence;
- otherwise `PartiallySynced`, with the tooltip naming the counts ("2 de 3 proveedores al día").

`LocalNodeViewModel` gets the same `SyncStatus`/tooltip pair, fed by a cross-account query (§5).

## 5. S5 — The per-provider section in Properties

`PropertiesDialog` renders a flat `IReadOnlyList<PropertyField>`; a per-provider breakdown is a
*section*, not a field. Two options, and the second is the recommendation:

1. Add `PropertyField.IsHeader` and push rows in. Cheapest, but the values stop being label/value
   pairs and the dialog's one job blurs.
2. Give `ShowAsync` an optional `IReadOnlyList<SyncStatusRow> syncStatuses` parameter rendered as
   its own titled block below the fields — provider name, direction glyph, status glyph + text,
   last-sync time, and the local path for that pair. Additive: every existing call site compiles
   unchanged, exactly the way `PropertyField.IsCopyable` was added.

The data comes from a cross-account lookup, and `SyncPanelViewModel` is the only object that holds
every account's `SyncStateStore` (its `AccountSlot` list). Give it
`Task<IReadOnlyList<SyncStatusRow>> DescribeSyncStatusAsync(string? remotePath, string? localPath)`:
it maps the item to a relative path per slot, asks each slot's `SyncStatusService`, and labels each
row with the slot's `DisplayName`. `MainWindowViewModel.ShowPropertiesAsync` and
`LocalExplorerViewModel`'s equivalent both call it — which is what makes the local pane's
"is this in Proton, in OneDrive, in both?" question answerable at all.

Every provider the file is *not* in is shown too, as `NotInPair` — "no está sincronizado con Google
Drive" is exactly the answer the user is opening this dialog for, and an absent row does not say it.

## 6. S6 — Keeping it live

A badge that is right only at listing time is a bug with extra steps: the common case is watching a
folder sync. `SyncScheduler`/`SyncExecutor` already raise per-cycle events the sync panel consumes;
subscribe `SyncStatusService` to them, invalidate the affected pair's index, and have the pane
re-ask `StatusFor` for its existing rows — updating `SyncStatus` on each row, **not** calling
`RefreshPaneAsync`, which re-lists remotely and would mean a CLI round trip per sync event.

Tests to finish on:

- `SyncStatusIndex` table tests (S1) — the bulk.
- `SyncStateStore.GetQueueStatesByPathAsync` against a temp db, including the collapse of several
  rows for one path.
- A ViewModel test that a queue event flips a row's `SyncStatus` with no re-listing.
- A `MyPersonalDrive.UiTests` layout test that the badge column does not squeeze the name column at
  the narrow width — the one class of defect only that project can see.
- Accessible-name assertions per status (the `a11y-theming` skill's checklist).
- New localisation keys in **every** locale file, per the `add-language` skill — not English only.

## 7. Ordering, and why

S1 before everything because the vocabulary is what the other five phases agree on, and it is the
only phase that can be fully right before any UI exists. S2 before S3 for the obvious reason. S3
before S4 because the cloud pane is the single-pair case and S4 is the same feature plus an
aggregation rule — doing the harder one first would mean inventing the aggregation before the base
case is known good. S5 after both panes have a status to show, so it reuses `SyncStatusService`
rather than defining a second path to the same answer. S6 last because a stale-but-correct badge is
a shippable intermediate state; a live-but-wrong one is not.

## 8. Explicitly out of scope

- **Any new IO to compute a status.** No extra listing, no on-demand hashing, no local scan
  triggered by drawing a row. If the data isn't already there, the answer is `Unknown`.
- **Per-byte transfer progress on a row.** That is PLAN-LOCAL-SYNC B6, still blocked on Appendix A
  #12 (whether the CLI emits parseable progress). A row says `Syncing`; the transfer queue says how
  much.
- **Making status actionable** ("sync just this file", "resolve from here"). A natural follow-up,
  and a different change — this plan is read-only.
- **A tree/overlay in the OS file manager** (Nautilus emblems). Out of the app's process.
- **Changing what the engine records.** No schema change beyond an index; if the rollup turns out to
  need a materialised per-folder state, that is a separate decision with its own write path.
