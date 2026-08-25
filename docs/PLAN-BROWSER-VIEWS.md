# Technical Plan — Browser view modes and per-directory metrics

> Two independent features for the `/my-files` explorer, planned together because both hang off the
> same listing pipeline (`MainWindowViewModel.LoadFolderAsync` → `DriveCacheService` →
> `DriveNodeViewModel`) and both need a place to store a user preference:
>
> - **V — View modes.** Let the user switch the file listing between the current *list*, a compact
>   *icons* grid, and a *gallery* (large tiles). A user choice, persisted across restarts.
> - **M — Directory metrics.** Per-directory statistics: item counts, total size, breakdown by file
>   type, largest items, newest/oldest. Shallow (direct children) for free; deep (recursive) as an
>   explicit, cancellable, cached operation.
>
> Companions: [ARCHITECTURE.md](ARCHITECTURE.md) §1, [PLAN-LOCAL-SYNC.md](PLAN-LOCAL-SYNC.md)
> (Appendix A — verified CLI behavior; the cost model in M is entirely built on Appendix A #4 and
> #11a/#11b), [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md) B4.1 (cache schema versioning).
> No implementation branch yet.

## Status

- [x] **V1 — view-mode enum, VM state, persistence.** `Models/DriveViewMode.cs`;
  `AppSettings.ViewMode` (string) + `ViewModeOrDefault()`; `MainWindowViewModel.ViewMode` with
  `IsListView`/`IsIconsView`/`IsGalleryView` and `ShowListViewCommand`/`ShowIconsViewCommand`/
  `ShowGalleryViewCommand`. The `PersistSettings` data-loss bug is fixed via a new
  `AppSettingsService.Update(Action<AppSettings>)`. Covered by
  `tests/.../ViewModels/MainWindowViewModeTests.cs` (6 tests, incl. the regression).
- [x] **V2 — the three presentations** (partial). `MainWindow.axaml` now hosts three
  `ListBox Classes="driveListing"` (list / icons / gallery) gated on the derived booleans, with
  the row actions moved to a `ContextMenu` in the tile modes, plus three toolbar buttons
  (`IconViewList`/`IconViewGrid`/`IconViewGallery`).
  **Not yet done:** (a) the large-folder measurement of §3 — `WrapPanel` is still in place and
  the `ItemsRepeater` decision is unmade; (b) Enter-to-open on the focused row — the `ListBox`
  gives arrow-key focus, but activation still goes through the row's `Button`/`RowCommand`;
  (c) no visual pass has been signed off (see the note under §3).
- [x] **V3 — extension→kind classification.** `Models/FileKind.cs`,
  `Services/FileKindClassifier.cs` (frozen extension table, compound extensions, dotfiles,
  invariant casing), `DriveNodeViewModel.FileKind`, nine new icons in `Assets/Icons.axaml`, and
  `Views/Converters/FileKindIconConverter.cs`. Covered by
  `tests/.../Services/FileKindClassifierTests.cs`.
- [ ] **V4** — sort control (list-mode headers reused by the other modes). Not started, optional.
- [x] **M1 — classifier + model.** `Models/FileKind.cs` and `Services/FileKindClassifier.cs`
  landed with V3; `Models/FolderMetrics.cs` (`FolderMetrics` + `FolderKindBucket`, with
  `IsDeep`/`IsComplete`/`UnknownSizeCount`/`ScannedFolderCount`) and `Services/ByteSize.cs`.
- [x] **M2 — shallow metrics.** `Services/FolderMetricsCalculator.FromChildren`, called from
  `MainWindowViewModel.DisplayItems` so both the cached paint and the CLI result refresh it. Zero
  CLI cost. Covered by `FolderMetricsCalculatorTests` (12 tests) and `ByteSizeTests`.
- [x] **M3 — deep metrics scanner.** `Services/RemoteTreeWalker.cs` extracted from
  `Services/Sync/RemoteScanner.cs` (which kept its own tests unchanged) and
  `Services/FolderStatsScanner.cs` on top of it: progress as counts, cancellation checked per wave
  *and* after each semaphore slot, partial results returned with `IsComplete = false`, and no CLI
  cache reset. Covered by `FolderStatsScannerTests` (8 tests).
- [x] **M4 — persistence.** Migration 5 (`FolderMetrics` table), `Services/FolderMetricsStore.cs`
  (save/get/get-many/invalidate), `[JsonSerializable(typeof(List<FolderKindBucket>))]` in
  `AppJsonContext`. Only complete deep scans are stored; ancestor *and* descendant invalidation is
  wired into trash, upload, rename, copy and create-folder. Covered by `FolderMetricsStoreTests`
  (13 tests) and `MainWindowDeepMetricsTests` (7 tests).
- [x] **M5 — metrics UI** (partial: the shallow half). `ViewModels/FolderMetricsViewModel.cs`
  (`FolderMetricsViewModel`, `FolderMetricBucketViewModel`, `LargestItemViewModel`) and a
  "Métricas de la carpeta" section in the side panel: headline counts, total with an explicit
  scope note, a `Border`-based histogram, clickable top-5, newest/oldest. The panel is now
  scrollable, since status + selection + metrics overflows a short window. Covered by
  `FolderMetricsViewModelTests` (10 tests).
  Completed with M3/M4: the "Calcular tamaño total (recursivo)" button with its cost warning, the
  indeterminate progress line with a cancel button, the depth/age note ("Recursivo · 412 carpetas
  analizadas · calculado hace 3 días"), and the per-folder deep size in list rows and gallery
  tiles, loaded with one query per listing.
- [ ] **M6** — out of scope, recorded: thumbnails, whole-drive dashboard. See §7.

---

## 0. Executive summary

**V is cheap and self-contained.** The listing is already a flat collection of
`DriveNodeViewModel` rendered by a `TreeView` used as a list (`MainWindow.axaml:119`, no
hierarchical template — it is a list wearing a TreeView's clothes). Swapping the presentation is a
matter of replacing that control with one items control whose *panel* and *item template* are
selected by a `ViewMode` property. No service, no CLI, no schema change. Roughly a day.

**M is cheap shallow and expensive deep**, and the plan is built around that split:

- *Shallow* metrics (this folder's direct children) are **free**: `DriveItem` already carries
  `Size`, `ModifiedAt`, `IsFolder` for every child, and the rows are already in memory after
  `LoadFolderAsync`. Computing counts, total size and a type histogram is pure in-memory
  aggregation over `RootItems`. This lands first and covers most of the perceived value.
- *Deep* metrics (recursive: "how much does `Fotos` actually weigh") are **structurally
  expensive**. The CLI has **no recursive listing** (PLAN-LOCAL-SYNC Appendix A #4) and each
  `filesystem list` costs ~3.5 s of Node.js process startup regardless of folder size (#11a), with
  no subtree caching possible (#11b). So a deep scan is unavoidably `O(folders) × 3.5 s / concurrency`.
  It must therefore be **user-initiated, progress-reporting, cancellable, and persisted** — never a
  side effect of navigating into a folder.

Ordering: V1–V3 first (independent, immediately visible), then M1–M2 (free value, and M1's
classifier is what V3's icons need), then M3–M5 (the expensive part, once the cheap part has
proven the UI shape).

---

## 1. Current state this builds on

| Fact | Where |
|---|---|
| Listing is a flat `ObservableCollection<DriveNodeViewModel>` named `RootItems` | `ViewModels/MainWindowViewModel.cs:117`, filled by `DisplayItems` (`:990`) |
| Rendered by a `TreeView` with a single non-hierarchical `DataTemplate` | `Views/MainWindow.axaml:119-174` |
| Row already exposes `IsFolder`/`IsFile`/`DisplayName`/`SizeText`/`ModifiedText` and 5 commands | `ViewModels/DriveNodeViewModel.cs` |
| Sizes/mtimes come from the CLI's `activeRevision.value` (claimed size/mtime); **folders have neither** | `Models/DriveItem.cs` doc comment |
| Cache is SQLite `cache.db`, migrations in one shared list | `Services/DriveDatabaseMigrations.cs` |
| A key/value `AppSettings` table exists (migration 4), accessed via `SyncStateStore` | `DriveDatabaseMigrations.cs` migration 4, `Services/Sync/SyncStateStore.cs:41-75` |
| File-based settings are `settings.json` via `AppSettingsService`, serialized through `AppJsonContext` | `Services/AppSettingsService.cs`, `Services/AppJsonContext.cs` |
| Icons are `StreamGeometry` resources, currently only `IconFolder`/`IconFile` for rows | `Assets/Icons.axaml:11-12` |
| BFS-over-`filesystem list` already exists for sync, with private `XDG_CACHE_HOME` per process making concurrency safe | `Services/Sync/RemoteScanner.cs` |

Two consequences worth stating up front:

1. **Folder sizes do not exist remotely.** Any "this folder is 4.2 GB" number is *computed by us*
   from a recursive walk. There is no cheap server-side answer to fetch. This is the single fact
   that shapes all of M.
2. **`DriveItem.Size` is the claimed original size**, not the encrypted on-server size. Metrics must
   say so in the UI copy ("size as uploaded"), or the totals will not match Proton's own quota view.

---

## 2. V1 — View-mode state and persistence

**New model** — `Models/DriveViewMode.cs`:

```csharp
public enum DriveViewMode { List, Icons, Gallery }
```

**VM changes** — `MainWindowViewModel`:

- `public DriveViewMode ViewMode { get; set; }` via `SetProperty`, raising alongside it three
  computed booleans for the templates: `IsListView`, `IsIconsView`, `IsGalleryView`. Compiled
  bindings need a concrete `bool`; do **not** introduce an enum→bool converter (an extra
  reflection-shaped surface for AOT, and this VM already spells out derived state as properties —
  see `SelectedKind`, `CommandConsoleToggleGlyph`).
- `SetViewModeCommand` — one `AsyncCommand` per mode is clumsy; instead three thin commands
  (`ShowListViewCommand`, `ShowIconsViewCommand`, `ShowGalleryViewCommand`), matching the existing
  `ShowExplorer`/`ShowSettings` pattern (`:1096`, `:1102`). Each sets `ViewMode` and persists.
- **Do not rebuild `RootItems` on a mode change.** The collection is presentation-independent; only
  the items control's template changes. `DisplayItems` stays untouched, which also keeps the
  selection-carry-forward logic intact.

**Persistence** — reuse `AppSettings` (`settings.json`), not the SQLite KV table:

```csharp
public string ViewMode { get; set; } = nameof(DriveViewMode.List);
```

Rationale: `settings.json` is already loaded synchronously at startup before the first render, so
the app can paint in the user's chosen mode with no flash of list-mode. The SQLite KV table is
async and is read after construction (that was acceptable for the sync toggle, which has no
first-frame consequence). Store it as a **string**, and parse with
`Enum.TryParse(..., out var mode) ? mode : DriveViewMode.List` so an unknown value from a future
version degrades instead of throwing. `AppSettings` is already in `AppJsonContext`; adding a
`string` property needs no context change (**AOT check**: adding an `enum`-typed property *would*
change the generated converter set — that is the reason for the string).

Note `MainWindowViewModel:1066` currently does `_settings.Save(new AppSettings { ... })`, i.e. it
constructs a fresh instance. Adding a field there is a **latent data-loss bug for every new
setting**: saving the CLI path would reset the view mode. Fix as part of V1 — hold the loaded
`AppSettings` instance in a field and mutate-then-save, or add a `Save(Action<AppSettings>)`
mutator on `AppSettingsService`. Prefer the latter (keeps the VM out of settings-merging).

**Tests** (`tests/.../ViewModels/MainWindowViewModeTests.cs`):

- default is `List` when `settings.json` is absent;
- `ShowGalleryViewCommand` sets `ViewMode` and raises `IsGalleryView`/`IsListView`;
- an unrecognized persisted value falls back to `List`;
- switching mode does **not** clear `RootItems` nor the current selection;
- saving the CLI path after switching mode preserves the mode (the regression above).

---

## 3. V2 — The three presentations

Replace the `TreeView` at `MainWindow.axaml:119` with **one `ListBox`** whose `ItemsPanel` and
`ItemTemplate` are chosen per mode. Three sibling controls, each gated on `IsVisible`, is the
simpler and more debuggable option and is what this file already does for
explorer-vs-settings (`:291`); a single control with swapped templates avoids duplicating the
`ItemsSource`/selection wiring. **Recommendation: three sibling `ListBox`es**, because the item
templates differ in structure (not just style) and each mode wants a different panel; sharing one
control means runtime template swapping, which compiled bindings make awkward.

Moving from `TreeView` to `ListBox` is itself an improvement: `ListBox` gives real keyboard
navigation and `SelectedItem`, which the current button-per-row hack (`RowCommand`) fakes. Keep
`RowCommand` for the click-to-open-folder behavior (double-click semantics differ per mode), but
bind `SelectedItem` so arrow keys work.

### Mode: List (default, current look)

Unchanged template, moved verbatim. One addition for M5: a right-aligned size column bound to
`SizeText`, and (later) folder size when known.

### Mode: Icons — dense grid

- Panel: `WrapPanel` with `ItemWidth="112" ItemHeight="112"`.
- Template: 32×32 icon (from V3's classifier), name below, `TextTrimming="CharacterEllipsis"`,
  `MaxLines="2"`, `ToolTip.Tip` = full name.
- Row actions (copy/rename/download/trash) move into a **`ContextMenu`** — five buttons per tile
  does not fit and would be unreadable. Bind the same `AsyncCommand`s; a `ContextMenu` in a
  `DataTemplate` needs `x:DataType` on the template, which is already the rule here.

### Mode: Gallery — large tiles

- Panel: `WrapPanel` with `ItemWidth="176" ItemHeight="200"`.
- Template: 64×64 icon centered in a `Border` with `CardBackgroundBrush`, name, and a
  metadata line (`SizeText` for files, item count for folders once M2 lands).
- **No image thumbnails.** The CLI exposes no thumbnail/preview endpoint, and rendering real
  previews would mean downloading every image in the folder through a ~3.5 s-per-call CLI process
  into a local thumb cache. That is a separate feature with its own cost and privacy story — see
  §7 (M6). "Gallery" here means *large tiles*, and the UI copy should not promise previews.

### Virtualization — the one real risk

`WrapPanel` **does not virtualize** in Avalonia. A folder with a few thousand children in Icons
mode would materialize every tile. This repo has already been burned by exactly this class of
problem (see the `TextWrapping="NoWrap"` comment at `MainWindow.axaml:~275`: a 30-second UI hang
in `TextBlock.Measure`).

Plan:

1. Implement V2 with `WrapPanel` — simple, and correct for the folder sizes in the screenshot.
2. **Measure** before merging: generate a 2 000-item folder via the stub CLI (`run-app` skill) and
   time the mode switch. Record the number in this document.
3. If it is slow, switch the panel to `ItemsRepeater` + `UniformGridLayout`, which *is* virtualized.
   **Verify first** that `ItemsRepeater` ships in the `Avalonia` 12.0.4 package (it has been in
   core since 11.x) rather than needing a new `PackageReference` — a new dependency here would need
   the AOT/trim pass from the `aot-check` skill and a publish-date check.

Do not pre-emptively adopt `ItemsRepeater`: it has no built-in selection, so it would also mean
hand-rolling what `ListBox` gives free.

**Status of step 2:** not done. The three modes are implemented on `WrapPanel` and the app launches
against the stub CLI without error, but the number in step 2 has not been measured and no one has
looked at the rendered window yet — the session that built this had no screenshot capability on
Wayland. Compiled bindings *are* validated (the build fails on an unresolvable one), so the
bindings are known good; the layout is not.

**Tests.** XAML has no unit coverage in this repo; correctness here is the VM (V1) plus the
`smoke-test` skill checklist, extended with: switch each mode in a folder with mixed
files/folders, context-menu actions in Icons/Gallery, keyboard arrows + Enter, and a
large-folder pass.

---

## 4. V3/M1 — File-kind classification (shared)

Both features need "what kind of file is this": V3 to pick an icon, M2/M3 to build the histogram.
One place, no duplication.

**New** `Services/FileKindClassifier.cs` (pure, static, no I/O — trivially testable and AOT-safe):

```csharp
public enum FileKind { Folder, Image, Video, Audio, Document, Spreadsheet, Presentation,
                       Pdf, Archive, Code, Text, Other }

public static FileKind Classify(string name, bool isFolder);
public static string DisplayName(FileKind kind);   // "Imágenes", "Vídeos", ... (UI language)
```

Implementation: a `FrozenDictionary<string, FileKind>` from lowercased extension, built once.
Rules to get right (each becomes a test):

- No extension → `Other`; dotfiles (`.bashrc`) → `Text`, not "extension `bashrc`".
- Multi-part extensions: `.tar.gz`, `.tar.bz2` → `Archive` (check the last *two* segments first).
- Case-insensitive, invariant (`ToLowerInvariant`, not culture-sensitive — a Turkish-locale `I`
  bug here would silently misclassify `.JPG`).
- Unknown extension → `Other`, and M2 keeps the raw extension string separately so the histogram
  can still show `.webm` even before it is mapped.

Icons: add `IconImage`, `IconVideo`, `IconAudio`, `IconPdf`, `IconArchive`, `IconCode` to
`Assets/Icons.axaml` in the existing hand-written `StreamGeometry` style (24×24 viewbox, stroke
only). `DriveNodeViewModel` exposes `public string IconKey => ...` — but XAML cannot bind
`StaticResource` by a string key. Two options: (a) one `Path` per kind with `IsVisible` bound to
`IsImage`/`IsVideo`/… (verbose but compiled-binding-clean, and how `IsFolder`/`IsFile` already
work), or (b) expose the `Geometry` itself from the VM, which puts an Avalonia type in a
ViewModel — **forbidden by the MVVM non-negotiables in AGENTS.md**. So: **(a)**, generated by a
small `Style`-per-kind block. If the verbosity gets out of hand, an `IValueConverter` in
`Views/` returning the geometry from `Application.Current.Resources` is the escape hatch (a View
concern, so it may touch Avalonia types).

---

## 5. M2 — Shallow metrics (free)

**New model** `Models/FolderMetrics.cs`:

```csharp
public sealed record FolderKindBucket(FileKind Kind, int Count, long TotalSize);

public sealed record FolderMetrics(
    string Path,
    bool IsDeep,                 // false = direct children only
    int FileCount,
    int FolderCount,
    long TotalSize,              // sum of known file sizes
    int UnknownSizeCount,        // files whose Size was null — makes TotalSize honest
    IReadOnlyList<FolderKindBucket> Buckets,
    IReadOnlyList<DriveItem> LargestItems,   // top 5
    DateTimeOffset? NewestModifiedAt,
    DateTimeOffset? OldestModifiedAt,
    int ScannedFolderCount,      // 1 for shallow
    DateTimeOffset ComputedAt);
```

**New** `Services/FolderMetricsCalculator.cs` — `static FolderMetrics FromChildren(string path,
IReadOnlyList<DriveItem> children, DateTimeOffset now)`. Pure aggregation. Called from
`MainWindowViewModel.DisplayItems`, so metrics refresh automatically with the listing (cache paint
first, then the CLI result — same two-phase behavior the listing already has, which is fine: the
number just sharpens).

`UnknownSizeCount` matters: nested folders contribute 0 to a shallow total, so the UI must read
"1.2 GB in this folder's files (7 subfolders not counted)" and never a bare total that looks
recursive. Getting this wrong is worse than not shipping the feature.

**Tests** (`Services/FolderMetricsCalculatorTests.cs`): empty folder; folders-only; mixed with
`null` sizes; bucket ordering (by total size desc, ties by count desc, then kind name for
determinism); `LargestItems` with fewer than 5 items; newest/oldest with all-`null` mtimes.
Use `FakeTimeProvider` for `ComputedAt`.

---

## 6. M3/M4/M5 — Deep metrics

### M3 — `Services/FolderStatsScanner.cs`

Mirror `RemoteScanner` (`Services/Sync/RemoteScanner.cs`) rather than reuse it: `RemoteScanner`
returns a `NodeFingerprint` dictionary keyed by *sync-relative* path, needs a `PathMapper` and an
`ExclusionMatcher`, and calls `ResetRemoteCacheAsync` at scan start. Metrics need none of that and
must not force a CLI cache reset (that would slow the user's next navigation). **Extract the shared
BFS-wave-with-semaphore loop** into `Services/RemoteTreeWalker.cs` and let both call it with a
per-node callback — otherwise there are two BFS implementations to keep correct, and the concurrency
reasoning in `RemoteScanner`'s doc comment (private `XDG_CACHE_HOME`, Appendix A #16) has to be
duplicated.

```csharp
public sealed class FolderStatsScanner
{
    public Task<FolderMetrics> ScanAsync(
        string remotePath,
        IProgress<FolderScanProgress>? progress,   // (foldersDone, foldersQueued, currentPath)
        CancellationToken cancellationToken);
}
```

Requirements, all load-bearing:

- **Cancellable at every await.** A user who starts a scan of `Development` and changes their mind
  after 3 minutes must be able to stop it, and navigating away should cancel it.
- **Progress is a count, not a percentage.** BFS does not know the total folder count until it is
  done. Report "142 folders scanned, 37 queued" — an honest indeterminate, not a fake bar.
- **Partial results survive cancellation.** Return what was aggregated with a
  `IsComplete = false` marker, and label it in the UI as partial. Do not persist a partial result
  as if it were complete (M4).
- **One scan at a time**, app-wide. Two concurrent deep scans would fight the executor's
  concurrency ceiling with the sync engine and the browser's own listing. A second request either
  queues or is refused with a status message.
- **Reuse `cache.db` rows where valid?** *No, not in v1.* The cache holds only folders the user has
  visited, with no per-folder freshness stamp (PLAN-TECH-DEBT B4.2 is still open), so a
  cache-assisted scan would silently under-count. Revisit after B4.2 lands, and record that
  dependency here.

**Cost, to state in the UI before the user commits:** at ~3.5 s per folder and 8-way concurrency,
~0.45 s per folder → a 500-folder subtree is ~4 minutes. The confirmation prompt should say
"this scans every subfolder and can take several minutes" and the button should be explicit
("Calcular tamaño total"), never automatic on navigation.

### M4 — Persistence

Migration **5** appended to `DriveDatabaseMigrations.All` (never renumber):

```sql
CREATE TABLE FolderMetrics (
    Path            TEXT PRIMARY KEY,
    IsDeep          INTEGER NOT NULL,
    FileCount       INTEGER NOT NULL,
    FolderCount     INTEGER NOT NULL,
    TotalSize       INTEGER NOT NULL,
    UnknownSizeCount INTEGER NOT NULL,
    ScannedFolderCount INTEGER NOT NULL,
    NewestModifiedAt TEXT,
    OldestModifiedAt TEXT,
    BucketsJson     TEXT NOT NULL,
    ComputedAt      TEXT NOT NULL
);
```

Only **deep, complete** results are persisted — shallow ones are recomputed for free on every load,
and persisting them would just be a second source of truth to invalidate. `BucketsJson` is a
serialized `IReadOnlyList<FolderKindBucket>`: **add `[JsonSerializable(typeof(List<FolderKindBucket>))]`
to `AppJsonContext`** (AOT non-negotiable). Access via a new `FolderMetricsStore` following
`DriveCacheService`/`SyncStateStore` conventions: `SqliteOffThread.RunAsync`, parameterized
commands, tolerant parsing of `ComputedAt` (same "degrade, never throw" rule as
`DriveCacheService.ParseModifiedAt`).

Staleness: display the age ("calculado hace 3 días") and offer recalculation. Do **not** silently
auto-expire — the number cost the user four minutes. Invalidate the row (and ancestors) when the app
itself mutates the subtree: `DriveCacheService.RemoveItemAsync` already handles the `path LIKE
prefix` pattern; deep metrics need the *inverse* (invalidate every **ancestor** of a changed path),
which is a `WHERE @Path LIKE Path || '/%'` delete. Wire it into the existing trash/upload/rename/copy
paths in `MainWindowViewModel`; a metric that stays stale after the user deletes a 2 GB file is a
bug report waiting to happen.

### M5 — UI

The right-hand side panel (`MainWindow.axaml:177-236`) already shows selected-item details and
"Current folder". Add a **"Métricas"** section there:

- Always (M2): "N archivos · M carpetas · 1,2 GB en esta carpeta"; the kind histogram as
  labelled rows with a proportional `Border` width per bucket (a bar chart made of `Border`s — no
  charting dependency), each with count and size; top-5 largest items as clickable rows that select
  the item.
- On demand (M3): a "Calcular tamaño total (recursivo)" button → progress line with folders-scanned
  + a cancel button → the deep result replacing the shallow total, labelled with its `ComputedAt`
  age and `ScannedFolderCount`.
- **Per-folder deep sizes in the listing**: once a folder's deep metric exists in `FolderMetrics`,
  show it in that folder's row/tile. This is the payoff — the browser gradually learns folder sizes.
  Load them in one query per listing (`WHERE Path IN (...)`), not one per row.

Panel selection (Explorer/Settings) lives in the same header row as the view-mode buttons; add the
three mode buttons as a segmented control next to Refresh, using the existing `Classes="rowAction"`
button style and `ToolTip.Tip`, with the active mode marked via a `Classes.selected` binding (the
pattern already used for row selection at `MainWindow.axaml:122`).

---

## 7. Explicitly out of scope

- **Image/video thumbnails** (M6). Needs per-file downloads through the CLI plus a local thumb
  cache with eviction, and a decision about writing decrypted user content to disk. Separate plan.
- **Whole-drive dashboard / quota view.** Would mean a full-tree scan (hours on a large drive) or a
  Proton API call the app is architecturally forbidden from making (AGENTS.md: `CliReleaseFeed` is
  the *only* outbound call). Not attempted.
- **Duplicate detection** via `ContentHash`. Tempting — the column exists — but it needs the same
  full-tree scan and its own UI. Park in `PLAN-TECH-DEBT.md` if it comes up.
- **Sorting/filtering by kind.** V4 covers sort direction only; faceted filtering is a later
  feature.
- **Trash and shared-with-me views.** Unrelated to both features.

---

## 8. Sequencing and why

1. **V1 → V2 → V3.** V1 is pure VM + persistence with real tests, and fixes the
   `Save(new AppSettings{...})` data-loss bug that every future setting would hit. V2 is
   presentation only. V3's classifier is needed by V2's tiles *and* by M2, so it is the hinge.
2. **M1/M2 next.** Zero CLI cost, immediate value, and it validates the side-panel layout before
   any expensive machinery is built on the assumption that layout is right.
3. **M3 last**, after `RemoteTreeWalker` is extracted and `RemoteScanner`'s tests still pass —
   refactoring the sync engine's BFS is the riskiest edit in this plan and must not be bundled with
   a UI change.
4. **M4 with M3**, not after: an un-persisted deep scan that a user has to re-run costs minutes and
   would read as broken.

Suggested commits: `feat(browser): view mode state and persistence` · `feat(browser): icons and
gallery item templates` · `feat(browser): classify files by kind` · `feat(metrics): shallow folder
metrics` · `refactor(sync): extract RemoteTreeWalker from RemoteScanner` · `feat(metrics):
recursive folder scan with progress and cancel` · `feat(metrics): persist deep metrics` ·
`feat(metrics): metrics panel and folder sizes in the listing`.

## 9. Definition of done

- `./scripts/run-tests.sh` green, with new tests for `FileKindClassifier`,
  `FolderMetricsCalculator`, `FolderStatsScanner` (against `FakeCliExecutor`),
  `FolderMetricsStore`, the view-mode VM, and a migration-5 round-trip.
- `aot-check` skill run after touching `AppJsonContext` (M4) and after any new package (V2 fallback).
- `smoke-test` skill extended and run: three modes, context-menu actions, keyboard nav, a
  large-folder pass with the measured number recorded in §3, a deep scan cancelled mid-way, and a
  deep metric invalidated by deleting a file.
- `ARCHITECTURE.md` §1 updated (browser now has view modes and metrics) with a new commit reference.
- The status block at the top of this file ticked and honest, `(partial)` where partial.
