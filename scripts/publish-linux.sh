#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/MyPersonalDrive/MyPersonalDrive.csproj"
RUNTIME="${1:-linux-x64}"
CONFIGURATION="Release"
PUBLISH_DIR="$ROOT_DIR/artifacts/$RUNTIME/publish"
PACKAGE_DIR="$ROOT_DIR/artifacts/$RUNTIME/package"
APP_BINARY_NAME="MyPersonalDrive"
APP_OUTPUT_NAME="MyPersonalDrive"

rm -rf "$PUBLISH_DIR" "$PACKAGE_DIR"
mkdir -p "$PUBLISH_DIR" "$PACKAGE_DIR"

echo "Publishing for $RUNTIME..."
dotnet publish "$PROJECT" \
  -c "$CONFIGURATION" \
  -r "$RUNTIME" \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  /p:DebugType=None \
  /p:DebugSymbols=false \
  -o "$PUBLISH_DIR"

cp "$PUBLISH_DIR/$APP_BINARY_NAME" "$PACKAGE_DIR/$APP_OUTPUT_NAME"
cp "$PUBLISH_DIR"/*.so "$PACKAGE_DIR/" 2>/dev/null || true
cp "$ROOT_DIR/src/MyPersonalDrive/Assets/icon.png" "$PACKAGE_DIR/MyPersonalDrive.png"
cp "$ROOT_DIR/README.md" "$PACKAGE_DIR/README.md"
chmod +x "$PACKAGE_DIR/$APP_OUTPUT_NAME"

tar -C "$ROOT_DIR/artifacts/$RUNTIME" -czf "$ROOT_DIR/artifacts/$RUNTIME/mypersonaldrive-$RUNTIME.tar.gz" package

echo "Linux package staged at: $PACKAGE_DIR"
echo "Tarball created at: $ROOT_DIR/artifacts/$RUNTIME/mypersonaldrive-$RUNTIME.tar.gz"
