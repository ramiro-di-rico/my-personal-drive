#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/MyPersonalDrive/MyPersonalDrive.csproj"
PUBLISH_DIR="$ROOT_DIR/dist/linux-x64"

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

dotnet restore "$PROJECT"
dotnet publish "$PROJECT" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o "$PUBLISH_DIR"

tar -C "$PUBLISH_DIR" -czf "$ROOT_DIR/dist/MyPersonalDrive-linux-x64.tar.gz" .

echo "Release artifact: $ROOT_DIR/dist/MyPersonalDrive-linux-x64.tar.gz"
