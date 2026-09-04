# my-personal-drive

Avalonia desktop app for browsing Proton Drive through the Proton Drive CLI.

## Requirements

- .NET SDK 10
- Proton Drive CLI installed and configured (for the Proton provider)
- An Azure app registration (for the OneDrive provider — see [OneDrive setup](#onedrive-setup))
- A Google Cloud Console OAuth client (for the Google Drive provider — see
  [Google Drive setup](#google-drive-setup))
- Linux, Windows, or macOS

## Features

- Three cloud providers — Proton Drive (via the CLI), OneDrive (via Microsoft Graph, sign in with
  your Microsoft account), and Google Drive (via the Drive API, sign in with your Google account) —
  any number of them can be configured and syncing at once; the settings picker chooses which one
  you're *browsing* (restart to change that)
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
  automatically with the on/off choice persisted across restarts. Every provider syncs
  independently — pausing one doesn't affect the others. A one-way pair can mirror the destination
  exactly (deleting whatever isn't at the source, the historical behavior) or, unchecked, sync
  additively — never deleting files the destination already had. The same local folder can be
  synced to several providers at once as long as every pair sharing it is upload-only, since none
  of them ever writes back to that folder — any other combination (a pair that downloads or
  mirrors into a shared folder) is rejected
- Right-click a row in either pane for a context menu: copy its path, upload into or download a
  cloud folder, start a sync pair pre-filled with that path, pause/resume/run-now an existing pair,
  rename or delete a local item, and view its properties. Folders with an active sync pair show a
  small badge (paused pairs show a different one) — see badge in list view and the local pane
- Copy a share link for a cloud item to the clipboard (OneDrive and Google Drive — Proton Drive's
  CLI has no such command, so the menu entry stays disabled there, with a tooltip explaining why)
- Live console with realtime output from the CLI (Proton) and HTTP requests (OneDrive, Google
  Drive), tagged by account when several are active — collapsible (`Ctrl/Cmd+~`, remembered across
  restarts), with a search box, a warnings/errors-only filter, and a floating status line (active
  operation count, last log line) while collapsed
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

## Google Drive setup

Google Drive needs its own Cloud Console OAuth client — same reasoning as OneDrive's app
registration: the app has no client ID/secret of its own baked in, so each install brings its own.

1. In the [Google Cloud Console](https://console.cloud.google.com), create (or pick) a project,
   then go to **APIs & Services → Library** and enable the **Google Drive API**.
2. Go to **APIs & Services → OAuth consent screen**. Choose **External** (unless you have a Google
   Workspace organization) and fill in the required fields. Add the **`.../auth/drive`** scope
   ("See, edit, create, and delete all of your Google Drive files") — the app needs the broad
   scope, not `drive.file`, since it syncs whatever folder you point it at, not files picked one at
   a time through a picker. While the app is in "Testing" publishing status, add your own Google
   account under **Test users**, or sign-in will be refused.
3. Go to **APIs & Services → Credentials → Create Credentials → OAuth client ID**. Application
   type: **Desktop app**. Any name.
4. Copy the **Client ID** and **Client secret** from the credential's details (Google still issues
   a secret for a Desktop app client, even though it isn't required to stay confidential for this
   client type the way a web-app secret would be).
5. In the app, go to **Settings → Connection → Google Drive** and paste both into "OAuth client ID"
   and "OAuth client secret", then click the sign-in button.

Because this app requests the broad `drive` scope, sign-in shows Google's "unverified app" warning
screen while the OAuth client stays in "Testing" status — click through it (Advanced → Go to
{app name} (unsafe)) the same way you would for any other personal-use OAuth client that hasn't
gone through Google's verification process.

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
  OneDrive and Google Drive.
- File operations are delegated to the Proton Drive CLI (Proton), Microsoft Graph (OneDrive), or
  the Google Drive API (Google Drive).
- The UI shows the current CLI command / HTTP request and live output in the bottom console panel.
