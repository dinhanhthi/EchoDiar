#!/usr/bin/env bash
#
# bump-version.sh - Bump EchoDiar's version for a release (docs/RELEASING.md).
#
# macOS and Windows share one product version. Edits the two version settings in
# project.yml (the XcodeGen source of truth):
#   MARKETING_VERSION       -> CFBundleShortVersionString (user-facing)
#   CURRENT_PROJECT_VERSION -> CFBundleVersion (build number, Sparkle compare key)
#
# The build number ALWAYS increments by 1. Sparkle ships an update only when
# CFBundleVersion strictly increases - bump it every single release.
#
# A marketing version X.Y.Z is also written to the Windows package version
# <Identity Version="X.Y.Z.0"> in Package.appxmanifest (CI also patches the
# manifest from the release tag vX.Y.Z).
#
# Usage:
#   scripts/bump-version.sh 1.1.0     # set marketing version 1.1.0 (both apps), build +1
#   scripts/bump-version.sh           # keep marketing version, build +1 only
#   PROJECT_YML=/tmp/p.yml APPX_MANIFEST=/tmp/m.xml scripts/bump-version.sh 1.0.1   # edit other files (tests)
#
# Review the diff and commit the bump yourself.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_YML="${PROJECT_YML:-$REPO_ROOT/project.yml}"
APPX_MANIFEST="${APPX_MANIFEST:-$REPO_ROOT/windows/src/EchoDiar.App/Package.appxmanifest}"
NEW_MARKETING="${1:-}"

[[ -f "$PROJECT_YML" ]] || { echo "error: $PROJECT_YML not found" >&2; exit 1; }
[[ -f "$APPX_MANIFEST" ]] || { echo "error: $APPX_MANIFEST not found" >&2; exit 1; }

if [[ -n "$NEW_MARKETING" && ! "$NEW_MARKETING" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "error: marketing version must look like 1.2.3 (got \"$NEW_MARKETING\")" >&2
  exit 1
fi

CURRENT_BUILD="$(grep -Eo 'CURRENT_PROJECT_VERSION: *"?[0-9]+' "$PROJECT_YML" | grep -Eo '[0-9]+$' | head -1)"
[[ -n "$CURRENT_BUILD" ]] || { echo "error: no CURRENT_PROJECT_VERSION found in $PROJECT_YML" >&2; exit 1; }
NEXT_BUILD=$((CURRENT_BUILD + 1))

# The <Identity> element spans several lines: match it in slurp mode (the same pattern as CI's patch step).
# Exactly one Identity Version, so the edit can't miss or hit twice.
IDENTITY_VERSIONS="$(perl -0777 -ne 'my @m = /<Identity\b[^>]*?\bVersion="[^"]*"/g; print scalar @m' "$APPX_MANIFEST")"
[[ "$IDENTITY_VERSIONS" == 1 ]] \
  || { echo "error: expected one <Identity Version=\"...\"> in $APPX_MANIFEST (found $IDENTITY_VERSIONS)" >&2; exit 1; }

perl -i -pe "s/(CURRENT_PROJECT_VERSION: *)\"?[0-9]+\"?/\${1}${NEXT_BUILD}/" "$PROJECT_YML"
if [[ -n "$NEW_MARKETING" ]]; then
  perl -i -pe "s/(MARKETING_VERSION: *)\"?[0-9.]+\"?/\${1}${NEW_MARKETING}/" "$PROJECT_YML"
  perl -0777 -i -pe "s/(<Identity\\b[^>]*?\\bVersion=\")[^\"]*(\")/\${1}${NEW_MARKETING}.0\${2}/" "$APPX_MANIFEST"
fi

echo "==> Build number (CFBundleVersion):  ${CURRENT_BUILD} -> ${NEXT_BUILD}"
if [[ -n "$NEW_MARKETING" ]]; then
  echo "==> Marketing version:               set to ${NEW_MARKETING} (macOS + Windows ${NEW_MARKETING}.0)"
else
  echo "==> Marketing version:               unchanged"
fi
grep -nE 'MARKETING_VERSION|CURRENT_PROJECT_VERSION' "$PROJECT_YML"
perl -0777 -ne 'print "Windows package version: $1\n" if /<Identity\b[^>]*?\bVersion="([^"]*)"/' "$APPX_MANIFEST"
