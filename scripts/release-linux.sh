#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/MyPersonalDrive/MyPersonalDrive.csproj"
PUBLISH_DIR="$ROOT_DIR/dist/publish"
APPDIR="$ROOT_DIR/dist/MyPersonalDrive.AppDir"
DIST_DIR="$ROOT_DIR/dist"

rm -rf "$DIST_DIR"
mkdir -p "$PUBLISH_DIR"
mkdir -p "$APPDIR"

echo "Publishing .NET project..."
dotnet publish "$PROJECT" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o "$PUBLISH_DIR"

echo "Setting up AppDir..."
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/share/applications"
mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -r "$PUBLISH_DIR"/* "$APPDIR/usr/bin/"
cp "$ROOT_DIR/deploy/linux/MyPersonalDrive.desktop" "$APPDIR/usr/share/applications/"
cp "$ROOT_DIR/deploy/linux/MyPersonalDrive.desktop" "$APPDIR/"
cp "$ROOT_DIR/src/MyPersonalDrive/Assets/icon.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/MyPersonalDrive.png"
cp "$ROOT_DIR/src/MyPersonalDrive/Assets/icon.png" "$APPDIR/MyPersonalDrive.png"

# Create AppRun
cat > "$APPDIR/AppRun" <<EOF
#!/bin/sh
SELF=\$(readlink -f "\$0")
HERE=\$(dirname "\$SELF")
export PATH="\$HERE/usr/bin:\$PATH"
exec MyPersonalDrive "\$@"
EOF
chmod +x "$APPDIR/AppRun"

echo "Downloading appimagetool..."
APPIMAGETOOL="$ROOT_DIR/scripts/appimagetool-x86_64.AppImage"
if [ ! -f "$APPIMAGETOOL" ]; then
    curl -L -o "$APPIMAGETOOL" https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$APPIMAGETOOL"
fi

echo "Creating AppImage..."
ARCH=x86_64 "$APPIMAGETOOL" --appimage-extract-and-run "$APPDIR" "$DIST_DIR/MyPersonalDrive-x86_64.AppImage"

echo "Release artifact: $DIST_DIR/MyPersonalDrive-x86_64.AppImage"
