---
name: cli-command
description: Add or change a Proton Drive CLI command end-to-end (ProtonDriveService method, output parsing, error kind, fake response, tests). Use whenever a new `proton-drive` subcommand or flag needs to be reachable from the app.
---

# Add a Proton Drive CLI command

The app never talks to Proton's API. Every remote operation is a `proton-drive` process whose
stdout is parsed. That boundary is `src/MyPersonalDrive/Services/` and it has a fixed shape —
follow it, don't invent a parallel path.

## Before writing code

1. Run the real command once and capture its actual output. Never guess the JSON shape:

   ```bash
   proton-drive <subcommand> --json <args>
   ```

   If you can't run it (no auth), say so and stop — a guessed parser is worse than no command.
2. Check `docs/PLAN-LOCAL-SYNC.md` Appendix A. Most CLI behavior is already documented there
   (stable `uid` across move/rename, `activeRevision.value.claimedDigests.sha1`, recursive
   folder download, ~3.5s process cold start). Reuse those findings instead of re-investigating.

## Steps

1. **Add the method to `ProtonDriveService`** (`src/MyPersonalDrive/Services/ProtonDriveService.cs`).
   - Build `IReadOnlyList<string>` arguments and pass them to `_executor.ExecuteAsync`.
     One element per process argument — `ProcessStartInfo.ArgumentList` handles escaping.
     **Never** concatenate into a pre-quoted string; it cannot round-trip names with quotes
     or backslashes.
   - Always accept `CancellationToken cancellationToken = default` as the last parameter
     (or before an optional one) and forward it.
   - Pass an explicit `timeout:` only when the default is wrong. Commands that block on user
     interaction use `Timeout.InfiniteTimeSpan` (see `AuthenticateAsync`).
   - Flags go before positional args, target/parent path last — the fake routes by last
     argument, and so does the CLI's own convention (`MoveItemsAsync`, `UploadFilesAsync`).
   - Validate impossible inputs up front with `ArgumentException`, like `MoveItemsAsync` does
     for an empty path list.

2. **Parse the output** if the command returns data.
   - Reuse the three-way outcome pattern already in the file: parsed / not-JSON / malformed.
     Do not collapse "valid JSON, zero entries" into "unparseable" — that exact conflation was
     the bug fixed in `docs/PLAN-TECH-DEBT.md` B2.1.
   - A best-effort text fallback must raise `ListingParseWarning` so the miss is visible in the
     activity console, and must hard-fail when `AppSettings.StrictListingParsing` is on.
   - Map to a model in `src/MyPersonalDrive/Models/`. Use the real field names from Appendix A;
     no alias guessing.

3. **Classify new failure modes** in `CliErrorClassifier`
   (`src/MyPersonalDrive/Services/CliErrorClassifier.cs`).
   - The CLI has no per-failure exit codes, so this is substring matching against both streams
     concatenated (stderr then stdout — a crash writes the banner to stderr and the diagnosis to
     stdout). Keep it that way.
   - If the command can fail in a way no existing `CliErrorKind` covers, add a kind rather than
     letting it fall to `Unknown` and get surfaced as a generic error.
   - Callers switch on `CliException.Kind`. Never re-introduce message substring checks upstream.

4. **AOT safety.** The app project is `PublishAot=true`. Any new type you serialize must be
   registered in `AppJsonContext` (`[JsonSerializable(typeof(T))]`). No reflection-based
   `JsonSerializer` overloads. See the `aot-check` skill.

5. **Tests** — required, in `tests/MyPersonalDrive.Tests/Services/`.
   - Argument construction: assert on `FakeCliExecutor.Calls[n].Arguments` element by element.
     Add to `ProtonDriveServiceCommandTests.cs`.
   - Parsing: feed real captured stdout via `EnqueueOutput`. Cover the happy path, the empty
     result, and malformed output. Add to `ProtonDriveServiceListingTests.cs` or a sibling.
   - Error classification: add cases to `CliErrorClassifierTests.cs` using the real error text.
   - For scanners making concurrent calls whose order isn't deterministic, use
     `FakeCliExecutor.RespondForPath` instead of the queue.
   - Add a `[IntegrationFact]` test under `tests/.../Integration/` only if the behavior can't be
     proven with the fake. Those are opt-in via `MYPERSONALDRIVE_INTEGRATION=1`.

6. **Wire it to the UI** only if the ticket asks for it — see the `add-feature` skill.

7. **Verify**:

   ```bash
   ./scripts/run-tests.sh
   ```

## Checklist

- [ ] Real CLI output captured, not guessed
- [ ] Arguments as a list, cancellation token forwarded, timeout justified
- [ ] Empty result distinguished from unparseable result
- [ ] New failure text added to `CliErrorClassifier` (+ new `CliErrorKind` if needed)
- [ ] New serialized types registered in `AppJsonContext`
- [ ] Argument, parsing, and error tests added; `scripts/run-tests.sh` green
- [ ] `docs/ARCHITECTURE.md` updated if the service's public surface changed
