# Technical Plan — The compact view

> The app has one size: a 1280x800 window with a 960x600 floor, three view tabs and two file
> panes. There is nothing between that and minimising it, and minimising it is not what the user
> wants — a minimised window says nothing, and this app spends most of its life doing something
> worth glancing at. This plan adds a second shape: the window collapses to a single row that
> stays on top, shows what the app is doing, and offers the way back.
>
> Explicitly **not** a tray icon. A tray applet is a different program with a different lifecycle,
> a different toolkit surface (`StatusNotifierItem`/`AppIndicator`, which GNOME does not ship
> without an extension), and it hides the state instead of showing it. The ask was the opposite of
> hiding.
>
> Companions: [PLAN-SYNC-VISIBILITY.md](PLAN-SYNC-VISIBILITY.md) (the status vocabulary the strip
> reuses), [PLAN-UX-ROUND-2.md](PLAN-UX-ROUND-2.md) §14 (the header as the home of window-level
> toggles), [INTERFACE_IMPROVEMENT_PLAN.md](INTERFACE_IMPROVEMENT_PLAN.md) Task 3 (the precedent
> that a view toggle persists).
>
> Branch: `main`.

## Status

- [x] **C1 — The whole feature, one commit.** `AppSettings.CompactMode` + the geometry it
      remembers, `MainWindowViewModel.IsCompactMode` / `ToggleCompactModeCommand`, the strip and
      the header button in `MainWindow.axaml`, `MainWindow.ApplyCompactMode` for the window
      chrome, `IconCompact`/`IconExpand`, two string keys in all three locales.
      `MainWindowCompactModeTests` (7) and two layout tests in `WindowLayoutTests`.

## 1. What the strip shows, and why it is not the header

The obvious implementation is to keep the existing header and hide what does not fit. It was
rejected. The header is ~1100px of provider picker, connection badge, quota gauge, three theme
buttons, a pane toggle and Configuración; at 280px it is not a compact header, it is a broken one.
More to the point, most of it answers questions nobody asks of a strip — nobody changes theme in a
40px window.

So the strip is its own row, and it earns its pixels with the one thing the user cannot get from a
minimised window:

| Element | Source | Why it is there |
|---|---|---|
| Cloud glyph + provider name | `ActiveProviderDisplayName` | Which account this strip belongs to. Two windows, two accounts, is a real configuration. |
| Status dot + label | `IsOnline`/`IsSyncing`/`IsRateLimited`/`IsDisconnected`/`IsDegraded`, `ConnectionStatus` | The headline. |
| Transfer summary | `TransferQueue.Summary`, shown only when `HasItems` | "2 transfiriendo · 3 en cola" — the thing you keep the strip open to watch. |
| Failure dot | `SyncPanel.HasFailingPairs` | The Sincronización tab carries this normally, and in compact mode that tab is not on screen. |
| Expand button | `ToggleCompactModeCommand` | The way out. |

The status dot and label are the header badge's own bindings, not a second computation.
`UpdateConnectionTelemetry` already folds "syncing", "degraded", "rate limited", "disconnected" and
"loading" into one maintained line and raises for all of it; a strip that recomputed its own answer
would eventually disagree with the badge three pixels away in the full window.

## 2. The split between the view model and the view

`IsCompactMode` is a view-model property, persisted like the other panel toggles. Everything below
the strip — header, view tabs, alert banner, active view — is one `IsVisible="{Binding
!IsCompactMode}"` on the layout root. The strip is therefore **not a fourth view** alongside
Explorador/Previsualización/Sincronización: it is the absence of all of them, which is why it sits
as that root's sibling rather than inside it.

The window itself is the view's half, in `MainWindow.ApplyCompactMode`:

| | Compact | Full |
|---|---|---|
| `MinWidth`/`MinHeight` | 280 / 32 | the XAML values, read once in the constructor |
| `Width` | 420 | the recorded restore width, else the XAML default |
| `Height` | `SizeToContent.Height` | the recorded restore height |
| `Topmost` | `true` | `false` |
| `Position` | the recorded strip position, if any | untouched |

None of those have a view-model equivalent, and a view model that touched them could not be tested
without a UI thread. Two ordering traps live here, both commented at the call site:

- **`MinWidth` first.** The XAML floor is 960. Assigning `Width = 420` before lowering it is
  silently clamped straight back to 960.
- **Height is content-driven.** One row's height depends on padding, font size and DPI. A constant
  clips it on any machine that disagrees.

## 3. What is remembered, and what is deliberately not

Three values, written to `settings.json`:

- `CompactMode` — so the app reopens as a strip if that is how it was left.
- `CompactWindowLeft`/`CompactWindowTop` — where the strip sat. The sentinel for "never placed" is
  `int.MinValue`, not `0`: the top-left corner is a legitimate place to park a strip, and a `0`
  sentinel would move it on every launch.
- `RestoreWindowWidth`/`RestoreWindowHeight` — the size to expand back to.

This is **not** general window-geometry persistence, which the app still does not do. The restore
size is written on the transition into compact mode, not continuously, and a non-finite or
non-positive measurement is dropped rather than stored — Avalonia hands out `NaN` for a window it
has not laid out yet, and storing one would leave the expand button restoring to nothing.

The position is written when the strip is expanded and again on `Closing`, because an app closed
while still compact never takes the first path. It is deliberately not written on `PositionChanged`:
dragging a window raises that continuously, and each one would be a `settings.json` rewrite.

The first apply on launch counts as neither transition (`_compactChromeApplied` starts `null`) —
otherwise starting up would overwrite last session's two values with this session's defaults.

## 4. Testing

Split along the same seam:

- `MainWindowCompactModeTests` (view model): the toggle, the round trip through `settings.json`,
  the null-means-use-your-own-defaults contract, the `NaN`/zero guard, and the 0,0 corner that
  pins the sentinel choice.
- `WindowLayoutTests` (headless Avalonia): that the tabs are actually gone in compact mode, and
  that the expand button is fully on screen at the 280px floor — a clipped one would strand the
  window as a strip with no way out.

The second test squeezes the strip `Border` directly rather than the window. Compact mode turns on
`SizeToContent`, and `UpdateLayout` then re-arranges the window from its own `Width`, so the
project's usual `Layout(window, 280)` is undone before it can be read and everything measures
1280 wide.

## 5. Open

- **Not verified by hand.** The build and 1316 tests pass, and the headless tests measure the
  layout, but no one has looked at the strip on a real screen. Contrast, whether 420px is the
  right default width, and whether `Topmost` behaves under this compositor are all still open.
- **Multi-window.** One window, one strip. Nothing here considers two accounts open at once.
