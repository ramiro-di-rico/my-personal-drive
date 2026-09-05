# Junie guidelines — my-personal-drive

Avalonia UI 12 (.NET 10) desktop front-end for the Proton Drive CLI. The app never calls Proton's
API directly: it launches `proton-drive` processes and parses stdout.

**Start here:** read [`AGENTS.md`](../AGENTS.md) at the repo root. It holds the project rules,
commands, and layout, and it is shared by every assistant working in this repo.

## Task procedures (skills)

Before starting a task, open the matching file under `.claude/skills/` and follow it. These are
plain Markdown procedures, not Claude-specific — they apply to you unchanged.

| Task | Read this first |
|---|---|
| Add/change a `proton-drive` command | [`.claude/skills/cli-command/SKILL.md`](../.claude/skills/cli-command/SKILL.md) |
| Add a UI feature (MVVM) | [`.claude/skills/add-feature/SKILL.md`](../.claude/skills/add-feature/SKILL.md) |
| Add a new cloud storage provider | [`.claude/skills/add-cloud-provider/SKILL.md`](../.claude/skills/add-cloud-provider/SKILL.md) |
| Change the local-sync engine | [`.claude/skills/sync-change/SKILL.md`](../.claude/skills/sync-change/SKILL.md) |
| Provider sign-in / tokens | [`.claude/skills/provider-auth/SKILL.md`](../.claude/skills/provider-auth/SKILL.md) |
| Capture or debug real CLI output | [`.claude/skills/debug-cli/SKILL.md`](../.claude/skills/debug-cli/SKILL.md) |
| Add a UI language, or refresh a locale | [`.claude/skills/add-language/SKILL.md`](../.claude/skills/add-language/SKILL.md) |
| Cut a Linux release | [`.claude/skills/release-linux/SKILL.md`](../.claude/skills/release-linux/SKILL.md) |
| Add or change CI | [`.claude/skills/ci-setup/SKILL.md`](../.claude/skills/ci-setup/SKILL.md) |
| Check Native AOT / trim safety | [`.claude/skills/aot-check/SKILL.md`](../.claude/skills/aot-check/SKILL.md) |
| Run the app locally | [`.claude/skills/run-app/SKILL.md`](../.claude/skills/run-app/SKILL.md) |
| Manual smoke pass | [`.claude/skills/smoke-test/SKILL.md`](../.claude/skills/smoke-test/SKILL.md) |
| UI review of a visual change | [`.claude/skills/ui-review/SKILL.md`](../.claude/skills/ui-review/SKILL.md) |
| Write/update a plan doc | [`.claude/skills/plan-doc/SKILL.md`](../.claude/skills/plan-doc/SKILL.md) |
| Park an out-of-scope finding | [`.claude/skills/debt/SKILL.md`](../.claude/skills/debt/SKILL.md) |
| Create a git commit | [`.claude/skills/commit/SKILL.md`](../.claude/skills/commit/SKILL.md) |

## Hard rules

- CLI arguments are `IReadOnlyList<string>` passed to `ProcessStartInfo.ArgumentList` — never a
  pre-quoted string.
- Callers switch on `CliException.Kind`; message substring matching belongs only in
  `CliErrorClassifier`.
- The app project publishes with Native AOT: serialized types must be registered in
  `AppJsonContext`; no reflection-based `JsonSerializer` overloads. Tests run on the JIT host and
  prove nothing about AOT.
- Hand-rolled MVVM (`ObservableObject`, `AsyncCommand`) — do not add ReactiveUI or
  CommunityToolkit. No CLI, filesystem, or Avalonia types in ViewModels; no logic in code-behind.
- Compiled bindings are on by default: every binding needs a resolvable `x:DataType`.
- `AsyncCommand` always gets an `onError` callback — an escaping `async void` exception kills the
  process.
- Use `TimeProvider`, not `DateTime.Now`.
- Never invent CLI output shapes. Capture real output, or cite `docs/PLAN-LOCAL-SYNC.md`
  Appendix A.

## Verify before reporting done

```bash
./scripts/run-tests.sh
```

Plus the relevant skill's checklist. Report skipped steps explicitly instead of omitting them.
