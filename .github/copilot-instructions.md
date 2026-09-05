# Copilot instructions — my-personal-drive

Avalonia UI 12 (.NET 10) desktop front-end for the Proton Drive CLI. The app never calls Proton's
API directly: it launches `proton-drive` processes and parses stdout.

**Start here:** read [`AGENTS.md`](../AGENTS.md) at the repo root — shared project rules,
commands, and layout for every assistant in this repo.

## Task procedures (skills)

Detailed procedures live in `.claude/skills/<name>/SKILL.md`. They are plain Markdown and apply to
you unchanged. Open the matching file before starting; the reusable prompts in `.github/prompts/`
are pointers to these same files.

| Task | Read this first |
|---|---|
| Add/change a `proton-drive` command | [`cli-command`](../.claude/skills/cli-command/SKILL.md) |
| Add a UI feature (MVVM) | [`add-feature`](../.claude/skills/add-feature/SKILL.md) |
| Add a new cloud storage provider | [`add-cloud-provider`](../.claude/skills/add-cloud-provider/SKILL.md) |
| Change the local-sync engine | [`sync-change`](../.claude/skills/sync-change/SKILL.md) |
| Provider sign-in / tokens | [`provider-auth`](../.claude/skills/provider-auth/SKILL.md) |
| Capture or debug real CLI output | [`debug-cli`](../.claude/skills/debug-cli/SKILL.md) |
| Add a UI language, or refresh a locale | [`add-language`](../.claude/skills/add-language/SKILL.md) |
| Cut a Linux release | [`release-linux`](../.claude/skills/release-linux/SKILL.md) |
| Add or change CI | [`ci-setup`](../.claude/skills/ci-setup/SKILL.md) |
| Check Native AOT / trim safety | [`aot-check`](../.claude/skills/aot-check/SKILL.md) |
| Bump a dependency or the SDK | [`upgrade-deps`](../.claude/skills/upgrade-deps/SKILL.md) |
| Run the app locally | [`run-app`](../.claude/skills/run-app/SKILL.md) |
| Manual smoke pass | [`smoke-test`](../.claude/skills/smoke-test/SKILL.md) |
| UI review of a visual change | [`ui-review`](../.claude/skills/ui-review/SKILL.md) |
| Accessibility and theming | [`a11y-theming`](../.claude/skills/a11y-theming/SKILL.md) |
| Write/update a plan doc | [`plan-doc`](../.claude/skills/plan-doc/SKILL.md) |
| Park an out-of-scope finding | [`debt`](../.claude/skills/debt/SKILL.md) |
| Create a git commit | [`commit`](../.claude/skills/commit/SKILL.md) |

In VS Code these are also available as prompt files: `/cli-command`, `/add-feature`, and so on.

## Code generation rules

- **CLI calls:** build `IReadOnlyList<string>` arguments for
  `IProtonDriveCliExecutor.ExecuteAsync` (one element per process argument, escaped by
  `ProcessStartInfo.ArgumentList`). Never suggest a concatenated, pre-quoted argument string.
- **Errors:** switch on `CliException.Kind` (`CliErrorKind`). Substring matching on CLI output
  belongs only in `CliErrorClassifier`.
- **Native AOT:** the app project sets `PublishAot=true` / `TrimMode=partial`. Serialized types
  must be registered in `AppJsonContext` and serialized through it — never a reflection-based
  `JsonSerializer.Serialize<T>` overload. Tests run on the JIT host, so green tests do not prove
  AOT safety.
- **MVVM:** hand-rolled `ObservableObject` + `AsyncCommand`. Do not suggest ReactiveUI or
  CommunityToolkit.Mvvm. ViewModels take dependencies via the constructor and never touch
  `Process`, the filesystem, or Avalonia types; code-behind is only for the visual tree.
- **Bindings:** compiled bindings are on by default — every binding needs a resolvable
  `x:DataType`.
- **`AsyncCommand` always gets `onError`** — an escaping `async void` exception kills the process.
- **Time:** `TimeProvider`, not `DateTime.Now` (tests substitute `FakeTimeProvider`).
- **Tests:** xUnit under `tests/MyPersonalDrive.Tests/`, driven by `FakeCliExecutor`. Use real
  captured CLI output as fixtures; never invent JSON shapes. Real-CLI tests use
  `[IntegrationFact]` and are opt-in via `MYPERSONALDRIVE_INTEGRATION=1`.
- **Nullable and implicit usings are enabled** in both projects; C# collection expressions
  (`["a", "b"]`) are the house style.

## Commands

```bash
./scripts/run-tests.sh
dotnet run --project src/MyPersonalDrive
./scripts/publish-linux.sh
```
