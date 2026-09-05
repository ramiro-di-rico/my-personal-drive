---
name: debug-cli
description: Capture real `proton-drive` output and turn it into a test fixture or an Appendix A finding, and diagnose a CLI call that behaves differently in the app than in a terminal. Use before writing any parser, fake response, or sync behavior that depends on what the CLI actually prints.
---

# Capture and debug real CLI output

**Never invent a CLI output shape.** A parser written against a guessed field name compiles,
passes its tests against the same guess, and fails against the real Proton Drive. Verified
behavior lives in `docs/PLAN-LOCAL-SYNC.md` **Appendix A** — that file, or a capture you took
yourself, is the only acceptable source.

## Capture

Run the same executable the app runs (`CliPath` in `~/.config/MyPersonalDrive/settings.json`),
with the same argument *list* — never a pre-quoted string, since the app uses
`ProcessStartInfo.ArgumentList`:

```bash
proton-drive filesystem list --json /my-files > /tmp/capture-list.json; echo "exit=$?"
```

Capture all three channels, because the app cares about all three:

```bash
proton-drive filesystem list --json /my-files >/tmp/out.txt 2>/tmp/err.txt; echo "exit=$?"
```

Notes that repeatedly matter:

- **Cold start is ~3.5s per process** (Appendix A). A "hang" that is actually startup cost is the
  most common false alarm — time it before assuming a timeout bug.
- `auth login` runs with an **infinite timeout** on purpose; it waits for a human.
- Some commands are eventually consistent: a `filesystem list` right after a `trash` still
  returns the trashed node about two thirds of the time (Appendix A #15). Re-run before
  concluding anything about a delete.
- Scrub before pasting anywhere: real paths, file names, `uid`s and account labels.

## Turn a capture into a fixture

1. Save the *unedited* stdout next to the test that consumes it, or inline it as a raw string —
   match how `ProtonDriveServiceListingTests.cs` already does it.
2. Feed it through `FakeCliExecutor`:

   ```csharp
   var cli = new FakeCliExecutor();
   cli.EnqueueOutput(capturedJson);                       // by call order
   cli.RespondForPath("/my-files/sub", capturedSubJson);  // by target path — for BFS scanners
   cli.EnqueueFailure(new DriveException("auth login", 1, "", "not logged in", "not logged in",
       DriveErrorKind.NotAuthenticated));
   ```

   `RespondForPath` exists because concurrent scanners have no deterministic call order; use it
   instead of guessing the sequence.
3. Assert on `cli.Calls` — the exact argument list the app produced — not only on the parsed
   result. Half of the CLI bugs in this repo were malformed arguments, not bad parsing.
4. If the capture revealed behavior not yet recorded, **add it to Appendix A** with the command,
   the output and the date. That is what makes the next change cheap.

## Diagnose "works in my terminal, not in the app"

Check in this order:

1. **Which binary?** `ProtonDriveCliLocator` runs whatever `settings.json` points at — possibly a
   stub from an earlier session (`/tmp/...`). Check it first.
2. **The exact command.** The in-app console panel prints the argument list and live output.
   Quote from it; don't paraphrase.
3. **Argument shape.** A single argument containing spaces is passed literally by `ArgumentList` —
   correct behavior that looks like breakage if you expected shell splitting.
4. **Environment.** The app's process does not inherit your interactive shell's aliases, `PATH`
   tweaks, or `cd`. Absolute paths only.
5. **Parse warnings.** A `ListingParseWarning` in the console means the JSON parser fell back to
   the text heuristic — the shape changed, or `StrictListingParsing` is off.
6. **Classification.** If the message is raw, the case is missing from `CliErrorClassifier`. Add
   it there — one place per provider — never upstream.
7. **Crash.** `~/.config/MyPersonalDrive/crash.log` holds the last-resort handler output.

## Checklist

- [ ] Output captured from a real run, with exit code and stderr — not written from memory
- [ ] Sensitive paths/uids scrubbed
- [ ] Fixture is the unedited capture; assertions cover the argument list too
- [ ] New behavior recorded in `docs/PLAN-LOCAL-SYNC.md` Appendix A
- [ ] Error strings classified in `CliErrorClassifier`, not matched upstream
- [ ] Any stub CLI or settings folder used for the investigation restored afterwards (`run-app`)
