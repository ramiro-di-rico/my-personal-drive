# my-personal-drive

Avalonia desktop app for browsing Proton Drive through the Proton Drive CLI.

## Requirements

- .NET SDK 10
- Proton Drive CLI installed and configured (for the Proton provider)
- An Azure app registration (for the OneDrive provider — see [OneDrive setup](#onedrive-setup))
- Linux, Windows, or macOS

## Features

- Two cloud providers — Proton Drive (via the CLI) and OneDrive (via Microsoft Graph, sign in with
  your Microsoft account) — both can be configured and syncing at once; the settings picker
  chooses which one you're *browsing* (restart to change that)
- Authenticate and logout through the CLI
- Auto-load the active provider's root folder after authentication
- Browse folders with breadcrumb navigation
- Go back to parent folders without leaving `/my-files`
- Quick-filter either pane's current folder by name (case-insensitive, no CLI call) — combines with
  the cloud pane's type filter chips; both reset when you navigate to a different folder
- Browse the local filesystem in a resizable second pane alongside the cloud one — its own
  breadcrumb, a home shortcut, a hidden-files toggle, and a free-space indicator
- Show or hide the local pane from the header, and the Status/Metrics sidebar from a "User
  Settings" checkbox in settings — each choice is remembered and is also what the next launch
  starts with
- Select several rows at once in either pane — Ctrl/Cmd+Click adds or removes one row,
  Shift+Click selects the range from the last one you touched, Ctrl/Cmd+A selects everything — and
  batch-download or trash them in the cloud pane, or batch-delete them locally
- Drag a file or folder between the cloud and local panes to upload/download it — onto empty space
  to target the folder currently open, or onto a folder row to target that folder — tracked in a
  cancellable transfer queue (Status sidebar). The target pane and, if you're over one, the
  specific folder row light up, with a badge showing exactly where the drop will land
- Show file and folder metadata in the status pane
- Download files
- View plain-text files, common image formats (JPEG, PNG, GIF, BMP, WebP, ICO) and PDFs in the app,
  without downloading them to disk — from a row action, a context menu entry, or the "Visor"
  menu button. PDF pages are rendered as images (up to the first 20 pages of a document); a zoom
  slider on the image/PDF viewer (default 50%) is remembered across restarts
- Upload files to the current folder
- Move files and folders to trash (folders ask for confirmation first)
- Sync a remote folder with a local folder — download-only, upload-only, or two-way, running
  automatically with the on/off choice persisted across restarts. Proton and OneDrive each sync
  independently — pausing one doesn't affect the other. A one-way pair can mirror the destination
  exactly (deleting whatever isn't at the source, the historical behavior) or, unchecked, sync
  additively — never deleting files the destination already had. The same local folder can be
  synced to several providers at once as long as every pair sharing it is upload-only, since none
  of them ever writes back to that folder — any other combination (a pair that downloads or
  mirrors into a shared folder) is rejected
- Right-click a row in either pane for a context menu: copy its path, upload into or download a
  cloud folder, start a sync pair pre-filled with that path, pause/resume/run-now an existing pair,
  rename or delete a local item, and view its properties. Folders with an active sync pair show a
  small badge (paused pairs show a different one) — see badge in list view and the local pane
- Live console with realtime output from the CLI (Proton) and Graph requests (OneDrive), tagged
  by account when both are active — collapsible (`Ctrl/Cmd+~`, remembered across restarts), with a
  search box, a warnings/errors-only filter, and a floating status line (active operation count,
  last log line) while collapsed
- Show the installed `proton-drive` CLI version in the settings view
- Check Proton's published releases for a newer CLI, and install it after verifying its SHA-512

## Run locally

```bash
cd src/MyPersonalDrive
dotnet run
```

On first launch, point the app to the `proton-drive` executable. After authentication, the app stores the CLI path and auth state locally so it can reopen in the same state next time.

## OneDrive setup

OneDrive needs its own Azure app registration — the app has no client ID of its own baked in, so
each install brings its own (a public client ID isn't secret, but it's still *your* Azure
resource, with your own rate limits and audit trail; embedding one in a shared binary would put
everyone who runs it through the same registration).

1. In the [Azure portal](https://portal.azure.com), go to **Azure Active Directory → App
   registrations → New registration**. Any name; **Personal Microsoft accounts and organizational
   accounts** (or whichever account types you need) as the supported account type.
2. Open the registration → **Authentication → Add a platform → Mobile and desktop applications**.
   This step is easy to miss and the app won't sign in without it: without this specific platform,
   Microsoft rejects the login with `invalid_request: redirect_uri is not valid`, even though the
   registration itself exists.
3. Under that platform, register the redirect URI exactly as `http://localhost` (no port, no
   trailing slash) — the app requests `http://localhost:{a random free port}` at sign-in time, and
   Microsoft matches that against the port-less registration.
4. Copy the registration's **Application (client) ID** from the Overview page.
5. In the app, go to **Settings → Connection → OneDrive** and paste it into "Azure app
   registration client ID", then click the sign-in button.

No client secret is needed — this is a public client (PKCE), and the app never asks for one.

## Linux Build and Installation

To build and package the application for Linux:

```bash
./scripts/publish-linux.sh
```

This creates a standalone package and a tarball in `artifacts/linux-x64/`.

To install the application to your local system (includes desktop entry and icon integration):

```bash
./scripts/install-linux.sh
```

The app will be installed to `~/.local/share/MyPersonalDrive`.

## Notes

- Browsing starts at the active provider's own root — `/my-files` for Proton Drive, `/` for
  OneDrive.
- File operations are delegated to the Proton Drive CLI (Proton) or Microsoft Graph (OneDrive).
- The UI shows the current CLI command / Graph request and live output in the bottom console
  panel.
