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
> Companions: [ARCHITECTURE.md](ARCHITECTURE.md) (current state, commit `8637915`),
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
- [x] **P7, Phase A** — Proton and OneDrive active and syncing at once, no restart. Scope
      narrowed from the original sketch: exploration found Proton's CLI has no multi-account
      concept of its own (`auth login`/`auth logout` against one CLI installation), so true
      multiple accounts of the *same* provider stays out of scope (would need CLI
      config-directory isolation) — the real shape here is at most one session per provider
      *type*, both active together. This also removed the "global concurrency budget" problem the
      original sketch worried about: `SyncScheduler`'s per-instance one-cycle-at-a-time semaphore
      exists to stop two *same-provider* CLI processes from crashing each other, and with exactly
      one session per provider that risk doesn't exist between a Proton scheduler and a OneDrive
      scheduler — no new cross-instance gate was needed.

      `App.axaml.cs` now builds an `AccountSyncContext` per provider `ProviderCatalog.Available`
      lists (both constructed unconditionally — cheap and side-effect-free even unconfigured, real
      failures surface lazily on first use), picks one as "primary" (the persisted
      `ActiveProvider` preference, browsed by `MainWindowViewModel` same as before), and wires
      every other context's sync engine + console activity alongside it.
      `SyncPanelViewModel.AddAccount` merges a second account's pairs into the same `Pairs` list
      (each row labeled via `SyncPairViewModel.AccountLabel`, blank when there's only one
      account), with its own independent `AccountSyncToggleViewModel` — pausing one account's
      automatic sync doesn't touch the other's. `MainWindowViewModel.ObserveAdditionalProviderActivity`
      tags console lines by account (`[OneDrive] GET …`), and `CommandLogBuffer`'s cap doubled
      (200→400) since one buffer now serves two interleaved sources.

      **A live test of the actual UI (not just the Graph-level integration test) surfaced two real
      bugs, both fixed:**
      1. `SyncStateStore.GetAutomaticSyncEnabledAsync`/`SetAutomaticSyncEnabledAsync` read/wrote a
         single **unscoped** row in the shared `cache.db` — toggling one account's automatic sync
         silently toggled every account's. Fixed by scoping the key per `AccountKey`, with a
         one-time fallback to the old unscoped key *only* for `"proton:default"` so an existing
         single-Proton install's saved on/off choice survives the upgrade. Covered by
         `SyncStateStoreTests`.
      2. The explorer header ("Proton Drive browser", "Point the app at the Proton Drive CLI…")
         was a hardcoded string — harmless when Proton was the only provider, actively misleading
         once OneDrive could be the browsed account (a real screenshot showed "Proton Drive
         browser" over a OneDrive folder listing). Fixed: `MainWindowViewModel.BrowserHeaderTitle`/
         `BrowserHeaderSubtitle` are provider-neutral, computed from whichever provider is
         actually browsed.

      **Deliberately not done (Phase B):** the account-switcher *browsing* UI — extracting
      `RootItems`/`CurrentPath`/breadcrumbs/selection/viewer/metrics out of `MainWindowViewModel`
      into a per-account object with a `SelectedAccount` the view rebinds to, so which account
      you're browsing can change without a restart. The Settings provider picker still requires a
      restart to change what's *browsed* (sync/console already run for both regardless). Also not
      done: any real same-provider multi-account support, and an "add account" flow beyond what
      Settings already offers (both providers' settings already coexist independently).

      Verified: 726 tests pass (12 new — `SyncPanelMultiAccountTests`,
      `SyncStateStoreTests`'s two new account-scoping cases), manual test with both providers
      configured and authenticated for real: app launches with no crash, two independent
      automatic-sync toggles appear and behave independently, sync pairs on each account are
      correctly labeled, and the header correctly reads "OneDrive browser" when OneDrive is the
      browsed account.
- [ ] **P7, general form** — *optional* true multiple accounts of the same provider (needs Proton
      CLI config-directory isolation). Not started, deliberately last, and likely never needed
      given Phase A's scope already covers "different providers active together."
- [x] **P7, Phase B** — the account-switcher *browsing* UI Phase A deliberately deferred: change
      which account `MainWindowViewModel` browses live, no restart. Implemented with a smaller
      diff than originally sketched (mutable fields + `SwitchBrowserAccountAsync`, not a constructor
      reshape) and one scope cut (the Settings-card decoupling/browser-toolbar switcher was not
      attempted) — see [§P7 Phase B](#p7-phase-b--live-account-switch-for-the-browser--implemented)
      for what shipped and why.
- [x] **P8 (implementation landed, live verification pending)** — delta-based remote scanning for
      OneDrive (`IDeltaSource`, `DeltaRemoteScanner`, per-pair delta tokens); Proton has no
      delta/events command and stays on the full walk, same as a one-way pair of any provider
      (never populates the baseline a delta scanner needs to merge onto). See
      [the P8 phase entry below](#p8--optional-delta-based-remote-scanning--implemented-pending-live-verification)
      for the shipped shape. **Not yet
      done:** live verification against a real Graph account (sign in, run a delta cycle, make a
      real change, confirm the next call reports exactly it and a two-way pair picks it up) —
      still pending that session.
- [x] **P9** — filter the Sync window's pair list by account/provider, using the same filter-chip
      idiom the folder browser already uses for file kinds (`ProviderFilterViewModel`,
      `SyncPanelViewModel.VisiblePairs`). See [§P9](#p9--filter-sync-pairs-by-provider).
- [x] **P10 — Google Drive provider.** Design approved 2026-09-04 (§8's OAuth scope cost and the
      client-side conflict-strategy gap both explicitly accepted); Google Cloud Console side
      (project `my-personal-drive-507613`, Drive API enabled, `drive` scope on the OAuth consent
      screen, a Desktop-app OAuth client, the developer's own account added as a test user) set up
      and confirmed by the user the same day. `Services/Providers/GoogleDrive/` implemented per
      [§8](#8-google-drive-g--rest-api-v3-design)'s signed-off design — see its own phase entry
      below for what shipped and where it deviated. Unit tests green
      (`./scripts/run-tests.sh`), AOT-clean (no new IL2xxx/IL3xxx). **Live verification against a
      real Google account is still pending** — that happens in a separate follow-up session with
      real credentials, same as P8's own entry records for its own pending live pass.

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

### P7 Phase B — live account switch for the browser — implemented

Phase A deliberately deferred this (see its own "Deliberately not done (Phase B)" note above):
switching which account `MainWindowViewModel` *browses* still persists `AppSettings.ActiveProvider`
and asks for a restart (`MainWindowViewModel.SwitchProviderAsync`) — even though, since Phase A,
both accounts' sync engines and console activity already run all the time regardless of which one
is on screen. The restart is a pure browsing-UI limitation, not a sync one.

**Root cause, concretely.** `MainWindowViewModel` is constructed once, at startup, against a single
provider's whole toolchain — not just `_provider` itself, but `_cacheService`, `_statsScanner`,
`_previewLoader`, `_imagePreviewLoader`, and browsing state (`_navigationHistory`, `_currentPath`,
`_loadedItems`, `KindFilters`, selection). `_isAuthenticated` is worse: it's read once from
`AppSettingsService` at construction (`_provider.Id == ProviderId.OneDrive ? settings...IsOneDriveAuthenticated : settings...IsAuthenticated`)
and never revisited, so even today it can go stale if the user (dis)connects the *non-browsed*
account's auth from the Conexión cards without restarting. `App.axaml.cs` builds one
`AccountSyncContext` per provider already (for the sync engine); this phase's job is to build the
browsing-side equivalent per account too, and let `MainWindowViewModel` hold all of them instead of
just the primary's.

**Shipped design — lower-risk than the original sketch above (kept for context; items 2 and 4 below
changed on implementation).**

1. New private `MainWindowViewModel.BrowserAccountSession` record bundling exactly the per-account
   browsing resources: `Provider`, `CacheService`, `MetricsStore`, `StatsScanner`, `PreviewLoader`,
   `ImagePreviewLoader`. Held in a private `_browserSessions` list, the primary registered by the
   constructor itself (`_browserSessions[0]`), later ones via the new public
   `AddBrowsableAccount(...)` — an additive registration method, deliberately mirroring
   `SyncPanelViewModel.AddAccount`'s own shape (same rationale: existing callers/tests that never
   call it are completely unaffected).
2. **Simpler than originally planned:** rather than reshaping the constructor to take
   `IReadOnlyList<BrowserAccountSession>` (which would have broken every existing test building a
   `MainWindowViewModel` with one provider — there are many), `_provider`/`_cacheService`/
   `_metricsStore`/`_statsScanner`/`_previewLoader`/`_imagePreviewLoader`/`_rootPath` simply stopped
   being `readonly`. `SwitchBrowserAccountAsync` (replacing `SwitchProviderAsync`) reassigns all
   seven from the target session directly — a much smaller diff than rewriting every one of the
   ~35 `_provider.X` call sites the original sketch assumed, with identical runtime behavior. Event
   subscriptions (`Activity`/`ListingParseWarning`) needed no changes at all: Phase A already wires
   every account's activity permanently (`ObserveAdditionalProviderActivity`), regardless of which
   one is being browsed, so there was nothing tied to "the active session" to re-wire.
3. `SwitchBrowserAccountAsync(ProviderId id)`: a no-op if `id` isn't registered or already active;
   otherwise cancels the in-flight load (`_cts`), swaps the seven fields, re-reads `IsAuthenticated`
   fresh from `AppSettingsService` for the *new* provider (closing the staleness gap described
   above as a side effect), clears `KindFilters`/`FilterSummary` (a deep-scan histogram belongs to
   one specific folder on one specific account), raises property-changed for every
   `_provider`-derived display property (`ActiveProviderDisplayName`, `BrowserHeaderTitle/Subtitle`,
   `IsProtonActive`/`IsOneDriveActive`, `HasDiagnostics`, `OneDriveAccountLabel`, `RootPath`), then
   calls the existing `GoToRootAsync()` (already handles cancellation-safe loading, breadcrumbs,
   and selection reset). No confirmation dialog, no restart message; `RequestSwitchProviderConfirmationAsync`
   was removed outright — left in place, it would have kept asking a question whose premise
   (a restart) is no longer true.
4. **Decoupling the Conexión tabs from browsing (originally item 4) was *not* done.**
   `IsProtonActive`/`IsOneDriveActive` still gate both the browser's active account *and* which
   settings connection card is shown, same as before. Narrowing scope on purpose: making both
   settings cards always-visible would have meant auditing every Settings field
   (`CliVersion`/`HasDiagnostics`/`OneDriveAccountLabel`/the CLI-update flow) to see which are
   safe to read regardless of which account is "active" versus which are only ever meaningful for
   whichever account the card belongs to — real work, and riskier than it looks (`CliVersion`
   touches the self-update installer). The user's actual complaint was the restart requirement, not
   the tab/card coupling; keeping that coupling means the existing Settings tabs *are* the account
   switcher (now instant instead of restart-gated) with no separate browser-toolbar control needed.
   A toolbar-level switcher and the two-cards-always-visible decoupling remain a possible future
   increment, not attempted here.
5. `AppSettings.ActiveProvider` is still written on every switch (as "last browsed account", read by
   `App.axaml.cs` on the next cold start) — kept rather than dropped, since it costs nothing and
   preserves continuity across restarts.

**Scope actually shipped:** switching resets the target session to its own root path (`RootPath`
back to `/` or `/my-files`), not to wherever it was last left — remembering each session's own
`CurrentPath`/breadcrumbs/selection across switches remains a small, additive follow-up (the
`BrowserAccountSession` record already exists to eventually hold it), deliberately not bundled here.

**Explicitly not done:** the Settings-card decoupling and browser-toolbar switcher (item 4 above,
scope-narrowed out); same-provider multiple accounts (still P7 general form, unaffected); any
change to `ProviderCatalog`/`AccountSyncContext` (unneeded — `App.axaml.cs` only gained one
`AddBrowsableAccount` call per non-primary context, reusing the `FolderStatsScanner`/
`TextFilePreviewService`/`ImageFilePreviewService` construction pattern already used for the
primary).

**Testing.** `MainWindowProviderTests` gained coverage for: a live switch changing every
`_provider`-derived property with no confirmation, switching back returning to the original
account's own root, `IsAuthenticated` being re-read fresh (not the stale value) for the target
provider, and switching to an unregistered account being a safe no-op. `./scripts/run-tests.sh`
green throughout (762 total, 6 skipped-integration).

**A real hang found and fixed while writing those tests, worth recording:** any test that lets
`SwitchBrowserAccountAsync`'s own `GoToRootAsync()` call reach an *uncached* folder load hangs
forever — `FetchFromCliAndUpdateCacheAsync`'s **success** path (not just its error path) marshals
through `Dispatcher.UIThread.InvokeAsync`, which never completes without a running Avalonia
dispatcher (the exact limitation `MainWindowViewModel`'s own `DisplayItems` doc comment already
flags for the error path — this phase is the first to trip it on the success path too, since no
prior test ever drove a full `LoadFolderAsync` cycle against an empty cache). Fixed by pre-seeding
each test session's `DriveCacheService` with one item at its root path, which makes `LoadFolderAsync`
take its cache-hit, fire-and-forget branch instead of the awaited one — not a production bug, purely
a test-harness constraint, but one that silently hangs the test host (no failing assertion, no
stack trace) rather than failing loudly, so it is worth any future test in this area knowing about.

### P9 — Filter sync pairs by provider

Small, independent of Phase B — the Sync window already labels each row with its account
(`SyncPairViewModel.AccountLabel`, blank when there's only one account) once
`SyncPanelViewModel.AddAccount` is used; this phase only adds a way to narrow the list to one
account at a time when there's more than one, following the same filter-chip idiom the folder
browser already established for file kinds (`ViewModels/KindFilterViewModel.cs` — `Label`/`Count`/
`IsActive`/`ApplyCommand`, a `null`/"Todos" chip always present) rather than inventing a new pattern.

- New `SyncPanelViewModel.ProviderFilters` (`ObservableCollection` of the same chip shape, or a
  reused/generalized `KindFilterViewModel`-style type), rebuilt whenever `AccountSyncToggles`
  changes: one "Todos" chip plus one chip per distinct account label currently registered via
  `_slots`, each counting how many of `Pairs` belong to it.
- `Pairs` stays exactly as it is today — the authoritative, unfiltered collection every existing
  test (`SyncPanelMultiAccountTests` et al.) already reads. A new `VisiblePairs` property (or an
  `ICollectionView`-style wrapper) is what the Sync window's `ItemsControl` binds to instead;
  recomputed on `Pairs.CollectionChanged` and on the active filter changing, same as `KindFilters`
  today drives `RootItems` without the browser ever losing `_loadedItems`.
- Only shown when `AccountSyncToggles.Count > 1` — with a single account every pair already belongs
  to it, so the row would be pure noise (same rule `AccountLabel`'s own blank-when-one-account
  already follows).

**Explicitly not done:** filtering by direction/status/conflict state — provider is the one axis
asked for; a general filter/sort bar for the Sync window is a separate, larger feature if ever
wanted.

### P10 — Google Drive provider — implemented, pending live verification

Implements §8's signed-off design. New `Services/Providers/GoogleDrive/`:
`GoogleDriveAuthenticator.cs` (authorization-code + PKCE via a loopback `HttpListener` at
`http://127.0.0.1:{port}/`, mirroring `GraphAuthenticator`'s structure; Google-specific
`access_type=offline`/`prompt=consent` on the authorize URL and a `client_secret` on the token
exchange/refresh, per §8.1), `GoogleDriveTokenStore.cs` (`google-drive-token.json`, chmod 600,
same accepted-risk shape as OneDrive's own store), `GoogleDriveHttpClient.cs` (bearer attach,
401→refresh→retry-once, `Retry-After` on 429 and on a 403 whose `error.errors[0].reason` is
`rateLimitExceeded`/`userRateLimitExceeded`), `GoogleDriveRequests.cs` (typed DTOs — no
anonymous-type `JsonContent.Create` anywhere), `GoogleDriveErrorClassifier.cs` (the §8.7 table,
reading `error.errors[0].reason` with a defensive fallback to status-code-only classification),
`GoogleDrivePathSyntax.cs` (`Comparison = Ordinal`, `AllowsDuplicateNamesInSameParent = true`,
essentially unrestricted local-name mappability per §8.6), `GoogleDriveOperations.cs` (the
path→id resolution cache from §8.2, `q`-filter escaping, pagination to exhaustion, client-side
conflict-strategy enforcement for `None`/`Skip`/`Replace`/`KeepBoth` since Drive never rejects a
duplicate name server-side, synchronous `copy`, `addParents`/`removeParents` move, multipart vs.
resumable upload), and `GoogleDriveProvider.cs` (`RemoteHash: Sha256`, `SupportsDelta: false`,
`CopyIsAsynchronous: false`, `SupportsBatchMove: false`, `DeltaSource: null`, `RemoteView: null`,
`Diagnostics: null`).

**New shared-seam surface (per §8.9's own note, the only one this design proposes):**
`IProviderPathSyntax.AllowsDuplicateNamesInSameParent` — a C# default-interface member defaulting
to `false`, so `ProtonPathSyntax`/`OneDrivePathSyntax` and every existing test fake implementing
`IProviderPathSyntax` needed no change. `Models/DriveItem.cs` gained one new field,
`IsRemoteOnlyDocument` (default `false`) — the mechanism chosen (of the two the skill sketched)
for flagging a Google-native file (Docs/Sheets/Slides/…, no binary content or checksum at all,
§8.4): `ListFolderAsync` still returns them (so a plain folder *browse* still shows them) but marks
them, and `RemoteScanner` skips a marked item during a *sync* scan the same way it already skips an
unmappable name. `Services/Sync/RemoteScanner.cs`'s `NodeSkipReason` gained two values —
`GoogleNativeFile` (for the above) and `DuplicateName` (for two Drive siblings sharing an exact
name, §8.2) — and its sibling-collision filter (`DropCaseCollisions`, renamed `DropNameCollisions`)
now also fires when `Paths.AllowsDuplicateNamesInSameParent` is true, not only when `Comparison`
is case-insensitive; the same exact-duplicate-name group it already detected via
`StringComparer.FromComparison(Ordinal)` grouping just gets reported under the new reason instead
of `CaseCollision`. `Services/Sync/SyncExecutor.cs`'s `DescribeSkip` got matching messages for both.

**The real latent bug this session's own checklist called out, and fixed:** `App.axaml.cs`'s
`BuildAccountContext` picked a hasher with a two-way `provider.Capabilities.RemoteHash ==
RemoteHashAlgorithm.QuickXor ? new QuickXorHasher() : new Sha1ContentHasher()` — since Google
Drive's capability reports `RemoteHashAlgorithm.Sha256`, that ternary would have silently fallen
through to `Sha1ContentHasher()`, producing a hash that could never match Drive's real
`sha256Checksum` and would make every Google Drive file look permanently changed. Added
`Services/Providers/Sha256ContentHasher.cs` (a thin `System.Security.Cryptography.SHA256` wrapper,
lowercase-hex output to match Drive's own `sha256Checksum` format) and turned the hasher pick into
a real three-way `switch` so this class of mismatch can't reappear silently for a future provider
either.

**Deviations from §8 as written:**
- §8.4 said "fall back to `md5Checksum` in code if `sha256Checksum` turns out absent in practice."
  Not implemented that way: `GoogleDriveOperations.ToDriveItem` only ever reads `sha256Checksum`,
  with no fallback — falling back to `md5Checksum` while `Capabilities.RemoteHash` stays fixed at
  `Sha256` would have been exactly the silent hash-algorithm mismatch R2 (and this session's own
  hasher-switch fix above) exists to prevent. A file with no `sha256Checksum` just gets no content
  hash, mirroring `OneDriveOperations.BuildDriveItem`'s own no-fallback handling of `quickXorHash`.
- §8.6 left the resumable-upload chunk size as "a multiple of 256 KiB, your call." Chose
  `8 * 256 * 1024` (2 MiB) — a round number comfortably above Drive's 256 KiB minimum without being
  needlessly small for a multi-MB file.
- The Google-native-file skip mechanism (§8.4 left this as an open decision between two sketched
  options) landed as option (a): a new `DriveItem.IsRemoteOnlyDocument` flag plus a
  `RemoteScanner`-level skip, not an exclusion inside `ListFolderAsync` itself — so a folder browse
  still shows a Google Doc as a (non-syncable) row instead of hiding it outright.

**Wiring:** `ProviderCatalog.Create`'s `GoogleDrive` arm now builds
`GoogleDriveTokenStore` → `GoogleDriveAuthenticator(clientId, clientSecret, tokenStore)` →
`GoogleDriveHttpClient` → `GoogleDriveProvider`, replacing the `GenericCloudDriveProvider` stub —
mirrors `CreateOneDrive` exactly. `AppSettings` gained `GoogleDriveClientId` and
`GoogleDriveClientSecret` (the latter stored in plaintext, same accepted-risk reasoning as every
other credential this app persists to disk — R3-style, noted in its own doc comment);
`IsGoogleDriveAuthenticated`/`GoogleDriveAccountLabel` already existed from an earlier UI-scaffolding
commit and needed no change. `MainWindowViewModel` gained `GoogleDriveClientId`/
`GoogleDriveClientSecret`/`GoogleDriveAccountLabel` properties (mirroring OneDrive's), a
`GoogleDrive` arm in `CanAuthenticate`'s per-provider switch, and a `GoogleDriveProvider` arm in
the live-account-label switch used after sign-in/out. The Settings view's existing (UI-only)
Google Drive card gained the client-id/secret fields and account-label row the OneDrive card
already has, wired to those bindings.

**Testing:** `tests/.../Services/Providers/GoogleDrive/` — `GoogleDriveOperationsTests` (path→id
resolution including a duplicate-name-in-same-parent case confirming first-match-wins,
pagination exhaustion, all four conflict-strategy branches, the Google-native-file skip, move,
copy, trash, rename, create-folder, both the multipart and resumable upload paths),
`GoogleDrivePathSyntaxTests`, `GoogleDriveErrorClassifierTests` (Drive's real v3 error shape),
`GoogleDriveTokenStoreTests`, `GoogleDriveAuthenticatorTests` (PKCE verifier/challenge math and the
token-refresh path, no real browser). `RemoteScannerTests` extended with a minimal fake
`ICloudDriveProvider` (Proton's real CLI-backed fake can't produce a duplicate-name or
remote-only-document item) covering the two new skip reasons.
`ProviderCatalogTests.Create_GoogleDrive_ReturnsAWorkingGoogleDriveProvider_NotTheGenericStub`
replaces the old stub-returning assumption. No golden-vector hash test for
`Sha256ContentHasher` — a standard algorithm, not a from-spec implementation like `QuickXorHasher`.

**Not yet done:** live verification against a real Google account (sign in through the real OAuth
consent screen, list a real Drive root, upload/download and compare a real `sha256Checksum`,
observe a real Google Doc get skipped) — this phase's own Appendix A entry stays unfilled until
that follow-up session runs, same as P8's own entry still records for what it hasn't captured live
yet. Do not read the "implemented" checkbox above as "live-verified" — it isn't, on purpose.

### P8 — *Optional:* delta-based remote scanning — implemented, pending live verification

`Capabilities.SupportsDelta` + `IDeltaSource.GetChangesAsync(string? deltaToken)`, consumed by the
new `DeltaRemoteScanner : IRemoteScanner` for OneDrive's whole-drive Graph delta query
(`/me/drive/root/delta`), falling back to the full-walk `RemoteScanner` for a one-way pair (which
never populates a baseline to merge onto) and for Proton (no delta/events command exists —
`DeltaSource => null`). `SyncReconciler` needed zero changes: the scanner's job is to still hand it
a complete remote-tree dictionary, reconstructed by merging the delta's changes onto the persisted
three-way baseline (`SyncBaselineEntry.RemoteAtSync`). Delta tokens are scoped **per sync pair**,
not per account (`SyncStateStore.Get/SetDeltaTokenAsync`) — an account-wide token would let
whichever pair syncs first in a cycle "consume" the diff and silently starve a second pair sharing
it. See `Services/Providers/IDeltaSource.cs`, `Services/Sync/DeltaRemoteScanner.cs`,
`Services/Providers/OneDrive/OneDriveOperations.cs`'s `IDeltaSource` implementation (including the
`parentReference.path` parser delta items need, since they arrive with no ambient parent context
unlike `ListFolderAsync`'s recursive walk).

**Not yet done:** full live verification against a real Graph account (sign in, run one delta
cycle, make a real change, confirm the next delta call reports exactly that change and a two-way
pair picks it up) — this phase's Appendix A entry is still pending that session.

**A real bug found live, before that full session ran:** adding a new two-way OneDrive pair (a
brand-new pair has no stored delta token, so its first cycle is a full-resync whole-drive delta
call) hung the sync scheduler for minutes on a personal account with only a few hundred items
total — reported by the user as the app appearing "stuck" doing `GET /root/delta` over and over,
and confirmed in `crash.log` ("OneDrive sync scheduler did not stop within 10s of shutdown") and
in `cache.db` (the new pair's `LastSyncAt`/`LastSyncStatus` never updated — the cycle never
completed or failed, it just kept going). Root cause not fully isolated (candidates: Graph's
default delta page size being much smaller than assumed, so "a few hundred items" still meant many
pages; rate-limit backoff compounding across many pages) since the app wasn't running with the
fix's own diagnostics yet when this was investigated. Mitigated defensively either way:
`FetchDeltaAsync` now labels each page's `Activity` line with its real page number instead of a
generic repeated string (`GET /root/delta (page N)`), sets an explicit `$top=200` on the first
page instead of trusting Graph's server-side default, and — the actual safety net — aborts with a
loud `DriveException` after `MaxDeltaPages` (5,000) instead of paging forever, so a future
recurrence fails visibly instead of hanging the scheduler again. Next time this happens, the
per-page activity log is what should pin the exact root cause.

**A second, far more serious bug found live, root-caused this time:** a whole-drive delta
enumerates *every* item in the drive, including the pair's own root folder as an item in its own
right — something a full-walk `RemoteScanner`'s BFS can never report, since it starts *at* the
root and only ever visits children. `DeltaRemoteScanner` did not filter that item out:
`PathMapper.ToRelativeFromRemote` maps it to `""` (the same key `ToRemoteAbsolute`/`ToLocalAbsolute`
treat as "the sync root itself"), which then landed in the merged dictionary as an ordinary
syncable node. `SyncReconciler` was never built to see an entry for the root, and queued a real
`TrashRemote` action against relative path `""` — which resolves to the pair's *entire* remote
root folder. Confirmed via a real user's `SyncLog`: a fresh OneDrive pair's very first delta cycle
trashed its own root folder on OneDrive (`TrashRemote` with an empty `RelativePath`), and because
that pair's local folder was *also* the local side of a separate Proton pair (the user was trying
to mirror one folder from both providers at once — see the "coexisting providers" discussion this
finding prompted), the resulting local deletions cascaded into Proton trashing its own copies of
the same files too. Fixed: `DeltaRemoteScanner.ScanAsync` now skips any change whose resolved
relative path is empty, for both the upsert and delete branches, before it ever reaches the merged
dictionary — the pair's own root can never again be treated as a child of itself.

**Recovery note for the affected user:** both `TrashRemote` (Proton and OneDrive) and OneDrive's
own delta-driven root deletion go to each service's own trash/recycle bin, not a hard delete —
check Proton Drive's Trash and OneDrive's Recycle Bin (each provider's own web UI) before assuming
anything is permanently gone.

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
- **R7 — Drive has no server-side name-collision detection (§8.2, §8.6).** Unlike Proton and
  Graph, `files.create`/`copy` never reject on a duplicate name — the app has to check for an
  existing same-name sibling itself before it can honor `UploadConflictStrategy`, and that
  check-then-act has a real TOCTOU race if the same folder is touched from another client
  concurrently. Mitigation: documented as an accepted limitation in §8.6, not silently ignored;
  revisit only if it causes an observed duplicate in practice.
- **R8 — Drive addresses by id, not path (§8.2).** Every other provider in this plan takes a path
  string at the `IDriveOperations` boundary and the sync engine (`PathMapper`, `SyncReconciler`,
  the `DriveItems`/`FolderMetrics` schema) is built on "path uniquely identifies a node." Drive
  breaks that at the root: names are not unique within a folder (confirmed in §8.2). The design in
  §8.2 keeps the existing path-string interface intact by pushing id-resolution and an internal
  path→id cache entirely inside `GoogleDriveOperations`, and extends the *existing*
  collision-skip mechanism (§2.4, built for OneDrive's case-insensitivity) to also cover
  duplicate-named Drive siblings — deliberately not a redesign of the path-based interface, on the
  same "don't touch what isn't proven to need it" principle P3 already applied to `PathMapper`.

## 7. Explicitly out of scope

- Copying or moving items *between* providers.
- SharePoint document libraries and shared/other-user drives beyond `/me/drive`.
- Provider-specific features with no equivalent in the other: Proton sharing links, OneDrive
  version history, Personal Vault.
- Replacing the hand-wired composition root with a DI container.
- Windows/macOS packaging changes.
- **Google Drive, beyond the design in §8.** Shared Drives (`corpora=drive`/`allDrives`),
  syncing Google-native files (Docs/Sheets/Slides — no binary content or checksum to sync at all;
  §8.4 has the reasoning), and Drive's delta query (`changes.list`) are all explicitly deferred
  past the initial Google Drive phase (P10), mirroring how OneDrive shipped its core provider in
  P6 and delta separately in P8.

---

## 8. Google Drive (G) — REST API v3 design

> **Provenance.** Everything in this section comes from Google's public Drive API v3
> documentation (`developers.google.com/workspace/drive/api/...`), fetched live during this
> planning pass, and is **not verified against a live account from this repo**. Per `AGENTS.md`
> ("never invent output shapes") and the same rule §4 states for OneDrive, each item marked
> *(unverified)* must be confirmed with a real capture before code depends on it, with the
> confirmed shapes recorded in this document's Appendix A — the same discipline §4's Appendix A
> already follows for OneDrive.

### 8.1 Why REST, and which OAuth flow (G1)

No first-party Linux CLI, so this follows OneDrive's shape: direct REST over `HttpClient` +
`JsonDocument`/`AppJsonContext`, no Google API client library (B9 — Native AOT; Google's
`Google.Apis.Drive.v3` client is reflection-heavy and not a safe fit, same reasoning that ruled out
MSAL for OneDrive).

- **Auth**: Google's installed/desktop-app OAuth2 flow, **authorization code + PKCE**, loopback
  redirect (`http://127.0.0.1:{dynamic port}` — Google's own docs recommend the loopback IP form
  over a custom URI scheme; OOB/manual-copy is explicitly no longer supported). Structurally the
  same shape as `GraphAuthenticator` (P6): open the browser via `IAuthPrompt.OpenBrowserAsync`,
  run a loopback `HttpListener`, exchange the code at `https://oauth2.googleapis.com/token`.
- **Scope — the one decision to flag loudly.** A sync tool that mirrors an arbitrary,
  user-chosen folder tree (not files hand-picked one at a time through a picker UI) needs the
  broad **`drive`** scope ("View and manage all your Drive files"), not the narrower
  **`drive.file`** scope (which only grants access to files the user explicitly opens/creates
  through this app — useless for "sync whatever's already in this folder"). `drive` is a
  **restricted** scope: Google Cloud Console gates it behind an OAuth consent screen that, while
  the app is in "Testing" publishing status, caps external users and shows an "unverified app"
  warning at sign-in *(unverified — the exact cap and warning copy were not confirmed against a
  live Cloud Console session this pass, only against Google's scope-classification docs; confirm
  before relying on a specific number)*. This is a real UX cost the user should sign off on before
  P10 implementation starts: every sign-in shows a scarier consent screen than OneDrive's, unless
  the app later goes through Google's verification process (out of scope here — see §7).
- **Token storage**: same accepted-risk shape as OneDrive (§4.2) — `google-drive-token.json` in
  `AppSettingsService.BaseFolder`, `chmod 600`, via `AppJsonContext`. Refresh token is long-lived
  until revoked; access token expires in hours; same 401→refresh-once→retry-once pattern as
  `GraphHttpClient`.

### 8.2 Addressing model — the id-based mode `IProviderPathSyntax` needs (G2)

This is the one place Drive is structurally different from both existing providers, and the part
of this plan most worth the user's attention before sign-off.

**What's confirmed:**
- Every file/folder has a stable, opaque `id` (a folder is just an item with
  `mimeType: application/vnd.google-apps.folder`, no separate folder concept). There is **no
  native path** — a display path is built by walking `parents[0]` up to the root.
- `parents` is schematically an array, but current Drive behavior is **effectively single-parent**:
  the v3 File resource reference states "max one parent per file," and Google migrated away from
  true multi-parent in 2020 (former secondary parents became shortcuts). This plan treats Drive as
  single-parent — no DAG-shaped path resolution needed.
- **Names are not unique within a folder** — stated explicitly in Google's own File resource docs.
  Two files can share both a name and a parent, distinguished only by `id`. This is the load-bearing
  difference from Proton and OneDrive, both of which reject a same-name create in the same folder.

**Design decision — keep `IDriveOperations`'s path-string signatures unchanged; do not force an
id-shaped interface onto the two providers that don't need one.** `GoogleDriveOperations` resolves
a path to an id internally, segment by segment (`files.list` with
`q="'<parentId>' in parents and name='<segment>' and trashed=false"`, using the well-known
`root` alias id for the first segment so no extra call is needed to resolve the root itself), and
keeps a private path→id cache scoped to the operation's lifetime (a full listing already visits
every node, so `ListFolderAsync` populates it cheaply; a targeted single-path call like
`DownloadFileAsync` pays the segment-walk cost directly). This mirrors §0.1's own framing: the
false invariant here is "a path uniquely identifies a node," not "operations are addressed by
path" — so the fix belongs at the same layer P3 already uses for case-insensitivity, not in a
wider interface change.

**Duplicate-name handling extends the existing collision mechanism, not a new one.**
`IProviderPathSyntax` gains one more axis alongside `Comparison` (§2.4):

```csharp
bool AllowsDuplicateNamesInSameParent { get; }   // false: Proton, OneDrive. true: Google Drive.
```

`RemoteScanner`'s existing per-sibling-batch collision hook (added for OneDrive case-insensitivity,
§2.4 / the P1-P5 adversarial-review bug #1 fix) already runs once per sibling batch before any item
reaches the per-node callback — it is extended to also flag a duplicate name as a collision when
`AllowsDuplicateNamesInSameParent` is true, using the same `NodeSkip(string Name, NodeSkipReason)`
shape P1-P5's finding #5 already introduced (a new `NodeSkipReason.DuplicateName` value). The first
child with a given name in a listing order wins deterministically (Drive's `files.list` order is
not itself guaranteed stable across calls *(unverified)* — worth a real capture before relying on
"first" being consistent run to run); every other same-named sibling is reported skipped, exactly
like an unmappable name is today, never silently merged or overwritten.

**Path building (`Combine`)**: plain `/`-join, same shape as Proton/OneDrive — Drive itself has no
escaping requirement for `/` inside a *display* path the app builds (§8.6). **`Comparison`**:
`Ordinal` — Drive names are case-sensitive and case-preserving (unlike OneDrive's
`OrdinalIgnoreCase`). **`Root`**: `"/"`, and the literal id alias `"root"` is used directly as the
first parent id rather than being resolved via a lookup.

### 8.3 Listing and pagination (G3)

`files.list` with an explicit `fields` parameter (Drive's default response omits most fields
unless asked, opposite failure mode from Graph's "asks for everything by default" —
`fields=nextPageToken,files(id,name,mimeType,parents,size,modifiedTime,md5Checksum,sha256Checksum,trashed)`),
`q="'<parentId>' in parents and trashed=false"`, `pageSize` (max 1000), `pageToken` for
continuation, `corpora=user`/`spaces=drive` for My Drive (Shared Drives — `corpora=drive` +
`driveId`, or `allDrives` — are out of scope, §7). Must follow `nextPageToken` to exhaustion, same
"a partial listing reads as a remote deletion" failure mode §4.3 already calls out for Graph
pagination.

### 8.4 `DriveItem` mapping and the Google-native-file problem (G4)

| `DriveItem` field | Drive source |
|---|---|
| `NodeId` | `id` (stable across move/rename) |
| `Name` | `name` |
| `IsFolder` | `mimeType == "application/vnd.google-apps.folder"` |
| `Size` | `size` — **absent for Google-native files** (see below) |
| `ModifiedAt` | `modifiedTime` (client-settable on write, §8.5 — the analogue of OneDrive's `fileSystemInfo.lastModifiedDateTime`) |
| `ContentHash` | `sha256Checksum` when present, else `md5Checksum` — see `RemoteHash` below |
| `Path` | built by `GoogleDrivePathSyntax`/the id-resolution cache in §8.2, never from a server-provided path |

**Google-native files (Docs, Sheets, Slides, Forms, Drawings — `mimeType` starting
`application/vnd.google-apps.` other than `folder`/`shortcut`) have no binary content and
therefore no checksum at all** (confirmed: `md5Checksum`/`sha1Checksum`/`sha256Checksum` are all
documented as absent for them). There is no fallback hash to sync against. **Decision for P10:**
treat them like an unmappable/skipped node — the same `NodeSkip` mechanism as §8.2's duplicate
names — rather than attempting an export-to-binary conversion (Drive supports exporting a Doc to
`.docx`/PDF etc. via `files.export`, but that's a real feature with its own format-choice and
staleness questions, explicitly deferred, §7). A user syncing a folder containing Google Docs will
see them reported as skipped with a clear reason, not silently dropped.

**`RemoteHash` decision:** prefer `sha256Checksum` — `RemoteHashAlgorithm` (§2.5) already has a
`Sha256` member reserved (added alongside `QuickXor` in P3, unused until now), so this needs no
enum change. Unlike OneDrive's real per-drive-type hash split (§4.4/O4), Drive's docs show all
three checksums populated together for any binary file, so this is a fixed choice, not a
per-item fallback — *(unverified: confirm at least one real binary file actually returns
`sha256Checksum` populated, not just documented; fall back to `md5Checksum` in code if it turns out
absent in practice, same defensive shape O4 already uses)*.

### 8.5 Two-way sync specifics (G5)

- `modifiedTime` is client-writable directly in the request body on `files.update`/`create` (no v2-style
  `setModifiedDate` query flag needed) → `CanSetRemoteModificationTime = true`, same reasoning as
  O5: without this, every upload looks remotely-newer on the very next cycle.
- Rate limiting: shared `HttpClient`, a concurrency gate from `MaxRecommendedConcurrency`, and
  `DriveErrorKind.RateLimited` on 429 or 403 with reason `rateLimitExceeded`/`userRateLimitExceeded`
  — reuse `SyncRetryPolicy`'s existing `Busy`/`RateLimited` branch, not a second mechanism (same
  rule O5 states for Graph).
- `SyncEchoSuppressor` needs no provider-specific change (path-keyed, provider-independent).

### 8.6 Upload, conflict strategy, and the no-server-side-collision-check gap (G6)

- **Simple/multipart** (`uploadType=media`/`multipart`): ≤ 5 MB. **Resumable**
  (`uploadType=resumable`): initiate with JSON metadata → `Location` session URI → `PUT` in chunks
  (multiple of 256 KiB recommended) with `Content-Range`; sessions expire after one week.
  `MaxSingleShotUploadBytes = 5 MiB`, `UploadChunkSizeBytes` a multiple of 256 KiB (mirrors §4.3's
  shape, different numbers).
- **Trash**: `files.update(id, {trashed: true})` — 30-day auto-purge, matches "trash" semantics
  used elsewhere. **Move**: `files.update` with `addParents=<dest>&removeParents=<current>` query
  params (reparenting, not a path rename — consistent with §8.2's single-parent model).
  `SupportsBatchMove = false` (one PATCH per item, same as OneDrive). **Copy**: `files.copy(id)` —
  a normal synchronous POST, unlike Graph's async `202` + monitor-URL dance → `CopyIsAsynchronous
  = false`, `SupportsServerSideCopy = true`.
- **The real gap (R7): Drive never rejects a duplicate name.** `files.create`/`copy` happily
  create a second `report.pdf` in the same folder — there is no server-side conflict to translate
  the way Graph's `@microsoft.graph.conflictBehavior` or Proton's `skip` flag do. `UploadConflictStrategy`
  therefore has to be enforced **client-side** in `GoogleDriveOperations`: before an upload, list
  the target folder filtered to the exact name (`q="'<parent>' in parents and name='<name>' and
  trashed=false"`) and branch on `None`/`Skip` (upload only if absent), `Replace` (patch the
  existing file's content via `files.update` instead of creating a new one), `KeepBoth` (append a
  suffix client-side, same as the app would for a local-filesystem collision). **Stated plainly as
  a known, accepted limitation, not silently glossed over:** this check-then-act has a race if
  another Drive client creates a same-named file between the list and the create call — see R8 in
  §6.

### 8.7 Error mapping (G7) — `GoogleDriveErrorClassifier`

Body shape (v3, confirmed distinct from v2): `{"error":{"code":…,"message":…,"errors":[{"reason":…,"domain":…}]}}` —
classify on `error.errors[0].reason` (machine-readable), not the human `message`, same rule §4.7
states for Graph's `error.code`.

| Signal | `DriveErrorKind` |
|---|---|
| 401, reason `authError` | `NotAuthenticated` |
| 403, reason `insufficientFilePermissions` | `PermissionDenied` |
| 404, reason `notFound` (Drive returns 404, not 403, for existence-concealing permission denials) | `NotFound` |
| 403, reason `rateLimitExceeded`/`userRateLimitExceeded`; 429 | `RateLimited` |
| 403, reason `storageQuotaExceeded` | `Quota` |
| — (no native duplicate-name error; handled client-side, §8.6) | n/a |
| `HttpRequestException`, DNS, TLS | `Network` |
| `TaskCanceledException` from client timeout | `Timeout` |

### 8.8 Testing (G8)

Same shape as O8: `FakeHttpMessageHandler` + recorded/scrubbed Drive JSON fixtures under
`tests/.../Fixtures/GoogleDrive/`; unit tests for path→id resolution (including the duplicate-name
collision path, §8.2), paging exhaustion, client-side conflict-strategy enforcement (§8.6),
Google-native-file skip handling (§8.4), error mapping, the 401-refresh-retry-once path, resumable
upload chunk arithmetic. `MYPERSONALDRIVE_GOOGLEDRIVE_INTEGRATION=1` opt-in integration tests
against a throwaway account, mirroring `RealOneDriveAuthTests`. `FakeDriveProvider` (P1) still means
none of the sync-engine tests need any of this.

### 8.9 What P10 explicitly does not attempt

Shared Drives, Google-native-file export/sync, and delta (`changes.list`) are deferred past this
provider's first phase — see §7. `IProviderPathSyntax.AllowsDuplicateNamesInSameParent` (§8.2) is
the only new interface surface this design proposes; everything else reuses P1–P6's existing
seam unchanged.

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

## Appendix A2 — Verified Google Drive behavior

Live-verification session in progress, 2026-09-04, against a real personal Google account
(project `my-personal-drive-507613`) via the real app UI (`dotnet run`), not yet the opt-in
integration test suite.

1. **OAuth sign-in itself worked** — authorization-code + PKCE via the loopback listener,
   `access_type=offline`/`prompt=consent` on the authorize URL, `client_secret` on the token
   exchange — all as designed in §8.1. Confirms Google still issues a `client_secret` for a
   "Desktop app" OAuth client (seen directly in the downloaded credentials JSON) and that the
   token exchange accepts it.
2. **A real sign-in attempt hung the entire UI** the first time it was tried, confirming the
   defect §8.1's design didn't anticipate: `GoogleDriveAuthenticator.AuthenticateAsync` awaited
   `HttpListener.GetContextAsync()` with **no timeout**, and every `!IsLoading`-gated command in
   `MainWindowViewModel` (including switching the browsed provider) went unresponsive for as long
   as that wait lasted — observed lasting indefinitely across two separate attempts, both requiring
   the app to be killed. Root-caused by reading `MainWindowViewModel.AuthenticateAsync`/`SwitchBrowserAccountAsync`
   together: `SelectedProvider`'s setter (the header combo box) has no `IsLoading` guard and so kept
   working, which is what made the symptom look inconsistent ("the settings-tab switch buttons do
   nothing, but the header dropdown does") rather than a clean full freeze. **Fixed**:
   `GoogleDriveAuthenticator` now bounds that wait to a 5-minute `SignInTimeout`, throwing a clear
   `DriveErrorKind.Timeout` `DriveException` instead of hanging forever.
3. **`files.list`'s `size` field is a JSON string, not a bare number** — confirmed live: a real
   first `ListFolderAsync("/")` after a successful sign-in threw
   `System.Text.Json.JsonException: The JSON value could not be converted to
   System.Nullable\`1[System.Int64]` on `$.files[0].size`, because `GoogleDriveFile.Size` was typed
   `long?` with no string-reading allowance. This is Google's own documented convention for int64
   API fields (avoids precision loss for JavaScript clients) — the phase's own research pass (§8,
   pre-implementation) didn't surface it because no live capture had been taken yet. **Fixed**:
   `[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]` added to `GoogleDriveFile.Size`.
   The unit-test fixtures in `GoogleDriveOperationsTests` had been written with a bare-number
   `"size":42` (an assumption, not a captured shape) and were corrected to the real
   `"size":"42"` string form so they would have caught this before it reached a live account.

4. **Switching the browsed provider directly between two non-adjacent entries in the header
   ComboBox (e.g. Proton → Google Drive) silently did nothing** — going through a third provider
   first (Proton → OneDrive → Google Drive) always worked. Not specific to Google Drive —
   pre-existed for every provider pair, just never noticed before this session exercised more
   direct provider-to-provider switches than usual. Took **four** iterations, live-tested every
   time, to actually fix — the first three all treated a symptom, not the cause:
   - **First attempt**: `ProviderDescriptor` is a record with full-property default equality, and
     `AvailableProviders`/`SelectedProvider` rebuilt brand-new instances on every access; the
     ComboBox's two-way `SelectedItem` binding resolves the bound value against its current
     `ItemsSource` via `Equals`, and whenever two recomputations of "the same" provider differed in
     `AccountIdentity`/`IsAuthenticated`, Avalonia treated them as different items and lost the
     selection. Changed `ProviderDescriptor.Equals`/`GetHashCode` to compare by `Id` alone. Live
     retest: **did not fix it**.
   - **Second attempt**: suspected reentrancy instead — `SelectedProvider`'s setter ran the switch's
     synchronous prefix inline, inside the ComboBox's own SelectedItem-changed handling. Deferred
     the switch via `Dispatcher.UIThread.Post`. Live retest: **made it worse** — every switch now
     visibly flickered to the new provider and then snapped back to the original one, consistently,
     for every pair (not just non-adjacent ones).
   - **Third attempt**: stopped relying on `SelectedItem`/`Equals`-based matching — added
     `SelectedProviderIndex` (a plain `int`) and rebound the ComboBox to `SelectedIndex`. Live
     retest: **still broken**, same symptom as before switching to index binding.
   - **Actual root cause, found on the fourth pass**: none of the first three attempts touched the
     real problem — `AvailableProviders` itself was reassigned to a **brand-new collection object**
     on every provider switch (`OnPropertyChanged(nameof(AvailableProviders))` after a getter that
     rebuilt the whole list from scratch). Avalonia's `SelectingItemsControl` resets or mis-tracks
     `SelectedItem`/`SelectedIndex` whenever its bound `ItemsSource` **itself** changes identity —
     no amount of care in how the selected value is kept in sync afterward can compensate for the
     items collection being swapped out from under it. This is what the first three attempts were
     each, unknowingly, trying to work around from the wrong side.
   - **Fix**: `AvailableProviders` is now `ObservableCollection<ProviderDescriptor>` — one stable
     instance for the ViewModel's lifetime, populated once in the constructor and updated
     afterward by a new `RefreshAvailableProviders()` that writes each entry **in place** via the
     indexer (`AvailableProviders[i] = updated`), never reassigning the collection itself. Every
     `OnPropertyChanged(nameof(AvailableProviders))` call site became a `RefreshAvailableProviders()`
     call instead. `SelectedProviderIndex` (from the third attempt) and `SelectedProvider` both kept
     unchanged otherwise. Regression tests: `ProviderDescriptorTests`,
     `ProviderContextSwitcherTests.AvailableProviders_IsTheSameInstance_AcrossASwitch` (pins the
     reference identity directly — the actual property this bug depended on),
     `ProviderContextSwitcherTests.SelectedProviderIndex_TracksTheActiveProvider_AcrossASwitch`.
   Recorded in full, including the two failed middle attempts, because that arc is itself the
   lesson: a plausible-sounding fix for a UI binding bug still needs a live retest before being
   trusted, and "still broken" after a fix is information about where the *next* fix should look,
   not a reason to try a different plausible-sounding thing at the same layer — the same discipline
   this plan's Appendix A already applies to backend behavior.

**Not yet captured**: a full folder listing beyond the first page, upload (small and resumable),
move, copy, trash, rename, share-link creation, a real Google-native file (Doc/Sheet) actually
being skipped as designed, and the exact `sha256Checksum`/`md5Checksum` presence on a real file
(§8.4's own open question). Each stays marked (unverified) until captured — this live-verification
pass is still in progress, not complete.

## Appendix B — File-by-file change inventory

| File | P1 | P2 | P3 | P4 | P5 | P6 | P10 |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `App.axaml.cs` | ● | ● | ● | ● | ● | ● | ● |
| `Services/ProtonDriveService.cs` → `Providers/Proton/` | ● | | ● | | | | |
| `Services/ProtonDriveCli{Executor,Locator}.cs` → `Providers/Proton/` | ● | ● | | | | | |
| `Services/CliErrorClassifier.cs`, `CliException.cs`, `CliErrorKind.cs` | ● | ● | | | | | |
| `Services/Cli{ReleaseFeed,UpdateInstaller,PlatformKey,VersionComparer}.cs` | ● | | | | ● | | |
| `Services/CliCommandEventArgs.cs` → `ProviderActivity` | | ● | | | | | |
| `Services/RemoteViewFreshnessPolicy.cs` | ● | | | | | | |
| `Services/RemoteTreeWalker.cs`, `FolderStatsScanner.cs` | ● | | | | | | |
| `Services/DriveCacheService.cs`, `FolderMetricsStore.cs` | | | | ● | | | |
| `Services/DriveDatabaseMigrations.cs` | | | | ● | | | |
| `Services/AppSettings.cs`, `AppSettingsService.cs`, `AppJsonContext.cs` | | ● | | | ● | ● | ● |
| `Services/Providers/IProviderPathSyntax.cs` (new `AllowsDuplicateNamesInSameParent` default member) | | | ● | | | | ● |
| `Services/Providers/GoogleDrive/*` (new) | | | | | | | ● |
| `Services/Providers/Sha256ContentHasher.cs` (new) | | | | | | | ● |
| `Models/DriveItem.cs` (new `IsRemoteOnlyDocument` field) | ● | | | | | | ● |
| `Services/Sync/SyncExecutor.cs` | ● | | ● | | | | ● |
| `Services/Sync/RemoteScanner.cs` | ● | | ● | | | | ● |
| `Services/Sync/SyncBaselineWriter.cs` | ● | | ● | ● | | | |
| `Services/Sync/PathMapper.cs` | | | ● | | | | |
| `Services/Sync/SyncReconciler.cs` | | | ● | ● | | | |
| `Services/Sync/LocalFileHasher.cs` → `IContentHasher` | | | ● | | | | |
| `Services/Sync/SyncStateStore.cs` | | | | ● | | | |
| `Services/Sync/SyncRetryPolicy.cs` | | ● | | | | ● | |
| `ViewModels/MainWindowViewModel.cs` | ● | ● | ● | | ● | | ● |
| `ViewModels/CommandLogBuffer.cs` | | ● | | | | | |
| `ViewModels/Sync/SyncPanelViewModel.cs` | ● | | | | ● | | |
| `Views/MainWindow.axaml(.cs)` | | | | | ● | | ● |
| `AGENTS.md`, `docs/ARCHITECTURE.md` | ● | ● | ● | ● | ● | ● | ● |
| `.claude/skills/cli-command/SKILL.md` (becomes Proton-scoped) | ● | | | | | | |
