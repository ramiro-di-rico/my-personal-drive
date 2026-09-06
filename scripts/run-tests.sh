#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Two projects, and they are not interchangeable. MyPersonalDrive.Tests is the view-model and
# service suite; MyPersonalDrive.UiTests builds the real window in Avalonia's headless platform and
# measures it, which is the only thing here that can see a layout defect. They are separate
# projects because the headless package is xUnit v3 and the other suite is v2 — see the UI
# project's own .csproj for why that is a feature rather than a migration to do.
dotnet test "$ROOT_DIR/tests/MyPersonalDrive.Tests/MyPersonalDrive.Tests.csproj" "$@"
dotnet test "$ROOT_DIR/tests/MyPersonalDrive.UiTests/MyPersonalDrive.UiTests.csproj" "$@"
