---
name: refactor-judgment
description: Decide whether a piece of existing code deserves a refactor, and which shape it should take, before touching it. Use when a structural problem shows up in code you didn't come to change — duplication, a class that grew, a conditional that keeps branching, a pattern that looks like it wants extracting — and the real choice is fix it, leave it, or park it as debt.
---

# Deciding whether to refactor

The catalogue is not the hard part. Recognising Long Method, Feature Envy or a missing Strategy
is easy and mostly useless on its own: every codebase has dozens of them and almost none are
worth the diff. What's hard is deciding which one costs something *here*, and that decision is
what this file is about.

Default answer: **leave it**. Move off the default only for a reason you can state.

## The gate

Before proposing any refactor, name the concrete pain in one sentence, with a subject and a
consequence:

- "Adding a fourth provider means editing five switch statements that don't know about each
  other" — that's a reason.
- "`SyncReconciler` is 600 lines" — that's a measurement, not a reason.
- "This is fragile / could be cleaner / isn't very SOLID" — not a reason. Drop it.

If the sentence won't come out, the code stays as it is. A refactor with no named pain is taste
wearing an engineering costume.

Three questions that usually produce the sentence:

1. **Does it change?** `git log --oneline -- <file>` and `git log -S'<symbol>'`. Code that has
   been stable for months is not debt, whatever its shape. Churn is where structure pays off.
2. **Does it break things?** Has a bug actually come out of this shape, or is one obviously
   waiting? Check `docs/PLAN-TECH-DEBT.md` — the finding may already be recorded with a failure
   mode attached.
3. **Is the next change blocked on it?** A shape that makes the *current* task awkward is a
   reason. A shape that might make a hypothetical future task awkward is not.

## What actually costs money in this repo

This is a desktop app wrapping external processes and HTTP APIs, with a sync engine that deletes
user files. The smells that matter reflect that, and they are not the ones a metrics tool flags:

- **Temporal coupling** — a call order that isn't enforced by the types. "Scan before you
  reconcile, or you reconcile stale state." In `Services/Sync/` this produces data loss, not
  inconvenience. Highest priority, always worth the fix.
- **Shared mutable state across async paths** — sync runs, the UI polls, the scheduler fires.
  Two writers to one field is a bug that reproduces once a week and never in tests.
- **APIs whose misuse is the natural way to call them** — the reason `ExecuteAsync` takes
  `IReadOnlyList<string>` and not a string. If the wrong call compiles and looks right, fix the
  signature, not the callers.
- **Error handling drifting out of one place** — substring matching on CLI messages belongs in
  `CliErrorClassifier` alone. Every copy upstream is a real defect (`PLAN-CLOUD-PROVIDERS.md`
  §2.6).
- **`async void` and missing `onError`** — this one isn't a smell, it's a crash. Fix on sight,
  don't park.
- **ViewModels reaching past their constructor** — a ViewModel touching `Process`, the
  filesystem or Avalonia types isn't just off-convention, it's untestable. Fix on sight.

Low priority here, whatever the catalogue says: method length, class size, parameter counts,
comment density, "primitive obsession". None of them have produced a defect in this codebase.

## Choosing the shape

Prefer the smallest move that removes the named pain, in this order:

1. **Rename.** Most often the actual fix. A name that lies costs every future reader.
2. **Guard clauses / early return.** Nested conditionals flatten; no new types.
3. **Extract a function, in the same file.** Cheap, local, reversible.
4. **Move code to where its data lives.** Feature envy is usually a misplacement, not a missing
   abstraction.
5. **Introduce a type or a seam.** Only once the duplication is real and stable.

### On design patterns

A pattern answers a question about *variation that already exists*. Introduce one when there are
genuinely two or three live cases, not when you can imagine them. Two concrete cases plus a
guess is one case.

The exception, and it's not negotiable: **when a pattern is the idiom of this repo, use it
without debate.** Hand-rolled MVVM with `ObservableObject` + `AsyncCommand`, `ICloudDriveProvider`
as the provider seam, typed `DriveErrorKind` errors, `TimeProvider` for clocks. Going around the
local idiom to avoid "a pattern you don't need yet" is worse than the pattern. See `AGENTS.md`
§Non-negotiables — that list wins over anything in this file.

Never introduce: a factory with one product, an interface with one implementation and no test
seam, an event for a direct call, or a layer that only forwards.

### On AOT

`PublishAot=true`, `TrimMode=partial`. A refactor that moves toward reflection, dynamic types or
a non-source-generated `JsonSerializer` overload is not a cleanup, it's a regression — and unit
tests run on the JIT host, so they won't catch it. Anything touching serialization or dynamic
types goes through the `aot-check` skill before it's called done.

## Then

- **Refactor and behaviour change never share a commit.** If the diff does both, split it. A
  reviewer can check one or the other, not a blend.
- **No behaviour change means no test change.** If tests had to be rewritten, either it wasn't a
  refactor or the tests were asserting the implementation. Say which.
- Run `./scripts/run-tests.sh`. A refactor with unrun tests is a guess.
- **Worth doing but not here → `debt` skill.** That's the normal outcome for most findings, not
  a failure. Park it with its failure mode.
- **A design worth arguing about before coding → `plan-doc` skill.**

Report what you left alone as well as what you changed. "I noticed X, didn't touch it because
it's stable and the fix would drag in Y" is a useful thing for the dev to hear, and it's how a
finding avoids being rediscovered next month.

## Related

- Anything inside `Services/Sync/` → read `sync-change` **first**; it owns the decision table
- The CLI boundary → `cli-command`, and `debug-cli` before inventing an output shape
- UI structure → `add-feature` for the MVVM rules, `a11y-theming` for controls
- Parking instead of fixing → `debt`
