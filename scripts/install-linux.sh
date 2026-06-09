#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME="${1:-linux-x64}"
PACKAGE_DIR="$ROOT_DIR/artifacts/$RUNTIME/package"
APP_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_INSTALL_DIR="$APP_DATA_HOME/MyPersonalDrive"
APP_BIN="$APP_INSTALL_DIR/MyPersonalDrive"
DESKTOP_DIR="$APP_DATA_HOME/applications"
ICON_DIR="$APP_DATA_HOME/icons/hicolor/256x256/apps"
DESKTOP_FILE="$DESKTOP_DIR/MyPersonalDrive.desktop"

if [[ ! -f "$PACKAGE_DIR/MyPersonalDrive" ]]; then
  echo "Package not found at $PACKAGE_DIR/MyPersonalDrive"
  echo "Run scripts/publish-linux.sh first."
  exit 1
fi

mkdir -p "$APP_INSTALL_DIR" "$DESKTOP_DIR" "$ICON_DIR"
cp "$PACKAGE_DIR/MyPersonalDrive" "$APP_BIN"
cp "$PACKAGE_DIR/MyPersonalDrive.png" "$ICON_DIR/MyPersonalDrive.png"
chmod +x "$APP_BIN"

cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=MyPersonalDrive
Comment=Proton Drive GUI Client
Exec=$APP_BIN
Icon=MyPersonalDrive
Terminal=false
Categories=Network;Utility;
StartupNotify=true
EOF

echo "Installed MyPersonalDrive to: $APP_INSTALL_DIR"
echo "Desktop entry written to: $DESKTOP_FILE"
echo "You may need to log out/in or refresh desktop database to see launcher immediately."
