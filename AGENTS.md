# Agent guide — my-personal-drive

Avalonia UI 12 (.NET 10) desktop front-end for cloud drive storage, behind a provider seam
(`ICloudDriveProvider`, `Services/Providers/`) — see `docs/PLAN-CLOUD-PROVIDERS.md`. Three
providers exist: **Proton Drive**, via the official CLI (`proton-drive`) — every remote operation
launches a CLI process and parses its stdout, never talking to Proton's API directly — **OneDrive**,
via Microsoft Graph over HTTP (`Services/Providers/OneDrive/`), and **Google Drive**, via the Drive
API v3 over HTTP (`Services/Providers/GoogleDrive/`) — both inherently network-based, no CLI.

On the Proton side, the one exception to "CLI only" is `CliReleaseFeed`, which GETs the published
CLI release manifest (`https://proton.me/download/drive/cli/version.json`) so the app can offer to
update the CLI — a public static file, not the Drive API. Adding a further outbound call on the
Proton side is still an architectural decision, not a detail. See `docs/ARCHITECTURE.md` §5.4/§10.

Read `docs/ARCHITECTURE.md` for the current state, and the `docs/PLAN-*.md` files for planned work
and verified CLI behavior (`PLAN-LOCAL-SYNC.md` Appendix A).

## Skills

Task procedures live in `.claude/skills/<name>/SKILL.md`. **Those files are the single source of
truth** for every assistant working in this repo — Claude Code, OpenCode, Junie and Copilot all
resolve to the same file. Read the matching one *before* starting the task; don't work from the
one-line description.

| Skill | Use it when |
|---|---|
| [`cli-command`](.claude/skills/cli-command/SKILL.md) | Adding or changing a `proton-drive` command: service method, parsing, error kind, tests |
| [`add-feature`](.claude/skills/add-feature/SKILL.md) | Adding UI: view, panel, button, or any user-facing action (MVVM rules) |
| [`add-cloud-provider`](.claude/skills/add-cloud-provider/SKILL.md) | Planning and implementing a new cloud storage backend behind `ICloudDriveProvider` |
| [`sync-change`](.claude/skills/sync-change/SKILL.md) | Touching `Services/Sync/` — reconciler, executor, scanners, queue, echo, crash recovery |
| [`provider-auth`](.claude/skills/provider-auth/SKILL.md) | Sign-in, token refresh, token storage or sign-out for any provider |
| [`debug-cli`](.claude/skills/debug-cli/SKILL.md) | Capturing real CLI output for a fixture or Appendix A, or debugging a CLI call |
| [`add-language`](.claude/skills/add-language/SKILL.md) | Adding a UI language, or filling in a locale that fell behind English |
| [`release-linux`](.claude/skills/release-linux/SKILL.md) | Cutting a Linux release or producing an installable artifact |
| [`ci-setup`](.claude/skills/ci-setup/SKILL.md) | Adding or changing the automated CI checks |
| [`aot-check`](.claude/skills/aot-check/SKILL.md) | After touching serialization, reflection, packages, or bindings; before a release |
| [`upgrade-deps`](.claude/skills/upgrade-deps/SKILL.md) | Bumping a NuGet package, Avalonia, or the SDK — publish-date rule and the deliberate pins |
| [`run-app`](.claude/skills/run-app/SKILL.md) | Running the app for real, with the real CLI or a stub |
| [`smoke-test`](.claude/skills/smoke-test/SKILL.md) | Before a PR or release — the manual pass unit tests can't do |
| [`ui-review`](.claude/skills/ui-review/SKILL.md) | Reviewing a visual change against the UX plans, with real screenshots |
| [`a11y-theming`](.claude/skills/a11y-theming/SKILL.md) | Adding or restyling a control: theme brushes, keyboard reach, accessible names |
| [`plan-doc`](.claude/skills/plan-doc/SKILL.md) | Writing or refreshing a `docs/PLAN-*.md` or `ARCHITECTURE.md` |
| [`debt`](.claude/skills/debt/SKILL.md) | Parking an out-of-scope finding instead of widening the diff |
| [`commit`](.claude/skills/commit/SKILL.md) | Creating a git commit — no AI co-author trailer in this repo |

## Non-negotiables

- **CLI arguments are lists.** `IProtonDriveCliExecutor.ExecuteAsync` takes
  `IReadOnlyList<string>` and passes it to `ProcessStartInfo.ArgumentList`. Never build a
  pre-quoted argument string.
- **Errors are typed.** Callers switch on `DriveException.Kind` (`DriveErrorKind`). Substring
  matching on error messages lives in `CliErrorClassifier` — one place per provider, not
  reintroduced upstream (docs/PLAN-CLOUD-PROVIDERS.md §2.6).
- **The app project is Native AOT.** `PublishAot=true`, `TrimMode=partial`. Serialized types go
  in `AppJsonContext`; no reflection-based `JsonSerializer` overloads. Tests run on the JIT host,
  so passing tests do not prove AOT safety.
- **MVVM is hand-rolled.** `ObservableObject` + `AsyncCommand`, no ReactiveUI or
  CommunityToolkit. ViewModels take dependencies via the constructor and never touch `Process`,
  the filesystem, or Avalonia types. Code-behind only for things needing the visual tree.
- **Compiled bindings are on by default.** Every binding needs a resolvable `x:DataType`.
- **`async void` kills the process.** Always pass `onError` to `AsyncCommand`.
- **Use `TimeProvider`**, not `DateTime.Now` — tests substitute `FakeTimeProvider`.
- **Never invent CLI output shapes.** Capture real output or cite `PLAN-LOCAL-SYNC.md`
  Appendix A.

## Commands

```bash
./scripts/run-tests.sh                      # unit tests + headless UI layout tests (xUnit)
MYPERSONALDRIVE_INTEGRATION=1 ./scripts/run-tests.sh   # + real-CLI tests (slow, needs auth)
dotnet run --project src/MyPersonalDrive     # run the app
./scripts/publish-linux.sh                   # package to artifacts/linux-x64/
./scripts/install-linux.sh                   # install to ~/.local/share/MyPersonalDrive
```

## Layout

```
src/MyPersonalDrive/
  Services/           CLI boundary: executor, locator, ProtonDriveService, error classifier, cache
  Services/Providers/ ICloudDriveProvider seam + Proton/ (see docs/PLAN-CLOUD-PROVIDERS.md)
  Services/Sync/      local-sync engine: scanners, reconciler, executor, scheduler, state store
  ViewModels/         MVVM (ObservableObject, AsyncCommand)
  Views/              .axaml + minimal code-behind
  Models/             DTOs and enums
tests/MyPersonalDrive.Tests/
  Fakes/              FakeCliExecutor, FakeTimeProvider
  Integration/        real-CLI tests, opt-in via MYPERSONALDRIVE_INTEGRATION=1
tests/MyPersonalDrive.UiTests/
                      the real MainWindow, laid out headless and measured — the only tests here
                      that can see a layout defect. xUnit v3 (Avalonia.Headless.XUnit is a v3
                      package), which is why it is a separate project
docs/                 ARCHITECTURE.md (current state) + PLAN-*.md (planned work)
```

## Editing the skills

Edit `.claude/skills/<name>/SKILL.md`. The files under `.opencode/skill/`, `.github/prompts/`,
`.junie/guidelines.md` and `.github/copilot-instructions.md` are thin pointers — keep them
pointing, don't duplicate content into them. Adding a skill means adding the canonical file, a
pointer in each of those places, and a row in the table above.
