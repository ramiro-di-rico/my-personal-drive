---
name: ui-review
description: Review the app's interface against the UX plans after a visual change — layout, states, empty/error/loading, theming and per-provider affordances, using real screenshots. Use when a change is visual rather than behavioral, or when working through docs/PLAN-UX-ROUND-2.md and INTERFACE_IMPROVEMENT_PLAN.md.
---

# UI review pass

Unit tests prove a ViewModel exposes the right state. They prove nothing about whether the window
reads well. This is the pass for visual work — `smoke-test` covers whether the app *works*, this
covers whether it *looks right*.

## Getting a picture

**Only claim you reviewed the UI if you actually saw it.** No screenshot capture tool is
installed in this environment by default (this is a Wayland session — no `grim`, no `scrot`), so
in practice:

1. Launch with the stub CLI so no account is needed — see the `run-app` skill. The stub gives you
   real listings without a network round-trip, which is what you want for layout work.
2. Ask the user for a screenshot of the specific screen and state, naming both precisely
   ("the sync panel with one paused pair, window narrow"). A vague request comes back as a
   picture of the wrong state.
3. If you cannot get an image, say so plainly and report only what you did verify — build,
   bindings compiled, tests green. Never describe a layout you didn't see.

## What to check, per screen

- **The states, not just the happy one.** Empty, loading, error, and "too many items" are where
  this UI breaks. An empty folder must render as *empty*, not as a bogus listing.
- **Truncation and wrapping.** Long file names, long paths in the breadcrumb, long provider
  account labels. Resize the window narrow — the breadcrumb and the sync chip rows are the usual
  casualties.
- **Alignment and rhythm.** Consistent padding and control heights across panels; icons from
  `Assets/Icons.axaml`, never inline paths duplicated per view.
- **Per-provider truthfulness.** With three providers, the UI must not imply a capability a
  provider lacks — check `ProviderCapabilities` for what the current provider actually supports,
  and confirm the auth indicator matches the real signed-in state for *each* tab.
- **Theme.** Light and dark both, since Fluent is themed; hard-coded colors show up immediately.
- **Text.** Every user-visible string is localized and present in every locale — see the
  `add-language` skill. A new English string with no translations is an unfinished change.
- **Console panel.** Still readable, still showing the exact command and live output.

## Against the plan

The visual work is tracked in `docs/PLAN-UX-ROUND-2.md` and
`docs/INTERFACE_IMPROVEMENT_PLAN.md`. Review against the specific item you're implementing, tick
its status block when it's genuinely done, and use the `debt` skill for anything you notice but
aren't fixing — don't widen the diff mid-review.

## Reporting

One line per item checked, with **pass / fail / not verified**, and the screenshot each verdict
came from. Never mark an item you did not look at. If a whole area was skipped, say which and why.

## Checklist

- [ ] Every state seen, not only the populated one
- [ ] Narrow-window pass done; nothing overlaps or clips
- [ ] Both themes checked
- [ ] All three provider tabs checked; no capability implied that isn't there
- [ ] All new strings localized in every locale
- [ ] Plan item ticked, or the gap parked with `debt`
- [ ] Stub CLI and any moved settings folder restored and verified (`run-app`)
