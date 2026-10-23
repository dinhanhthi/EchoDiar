#!/usr/bin/env bash
#
# sync-strings.sh — Add the UI strings of the last Debug build to Localizable.xcstrings.
#
# CLI xcodebuild builds extract each source's strings (.stringsdata) but never write
# new keys into the catalog; only Xcode IDE builds do. This runs `xcstringstool sync`
# on this checkout's .stringsdata files, as Xcode would (keys no longer used turn stale).
#
# Usage:
#   scripts/sync-strings.sh
#
# Prerequisites: a Debug build of this checkout in Xcode's default DerivedData:
#   xcodegen generate && xcodebuild -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug build
#
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

CATALOG="EchoDiar/Localizable.xcstrings"

die() { echo "error: $*" >&2; exit 1; }
count_keys() { python3 -I -c 'import json,sys; print(len(json.load(open(sys.argv[1]))["strings"]))' "$CATALOG"; }

# 1. This checkout's DerivedData folder (other checkouts have their own)
DERIVED=""
for dir in "$HOME"/Library/Developer/Xcode/DerivedData/EchoDiar-*; do
  [[ -f "$dir/info.plist" ]] || continue
  if [[ "$(/usr/libexec/PlistBuddy -c 'Print :WorkspacePath' "$dir/info.plist" 2>/dev/null)" == "$REPO_ROOT/EchoDiar.xcodeproj" ]]; then
    DERIVED="$dir"
    break
  fi
done
[[ -n "$DERIVED" ]] || die "no DerivedData for $REPO_ROOT/EchoDiar.xcodeproj; run a Debug build first:
  xcodegen generate && xcodebuild -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug build"

# 2. One .stringsdata per current source (first arch that has it)
OBJECTS="$DERIVED/Build/Intermediates.noindex/EchoDiar.build/Debug/EchoDiar.build/Objects-normal"
FILES=()
for src in EchoDiar/*.swift; do
  name="$(basename "$src" .swift)"
  for f in "$OBJECTS"/*/"$name.stringsdata"; do
    if [[ -f "$f" ]]; then
      FILES+=("$f")
      break
    fi
  done
done
((${#FILES[@]})) || die "no .stringsdata in $OBJECTS; run a Debug build first"

# 3. Sync
echo "==> $CATALOG: $(count_keys) keys"
echo "==> Syncing ${#FILES[@]} .stringsdata files from $DERIVED..."
xcrun xcstringstool sync "$CATALOG" --stringsdata "${FILES[@]}"
echo "==> $CATALOG: $(count_keys) keys"
