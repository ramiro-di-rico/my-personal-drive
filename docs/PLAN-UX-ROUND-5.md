# Technical Plan — UX Round 5

> Everything rounds 3 and 4 left open, in one place, so the next session starts from a list rather
> than from a re-audit. Nothing here is new work discovered by a fresh review: every item was found
> in round 4, written down there, and deliberately not done.
>
> Companions: [PLAN-UX-ROUND-4.md](PLAN-UX-ROUND-4.md) (where these were found),
> [PLAN-UX-ROUND-3.md](PLAN-UX-ROUND-3.md), [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md),
> [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md) (W1 belongs to the provider seam).
>
> Branch: not started. Round 4 shipped on `feature/ux-round-3`; this one wants its own.

## Status

> **Nothing here is implemented.** Six items: two are UX defects with a known fix, one is a test
> gap, one is a flake, one is a set of pictures nobody has taken, and one is the rest of a refactor
> that is at a sensible stopping point.

- [ ] **W1 — The quota gauge asserts a total nobody measured.** Round 4's Y2, unchanged.
- [ ] **W2 — The recovery button is offered for every warning.** Round 4's Y3, unchanged, with the
      seam it needs (`StatusSurface.HasRemedy`) already built.
- [ ] **W3 — `Shift+F10` is still an untested claim.** Round 4's Y4 gap.
- [ ] **W4 — The `FakeCliExecutor.Calls` race.** PLAN-TECH-DEBT B3.1, still uncaptured by name.
- [ ] **W5 — Three screenshots that would settle three items.** Round 4's Appendix B.
- [ ] **W6 — What is left of `MainWindowViewModel`.** 3394 lines after Z5; optional, and the case
      for stopping is made below alongside the case for continuing.

---

## 1. W1 — The quota gauge asserts a total nobody measured

**State.** Unchanged since round 4 §2. `MainWindowViewModel.UpdateQuotaMetrics` picks the total from
a `switch` on `ProviderId`: 1 TB for OneDrive, 15 GB for Google Drive, 100 GB for Nextcloud, 5 TB
for S3, 500 GB otherwise. These are marketing plan sizes, not the user's account, and for S3 there
is no such thing as an account quota at all.

**Why it is still open.** It is the only item of the six that needs a decision rather than an edit,
and the decision is a product one: what does the header show for a provider that cannot report a
total? Three answers, all defensible:

1. **Hide the gauge entirely** when the total is unknown. Honest, and loses the used-bytes figure
   for providers that do report usage.
2. **Show usage alone**, no denominator — `1.2 GB used`. Keeps what is known, drops what is not.
   This is the one to prefer: it is the same rule U3 applied to the numerator.
3. **Keep a total but source it.** Real provider work — a quota call per provider, absent from the
   CLI seam today — and it belongs in PLAN-CLOUD-PROVIDERS, not here.

**Do.** Take option 2 now and leave option 3 as provider work. Concretely: `_quotaTotalBytes`
becomes nullable and the `switch` goes; the gauge renders a bar only when a total exists and a plain
used-figure otherwise; `QuotaTooltip` stops explaining a constant that no longer exists.

**Gate.** The rule from U3 — unknown and zero must not render alike — applies to the total now as
well as the used half. A test that builds the view model per provider and asserts no numeric total
is rendered for any of them is three lines and would have caught this in round 2.

---

## 2. W2 — The recovery button is offered for every warning

**State.** Unchanged since round 4 §3: `HasStatusAction => _isWarning`, so all ~20 `IsWarning = true`
sites get a Retry or Reconnect button, including refusals where retrying re-runs a refresh that
changes nothing ("Not moving 'a' over the existing file 'b'", an unsupported preview, a name the
provider rejected).

**Why it is close to done.** Z5 step 0 built the seam while extracting the status surface:
`StatusSurface.HasRemedy(kind)` exists and is already the shape this needs. What is missing is that
most refusal sites never set `ErrorKind` at all, so `HasRemedy` cannot tell a refusal from an
unclassified failure — both arrive as null.

**Do.**
1. Give `DriveErrorKind` an explicit member for "refused, nothing to retry" — the absence of a kind
   must not be what carries the meaning, because the absence is also what an unclassified failure
   looks like.
2. Set it at the refusal sites (the overwrite guard, the preview-unsupported paths, the rejected
   name).
3. Point `HasStatusAction` at `HasRemedy` and let a warning with no remedy show its sentence and a
   dismiss.

**Gate.** Walk every `IsWarning`-raising path in the view model and assert each one either sets a
kind or is on a named allowlist — the shape that worked for `AmbientClockTests` and Z4. A list of
names alone will not hold; see Appendix A.4 of round 4.

---

## 3. W3 — `Shift+F10` is still an untested claim

**State.** Round 3's X5 left a code comment saying the row's actions stay reachable because the
context menu opens on `Shift+F10`. Round 4 verified eight other gestures against the real window and
could not verify this one: after switching view modes the harness cannot find a materialized row to
send the gesture to, so the check reported "row not found" rather than an answer, and it was removed
rather than left as a test that cannot fail.

**Do.** The blocker is materialization, not the gesture. Force the `ItemsControl` to realize its
containers before pressing — lay out, then walk the visual tree for the container of a known item,
and fail the test loudly if it is not there rather than skipping. If Avalonia turns out not to raise
`ContextRequested` on that gesture, the fix is the comment, not the test: say which gesture actually
opens the menu, or bind one.

---

## 4. W4 — The `FakeCliExecutor.Calls` race

**State.** PLAN-TECH-DEBT B3.1. `Calls` is a bare `List<T>` appended from whichever thread ran the
command, and several view-model actions end with a fire-and-forget refresh, so assertions can
enumerate a list another thread is still writing. Seen three times in roughly twenty suite runs;
never captured by name, because the runs were being grepped for their summary line only.

**Do.** Two halves, in this order:
1. **Make it a non-issue.** `Calls` becomes a lock-guarded append with a snapshotting reader (or a
   `ConcurrentQueue` exposed as `ToArray()`). This is small and removes the race whether or not it
   is ever reproduced.
2. **Keep the evidence.** Whatever else changes, stop discarding the failure output: run the suite
   with the full log retained so the next occurrence is captured by name instead of by memory.

**Note.** Half of this item is about how the run was watched, not about the code. That is worth
leaving in writing.

---

## 5. W5 — Three screenshots that would settle three items

This environment cannot take a screenshot, so these need the user. Naming the exact state matters —
a vague request comes back as a picture of the wrong thing.

1. **Explorer, dark theme, with a standing warning.** Unplug the network and refresh. Settles X6's
   warning card and X1's strip, neither of which has been seen in a failure state.
2. **The window at roughly 960px.** Settles all of X7 — the wrapping rows, the window minimum, and
   whether Y5's trimming lands sensibly.
3. **An empty folder, and a search matching nothing.** Settles X3, still unseen.

The headless harness added in round 4 can now measure a layout, so some of what these pictures were
for could instead become a `WindowLayoutTests` case. Where a question is "does this number fit its
slot", prefer the test. Where it is "does this read well", only the picture answers.

---

## 6. W6 — What is left of `MainWindowViewModel`

**State.** 3394 lines, down from 4460, after Z5's four commits. What remains is browsing, listing,
selection, navigation, transfer and settings — plus the ~89 members round 4's table could not
classify.

**The case for stopping here.** The reason the class was hard to split is gone: reporting is a type
now, not a private method, so a future feature can be built outside the class instead of inside it.
The three clusters that were genuinely separate products living in one file have left. What is left
is what a main window's view model is for, and splitting "browsing" from "selection" would produce
two classes that talk to each other constantly.

**The case for continuing.** Transfer (23 members) and settings (28) are as self-contained as the
CLI updater was, and the unclassified 89 are worth a pass on their own — some of that is almost
certainly dead.

**Do, if it is picked up.** Classify the 89 first; do not extract anything until that table exists.
Then one cluster per commit, tests green at each, exactly as Z5 ran. And keep
`LanguageSwitchStalenessTests`' one-level walk into child view models in step with any new child, or
the gate quietly stops covering what it names.

---

## Appendix — The one process finding worth carrying forward

Round 4's Appendix A.4, restated because it kept being confirmed after it was written:

**Gates that compare two states keep catching things. Gates that are lists of names keep being
walked past.** Two view models compared across a language switch found six stale labels; a
measurement compared against its slot found a fix that had changed nothing; a routed-event
registration compared against the event's own metadata found the double click. Meanwhile every
defect in round 4 was in a file covered by a passing test suite of 1138 tests.

Applied to this document: W1 and W2 each name a gate, and both are comparisons rather than lists.
That is deliberate.
