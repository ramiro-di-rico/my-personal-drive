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
real CLI path in there):

```bash
mv ~/.config/MyPersonalDrive ~/.config/MyPersonalDrive.bak
```

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
moved aside.
