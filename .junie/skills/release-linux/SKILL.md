---
name: release-linux
description: Build, verify, and package a Linux release — tests, publish-linux.sh, native-library and tarball verification, docs/version bump. Use when cutting a release or producing an installable artifact.
---

# Cut a Linux release

## 1. Clean tree

```bash
git status --porcelain
```

Refuse to release from a dirty tree. Note the commit being released.

## 2. Tests

```bash
./scripts/run-tests.sh
```

All green, no skips other than the opt-in `[IntegrationFact]` ones. If you have a real
authenticated CLI available, also run:

```bash
MYPERSONALDRIVE_INTEGRATION=1 ./scripts/run-tests.sh
```

These are slow (~3.5s per CLI process) and create and trash real remote folders. Skipping them
is acceptable; **silently** skipping them is not — say so in the release notes.

## 3. Publish

```bash
./scripts/publish-linux.sh
```

Outputs land in `artifacts/linux-x64/` (`publish/`, `package/`, and the tarball).

## 4. Verify the package — this is the part that has broken before

Commit `b7982ef` fixed a release that shipped without SkiaSharp's native libraries and would not
start on a clean machine. Check every item:

```bash
ls -la artifacts/linux-x64/package/
```

- [ ] `MyPersonalDrive` binary present and executable
- [ ] `libSkiaSharp.so` present (and any other `*.so` the publish produced) — `publish-linux.sh`
      copies `*.so` with `|| true`, so a missing native library **fails silently**
- [ ] `MyPersonalDrive.png` and `README.md` present
- [ ] Tarball `mypersonaldrive-linux-x64.tar.gz` created, and its listing matches `package/`

Then prove it actually runs, ideally from an extracted copy of the tarball rather than the build
tree, so a missing dependency surfaces:

```bash
tar -tzf artifacts/linux-x64/mypersonaldrive-linux-x64.tar.gz
```

Launch the extracted binary and confirm the window opens (`run-app` skill), then run the
`smoke-test` skill against it.

## 5. Install path (optional)

```bash
./scripts/install-linux.sh
```

Installs to `~/.local/share/MyPersonalDrive` with the desktop entry from `deploy/linux/`. Verify
the entry appears in the launcher and the icon resolves.

## 6. Docs and tag

- Update `README.md` if features or requirements changed.
- Update the status checkboxes in `docs/PLAN-TECH-DEBT.md` / `docs/PLAN-LOCAL-SYNC.md` for
  anything this release completes.
- Update the commit reference in the `docs/ARCHITECTURE.md` header.
- Write release notes: what changed, what was verified, what was **not** verified (integration
  tests, other runtimes).

Tagging and pushing are the user's call — propose the tag, don't create it unasked.

## Notes

- `publish-linux.sh` takes an optional runtime argument (`./scripts/publish-linux.sh linux-arm64`).
  Only claim a runtime works if you actually built and ran it.
- The project sets both `PublishAot` and `PublishSingleFile`. If the publish emits trimming or
  AOT warnings, stop and run the `aot-check` skill — a warning here means a runtime failure on a
  code path the build couldn't see.
