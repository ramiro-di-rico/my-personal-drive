# Technical Plan — Paying Down the Technical Debt

> Remediation plan for the items listed in
> [ARCHITECTURE.md §9](ARCHITECTURE.md#9-technical-debt--known-risks).
> Companion to [PLAN-LOCAL-SYNC.md](PLAN-LOCAL-SYNC.md), whose phase F0.5 is a subset of this
> document (see §7 for the mapping).

## Status

- [x] **B0 — Crash-proofing.** `AsyncCommand` now catches and routes exceptions instead of
      letting `async void` crash the process; `Program.cs` has last-resort handlers writing to
      `crash.log`; `AppSettingsService.Load()` quarantines a corrupt `settings.json` instead of
      throwing; `DownloadActivityAsync`'s file write is guarded. C1, C2, C3 (§1) no longer crash.
- [x] **B1 — CLI boundary.** `CliException`/`CliErrorKind`/`CliErrorClassifier` added; the
      ViewModel switches on `Kind` instead of message substrings; `IProtonDriveCliExecutor`
      takes `IReadOnlyList<string>` via `ArgumentList` (the hand-rolled `Quote()` is gone); a
      configurable per-call timeout was added (infinite for `auth login`).
- [x] **B2.1 / B2.2 — Parser correctness.** The empty-vs-unparseable bug is fixed: JSON parsing
      now returns a three-way `Parsed`/`NotJson`/`Malformed` outcome, an empty folder renders as
      empty, and an unrecognized-but-valid JSON shape throws instead of silently returning
      nothing. The text-fallback parser now raises a `ListingParseWarning` (shown in the
      activity console) and can be turned into a hard error via the new
      `AppSettings.StrictListingParsing` flag.
- [ ] **B2.3 / B2.4** (typed `ModifiedAt`, dropping alias guessing) — held, pending
      [PLAN-LOCAL-SYNC.md F0](PLAN-LOCAL-SYNC.md#2-phase-0--cli-investigation-blocking-half-a-day)
      as planned in §4.
- [x] **B3 — Test foundation (partial).** `tests/MyPersonalDrive.Tests` added (xUnit,
      `FakeCliExecutor`), 37 tests covering listing parsing (every outcome + the empty-folder
      regression), command/argument construction, `CliErrorClassifier`, `AsyncCommand`'s
      exception routing, and `AppSettingsService`'s corrupt-file handling. Run with
      `scripts/run-tests.sh`. Not yet covered: `DriveCacheService`, `PathMapper`-equivalent
      (doesn't exist until sync work starts).
- [ ] **B4 (persistence/state), B5 (async lifecycle), B6 (observability)** — not started.

---

## 0. Executive summary

The twelve items in §9 are not twelve independent tasks. They cluster into **six batches**, and
the ordering between them is driven by one observation made while writing this plan:

> **Three of the debt items compose into reachable crashes that exist in the code today.**
> They are not hypothetical. §1 documents them, with the call chains.

So the sequencing is not "highest debt first" but:

1. **B0 — Stop the crashes** (half a day). Cheap, no behavior change, immediately valuable.
2. **B1 — Fix the CLI boundary** (2 d). Typed errors, safe argument passing, timeouts.
   Everything else depends on this seam being sound.
3. **B2 — Fix the parser** (1.5 d). Contains the one bug that becomes *destructive* the moment
   sync exists.
4. **B3 — Test foundation** (1 d). Deliberately after B1/B2, so the first tests are written
   against interfaces worth keeping.
5. **B4 — Persistence & state** (1.5 d). Schema versioning, cache staleness, real auth check.
6. **B5 — Async lifecycle** (1 d) and **B6 — Observability** (1 d).
7. **B7 — Product gaps** (§6): not debt, deferred features. Separately scoped.

**Total for B0–B6: ~8.5 days.** B0+B1+B2 (~4 days) removes every crash and every
correctness risk; the rest is quality of life.

A note on what this plan deliberately does *not* do: it does not rewrite
`MainWindowViewModel`, does not introduce a DI container, and does not restructure the View.
Those are worth doing, but they are churn without a forcing function. The forcing function
arrives with the sync feature ([PLAN-LOCAL-SYNC.md §8](PLAN-LOCAL-SYNC.md)); until then, the
950-line VM is ugly but not dangerous.

---

## 1. Reachable crashes found while writing this plan

The app has no global exception handler ([`Program.cs`](../src/MyPersonalDrive/Program.cs) is
14 lines with none), every `catch` in the ViewModel is `catch (InvalidOperationException)`, and
[`AsyncCommand.Execute`](../src/MyPersonalDrive/ViewModels/AsyncCommand.cs#L869) is
`async void`. Any exception that is *not* `InvalidOperationException` therefore terminates the
process silently.

Three such paths exist:

### C1 — Bad CLI path crashes the app (TD-7 + type mismatch)

`CliPath` is `Mode=TwoWay` bound to a free-text `TextBox`, and `CanAuthenticate()` only checks
that the string is non-empty — not that the file exists.

```
User types a nonexistent path → clicks 🔑
  → AuthenticateAsync()                    catch (InvalidOperationException)
  → ProtonDriveService.AuthenticateAsync()
  → ProtonDriveCliExecutor.ExecuteAsync()
  → ProtonDriveCliLocator.Locate()         throws FileNotFoundException
                                           ↑ IOException, NOT InvalidOperationException
  → escapes every catch
  → AsyncCommand.Execute (async void)      → unhandled → process dies
```

### C2 — Corrupt `settings.json` crashes every action (TD-7 + TD-8)

`AppSettingsService.Load()` calls `JsonSerializer.Deserialize` with no error handling, and
`ProtonDriveCliLocator.Locate()` calls `Load()` **on every single command**. A truncated or
hand-edited settings file therefore turns *every* button in the app into a crash, with no
message and no way to recover from inside the app.

### C3 — Activity export crashes on an unwritable path (TD-7)

`DownloadActivityAsync` calls `File.WriteAllTextAsync` with no try/catch. A read-only
destination or a full disk → `IOException` → same async-void death.

**These three are the justification for doing B0 first, before anything else.**

---

## 2. Batch B0 — Crash-proofing (0.5 d, no behavior change)

Goal: make it structurally impossible for an unhandled exception to kill the app, without yet
changing any error semantics.

### B0.1 — Make `AsyncCommand` safe

```csharp
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;   // new
    private bool _isExecuting;

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (OperationCanceledException)
        {
            // expected on navigation; swallow
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);   // never let it escape an async void
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }
}
```

The VM passes a single `HandleUnexpectedError` that sets `StatusMessage` + `IsWarning` and
writes to the activity log. `_onError` is optional so existing call sites keep compiling; a
follow-up pass wires it everywhere.

### B0.2 — Last-resort handlers in `Program.cs`

```csharp
public static void Main(string[] args)
{
    AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject);
    TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write(e.Exception); e.SetObserved(); };
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
```

`CrashLog.Write` appends to `<BaseFolder>/crash.log` with a timestamp. This is the minimum
viable version of TD-9 and costs ~20 lines; the full logging story is B6.

`UnobservedTaskException` matters specifically because of the five `_ = RefreshAsync()`
fire-and-forget calls (TD-6) — today a failure in any of them is invisible.

### B0.3 — Harden `AppSettingsService.Load()`

Wrap the deserialize in a try/catch for `JsonException`/`IOException`. On failure: rename the
bad file to `settings.json.corrupt-<timestamp>`, return defaults, and surface a warning. This
alone kills C2.

### B0.4 — Guard the two unprotected IO call sites

`DownloadActivityAsync` (C3) and `AppSettingsService.Save()` get try/catch with a status message.

**Exit criteria for B0**: manually verify C1, C2 and C3 produce a status-bar message instead of
a dead process.

---

## 3. Batch B1 — The CLI boundary (2 d) · TD-3, TD-5, and the root of TD-7

This is the seam everything else hangs off. Three changes, all in `Services/`.

### B1.1 — `CliException` with structured data (TD-3)

```csharp
public class CliException : InvalidOperationException   // inherits, so existing catches still work
{
    public CliException(string commandText, int exitCode, string stdout, string stderr, string message)
        : base(message) { … }

    public string CommandText { get; }
    public int ExitCode { get; }
    public string Stdout { get; }
    public string Stderr { get; }
    public CliErrorKind Kind { get; init; } = CliErrorKind.Unknown;
}

public enum CliErrorKind { Unknown, NotAuthenticated, NotFound, AlreadyExists, Quota,
                           Network, Timeout, PermissionDenied, InvalidArgument }
```

Inheriting from `InvalidOperationException` is deliberate: all fifteen existing `catch` clauses
keep working unchanged, so this lands as a pure addition and can be adopted incrementally.

### B1.2 — `CliErrorClassifier`, isolated in one file (TD-3)

The substring matching does not disappear yet — the CLI gives us nothing better until
[PLAN-LOCAL-SYNC.md §2 F0 #10](PLAN-LOCAL-SYNC.md#2-phase-0--cli-investigation-blocking-half-a-day)
is answered. What changes is that it stops being scattered across `HandleLoadError` and
`FormatCliError` in the ViewModel and becomes a single, unit-testable class:

```csharp
internal static class CliErrorClassifier
{
    public static CliErrorKind Classify(int exitCode, string stderr, string stdout) { … }
}
```

The ViewModel then switches on `CliErrorKind`, never on message text. The day the CLI provides
real exit codes, one file changes. `FormatCliError`'s pseudo-path hack (passing `"auth login"`
as a `path` argument to select an error message) goes away with it.

### B1.3 — `ArgumentList` instead of string concatenation (TD-5)

Today arguments are built as one string with hand-rolled quoting
(`Quote(v) => $"\"{v.Replace("\"", "\\\"")}\""`), and `LoadFolderAsync` doesn't even use
`Quote` — it interpolates the quotes inline. .NET's own escaping rules for the `Arguments`
string are Windows-shaped and do not round-trip reliably for names containing `"`, `\`, or
trailing backslashes.

The fix is to stop building a string at all:

```csharp
Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct = default);
```

populating `ProcessStartInfo.ArgumentList`, which .NET escapes correctly per platform.
`ProtonDriveService` changes from

```csharp
_executor.ExecuteAsync($"filesystem download {Quote(path)} {Quote(localFolder)}", ct)
```

to

```csharp
_executor.ExecuteAsync(["filesystem", "download", path, localFolder], ct)
```

`Quote` is deleted. The `commandText` used for the console display is derived from the list for
presentation only — it never feeds back into execution. This removes a whole class of bug with
filenames containing quotes and, incidentally, any argument-injection concern.

> This is a breaking change to `IProtonDriveCliExecutor`, which is exactly why it should happen
> **before** B3 writes tests against that interface, and before the sync engine adds a second
> consumer of it.

### B1.4 — Timeout

Add a per-call timeout (default 120 s, configurable; effectively infinite for `auth login`,
which waits on a browser round-trip) via a linked `CancellationTokenSource`. On expiry, throw
`CliException` with `Kind = Timeout`. Today a hung CLI process hangs that operation forever with
`IsLoading` stuck true.

---

## 4. Batch B2 — Parsing correctness (1.5 d) · TD-2, TD-4

### B2.1 — The empty-vs-unparseable bug (TD-4) — **highest-severity item in this document**

[`TryParseJsonListing`](../src/MyPersonalDrive/Services/ProtonDriveService.cs#L696) returns
`false` when it successfully parses JSON containing zero entries, which sends valid JSON to
`ParseTextListing`, whose emoji-and-last-space heuristic produces garbage rows from JSON
punctuation.

Today this is cosmetic: a wrong-looking listing that a refresh fixes. **Under
[PLAN-LOCAL-SYNC.md](PLAN-LOCAL-SYNC.md) it becomes destructive** — the reconciler reads
"folder is empty" as "everything was deleted remotely" and, in `TwoWay` or `LocalToRemote`,
happily propagates the deletion. This item must be closed before any sync code is written.

The fix is to make the three outcomes distinct instead of collapsing them into `bool`:

```csharp
private enum ListingParseOutcome { Parsed, NotJson, Malformed }

private static ListingParseOutcome TryParseJsonListing(
    string output, string parentPath, out IReadOnlyList<DriveItem> items);
```

- Valid JSON with a recognized container → `Parsed`, even with zero items.
- Not JSON at all (`JsonException` on the very first token) → `NotJson`, fall back to text.
- Valid JSON whose shape we don't recognize → `Malformed`, **throw**. Silently returning an
  empty listing for an unrecognized response shape is the failure mode that loses data.

### B2.2 — Retire the text parser to a fallback of last resort (TD-2)

`ParseTextListing` splits on the last space and looks for a `🗂` emoji; any folder or file whose
name contains a space is parsed incorrectly. Since `--json` is always passed, this path should
be unreachable in practice.

- Keep it, but log at `Warning` when it is used, with the first line of the offending output.
- Add a settings flag `StrictListingParsing` (default **on** after F0 confirms the JSON shape)
  that turns the fallback into a hard error.
- If F0 confirms `--json` is reliable, delete it in a follow-up.

### B2.3 — Type `ModifiedAt` properly (TD-2)

`DriveItem.ModifiedAt` is `string?` carrying whatever the CLI emitted. It is displayed raw and
is uncomparable, which blocks sync change-detection entirely.

```csharp
public sealed record DriveItem(
    string Path, string Name, bool IsFolder,
    long? Size = null,
    DateTimeOffset? ModifiedAt = null,     // was string?
    string? Owner = null,
    bool IsShared = false,
    string? NodeId = null);                // if F0 #3 finds one
```

Parse with `DateTimeOffset.TryParse(…, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)`;
on failure keep `null` and log once per session rather than throwing.

Ripples: `DriveNodeViewModel.ModifiedText` formats for display; `SelectedModified` likewise;
the `DriveItems.ModifiedAt` column becomes ISO-8601 UTC, which needs the migration from B4.1.

**Depends on F0 #1/#2** — do not guess the field names a second time. If F0 hasn't run, do B2.1
and B2.2 now and hold B2.3.

### B2.4 — Bonus: drop the alias guessing

Once F0 #1 documents the real field names, collapse
`ReadString(entry, "modifiedAt", "updatedAt", "lastModified")` down to the actual field, and
make an unexpected shape an error rather than a silent `null`. Alias-tolerant reading was a
reasonable hedge while the format was unknown; once it's known, it hides breakage.

---

## 5. Batch B3 — Test foundation (1 d) · TD-1

Deliberately sequenced *after* B1/B2 so the first tests target interfaces that are already the
right shape.

```
tests/MyPersonalDrive.Tests/
  MyPersonalDrive.Tests.csproj        # xUnit, targets net10.0, references the app project
  Fakes/FakeCliExecutor.cs            # records IReadOnlyList<string> calls, returns canned output
  Services/ProtonDriveServiceTests.cs # command construction + listing parsing
  Services/CliErrorClassifierTests.cs
  Services/DriveCacheServiceTests.cs  # against a temp-file SQLite db
  Services/AppSettingsServiceTests.cs # incl. the corrupt-file path from B0.3
  ViewModels/AsyncCommandTests.cs     # incl. "throwing task does not escape"
```

Two notes:

- **`IsAotCompatible` does not apply to the test project.** Tests run on the JIT runtime;
  xUnit's reflection is fine there. Only the app assembly must stay AOT-clean.
- `FakeCliExecutor` is the highest-leverage object in the repo. `IProtonDriveCliExecutor`
  already exists and is already the exact seam needed — the interface was well chosen; it just
  never got used for this.

Priority coverage, in order: listing parsing (every JSON shape variant + the three outcomes from
B2.1) → command construction (assert the argument *list*, which is why B1.3 comes first) → error
classification → cache upsert/delete-subtree semantics.

Target: ~60 tests, all offline, no CLI and no network. Add `dotnet test` to
`scripts/` and to any future CI.

---

## 6. Batch B4 — Persistence and state (1.5 d) · TD-8

### B4.1 — Schema versioning (prerequisite for everything that touches the DB)

```csharp
// DriveCacheService
private static readonly Migration[] Migrations = [
    new(1, Sql.CreateDriveItems),       // formalizes today's implicit schema
    new(2, Sql.DriveItemsIsoDates),     // B2.3: rewrite ModifiedAt to ISO-8601 UTC
    // 3+ : the sync tables, from PLAN-LOCAL-SYNC.md §3.1
];
```

On open: read `PRAGMA user_version`, apply pending migrations in a single transaction, write the
new version. Existing users are at version 0 with the current schema, so migration 1 must be
written to be a no-op against it (`CREATE TABLE IF NOT EXISTS`) and simply stamps the version.

Also worth fixing here, spotted while reading `DriveCacheService`:

- **`SyncItemsAsync` calls `GetCachedItemsAsync` from inside an open write transaction**, which
  opens a *second* connection to the same file. That is a `SQLITE_BUSY` waiting to happen under
  any concurrency. Read the existing rows on the same connection, before `BeginTransaction`.
- Enable WAL (`PRAGMA journal_mode=WAL`) — the sync engine will read while the browser writes.
- Consider a single long-lived connection instead of open/close per operation.

### B4.2 — Cache staleness (TD-8)

Add `CachedAt TEXT` to `DriveItems` (migration 3). The cache-first render stays as-is, but:

- Entries older than a configurable TTL (default **24 h**) are not rendered from cache at all —
  show the loading state instead of stale data.
- The status message distinguishes "showing cached (2 min old)" from "showing cached (3 days old)."
- Add an explicit "clear cache" action, useful for support and for the corrupt-DB case.

### B4.3 — Real authentication check (TD-8)

`IsAuthenticated` is a locally persisted guess, so the app can start believing it is logged in
when the CLI session has expired, producing a confusing failure on the first listing.

Depends on F0: if a cheap `auth status`-style command exists, call it in `InitializeAsync` and
trust it. If not, treat the first `filesystem list` as the probe and derive the flag from
`CliErrorKind.NotAuthenticated` (which B1.2 makes reliable) instead of from a substring. Either
way `IsAuthenticated` stops being authoritative and becomes a cached hint.

### B4.4 — Cache the settings read

`ProtonDriveCliLocator.Locate()` reads and deserializes `settings.json` from disk on **every CLI
command**. Cache the value in `AppSettingsService` and invalidate on `Save()`. Trivial, and it
removes the per-command IO that made C2 so pervasive.

---

## 7. Batch B5 — Async lifecycle (1 d) · TD-6

### B5.1 — Scope the `CancellationTokenSource`

One `_cts` field currently covers all navigation, so any new load cancels any in-flight
operation, including unrelated ones. Replace with a small `OperationScope` helper holding one
CTS per logical concern (navigation, mutation, and later sync), and dispose them properly —
today the old CTS is never disposed.

### B5.2 — Give the five `_ = RefreshAsync()` calls a home

`_ = RefreshAsync()` after upload/create/rename/copy/trash means a failed refresh is invisible
and unobserved. Introduce:

```csharp
private void FireAndForget(Task task, string context) =>
    task.ContinueWith(t => Dispatcher.UIThread.Post(() =>
            HandleBackgroundFailure(context, t.Exception!)),
        TaskContinuationOptions.OnlyOnFaulted);
```

Combined with B0.2's `UnobservedTaskException` handler, background failures become visible
instead of silently swallowed.

### B5.3 — Fix the `IsLoading` race

`LoadFolderAsync`'s `finally` sets `IsLoading = false` guarded by `CurrentPath == path`, while
the fire-and-forget `FetchFromCliAndUpdateCacheAsync` sets it independently. With fast
navigation these interleave and the spinner can stick on or flicker off early. Make `IsLoading`
a counter (`BeginLoading()`/`EndLoading()` returning `IDisposable`) rather than a bool.

---

## 8. Batch B6 — Observability (1 d) · TD-9

### B6.1 — Real logging

Introduce a minimal `IAppLogger` writing to `<BaseFolder>/logs/app-<date>.log`, with rotation at
5 files / 5 MB. **Do not add Serilog or Microsoft.Extensions.Logging** — both are reflection-heavy
and would need AOT-specific configuration for a ~100-line need. A hand-rolled writer with a
`Channel<LogEntry>` and a background drain is enough, and stays trivially AOT-clean.

Log: every CLI command with its exit code and duration, every classified error, migrations
applied, cache hits/misses at debug level.

Redaction: paths and file names are personal data. Log full paths at debug level only; at info
level log the operation and the path *depth*, not the names.

### B6.2 — Fix the console's O(n²) append

`AppendCommandLine` rebuilds the entire 200-line buffer with `string.Join` **and** raises eight
`CanExecuteChanged` events **per output line**. During a large listing this is the app's hottest
path for no reason.

- Bind the console to an `ObservableCollection<string>` with an `ItemsControl`, or keep a
  `StringBuilder` and rebuild on a 100 ms timer.
- Move `RaiseCommandStates()` out of the per-line path — only `DownloadActivityCommand` and
  `ClearActivityCommand` depend on the line count, and only on the empty/non-empty transition.

---

## 9. Out of scope here: TD-10, TD-11, TD-12

These three are listed as debt but are really **missing features**, and shouldn't compete for
the same budget:

| Item | Assessment |
|---|---|
| **TD-10** hardcoded `_rootPath = "/my-files"` | Not harmful. Becomes relevant only if Proton exposes other roots (shared-with-me, devices). Needs F0 to even know what roots exist. ~0.5 d once known. |
| **TD-11** no recursive operations (folder download/delete) | A real gap, but the sync engine's `RemoteScanner` + `TransferQueue` ([PLAN-LOCAL-SYNC.md §6.2, §7](PLAN-LOCAL-SYNC.md)) build exactly this machinery. **Do it there and reuse it**, rather than writing a throwaway recursive walker now. |
| **TD-12** no progress/throughput | Blocked on F0 #12 (does the CLI emit parseable progress?). If it does, the plumbing is small since `CommandOutput` already streams per line. If it doesn't, the honest answer is an indeterminate spinner. |

---

## 10. Sequencing and dependencies

```
        F0 (CLI investigation, from PLAN-LOCAL-SYNC.md §2)
         │
         │  ┌──────────────────────────────────────────┐
         │  │ B0  Crash-proofing            0.5 d      │  ← no dependencies, do first
         │  └──────────────┬───────────────────────────┘
         │                 ▼
         │  ┌──────────────────────────────────────────┐
         ├─►│ B1  CLI boundary              2.0 d      │  B1.2 needs F0 #10
         │  └──────────────┬───────────────────────────┘
         │                 ▼
         ├─►│ B2  Parser correctness        1.5 d      │  B2.3/B2.4 need F0 #1,#2
         │  └──────────────┬───────────────────────────┘
         │                 ▼
         │  ┌──────────────────────────────────────────┐
         │  │ B3  Test foundation           1.0 d      │  after B1.3 (interface change)
         │  └──────────────┬───────────────────────────┘
         │                 ▼
         ├─►│ B4  Persistence & state       1.5 d      │  B4.3 needs F0
         │  └──────────────┬───────────────────────────┘
         │                 ▼
         │     B5  Async lifecycle          1.0 d
         │     B6  Observability            1.0 d         (B5/B6 are independent, parallelizable)
         ▼
      PLAN-LOCAL-SYNC.md F1 →
```

**B0 can start today** — it depends on nothing and removes three live crashes.
**B1 and B2 should not start before F0**, or they will bake in a second round of guesses about
the CLI's behavior.

### Mapping to the sync plan

[PLAN-LOCAL-SYNC.md §13](PLAN-LOCAL-SYNC.md#13-phasing-and-estimate) budgets 1.5 d for "F0.5 —
preliminary refactors." That estimate assumed the minimum needed to unblock sync, which is
exactly:

**F0.5 = B1.1 + B1.2 + B2.1 + B2.3 + B4.1 + B3 (reconciler-adjacent tests only)**

Doing the full B0–B6 (~8.5 d) instead of just F0.5 (~1.5 d) adds ~7 days but means the sync
engine is built on a foundation that can report typed errors, cannot crash on an unexpected
exception, has a test harness, and has real logs to debug against. Given that sync is the
feature most capable of destroying user data, **doing the full remediation first is the
recommendation** — a sync bug found through a crash log and a failing unit test is a very
different experience from one found by a user missing files.

---

## 11. Suggested commit sequence

One PR per batch, each independently revertible:

| # | Branch | Contents | Behavior change |
|---|---|---|---|
| 1 | `fix/crash-proofing` | B0.1–B0.4 | Errors show a message instead of killing the app |
| 2 | `refactor/cli-exception` | B1.1, B1.2 | None (new type, classifier extracted) |
| 3 | `refactor/cli-argument-list` | B1.3, B1.4 | Fixes names with quotes; adds timeout |
| 4 | `fix/listing-parse-outcomes` | B2.1, B2.2 | Empty folders render correctly |
| 5 | `test/foundation` | B3 | None |
| 6 | `refactor/typed-modified-at` | B2.3, B2.4 + migration 2 | Dates render formatted |
| 7 | `refactor/db-migrations` | B4.1, B4.4 | None |
| 8 | `feat/cache-ttl-and-auth-check` | B4.2, B4.3 | Stale cache no longer shown |
| 9 | `refactor/async-lifecycle` | B5 | Background failures become visible |
| 10 | `feat/app-logging` | B6 | Log files under `<BaseFolder>/logs/` |

PRs 1, 2, 5, 7 and 9 are behavior-preserving and can merge with low scrutiny. PRs 3, 4, 6 and 8
change observable behavior and deserve a manual pass against a real drive.

---

## 12. Definition of done

- [ ] C1, C2, C3 verified as non-crashing.
- [ ] No `catch (InvalidOperationException)` remains in the ViewModel that isn't matched by a
      `CliException` with a classified `Kind`.
- [ ] No CLI argument is built by string concatenation; `Quote` is deleted.
- [ ] A folder that is genuinely empty renders as empty, and an unrecognized response shape
      raises an error. Covered by tests.
- [ ] `dotnet test` runs green offline, with no CLI installed.
- [ ] `PRAGMA user_version` is non-zero and the migration runner is exercised by a test.
- [ ] `DriveItem.ModifiedAt` is a `DateTimeOffset?`.
- [ ] A crash log and an app log exist and are written to under normal operation.
- [ ] `ARCHITECTURE.md §9` is rewritten to reflect what actually remains.
