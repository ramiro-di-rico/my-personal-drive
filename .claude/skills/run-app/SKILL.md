---
name: run-app
description: Launch the Avalonia app locally — against the real Proton Drive CLI or a stub CLI so the UI can be exercised without authenticating. Use when asked to run, start, or screenshot the app, or to confirm a change works in the real UI.
---

# Run the app

## Real CLI

```bash
dotnet run --project src/MyPersonalDrive
```

On first launch the app asks for the `proton-drive` executable path, then stores it in
`settings.json`. Settings and cache live in `Environment.SpecialFolder.ApplicationData` —
on Linux `~/.config/MyPersonalDrive/`:

- `settings.json` — CLI path, auth flag, `StrictListingParsing`
- `crash.log` — last-resort handler output from `Program.cs`; read this first when the app dies
- the SQLite cache/state DBs

To reset to a first-launch state, move that folder aside (don't delete it — the user may have a
real CLI path in there). **Before moving it**, check for a leftover backup from a previous
session that never got restored — a stale `~/.config/MyPersonalDrive.bak*` sitting next to a
*live* `settings.json` that points at a stub path (`/tmp/...`) means an earlier restore silently
failed and the user's real config is one directory over, not the one currently live. Restore that
first, and never move a real config over a backup slot that's already occupied — pick a fresh,
timestamped name instead of reusing `.bak`:

```bash
mv ~/.config/MyPersonalDrive ~/.config/MyPersonalDrive.bak-$(date +%s)
```

**After the session, verify the restore actually worked — check content, not just that the file
exists.** A `mv` can partially fail, or a later step in the same command chain can silently not
run, leaving the stub config live with a backup directory sitting unused next to it:

```bash
grep -q '"CliPath": *"/tmp/' ~/.config/MyPersonalDrive/settings.json && echo "STILL STUBBED — restore failed"
```

**If the app is already running when you start**, don't `mv` its config out from under it and
assume the swap takes effect — on Linux the running process keeps writing to the old inode via
its already-open file handle regardless of what the path now points at, so the live app won't
see a restored config (or notice a broken one) until it's restarted. Either ask the user to
close it first, or clearly tell them afterward that a restart is needed for the fix to apply.

## Stub CLI (no Proton account needed)

Preferred for UI work. `ProtonDriveCliLocator` runs whatever path `settings.json` points at, so
point it at a script that prints canned `filesystem list --json` output.

1. Write the stub somewhere temporary (not in the repo):

   ```bash
   cat > /tmp/fake-proton-drive <<'EOF'
   #!/usr/bin/env bash
   # $1=filesystem $2=list ... ; auth commands just succeed
   case "$1 $2" in
     "auth login"|"auth logout") exit 0 ;;
     "filesystem list") cat /tmp/fake-listing.json ;;
     *) exit 0 ;;
   esac
   EOF
   chmod +x /tmp/fake-proton-drive
   ```

2. Put a **real captured** listing in `/tmp/fake-listing.json`. Take it from
   `docs/PLAN-LOCAL-SYNC.md` Appendix A or from the fixtures used in
   `tests/MyPersonalDrive.Tests/Services/ProtonDriveServiceListingTests.cs`. Do not invent field
   names — a stub with the wrong shape tests nothing.

3. Point the app at it: launch, and enter `/tmp/fake-proton-drive` as the CLI path (or set
   `CliPath` in `~/.config/MyPersonalDrive/settings.json`).

Destructive commands (`trash`, `move`, `upload`) become no-ops, which is exactly what you want
while iterating on the UI.

## Reading what happened

- The in-app console panel shows the exact command and its live output — quote from it rather
  than describing it.
- `ListingParseWarning` surfaces there too; a warning means the JSON parser fell back to the
  text heuristic.
- On a crash, check `~/.config/MyPersonalDrive/crash.log`.

## Screenshots

Only claim the UI looks right if you actually saw it. If you can't render a window in this
environment, say so and describe what you verified instead (build succeeded, tests pass) rather
than implying a visual check.

## Cleanup

Remove `/tmp/fake-proton-drive` and `/tmp/fake-listing.json`, and restore any settings folder you
moved aside — then verify the restore per the check above, in the same turn, before reporting the
test as done. Don't just check that `~/.config/MyPersonalDrive/settings.json` exists; a stub
config existing at that path passes an existence check just as well as a real one does.
