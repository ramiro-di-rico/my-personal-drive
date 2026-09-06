# Technical Plan — UX Round 3

> Third round of user-experience work, driven by a source review of the app as it stands on
> `feature/i18n` (2026-09-06), with the app running against the real Proton Drive CLI. Round 1 was
> [INTERFACE_IMPROVEMENT_PLAN.md](INTERFACE_IMPROVEMENT_PLAN.md) (the dual-pane explorer), round 2
> [PLAN-UX-ROUND-2.md](PLAN-UX-ROUND-2.md) (making the interface tell the truth), and
> [PLAN-I18N.md](PLAN-I18N.md) made it translatable and moved the default to English. This round is
> about what those three rounds *left reachable only by mouse, only in one view mode, or only
> inside a panel the user is allowed to turn off*.
>
> Companions: [PLAN-BROWSER-VIEWS.md](PLAN-BROWSER-VIEWS.md) (the icons/gallery modes X2 is about),
> [PLAN-I18N.md](PLAN-I18N.md#11-l9--the-no-literals-lint-gate) (the gate X8 widens),
> [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md), and the
> [`a11y-theming`](../.claude/skills/a11y-theming/SKILL.md) skill, which X4 and X6 exist to satisfy.
>
> Implementation branch: `feature/ux-round-3`, branched from `main` at `bc84166`.

## Status

> **All ten items implemented on branch `feature/ux-round-3`, 2026-09-06**, from `main` at
> `bc84166`. 1130 tests passing, from 1104 — plus three new gates. Every phase's XAML was compiled
> with `--no-incremental`, which the i18n round established as the only way to actually re-run the
> Avalonia XAML compiler.
>
> **Still not visually verified.** Screen capture does not work in this environment and this round
> did not get around it: the session is Wayland with no `grim`/`scrot`/`xwd`/ImageMagick,
> `ffmpeg -f x11grab` against the XWayland window returned a fully black frame, and PIL's X11 grab
> failed outright (`X get_image failed: error 8`). The app was launched against the real CLI and
> did not crash, and the view-model half of every item is pinned by tests — but no layout below has
> been looked at. The five screenshots in [Appendix B](#appendix-b--what-a-screenshot-would-settle)
> are still the ones that would close it, and X7 in particular is the item most likely to change
> after one.
>
> **Order taken, and why it differs from the plan's.** X6 ran first rather than fourth: X1 adds a
> warning banner, and writing it against colour literals that X6 would replace two commits later
> made no sense. Everything after that followed the documented order.

- [x] **X1 — The error surface lives inside an optional, explorer-only panel.** Split by kind:
      `IsStatusBannerVisible` drives a window-level alert strip in its own grid row above all three
      views, `IsInformationalStatus` keeps the panel's card for progress. A dismissal answers one
      message — the next `SetStatus` clears it. Settings and Sync have an error surface for the
      first time. See [§1](#1-x1--the-error-surface-lives-inside-an-optional-explorer-only-panel).
- [x] **X2 — Icons and Gallery are second-class view modes.** Resolved by option (a), decided
      with the user: click selects, double click opens, everywhere. `RowCommand` became
      `SelectCommand` + `ActivateCommand` on both panes' node view models; the pointer handlers stop
      resolving rows by `ListBoxItem` and walk up to whatever is bound to a node, so the same three
      handlers serve list rows and tiles. Both tile scrollers accept drops. `Ctrl+A` landed with X5.
      See [§2](#2-x2--icons-and-gallery-are-second-class-view-modes).
- [x] **X3 — No empty state and no "nothing matched" state.** `IsListingEmpty` /
      `IsListingFilteredToNothing` in both panes, three distinct messages, and a "clear search and
      filters" button on the two situations that have a remedy. Gated on `_hasRenderedListing` so it
      cannot flash before the first paint. See [§3](#3-x3--no-empty-state-and-no-nothing-matched-state).
- [x] **X4 — Icon-only buttons have no accessible name.** 47 buttons plus eight indicators and
      unlabelled fields, each bound to the same `Loc` key as its tooltip. `IconOnlyControlsAreNamedTests`
      parses the markup (buttons nest here) and fails on any icon-only button without a name.
      See [§4](#4-x4--icon-only-buttons-have-no-accessible-name).
- [x] **X5 — The keyboard reaches almost nothing.** One window-level bubble-phase `KeyDown`:
      `F5`, `F2`, `Delete`, `Backspace`/`Alt+←`, `Ctrl+F`, `Ctrl+Shift+N`, `Escape`, and `Ctrl+A`
      for all three modes. The listing takes focus on open; row actions left the tab order, and the
      five actions that were inline-only joined the list-mode context menu.
      See [§5](#5-x5--the-keyboard-reaches-almost-nothing).
- [x] **X6 — Seventeen hard-coded colours.** Eight brushes in both `ThemeDictionaries`, values
      measured rather than picked: every one clears 4.5:1 on its own card background except the
      syncing dot at 4.2, which is an 8px UI element. `NoHardcodedColorsTests` and
      `EveryBrushIsDefinedInBothThemeDictionaries` keep them there.
      See [§6](#6-x6--seventeen-hard-coded-colours).
- [x] **X7 — Fixed widths, no window minimum, and a console that still cannot be resized.**
      `MinWidth` 960 / `MinHeight` 600 (estimated, not measured — see the status note above), the
      sort row and the provider tabs wrap, the settings fields shrink between 200 and 420, and the
      console body has a drag handle with a persisted, clamped height, which closes round 1's
      Task 4. `BarMaxWidth` deliberately untouched: the status panel stayed fixed-width.
      See [§7](#7-x7--fixed-widths-no-window-minimum-and-a-console-that-still-cannot-be-resized).
- [x] **X8 — One Spanish literal survived the i18n sweep.** Two, in the end: the breadcrumb
      `Label` and `LocalExplorerViewModel.SearchResultText`, which built "{n} resultados" by hand and
      slipped past the source gate for having no accent. The markup gate now derives its property
      list from the repository's own `StyledProperty<string>` declarations.
      See [§8](#8-x8--one-spanish-literal-survived-the-i18n-sweep).
- [x] **X9 — The code-built prompts are fixed-height, unfocused and unvalidated.** The three
      collapsed into one `PromptForNameAsync`: sizes to content, wraps, focuses and selects up to the
      last dot, and keeps confirm disabled until the name is non-empty, separator-free and changed.
      See [§9](#9-x9--the-code-built-prompts-are-fixed-height-unfocused-and-unvalidated).
- [x] **X10 — The five provider cards offer the same action in three different shapes.** One
      row on every card: labelled Connect (accented only while signed out), icon-only sign-out and
      refresh, both keeping their tooltips when disabled. The duplicate `Content` assignment is gone
      and the two per-provider connect keys with it.
      See [§10](#10-x10--the-five-provider-cards-offer-the-same-action-in-three-different-shapes).

---

## 0. Executive summary

Rounds 1 and 2 built the interface and then made it stop lying. Reading it now, with English as the
default language and three view modes shipped, the recurring defect is different and can be stated
in one line: **capability in this app is conditional on the mouse, on list mode, and on a panel the
user can switch off — and nothing tells the user what they lost.**

Three families, in descending order of what they cost:

1. **Surfaces that are optional but carry non-optional information.** The status card is the app's
   only general error surface, and it is (a) inside a panel governed by a persisted user preference
   and (b) inside the explorer view's grid, so it does not exist while the user is in Settings or
   Sync. Round 2's U1 gave that card a recovery button; the button inherits both conditions. The
   header badge partially covers the gap, but only for the five error kinds `IsConnectionFailure`
   accepts (`MainWindowViewModel.cs:3552`). A `NotFound`, an `Unknown`, a rejected upload or a bad
   Google client id has no surface at all when the panel is off.
2. **Capability that silently depends on which mode you are in.** `Ctrl/Shift+Click`, drag-and-drop
   and `Ctrl+A` are attached to `ListModeListing`/`LocalListing`. The icons and gallery modes are
   `ItemsRepeater`s with a `Classes.selected` binding and no way to *set* it: a click on a tile runs
   `RowCommand`, which navigates or previews. So the batch action bar and the selected-item details
   are unreachable from two of the three view modes, and the view-mode buttons look like a pure
   presentation switch (which is exactly what `PLAN-BROWSER-VIEWS.md` V1 says they should be).
3. **Reach.** Zero accessible names on ~30 icon-only controls, no `Focus()` call anywhere in the
   codebase, `Escape` does not close the viewer, and there is no `F2`/`Delete`/`F5`/`Backspace`.
   The app is fully usable with a mouse and barely usable without one.

Below those sit four smaller, independent items: hard-coded colours (X6), fixed widths and the
still-unresizable console (X7), the one Spanish string the L9 gate structurally cannot see (X8), and
the code-built prompts (X9/X10).

Worth stating plainly, because it shapes the estimate: `Views/MainWindow.axaml` is still 1855 lines
and the codebase still contains exactly two `UserControl`s (`BreadcrumbBar`, `SyncPanelView`). X1's
"move the status strip out of the panel" and X3's empty state both land in the same overlapping
`Grid` cell as the listing modes and the auth fallback card. Doing them together is one change;
doing them apart is two edits to the same forty lines.

### Ordering

1. **X1 and X3 together** (§1, §3). Same cell, same theme — "the app has something to say and no
   place to say it". X1 is the one item here with a real failure mode (a user who turned the panel
   off gets a silent failure), so it leads.
2. **X2** (§2). The largest behavioural change, and the one most likely to want a design decision
   (click-to-select + double-click-to-open, applied to all three modes, versus per-mode gestures).
   Do it after X1/X3 so the empty state it will also need already exists.
3. **X4 and X5** (§4, §5). Mechanical and broad — a name and a gesture per control. They touch
   nearly every line X1–X3 will have rewritten, so they go after, not before.
4. **X6, X7** (§6, §7). Rendering. X6 is a find-and-replace against new brushes plus two decisions;
   X7 needs a picture at a narrow width before its fixes can be judged.
5. **X8, X9, X10** (§8–§10). Small, independent, safe to drop from the round without stranding
   anything above. X8 is fifteen minutes and closes a real i18n hole.

### Out of scope

- **Decomposing `MainWindow.axaml`.** Same rule as round 2: extract only what an item needs. X1/X3
  may well produce a third `UserControl`; a routing architecture is not this round.
- **Per-item sync-state badges** (round 1 §2.2, round 2's out-of-scope list). Rows show a refresh or
  pause glyph when the row *is* a sync pair root; per-file synced/pending/error badges still need a
  per-path state lookup that does not exist. Still wanted, still not this round.
- **Transfer progress and ETA.** Round 1 asked for MB/s and a progress bar; the CLI reports no
  byte-level progress and `TransferQueueViewModel` says so in its own doc comment. Nothing has
  changed there.
- **A screen-reader pass with an actual screen reader.** X4 adds the names; verifying Orca announces
  them is a separate session on a machine where that can be run.
- **Real quota APIs** — still provider work, still `PLAN-CLOUD-PROVIDERS.md`.

---

## 1. X1 — The error surface lives inside an optional, explorer-only panel

**Observed (from source).** Every general-purpose message the app produces goes to `StatusMessage`,
and every warning to `IsWarning`. Both are rendered in exactly one place: the status card at
`Views/MainWindow.axaml:1114-1156`, inside the `Border` at `:1071`, which carries
`IsVisible="{Binding IsStatusPanelVisible}"` and sits in `Grid.Column="3"` of the explorer grid at
`:383` — a grid whose own `IsVisible` is `{Binding IsExplorerView}`.

Two consequences follow, neither of which requires a bug anywhere:

- **Panel off → most failures are silent.** `IsStatusPanelVisible` is a persisted preference
  (`AppSettings.ShowStatusPanel`, default `true`, toggled from Settings at `:1586`). The live config
  on this machine has it `false`, which is what prompted the check. With it off, the only remaining
  surfaces are the header connection badge — which only reacts to the five kinds
  `IsConnectionFailure` lists: `Network`, `Timeout`, `NotAuthenticated`, `PermissionDenied`, `Busy`
  (`MainWindowViewModel.cs:3552-3557`) — and `LastLogLine` in the collapsed console strip, at
  opacity 0.7 with `TextTrimming`. A `NotFound`, an `Unknown`, a rejected rename, an upload refused
  for a bad name, an `InsufficientStorage`: none of them demote the badge, so none of them appear.
- **Wrong view → all failures are silent.** The card is inside the explorer grid, so a failure
  raised while the user is on the Settings tab (a sign-in that fails for a bad OAuth client id, say)
  or the Sync tab renders nowhere. Sync has its own `StatusMessage` border
  (`SyncPanelView.axaml:211-216`); Settings has nothing.

**Source.** `StatusMessage` `MainWindowViewModel.cs:959`; `IsWarning` `:972`; `HasStatusAction`
`:810`; `StatusActionLabel` `:816`; `UpdateConnectionTelemetry` `:3490-3544`.

**Do.**
1. Separate "the details panel" from "the app is telling you something". The card's two rows
   (message + `StatusActionLabel` button) should render in a slim strip that belongs to the window,
   not to the panel — above the view tabs, or directly above whichever view is active — and be
   dismissible per message rather than hidden by a preference.
2. Keep the panel's copy of the selection/metrics content exactly as it is. This item is about the
   status rows only.
3. Decide what happens to a standing warning when the user switches view. The simplest coherent
   rule: a warning survives the switch and is dismissed by the user or by the next successful
   operation, which is already what `StatusMessage`'s setter does (`:966` resets `IsWarning`).
4. While in there: the Settings view has no status surface of its own, which is the reason a failed
   sign-in reads as nothing happening. The strip from (1) covers it if it is window-level.

**Not confirmed.** Whether the strip reads better above the tabs or inside each view is a layout
call and **needs a picture**. Nothing about the visibility conditions above needs one — they are
`IsVisible` bindings.

## 2. X2 — Icons and Gallery are second-class view modes

**Observed.** `PLAN-BROWSER-VIEWS.md` V1 says the view-mode buttons change presentation only, and
that "the listing and the current selection are untouched". In practice the three modes have
different capabilities:

| Gesture | List | Icons | Gallery |
|---|---|---|---|
| Click to select | yes (row root) | **no** — click runs `RowCommand` | **no** |
| `Ctrl`/`Shift+Click` multi-select | yes | **no** | **no** |
| `Ctrl+A` | yes | **no** | **no** |
| `Enter`/`Space` to open | yes | **no** | **no** |
| Drag out to the other pane | yes | **no** | **no** |
| Context menu | yes | yes | yes |
| Per-row inline actions | yes | context menu only | context menu only |

**Source.** The pointer and key handlers are attached to the two `ListBox`es and nothing else:
`OnCloudRowPointerPressed` / `OnCloudRowPointerMoved` (`MainWindow.axaml.cs:353`, `:380`) are
subscribed in the constructor against the listing `ListBox`; `KeyDown="OnListingKeyDown"` is on
`ListModeListing` (`MainWindow.axaml:586`), and the `DragDrop.*` attached handlers with it (`:587-590`).
The icons and gallery modes are `ItemsRepeater`s (`:698-766`, `:767-852`) whose item template binds
`Classes.selected="{Binding IsSelected}"` — a read of a flag with no gesture that writes it — and
whose tile root is a `Button` bound to `RowCommand`, i.e. navigate-or-preview.

The knock-on effects are the interesting part, because they are not obvious from the tile itself:
the batch action bar (`:514-529`) is bound to `HasMultipleSelected`, and the status panel's
selection block to `IsSingleSelected` (`:1171`). Both are therefore dead in two of three modes,
except for a selection carried over from list mode before switching.

**Do.** Pick one of two shapes and apply it to all three modes:
- **(a) One interaction model.** Single click selects, double click opens, everywhere. This is what
  a file manager does, it makes the tile modes complete, and it costs list mode its current
  single-click-to-open — which is a real behaviour change and needs to be a deliberate decision, not
  a side effect.
- **(b) Lift the gestures.** Keep single-click-to-open and add `Ctrl`/`Shift+Click`, `Ctrl+A` and
  drag to the `ItemsRepeater` items. Cheaper conceptually, more code, and it leaves "how do I select
  without opening" answered only by a modifier key.

Recommendation: **(a)**, with `Enter` as the keyboard open (which X5 wants regardless). Whichever is
picked, `ItemsRepeater` supplies no selection or keyboard model of its own — the existing comment at
`:688-697` already says so (`:688-697`) — so the gestures land in code-behind either way.

**Not confirmed.** That a single click on a tile *feels* like it should select rather than open is a
judgement, not a defect. What is a defect, and does not need a picture, is that the batch bar and
the details block cannot be populated from two of the three modes.

## 3. X3 — No empty state and no "nothing matched" state

**Observed.** There is no `IsEmpty`, `HasItems` or equivalent on `MainWindowViewModel`, and no
markup anywhere in the listing cell for "this folder has nothing in it". An empty folder therefore
renders as an empty `ListBox`: a blank rectangle under the toolbar. The nearest thing to empty-state
copy is `metrics.emptyfolder`, which `FolderMetricsViewModel.cs:240` puts in the metrics *headline*
— inside the status panel, so it is subject to X1's two conditions.

The filtered-to-nothing case is only half-covered: `SearchResultText` shows a count beside the
search box when `HasSearchText` (`:472-477`), and `FilterSummary` shows "N of M" when a kind chip is
active (`:504-509`), but when the two combine to hide every row, the pane itself says nothing and
offers no way back other than finding the two controls that caused it.

**Do.**
1. An empty-state block in the listing cell (same overlapping `Grid` as the three modes and the auth
   card), with three distinct messages: empty folder, no match for the search text, no match for the
   active filters. They are different situations and the third one has an obvious action.
2. Give the filtered case a "clear filters and search" button that resets both. `ClearSearchCommand`
   exists; the kind chips clear by clicking the active chip (`MainWindowViewModel.cs:3703`), which is
   discoverable only if you already know it.
3. The local pane needs the same thing; its listing (`:975-1046`) has no empty state either.

**Not confirmed.** Whether an empty remote folder currently shows anything at all in the pane —
markup says no; **needs a picture** to be certain nothing else fills the space.

## 4. X4 — Icon-only buttons have no accessible name

**Observed.** `grep -rn AutomationProperties src/` returns **zero** hits. `Classes="icon"` appears 64
times in `MainWindow.axaml` and 8 in `SyncPanelView.axaml`, and roughly thirty of those are the
entire visible content of a `Button`: back, new folder, upload, the three view-mode buttons, home,
hidden-files, refresh, the four console buttons, clear-search in both panes, the five per-row
actions, the five sync-pair row actions, sign-in/sign-out/refresh on each of the five provider
cards, and the transfer-queue cancel.

Every one of them has a `ToolTip.Tip`, which is the right thing for a pointer and does nothing for
an automation client: Avalonia derives a control's automation name from `AutomationProperties.Name`,
falling back to its content — and the content here is a `Path`, which has no text. So these controls
are announced, at best, as "button".

**Do.**
1. Add `AutomationProperties.Name` to every icon-only control, bound to the same `Loc[...]` key the
   tooltip already uses. No new strings, no new keys — the wording is written already.
2. Extend the L9 gate rather than trusting review: a test that walks the `.axaml` files and fails on
   a `Button` whose only child is a `Path` and which carries no `AutomationProperties.Name`. It is
   the same shape as `NoHardcodedStringsTests` and belongs beside it.
3. While there, the two status dots that carry meaning by colour alone — the header connection
   `Ellipse` (`:220-253`) and the provider session ring (`:158-180`) — need a name too, or the state
   is invisible to anything that is not looking at the pixels. Their tooltips already say it.

**Not confirmed.** Nothing here needs a picture. Whether a real screen reader reads the result
sensibly does need a screen reader, which this round does not have — see Out of scope.

## 5. X5 — The keyboard reaches almost nothing

**Observed.** The complete inventory of keyboard support in the application:

- `Ctrl/Cmd+,` → Settings, `Ctrl/Cmd+~` → console (`MainWindow.axaml:15-20`).
- `Ctrl+A` in each listing, and `Enter`/`Space` to activate a row in the cloud listing
  (`MainWindow.axaml.cs:54-96`).
- `IsDefault`/`IsCancel` on the code-built dialog buttons, so `Enter` and `Escape` work *inside*
  dialogs.

That is all of it. Missing, in rough order of how often a file manager needs them: `F5` refresh,
`F2` rename, `Delete` trash/delete, `Backspace` or `Alt+←` up a level, `Ctrl+F` focus the search
box, `Escape` to close the viewer, `Ctrl+Shift+N` new folder. `RefreshCommand`, `RenameCommand`,
`TrashCommand`, `BackCommand`, `CreateFolderCommand` and `CloseViewerCommand` all exist and are all
mouse-only.

Two related gaps:

- **Nothing is ever focused.** `grep -rn '\.Focus()' src/` returns zero hits. Dialogs open with no
  focused control — the rename prompt included, where the obvious behaviour is to focus the box and
  select the basename. The listing is never focused after a load, so `Ctrl+A` and `Enter` only work
  after the user has clicked something first.
- **Tab order in a listing is unbounded.** Each cloud row contributes up to five focusable action
  buttons (`:643-685`), so tabbing past a 50-item folder is ~250 stops. The row-action style already
  reveals them on `:focus-visible` (`:40`), which is the right instinct, but nothing limits or
  groups the traversal.

**Do.**
1. Window-level `KeyBinding`s for the four that are unambiguous: `F5`, `Ctrl+Shift+N`, `Ctrl+F`,
   `Escape`-closes-viewer (guarded on `IsViewerVisible`, so it does not swallow `Escape` elsewhere).
2. `F2` and `Delete` in the listings' own `KeyDown` handlers, next to the existing `Ctrl+A` — they
   act on the selection, so they belong where the selection is, and `Delete` must go through the
   same `RequestConfirmationAsync` path the buttons use.
3. Focus the listing after a successful load, and focus-and-select the text in the rename/new-folder
   prompts.
4. Decide the tab story for rows: either `IsTabStop="False"` on the row actions (reachable via the
   context menu, which is `Shift+F10`) or a keyboard-visible action affordance per row. Do not leave
   it as it is.

**Not confirmed.** Nothing here needs a picture; every claim is a grep.

## 6. X6 — Seventeen hard-coded colours

**Observed.** `App.axaml:34-49` defines seven brushes per theme and the views then bypass them
seventeen times:

| Colour | Where | Meaning |
|---|---|---|
| `#28A745` / `#6C757D` | `:158-180`, `:1599-1610`, `SyncPanelView:71-76` | session / running · signed-out |
| `#DC3545` | `:216`, `:232`, `:374` | disconnected · failing pair |
| `#FFC107`, `#FD7E14`, `#007ACC` | `:220-253` | rate-limited · degraded · syncing |
| `#FFF3CD` + `#856404` | `:1126`, `:1150`, `:1153` | the warning card's background and text |
| `#F5C451`, `#E06C6C` | `SyncPanelView:152`, `:161` | conflicts · failures, as button foregrounds |

The two that matter are not the status dots — a semantic palette that ignores the theme is defensible
for a 8px dot — but the two text/background pairs:

- The warning card paints `#FFF3CD` (a pale yellow) with `#856404` text **in both themes**. In dark
  mode that is a bright light block dropped into a dark panel; it will read as a rendering bug.
- `Foreground="#F5C451"` on the conflicts button and `#E06C6C` on the failures button are applied
  over whatever the panel background happens to be. On the light theme's `#DCE0EA` card, a
  `#F5C451` label is around 1.9:1 — below any contrast threshold, on the one control that exists to
  say "something needs your attention".

**Do.**
1. Promote all of it into `App.axaml` as named brushes with a light and a dark value:
   `StatusOkBrush`, `StatusOfflineBrush`, `StatusWarningBrush`, `StatusDegradedBrush`,
   `StatusSyncingBrush`, `WarningCardBackgroundBrush`, `WarningCardForegroundBrush`. This is exactly
   what the [`a11y-theming`](../.claude/skills/a11y-theming/SKILL.md) skill asks for, and the
   duplicated `#28A745`/`#6C757D` ring styles in three files collapse into one definition.
2. Check the two attention colours against their actual backgrounds in both themes and pick values
   that clear 4.5:1, or restyle those two buttons as chips with a background rather than coloured
   text.

**Not confirmed.** The exact contrast figures above are computed from the declared brush values, not
sampled from a rendering; the *conclusion* (a fixed light-yellow card in a dark UI) follows from the
markup alone. **Needs a picture** for the dark-mode warning card.

## 7. X7 — Fixed widths, no window minimum, and a console that still cannot be resized

**Observed.** `Window` declares `Width="1280" Height="800"` and **no `MinWidth`/`MinHeight`**
(`:13-14`), so the window can be dragged to any size. Inside it:

- The status panel is `Width="340"` (`:1071`), and `FolderMetricBucketViewModel.BarMaxWidth` is
  hard-coded against the 260px of content that leaves.
- Both search boxes are `Width="200"` (`:461`, `:914`), the console search `Width="160"` (`:1310`),
  the settings text boxes `Width="420"` (five of them), the language and bandwidth controls
  `Width="200"`.
- The explorer's sort row (`:443-511`) is a non-wrapping horizontal `StackPanel` holding a search
  box, a clear button, a result count, a label, four sort buttons and a filter summary. The kind
  chips below it wrap (`WrapPanel`, `:532-556`); this row does not.
- The five provider tabs in Settings (`:1598-1642`) are a non-wrapping horizontal `StackPanel`.
- The console body is a `ScrollViewer Height="140"` (`:1376`) with no resize affordance. Round 1's
  Task 4 asked for "panel height resizing via drag handle"; collapse/expand shipped, the handle did
  not.

**Do.**
1. `MinWidth`/`MinHeight` on the window, set to the width at which the header stops clipping — a
   number that has to come from a measurement, not a guess.
2. `WrapPanel` for the sort row and the provider tabs; `MaxWidth` instead of `Width` on the settings
   text boxes so they can shrink.
3. A `GridSplitter` above the console body, persisting its height alongside `ShowCommandConsole` —
   this closes round 1 Task 4 properly.
4. If the status panel is to stay fixed-width, make `BarMaxWidth` derive from the actual width
   instead of a constant; if it becomes resizable, that is required rather than tidy.

**Not confirmed.** Everything in this item **needs a picture** at a narrow width. The fixed values
are facts; whether they actually clip, and at what width, is not something markup can tell you. This
is the item most likely to shrink after one screenshot.

## 8. X8 — One Spanish literal survived the i18n sweep

**Observed.** `Views/MainWindow.axaml:909`:

```xml
<views:BreadcrumbBar ItemsSource="{Binding BreadcrumbItems}"
                     Label="Este equipo (local)"
                     Icon="{StaticResource IconHome}" />
```

The local pane's breadcrumb heading is a Spanish string literal, in an interface that now defaults to
English and ships Italian. Its cloud counterpart at `:400` binds
`Label="{Binding ActiveProviderDisplayName}"` correctly.

**Why the gate missed it.** `NoHardcodedStringsTests.LocalizableAttribute` matches
`Text|Content|PlaceholderText|Watermark|Header|ToolTip.Tip`
(`tests/.../Localization/NoHardcodedStringsTests.cs:18-20`). `BreadcrumbBar.Label` is a custom
control's own styled property, so no regex in the gate looks at it. This is the exact failure mode
`PLAN-I18N.md` L9 was written to prevent, one level of indirection out of reach — and the reason the
Italian round could widen two gates without anyone noticing this one.

**Do.**
1. Add a key (`local.breadcrumb.label`, matching the existing `local.*` namespace) in all three
   locales and bind it.
2. Widen the gate. Two options, and the second is the durable one:
   - add `Label` to the attribute list — closes this hole, leaves the next custom property open;
   - drive the gate from the control types the repo actually declares: for each `UserControl` in
     `src/`, read its `StyledProperty<string>` declarations and treat every one of them as
     localizable. `BreadcrumbBar.Label` is currently the only such property, so this costs one
     reflection helper and stays correct when the third `UserControl` arrives.
3. Note in `PLAN-I18N.md`'s status block that the sweep was 619/620, not complete — the plan
   currently reads as finished.

**Verified clean while checking this.** All three locale files hold exactly 620 keys; no key has
Spanish text under `en`; the only value identical between `en` and `es` is
`sync.exec.progress` (`{0}/{1}  {2}  {3}`), which is a pure format string and correctly identical.

## 9. X9 — The code-built prompts are fixed-height, unfocused and unvalidated

**Observed.** `PromptForRenameAsync` (`MainWindow.axaml.cs:617-676`), and the near-identical
`PromptForNewFolderNameAsync` (`:677`) and `PromptForCopyNameAsync` (`:737`), each build a `Window`
with `Width = 400, Height = 180` — a fixed height — whose first child is a `TextBlock` carrying
`Loc.F(Dialog.RenamePrompt, currentName)` **with no `TextWrapping`**. A long file name therefore
either overflows a window that cannot grow or pushes the buttons past its bottom edge. `AskAsync`
(`:1668`) and `ShowAlertAsync` (`:1716`) get this right: `SizeToContent.Height` plus
`TextWrapping.Wrap`. The three prompts predate them.

Three smaller defects in the same forty lines:

- **No focus.** The dialog opens with nothing focused; the user must click into the text box before
  typing. Renaming should also pre-select the basename, which is the whole reason a rename dialog
  starts pre-filled.
- **No validation.** `renameButton.Click` assigns `result = textBox.Text` unconditionally, so an
  empty name, an unchanged name, or a name with a path separator all pass to the provider and come
  back as a CLI error. The confirm button should be disabled until the text is a valid, changed
  name.
- **`Width = 80` buttons.** Fine for "Rename"/"Cancel", tight for Italian's "Rinomina"/"Annulla",
  and there is no reason to pin them at all.

**Do.** Give the three prompts `AskAsync`'s shape (`SizeToContent.Height`, wrapping prompt, no fixed
button widths), focus and select the text box on open, and gate the confirm button on a non-empty,
changed, separator-free name.

**Not confirmed.** Whether the current 180px actually clips at a realistic name length **needs a
picture**; the missing focus and the missing validation do not.

## 10. X10 — The five provider cards offer the same action in three different shapes

**Observed.** "Connect this provider" is rendered three different ways across the Settings cards:

- **Proton, OneDrive, Google Drive** — an icon-only key button with `ToolTip.Tip="{Binding
  SignInTooltip}"` (`:1659`, `:1723`, `:1768`).
- **Nextcloud, Custom S3** — a labelled button (`:1801`, `:1828`).
- Those two labelled buttons also set their content **twice**: `Content="{Binding
  Loc[settings.nextcloud.connect]}"` as an attribute *and* an inline `StackPanel` containing the
  icon plus a `TextBlock` bound to the same key. One of the two assignments is dead markup — the
  child content is applied after the attribute, so the `StackPanel` should win and the attribute
  binding is the dead one.

Sign-out is icon-only on all five, but only visible when authenticated on Nextcloud/S3 and always
visible on the other three. So the same three actions appear in a different shape depending on which
tab you are on, in a block whose whole purpose is to look uniform.

**Do.** Pick one shape for all five cards — a labelled primary "Connect" and icon-only
sign-out/refresh is the most defensible, since connect is the one action a new user is looking for —
and delete the duplicate `Content` assignment while doing it.

**Not confirmed.** Which of the two content assignments wins at runtime is an inference from XamlIl's
ordering, not something this round observed. It does not change the fix: one of them is dead either
way. **Needs a picture** only if the button turns out to render its label twice.

---

## Appendix A — claims checked against the source

Things that looked like findings and were not, recorded so the next round does not re-open them:

1. **The locale files are in step.** 620 keys in each of `en.json`, `es.json`, `it.json`; no English
   entry contains Spanish; the single identical `en`/`es` value is a format string. X8 is the only
   localization hole found, and it is in markup, not in the tables.
2. **Destructive operations are confirmed.** Trashing a folder confirms
   (`MainWindowViewModel.cs:2665`), trashing a multi-selection confirms (`:3040`), and local delete —
   the genuinely irreversible one — confirms in all three of its forms (`local.confirm.deleteone`,
   `.deletefolder`, `.deletemany.*`). `AskAsync` makes Cancel the default button. Trashing a single
   file is not confirmed, which is defensible: it is recoverable from the provider's trash.
3. **The recursive scan is cancellable, and says what it costs.** `CancelDeepScanCommand` plus a
   labelled cost hint (`:1215-1227`). Round 2 already corrected an earlier claim to the contrary;
   this round confirms it again.
4. **Drag-and-drop is complete in list mode**, both directions, with pane-level and folder-level drop
   highlighting and a badge naming the operation (`:854-872`, `:1050-1068`). X2 is that it exists
   *only* in list mode, not that it is missing.
5. **The console's `TextWrapping="NoWrap"` is deliberate**, with a captured stack behind it
   (`:1369-1375`). Do not "fix" it.
6. **`ItemsRepeater` for the tile modes is deliberate** — virtualization over a `WrapPanel` that
   would materialize every child (`:690-697`). X2's fix must not undo that by reaching for a
   `ListBox`.
7. **Localized string lengths are not a layout risk in practice.** The largest English→other-locale
   growth among UI keys is in wrapping body copy (`sync.intro`, +56 chars in Italian); no fixed-width
   control's label grows meaningfully. X7's fixed widths are a resize problem, not a translation
   problem.

## Appendix B — what a screenshot would settle

Screen capture failed in this environment; the app itself ran fine against the real CLI. If a human
pass is available, these five pictures close the open questions above, and nothing else needs one:

1. **Explorer, dark theme, with a standing warning** (unplug the network and refresh). Settles X6's
   warning card and confirms X1's strip placement.
2. **Explorer at ~900px wide**, status panel visible. Settles all of X7 — which of the fixed widths
   actually clip, and where the window minimum should sit.
3. **An empty folder, and a search that matches nothing.** Settles X3's "the pane says nothing".
4. **Icons mode with something selected in list mode first, then switched.** Settles whether the
   selection survives the switch and how a selected tile reads.
5. **Settings, scrolled to the Nextcloud card.** Settles X10's duplicate content assignment.

Until those exist, every item in this document is a source-level finding. The ones marked **needs a
picture** are the ones that could still turn out to render acceptably.
