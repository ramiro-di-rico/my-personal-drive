---
name: ci-setup
description: Add or change automated CI for this repo — the GitHub Actions workflow that runs the unit tests, the AOT publish and the Linux packaging on every push. Use when setting CI up for the first time, when a check needs adding, or when a CI run fails for a reason a local run does not reproduce.
---

# CI

There is no workflow in `.github/workflows/` today; every gate (`run-tests.sh`, the AOT publish,
the packaging) is run by hand. CI's job here is exactly the automatable part of the
`release-linux` and `aot-check` skills — nothing that needs a human or a real account.

## What can and cannot run unattended

| Gate | Automatable | Why |
|---|---|---|
| `./scripts/run-tests.sh` | yes | pure xUnit, no CLI, no network |
| AOT publish with warnings visible | yes | this is the only gate that catches trim/reflection breakage — tests run on the JIT host and prove nothing about AOT |
| `./scripts/publish-linux.sh` + tarball check | yes | catches a missing native `.so` before a user does |
| `MYPERSONALDRIVE_INTEGRATION=1` tests | **no** | needs a real `proton-drive` binary and an authenticated account |
| `RealOneDriveAuth*` / any OAuth path | **no** | needs an interactive browser sign-in |
| `smoke-test` | **no** | manual by definition |

Never wire a secret or a real account into CI to make the last three run. They stay manual, and
the PR says whether they were done.

## Shape of the workflow

- Trigger on `push` and `pull_request`; `ubuntu-latest`; `actions/setup-dotnet` pinned to the
  `net10.0` SDK the project targets.
- Job 1 `test`: `./scripts/run-tests.sh`.
- Job 2 `aot`: the publish from the `aot-check` skill, with **trim/AOT warnings not suppressed**
  and treated as failures. This job is the whole point — don't let it be the one that gets
  `continue-on-error`.
- Job 3 `package` (on tags or manually): `./scripts/publish-linux.sh`, then the native-library and
  tarball verification from `release-linux`, uploading `artifacts/linux-x64/*.tar.gz`.
- Cache NuGet by `packages.lock.json`/csproj hash. Don't cache `obj/` — a stale AOT `obj/` hides
  exactly the failures job 2 exists to find.
- Pin third-party actions by commit SHA, not by a floating tag.

## Rules

- **CI mirrors the scripts, it does not replace them.** A step that only exists in the YAML can't
  be run locally, so it drifts. If a new gate is worth automating, add it to a script in
  `scripts/` and have CI call the script.
- **Native dependencies.** The app pulls SkiaSharp and SQLitePCLRaw native assets; a runner
  missing a system library fails at publish, not at build. If a job needs `apt-get`, note in the
  YAML *which* package and *why*.
- **A red CI on `main` is a stop-the-line event**, not a thing to work around with a re-run.

## Debugging a failure that doesn't reproduce locally

1. Compare SDK versions — the runner's pinned SDK versus your local `dotnet --version`.
2. Re-run the exact script the job calls, not an approximation of it.
3. AOT-only failures: reproduce with the publish from `aot-check`, never with `dotnet test`.
4. Filesystem-case and POSIX-mode assumptions: the runner is Linux and case-sensitive; tests
   guarded by `PosixFactAttribute`/`CaseInsensitivePathsDecorator` exist for exactly this.

## Checklist

- [ ] Every automated step is a call into `scripts/`, runnable locally
- [ ] AOT/trim warnings surfaced and failing the job
- [ ] No secrets, no real account, no interactive step in CI
- [ ] Actions pinned by SHA; SDK pinned to the project's TFM
- [ ] `obj/` not cached
- [ ] `AGENTS.md` / `README.md` updated if the contributor workflow changed
