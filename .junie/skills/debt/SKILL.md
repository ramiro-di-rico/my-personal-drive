---
name: debt
description: Park an out-of-scope finding in docs/PLAN-TECH-DEBT.md instead of fixing it inline. Use when you spot a real problem while working on something else and fixing it would widen the change.
---

# Park a technical-debt finding

Fixing an unrelated problem mid-change makes the diff unreviewable and hides the real change.
Park it instead — but park it *well enough to act on later without this conversation*.

## Decide first

Fix it now, don't park, if any of these hold:

- It's in a line you're already editing and the fix is a line or two
- The change you're making is wrong or unsafe without it
- It's a crash or data-loss path (those go straight to the user, not to a document)

Otherwise park it. Do **not** park vague code smells or "this could be nicer" — the document is
for findings with a concrete failure mode.

## How to park it

1. Read the `## Status` block and §-structure of `docs/PLAN-TECH-DEBT.md` first. If the finding
   belongs to an existing batch (B0–B6), add it there as a sub-item (`B4.3`) rather than
   inventing a new top-level ID. Never renumber existing IDs — code comments cite them.

2. Add the entry with, at minimum:

   - **ID and one-line name**
   - **Where** — `file:line`, with the type or method named
   - **What goes wrong** — a concrete failure: inputs, state, and the resulting wrong behavior.
     "This is fragile" is not a finding.
   - **Why it wasn't fixed here** — what it would have dragged in
   - **What it blocks**, if anything, and any dependency on another batch or on
     `PLAN-LOCAL-SYNC.md`

3. Leave the checkbox unchecked (`- [ ]`) and, if it belongs in the top status block, add it
   there as not-started.

4. If the finding contradicts something `docs/ARCHITECTURE.md` §9 claims, fix that section too.

5. Consider a code pointer at the site — but only when it genuinely helps the next reader:

   ```csharp
   // See docs/PLAN-TECH-DEBT.md B4.3.
   ```

   Do not scatter bare `TODO`s; `done-checker`-style scans treat them as unfinished work.

## Then

Tell the user what you parked and why, in one or two sentences, as part of reporting the actual
change. A parked finding they never hear about is a finding that's lost.

## Related

- Designing the fix rather than just recording it → `plan-doc` skill
- Anything touching the CLI boundary → `cli-command` skill
