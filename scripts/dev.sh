#!/usr/bin/env bash
#
# dev.sh — Generate the Xcode project, build Debug, and launch EchoDiar.
#
# Uses Xcode's default DerivedData (no -derivedDataPath), so Xcode's Run reuses
# this build and the app path stays stable (mic / system-audio grants survive rebuilds).
#
# Usage:
#   scripts/dev.sh              # incremental build + launch
#   scripts/dev.sh --clean      # clean build first
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

CLEAN_ACTION=""
[[ "${1:-}" == "--clean" ]] && CLEAN_ACTION="clean"

echo "==> Generating EchoDiar.xcodeproj..."
xcodegen generate

echo "==> Stopping any running EchoDiar..."
pkill -x EchoDiar 2>/dev/null || true

echo "==> Building (Debug)..."
xcodebuild \
  -project EchoDiar.xcodeproj \
  -scheme EchoDiar \
  -configuration Debug \
  $CLEAN_ACTION build

PRODUCTS="$(xcodebuild -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug \
  -showBuildSettings 2>/dev/null \
  | awk -F' = ' '!found && /^ *BUILT_PRODUCTS_DIR = /{print $2; found=1}')"
APP="$PRODUCTS/EchoDiar.app"

echo "==> Launching $APP"
open "$APP"
