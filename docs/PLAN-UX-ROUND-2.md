# Technical Plan — UX Round 2

> Second round of user-experience work, driven by a review of the running app (three screenshots
> of the Explorer, the Settings view, and the sync pair list, 2026-09-05) checked against the
> source. Round 1 was [INTERFACE_IMPROVEMENT_PLAN.md](INTERFACE_IMPROVEMENT_PLAN.md) — the
> dual-pane explorer, breadcrumbs, quota gauge, connection chip and activity panel it specified
> have all shipped. This document covers what that round *created* or left unfinished, now that
> the interface exists and can be used against real accounts.
>
> Companion to [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md) (whose P10 live-verification
> session surfaced two of the items below) and
> [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md#7-mapping-to-plan-local-syncmd).
>
> No implementation branch yet. Current branch at time of writing: `feature/google-drive-provider`.

## Status

- [ ] **U1 — Recoverable error surface.** Not started.
- [ ] **U2 — Consolidate the three connection/identity indicators.** Not started.
- [ ] **U3 — Stop the quota gauge asserting `0 B` when the size is unknown.** Not started.
- [ ] **U4 — Single UI language.** Not started. Blocks nothing, blocked by nothing, but must be
      one atomic pass — see §5.
- [ ] **U5 — Promote sync to a top-level view.** Not started.
- [ ] **U6 — Per-action detail for sync failures.** Not started. Depends on U5 for its home.
- [ ] **U7 — Disambiguate the two chip rows in the sync section.** Not started.
- [ ] **U8 — Per-provider auth state on the Conexión tabs.** Not started.
- [ ] **U9 — Search affordances (result count, clear).** Not started.
- [ ] **U10 — Deduplicate the two breadcrumb implementations.** Not started.

**Investigated and dismissed:** the recursive folder scan *does* have cancellation
(`CancelDeepScanCommand`, "Cancelar análisis" button, a real `CancellationToken` threaded to
`IFolderStatsScanner.ScanAsync`) — the initial review claimed otherwise and was wrong. See
[Appendix A](#appendix-a--claims-checked-against-the-source).

---

## 0. Executive summary

The round-1 interface is built and works. What the screenshots show is the second-order problem
of a UI that grew feature-first: **the app knows more than it tells the user, and tells the user
things it does not know.**

Three distinct failure modes, in descending order of how much they cost the user:

1. **Dead ends.** The app detects a problem, names it, and offers no way out. `Failed to load
   /my-files: Invalid access token` is a yellow card with an icon and no button, next to a
   `RefreshCommand` that exists and is not bound to it. The user's only recovery is to guess.
2. **Assertions the app can't back.** `0 B / 500 GB (0% used)` is displayed above a folder listing
   with 14 items, because `UpdateQuotaMetrics` sums only files where `Size.HasValue` and the
   provider returned no sizes. "Unknown" and "empty" render identically. Likewise the header
   claims `Online` in the same frame the body reports a failed load.
3. **Information the app already has and discards.** `SyncPairViewModel` calls
   `GetFailedActionsAsync(_pair.Id)` — which returns full `QueuedSyncAction` rows with
   `RelativePath`, `Operation` and `LastError` — and keeps `.Count`. The user is shown
   `4 action(s) failed` and a `Retry failed actions` button, and is asked to retry blind. The
   equivalent path for *conflicts* already does this properly (`RequestConflictResolutionsAsync`
   → `ShowConflictsAsync(IReadOnlyList<QueuedSyncAction>)`), so the pattern to copy exists in
   the same file.

Cutting across all three is a structural fact worth stating plainly: **`Views/MainWindow.axaml`
is 1843 lines and the application contains no `UserControl` at all.** Every view — explorer,
viewer, settings, sync — is a `IsVisible`-toggled section of one file. That is why the two
breadcrumb bars are copy-pasted markup (U10) and why moving sync out of the settings scroll
(U5) is a layout change rather than a routing change. U5 and U10 are the natural moments to
start extracting controls; neither should turn into a speculative full decomposition.

### Ordering

The sequence is not "worst first". It is driven by one constraint: **U4 rewrites nearly every
user-visible string in `MainWindow.axaml`, so it conflicts with every other item in this plan.**

1. **U1, U2, U3** first (§1–§3). Small, independent, header/status-panel-local, and they are the
   items that make the app feel untrustworthy. Doing them before U4 means the language sweep sees
   their final strings once, instead of translating text that is about to be rewritten.
2. **U4** next (§4), as a single atomic commit. Any later item then writes strings in the settled
   language.
3. **U5** (§5) — the structural move. After U4 so we relocate already-consistent markup; before
   U6/U7 because both live in the section being moved.
4. **U6, U7, U8** (§6–§8) — sync and settings work, in the home U5 gives them.
5. **U9, U10** (§9–§10) — explorer polish. Lowest user cost, fully independent, safe to drop or
   defer without stranding anything above.

### Out of scope

- Any localization *framework* (`.resx`, `ILocalizer`, culture switching, RTL). U4 is a
  consistency fix, not an i18n feature. Real multi-language support is a separate plan and should
  not be smuggled in as "while we're in here".
- Decomposing `MainWindow.axaml` into a full view/routing architecture. U5 and U10 extract what
  they need and no more.
- Real quota APIs. U3 fixes the *display* of an unknown value; actually asking each provider for
  its true used/total bytes is provider work and belongs in
  [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md). See §3.
- Per-item sync-state badges on explorer rows (round-1 §2.2, never built). Still wanted, still
  not this round.

---

## 1. U1 — Recoverable error surface

**Observed.** Status panel shows `⚠ Failed to load /my-files: Invalid access tok…` (clipped at the
window edge). No action control anywhere in the card.

**Source.** The message is built by `MainWindowViewModel.FormatDriveError`
(`ViewModels/MainWindowViewModel.cs:3703`), stored in `StatusMessage` (`:959`), flagged by
`IsWarning` (`:972`). Rendered at `Views/MainWindow.axaml:1041-1069` — a `Border` named
`StatusBorder` containing exactly a warning `Path` and a `TextBlock`. `RefreshCommand` exists
(`MainWindowViewModel.cs:406`, `:492`) and is not bound anywhere near it.

**Do.**
1. Give the status card an action slot. When `IsWarning` is set by a failure that has a known
   remedy, bind a button to it.
2. Distinguish two remedies, because they are not the same: a transient load failure needs
   **Refresh** (`RefreshCommand`); an auth failure — `CliErrorKind` / the provider classifiers
   already distinguish this — needs **Reconnect**, i.e. the active provider's sign-in flow.
   Deriving which to offer from the existing error kind is the point of this item; do not
   pattern-match the message string.
3. Note that `StatusMessage`'s setter resets `IsWarning = false` (`MainWindowViewModel.cs:966`),
   so the action slot's visibility must be driven off the same state, not set independently, or
   it will outlive its message.

**Not confirmed.** The clipping in the screenshot is *not* `TextTrimming` — the `TextBlock` has
`TextWrapping="Wrap"` and no `MaxLines` (`MainWindow.axaml:1053-1056`). So the text is being cut
by the Status panel column's width/clip at that window size, not by a trimming setting. Reproduce
at the screenshot's window width before choosing a fix.

## 2. U2 — Consolidate the three connection/identity indicators

**Observed.** Screenshot 1: header chip reads `● Online` while the body reports a failed load.
Screenshots 2 and 3: header chip reads `● Disconnected` while the account ComboBox immediately to
its left still shows `user@proton.me` with a **green** dot.

**Source.** These are three different things wearing the same clothes:

| Indicator | Bound to | Means |
|---|---|---|
| ComboBox dot (`MainWindow.axaml:144-158`) | `ProviderDescriptor.IsAuthenticated` (`Services/Providers/ProviderDescriptor.cs:8`) | *signed in* — green `#28A745` / grey `#6C757D` |
| Header chip (`MainWindow.axaml:170-205`) | `MainWindowViewModel.ConnectionStatus` (`:640`), computed in `UpdateConnectionTelemetry()` (`:3104-3142`) | *reachable* — Online / Syncing / Rate-Limited / Disconnected |
| Status card | `StatusMessage` / `IsWarning` | *the last operation's outcome* |

Being signed in and being reachable are genuinely different axes, and the app is right to track
both. The defect is that they are rendered as two coloured dots five pixels apart, so they read
as one contradictory signal.

**Do.**
1. Make the difference legible — the account dot should say "signed in", the chip should say
   "reachable", by shape/label/tooltip rather than by two dots of the same size.
2. Make the chip actionable. It is a plain `Border` + `Ellipse` + `TextBlock` with only a
   `ToolTip.Tip` (`MainWindow.axaml:170-205`). When `IsDisconnected`, it should be a button that
   retries. This overlaps U1 deliberately: both are "the app knows it's broken and offers no
   verb", and they should land as one coherent recovery story.
3. Check why the chip said `Online` while the load failed. `UpdateConnectionTelemetry` should be
   driven by, or at minimum invalidated by, the same failure that set `IsWarning`.

## 3. U3 — Stop the quota gauge asserting `0 B`

**Observed.** `0 B / 500 GB (0% used)` in the header, above a root listing of 14 items, while the
Status panel simultaneously admits `6 archivos sin tamaño conocido`.

**Source.** `MainWindowViewModel.QuotaDisplay` (`:662-664`), fed by `_quotaUsedBytes` — a `long`
initialised to `0` (`:141`) and recomputed only when browsing the root path, summing files where
`Size.HasValue` (`UpdateQuotaMetrics`, `:3159-3161`). Files with unknown size are silently
skipped; browsing a non-root folder leaves the previous value in place. `ByteSize.Format(0)`
returns `"0 B"` (`Services/ByteSize.cs:28-31`). **Unknown, not-yet-computed and genuinely-empty
all render identically.** Separately, `QuotaTotalBytes` is hardcoded per provider
(`MainWindowViewModel.cs:3146-3153`; 500 GB for Proton, `:142`, `:3152`) — the `500 GB` half of
that string is a constant, not a fact about the account.

**Do.**
1. Make "used" a nullable/tri-state (unknown / partial / exact) rather than a `long` that defaults
   to zero. When unknown, render `— / 500 GB` and leave the `ProgressBar` empty or hidden; never
   `0 B (0% used)`.
2. When the sum is known to be *partial* (some siblings had no size, or we are not at the root),
   say so — `≥ 4.2 GB` with a tooltip beats a confident wrong number.
3. Label the hardcoded total honestly in the tooltip until a provider actually reports it.

**Out of scope here:** fetching real quota from each provider's API. That is provider work
(Proton CLI, Graph `/me/drive`, Drive `about.get`) and belongs in
[PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md). U3 only stops the UI from lying while that
number is unavailable.

## 4. U4 — Single UI language

**Observed.** One screenshot of the settings view contains, together: *Preferencias Generales*,
*User Settings*, *Status Visible*, *Conexión*, *Sincronización de carpetas*, an entire English
paragraph (`Keeps a Proton Drive folder and a local folder in step…`), and the buttons *Add pair*
/ *Refresh* / *Retry failed actions* beside the chip *Todos (3)* and the statuses *Two-way* /
*Up to date*.

**Source.** There is no localization mechanism in the repository — no `.resx`, no resource
dictionary of strings, no localizer service. Every user-visible string is a literal in
`Views/MainWindow.axaml` or interpolated in a ViewModel (`AccountSyncToggleViewModel.cs:37`,
`ProviderFilterViewModel.cs:29` — `"Todos"` at `:27` —, `SyncPairViewModel.cs:433`,
`FolderMetricsViewModel.cs:302-329`, `MainWindowViewModel.cs:3703`).

**Language decision: Spanish.** Decided by the repo owner on 2026-09-05, against this document's
own recommendation of English. Recorded here because the reasoning matters for future work: the
recommendation was based on diff size (most chrome, every `SyncPairStatus` string, and all
pass-through CLI output are already English), and the owner chose Spanish anyway because it is
the language the application is actually used in. That means U4 is the **larger** sweep — sync
statuses, error messages and the whole English half of the settings view all get translated.

**Boundary: the CLI's own output is not translated.** Text that originates from `proton-drive`
or a provider API and passes through the app verbatim (the activity console, raw error strings
embedded in a message) stays as the tool emitted it. Only strings the app itself authors are in
scope.

**Do.** One sweep, one commit, no behavior change. Both the `.axaml` literals and the ViewModel
interpolations — the string tables in `FolderMetricsViewModel` and `SyncPairViewModel` are the
easy ones to miss. Grep for non-ASCII (`[áéíóúñ¿¡]`) as a completeness check, then re-run the
screenshots.

**Explicitly not this item:** introducing `.resx`/`ILocalizer`/culture switching. If bilingual
support is genuinely wanted it is a separate plan with its own AOT considerations (see
`.claude/skills/aot-check/`), and doing it here would turn a mechanical sweep into a feature.

## 5. U5 — Promote sync to a top-level view

**Observed.** *Sincronización de carpetas* is the last section of the Settings scroll. Checking
whether a sync pair failed requires opening Configuración and scrolling past General Preferences,
User Settings, and the whole Conexión block including the proton-drive executable path.

**Source.** `MainWindow.axaml:1432` opens a single `ScrollViewer` bound to `IsSettingsView`;
`"Preferencias Generales"` at `:1436`, `"User Settings"` at `:1479`, `"Conexión"` at `:1486`, and
`"Sincronización de carpetas"` at `:1699` — with `DataContext="{Binding SyncPanel}"` set at
`:1698` — are all sections of that one scroll. The top-level "tabs" at `:258-285` are not a
`TabControl` but two `Button`s driving two independent booleans, `IsSettingsView`
(`MainWindowViewModel.cs:822`) and `IsViewerVisible` (`:1089`).

**Do.**
1. Add a third top-level entry beside *Explorador* and *Visor* for sync. The `SyncPanel`
   `DataContext` is already scoped at `:1698`, so the markup block is liftable largely as-is.
2. Replace the two independent booleans with one selected-view value. Two booleans for three
   mutually exclusive views is a bug waiting to happen (both true, or neither) and the third view
   is the point at which it stops being tolerable.
3. Extract the lifted block into the first `UserControl` in the codebase. This is the cheapest
   honest place to start splitting the 1843-line file; do not extend the extraction beyond the
   sync section in this item.
4. Surface failure state on the tab itself — a badge when any pair is in `PartialFailure` — so the
   user does not have to open the view to learn there is something to open it for.

## 6. U6 — Per-action detail for sync failures

**Observed.** A pair card reading `Partial failure (8/30/2026 11:22 AM): 4 action(s) failed` with
a single `↻ Retry failed actions` button. Dated 30 August, still present on 5 September — looked
at, not understood, abandoned.

**Source.** The string is composed at `ViewModels/Sync/SyncPairViewModel.cs:433`
(`UpdateStatusText()`, `:427`), with the `4 action(s) failed` tail coming from
`Services/Sync/SyncExecutor.cs:171` (`BuildStatusMessage`, `:166`). The button is
`MainWindow.axaml:1809-1813`, bound to `RetryFailedCommand` (`SyncPairViewModel.cs:103`, `:334`)
and gated on `HasFailures` (`:153`).

**The detail already exists and is thrown away.** `Models/QueuedSyncAction.cs:4` carries
`RelativePath`, `Operation`, `SecondaryPath`, `AttemptCount`, `State`, `LastError` and
`EnqueuedAt`; `Services/Sync/SyncStateStore.cs:690` `GetFailedActionsAsync` returns those rows in
full. The only ViewModel consumer collapses them: `SyncPairViewModel.cs:277` —
`FailedCount = (await _stateStore.GetFailedActionsAsync(_pair.Id)).Count;` (same shape at
`SyncExecutor.cs:152-153`).

**Do.** Copy the pattern the conflict path already uses in the same class:
`SyncPanelViewModel.cs:189` `RequestConflictResolutionsAsync` →
`Views/MainWindow.axaml.cs:1360` `ShowConflictsAsync(IReadOnlyList<QueuedSyncAction>)`. A failures
view over the same row type, listing path, operation and `LastError` per action, with per-action
retry/discard alongside the existing retry-all. Keep `FailedCount` for the collapsed card;
add the expansion, don't replace the summary.

## 7. U7 — Disambiguate the two chip rows

**Observed.** Two adjacent rows of pill-shaped controls in the sync section:
`⏸ Proton Drive: on | ⏸ OneDrive: on | …` directly above
`Todos (3) | Proton Drive (3) | OneDrive (0) | …`. Same shape, same size, adjacent, same
provider names — and they do entirely different things.

**Source.** Confirmed distinct.
- Row 1 (`MainWindow.axaml:1729-1743`) is `AccountSyncToggles`
  (`ViewModels/Sync/SyncPanelViewModel.cs:113`, populated `:99`), items of type
  `AccountSyncToggleViewModel` whose `Label` (`:37`) is
  `IsRunning ? $"⏸ {DisplayName}: on" : $"▶ {DisplayName}: off"`. These **start and stop the
  scheduler** for that account.
- Row 2 (`MainWindow.axaml:1749-1783`) is `ProviderFilters` (`SyncPanelViewModel.cs:120`,
  rebuilt `:354`), items of type `ProviderFilterViewModel` with `LabelWithCount` (`:29`) and
  `ApplyCommand` (`:37`), driving `VisiblePairs` (`:131`). These **filter the list below**.

One row changes what the app does; the other changes what you can see. Rendering them as
neighbouring twins is the defect.

**Do.** Separate them by role, not by decoration — the toggles belong with the pair list's
controls (near *Add pair* / *Refresh*, or on each provider's own card), the filter chips belong
directly above the list they filter, matching the explorer's kind-filter idiom they were modelled
on. If they must stay adjacent, they need different affordances (switch vs. chip), not different
colours.

## 8. U8 — Per-provider auth state on the Conexión tabs

**Observed.** The five tabs *Proton Drive | OneDrive | Google Drive | Nextcloud | Custom S3* are
visually identical regardless of whether that provider has ever been configured.

**Source.** `MainWindow.axaml:1488-1495` — five buttons whose only state binding is
`Classes.primary` on `IsProtonActive` / `IsOneDriveActive` / `IsGoogleDriveActive` /
`IsNextcloudActive` / `IsS3Active` (`MainWindowViewModel.cs:275-283`), each defined purely as
`_provider.Id == ProviderId.X`. **Selected, not authenticated.** Per-provider auth state *is*
already materialised — `ProviderDescriptor.IsAuthenticated` / `AccountSummary`
(`Services/Providers/ProviderDescriptor.cs:8`, `:10`), filled by
`MainWindowViewModel.RefreshAvailableProviders()` (`:172`, `:183`, `:196`) into
`AvailableProviders` (`:162`) — but it is rendered only as the header ComboBox dot
(`MainWindow.axaml:144-159`). The Conexión tabs never see it. Per-card auth affordances exist
only for Nextcloud (`:1654`, `:1660`) and S3 (`:1681`, `:1687`), and only for the *active*
provider.

**Do.** Reuse `AvailableProviders`/`ProviderDescriptor` to put the same green/grey dot on each
Conexión tab. This is a binding change, not new state.

> **Doc discrepancy to resolve.**
> [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md) §P10 (around line 977) states that
> `IsGoogleDriveAuthenticated` / `GoogleDriveAccountLabel` "already existed from an earlier
> UI-scaffolding commit". A search of the source found **no `Is<Provider>Authenticated` property
> of any kind**; the only auth flag is `MainWindowViewModel.IsAuthenticated` (`:945`), for the
> active provider, backed by `AppSettings.IsProviderAuthenticated(_provider.Id)` (`:1597`).
> Confirm which is true and correct whichever document is wrong before building on it.

## 9. U9 — Search affordances

**Observed.** Both panes have a `Buscar en esta carpeta…` box that gives no feedback: no result
count, no way to clear it but selecting and deleting.

**Source.** Remote: `MainWindow.axaml:393-397`, a plain `TextBox Width="200"` bound to
`MainWindowViewModel.SearchText` (`:744`, setter calls `RenderItems()`). Local:
`MainWindow.axaml:853-857`, same shape, bound to `LocalExplorerViewModel.SearchText` (`:134`,
same setter shape). No `ClearSearchCommand` and no result-count property exists in either
ViewModel. The nearest existing precedent for showing counts is the kind chips
(`ViewModels/KindFilterViewModel.cs`), which already do it.

**Do.** A match count and a clear affordance in both panes, following the kind chips' existing
count formatting. A filter that hides rows without saying how many it hid is the specific problem.

## 10. U10 — Deduplicate the breadcrumbs

**Observed.** The remote breadcrumb renders as `Proton Drive` above chips `my-files`; the local
one as `Este equipo (local)` above chips `/ | home | ramiro`. They are mirrored panes and do not
look mirrored.

**Source.** Two separate copy-pasted markup blocks. Remote: `MainWindow.axaml:304-322`, a named
`ScrollViewer` + `ItemsControl` over `MainWindowViewModel.BreadcrumbItems` (`:468`, built by
`UpdateBreadcrumbs(string)` at `:3015`), with an inline `DataTemplate` at `:314`. Local:
`MainWindow.axaml:834-850`, an unnamed `ScrollViewer` with its own duplicated `ItemsControl` /
`ItemsPanel` / `DataTemplate` at `:842`, over `LocalExplorerViewModel.BreadcrumbItems` (`:52`,
built by `RebuildBreadcrumbs()` at `:358`). No shared `UserControl`, `ControlTemplate` or
`DataTemplate` resource — the only shared piece is the item type
`ViewModels/BreadcrumbSegmentViewModel.cs`.

**Do.** Extract one breadcrumb `UserControl` over `BreadcrumbSegmentViewModel` and use it in both
panes. The item ViewModel is already common, so this is markup consolidation with no ViewModel
change — and it makes the two panes consistent by construction rather than by discipline.

---

## Appendix A — Claims checked against the source

The initial screenshot review made eleven claims. Each was checked before being written up here.
Recording the corrections because two of them were wrong in ways worth not repeating.

| # | Claim | Verdict |
|---|---|---|
| 1 | Error card has no retry action | **Confirmed** — `MainWindow.axaml:1041-1069` contains only icon + text; `RefreshCommand` exists unbound |
| 1b | Error text is truncated by `TextTrimming` | **Wrong** — `TextWrapping="Wrap"`, no `MaxLines` (`:1053-1056`). The clipping is a panel-width/clip issue at that window size; unreproduced |
| 2 | Chip and account dot contradict each other | **Confirmed, and sharper than claimed** — they are bound to different things (`ConnectionStatus` vs `ProviderDescriptor.IsAuthenticated`), rendered alike. Dot is green/**grey**, not green/red |
| 2b | Chip is not clickable | **Confirmed** — plain `Border`, `ToolTip.Tip` only (`:170-205`) |
| 3 | Quota shows `0 B` for unknown | **Confirmed** — `long` defaulting to `0`, `Size.HasValue` files only (`:141`, `:3159-3161`). Also found: total is hardcoded per provider (`:3146-3153`) |
| 4 | No localization system; strings mixed | **Confirmed** — no `.resx` or localizer anywhere; literals in `.axaml` and interpolated in ViewModels |
| 5 | Sync buried in the settings scroll | **Confirmed** — same `ScrollViewer` from `:1432`, sync section at `:1699` |
| 6 | Failure detail not available | **Confirmed as a UI gap, not a data gap** — `QueuedSyncAction` rows with per-action `LastError` are persisted and queryable (`SyncStateStore.cs:690`); `SyncPairViewModel.cs:277` keeps `.Count`. The conflicts path already does this right |
| 7 | Two chip rows duplicate each other | **Confirmed as a rendering defect, not a duplication** — `AccountSyncToggleViewModel` (scheduler on/off) vs `ProviderFilterViewModel` (list filter); genuinely different, identically styled |
| 8 | Conexión tabs show no auth state | **Confirmed** — `Is*Active` are selection-only (`:275-283`); auth state exists on `ProviderDescriptor` but is bound only in the header |
| 9 | Search has no count or clear | **Confirmed** — no such property or command in either ViewModel |
| 10 | Breadcrumbs are two implementations | **Confirmed** — duplicated markup (`:304-322`, `:834-850`), shared item type only |
| 11 | Recursive size scan can't be cancelled | **Wrong** — `CancelDeepScanCommand` (`MainWindowViewModel.cs:421`, `:506`, impl `:1421-1425`), "Cancelar análisis" button (`MainWindow.axaml:1144`), `CancellationTokenSource _deepScanCts` (`:30`) threaded into `ScanAsync` (`:1377`). Dropped from the plan |

All line references are against `feature/google-drive-provider` at commit `416c550`.
