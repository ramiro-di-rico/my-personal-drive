# my-personal-drive

Avalonia desktop app for browsing Proton Drive through the Proton Drive CLI.

## Requirements

- .NET SDK 10
- Proton Drive CLI installed and configured
- Linux, Windows, or macOS

## Features

- Authenticate and logout through the CLI
- Auto-load `/my-files` after authentication
- Browse folders with breadcrumb navigation
- Go back to parent folders without leaving `/my-files`
- Show file and folder metadata in the status pane
- Download files
- Upload files to the current folder
- Move files to trash
- Live CLI command console with realtime output

## Run locally

```bash
cd src/MyPersonalDrive
dotnet run
```

On first launch, point the app to the `proton-drive` executable. After authentication, the app stores the CLI path and auth state locally so it can reopen in the same state next time.

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

- The app is currently centered on `/my-files`.
- File operations are delegated to the Proton Drive CLI.
- The UI shows the current CLI command and live output in the bottom console panel.
