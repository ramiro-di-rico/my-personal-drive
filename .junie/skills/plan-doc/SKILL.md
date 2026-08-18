---
name: plan-doc
description: Create or update a docs/PLAN-*.md technical plan, or refresh ARCHITECTURE.md, in this repo's established format. Use when designing work before implementing it, or when a completed change makes an existing plan stale.
---

# Write or update a plan document

`docs/` holds three kinds of file, and they are not interchangeable:

- **`ARCHITECTURE.md`** — what the code *is* right now, on `main`. Present tense. Its header
  names the branch and commit it describes; update that when you touch it.
- **`PLAN-*.md`** — what *should* happen, why, and in what order. Survives across sessions.
- Both are written to give a future session full context without re-reading the code. Write for
  that reader.

## Format of a plan (follow `PLAN-TECH-DEBT.md` / `PLAN-LOCAL-SYNC.md`)

```markdown
# Technical Plan — <Title>

> One-paragraph goal. Links to the companion docs. Implementation branch, if any.

## Status

- [x] **<ID> — <name>.** What was actually done, naming the concrete types and files.
- [ ] **<ID>** — not started / held, and what it's blocked on.

---

## 0. Executive summary

...

## 1..N. <Phase or batch>

...

## Appendix A — <investigation findings>
```

Rules the existing docs follow — keep them:

- **Stable IDs.** `B2.1`, `F0.5`, `§9`. Other documents and code comments cite them
  (`CliErrorClassifier` cites `PLAN-LOCAL-SYNC.md` Phase 0 #10). Never renumber an existing ID.
- **Status checkboxes at the top**, updated as work lands. A plan whose status block lies is
  worse than no plan.
- **Cross-links** between documents, with the section anchor.
- **Record findings, not guesses.** Appendix A of `PLAN-LOCAL-SYNC.md` is verified CLI behavior
  from a real account. Mark anything unverified as such, explicitly.
- **Explain the ordering.** These plans say *why* batch X precedes Y. That reasoning is the
  most valuable part; a bare task list is not a plan.
- **Say what is explicitly out of scope.**

## Updating an existing plan

1. Read the whole document first. Its structure carries decisions.
2. Tick the status checkbox and describe what actually shipped, naming files and types.
3. If reality diverged from the plan, say so and why — don't rewrite history to match.
4. Update the mapping sections between companion documents when a shared item moves.
5. If the change alters the current state, update `ARCHITECTURE.md` too, including the commit
   reference in its header.

## Anti-patterns

- A new `PLAN-*.md` for something that belongs as a phase in an existing one
- Renumbering IDs, which silently breaks the code comments citing them
- Marking a phase `[x]` when only part of it landed — `PLAN-LOCAL-SYNC.md` F0.5 shows the right
  way: `(partial)`, with an explicit **Not yet done** list
- Speculative CLI behavior stated as fact
