---
name: add-cloud-provider
description: Plan and implement a new cloud storage backend (a second/third provider alongside Proton Drive and OneDrive) behind the ICloudDriveProvider seam. Use whenever the app needs to talk to a new remote drive service.
---

# Add a cloud storage provider

Two phases in one skill, always in order: **Plan** first, get sign-off, *then* **Implement**. The
two are kept together because the same person needs to reason about both — a capability decided
wrong in the plan (which content-hash algorithm, whether copy is async) reshapes the code — but
they are still distinct steps: do not start writing provider code before the plan exists and the
user has agreed to it.

Read `docs/PLAN-CLOUD-PROVIDERS.md` first, in full. It is both the design record for the seam
(`ICloudDriveProvider`/`IDriveOperations`/`IDriveAuthenticator`/`IProviderPathSyntax`/
`ProviderCapabilities`/optional `IRemoteViewInvalidator`/`IProviderDiagnostics`/`IDeltaSource`) and
the log of two real implementations (Proton's CLI-process adapter, OneDrive's Graph REST adapter)
with the mistakes each one made along the way. Treat those two as your reference shape — one
process-based, one HTTP-based — and pick whichever is closer to the new backend's own transport.

## Phase 1 — Plan

Produces a new phase section in `docs/PLAN-CLOUD-PROVIDERS.md` (use the `plan-doc` skill for the
document mechanics — stable IDs, status checkboxes, cross-links). This phase is research and
writing only; no provider code yet.

1. **Read the backend's real documentation.** Not memory, not a guess — the actual published API
   or CLI reference. `docs/PLAN-CLOUD-PROVIDERS.md`'s own §4 header states the rule this repeats:
   mark anything not yet confirmed against a live account as *(unverified)*, explicitly, in the
   plan text itself. A plan that states an assumed JSON shape as fact is worse than one that says
   "unverified — confirm in Phase 2."
2. **Decide the auth model** and where credentials live: an OAuth flow (PKCE + loopback listener,
   like OneDrive's `GraphAuthenticator`; device-code as a fallback for headless use), an API key,
   or a CLI subprocess with its own session (like Proton). If tokens get written to disk, state the
   at-rest exposure plainly — OneDrive's own plan entry (§4.2) records shipping 0600-plaintext as a
   stated, accepted risk rather than silently doing it.
3. **Decide path addressing.** Case-sensitive or case-insensitive? Reserved characters/names?
   Does the backend's own root have a fixed non-generic name (Proton's `/my-files` broke on
   OneDrive, which roots at `/` — a real regression caught after that assumption leaked past
   Proton)? This becomes the new `IProviderPathSyntax` implementation.
4. **Fill in `ProviderCapabilities` for real, with a reason for each field** — don't leave any of
   these as a guess:
   - `RemoteHash` — which content-hash algorithm the backend's listing actually returns. If it can
     return more than one (OneDrive can return sha1/sha256/quickXorHash), pick exactly one and
     never silently fall back across algorithms — a mismatched algorithm tag is silently
     destructive to the sync reconciler (docs/PLAN-CLOUD-PROVIDERS.md R2).
   - `SupportsServerSideMove` / `SupportsServerSideCopy` / `CopyIsAsynchronous` (does copy return a
     monitor URL to poll, like Graph's 202, or complete synchronously like Proton's CLI?) /
     `SupportsBatchMove`.
   - `SupportsDelta` — does the backend have a changes/delta query at all? If not, this is `false`
     and `DeltaSource` on the facade is `null` — see `IDeltaSource`'s doc comment for why that's a
     legitimate, permanent answer for a backend like Proton's CLI, not something to fake.
   - `RequiresRemoteViewInvalidation` — does the backend (or its CLI) cache listings staler than
     the sync engine can tolerate?
   - Upload limits: `MaxSingleShotUploadBytes` / `UploadChunkSizeBytes` (does it need a chunked
     upload session at all?), `MaxRecommendedConcurrency`, `CanSetRemoteModificationTime`.
5. **Decide error mapping.** Does the backend give structured error codes (map them to
   `DriveErrorKind` directly, like `GraphErrorClassifier` reading `error.code`) or only free text
   (substring-match like `CliErrorClassifier`, and say so explicitly — it's the CLI's fault, not a
   shortcut taken here)?
6. **Write the plan section**: a `docs/PLAN-CLOUD-PROVIDERS.md` §4-style design write-up (operation
   mapping table, the capabilities above, auth flow, known risks) plus a `### P<N>` phase entry in
   the numbered phase list, `[ ]` in the Status block. Stop here and get the user's explicit
   sign-off on the plan before writing any provider code — auth flow and capability choices are
   expensive to walk back once `IDriveOperations` call sites depend on them.

## Phase 2 — Implement

Follow the signed-off plan. Deviating from it (a capability turns out different once you actually
call the API, an auth flow doesn't work) is fine and expected — just say so in the plan's own
status entry afterward, the way OneDrive's P6 entry records deviations from its own §4 design.

1. **New `Services/Providers/<Name>/` folder.** Shape depends on transport:
   - Process-based (a CLI, like Proton): one `<Name>DriveService` doing process execution +
     parsing, one thin `<Name>DriveProvider` adapter implementing `ICloudDriveProvider` over it.
     See the `cli-command` skill for the process/parsing conventions if this is your shape.
   - HTTP-based (like OneDrive/Graph): split `<Name>HttpClient` (auth header, retry-on-401,
     rate-limit backoff), `<Name>Operations` (`IDriveOperations`), `<Name>Authenticator`
     (`IDriveAuthenticator`), `<Name>PathSyntax` (`IProviderPathSyntax`), request/response DTOs,
     and `<Name>Provider` (the thin facade wiring them together) as separate files.
   - Either way: a `<Name>ErrorClassifier` (or equivalent) mapping to `DriveErrorKind`, and a
     `ProviderCapabilities` instance matching exactly what the plan decided (with the same
     justification carried into the doc comment, not just the plan file).
2. **AOT.** The app publishes `PublishAot=true`. Every serialized DTO needs
   `[JsonSerializable(typeof(T))]` in `AppJsonContext`. Never build a JSON body with an anonymous
   type + `JsonContent.Create` — that exact mistake was made and fixed during OneDrive's build (see
   `GraphRequests.cs`'s typed request DTOs). Run the `aot-check` skill once the provider compiles.
3. **Register the provider:**
   - `ProviderId` — add the enum value.
   - `ProviderCatalog.Available` — add a `ProviderDescriptor`; `ProviderCatalog.Create` — add the
     construction case (mirror `CreateOneDrive`'s shape: build the transport/auth objects, hand
     them to the provider's constructor).
   - `App.axaml.cs` usually needs **no changes** beyond that: `BuildAccountContext` and the
     browsable-account registration loop already iterate `catalog.Available` generically (P7
     Phases A and B), so a new catalog entry gets a sync engine and a live-switchable browsing
     session automatically. The one thing that *can* need a manual touch is the hasher selection
     (`IContentHasher`) in `BuildAccountContext` — if the new provider's `RemoteHash` algorithm
     isn't `Sha1` or `QuickXor` already, add a new `IContentHasher` implementation (mirror
     `QuickXorHasher`) and extend that switch.
4. **Tests**, mirroring the OneDrive suite's shape (`tests/.../Services/Providers/OneDrive/`):
   - A fake transport double (`FakeHttpMessageHandler` for HTTP, `FakeCliExecutor` for a process)
     — never hit the real backend from a unit test.
   - Operations tests asserting the actual request shape (method, path, headers, body) against
     canned responses.
   - Path-syntax tests for the case-sensitivity and reserved-name rules decided in the plan.
   - Error-classifier tests using the backend's real error shape.
   - A golden-vector test for any nontrivial hash algorithm, confirmed against real output — this
     is exactly how OneDrive's QuickXorHash implementation bug (a 160-bit circular buffer
     miscoded as 192-bit physical storage) was caught; a hash algorithm ported from a spec without
     a real confirmed vector is unverified, not implemented.
   - An opt-in integration test gated by a custom `[XIntegrationFactAttribute]` (mirror
     `OneDriveIntegrationFactAttribute`: an env-var switch, e.g. `MYPERSONALDRIVE_X_INTEGRATION=1`),
     for whatever truly needs a live account (auth flow, real listing shape).
5. **Live verification is mandatory, not optional.** Every assumed shape from Phase 1 — JSON
   fields, hash output, path encoding, error codes — must be confirmed against the real backend
   before this is considered done, the same way OneDrive's live-verification session caught the
   QuickXorHash bug and the Azure app-registration platform requirement that no amount of reading
   docs would have surfaced. Record what was confirmed as an Appendix A entry in
   `docs/PLAN-CLOUD-PROVIDERS.md`, the same way `PLAN-LOCAL-SYNC.md`'s own Appendix A records
   verified Proton CLI behavior. If live verification needs the user to complete an OAuth login or
   similar interactive step, drive it through the `run-app` skill's Browser-pane flow.
6. **Docs.** Flip the plan's `### P<N>` phase entry to `[x]` and describe what actually shipped,
   naming files and types (per `plan-doc`'s own rules — don't just mark it done). Update
   `docs/ARCHITECTURE.md`'s header commit pointer. Update `README.md` if the provider needs
   external setup (an app registration, an API key, a CLI install) — mirror the README's existing
   "OneDrive setup" section.
7. **Verify**: `./scripts/run-tests.sh` green, then the `run-app` skill to actually browse the new
   provider for real, then the `smoke-test` skill before calling it done — a provider that only
   passes unit tests has not been shown to work.

## Checklist

- [ ] Plan written in `docs/PLAN-CLOUD-PROVIDERS.md` (capabilities, auth, path syntax, error
      mapping) and explicitly approved before any provider code was written
- [ ] `ICloudDriveProvider` + required sub-interfaces implemented; optional ones (`IRemoteViewInvalidator`,
      `IProviderDiagnostics`, `IDeltaSource`) are `null` where the backend genuinely has nothing,
      not stubbed
- [ ] `ProviderCapabilities` matches the plan, each field justified
- [ ] Content-hash algorithm never silently mismatched or falls back across algorithms
- [ ] New DTOs registered in `AppJsonContext`; no anonymous-type `JsonContent.Create`; `aot-check` run
- [ ] `ProviderId` / `ProviderCatalog` updated; `App.axaml.cs` touched only if a new hasher was needed
- [ ] Fake-transport tests for operations, path syntax, error classification; golden vector for any
      hash algorithm
- [ ] Live verification done against a real account; findings recorded in Appendix A
- [ ] Plan's phase entry flipped to `[x]` with what shipped; `ARCHITECTURE.md` header updated;
      `README.md` updated if external setup is needed
- [ ] `scripts/run-tests.sh` green; app run for real via `run-app`; `smoke-test` passed
