---
name: a11y-theming
description: Keep the UI keyboard-reachable, screen-reader-nameable and correct in both themes — theme brushes instead of hard-coded colors, icon-only buttons that announce themselves, focus order, contrast. Use when adding or restyling any control, icon, or color.
---

# Accessibility and theming

Two problems with one root: a control that only communicates through a glyph and a hard-coded
color communicates nothing to a screen reader and nothing in the other theme.

## Theming

Colors live in `App.axaml`'s `ThemeDictionaries` (`Light` / `Dark`) and are consumed with
`{DynamicResource …}`. The theme itself comes from `AppSettings` via
`App.axaml.cs` → `RequestedThemeVariant` (`light` / `dark` / default = follow the system).

- **Never hard-code a color in a view.** If you need a new one, add the brush to *both* theme
  dictionaries and reference it by key. A single-theme brush is a bug that only shows up for
  users on the other setting.
- `{StaticResource}` freezes the value at load and will not follow a theme switch. Use
  `{DynamicResource}` for anything themed.
- **Icons** come from `Assets/Icons.axaml` and get `Classes="icon"`, which strokes them with
  `IconBrush`. Don't inline a `Path` geometry in a view and don't set `Stroke` by hand — an icon
  with a literal stroke stays black on a dark background.
- **The accent means "the one primary action in this section"** (`Button.primary` → `AccentBrush`).
  Adding a second accented button in the same section is a design change, not a style tweak.
- **Contrast.** Aim for WCAG AA (4.5:1 body text, 3:1 for large text and UI boundaries) against
  the panel/card/console backgrounds in *both* dictionaries. A brush that passes on `#E9EBF2` can
  fail on `#11151D`.
- Never encode state in color alone — a red border needs an icon or text alongside it. This is the
  same rule that makes the sync chip rows readable.

## Accessibility

The current baseline: **84 `ToolTip.Tip` usages and zero `AutomationProperties`**. Tooltips are
mouse-only; they are not an accessible name. So:

- **Every icon-only control needs an accessible name**:

  ```xml
  <Button ToolTip.Tip="{Binding RefreshLabel}"
          AutomationProperties.Name="{Binding RefreshLabel}">
    <Path Classes="icon" Data="{StaticResource IconRefresh}" />
  </Button>
  ```

  Bind both to the same value so they cannot drift. When you touch an existing icon-only button,
  add the missing `AutomationProperties.Name` — that is how the gap closes.
- **Keyboard reachable.** Every action must be reachable by Tab and triggerable by Enter/Space.
  Tab order follows the visual order; fix it by moving the element in the tree, not by scattering
  `TabIndex`. Decorative elements get `IsTabStop="False"` / `Focusable="False"`.
- **Dialogs**: focus lands on the first meaningful control on open, Esc cancels, Enter confirms
  the primary action, and focus returns to the invoking control on close.
- **Labels**: a `TextBox` without a visible label needs `AutomationProperties.Name`. A label
  sitting next to a field should be associated with `AutomationProperties.LabeledBy`.
- **Live state.** Progress, errors and the console update asynchronously; make sure the *text*
  changes, not only a color or a spinner, so the state is readable at all.
- **No fixed sizes for text containers.** Users run larger system fonts; heights in `px` around
  text clip it.

## Verify

`./scripts/run-tests.sh` proves none of this. Run the app (`run-app`) and:

1. Unplug the mouse — literally do the flow with Tab / Shift+Tab / Enter / Esc.
2. Switch theme in Settings both ways, and once with the system setting (default variant).
3. Raise the system font scale one step and re-check the panels you touched.
4. Report per the `ui-review` skill: pass / fail / not verified, never a claim you didn't test.

## Checklist

- [ ] No hard-coded colors; new brushes added to both theme dictionaries; `DynamicResource` used
- [ ] Icons from `Assets/Icons.axaml` with `Classes="icon"`; no manual `Stroke`
- [ ] Accent still marks exactly one primary action per section
- [ ] Contrast checked against light *and* dark backgrounds
- [ ] State never conveyed by color alone
- [ ] Every icon-only control has `AutomationProperties.Name`, bound to the same text as its tooltip
- [ ] Full flow completed with the keyboard only; dialog focus in/out correct
- [ ] Checked at a larger font scale; nothing clipped
