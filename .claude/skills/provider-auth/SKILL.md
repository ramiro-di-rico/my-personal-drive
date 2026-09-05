---
name: provider-auth
description: Add or fix a provider's sign-in, token refresh, or token storage — Proton's CLI auth, OneDrive/Google Drive OAuth (PKCE loopback), token stores, expiry and sign-out. Use whenever credentials, tokens, scopes, or the authenticated/not-authenticated state are involved.
---

# Provider authentication

Two auth models live behind one seam:

| Provider | Model | Where the session lives |
|---|---|---|
| Proton Drive | `proton-drive auth login` / `auth logout`, a CLI process | the CLI's own store, outside this app |
| OneDrive | OAuth authorization-code + PKCE over a loopback `HttpListener`, no MSAL | `onedrive-token.json`, chmod 600 |
| Google Drive | same shape, Drive API v3 | `google-token.json`, chmod 600 |

The seam is `IDriveAuthenticator` (`AuthenticateAsync` / `LogoutAsync`) — deliberately minimal.
Read `docs/PLAN-CLOUD-PROVIDERS.md` §2.3 and §4.2 before widening it; the interface stays small
until a second implementation justifies the shape.

## Rules

- **Never ask the user for a password, and never handle one.** The OAuth providers hand off to
  the system browser; Proton's CLI prompts in its own process. There is no code path in this app
  that reads a credential.
- **PKCE, public client, no secret.** `GraphAuthenticator` is the reference: verifier + S256
  challenge, a `state` value that is *checked* on the redirect, a loopback redirect URI, and no
  client secret anywhere — not in source, not in `settings.json`. The client ID is user-supplied
  configuration, not a credential.
- **Refresh behind a gate, ahead of expiry.** `SemaphoreSlim` so concurrent requests refresh once,
  and a margin (`RefreshMargin`, 5 minutes) so a request in flight never races the expiry.
- **Tokens are at-rest plaintext, chmod 600, in `AppSettingsService.BaseFolder`** — an accepted
  risk documented in the token-store class doc (§4.2 R3). Don't quietly change that; don't put
  tokens in `settings.json` either.
- **A corrupt or unreadable token file degrades to "signed out"**, never to a crash.
- **Sign-out clears everything**: the token file, the cached account label, and any provider view
  cache keyed to that account.
- **Serialized token types go in `AppJsonContext`.** The app is AOT — a reflection-based
  `JsonSerializer` overload compiles and then fails in the published build.
- **Errors are typed.** Auth failures surface as `DriveException` with the right
  `DriveErrorKind` (`NotAuthenticated` for an expired/absent session), classified in that
  provider's `*ErrorClassifier`. Never match on a message upstream of the classifier.
- **Activity is observable.** Raise `ProviderActivity` for the authorize URL and the outcome, so
  the in-app console shows the same trail the CLI providers produce.

## Steps

1. Identify the layer: the *flow* (`*Authenticator`), *persistence* (`*TokenStore`), *attaching
   the token to requests* (`*HttpClient`), or *classifying the failure* (`*ErrorClassifier`).
   Changes usually belong in exactly one.
2. Check the scopes. Widening scopes is a user-visible consent change — say so, and record it in
   `docs/PLAN-CLOUD-PROVIDERS.md`.
3. Wire the state through `ProviderCatalog`/`ProviderDescriptor` so the UI's per-provider
   signed-in indicator reflects it. The UI must never call an authenticator directly from
   code-behind — it goes through the ViewModel (`add-feature` skill).
4. **Tests** — `tests/MyPersonalDrive.Tests/Services/Providers/`:
   - `FakeHttpMessageHandler` for the token endpoint. Cover: successful exchange, refresh before
     expiry, refresh *failure* (→ signed out, not a crash), and a mismatched `state` on redirect.
   - Token store: save/load round-trip, the 600 mode on POSIX (`PosixFactAttribute`), and a
     corrupt file loading as `null`.
   - Never put a real token or client ID in a fixture.
5. **Verify**:

   ```bash
   ./scripts/run-tests.sh
   ```

   The real sign-in path can only be proven interactively — `RealOneDriveAuthTests` runs under
   `MYPERSONALDRIVE_INTEGRATION=1` and needs a live account. If you didn't run it, say so.

## Checklist

- [ ] No password, no client secret, no token in source or in `settings.json`
- [ ] `state` verified on redirect; PKCE verifier generated per attempt
- [ ] Refresh gated and ahead of expiry; concurrent callers refresh once
- [ ] Token file chmod 600; corrupt file → signed out
- [ ] Token DTOs registered in `AppJsonContext`
- [ ] Failures typed via `DriveErrorKind`; `ProviderActivity` raised
- [ ] Sign-out clears token, label and account-scoped cache
- [ ] Tests incl. refresh failure and bad `state`; interactive pass run or reported as not run
