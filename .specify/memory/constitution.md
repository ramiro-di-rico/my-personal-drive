# my-personal-drive Constitution

The rules a change to this repository must satisfy, restated for Spec Kit's planning commands so a
spec, plan or task list can be checked against them without reading the whole repo.

**This file is not the source.** Every principle below is a restatement of
[`AGENTS.md`](../../AGENTS.md) § Non-negotiables and the `docs/PLAN-*.md` it cites. If the two ever
disagree, `AGENTS.md` wins and this file is stale — fix it here rather than working around it.

## Core Principles

### I. The CLI boundary is typed, and arguments are lists

`IProtonDriveCliExecutor.ExecuteAsync` takes `IReadOnlyList<string>` and hands it to
`ProcessStartInfo.ArgumentList`. No pre-quoted argument string is ever built — a filename with a
quote or a space in it is ordinary, not an edge case.

Errors are typed: callers switch on `DriveException.Kind` (`DriveErrorKind`). Substring matching on
error text lives in `CliErrorClassifier`, one place per provider, and is never reintroduced upstream
(docs/PLAN-CLOUD-PROVIDERS.md §2.6).

### II. Never invent remote behavior

What a provider actually returns is recorded in `docs/PLAN-LOCAL-SYNC.md` Appendix A, captured from
a real account. A plan that depends on behavior not recorded there must capture it first (the
`debug-cli` skill) and add the finding.

This is the principle with the worst failure mode, and it has already been paid for twice: a parser
that accepted only one of two real shapes of `activeRevision` silently produced size-less items and
re-uploaded every synced file forever (Appendix A #1a). Guessing here does not throw — it degrades.

### III. The app is Native AOT

`PublishAot=true`, `TrimMode=partial`. Serialized types go in `AppJsonContext`; no reflection-based
`JsonSerializer` overloads. **Tests run on the JIT host, so a green suite does not prove AOT
safety** — anything touching serialization, reflection or dynamic types owes an `aot-check` pass.

### IV. MVVM is hand-rolled, and the seam is load-bearing

`ObservableObject` + `AsyncCommand`, no ReactiveUI and no CommunityToolkit. ViewModels take their
dependencies through the constructor and never touch `Process`, the filesystem, or Avalonia types;
code-behind holds only what genuinely needs the visual tree. That seam is what makes a ViewModel
testable without a UI thread, so it is not traded away for convenience in one feature.

Compiled bindings are on by default: every binding needs a resolvable `x:DataType`. `async void`
kills the process, so `AsyncCommand` always gets an `onError`. Anything time-based uses
`TimeProvider`, never `DateTime.Now`, because tests substitute `FakeTimeProvider`.

### V. The sync engine is guilty until proven innocent

`Services/Sync/` does not fail loudly — a wrong decision there deletes a file or loops forever.
A change to it identifies the governing row of `docs/PLAN-LOCAL-SYNC.md` §5.2 first, updates that
document in the same change if behavior diverges, and keeps the reconciler pure: maps in, `SyncPlan`
out, no IO and no clock reads.

Nothing is ever permanently deleted (local deletions go to `.mypersonaldrive-trash/`, remote ones
through the provider's trash). Writes are atomic: temp dir, then `File.Move`, then restore the mtime.

## Quality Gates

- `./scripts/run-tests.sh` green — unit plus headless UI layout tests.
- New behavior covered by a test that fails without the change. A test that passes either way is
  not coverage.
- Anything that moves real bytes owes the real-CLI pass
  (`MYPERSONALDRIVE_INTEGRATION=1 ./scripts/run-tests.sh`), or an explicit statement that it was
  not run and why.
- A visual change owes a look at the running app, not only a passing test. If the app could not be
  rendered, say so rather than implying a check happened.
- A user-visible change updates `README.md` § Features and `docs/ARCHITECTURE.md`.

## Development Workflow

Task procedures live in `.claude/skills/<name>/SKILL.md` and are indexed in `AGENTS.md`. They are
the single source of truth for *how* a given kind of change is made, they are plain Markdown, and
they apply to every assistant working here unchanged. Read the matching one before starting;
`AGENTS.md`'s one-line table is an index, not the procedure.

A finding that is real but out of scope is parked in `docs/PLAN-TECH-DEBT.md` (the `debt` skill)
rather than fixed inline — a fix that widens the diff hides the change under review.

Commits in this repository carry no AI co-author or attribution trailer (the `commit` skill).

## Governance

This constitution restates rules that live elsewhere; it does not create new ones and it does not
supersede `AGENTS.md`. Amending it means amending the source first.

A plan that cannot satisfy a principle states which one and why, in the plan, rather than going
quiet about it. Complexity is allowed — unexplained complexity is not.

**Version**: 1.0.0 | **Ratified**: 2026-09-30 | **Last Amended**: 2026-09-30
