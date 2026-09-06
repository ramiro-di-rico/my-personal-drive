# Technical Plan — UX Round 4

> Fourth round of user-experience work. Unlike round 3, which was written from the source because
> this environment cannot take a screenshot, **this one starts from eight real screenshots of the
> running app** — the first pictures anyone has taken of it — plus an adversarial review of rounds
> 1–3 ([Appendix A](#appendix-a--adversarial-review-of-rounds-13)).
>
> That combination is the point. Three defects here were found by looking at the app, three more by
> comparing two view models mechanically, and none of them by the 1138 tests that were passing the
> whole time.
>
> Companions: [PLAN-UX-ROUND-3.md](PLAN-UX-ROUND-3.md), [PLAN-I18N.md](PLAN-I18N.md),
> [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md) (Y2 belongs to the provider seam),
> [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md).
>
> Implementation branch: `feature/ux-round-3` — the same branch, because these are corrections to
> what that branch and its two predecessors shipped, not a new body of work.

## Status

> **Y1, Y5, Y6 and Y7 are implemented; Y2, Y3, Y4 and Y8 are not.** 1138 + 4 tests passing, from
> 1104 at the start of round 3. Three new gates this round, each verified by reintroducing the
> defect it exists for.
>
> Three of the eight items are defects **round 3 introduced**, and two of those were stated as
> working in its own commit messages. That is recorded here rather than quietly fixed, because the
> pattern it shows — a claim written at the same time as the code, with nothing in between to check
> it — is the most useful finding in this document.

- [x] **Y1 — Opening a file did nothing.** Round 3's X2 made the double click the open gesture and
      said it "opens the folder or previews the file". It returned early for anything that was not a
      folder, so double-clicking a text file, an image or a PDF only selected it. Fixed, and the
      preview rule moved into `Services/PreviewPolicy` because two callers now need it —
      see [§1](#1-y1--opening-a-file-did-nothing).
- [ ] **Y2 — The quota gauge asserts a total nobody measured.** `— / 500 GB` for Proton, `1 TB` for
      OneDrive, `15 GB` for Google Drive: hardcoded constants, rendered as though they were the
      account's. Round 2's U3 fixed exactly this defect on the *used* half of the same string —
      see [§2](#2-y2--the-quota-gauge-asserts-a-total-nobody-measured).
- [ ] **Y3 — The recovery button is offered for every warning.** `HasStatusAction => _isWarning`,
      so a refusal the user cannot act on ("not moving X over the existing file") gets a Retry
      button. U1's own plan said the action appears "when `IsWarning` is set by a failure that has a
      known remedy" — see [§3](#3-y3--the-recovery-button-is-offered-for-every-warning).
- [ ] **Y4 — The keyboard map is still unverified end to end.** X5 shipped nine gestures and the
      commit says they were verified by build and launch, which is not verification. The headless
      harness cannot drive them either: `Focus()` returns false with no activated window —
      see [§4](#4-y4--the-keyboard-map-is-still-unverified-end-to-end).
- [x] **Y5 — A long name widened its own row.** The list-mode name column had no `TextTrimming`,
      unlike the tiles and the local pane. Visible in the screenshots as the `Lumo_generated_…` rows
      reaching further right than every other file — see [§5](#5-y5--a-long-name-widened-its-own-row).
- [x] **Y6 — Dragging the console rewrote settings.json on every pointer move.**
      `AppSettingsService.Update` reads the file and writes it back, and X7 called it from the
      height setter. One drag was on the order of a hundred read-modify-write cycles. Fixed for the
      console; **the viewer's zoom slider still does it** and is the older half of the same finding —
      see [§6](#6-y6--continuous-controls-rewrote-settingsjson-on-every-tick).
- [x] **Y7 — Six labels never followed the language picker.** Measured, not read: build the window
      in English and switch it, build another in Spanish, compare every string property. Four fixed;
      `CliVersion` and `CliUpdateStatus` need a `LocalizedText` each and are allowlisted with a
      reason — see [§7](#7-y7--six-labels-never-followed-the-language-picker).
- [ ] **Y8 — The properties dialog's buttons sit where the layout put them, not where they belong.**
      Cosmetic, from screenshot 8 — see [§8](#8-y8--the-properties-dialogs-buttons).

---

## 0. Executive summary

Round 3 asked whether the app tells the user the truth. This round asks a narrower question, forced
by having pictures for the first time: **does it do what it says it does?**

Three answers, in descending order of how badly they read:

1. **A gesture that was documented, tested and did nothing.** X2's whole subject was the open
   gesture. Its commit message says `ActivateCommand` "opens the folder or previews the file". The
   code returned early for files. There was even a test — `ActivatingAFile_SelectsIt_AndNavigatesNowhere`
   — asserting precisely the broken behaviour, which is how a gap gets recorded as a decision.
2. **Numbers the app never measured, presented as fact.** The quota total is a per-provider
   constant. Round 2 established that "unknown" and "empty" must not render identically, and fixed
   the used half; the total half has been asserting 500 GB the whole time.
3. **Six labels frozen in the language they were born in.** The same defect as X8's chips, four
   rounds running. Found this time by comparing two view models rather than by reading code, which
   took a minute and named all six with both values.

Cutting across them: **every one of these was defended by a claim rather than by a check.** X2's
claim was in a commit message. U3's was in a status block. The i18n round's was "confirmed working
in the running app", which was true of the mechanism and false of six properties. The gates added
in round 3 are lists of property names, and a list is a claim about what you remembered.

So the through-line of this round is not a UI theme. It is: *replace claims with comparisons.*
Y1 and Y5 came from a screenshot. Y6 and Y7 came from tests that compare two states rather than
assert a remembered value. The three new gates all work that way, and each was checked by putting
its defect back.

### Ordering

1. **Y2 and Y3** (§2, §3) are what is left of "the app says things it cannot back", which is round
   2's own subject. They are the ones a user can be actively misled by, so they lead.
2. **Y4** (§4) is a verification gap, not a defect — but it is nine gestures wide, and until
   somebody presses the keys it is nine claims.
3. **Y6's remaining half and Y7's remaining half** (§6, §7) are one change each, both in code this
   round already touched.
4. **Y8** (§8) is cosmetic and can be dropped without stranding anything.

### Out of scope

- **Real quota APIs.** Y2 is about not asserting a number; asking each provider for the true figure
  is provider work and belongs to [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md), exactly as
  round 2 said when it deferred the same thing.
- **The test flake** ([PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md) B3.1). Seen three times in about
  twenty runs, never captured by name.
- **Per-item sync badges**, still wanted since round 1, still not this round.

---

## 1. Y1 — Opening a file did nothing

**Observed.** Double-clicking a `.txt`, `.png` or `.pdf` in the remote pane selects it and nothing
else. The viewer does not open. Enter on a focused row behaves the same, since both go through
`ActivateCommand`.

**Source.** `MainWindowViewModel.HandleRowClickAsync` selected the row and then
`if (!item.IsFolder) return;`. Before round 3 this was invisible: a single click both selected and
opened, and preview was a row button and a context-menu entry. X2 moved the open gesture to the
double click without moving the behaviour, and wrote that it had.

**Done.** The early return is now a preview when the item has one. The rule that decides that —
including the Google-native-document exclusion, which exists because the preview button once
appeared for a file with no bytes to read (PLAN-CLOUD-PROVIDERS.md §8.4/G4) — moved out of
`DriveNodeViewModel`'s constructor into `Services/PreviewPolicy`, because it now has two callers and
a duplicated clause is a clause that drifts.

`ActivatingAFile_SelectsIt_AndNavigatesNowhere` was replaced by two tests that say what should
happen: a previewable file opens the viewer, a file with no preview just selects.

**Not done.** The local pane's own double click still only opens folders. Opening a local file would
mean handing it to the desktop, which is a new capability and a new decision, not a fix.

## 2. Y2 — The quota gauge asserts a total nobody measured

**Observed.** The header reads `— / 500 GB` on Proton and `— / 1,0 TB` on OneDrive. The em dash is
round 2's U3 working: usage is unknown and says so. The number after the slash is not.

**Source.** `MainWindowViewModel.UpdateQuotaMetrics` (`:3726-3733`):

```csharp
_quotaTotalBytes = _provider.Id switch
{
    ProviderId.OneDrive => 1024L * 1024 * 1024 * 1024,   // 1 TB
    ProviderId.GoogleDrive => 15L * 1024 * 1024 * 1024,  // 15 GB
    ProviderId.Nextcloud => 100L * 1024 * 1024 * 1024,   // 100 GB
    ProviderId.S3 => 5120L * 1024 * 1024 * 1024,         // 5 TB
    _ => 500L * 1024 * 1024 * 1024                       // 500 GB (Proton)
};
```

These are plan defaults for each service, not the user's account. A Proton free account is 5 GB; a
paid one might be 500 GB or 3 TB. A self-hosted Nextcloud has whatever its owner gave it, and an S3
bucket has no quota at all — 5 TB is an invention with no counterpart in reality.

U3's `QuotaTooltip` does say the total is a per-provider constant, which is honest and is also a
tooltip: the number is in the header at all times, the caveat only when hovered.

**Do.**
1. Stop rendering a total the app does not have. With usage unknown *and* total unmeasured, the
   gauge has nothing to say — hide it, or show only what is known.
2. When a provider can report a real quota, show it and drop the constant for that provider. That is
   provider work; this item is only about not asserting the number meanwhile.
3. Whatever replaces it, the rule from U3 applies unchanged: unknown and zero must not render alike.

**Note.** This is round 2's own finding, half-fixed. Worth stating plainly, because it is the
clearest case in this document of a round declaring a defect closed while half of it stood.

## 3. Y3 — The recovery button is offered for every warning

**Observed.** Any warning raises the alert strip with a Reconnect or Retry button beside it.

**Source.** `HasStatusAction => _isWarning` (`MainWindowViewModel.cs:810`). Every `IsWarning = true`
in the file — there are about twenty — produces an action. Some are failures with a remedy: a
network error, an expired session. Others are refusals: `error.sync.wontoverwrite` ("Not moving 'a'
over the existing file 'b'"), an unsupported preview, a name the provider rejected. Retrying those
runs a refresh that changes nothing.

U1's plan (PLAN-UX-ROUND-3.md's predecessor, PLAN-UX-ROUND-2.md §1) says the slot appears "when
`IsWarning` is set by a failure that has a known remedy", and the implementation reduced that to
"whenever there is a warning". Round 3's X1 then moved this strip to the window, where it is
strictly more prominent — so the gap got louder without getting looked at.

**Do.** Separate "something went wrong" from "and here is what to do". The typed `DriveErrorKind`
already distinguishes them for connection failures; the refusals mostly do not set `_lastErrorKind`
at all, which is the seam. A warning with no remedy should show its sentence and a dismiss, nothing
more.

## 4. Y4 — The keyboard map is still unverified end to end

**Observed.** Nothing — which is the item. X5 added `F5`, `F2`, `Delete`, `Backspace`/`Alt+←`,
`Ctrl+F`, `Ctrl+Shift+N`, `Escape` and `Ctrl+A`, plus focus-on-open, and its commit says: "the app
launched against the real CLI with no crash — but the gestures themselves are unverified by hand".
That is nine claims.

**What was tried.** The headless harness added at the end of round 3 can drive keys
(`window.KeyPress`), and a probe found that `listing.Focus()` returns **false** in headless with no
activated window, and that key events reach the window but the row containers are not realized the
way a layout pass leaves them. So the probe could not distinguish "the app is broken" from "the
harness cannot drive this", and was discarded rather than committed — a probe that cannot fail
honestly is worse than none.

**Do.**
1. Ask for the four gestures that would settle most of it: open the app and press `↓` without
   clicking first (focus-on-open), `F2` on a selected row, `Delete` on a selected folder,
   `Ctrl+F`.
2. If they work, the remaining risk is small. If `↓` does nothing, `ListModeListing.Focus()` fails
   in the real app too and the whole X5 story starts one click later than it says.
3. Two claims in X5's commit message need correcting either way:
   - "Delete inherits the confirmation prompt" is true for folders and multi-selections and **false
     for a single file**, which trashes on one keystroke with no prompt. Trash is recoverable, so
     this may be the right behaviour — but it is not what the message says.
   - "reachable via the context menu, which is `Shift+F10`" was never checked in Avalonia. If that
     gesture is not wired, taking the row actions out of the tab order left them mouse-only, which
     is the opposite of what X5 set out to do.

## 5. Y5 — A long name widened its own row

**Observed.** In the first screenshot the three `Lumo_generated_2026-08-29_…png` rows are visibly
wider than every other file row.

**Source.** The list-mode row's name was `<TextBlock Text="{Binding DisplayName}" />` with no
`TextTrimming`, while both tile modes and the local pane's row use `CharacterEllipsis`. A name wider
than its share pushes the row past the others, and in a narrow window pushes the row's own actions
out of the pane.

**Done.** `TextTrimming="CharacterEllipsis"`, matching the other three listings.

## 6. Y6 — Continuous controls rewrote settings.json on every tick

**Observed.** Not visible; found by reading X7's own code against `AppSettingsService`.

**Source.** `AppSettingsService.Update` is `Load()` — read and deserialize the file — then `Save()` —
serialize and `File.WriteAllText`. X7's `CommandConsoleHeight` setter called it, and the setter runs
once per `PointerMoved` during a drag. A drag across the console was on the order of a hundred
read-modify-write cycles on the user's configuration file.

**Done.** The console persists once, when the drag ends: the view calls `CommitCommandConsoleHeight`
from `OnConsoleResizeFinished`. A test asserts that twenty drag steps leave the file's timestamp
untouched.

**Not done.** `ViewerZoom` (`:1606-1617`) has the same shape and predates round 3 — a slider bound
`TwoWay` that writes the file on every intermediate value. Same fix, one commit, and it needs a
commit point the slider can supply.

## 7. Y7 — Six labels never followed the language picker

**Observed.** Measured. Build the window in English and switch it to Spanish; build a second one in
Spanish from the start; compare every public string property:

| Property | After a switch | From a fresh start |
|---|---|---|
| `ActiveCommand` | `Idle` | `Inactivo` |
| `CommandLogText` | `No CLI command is running.` | `No hay ningún comando…` |
| `ViewerTitle` | `Viewer` | `Visor` |
| `CommandConsoleToggleLabel` | `Hide the CLI activity` | `Ocultar la actividad…` |
| `CliVersion` | `Unknown` | `Desconocida` |
| `CliUpdateStatus` | `Not checked yet.` | `Todavía no se verificó.` |

**Source.** Each is a `string` assigned once from `Loc.T(...)` and stored. `OnAllPropertiesChanged`
tells the binding to re-read a property that returns the same stale string. This is the fourth
appearance of the class: PLAN-I18N §3.1 named it, X8 fixed it for the filter chips, and these six
were underneath.

`ActiveCommand` had a second defect on top: one site set `Loc.T(Console.Idle)` and another set the
literal `"Idle"`, so the console reverted to English after every operation finished. X8's own gate
missed it because `ActiveCommand` is not one of the property names that gate enumerates.

**Done.** The four that are functions of current state are recomputed in `OnLanguageChanged`. The
gate is `LanguageSwitchStalenessTests`, which reflects over every string property and compares the
two view models — no list to keep current, and it cannot be satisfied by a property that happens to
read correctly.

**Not done.** `CliVersion` and `CliUpdateStatus` carry the result of a past operation, so each needs
a `LocalizedText` field re-rendered on a language change, the way `StatusMessage` already works.
That is fifteen assignment sites inside the CLI self-update flow. They are in the gate's allowlist,
named, with this section as the reason.

Related, same fix: `:3963` and `:4021` compare `CliVersion == UnknownCliVersion` — a stored display
string against a freshly rendered one. If the language changed in between, the comparison silently
stops matching and the version check re-runs (or does not) for the wrong reason. Control flow should
not depend on rendered prose.

## 8. Y8 — The properties dialog's buttons

**Observed.** Screenshot 8: in the properties window, `Copiar` sits to the right of the `Ruta:` line
rather than with the path it copies, and `OK` floats near the centre-bottom instead of at a
consistent corner.

**Source.** `ShowPropertiesAsync` builds the dialog imperatively, and the buttons are children of
the same vertical stack as the fields, so they land wherever their row does.

**Do.** Give the dialog the shape the name prompts got in X9: fields, then one right-aligned button
row. Small, isolated, and the last of the code-built dialogs still laid out by accident.

---

## Appendix A — Adversarial review of rounds 1–3

What was checked, and what did not survive it. The items above are the findings that became work;
this is the audit itself, including the claims that held.

### A.1 Claims that did not hold

| Claim | Where | Reality |
|---|---|---|
| "opens the folder or previews the file" | round 3, X2 commit + plan | Files returned early. **Y1** |
| "Delete inherits the confirmation prompt" | round 3, X5 commit | True for folders and multi-selections; a single file trashes with no prompt. **Y4** |
| "reachable via the context menu, which is Shift+F10" | round 3, X5 code comment | Never verified that Avalonia raises the menu on that gesture. **Y4** |
| "the gestures … verified by build and launch" | round 3, X5 commit | A build is not a gesture. **Y4** |
| U3 fixed the quota's honesty | round 2 status | The used half only; the total is still a constant. **Y2** |
| U1 shows an action "when there is a known remedy" | round 2 §1 | `HasStatusAction => _isWarning`, i.e. always. **Y3** |
| "the picker changes the whole interface" | i18n status block | True of the mechanism; six properties stayed English. **Y7** |
| "620 keys … final state" | i18n status block | Four user-visible strings were outside the tables. Corrected during round 3. |
| The row-width fix (`d0c4475`) | round 3 commit | Changed nothing; the two setters measured as no-ops. Corrected in `5a35a4b`. |

### A.2 Claims that held

Checked and confirmed, so the next round does not re-audit them: U2's connection/session split
(screenshot 6 shows `Desconectado · Reconectar` beside a signed-out account ring), U5's sync tab,
U7's labelled chip rows, U9's search count, U10's shared breadcrumb, X1's banner in a view with no
status panel, X3's empty states, X4's names, X6's brushes in both dictionaries, X10's uniform
account row, and X2's gestures in all three view modes — the last confirmed by the user after the
routing fix.

### A.3 Test theater found

- `ActivatingAFile_SelectsIt_AndNavigatesNowhere` asserted the Y1 defect as though it were the
  design. A test that pins a gap reads as a decision to the next person.
- The first draft of `ALocalPaneRow_FillsTheWidthOfItsListing` passed with its own bug present: one
  short row in a non-scrolling listing measures nearly full width, and 40px of slack against the
  outer `ListBox` hid a 53px defect. Fixed before it was committed, and the reason is in the test.
- Every other gate added in round 3 was verified by reintroducing its defect. That habit is the only
  reason the two above were caught.

### A.4 The systemic finding

The three gates round 3 added — hardcoded colours, icon-only names, label literals — are **lists**:
of property names, of attribute names, of allowed values. Each was written from what the author
could remember to enumerate, and each has already been shown to have a blind spot the moment
something outside the list appeared. `ActiveCommand = "Idle"` walked straight past the literal gate.
`BreadcrumbBar.Label` walked past the markup gate for two whole rounds.

The gates that have actually caught things are the ones that **compare two states** instead of
checking a register: the language-staleness test compares two view models, the headless layout tests
compare a measurement against the slot it sits in, and the routed-event test compares a registration
against the event's own metadata. None of them needs to be kept current.

That is worth carrying forward as a rule: when a class of defect keeps recurring, look for the
comparison that makes it structural, not the list that makes it enumerable.

## Appendix B — What a screenshot would still settle

Round 3's Appendix B had five pictures. Four were taken and three items closed; these remain, plus
one the keyboard needs:

1. **Explorer, dark theme, with a standing warning** — X6's warning card and X1's strip, neither
   seen in a failure state yet. Unplug the network and refresh.
2. **The window at roughly 960px** — all of X7. The wrapping rows, the window minimum, and whether
   the trimming added in Y5 lands sensibly.
3. **An empty folder, and a search matching nothing** — X3, still unseen.
4. **The four keyboard gestures in §4** — not a picture, but the same kind of answer: press `↓`
   without clicking, then `F2`, `Delete` and `Ctrl+F`.
