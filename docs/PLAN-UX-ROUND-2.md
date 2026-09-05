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
> Implementation branch: `feature/ux-round-2`, branched from `feature/google-drive-provider`.

## Status

> **Implemented on branch `feature/ux-round-2`, 2026-09-05.** All ten items landed; 1001 tests
> passing (from 974 at the start). Verified by unit tests plus a stub-CLI launch; **not visually
> verified — this environment has no screenshot tool**, so the rendering of every layout change
> below is unconfirmed and wants a human pass.

- [x] **U1 — Recoverable error surface.** The status card carries an action button whose verb comes
      from the typed `DriveErrorKind`: "Reconectar" (`AuthenticateCommand`) on
      `NotAuthenticated`, "Reintentar" (`RefreshCommand`) otherwise (`HasStatusAction`,
      `StatusActionLabel`, `StatusActionCommand`). Root cause found in passing:
      `CliErrorClassifier` did not recognise "invalid access token", so an expired session
      classified as `Unknown` — see [§1](#1-u1--recoverable-error-surface).
- [x] **U2 — Consolidate the three connection/identity indicators.** The account indicator is a
      ring (session) and the connection indicator a filled dot (reachability); the connection badge
      became a `Button` that recovers when actionable; a new `Degraded` state
      (`IsConnectionFailure`) stops the header claiming "En línea" while a connection failure is
      standing. A `NotFound` on one path deliberately does not demote it.
- [x] **U3 — Stop the quota gauge asserting `0 B`.** Usage is tri-state (`_quotaUsedIsKnown` /
      `_quotaUsedIsPartial`): em dash when unknown, `≥ X` when the sum covers only the root's own
      sized files, a percentage only when exact. Progress bar hidden while unknown;
      `QuotaTooltip` says the total is a per-provider constant.
- [x] **U4 — Single UI language: Spanish.** 419 insertions / 419 deletions across the `.axaml`,
      ViewModels and Services, with no modified line lacking a string literal, plus a follow-up
      pass over `MainWindow.axaml.cs` (~75 strings across every code-built dialog) that a scoping
      mistake left out of the first sweep. Exposed a latent bug: `SyncPairViewModel.HasFailures`
      substring-matched `LastError` for failure wording — removed in U6.
- [x] **U5 — Promote sync to a top-level view.** Third tab beside Explorador and Visor; markup
      lifted into `Views/SyncPanelView.axaml`, the first `UserControl` in the codebase. The two
      independent booleans became one `MainView` value. The tab carries a failure badge
      (`SyncPanelViewModel.HasFailingPairs`), which reads `Pairs` and not `VisiblePairs`.
- [x] **U6 — Per-action detail for sync failures.** `RequestFailureReviewAsync` →
      `ShowFailuresAsync`, mirroring the conflict flow, showing path, operation, attempts and the
      provider's own sentence verbatim, with per-action retry/discard alongside the kept
      retry-all. New `SyncStateStore.RetryFailedAsync(pairId, ids)` and `DiscardFailedAsync`.
      Removes U4's string matching in favour of `FailedCount`, with a regression test.
- [x] **U7 — Disambiguate the two chip rows.** Both rows labelled by role
      ("Sincronización automática:" / "Mostrar:") and given different affordances. The toggle label
      stopped baking name, state and action glyph into one string —
      `Label` / `StateText` / `ActionTooltip`.
- [x] **U8 — Per-provider auth state on the Conexión tabs.** New `Is*Authenticated` properties
      feeding the same session ring the header dropdown uses. The active provider reports its live
      flag rather than the persisted one.
- [x] **U9 — Search affordances.** `HasSearchText`, `SearchResultText` and a `ClearSearchCommand`
      in both panes; the count follows the rendered rows so it cannot disagree with the screen.
- [x] **U10 — Deduplicate the breadcrumbs.** One `Views/BreadcrumbBar` `UserControl` with
      `ItemsSource` / `Label` / `Icon`. The scroll-to-current-folder behaviour moved in from
      `MainWindow`'s code-behind, where an `x:Name` binding meant only the remote pane could have
      it — so the local pane gains it.

### Follow-ups this round deliberately did not take

- **No visual verification.** Every layout change (U1's two-row status card, U2's badge button,
  U5's tab and lifted view, U7's two labelled rows, U9's inline count, U10's shared bar) built,
  loaded and ran, but was never *looked at*. Spacing, wrapping and overflow at the real window
  width are unverified.
- **`ClearStaleFailedActionsAsync` and discard.** U6's discard deletes queue rows directly. It
  coexists with the stale-clearing path but the interaction was not exercised together.
- **`AccountSyncToggleViewModel` still duplicates the panel's primary-slot toggle logic**, as its
  own doc comment says. U7 restyled it without addressing that.

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

> **Doc discrepancy — checked, and this document was the one that was wrong.**
> An earlier draft of §8 claimed [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md) §P10 was
> mistaken about `IsGoogleDriveAuthenticated` existing. It is not: the flag lives on
> **`Services/AppSettings.cs:70`**, alongside `IsProviderAuthenticated`/`SetProviderAuthenticated`
> (`:153`, `:164`), and `GoogleDriveAccountLabel` is a real `MainWindowViewModel` property
> (`:301`). The search that produced the claim was scoped to `ViewModels/` and missed the settings
> record. PLAN-CLOUD-PROVIDERS.md needs no correction; this note is kept rather than deleted
> because the mistake is instructive — a scoped grep reported as an absence proof.

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

## 11. Two bugs found by actually looking at the app

Both reported by the repo owner on first launch of the finished branch, and neither was findable
from the source review that produced §§1–10 — the first needs Avalonia's runtime behaviour, the
second needs a real multi-provider install. Recorded because they are the direct argument for the
"no visual verification" caveat in the status block.

### 11.1 The header ComboBox went blank after signing in

**Symptom.** The provider picker rendered empty while the app was demonstrably browsing Proton
Drive. Noticed after navigating to Sincronización, but present from startup.

**Not a view bug.** `AvailableProviders.Count == 5` and `SelectedProviderIndex == 0` throughout;
the view model always knew exactly which provider was active.

**Cause.** `RefreshAvailableProviders` updates the collection in place
(`AvailableProviders[i] = updated`), and element 0 is normally the selected one. Avalonia's
`SelectingItemsControl` clears its selection when the *selected element* is replaced, even in
place. The two-way binding then wrote `-1` back to `SelectedProviderIndex`, whose setter correctly
ignores an out-of-range value — and nothing ever pushed the real index out again, so the control
stayed at `-1` forever.

Three of the four call sites already re-raised `SelectedProviderIndex` afterwards. **The sign-in
path did not**, which is exactly why it only reproduced after authenticating. The raise moved
inside `RefreshAvailableProviders` itself, so no call site can forget it again.

This is the same family as [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md) P10 Appendix A2 #4,
which took four iterations to diagnose. The general shape worth remembering: **a stable collection
updated in place still perturbs a selection control**, and keeping the view model correct is not
the same as keeping the control in sync with it.

### 11.2 Every provider reported its automatic sync as "activada"

**Symptom.** The sync view listed all five providers as `activada` when only Proton Drive had a
session.

**Cause, and why it is not a lie exactly.** `App.axaml.cs` builds an `AccountSyncContext` — with a
`SyncScheduler` — for every provider in the catalog, configured or not, and
`RecoverFromPreviousRunAsync` starts every scheduler whose store has automatic sync enabled (the
default). So all five loops genuinely were running. Each one gates every *cycle* on
`isAuthenticated`, so an unconfigured provider's loop wakes up and does nothing, forever.
`IsRunning` was reporting a true fact about the loop and a false one about the app.

**Fix.** `SyncScheduler.IsAccountAuthenticated` exposes the gate; `AccountSyncToggleViewModel`
derives `IsRelevant` from it; the view binds a filtered `VisibleAccountSyncToggles`, with the
unfiltered `AccountSyncToggles` left as the source of truth every existing caller reads — the same
relationship `VisiblePairs` has to `Pairs`. The whole row hides when no account is signed in.

**Pre-existing, not a regression.** The original screenshots that started this round already showed
`OneDrive: on` beside `Proton Drive: on`. §7 restyled these toggles without questioning what they
were asserting, which is its own lesson: a control can be *legible* and still be wrong.

### 11.3 The picker went blank again, switching provider from the settings view

**Symptom.** Walking the Conexión tabs — OneDrive → Google Drive → Nextcloud → Proton Drive — left
the header picker blank on some steps and populated on others, alternating. §11.1's fix was
necessary and not sufficient: it made the view model re-publish the right index, but did nothing
about the control fighting it.

**Two causes, both real.**

1. **Every switch replaced all five descriptors.** `RefreshAvailableProviders` rewrote each
   element unconditionally, and a provider *switch* changes no descriptor's displayed fields at
   all. Replacing the selected element is exactly what makes Avalonia drop the selection, so the
   refresh was manufacturing the very perturbation §11.1 then had to repair.
   `ProviderDescriptor.Equals` is Id-only by design (P10 Appendix A2), so it cannot answer "did
   anything visible change?" — `IsAuthenticated` and `AccountIdentity` are now compared by hand and
   an unchanged element is left alone.
2. **The write-back re-entered the setter.** `SelectedProviderIndex`'s setter starts a switch
   (`_ = SwitchProviderAndReportErrorsAsync(...)`, fire and forget). The ComboBox writing its own
   transient index back mid-switch therefore began a *second* switch from inside the first — which
   is what produced the alternation. A `_isSwitchingProvider` guard now ignores writes arriving
   during a switch; `SwitchBrowserAccountAsync` became a thin wrapper that sets the flag, delegates
   to `SwitchBrowserAccountCoreAsync`, and in its `finally` clears the flag *before* the final
   index raise, so that last push is the one the control accepts.

**The lesson, stated once for the next person.** This is the fourth distinct fix to this one
ComboBox across two plans (P10 Appendix A2 #4 took four iterations of its own). Every one of them,
including §11.1, treated the view model as the thing to correct. The actual invariant is about the
*collection*: **do not touch an element of a bound collection unless its content changed**, because
a selection control cannot tell a no-op rewrite from a real one.

### 11.4 Filter chips for accounts with no pairs

Previously listed under "still open" and now done, at the owner's request. `RebuildProviderFilters`
skips any account with zero pairs — every provider in the catalog gets a slot whether or not it is
configured, which is how `OneDrive (0)` and `Google Drive (0)` reached the screen. The whole row
also collapses when only one account holds pairs, since `Todos (3) | Proton Drive (3)` offers a
choice between two identical lists; that is the same reasoning as the pre-existing single-account
gate, applied to accounts that *have* pairs rather than accounts that exist.

`RemovingTheFilteredAccountsLastPair_FallsBackToTodosInsteadOfGoingEmpty` changed as a result: the
behaviour it guards (a stale filter must not survive and leave the list empty) is unchanged, but
the chip it asserted on correctly no longer exists.

---

## 12. Where a synced item lives on this machine

Asked for by the repo owner after using the finished branch: the properties dialog for a remote
item that is part of a sync pair should say which local path it maps to, and offer to copy it.

**The data was already there, one lookup away.** Every pair knows both of its roots, and
`PathMapper` already converts between the three path shapes — it is the single place allowed to,
per [PLAN-LOCAL-SYNC.md](PLAN-LOCAL-SYNC.md) §3.2's golden rule. Routing the dialog through it
rather than composing the path locally is what guarantees the dialog cannot disagree with the path
the sync engine actually writes to.

**What was missing was the containment question.** `FindPairByRemotePath` matches a pair *root*
exactly, which is all the row badges ever needed. A file several folders deep is equally synced and
answered `null`. New `FindPairContainingRemotePath` / `FindPairContainingLocalPath` answer
"which pair is this inside", longest root first so a nested pair beats the outer one. The check is
segment-wise rather than a bare `StartsWith`, so `/my-files/Libros2` is not treated as living
inside `/my-files/Libros` — a real pair of folders in the owner's own account.

**Copying.** `PropertyField` gained an `IsCopyable` flag, defaulting to false so every existing
field is untouched. It is set on paths only: they are the sole values here anyone needs elsewhere,
and the only ones too long to retype. The button confirms in place ("Copiado"), because a clipboard
write is otherwise entirely invisible.

**Both panes, not one.** The local pane's dialog gained the mirror field ("Ruta remota"). Only the
remote half was requested, but [§10](#10-u10--deduplicate-the-breadcrumbs) had just finished making
these two panes consistent by construction; shipping half of this would have put that asymmetry
straight back.

**Found in passing.** Writing the end-to-end test surfaced that several existing tests construct
`DriveItem` with `Name` and `Path` swapped — the record is `(Path, Name, IsFolder, Size)`. They
passed anyway (the quota tests only sum sizes; the §9 search test happened to match either field),
but they were asserting against a shape the app never produces. Fixed in the tests this round
touched. Others elsewhere in the suite may still have it; not swept, since a broad rewrite of
untouched tests belongs in its own change.

---

## 13. Is the sync view supposed to ignore the provider dropdown?

Asked by the repo owner after switching the header dropdown to Google Drive while standing on
Sincronización and still seeing three Proton Drive pairs — noting that the *settings* view does
follow that dropdown, since each provider's connection card shows its own auth mechanism.

**Yes, and both halves are correct.** The sync view is deliberately account-agnostic: P7 Phase A
merged every account's pairs into one list so Proton and OneDrive can sync at once and be seen
together, with each row labelled by its own account (`SyncPairViewModel.AccountLabel`). The header
dropdown selects **which account the Explorer browses**, not a global context. Settings differs
because its Conexión section genuinely is per-provider. Different questions, different answers.

**But the dropdown is not as inert here as it looks, and that exposed a real bug.**
`SwitchBrowserAccountAsync` calls `SyncPanel.SetActiveAccount`, and `AddPairAsync` creates the pair
on `ActiveSlot` — so switching the dropdown silently changes which account "Agregar par" targets.
Meanwhile the panel's prompt read *"Agregá una carpeta para empezar a sincronizarla desde **Proton
Drive**"* while the dropdown said Google Drive, because that sentence was the panel's initial
`StatusMessage`, interpolated once at construction with whichever account was primary at startup.

It was wrong twice over: it named the wrong provider (the one thing on that screen that *had*
changed), and it was still on screen underneath three configured pairs, telling the user to add
their first one.

Now `EmptyStateMessage` derives the name from `ActiveSlot` — the same slot `AddPairAsync` actually
uses, so the sentence cannot promise one provider and create a pair on another — and it renders
only while `HasNoPairs`. `SyncPanelProviderNameTests` moved to the new property; what it guards
(the name is interpolated, not hardcoded) is unchanged.

### Left as a decision, not fixed

**The header dropdown reads as global and is not.** It governs the Explorer, and invisibly the
target account for a new pair, but not the pair list. Nothing on screen says so. Options, none of
them free:

1. Leave it. The per-row account labels already disambiguate, and merging every account's pairs is
   the feature, not a bug.
2. Filter the sync list by the dropdown. Loses the single-pane multi-account view P7 built on
   purpose.
3. Say it explicitly — a line on the sync view naming the account a new pair would target.

Worth noting that [§11.4](#114-filter-chips-for-accounts-with-no-pairs) made this slightly less
visible: with only one account holding pairs the filter chips now collapse, so there is no longer
even a control hinting the list spans accounts. That was the right call for its own reason and it
does cost something here.

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
