---
name: smoke-test
description: Manual post-change smoke checklist for the app — auth, browse, download, upload, trash, sync pair, console. Use before a PR or release, after any change that touches the UI or the CLI boundary.
---

# Smoke test

Unit tests cover parsing and command construction. They do not cover bindings, layout, the
Avalonia dispatcher, or the real CLI. This is the pass that does.

Run against a real authenticated CLI when possible. With the stub CLI (`run-app` skill) only the
read-only rows are meaningful — mark the rest as not verified.

Record the result of each row as **pass / fail / not verified**. Never mark a row you did not
perform.

## Baseline

| # | Step | Expected |
|---|---|---|
| 1 | Launch (`dotnet run --project src/MyPersonalDrive`) | Window opens, no `crash.log` entry |
| 2 | First launch with settings moved aside | Prompts for the CLI path, accepts it, persists it |
| 3 | Authenticate | `auth login` shown in the console, no timeout (it runs with an infinite timeout) |
| 4 | Post-auth | `/my-files` loads automatically |

## Browsing

| # | Step | Expected |
|---|---|---|
| 5 | Open a folder | Children listed, breadcrumb grows |
| 6 | Open an **empty** folder | Renders as empty — *not* as a bogus listing (the B2.1 regression) |
| 7 | Back / breadcrumb | Returns to parent, never navigates above `/my-files` |
| 8 | Select a file | Metadata pane shows size and modified date |
| 9 | Console panel | Shows the exact command and live output; no `ListingParseWarning` |

## File operations

| # | Step | Expected |
|---|---|---|
| 10 | Download a file | Lands in the chosen folder, correct size |
| 11 | Upload a file | Appears in the current folder after refresh |
| 12 | Upload a name that already exists | Conflict strategy honored (keep-both / replace / skip) |
| 13 | Create folder | Appears; creating it twice surfaces `AlreadyExists`, not a raw error |
| 14 | Rename | New name shown; navigating in still works (`uid` is stable) |
| 15 | Move | Item leaves the source folder and appears in the target |
| 16 | Trash | Item disappears from the listing |

## Sync (skip entirely if untouched — and say so)

| # | Step | Expected |
|---|---|---|
| 17 | Create a sync pair | Validation rejects a nested/invalid local path with a clear message |
| 18 | Local change | Picked up after the debounce, one queued action, no echo loop |
| 19 | Remote change | Reconciled on the next scan |
| 20 | Pause / resume | Queue stops and drains; state survives a restart |
| 21 | Conflict | Surfaced with a real choice; the resolution is applied |
| 22 | Kill the app mid-sync | `SyncCrashRecovery` resumes cleanly on relaunch, no duplicated work |

## Failure paths

| # | Step | Expected |
|---|---|---|
| 23 | Wrong CLI path in settings | Clear "could not locate the CLI" message, no crash |
| 24 | Corrupt `settings.json` | Quarantined as `settings.json.corrupt-*`, app starts with defaults |
| 25 | Log out mid-session | `NotAuthenticated` surfaced as a real message, not a stack trace |
| 26 | Cancel a long operation | Cancels quietly, no error dialog, UI re-enables |

## Report

One line per row with its status, then the failures in detail with the console output. If sync
or a whole section was skipped, state that explicitly rather than omitting it.
