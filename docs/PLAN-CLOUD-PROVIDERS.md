# Technical Plan — Multiple cloud storage providers (Proton Drive, OneDrive, …)

> Today the app *is* a Proton Drive CLI front-end: `ProtonDriveService` is a concrete class
> referenced directly by the sync engine, the metrics scanners and `MainWindowViewModel`, and the
> whole error/console/auth contract is shaped like "we launch a process and read its stdout".
> This plan does two things:
>
> - **P — Provider seam.** Extract everything Proton-specific behind a provider interface, so a
>   second backend is an implementation instead of a rewrite. No user-visible behavior change.
> - **O — OneDrive.** Add Microsoft OneDrive as the second provider, over Microsoft Graph REST
>   (there is no `onedrive` CLI to shell out to — see [§4](#4-onedrive-o--microsoft-graph-design)).
>
> **Scope decision (from the request):** the user configures **one active provider at a time**.
> Multiple providers live *simultaneously* is explicitly optional and deferred to
> [P7](#p7--optional-more-than-one-account-active-at-once), but every schema and settings change in
> P1–P6 is designed so P7 is additive and needs no second migration of user data.
>
> Companions: [ARCHITECTURE.md](ARCHITECTURE.md) (current state, commit `a82b0a8`),
> [PLAN-LOCAL-SYNC.md](PLAN-LOCAL-SYNC.md) (the sync engine this seam sits under; **Appendix A** is
> the verified Proton CLI behavior that several of the abstractions below exist to contain),
> [PLAN-BROWSER-VIEWS.md](PLAN-BROWSER-VIEWS.md) (the metrics scanners that also hold a service),
> [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md).
> No implementation branch yet.

## Status

- [x] **P1 — provider interfaces + `ProtonDriveProvider` adapter, mechanical call-site swap.**
      `Services/Providers/{ICloudDriveProvider,IDriveOperations,IDriveAuthenticator,IRemoteViewInvalidator,IProviderDiagnostics,ProviderCapabilities,ProviderId}.cs`
      added; `ProtonDriveService`, `ProtonDriveCliExecutor`, `ProtonDriveCliLocator`,
      `CliErrorClassifier`, `CliReleaseFeed`, `CliUpdateInstaller`, `CliPlatformKey`,
      `CliVersionComparer` (+ their interfaces) moved to `Services/Providers/Proton/` with a
      `ProtonDriveProvider` adapter alongside them. `App.axaml.cs`, `SyncExecutor`,
      `SyncBaselineWriter`, `RemoteScanner`, `RemoteTreeWalker`, `FolderStatsScanner` and
      `MainWindowViewModel` now depend on `ICloudDriveProvider`/`IDriveOperations` instead of
      `ProtonDriveService`. Two deliberate deviations from §2 as originally written, kept minimal
      on purpose: `IDriveAuthenticator` ships as `AuthenticateAsync`/`LogoutAsync` only (the
      richer `AuthStatus`/`IAuthPrompt` shape is deferred to P6, when OneDrive's OAuth flow
      actually needs it); `IProviderPathSyntax` and the `ProviderActivity`/`DriveException`
      rename were **not** done — `HasUnmappableName`/`CombinePath` are still called as
      `Providers.Proton.ProtonDriveService` statics (flagged inline in `RemoteScanner` and
      `MainWindowViewModel`, and in Appendix B), and the console events keep today's
      `Cli*EventArgs` shape on `ICloudDriveProvider` — both are explicitly P2/P3's job, not P1's.
      `CliException`/`CliErrorKind`/`CliCommandEventArgs` were left in `Services/` unmoved for the
      same reason. Verified: `./scripts/run-tests.sh` green (561 passed, 5 skipped-integration),
      `dotnet publish -r linux-x64` AOT-clean (no IL2xxx/IL3xxx), and the published binary runs
      against a stub CLI with no crash.log. Landed on branch `feature/cloud-providers-seam`.
- [x] **P2 — generalize the error and activity (console) contract off the CLI.**
      `CliException`/`CliErrorKind` renamed to `DriveException`/`DriveErrorKind` and moved to
      `Services/Providers/` (member names/values kept, plus two new kinds: `RateLimited`,
      `Conflict`). Added `Services/Providers/ProviderActivity.cs`
      (`ActivityKind` + `ProviderActivity`); `ICloudDriveProvider`'s three
      `CommandStarted`/`CommandOutput`/`CommandFinished` events collapsed into one
      `event EventHandler<ProviderActivity>? Activity`, with `ProtonDriveProvider` doing the
      translation from the CLI-shaped events it still gets from `ProtonDriveService`.
      `MainWindowViewModel`'s three `OnCommand*` handlers became one `OnActivity`;
      `FormatCliError` renamed to `FormatDriveError`. `CliCommandEventArgs.cs` moved to
      `Services/Providers/Proton/` (it is now purely that provider's internal executor-event
      shape). Added `ProtonDriveProviderTests` covering the Started→Finished translation.
      Deviation: **`StrictListingParsing` was left on the flat `AppSettings`**, not moved into a
      Proton settings section — P5 already owns building the real per-provider settings
      structure (`ProviderSettings` keyed by provider id, migrating `CliPath`/`IsAuthenticated`);
      building a one-off nested section for this single flag now would just be redone there.
      Verified: 562 tests pass (561 + the new provider test), app and AOT builds clean.
- [x] **P3 — per-provider path syntax and content-hash algorithm.**
      Added `Services/Providers/IProviderPathSyntax.cs` (`Combine`, `IsRemoteNameMappableLocally`,
      `Comparison`) and `Providers/Proton/ProtonPathSyntax.cs` (delegates to
      `ProtonDriveService.CombinePath`/`HasUnmappableName`); `ICloudDriveProvider.Paths` exposes
      it. `RemoteScanner` and `MainWindowViewModel` now go through `provider.Paths` instead of the
      static Proton calls; `RemoteScanner` also reports a case-collision as a skipped node when
      `Paths.Comparison` is case-insensitive (inert for Proton, exercised via a test decorator
      since no case-insensitive provider exists yet).
      Added `Services/Providers/IContentHasher.cs` + `Sha1ContentHasher.cs` (wraps
      `LocalFileHasher`); `SyncExecutor` and `SyncBaselineWriter` take a hasher and a
      `RemoteHashAlgorithm` via optional constructor parameters (default: Sha1, so every existing
      call site is unaffected) and tag every `NodeFingerprint` they build with which algorithm
      produced its hash. `SyncReconciler.AreEquivalent` gained the mismatch guard: two hashes are
      only compared when both sides' algorithm tags agree (or are unset); otherwise it falls back
      to size+mtime instead of reporting a spurious change. `RemoteHashAlgorithm` moved from
      `Services/Providers/ProviderCapabilities.cs` to `Models/RemoteHashAlgorithm.cs` so
      `NodeFingerprint` (a plain data record) can carry it without depending on `Services`.
      **Deviations:** `PathMapper` was *not* injected with `IProviderPathSyntax` — it never called
      `CombinePath`/`HasUnmappableName` in the first place (it builds remote paths by plain `/`
      concatenation, which is exactly equivalent since an unmappable name is filtered by
      `RemoteScanner` before any relative path reaches it); injecting an unused dependency there
      would be abstraction with nothing to justify it. `IProviderPathSyntax` also omits `Root`,
      `GetParent` and `IsLocalNameMappableRemotely` from §2.4's original design — nothing calls
      them yet (the upload-side check is a P6 concern once OneDrive's own rules exist to test
      against). `SyncReconciler`'s dictionaries stay `StringComparer.Ordinal`, per §2.4's own
      decision, not a deviation.
      Verified: 565 tests pass (2 new RemoteScanner case-collision tests, 1 new
      SyncReconciler algorithm-mismatch test — confirmed load-bearing by temporarily removing the
      guard and watching the test fail with a spurious upload), AOT publish clean, published
      binary runs against a stub CLI.
- [x] **P4 — account-scope the persisted state (`cache.db` migration 6).**
      Migration 6 adds `AccountKey` (default `'proton:default'`) to `DriveItems`, `FolderMetrics`
      and `SyncPairs`, rebuilding each table's primary key/unique constraint, plus
      `SyncState.HashAlgorithm` (a plain `ALTER TABLE ADD COLUMN`, no rebuild needed).
      `DriveCacheService`, `FolderMetricsStore` and `SyncStateStore` take an `accountKey`
      constructor parameter (default `'proton:default'`, so every existing call site — including
      `App.axaml.cs`, unchanged until P5 gives it something else to pass — keeps working) and add
      it to every `CommandText` touching `DriveItems`/`FolderMetrics`/`SyncPairs` (`SyncState`,
      `SyncQueue`, `SyncLog` are keyed by `PairId`, which is only ever obtained from an
      already-scoped `GetPairsAsync`, so they inherit the scoping rather than needing their own
      column). `SyncStateStore` also now reads/writes `SyncState.HashAlgorithm` for P3's guard.
      `NodeFingerprint` reconstructed from a pre-migration-6 row gets `HashAlgorithm: null`
      ("unknown"), which the P3 guard already treats as "not a mismatch."
      **Two real bugs found and fixed while verifying this against a copy of a real, warm
      `cache.db`** (not a synthetic one — the R1 risk this plan calls out by name):
      1. The original migration SQL renamed `SyncPairs`/`DriveItems`/`FolderMetrics` out of the
         way before recreating them under the same name. SQLite's `ALTER TABLE … RENAME` silently
         rewrites *every other table's* schema that references the renamed one in a foreign key —
         `SyncQueue`/`SyncState` declare `PairId … REFERENCES SyncPairs(Id)` — so their FK clause
         got rewritten to point at the throwaway `SyncPairs_pre6` name, and the subsequent
         `DROP TABLE SyncPairs_pre6` left them referencing a table that no longer existed. First
         symptom: a `SyncQueue` insert failing with "no such table: main.SyncPairs_pre6" — a name
         that appears nowhere in that statement. Fixed by never renaming the original table at
         all: create the replacement under a temp name, copy data in, drop the original, rename
         the *replacement* into the final name — nothing references the temp name, so nothing
         gets rewritten.
      2. Even with that reordering, `DROP TABLE SyncPairs` (now dropping only the never-renamed
         original) with `PRAGMA foreign_keys=ON` — which `SyncStateStore.OpenConnection` always
         sets — is treated by SQLite as deleting every row of the parent table first, which
         **cascades**: it silently deleted all 568 `SyncState` rows and both `SyncQueue` rows in
         the test copy before the migration even finished. Fixed at the root, in
         `SqliteMigrationRunner.Apply` itself (not per-migration): foreign key enforcement is now
         switched off for the duration of *any* migration run and restored to whatever the caller
         had before returning, since any future migration that rebuilds an FK target would hit the
         exact same footgun.
      Verified end to end: 565 tests pass; a copy of this machine's real `cache.db` (171
      `DriveItems`, 4 `SyncPairs`, 568 `SyncState`, 2 `SyncQueue` rows) migrated with zero FK
      violations and byte-for-byte-equal row counts before/after, and a write through the
      migrated schema (`EnqueueActionsAsync`) succeeded; AOT publish clean; **the migration was
      then run for real** against this machine's actual `~/.config/MyPersonalDrive/cache.db` via
      the published binary (full directory backed up first to
      `~/.config/MyPersonalDrive.pre-p4-backup`) — same result, `user_version` now 6, all rows
      intact, app stayed running.
- [x] **P5 (partial) — provider selection scaffolding in settings.**
      Added `Services/Providers/{ProviderDescriptor,IProviderCatalog,ProviderCatalog}.cs`: the
      catalog centralizes the Proton-construction wiring that used to sit inline in
      `App.axaml.cs` (`ProtonDriveCliLocator` → `ProtonDriveCliExecutor` → `ProtonDriveService` →
      `ProtonDriveProvider`) behind `Create(ProviderId, AppSettingsService)`, and exposes
      `Available` — today exactly one entry, Proton — for a settings-view picker to enumerate.
      `AppSettings.ActiveProvider` (string, `nameof(ProviderId.Proton)` default,
      `ActiveProviderOrDefault()` — same degrade-on-unrecognized-value contract as `ViewMode`) is
      read once, in `App.axaml.cs`, to choose which provider `catalog.Create` builds.
      `MainWindowViewModel` gained `AvailableProviders` (from the injected/default catalog) and
      `ActiveProviderDisplayName` (`_provider.DisplayName`); the settings view's connection card
      shows the active provider's name above the CLI-path row.
      **Deliberately not done, and why:** the plan's "switching provider with sync pairs
      configured must be blocked or explicit... persist and prompt for restart" flow, and moving
      `AppSettings.CliPath`/`IsAuthenticated` into a provider-scoped structure, are **deferred to
      P6**. `AvailableProviders` has exactly one entry today — there is nothing to switch *to*,
      so a confirmation/restart flow built now would be exercised only by a synthetic second
      provider in a unit test, never by the real UI, which is exactly the kind of untested,
      premature surface this plan has avoided building at every other phase (P1's
      `IDriveAuthenticator`, P3's `IProviderPathSyntax` members). Restructuring `AppSettings` into
      a dictionary now, before OneDrive's actual settings shape (a token path, no `CliPath` at
      all) is known, risks designing the wrong shape and redoing it in P6 anyway. Both land
      together with P6, when there is a second real provider to build and verify them against.
      Verified: 573 tests pass (8 new — `ProviderCatalogTests`, `MainWindowProviderTests`, two
      `AppSettings.ActiveProviderOrDefault` cases), AOT publish clean, published binary runs
      against a stub CLI with no crash. The new settings-view label could not be visually
      confirmed in this sandbox (screenshot tooling unavailable here — `import`/`xwd` both failed
      to capture despite a live X display); the binding itself is covered by
      `MainWindowProviderTests.ActiveProviderDisplayName_ReflectsTheInjectedProvider`, but an
      actual look at the running window is still owed before calling this fully done.
- [x] **P6 (core paths live-verified)** — `OneDriveProvider` over Microsoft Graph, plus the
      P5 items that only made sense with a second real provider to build them against.
      Added `Services/Providers/OneDrive/`: `GraphAuthenticator` (authorization-code + PKCE via a
      loopback `HttpListener`, no MSAL, no device-code fallback — documented gap),
      `OneDriveTokenStore` (`onedrive-token.json`, chmod 600 — accepted plaintext risk, R3),
      `GraphHttpClient` (bearer attach, 401-refresh-retry-once, `Retry-After` honored on 429/503),
      `GraphErrorClassifier` (status + structured `error.code` → `DriveErrorKind`),
      `OneDriveOperations` (paginated listing, small-vs-chunked upload, async copy + polling,
      cached per-target move/copy id lookups), `OneDrivePathSyntax` (`OrdinalIgnoreCase`,
      the O6 reserved-name rule), `QuickXorHasher`, `OneDriveProvider` (the facade). Extended
      `IProviderPathSyntax` with `IsLocalNameMappableRemotely` (Proton: always true; OneDrive: the
      real rule) — wiring it into the upload path itself (a `LocalScanner`/`SyncExecutor`
      skip-with-reason) was **not** done: `LocalScanner` has no provider dependency to call it
      through, and adding one was wider surgery than this pass — an OneDrive upload of an
      unmappable local name still fails as a raw Graph 400 today rather than a clean skip; tracked
      as follow-up, not silently dropped. `ProviderCatalog` now registers OneDrive
      (`Available`/`Create`/`ResolveOrDefault`); `AppSettings` gained `OneDriveClientId` (entered
      in Settings, not embedded — a public-client id isn't secret, but keeping it out of the repo
      avoids tying the app to one person's app registration) and `IsOneDriveAuthenticated`, kept
      **separate** from Proton's `CliPath`/`IsAuthenticated` rather than unified into a
      provider-keyed structure — the two connection cards are structurally different enough that
      there was nothing to share; a real per-provider settings shape stays deferred, now to P7.
      Fixed a real gap this phase's own code surfaced: `App.axaml.cs` now computes `accountKey` as
      `{provider.Id}:default` (lowercased) and passes it to `DriveCacheService`/`SyncStateStore`/
      `FolderMetricsStore` — previously every store defaulted to `"proton:default"` unconditionally
      (P4's own doc comment had flagged this as owed once a second provider existed), so switching
      to OneDrive would have let its cache/sync-pair rows collide with Proton's under the same
      sentinel. UI: a provider picker in Settings → Connection (confirm + restart, §2.7 — no live
      hot-swap), Proton's and OneDrive's connection cards each `IsVisible`-gated on the active
      provider, the version/self-update rows gated on `HasDiagnostics` rather than a
      Proton-specific flag.

      `SyncPanelViewModel` gained an optional `providerDisplayName` constructor parameter
      (defaulting to `"Proton Drive"`, so every existing call site — tests above all — keeps
      working unchanged) and now interpolates it into its two Proton-named strings, per §5 item 3.

      **Deliberately not done, and why:** device-code auth fallback (no browser-less machine to
      support yet); the `IsLocalNameMappableRemotely` upload-path wiring above; a provider-keyed
      settings structure (P7, once multi-account forces the issue anyway); a full sweep of every
      remaining Proton-named string beyond the ones §5 item 3 named (e.g. context-menu labels) —
      cosmetic, parked via the `debt` skill rather than chased here.

      **Live-verified (R6), 2026-08-27 — see Appendix A for the full findings:** real sign-in
      (after discovering the Azure app registration needs its "Mobile and desktop applications"
      platform added explicitly — Appendix A #1), a real `ListFolderAsync("/")` against a live
      personal account (#2), and a real small-file upload. `QuickXorHasher` was **wrong on its
      first attempt** — an accumulator storage/wraparound bug that live verification caught: 18 of
      20 output bytes matched Graph's own reported hash, the first byte didn't. Root cause and fix
      in Appendix A #3; rewritten as a genuinely circular 160-bit bit array and **confirmed
      matching Graph's real `quickXorHash` on two separate uploads** after the fix. Not yet
      captured live: pagination past one page, chunked upload, async copy, rate-limiting, and the
      exact O6 reserved-name list — still per Microsoft's docs only, per Appendix A's "not yet
      captured" note.

      Verified: 715 tests pass, 6 skipped opt-in (75 new tests overall — `FakeHttpMessageHandler`-
      based unit tests plus the live integration test above; no real account needed for the
      unit-test count), 0 IL2xxx/IL3xxx trim/AOT warnings on a `linux-x64` self-contained publish
      (R4), the published binary launches and stays up.
- [ ] **P7** — *optional* multiple active accounts. Not started, deliberately last.
- [ ] **P8** — *optional* delta-based remote scanning where the provider supports it. Not started.

### Adversarial review of P1–P5 — 5 confirmed bugs fixed

An 8-angle adversarial review of the full P1–P5 diff found 5 CONFIRMED correctness bugs and 5
PLAUSIBLE cleanup/design issues. All 5 confirmed bugs are fixed, each with a regression test
verified to fail without the fix:

1. **`RemoteScanner`/`RemoteTreeWalker` case-collision leak.** The per-item collision check
   retracted a colliding folder from the scan's `result` dictionary, but `RemoteTreeWalker` had
   already queued that folder into the next BFS wave before the collision was even detected — so
   its children were still walked and added, leaking part of a folder that was supposed to be
   entirely excluded. Fixed by moving collision detection to a new `filterSiblings` hook on
   `RemoteTreeWalker.WalkAsync`, run once per sibling batch *before* any item reaches the
   per-node callback, so a colliding folder is never queued in the first place. Test:
   `RemoteScannerTests.OnACaseInsensitiveProvider_ACollidingFolder_IsNeverDescendedInto`.
2. **`ProviderId.OneDrive` could crash startup.** It's a real enum member (added in P1, ahead of
   P6) with no catalog entry; `AppSettings.ActiveProviderOrDefault`'s `Enum.TryParse` only catches
   a name this build has never heard of, not a valid id it can't build, so `App.axaml.cs` would
   call `ProviderCatalog.Create` uncaught and crash. Fixed by adding
   `IProviderCatalog.ResolveOrDefault`, which checks catalog membership (not just enum-parseability)
   before `App.axaml.cs` ever calls `Create`. Tests: `ProviderCatalogTests.ResolveOrDefault_*`.
3. **`SyncReconciler.DetectLocalMoves` skipped the P3 algorithm guard.** It compared a persisted
   baseline hash against a freshly-computed candidate hash by raw string equality, with no check
   that both came from the same `IContentHasher` — unlike `AreEquivalent`, ~50 lines away in the
   same file, which already had this guard. Fixed by reusing `IsAlgorithmMismatch` here too. Test:
   `AMoveIsRefused_WhenTheBaselineAndCandidateHashesCameFromDifferentAlgorithms`.
4. **`IsAlgorithmMismatch` treated `RemoteHashAlgorithm.None` as a real algorithm**, contradicting
   its own doc comment ("`None` or a missing tag is not treated as a mismatch: it means
   'unknown'"). Fixed by adding an `IsKnownAlgorithm` check so only two *known*, differing
   algorithms count as a mismatch. Test: `TwoWay_BothNew_OneSideTaggedNone_IsNotTreatedAsAnAlgorithmMismatch`.
5. **Stale skip-log message.** Once P3 broadened `RemoteScanner.NodeSkipped` to also fire for
   case collisions, `SyncExecutor`'s log handler still hardcoded the unmappable-name explanation
   ("its name contains '/'") for every skip, regardless of reason. Fixed by giving `NodeSkipped` a
   typed payload (`NodeSkip(string Name, NodeSkipReason Reason)`) instead of a bare string, so the
   log message can be accurate per reason. Test:
   `SyncExecutorTests.RunAsync_ACaseCollidingRemoteName_IsSkippedWithAnAccurateExplanation_NotTheSlashOne`.

The 5 PLAUSIBLE findings (hasher/remoteHashAlgorithm can drift apart, `SqliteMigrationRunner`
disables foreign keys for the whole migration run rather than just the rebuild statements, the
`"proton:default"` sentinel and the hash-tagging ternary are each duplicated across a few files,
and ~50 test call sites repeat provider-construction boilerplate) are cleanup/design items, not
correctness bugs, and are left for a future pass.

---

## 0. Executive summary

### 0.1 What the coupling actually is

Counting references is misleading here. Of the nine production files that name
`ProtonDriveService`, **six only ever call folder listing and the six mutation methods** — for them
the change is a type name (§1.A). The real work is the four things that are Proton-CLI-*shaped*
and would otherwise be forced onto OneDrive as false invariants:

| # | Proton-shaped assumption | Where | Why OneDrive breaks it |
|---|---|---|---|
| 1 | A remote op is "launch a process, read stdout, exit code ≠ 0 = failure" | `IProtonDriveCliExecutor`, `CliException`, `CliErrorClassifier`, the console in `MainWindowViewModel` | Graph is HTTP: status codes, JSON error bodies, `Retry-After`, token refresh. There is no process and no stdout. |
| 2 | Listings come from a cache the backend never revalidates, so the app must wipe it | `IProtonDriveCliExecutor.ResetRemoteCacheAsync`, `RemoteViewFreshnessPolicy`, `MainWindowViewModel:1119` | Graph answers from the service. There is nothing to invalidate, and calling a no-op "reset" per scan is harmless but the *freshness policy built on top of it* would keep paying for a problem that no longer exists. |
| 3 | Remote content hashes are SHA-1, comparable to a local SHA-1 | `LocalFileHasher.ComputeSha1Async`, `SyncReconciler:466`, `SyncBaselineWriter:123`, `SyncExecutor:417` | OneDrive returns `quickXorHash` on business/SharePoint drives and `sha1Hash`/`sha256Hash` on personal ones. Comparing a SHA-1 against a QuickXor silently reports "content changed" for every single file. |
| 4 | A path is `/a/b/c` with `/` inside a name escaped as `\/`, and `/` is the only unmappable character | `ProtonDriveService.CombinePath`, `HasUnmappableName`, `Sync/PathMapper` | OneDrive forbids `" * : < > ? / \ \|`, trailing spaces/dots and some reserved names, and addresses paths as `…/root:/a/b/c:` with URL encoding. Both the escaping *and* the "can this remote name exist locally / can this local name exist remotely" question are per-provider. |

Two more that are cheap to get wrong: all persisted state is keyed by remote path with **no account
column** (§1.B6), and the CLI version/update UI (`CliReleaseFeed`, `CliUpdateInstaller`,
`CliPlatformKey`, `CliVersionComparer`) is Proton-only and must not become a hole in the interface
that OneDrive has to stub out (§2.6).

### 0.2 The seam, in one line each

```
ICloudDriveProvider          the one thing the app depends on; a facade over the five below
  ├─ IDriveOperations        list / download / upload / trash / rename / create-folder / move / copy
  ├─ IDriveAuthenticator     sign in, sign out, "am I signed in", account identity
  ├─ IProviderPathSyntax     Combine, escaping, name mappability, comparison rules
  ├─ IRemoteViewInvalidator? null when the provider has no stale-cache problem (Graph)
  └─ IProviderDiagnostics?   null when there is no external binary to version/update (Graph)
ProviderCapabilities         a value: hash algorithm, server-side move/copy, delta, upload limits…
IProviderActivity            the console feed, provider-neutral (replaces the three Cli* events)
DriveException/DriveErrorKind  the typed error contract, renamed off "Cli"
IProviderCatalog             ProviderId → factory; what the settings dropdown enumerates
```

### 0.3 Ordering rationale

P1 before everything because it is the only step that is *provably* behavior-preserving (pure
renames plus one adapter), so it can land and be smoke-tested on its own with the real Proton CLI —
which is the last moment where "the app still works exactly as before" is a cheap thing to verify.

P2 and P3 next because they are the two places where adding OneDrive *first* would produce
plausible-looking wrong behavior rather than a compile error: an HTTP failure classified as
`Unknown` degrades every `catch` in `MainWindowViewModel`, and a hash-algorithm mismatch corrupts a
two-way sync silently. Both must exist before any OneDrive code can be trusted.

P4 (the account column) must precede P5/P6, because the first time a user switches provider with a
warm `cache.db` the browser cache, `FolderMetrics` and `SyncPairs` from the old account become
wrong-but-plausible rows. Doing it after OneDrive ships means a migration of dirty user data
instead of clean data.

P5 before P6 so the OneDrive work has a place to put its credentials and a way to be selected,
rather than being wired in behind a compile-time flag and re-wired later.

P8 (delta) is deliberately after P6: `/delta` is the single biggest win available for OneDrive
sync cost, but it changes the *scanner* contract for all providers, and doing it before OneDrive
works at all means designing that contract against an unimplemented backend.

---

## 1. Coupling inventory (current code)

### 1.A Name-only coupling — mechanical in P1

These hold a `ProtonDriveService` but only use provider-neutral operations. Each becomes a
constructor-type change to `ICloudDriveProvider` (or the narrower `IDriveOperations`).

| File | Uses |
|---|---|
| [Services/Sync/SyncExecutor.cs:28](../src/MyPersonalDrive/Services/Sync/SyncExecutor.cs#L28) | `LoadFolderAsync`, `TrashItemAsync`, `CreateFolderAsync`, `UploadFilesAsync`, `MoveItemAsync`, `RenameItemAsync`, `DownloadFileAsync` |
| [Services/Sync/RemoteScanner.cs:29](../src/MyPersonalDrive/Services/Sync/RemoteScanner.cs#L29) | `LoadFolderAsync`, `ResetRemoteCacheAsync`, `HasUnmappableName` (→ P3) |
| [Services/Sync/SyncBaselineWriter.cs:19](../src/MyPersonalDrive/Services/Sync/SyncBaselineWriter.cs#L19) | `LoadFolderAsync` |
| [Services/RemoteTreeWalker.cs:29](../src/MyPersonalDrive/Services/RemoteTreeWalker.cs#L29) | `LoadFolderAsync`, `ResetRemoteCacheAsync` |
| [Services/FolderStatsScanner.cs:26](../src/MyPersonalDrive/Services/FolderStatsScanner.cs#L26) | walks via `RemoteTreeWalker` |
| [ViewModels/Sync/SyncPanelViewModel.cs:89](../src/MyPersonalDrive/ViewModels/Sync/SyncPanelViewModel.cs#L89) | `GetRemoteFolderChildren` delegate — already a delegate, so it needs *nothing* but a doc-comment update |

`MainWindowViewModel` is in both lists: its ten operation calls are name-only, its CLI path /
version / update / console / auth surface is §1.B.

### 1.B Semantic coupling — the actual design work

- **B1 — Process execution model.** `IProtonDriveCliExecutor` (args list, `XDG_CACHE_HOME` per
  concurrency slot, 120 s default timeout, `Timeout.InfiniteTimeSpan` for `auth login`),
  `ProtonDriveCliLocator`, `AppSettings.CliPath`, `AppSettings.StrictListingParsing`. All of this
  stays — it just stops being *the* boundary and becomes `ProtonDriveProvider`'s private
  implementation detail. Nothing outside `Services/Providers/Proton/` may name it after P1.

- **B2 — Cache invalidation as a mandatory step.** `ResetRemoteCacheAsync` exists only because of
  PLAN-LOCAL-SYNC Appendix A #16 (a folder listed 17 children warm, 21 cold, and never healed).
  Becomes the optional `IRemoteViewInvalidator`; `RemoteScanner`/`RemoteTreeWalker` call it only
  when non-null, and `RemoteViewFreshnessPolicy` is constructed only for providers that expose one.

- **B3 — SHA-1 as the universal content hash.** `LocalFileHasher.ComputeSha1Async` is documented as
  "the same algorithm the CLI's `claimedDigests.sha1` reports"; `SyncReconciler.SameContent`
  (line 466) compares the two strings ordinally. Needs `ProviderCapabilities.RemoteHashAlgorithm`
  and a hasher chosen from it (§3.3).

- **B4 — Path syntax.** `ProtonDriveService.CombinePath` / `HasUnmappableName` are `public static`
  and called from `Sync/PathMapper`, `RemoteScanner:60` and `MainWindowViewModel:864,936`. Becomes
  `IProviderPathSyntax`, injected.

- **B5 — Auth model.** Today: `auth login` runs as a blocking child process with an infinite
  timeout, and `AppSettings.IsAuthenticated` is a local *guess* flipped by success/failure text.
  OAuth needs tokens, expiry, refresh, and an account identity — so the interface returns a status
  object, not a bool (§2.3), and `IsAuthenticated` stays only as a fast cached hint for
  `SyncScheduler`'s existing `isAuthenticated: () => …` gate.

- **B6 — Persisted state is not account-scoped.**
  `DriveItems.Path` is the primary key; `FolderMetrics.Path` is the primary key;
  `SyncPairs` has `UNIQUE(RemotePath, LocalPath)`; `AppSettings(Key)` is a flat KV table
  ([DriveDatabaseMigrations.cs](../src/MyPersonalDrive/Services/DriveDatabaseMigrations.cs)).
  `/my-files/Photos` means two different folders on two providers. Fixed in P4.

- **B7 — Stable node identity.** `DriveItem.NodeId` is the CLI `uid`, verified stable across rename
  and move (Appendix A #3) — the reconciler's move detection depends on it. Graph's `id` has the
  same property, so this assumption survives; it must be *documented* as a provider requirement so
  a future provider without stable ids is caught at design time, not by a corrupted baseline.

- **B8 — Concurrency and good-citizenship policy.** The executor's read-slot semaphore + per-slot
  private CLI cache exists to dodge `SQLITE_BUSY` (Appendix A #11). Graph's equivalent problem is
  HTTP 429 with `Retry-After`. Same *role*, entirely different mechanism → belongs inside each
  provider, expressed outward only as `ProviderCapabilities.MaxRecommendedConcurrency` for the
  scanners that already take a `maxConcurrency`.

- **B9 — Native AOT.** `PublishAot=true`, `TrimMode=partial`. The Microsoft Graph SDK and MSAL are
  reflection- and dynamic-code-heavy and are not a safe fit; P6 uses `HttpClient` +
  `JsonDocument` for reads and `AppJsonContext` source generation for anything serialized. See
  [aot-check](../.claude/skills/aot-check/SKILL.md).

- **B10 — The "one outbound call" invariant.** `AGENTS.md` states the app's *only* outbound network
  call is `CliReleaseFeed` and that adding a second is an architectural decision. P6 is exactly
  that decision; `AGENTS.md` and `ARCHITECTURE.md` §1 must be rewritten in the same change, not
  after it.

---

## 2. Target design (P1–P3 surface)

New namespace `MyPersonalDrive.Services.Providers`, with `Providers/Proton/` and
`Providers/OneDrive/` beneath it. Signatures below are the contract; they are what P1 lands.

### 2.1 `ICloudDriveProvider`

```csharp
public interface ICloudDriveProvider
{
    ProviderId Id { get; }                        // Proton | OneDrive
    string DisplayName { get; }                    // "Proton Drive"
    ProviderCapabilities Capabilities { get; }
    IProviderPathSyntax Paths { get; }
    IDriveAuthenticator Auth { get; }
    IDriveOperations Operations { get; }
    IRemoteViewInvalidator? RemoteView { get; }    // null ⇒ listings are always authoritative
    IProviderDiagnostics? Diagnostics { get; }     // null ⇒ nothing external to version/update
}
```

Kept as a facade rather than one flat interface because the consumers genuinely split along these
lines: the sync engine wants `Operations` + `Paths`, `MainWindowViewModel` wants all of it, and the
scanners want `Operations` + the optional `RemoteView`. A flat interface would make every fake in
`tests/` implement twenty members to test three.

### 2.2 `IDriveOperations`

One method per existing `ProtonDriveService` operation, same argument order, so P1 stays a rename:

```csharp
public interface IDriveOperations
{
    Task<IReadOnlyList<DriveItem>> ListFolderAsync(string path, CancellationToken ct = default);
    Task DownloadFileAsync(string path, string localFolder, CancellationToken ct = default);
    Task UploadFilesAsync(IReadOnlyList<string> localPaths, string parentPath,
                          UploadConflictStrategy strategy = UploadConflictStrategy.None,
                          CancellationToken ct = default);
    Task TrashItemAsync(string path, CancellationToken ct = default);
    Task RenameItemAsync(string path, string newName, CancellationToken ct = default);
    Task CreateFolderAsync(string parentPath, string name, CancellationToken ct = default);
    Task MoveItemsAsync(IReadOnlyList<string> paths, string targetParentPath, CancellationToken ct = default);
    Task CopyItemAsync(string sourcePath, string targetParentPath, string? newName = null,
                       CancellationToken ct = default);
}
```

`GetChildrenAsync` disappears — it is a same-signature alias of `LoadFolderAsync` today, and the
two names in one interface invite a provider to implement them differently.

`MoveItemAsync(single)` becomes an extension method over `MoveItemsAsync`, so a provider without
batch move (Graph: one `PATCH` per item) implements the batch by looping and the single call stays
free. `Capabilities.SupportsBatchMove` tells `SyncExecutor` whether batching is worth building.

### 2.3 `IDriveAuthenticator`

```csharp
public sealed record DriveAccount(string AccountId, string? DisplayName, string? Email);

public sealed record AuthStatus(bool IsSignedIn, DriveAccount? Account, DateTimeOffset? ExpiresAt);

public interface IDriveAuthenticator
{
    Task<AuthStatus> GetStatusAsync(CancellationToken ct = default);
    Task<AuthStatus> SignInAsync(IAuthPrompt prompt, CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
}
```

`IAuthPrompt` is how a provider asks the UI for the one thing it cannot do itself, with **no**
Avalonia types crossing the seam (an MVVM non-negotiable in `AGENTS.md`):

```csharp
public interface IAuthPrompt
{
    Task OpenBrowserAsync(Uri url, CancellationToken ct);           // loopback / auth-code flow
    Task ShowDeviceCodeAsync(string userCode, Uri verificationUri, CancellationToken ct); // fallback
}
```

Proton's implementation ignores the prompt entirely (the CLI opens its own browser) — which is the
proof the abstraction is at the right height: it describes *what the user must do*, not *who does it*.

`AccountId` is the value P4's schema column stores. Proton: the account email from
`ownedBy.email`, or a stable `"proton:default"` sentinel until a real identity is available (the
CLI exposes no whoami on `cli-drive@0.6.0` — **unverified beyond `--help`; confirm before P4**).
OneDrive: the Graph `driveId`, which is stable and does not change when the user renames anything.

### 2.4 `IProviderPathSyntax`

```csharp
public interface IProviderPathSyntax
{
    string Root { get; }                                   // "/my-files" | "/"
    string Combine(string parentPath, string name);         // moves ProtonDriveService.CombinePath here
    string GetParent(string path);
    bool IsRemoteNameMappableLocally(string name);          // was HasUnmappableName, inverted
    bool IsLocalNameMappableRemotely(string name);          // new: needed by OneDrive (§4.7)
    StringComparison Comparison { get; }                    // Ordinal | OrdinalIgnoreCase
}
```

`IsLocalNameMappableRemotely` has no Proton implementation beyond `true` and is the reason to add
it *in P3 rather than P6*: without it, an upload-only pair on OneDrive fails per-file at execution
time with a 400, instead of being reported once at scan time the way remote-side unmappables
already are in `RemoteScanner:60`.

`Comparison` matters because OneDrive is case-**insensitive** while Proton and Linux are
case-sensitive: `Photos/` and `photos/` are one remote folder and two local ones. `PathMapper` and
`SyncReconciler`'s dictionaries currently hard-code `StringComparer.Ordinal`. **Decision for P3:**
keep ordinal comparison everywhere, and have `RemoteScanner` *detect and report* a
case-collision on a case-insensitive provider as a skipped-node warning, exactly like an
unmappable name. Making the reconciler's comparer provider-dependent touches move detection,
baseline correlation and the queue's primary key, and is not worth it for a case the user can fix
by renaming.

### 2.5 `ProviderCapabilities`

```csharp
public sealed record ProviderCapabilities(
    RemoteHashAlgorithm RemoteHash,        // Sha1 | QuickXor | Sha256 | None
    bool SupportsServerSideMove,           // true both
    bool SupportsServerSideCopy,           // Proton: sync; OneDrive: async, see §4.5
    bool CopyIsAsynchronous,
    bool SupportsBatchMove,
    bool SupportsDelta,                    // false Proton, true OneDrive → P8
    bool RequiresRemoteViewInvalidation,
    long? MaxSingleShotUploadBytes,        // OneDrive: 4 MiB
    long? UploadChunkSizeBytes,            // OneDrive: multiple of 320 KiB
    int MaxRecommendedConcurrency,
    bool CanSetRemoteModificationTime);    // OneDrive: yes, via fileSystemInfo; Proton: implicit
```

`CanSetRemoteModificationTime` is not decoration: two-way sync compares claimed mtimes, and an
upload that lets the server stamp "now" makes the file it just uploaded look newer than the local
original on the next cycle.

### 2.6 Errors and activity (P2)

- `CliException` → `DriveException`, `CliErrorKind` → `DriveErrorKind`, adding `RateLimited` and
  `Conflict`. Existing member names and values are kept so the ~15 `catch` sites and
  `CliErrorClassifier`'s tests move by rename only. `CliErrorClassifier` stays, becomes
  Proton-internal, and gains a Graph sibling `GraphErrorClassifier` mapping status codes (§4.8) —
  the `AGENTS.md` rule "substring matching lives in one place" becomes "one place *per provider*".
- The three `CommandStarted/Output/Finished` events become one activity feed so the existing
  console keeps working for both providers:

```csharp
public enum ActivityKind { Started, Output, Finished }

public sealed record ProviderActivity(
    ActivityKind Kind, string Label, string? Text, bool IsError, int? ExitCode, TimeSpan? Duration);
```

  Proton maps `Label` to the command line it already formats; OneDrive maps it to
  `GET /me/drive/root:/Photos:/children → 200 (182 ms)`. `CommandLogBuffer` and
  `MainWindowViewModel.OnCommand*` adapt to the single event; `ExitCode` stays nullable rather than
  being faked from an HTTP status, so the console never invents a number.
- **`StrictListingParsing`** is Proton-only (it guards the text-parser fallback for a CLI that may
  ignore `--json`). It moves from `AppSettings` into the Proton provider's own settings section
  (§5.2) rather than staying a global the OneDrive path ignores.

### 2.7 Catalog and composition root

```csharp
public interface IProviderCatalog
{
    IReadOnlyList<ProviderDescriptor> Available { get; }          // for the settings dropdown
    ICloudDriveProvider Create(ProviderId id);                    // one active instance
}
```

`App.axaml.cs` keeps its hand-wiring (no DI container — an `AGENTS.md`/ARCHITECTURE §3 invariant);
it reads `settings.ActiveProvider`, calls `catalog.Create(...)` once, and passes the result where
`service` goes today. Switching provider requires an app restart in P1–P6 (§5.3) — rebuilding the
scheduler, watchers and executor live is a distinct problem and belongs to P7.

---

## 3. Phases

### P1 — Provider interfaces and the Proton adapter

**Adds** `Services/Providers/{ICloudDriveProvider,IDriveOperations,IDriveAuthenticator,IProviderPathSyntax,IRemoteViewInvalidator,IProviderDiagnostics,ProviderCapabilities,ProviderId,IProviderCatalog}.cs`
and `Services/Providers/Proton/ProtonDriveProvider.cs`.

**Moves** `ProtonDriveService`, `ProtonDriveCliExecutor`, `ProtonDriveCliLocator`,
`CliErrorClassifier`, `CliReleaseFeed`, `CliUpdateInstaller`, `CliPlatformKey`,
`CliVersionComparer` under `Services/Providers/Proton/` (namespace change; `ProtonDriveService`
keeps its name and its `--json` parser verbatim — it becomes the Proton provider's internals).

**Changes** the six §1.A call sites plus `MainWindowViewModel`'s ten operation calls, and
`App.axaml.cs`.

**Tests.** `FakeCliExecutor` stays (it tests the Proton provider at the CLI boundary, which is
still the right level). Add `Fakes/FakeDriveProvider.cs` — an in-memory `ICloudDriveProvider` — and
retarget `SyncExecutorTests` (33 references), `RemoteScannerTests`, `FolderStatsScannerTests`,
`SyncSchedulerTests` and the ViewModel tests at it. This is the bulk of P1's diff and its main
payoff: those tests stop needing a fake *CLI* to exercise sync logic.

**Done when** `./scripts/run-tests.sh` is green, `MYPERSONALDRIVE_INTEGRATION=1` real-CLI tests
still pass, the [smoke-test](../.claude/skills/smoke-test/SKILL.md) checklist passes, and
`grep -rn "ProtonDrive\|Cli" src --include=*.cs | grep -v Services/Providers/Proton` returns only
`AppSettings.CliPath` (removed in P5) and the settings view bindings.

### P2 — Generalize errors and the console

`DriveException`/`DriveErrorKind`, the `ProviderActivity` feed, `CommandLogBuffer` and
`MainWindowViewModel.FormatCliError` → `FormatDriveError` (its `NotAuthenticated` copy stops saying
"the CLI"). `StrictListingParsing` moves to the Proton settings section.

**Done when** no type outside `Providers/Proton` contains `Cli` in its name, and
`CliErrorClassifierTests` passes unchanged apart from the rename.

### P3 — Path syntax and content hash

- `IProviderPathSyntax` + `ProtonPathSyntax` (the current `CombinePath`/`HasUnmappableName` bodies,
  moved, still `\/`-escaping); inject into `PathMapper`, `RemoteScanner`, `MainWindowViewModel`.
- `IContentHasher` chosen from `Capabilities.RemoteHash`: `Sha1ContentHasher` (existing
  `LocalFileHasher` body) and, in P6, `QuickXorContentHasher`. `SyncBaselineWriter:123`,
  `SyncExecutor:417` take the hasher via constructor.
- `SyncReconciler.SameContent` gains an explicit precondition doc + a guard: when either side's
  hash is present but the two were produced by different algorithms, fall back to size+mtime rather
  than declaring a difference. The algorithm therefore has to be *stored*, which is why P4 adds
  `SyncState.HashAlgorithm`.
- `RemoteScanner` reports case-collisions as skipped nodes on case-insensitive providers (§2.4).

**Done when** `SyncReconcilerTests` covers "same content, different algorithm ⇒ not a change" and
`PathMapperTests` runs against an injected syntax.

### P4 — Account-scope the persisted state (`cache.db` migration 6)

```sql
-- migration 6
ALTER TABLE DriveItems     ADD COLUMN AccountKey TEXT NOT NULL DEFAULT 'proton:default';
ALTER TABLE FolderMetrics  ADD COLUMN AccountKey TEXT NOT NULL DEFAULT 'proton:default';
ALTER TABLE SyncPairs      ADD COLUMN AccountKey TEXT NOT NULL DEFAULT 'proton:default';
ALTER TABLE SyncState      ADD COLUMN HashAlgorithm TEXT;   -- null = legacy Sha1, see P3
```

`AccountKey` is `"<providerId>:<accountId>"` from §2.3. The default backfills every existing row to
the Proton account the user already has, so an upgrade is a no-op for them.

`DriveItems` and `FolderMetrics` are keyed by `Path` alone today, so the primary key has to be
rebuilt (`CREATE TABLE … PRIMARY KEY (AccountKey, Path)` + `INSERT … SELECT` + drop/rename, inside
the migration's transaction — SQLite cannot alter a primary key in place). `SyncPairs` needs
`UNIQUE(AccountKey, RemotePath, LocalPath)`, which likewise means a table rebuild.
`SqliteMigrationRunner` already applies these in order; the rebuild must be written so a partially
applied migration cannot leave both tables (see how migration 3 is structured).

`DriveCacheService`, `FolderMetricsStore` and `SyncStateStore` take the active `AccountKey` in
their constructor and add it to every `WHERE`/`INSERT`. **This is the phase where a missed query is
a silent cross-account data leak**, so the check is mechanical: every `CommandText` in those three
files must mention `AccountKey`.

`AppSettings` (the SQLite KV table, migration 4) stays global — its only current key is the sync
on/off toggle, which is an app-level preference, not per account. Revisit in P7.

### P5 — Provider selection in settings

- `AppSettings.ActiveProvider` (string, same "store the enum name, degrade to default" pattern as
  `ViewMode` — see [AppSettings.cs](../src/MyPersonalDrive/Services/AppSettings.cs)), default
  `nameof(ProviderId.Proton)`. Add to `AppJsonContext` if a new type is introduced.
- `MainWindowViewModel` gains `AvailableProviders` + `SelectedProvider`; the settings view's
  connection card becomes provider-driven: the CLI-path row, version row and update rows are bound
  to `IsProtonSelected` (they are `Diagnostics is not null` in truth), and a OneDrive card takes
  their place. `MainWindow.axaml:586–640` is the region that changes.
- Switching provider with sync pairs configured must be blocked or explicit: show what will be
  affected and require confirmation, then persist and prompt for restart. Never silently
  re-point existing pairs at a different account.
- `AppSettings.CliPath`/`IsAuthenticated` become provider-scoped: `ProviderSettings` dictionary
  keyed by provider id, with a one-time migration of the two legacy top-level properties.

### P6 — OneDrive provider

See §4. Lands `Services/Providers/OneDrive/` and nothing outside it except catalog registration,
`AppJsonContext` entries for the token/settings records, and the doc updates in §1.B10.

### P7 — *Optional:* more than one account active at once

Everything above already stores `AccountKey`, so this is: `ICloudDriveProvider` instances become a
keyed collection; `SyncScheduler` gets a per-account authentication gate; the browser gets an
account switcher and a per-account `CurrentPath`; `SyncPanelViewModel` groups pairs by account.
The genuinely new problems are the ones no schema column solves: a global concurrency budget across
providers, and one console feed carrying two providers' activity. Not scoped here.

### P8 — *Optional:* delta-based remote scanning

`Capabilities.SupportsDelta` + `IDeltaSource { Task<DeltaPage> GetChangesAsync(string? token) }`,
consumed by `RemoteScanner` when available, falling back to the full walk when the provider
returns "token expired". Worth its own plan: it changes what a "scan" *is*, and the baseline
correlation in `SyncReconciler` assumes a complete tree snapshot.

---

## 4. OneDrive (O) — Microsoft Graph design

> **Provenance.** Everything in this section comes from Microsoft's public Graph documentation, and
> is **not verified against a live tenant from this repo**. Per `AGENTS.md` ("never invent output
> shapes"), each item marked *(unverified)* must be confirmed with a real capture before code
> depends on it, and the confirmed shapes recorded in Appendix A of this document — the same way
> PLAN-LOCAL-SYNC Appendix A records the Proton CLI's real behavior.

### 4.1 Why not a CLI

There is no first-party OneDrive CLI to shell out to, so the "launch a process, parse stdout"
model does not transfer. `rclone` was considered as a universal backend and rejected: it would make
a third-party binary a hard dependency of a second provider, it hides exactly the per-item metadata
(stable id, claimed hash, claimed mtime) the reconciler depends on, and its own abstraction is at
the wrong height — the app would end up parsing `rclone lsjson` and inheriting a second unverified
output contract. Direct Graph REST it is.

### 4.2 App registration and auth (O1)

- Azure app registration, **public client** (no secret shipped in the binary), redirect URI
  `http://localhost` (loopback, dynamic port). Multi-tenant + personal accounts
  (`/common` authority) so both work/school and personal OneDrive sign in.
- Scopes: `Files.ReadWrite.All offline_access User.Read`. `offline_access` is what yields a refresh
  token; without it the app re-prompts every hour.
- Flow: **authorization code + PKCE** on a loopback `HttpListener`, opened via `IAuthPrompt.OpenBrowserAsync`.
  Device-code as the fallback for a machine with no usable browser (`ShowDeviceCodeAsync`).
- No MSAL (B9): the two flows are ~150 lines of `HttpClient` against
  `https://login.microsoftonline.com/common/oauth2/v2.0/{authorize,token}`.
- **Token storage.** Refresh + access token in `AppSettingsService.BaseFolder/onedrive-token.json`,
  `chmod 600`, written via `AppJsonContext`. **Risk, stated plainly:** that is at-rest plaintext.
  The alternatives are a libsecret P/Invoke (a native dependency and an AOT/packaging cost) or
  DPAPI (Windows-only). Recommendation: ship 0600-plaintext for the first version, matching where
  the Proton CLI keeps its own session, and record it as a known limitation in `ARCHITECTURE.md`.
- Refresh: on 401, refresh once and retry the request once; a second 401 is
  `DriveErrorKind.NotAuthenticated` and flips the cached `IsAuthenticated` hint, reusing the exact
  path `MainWindowViewModel:1188` already has.

### 4.3 Operation mapping (O2)

Paths are addressed as `/me/drive/root:/{path}:` (`{path}` percent-encoded, `:` omitted at root).

| `IDriveOperations` | Graph request | Notes |
|---|---|---|
| `ListFolderAsync` | `GET /me/drive/root:/{path}:/children?$select=id,name,size,file,folder,fileSystemInfo,parentReference,shared,createdBy&$top=200` | Must follow `@odata.nextLink` to exhaustion — a partial listing reads as a remote deletion, the same failure mode as Appendix A #16 *(unverified: default page size)* |
| `DownloadFileAsync` | `GET …/{path}:/content` | 302 to a pre-authenticated URL; follow it **without** the auth header |
| `UploadFilesAsync` (< 4 MiB) | `PUT …/{parent}:/{name}:/content?@microsoft.graph.conflictBehavior=…` | |
| `UploadFilesAsync` (≥ 4 MiB) | `POST …:/createUploadSession` then `PUT` ranges | Chunks a multiple of 320 KiB; session URL needs no auth header |
| `TrashItemAsync` | `DELETE …/{path}:` | Goes to the recycle bin — matches "trash", not "delete" |
| `RenameItemAsync` | `PATCH …/{path}:` body `{"name":"…"}` | |
| `CreateFolderAsync` | `POST …/{parent}:/children` body `{"name":…,"folder":{},"@microsoft.graph.conflictBehavior":"fail"}` | |
| `MoveItemsAsync` | `PATCH …/{path}:` body `{"parentReference":{"id":"…"}}`, one per item | needs the target folder's id → one extra `GET` per distinct target, cached per operation. `SupportsBatchMove = false` |
| `CopyItemAsync` | `POST …/{path}:/copy` → `202` + `Location` monitor URL | **Asynchronous.** `CopyIsAsynchronous = true`; the provider polls the monitor URL until `completed`/`failed` so the caller keeps a synchronous `Task` |
| version / update | — | `Diagnostics = null`: there is no external binary. The settings UI hides those rows (§5) |
| cache invalidation | — | `RemoteView = null`: Graph listings are served by the service |

Conflict strategy mapping: `None`/`Skip` → `fail`, `Replace` → `replace`, `KeepBoth` → `rename`.
Note the asymmetry to record in `ARCHITECTURE.md`: Proton's `skip` silently succeeds, Graph's
`fail` returns 409 → the provider translates a 409 on a `Skip` upload into success, and anything
else into `DriveErrorKind.AlreadyExists`.

### 4.4 `DriveItem` mapping (O3)

| `DriveItem` field | Graph source |
|---|---|
| `NodeId` | `id` (stable across move/rename — the B7 requirement) |
| `Name` | `name` |
| `IsFolder` | presence of `folder` facet |
| `Size` | `size` |
| `ModifiedAt` | `fileSystemInfo.lastModifiedDateTime` — the *client-claimed* time, the true analogue of Proton's `claimedModificationTime`; **not** the top-level `lastModifiedDateTime`, which is server-side |
| `ContentHash` | `file.hashes.quickXorHash` (business/SharePoint) or `file.hashes.sha1Hash`/`sha256Hash` (personal) |
| `Owner` | `createdBy.user.email`/`displayName` |
| `IsShared` | presence of the `shared` facet |
| `Path` | built by `OneDrivePathSyntax`, not read from `parentReference.path` (which is URL-encoded and prefixed `/drive/root:`) |

**Hash consequence (O4).** The available algorithm depends on the drive type, which is only known
after sign-in — so `ProviderCapabilities` cannot be a compile-time constant for OneDrive. Either
capabilities become available-after-auth, or the provider reports `RemoteHash = QuickXor` and
prefers `quickXorHash` when present, falling back to `Sha1` per item with the algorithm recorded in
`SyncState.HashAlgorithm` (P4). **Recommendation:** the latter — per-item, recorded — because it is
the only version that survives a user with both a personal and a work drive. Requires implementing
QuickXorHash locally (Microsoft publishes the algorithm; ~50 lines, needs its own unit test against
a published vector) *(unverified: which hashes a personal drive returns today; Microsoft has been
retiring `sha1Hash`)*.

### 4.5 Two-way sync specifics (O5)

- Set `fileSystemInfo.lastModifiedDateTime` on upload (`CanSetRemoteModificationTime = true`), or
  every uploaded file looks remotely-newer on the next cycle.
- `SyncEchoSuppressor` needs no change: it is keyed on local paths the app itself writes, which is
  provider-independent.
- The `SyncExecutor` download path writes to a temp dir then moves into place — unchanged.
- Rate limiting replaces slot management: a shared `HttpClient`, a small concurrency gate from
  `MaxRecommendedConcurrency`, and mandatory `Retry-After` honoring on 429/503. This is the
  provider's analogue of the `SQLITE_BUSY` handling and belongs in the same conceptual slot as
  `SyncRetryPolicy`'s `Busy` branch — reuse `DriveErrorKind.RateLimited` there rather than adding a
  second retry mechanism.

### 4.6 Name mappability (O6)

`OneDrivePathSyntax.IsLocalNameMappableRemotely` rejects: any of `" * : < > ? / \ |`, a leading or
trailing space, a trailing `.`, the names `.lock`, `CON`, `PRN`, `AUX`, `NUL`, `COM0`–`COM9`,
`LPT0`–`LPT9`, `desktop.ini`, and any name starting `~$` *(unverified against the live service;
confirm the current list before relying on it)*. `IsRemoteNameMappableLocally` rejects only `/`,
as on Linux today. Both directions report a skipped node with a reason, never a silent drop —
the rule `RemoteScanner:60` already follows.

### 4.7 Error mapping (O7) — `GraphErrorClassifier`

| Signal | `DriveErrorKind` |
|---|---|
| 401, or `InvalidAuthenticationToken` after one refresh | `NotAuthenticated` |
| 403 `accessDenied` | `PermissionDenied` |
| 404 `itemNotFound` | `NotFound` |
| 409 `nameAlreadyExists` | `AlreadyExists` |
| 400 `invalidRequest` / `malformedIdentifier` | `InvalidArgument` |
| 429, 503 + `Retry-After` | `RateLimited` |
| 507 `insufficientStorage`, `quotaLimitReached` | `Quota` |
| `HttpRequestException`, DNS, TLS | `Network` |
| `TaskCanceledException` from the client timeout | `Timeout` |

Graph error bodies are `{"error":{"code":…,"message":…}}` — read the **code**, not the message.
That is strictly better than the Proton path (which has to substring-match) and the classifier
should not imitate it.

### 4.8 Testing (O8)

- `FakeHttpMessageHandler` + recorded Graph JSON fixtures under `tests/.../Fixtures/OneDrive/`,
  captured from a real drive and scrubbed. No hand-authored response shapes.
- Unit tests: path building/encoding, paging exhaustion, conflict-behavior mapping, error mapping,
  the 401-refresh-retry-once path, chunked-upload boundary arithmetic, QuickXorHash vectors.
- `MYPERSONALDRIVE_ONEDRIVE_INTEGRATION=1` opt-in integration tests mirroring
  `Integration/RealCli*Tests`, against a throwaway account and a dedicated test folder.
- `FakeDriveProvider` (P1) means none of the sync-engine tests need any of this.

---

## 5. UI changes (P5)

1. **Settings → Connection** becomes a provider picker plus one provider card. Proton's card is
   today's rows (CLI path + browse, authenticate, version, update). OneDrive's card is: sign-in
   button, signed-in account, sign-out, and nothing else.
2. Bind the diagnostics rows to a single `HasDiagnostics`-style flag rather than
   `IsProtonSelected`, so a third provider needs no XAML change.
3. Strings currently naming Proton (`"Select a Proton Drive CLI executable to begin."`,
   `SyncPanelViewModel`'s `"…from Proton Drive."`, the `MainWindow.axaml` tooltips) become
   provider-name interpolations. There is one Spanish/English mix in the context menus already —
   leave it; changing it here is scope creep, park it via [debt](../.claude/skills/debt/SKILL.md).
4. Provider switch = confirm + restart (§2.7).

---

## 6. Risks

- **R1 — P4's rebuilt primary keys** touch a database holding real sync baselines. A wrong
  migration turns into phantom deletes on the next cycle. Mitigation (executed, not just
  planned): tested against a copy of this machine's real warm `cache.db` first, which is exactly
  what caught two real cascade-delete/dangling-FK bugs in the original migration SQL (see P4's
  status entry) before they ever touched real data; backed up to
  `~/.config/MyPersonalDrive.pre-p4-backup` before running the fixed migration for real.
- **R2 — hash-algorithm mismatch** is silent and destructive (§0.1 #3). Mitigation: P3's
  different-algorithm guard lands *before* any OneDrive code, with the test named in P3's "done when".
- **R3 — plaintext refresh token** (§4.2). Accepted with a documented limitation, revisit if the
  app ever ships beyond single-user Linux.
- **R4 — Native AOT regression** from the first real HTTP/JSON serialization surface. Mitigation:
  run [aot-check](../.claude/skills/aot-check/SKILL.md) as part of P6, not at release time.
- **R5 — scope drift into P7.** Multi-account is where an "extract an interface" change turns into
  a rewrite. The guard is P4: as long as `AccountKey` is stored, P7 stays additive and can be
  refused indefinitely without cost.
- **R6 — unverified Graph shapes.** Every *(unverified)* marker in §4 is a place where code written
  from documentation could be plausibly wrong. None of them may be implemented before a real capture
  lands in Appendix A.

## 7. Explicitly out of scope

- Google Drive. The seam is designed so it *can* be added (its listing is id-based rather than
  path-based, which is the one thing `IProviderPathSyntax` would have to grow a mode for), but no
  Google-specific code, scope or registration is planned here.
- Copying or moving items *between* providers.
- SharePoint document libraries and shared/other-user drives beyond `/me/drive`.
- Provider-specific features with no equivalent in the other: Proton sharing links, OneDrive
  version history, Personal Vault.
- Replacing the hand-wired composition root with a DI container.
- Windows/macOS packaging changes.

---

## Appendix A — Verified OneDrive/Graph behavior

Captured 2026-08-27, live session against a real personal Microsoft account, via
`tests/MyPersonalDrive.Tests/Integration/RealOneDriveAuthTests.cs`
(`MYPERSONALDRIVE_ONEDRIVE_INTEGRATION=1`).

1. **Loopback redirect URI needs the "Mobile and desktop applications" platform explicitly added
   in the Azure app registration, registered as the port-less `http://localhost`.** The first
   attempt failed with `invalid_request: The provided value for the input parameter 'redirect_uri'
   is not valid` even though the app registration existed — the registration has to add that
   specific platform (Authentication → Add a platform → Mobile and desktop applications) before
   `http://localhost:{any port}` requests are accepted. Once added, sign-in worked immediately with
   `redirect_uri=http://localhost:{dynamic port}/` (confirmed across three different dynamically
   allocated ports on three separate runs) — confirms §4.2's design as built in
   `GraphAuthenticator`, with this one prerequisite spelled out for anyone repeating the setup.
2. **`ListFolderAsync("/")` against a real account's root**: returned successfully, 41 children,
   ordinary names (`Documents`, `Desktop`, `.Trash-1000`, etc.) — no surprises in the response
   shape versus §4.3's documented `$select` fields; `GraphDriveItem`'s mapping needed no changes.
3. **`quickXorHash` — the first `QuickXorHasher` implementation was wrong**, confirmed by comparing
   its local output against Graph's own reported hash for an uploaded file: 18 of the resulting 20
   bytes matched exactly, but the first byte differed by a few bits. Root cause: that
   implementation stored the accumulator as `ulong[3]` (192 bits of storage) and only handled
   overflow across a 64-bit array-element boundary — it missed that the algorithm's accumulator is
   circular over exactly **160 bits**, not 192, so a byte whose 8-bit span crosses the 160-bit
   wraparound point while still sitting entirely inside one 64-bit array element (shift positions
   154, 159, 153, 158 for short inputs, i.e. bytes 14/29/43/58 mod 160 at `Shift=11`) had its
   overflow bits silently dropped into unused storage past byte 19 instead of folding back to bit
   0. Rewritten as a genuinely circular 160-bit (20-byte) bit array with a per-bit XOR helper
   (`QuickXorHasher.QuickXorState`, see its doc comment) — **confirmed matching Graph's own
   `quickXorHash` on three separate real uploads** after the fix, one of them with fixed,
   known content (`QuickXorGoldenVector.Content`, 81 bytes) pinned as a permanent unit test
   (`QuickXorHasherTests.KnownGoldenVector_MatchesGraphsRealQuickXorHash`) so this exact bug class
   can never silently regress without a live account. `RemoteHashAlgorithm` field
   present: `quickXorHash` was populated for every file checked; `sha1Hash`/`sha256Hash` were not
   inspected on this personal account (§4.4's "unverified: which hashes a personal drive returns
   today" note stays open for that specific sub-question, but is moot for this app either way since
   `OneDriveOperations.ToDriveItem` only ever reads `quickXorHash`).
4. **Upload (small-file path)**: `PUT .../content?@microsoft.graph.conflictBehavior=replace`
   succeeded for a short text file and the resulting item's `quickXorHash` was readable on the very
   next listing call with no propagation delay observed.
5. **Trash**: `DELETE .../{path}:` on the uploaded test file succeeded with no error, run as
   best-effort cleanup after the test's assertions.
6. **`MainWindowViewModel._rootPath` was hardcoded to `"/my-files"` app-wide** — Proton's own root
   folder name, not a generic convention. Caught by hand (not the integration test, which talks to
   `OneDriveOperations` directly and never exercises the view model): the real app, switched to
   OneDrive and signed in successfully via the UI, tried to browse `/my-files` on launch and got a
   real `[fail]` from `GET /my-files/children` with a "path no longer exists" warning — OneDrive
   404s a nonexistent path exactly as expected, the bug was assuming that path existed at all.
   Fixed: `_rootPath` (and the initial `_currentPath`) are now computed from `provider.Id` in
   `MainWindowViewModel`'s constructor (`"/"` for OneDrive, `"/my-files"` for Proton), covered by
   `MainWindowProviderTests.RootPath_ForOneDrive_IsSlash`/`RootPath_ForProton_IsMyFiles`.

**Not yet captured**: pagination (`@odata.nextLink`) against a folder with more than `$top=200`
children, chunked upload for a file over 4 MiB, async copy monitoring, 429/503 rate-limiting
in practice, and the exact reserved-name list in §4.6/O6 (still per Microsoft's documentation, not
tested against the live service). Each remains marked (unverified) until captured.

## Appendix B — File-by-file change inventory

| File | P1 | P2 | P3 | P4 | P5 | P6 |
|---|:-:|:-:|:-:|:-:|:-:|:-:|
| `App.axaml.cs` | ● | ● | ● | ● | ● | ● |
| `Services/ProtonDriveService.cs` → `Providers/Proton/` | ● | | ● | | | |
| `Services/ProtonDriveCli{Executor,Locator}.cs` → `Providers/Proton/` | ● | ● | | | | |
| `Services/CliErrorClassifier.cs`, `CliException.cs`, `CliErrorKind.cs` | ● | ● | | | | |
| `Services/Cli{ReleaseFeed,UpdateInstaller,PlatformKey,VersionComparer}.cs` | ● | | | | ● | |
| `Services/CliCommandEventArgs.cs` → `ProviderActivity` | | ● | | | | |
| `Services/RemoteViewFreshnessPolicy.cs` | ● | | | | | |
| `Services/RemoteTreeWalker.cs`, `FolderStatsScanner.cs` | ● | | | | | |
| `Services/DriveCacheService.cs`, `FolderMetricsStore.cs` | | | | ● | | |
| `Services/DriveDatabaseMigrations.cs` | | | | ● | | |
| `Services/AppSettings.cs`, `AppSettingsService.cs`, `AppJsonContext.cs` | | ● | | | ● | ● |
| `Services/Sync/SyncExecutor.cs` | ● | | ● | | | |
| `Services/Sync/RemoteScanner.cs` | ● | | ● | | | |
| `Services/Sync/SyncBaselineWriter.cs` | ● | | ● | ● | | |
| `Services/Sync/PathMapper.cs` | | | ● | | | |
| `Services/Sync/SyncReconciler.cs` | | | ● | ● | | |
| `Services/Sync/LocalFileHasher.cs` → `IContentHasher` | | | ● | | | |
| `Services/Sync/SyncStateStore.cs` | | | | ● | | |
| `Services/Sync/SyncRetryPolicy.cs` | | ● | | | | ● |
| `ViewModels/MainWindowViewModel.cs` | ● | ● | ● | | ● | |
| `ViewModels/CommandLogBuffer.cs` | | ● | | | | |
| `ViewModels/Sync/SyncPanelViewModel.cs` | ● | | | | ● | |
| `Views/MainWindow.axaml(.cs)` | | | | | ● | |
| `AGENTS.md`, `docs/ARCHITECTURE.md` | ● | ● | ● | ● | ● | ● |
| `.claude/skills/cli-command/SKILL.md` (becomes Proton-scoped) | ● | | | | | |
