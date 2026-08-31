# MyPersonalDrive — Technical Reference

> Reference document describing the current state of the application (branch
> `feature/cloud-providers-seam`, commit `3538eb7`).
> Meant to give full context to any future chat/session without having to re-read all the code.

---

## 1. What this is

An **Avalonia UI 12** (.NET 10) desktop application that acts as a graphical front-end for the
**official Proton Drive CLI** (`proton-drive`). The app **does not talk to Proton's API directly**:
everything is done by launching CLI processes, parsing their stdout, and rendering the result.

Current functional state: browse `/my-files`, upload, download, rename, copy, create folder,
move to trash, and a live console showing CLI output. There's a local SQLite cache used to
paint the UI instantly while the CLI responds.

The listing can be shown as a **list, an icons grid or a gallery** of large tiles (the choice is
persisted in `settings.json`), with per-type icons from `FileKindClassifier`. The side panel shows
**per-directory metrics**: counts, total size and a type histogram computed for free from the
listing already on screen, plus an opt-in **recursive scan** (`FolderStatsScanner`, progress and
cancel) whose result is persisted in the `FolderMetrics` table and then annotated onto folder rows.
A recursive scan costs ~3.5 s per subfolder, so it is never automatic. See
[PLAN-BROWSER-VIEWS.md](PLAN-BROWSER-VIEWS.md).

Synchronization with a local directory is **present** as of commit `87e91d6`: sync pairs
(download-only, upload-only, or two-way) that run on their own, with the on/off choice persisted
across restarts. See [PLAN-LOCAL-SYNC.md](PLAN-LOCAL-SYNC.md) for the design.

Proton is currently the only supported provider, but the app no longer talks to
`ProtonDriveService` directly anywhere outside `Services/Providers/Proton/` — every consumer goes
through `ICloudDriveProvider` (§4, §5). See [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md) for
the seam and the plan to add Microsoft OneDrive as a second provider.

---

## 2. Stack and dependencies

| Component | Version / detail |
|---|---|
| TargetFramework | `net10.0` |
| UI | Avalonia.Desktop / Themes.Fluent / Fonts.Inter `12.0.4` |
| Persistence | `Microsoft.Data.Sqlite` `10.0.10` |
| Linux natives | `SkiaSharp.NativeAssets.Linux.NoDependencies` `3.119.4` |
| Build | `PublishAot=true`, `TrimMode=partial`, `IsAotCompatible=true` |
| Bindings | `AvaloniaUseCompiledBindingsByDefault=true` |

Consequences of **Native AOT** (important for any future change):

- **No unannotated reflection.** Settings JSON serialization uses **source-generated**
  `System.Text.Json` via [`AppJsonContext`](../src/MyPersonalDrive/Services/AppJsonContext.cs).
  Any new type that gets serialized **must** be added with `[JsonSerializable(typeof(X))]`.
  Parsing of the CLI output uses `JsonDocument` (not reflection-based), so that's safe.
- XAML bindings are compiled (`x:DataType` is mandatory on every `DataTemplate`).
- Native libs (`libSkiaSharp.so`, `libHarfBuzzSharp.so`, `libe_sqlite3.so`) are packaged
  manually in the publish script.

---

## 3. Repository layout

```
src/MyPersonalDrive/
  Program.cs                       # Avalonia entrypoint (STAThread)
  App.axaml(.cs)                   # composition root: wires everything by hand
  Models/
    DriveItem.cs                   # immutable record of a drive node
    NodeFingerprint.cs             # per-node snapshot; HashAlgorithm tags which algorithm produced ContentHash
    RemoteHashAlgorithm.cs         # None|Sha1|Sha256|QuickXor
    UploadConflictStrategy.cs      # enum None|KeepBoth|Replace|Skip
  Services/
    Providers/
      ICloudDriveProvider.cs       # the facade every consumer talks to (see §5); exposes Paths
      IDriveOperations.cs / IDriveAuthenticator.cs / IRemoteViewInvalidator.cs / IProviderDiagnostics.cs
      IProviderPathSyntax.cs       # Combine / IsRemoteNameMappableLocally / Comparison
      IContentHasher.cs / Sha1ContentHasher.cs
      ProviderCapabilities.cs / ProviderId.cs
      ProviderActivity.cs          # provider-neutral activity feed (Started/Output/Finished)
      DriveException.cs / DriveErrorKind.cs
      Proton/
        ProtonPathSyntax.cs        # delegates to ProtonDriveService.CombinePath/HasUnmappableName
        ProtonDriveProvider.cs     # adapts ProtonDriveService to ICloudDriveProvider; maps its
                                    # three Cli*EventArgs events onto one ProviderActivity feed
        ProtonDriveService.cs      # translates domain operations -> CLI args + parsing
        ProtonDriveCliLocator.cs / IProtonDriveCliLocator.cs
        ProtonDriveCliExecutor.cs / IProtonDriveCliExecutor.cs
        CliCommandEventArgs.cs     # Started / Output / Finished — Proton-internal, CLI-shaped
        CliErrorClassifier.cs, CliReleaseFeed.cs, CliUpdateInstaller.cs, CliPlatformKey.cs, CliVersionComparer.cs
    DriveCacheService.cs           # SQLite cache of listings
    AppSettings.cs / AppSettingsService.cs / AppJsonContext.cs
    LocalFileSystemService.cs      # local pane's only filesystem access (read-only browsing)
  ViewModels/
    ObservableObject.cs            # minimal INotifyPropertyChanged
    AsyncCommand.cs                # async ICommand with re-entrancy guard
    MainWindowViewModel.cs         # ~950 lines, the brain of the app
    DriveNodeViewModel.cs          # one row of the listing
    BreadcrumbSegmentViewModel.cs
    Local/
      LocalExplorerViewModel.cs    # local pane's state (current path, listing, hidden-files toggle)
      LocalNodeViewModel.cs        # one row of the local listing — navigation only, no file ops yet
  Views/
    MainWindow.axaml(.cs)          # UI + dialogs built in code-behind
scripts/publish-linux.sh           # AOT publish + tarball
scripts/install-linux.sh           # installs to ~/.local/share/MyPersonalDrive + .desktop
deploy/linux, dist/, artifacts/    # packaging outputs (not versioned)
```

There is no test project. There is no DI container: the object graph is wired by hand.

---

## 4. Composition root

[`App.axaml.cs`](../src/MyPersonalDrive/App.axaml.cs) — all the wiring in a single method. Since
the provider seam (docs/PLAN-CLOUD-PROVIDERS.md P1–P5), everything below the composition root
depends on `ICloudDriveProvider`/`IDriveOperations`, never on `ProtonDriveService` directly. Since
P7 Phase A, it builds **one `AccountSyncContext` per provider `ProviderCatalog.Available` lists**
(`Services/AccountSyncContext.cs` — `Provider`, `AccountKey`, `DisplayName`,
`CacheService`/`StateStore`/`MetricsStore`, `Executor`, `Scheduler`), not one provider for the
whole app:

```
ProviderCatalog.Available ──► BuildAccountContext(provider) per entry ──► [AccountSyncContext, AccountSyncContext, …]
                                                                                    │
                             pick "primary" = AppSettings.ActiveProviderOrDefault() │
                                    ├─► MainWindowViewModel (browses only the primary)
                                    ├─► SyncPanelViewModel(primary) + .AddAccount(other) per remaining context
                                    └─► MainWindowViewModel.ObserveAdditionalProviderActivity(other) per remaining context
```

`ProviderCatalog` (`Services/Providers/ProviderCatalog.cs`) is the one place that knows how to
build either provider's construction chain (Proton: `ProtonDriveCliLocator` →
`ProtonDriveCliExecutor` → `ProtonDriveService` → `ProtonDriveProvider`; OneDrive:
`OneDriveTokenStore` → `GraphAuthenticator` → `GraphHttpClient` → `OneDriveProvider`); both
constructors are cheap and side-effect-free even when unconfigured (an empty
`CliPath`/`OneDriveClientId` — real failures surface lazily, on the first actual operation), which
is what makes building a context for every available provider unconditionally safe. `Available`
also feeds `MainWindowViewModel.AvailableProviders` for the settings view's provider picker.
`SyncExecutor`, `RemoteScanner`, `FolderStatsScanner` and `SyncPanelViewModel`'s
`GetRemoteFolderChildren` delegate are all built from `provider`/`provider.Operations`, never from
a provider-specific type — adding OneDrive meant a case in `ProviderCatalog.Create`, not a change
to any of those consumers. Each context picks its own `SyncExecutor` content hasher from
`provider.Capabilities.RemoteHash` (`Sha1ContentHasher` vs `QuickXorHasher`) rather than hardcoding
Proton's, and computes its own `accountKey` as `{provider.Id}:default` lowercased (`"proton:
default"`/`"onedrive:default"`) — without this, every store's own `"proton:default"` constructor
default would apply regardless of which provider is active, letting one account's cache/sync-pair
rows collide with another's under the same sentinel.

**P7 Phase A** (docs/PLAN-CLOUD-PROVIDERS.md): Proton and OneDrive can both be configured and
syncing *at once*, not just one "active" provider — narrowed from a general multi-account design
because Proton's CLI has no multi-account concept of its own, so at most one session per provider
*type* exists. Only the *browsing* UI still shows one account (the "primary" — the persisted
`ActiveProvider` preference); sync and the console activity feed run for every built context
regardless. `SyncPanelViewModel.AddAccount` merges a second account's pairs into one `Pairs` list
(labeled per row once there's more than one account) with its own independent
`AccountSyncToggleViewModel`, and `MainWindowViewModel.ObserveAdditionalProviderActivity` tags
console lines by account. A real gap P7 Phase A's own live testing found and fixed: `AppSettings`
(`CliPath`/`IsAuthenticated` vs `OneDriveClientId`/`IsOneDriveAuthenticated`) are still flat rather
than a shared provider-keyed structure (deliberately — see §5.4's "Settings surface" note); a
provider-picker switch still confirms and restarts for changing what's *browsed*
(browsing-account-switch-without-restart is Phase B, not built yet), and does not warn about
affected sync pairs; `":default"` is still a fixed per-provider suffix, not a real per-account
identity — true same-provider multi-account (P7's general form) stays out of scope, needing Proton
CLI config-directory isolation.

Nothing is a container singleton; they're single instances created by hand.

---

## 5. The providers

Two backends exist behind `ICloudDriveProvider` (§4): Proton (a CLI process) and, since P6,
OneDrive (Microsoft Graph over HTTP). Everything in §5.1–§5.3 lives under
`Services/Providers/Proton/` and is reached only through `ICloudDriveProvider` — nothing outside
that folder should name these types directly except the composition root and the doc-flagged P3
follow-ups in `RemoteScanner`/`MainWindowViewModel` (path-syntax calls not yet behind an
interface). §5.4 covers OneDrive, under `Services/Providers/OneDrive/`, on the same rule.

### 5.1 `ProtonDriveCliLocator`

Resolution order ([`ProtonDriveCliLocator.cs:31`](../src/MyPersonalDrive/Services/Providers/Proton/ProtonDriveCliLocator.cs)):

1. `settings.CliPath` if the file exists.
2. Sweep of `$PATH` looking for `proton-drive` (on Windows, cross-referenced with `PATHEXT`).
3. If not found: `FileNotFoundException`.

Relevant detail: **it re-reads `settings.Load()` on every `Locate()`** — i.e., the settings JSON
is read from disk on every command. Inefficient, but it makes changing the path in the UI take
effect immediately.

### 5.2 `ProtonDriveCliExecutor`

[`ProtonDriveCliExecutor.cs`](../src/MyPersonalDrive/Services/Providers/Proton/ProtonDriveCliExecutor.cs) —
`ExecuteAsync(arguments, ct)`:

- `ProcessStartInfo` with `UseShellExecute=false`, `CreateNoWindow=true`, stdout+stderr redirected.
- Reads stdout and stderr **line by line in parallel**, accumulating into a `StringBuilder`
  (with `lock`) and emitting `CommandOutput` per line → this feeds the live console.
- `cancellationToken.Register` → `process.Kill(entireProcessTree: true)`.
- Events: `CommandStarted(commandText)` → N×`CommandOutput(text, isError)` → `CommandFinished(commandText, exitCode)`.
- **Exit code ≠ 0 ⇒ throws `InvalidOperationException`** with the stderr text (or stdout if
  stderr is empty). This is the error contract for the whole app: almost every `catch` block
  above specifically catches `InvalidOperationException`.

Known limitations of this layer:

- No timeout.
- `ReadStreamAsync` checks the token between lines, but `ReadLineAsync()` itself is not
  cancelable (it unblocks because the process gets killed).
- No controlled concurrency: two simultaneous commands are two simultaneous processes.

### 5.3 `ProtonDriveService`

[`ProtonDriveService.cs`](../src/MyPersonalDrive/Services/Providers/Proton/ProtonDriveService.cs) — maps each
operation to a command line. **This is the catalog of what's currently known about the CLI:**

| Method | Command emitted |
|---|---|
| `LoadFolderAsync` / `GetChildrenAsync` | `filesystem list --json "<path>"` |
| `AuthenticateAsync` | `auth login` |
| `LogoutAsync` | `auth logout` |
| `DownloadFileAsync` | `filesystem download "<path>" "<localFolder>"` |
| `TrashItemAsync` | `filesystem trash "<path>"` |
| `RenameItemAsync` | `filesystem rename "<path>" "<newName>"` |
| `CreateFolderAsync` | `filesystem create-folder "<parent>" "<name>"` |
| `CopyItemAsync` | `filesystem copy [-n "<newName>"] "<src>" "<targetParent>"` |
| `UploadFilesAsync` | `filesystem upload [-f rename\|replace\|skip] [-d rename\|replace\|skip] "<f1>" "<f2>"… "<parent>"` |
| `GetCliVersionAsync` | `--version` |

`--version` is the one command here that is not a subcommand. Captured from `cli-drive@0.8.0`:

```
Proton Drive CLI cli-drive@0.8.0+06e8c605
Proton Drive SDK js@0.21.0+06e8c605
```

**Regression found live (2026-08-28):** `cli-drive` 0.8.0 replaced the single `-c keep-both|replace|skip`
flag `filesystem upload` used to take with two separate ones (`-f`/`--file-conflict-strategy` and
`-d`/`--folder-conflict-strategy`), and renamed `keep-both` to `rename`. The old `-c` is simply
unrecognized now, which silently broke every upload that specified a strategy — including this
app's own default `Replace`-strategy retry for a plain changed-file upload
(`SyncExecutor.UploadFileAsync`), not just conflict resolution. A sync pair with any file edited on
both sides would upload the initial version fine, then fail every subsequent update forever, always
landing back in "Conflict"/"Failed" no matter how many times the user retried or resolved it by
hand — because the retry and the resolution both route through the same broken upload call. Fixed
in `ProtonDriveService.UploadFilesAsync` to send both new flags with the same resolved value.

The service returns the first line verbatim and parses nothing — the app only displays it, and
splitting `cli-drive@0.6.0+f8e16aac` into fields would assume a format the CLI hasn't promised.
The second line is the bundled SDK, not the CLI, so it is dropped. `--help` on that build lists
**no `update`/`self-update` subcommand**, so checking for a newer release needs an external source —
Proton's published release manifest. See §10.

Forwards all three executor events upward.

**Listing parsing** — `ParseListing` tries JSON first and falls back to text:

1. `TryParseJsonListing`: accepts a root array, or an object with `items` / `entries` / `children`.
   For each entry it uses alias-tolerant readers:
   - name: `name` | `title` | `label`
   - type: `type` | `kind` | `entryType`; it's a folder if it contains `folder`/`directory`/`dir`
   - size: `size` | `bytes`
   - date: `modifiedAt` | `updatedAt` | `lastModified` (**stored as a raw string, not parsed
     into a DateTime**)
   - owner: `owner` | `user` | `createdBy`
   - shared: `isShared` | `shared` | `linkShared`
   All of these also accept the nested form `{ "value": … }`.
   If the result has 0 items, it **returns false** and falls back to the text parser (this means
   "empty folder" and "couldn't parse" get conflated).
2. `ParseTextListing`: heuristic over plain text — `isFolder` = the line contains `🗂`, and the
   name is whatever comes after the **last space**. Fragile with names that contain spaces.

**Paths**: `CombinePath(parent, name)` — string join with `/`, normalizing the root.
`Quote(v)` = double quotes with `"` escaped. Arguments are concatenated as a string, not as an
array; quoting is the only defense (`LoadFolderAsync` even interpolates the quotes by hand
instead of using `Quote`).

### 5.4 The OneDrive provider (Microsoft Graph)

No CLI to shell out to — this provider talks HTTP directly. docs/PLAN-CLOUD-PROVIDERS.md §4 has
the full request-by-request design; this is the as-built shape.

| Piece | Role |
|---|---|
| [`GraphAuthenticator`](../src/MyPersonalDrive/Services/Providers/OneDrive/GraphAuthenticator.cs) | `IDriveAuthenticator`. Authorization-code + PKCE via a loopback `HttpListener` (no MSAL, no device-code fallback — a documented gap); exchanges/refreshes tokens against `login.microsoftonline.com`; `GetValidAccessTokenAsync`/`ForceRefreshAsync` are what `GraphHttpClient` calls before/after a 401 |
| [`OneDriveTokenStore`](../src/MyPersonalDrive/Services/Providers/OneDrive/OneDriveTokenStore.cs) | Persists the refresh/access token pair to `onedrive-token.json` under `AppSettingsService.BaseFolder`, chmod 600 — at-rest plaintext, an accepted risk (R3) matching where Proton's own CLI keeps its session |
| [`GraphHttpClient`](../src/MyPersonalDrive/Services/Providers/OneDrive/GraphHttpClient.cs) | Attaches the bearer token, retries once on 401 after forcing a refresh, honors `Retry-After` on 429/503, raises `Activity` events per request so the console shows Graph calls the same way it shows a Proton CLI command |
| [`GraphErrorClassifier`](../src/MyPersonalDrive/Services/Providers/OneDrive/GraphErrorClassifier.cs) | Reads the structured `{"error":{"code":…}}` body → `DriveErrorKind`, instead of substring-matching like Proton's classifier has to |
| [`OneDriveOperations`](../src/MyPersonalDrive/Services/Providers/OneDrive/OneDriveOperations.cs) | `IDriveOperations`. Paginated listing (follows `@odata.nextLink` to exhaustion), small vs. chunked upload (4 MiB single-shot ceiling, 320 KiB-multiple chunks), asynchronous copy (`202` + polled monitor URL), one `GET` per distinct move/copy target parent (cached per instance — `SupportsBatchMove = false`) |
| [`OneDrivePathSyntax`](../src/MyPersonalDrive/Services/Providers/OneDrive/OneDrivePathSyntax.cs) | `Comparison = OrdinalIgnoreCase` (OneDrive is case-insensitive, unlike Proton/Linux); `IsLocalNameMappableRemotely` (§4.6/O6 reserved-name rule — new `IProviderPathSyntax` member this phase added, Proton implements it as always-true) |
| [`QuickXorHasher`](../src/MyPersonalDrive/Services/Providers/OneDrive/QuickXorHasher.cs) | `IContentHasher` for `RemoteHashAlgorithm.QuickXor`. Live-verified (Appendix A #3) — its first version was wrong (a 192-bit accumulator storage detail leaking into what must be a genuinely circular 160-bit width), caught by comparing against Graph's own reported hash; fixed and confirmed matching on two separate uploads |

**Hash tagging**: `OneDriveOperations.ToDriveItem` only ever reads `file.hashes.quickXorHash` —
deliberately never falling back to `sha1Hash`/`sha256Hash` when a drive doesn't return it, because
this provider's `Capabilities.RemoteHash` is the fixed value `QuickXor`; tagging a sha1-only item's
hash as QuickXor would silently mislabel it, exactly the "hash-algorithm mismatch is silent and
destructive" failure P3's `IsAlgorithmMismatch` guard exists to prevent. A file with no
quickXorHash just gets no content hash, a safe degrade (RemoteScanner already treats a null hash
as "unknown algorithm, don't compare").

**Auth flow, mechanically**: `AuthenticateAsync` reserves a loopback port (a throwaway
`TcpListener` on port 0, then handed to `HttpListener` by exact port number — `HttpListener` has
no "any free port" mode of its own), builds the `login.microsoftonline.com` authorize URL against
the registered port-less `http://localhost` redirect URI, launches the system browser
(`Process.Start(UseShellExecute: true)`) and also emits the URL through `Activity` so it's visible
in the console even if no browser could be launched, then blocks on the loopback listener for the
redirect carrying the authorization code. `AppSettings.OneDriveClientId` (entered in Settings, not
embedded in the binary) is the public-client application id; `Files.ReadWrite.All offline_access
User.Read` is the fixed scope set. **Azure setup note (Appendix A #1):** the app registration needs
its "Mobile and desktop applications" platform added explicitly (Authentication → Add a platform),
registered with the port-less `http://localhost` redirect URI — without that specific platform,
Microsoft rejects the loopback redirect with `invalid_request: redirect_uri is not valid` even
though the registration itself exists.

**Settings surface**: `AppSettings.OneDriveClientId` (string) and `IsOneDriveAuthenticated` (bool)
sit alongside Proton's `CliPath`/`IsAuthenticated` rather than a shared provider-keyed structure —
the two providers' connection cards are structurally different (CLI path + version vs. sign-in/out
+ account label), so there was nothing to share. `MainWindowViewModel.IsAuthenticated` reads/writes
whichever of the two backs the active provider, decided once at construction
(`_provider.Id == ProviderId.OneDrive`) — switching providers requires a restart (§2.7 in the
plan), so this never changes mid-session.

**Verification status**: sign-in, `ListFolderAsync`, small-file upload, and `QuickXorHasher` are
live-verified against a real account (docs/PLAN-CLOUD-PROVIDERS.md Appendix A). Pagination past one
page, chunked upload, async copy, rate-limiting, and the exact O6 reserved-name list remain per
Microsoft's documentation only, not yet captured live — R6 still applies to those specifically.

---

## 6. Persistence

### 6.1 Settings

`%APPDATA%/MyPersonalDrive` (on Linux, via `SpecialFolder.ApplicationData` ⇒ `~/.config/MyPersonalDrive`).

- `settings.json` → `{ CliPath, IsAuthenticated }`. Serialized with the generated
  `JsonSerializerContext`.
- `IsAuthenticated` is a **local assumption**: it's set to `true` after a successful `auth login`
  and to `false` after `auth logout` or if any error message contains `"login first"`. It is not
  verified against the CLI on startup.

### 6.2 SQLite cache

`<BaseFolder>/cache.db`. The schema below is migration 1's; six migrations have landed since
(see [`DriveDatabaseMigrations.cs`](../src/MyPersonalDrive/Services/DriveDatabaseMigrations.cs)
for the current, authoritative shape). Notably, migration 6
(docs/PLAN-CLOUD-PROVIDERS.md P4) added an `AccountKey` column to `DriveItems`, `FolderMetrics`
and `SyncPairs` — `"<providerId>:<accountId>"`, defaulted to `'proton:default'` — and
`DriveCacheService`/`FolderMetricsStore`/`SyncStateStore` all take the active account key in
their constructor now, scoping every query to it.

Original migration 1, for the shape/reasoning it started from:

```sql
CREATE TABLE IF NOT EXISTS DriveItems (
    Path TEXT PRIMARY KEY,
    ParentPath TEXT,
    Name TEXT,
    IsFolder INTEGER,
    Size INTEGER,
    ModifiedAt TEXT,
    Owner TEXT,
    IsShared INTEGER
);
CREATE INDEX IF NOT EXISTS idx_ParentPath ON DriveItems(ParentPath);
```

API: `GetCachedItemsAsync(parentPath)`, `SyncItemsAsync(parentPath, remoteItems)` (delete-diff +
upsert inside a transaction), `RemoveItemAsync(path)` (deletes the node **and its subtree** via
`LIKE 'path/%'`), `AddOrUpdateItemAsync(parent, item)`.

Notes:
- Opens and closes a connection per operation (no explicit pooling or long-lived connection).
- **No schema versioning or migrations.** Adding columns requires thinking through the upgrade.
- `SyncItemsAsync` calls `GetCachedItemsAsync` **outside** the transaction (opens a second connection).
- No "cached at" timestamp: the cache never expires based on time.

---

## 7. Presentation layer

### 7.1 Primitives

- `ObservableObject`: `SetProperty(ref field, value)` with `[CallerMemberName]`.
- `AsyncCommand`: `ICommand` with `async void Execute`; blocks re-entrancy with `_isExecuting`
  and re-evaluates `CanExecute` before/after. `RaiseCanExecuteChanged()` is manual.
  **Uncaught exceptions inside the `Func<Task>` crash the process** (`async void`), which is
  why every handler in the VM has its own try/catch.

### 7.2 `MainWindowViewModel`

Central state. Key pieces:

- `_rootPath = "/my-files"` — hardcoded; the app is anchored to that root.
- `_navigationHistory : Stack<string>` — "back" history (no forward).
- `_cts : CancellationTokenSource` — **a single one**; every `LoadFolderAsync` cancels the previous one.
- `RootItems : ObservableCollection<DriveNodeViewModel>` — the visible listing.
- `BreadcrumbItems` — rebuilt entirely on every navigation from the path string.
- `Selected*` block — 7 flat strings feeding the detail panel.
- `LocalExplorer : Local.LocalExplorerViewModel` — composed the same way `SyncPanel` is; the local
  pane's own state (current path, listing, breadcrumb, hidden-files toggle), backed by
  `LocalFileSystemService`. Read-only browsing only — no local file operations yet.
- `IsLocalExplorerPanelVisible` / `IsStatusPanelVisible` — whether the local pane and the Status
  sidebar are shown, each persisted (`AppSettings.ShowLocalExplorerPanel`/`ShowStatusPanel`) so the
  choice is also next launch's default. The local pane toggles from a header button
  (`ToggleLocalExplorerPanelCommand`); its column is `*`-sized (for the splitter), so hiding it also
  needs `MainWindow.axaml.cs` to collapse `ExplorerColumnsGrid.ColumnDefinitions[2]` directly — an
  `Auto` column (the Status sidebar's) already shrinks to 0 from `IsVisible` alone. The Status
  sidebar instead toggles from a plain two-way-bound "User Settings" checkbox in the settings view
  (no command, same pattern as `DefaultSyncFolder`/`BandwidthLimitKbps`).
- Console block: `_commandLogLines` (ring buffer of 200), `CommandLogText`,
  `ActiveCommand`, and animation properties (`CommandConsoleMaxHeight/Opacity/HitTestVisible/ToggleLabel/Glyph`).
  Collapse state persists (`AppSettings.ShowCommandConsole`, `Ctrl/Cmd+~`). `ShowOnlyWarningsAndErrors`/
  `LogSearchText` re-render `CommandLogText` from `CommandLogBuffer.Lines` through `RefreshCommandLogText`
  rather than keeping a second filtered copy; `ActiveOperationCount` is a real Started/Finished-pair
  count (not the fabricated "transfer rate" Task 4's wishlist asks for — nothing in the activity feed
  reports bytes/sec, and AGENTS.md rules out inventing a shape for it) and `LastLogLine` is the most
  recent buffered line, both shown in a floating status line while the console is collapsed.
- `TransferQueue : TransferQueueViewModel` — the drag-and-drop transfer queue
  (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5), sequential and cancellable, shown in the Status
  sidebar when non-empty. `HandleLocalFilesDroppedAsync(localPaths, targetPath)` is the one
  entry point code-behind's drag/drop handlers call — it resolves the upload-conflict strategy
  (reusing `UploadAsync`'s own check, factored out as `ResolveUploadConflictStrategyAsync`) and
  hands off to the queue. Drag-and-drop mechanics themselves (`OnLocalRowPointerPressed/Moved`,
  `OnCloudListingDragOver/Drop`) live entirely in `MainWindow.axaml.cs`, per the same "no Avalonia
  types in the VM" rule as everything else — the row Button's own click still works normally
  below a small pixel threshold; only a real drag (past it) calls `DragDrop.DoDragDropAsync`.

**Extension points toward the View** — the VM doesn't know about Avalonia except for
`Dispatcher`; dialogs are injected as delegates the View sets in `OnDataContextChanged`:

```
RequestUploadFilesAsync      : Func<Task<IReadOnlyList<string>>>
RequestConflictStrategyAsync : Func<IReadOnlyList<string>, Task<UploadConflictStrategy>>
RequestRenameAsync           : Func<string, Task<string?>>
RequestCopyNameAsync         : Func<string, Task<string?>>
RequestCreateFolderAsync     : Func<Task<string?>>
RequestDownloadFolderAsync   : Func<Task<string?>>
RequestSaveActivityAsync     : Func<Task<string?>>
```

This is **the pattern to follow** for any new dialog (e.g. picking a local folder to sync).

**In-app viewer (text and images).** `PreviewItemAsync`/`ViewSelectedFileCommand`/
`CloseViewerCommand` open a panel (`IsViewerVisible` + `ViewerTitle/Path/Note`, `IsViewerLoading`,
plus `ViewerText`/`HasViewerText` or `ViewerImageBytes`/`HasViewerImage` depending on what's shown)
reachable from a row's eye button, a tile's context menu, or the "Visor" nav button.
`PreviewItemAsync` routes to one of two independent flows by file kind and policy:

- Text: `Services.TextPreviewPolicy` decides which files qualify (text/code kinds, delimited
  spreadsheets, extensionless files; capped at 1 MB), and `Services.TextFilePreviewService`
  (behind `ITextFilePreviewLoader`) downloads, reads and decodes up to the policy's byte/line
  limits (UTF-8 first, Latin-1 fallback, binary sniffed by a NUL byte).
- Images: `Services.ImagePreviewPolicy` narrows `FileKind.Image` down to formats
  `Avalonia.Media.Imaging.Bitmap` (SkiaSharp) actually decodes — png/jpg/jpeg/gif/bmp/webp/ico,
  excluding RAW formats, .psd and .svg despite those sharing the same `FileKind` — capped at 25 MB.
  `Services.ImageFilePreviewService` (behind `IImageFilePreviewLoader`) downloads and hands back
  the raw bytes undecoded; `Views.Converters.BytesToBitmapConverter` turns them into a `Bitmap` in
  the View, since view models never touch Avalonia types (AGENTS.md).

Both loaders are optional VM dependencies with the same shape: the CLI has no "read a file"
command, so each downloads into a private temp folder and deletes it again — a preview never
leaves a copy on disk. The row's `DriveNodeViewModel.CanPreview` (true when either policy accepts
the file) drives the eye button/context-menu entry's visibility; the actual routing decision is
made again, from the same policies, in `PreviewItemAsync`.

### 7.3 Folder-loading flow (cache-first)

[`LoadFolderAsync`](../src/MyPersonalDrive/ViewModels/MainWindowViewModel.cs#L655):

```
LoadFolderAsync(path)
 ├─ cancels the previous CTS, creates a new one
 ├─ CurrentPath = path; UpdateBreadcrumbs(path)     ← optimistic, immediate
 ├─ cached = cache.GetCachedItemsAsync(path)
 ├─ if there's a cache:
 │    DisplayItems(cached); IsLoading = false
 │    _ = FetchFromCliAndUpdateCacheAsync(path, …)  ← fire-and-forget
 └─ if not:
      DisplayItems(empty)
      await FetchFromCliAndUpdateCacheAsync(path, …)

FetchFromCliAndUpdateCacheAsync
 ├─ items = service.LoadFolderAsync(path, token)    ← proton-drive filesystem list --json
 ├─ cache.SyncItemsAsync(path, items)
 └─ Dispatcher.UIThread.InvokeAsync: if CurrentPath is still path and not canceled →
      DisplayItems(items); StatusMessage = "Loaded N items"
```

`DisplayItems` sorts folders first, then by name, case-insensitively.

Mutations (create folder, rename, trash) update the cache **optimistically** before firing off
a background `RefreshAsync()`. `Upload` and `Copy` only refresh.

### 7.4 Error handling

`HandleLoadError` classifies by **substring of the CLI message**:

- contains `"does not exist"` / `"not found"` → warning "the path no longer exists".
- contains `"login first"` → `IsAuthenticated = false`.
- otherwise → `FormatCliError`, which also handles `"login first"` and the pseudo-paths
  `"auth login"` / `"auth logout"` used as context labels.

In other words: **error detection depends on English-language substrings from the CLI**.
Fragile against CLI version changes.

### 7.5 View

[`MainWindow.axaml`](../src/MyPersonalDrive/Views/MainWindow.axaml): a 5-row `Grid` —
header, CLI-path bar + action buttons (📂 🔑 🚪 🔄), breadcrumbs + ⬅️ 📁 📤, the listing
`TreeView` + detail panel, and the collapsible CLI console.

[`MainWindow.axaml.cs`](../src/MyPersonalDrive/Views/MainWindow.axaml.cs): pickers via
`StorageProvider` and **dialogs built imperatively** (`new Window { Content = new StackPanel {…} }`),
with buttons reached by `Children` index (`(StackPanel)panel.Children[2]`). Fragile but the
current pattern; if more dialogs are added, consider extracting them into their own classes.

---

## 8. Packaging

- `scripts/publish-linux.sh` → AOT `dotnet publish` for linux-x64, copies `libSkiaSharp.so`,
  `libHarfBuzzSharp.so`, `libe_sqlite3.so` into the package, produces a tarball under
  `artifacts/linux-x64/`.
- `scripts/install-linux.sh` → installs to `~/.local/share/MyPersonalDrive` + `.desktop` + icon.
- `scripts/appimagetool-x86_64.AppImage` is present but not wired into the versioned scripts.

---

## 9. Technical debt / known risks

List to keep handy when planning any feature.
**Remediation plan: [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md)** — note that items 1, 7 and 8
compose into three reachable crashes documented there.

1. **No tests.** There is no test project or fakes; `IProtonDriveCliExecutor` is the natural
   seam to introduce them.
2. **Fragile CLI parsing**: the text fallback based on spaces/emoji breaks with unusual names;
   `ModifiedAt` is an unnormalized string (a blocker for comparing mtimes for sync).
3. **English-substring error handling.**
4. **`0 items` == `parsing failed`** in `TryParseJsonListing`.
5. **Commands built by string concatenation** — quoting is the only defense.
6. **A single `CancellationTokenSource`** shared across all navigation; fire-and-forget
   operations (`_ = RefreshAsync()`) without await or failure handling.
7. **`async void Execute`** in `AsyncCommand`: any uncaught exception = crash.
8. **Cache with no versioning or TTL**, and `IsAuthenticated` with no real verification.
9. **No persistent logging** beyond the in-memory 200-line ring buffer.
10. **`_rootPath` hardcoded** to `/my-files`.
11. **No recursive operations**: a whole folder cannot be downloaded/deleted
    (`DownloadCommand`/`TrashCommand` are disabled for folders).
12. **No progress or throughput** for upload/download: just the raw CLI text.

---

## 10. CLI self-update (Proton's one outbound network call)

Everything else in this app reaches Proton by launching `proton-drive`. **This is the single
exception on the Proton side**, and it is deliberately narrow: it reads one public static file and
never touches the Drive API. (OneDrive has no such distinction to draw — every OneDrive operation
is itself an HTTP call to Microsoft Graph; see §5.4.)

| Piece | Role |
|---|---|
| [`CliReleaseFeed`](../src/MyPersonalDrive/Services/Providers/Proton/CliReleaseFeed.cs) | GETs `https://proton.me/download/drive/cli/version.json`, picks the `Stable` release and the file for this platform |
| [`CliPlatformKey`](../src/MyPersonalDrive/Services/Providers/Proton/CliPlatformKey.cs) | Maps the running machine to the manifest's platform key, **including the glibc/musl split** |
| [`CliVersionComparer`](../src/MyPersonalDrive/Services/Providers/Proton/CliVersionComparer.cs) | Lifts `0.6.0` out of `cli-drive@0.6.0+f8e16aac` and compares it with the manifest's bare `0.7.0` |
| [`CliUpdateInstaller`](../src/MyPersonalDrive/Services/Providers/Proton/CliUpdateInstaller.cs) | Downloads, verifies SHA-512, then atomically replaces the executable |

The manifest is the same source of truth as the human download page — the SHA-512 values match.
`CliReleaseManifest`'s PascalCase property names are the manifest's own and are registered in
`AppJsonContext`, so this stays AOT-safe.

**Why a self-update exists at all**: `proton-drive --help` on `cli-drive@0.6.0` lists no `update`
or `self-update` subcommand, so there is nothing to delegate to.

### The install invariant

After any outcome the binary on disk is **either the old working one or the verified new one** —
never a partial write, never an unverified download. The order is what buys that:

```
stream download → temp file in the target's own directory   (same filesystem → rename, not copy)
      │           hashing as it streams (~115 MB, one pass)
      ▼
SHA-512 == manifest?  ── no ──▶ delete temp, throw CliUpdateException, original untouched
      │ yes
      ▼
chmod 0755 → File.Move(temp, target, overwrite: true)       (atomic on POSIX)
```

A process already running the old binary keeps its own inode, so an in-flight CLI call is not
corrupted by a swap underneath it. The install is still refused while a sync is mid-cycle
(`SyncPanelViewModel.IsSyncInProgress`), because the *next* call in that cycle would otherwise land
on a different version mid-operation.

Two refusals are load-bearing and covered by tests: a checksum mismatch, and an installed version
that could not be parsed — the app does not offer to overwrite a working CLI on a guess.

The check runs once in the background at startup (fired from the composition root, never from a
constructor) and on demand from the settings view.

## 11. Glossary of invariants

- Every remote path starts with `/` and hangs off `/my-files`.
- `DriveItem.Path` is the primary key in the cache and the unique identifier in the UI.
  **There is no stable ID from Proton's side**: renaming = changing identity.
  This is the most important design constraint for synchronization.
- A CLI failure always surfaces upward as `InvalidOperationException`.
- The app makes **exactly one** outbound network call of its own: `CliReleaseFeed` reading the
  published CLI release manifest. Everything else goes through a `proton-drive` process. Adding a
  second one is an architectural decision, not a detail.
- The `proton-drive` binary is only ever replaced after its SHA-512 matches the published one, and
  only by an atomic rename.
- Anything touching `RootItems` / bound properties must run on the UI thread
  (`Dispatcher.UIThread.InvokeAsync` / `.Post`).
