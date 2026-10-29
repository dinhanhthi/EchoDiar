#!/usr/bin/env bash
#
# ship.sh - End-to-end local release: build -> sign -> notarize -> GitHub Release -> appcast
#           -> Windows app attached to the same release -> Microsoft Store step.
#
# Run this AFTER the version-bump commit (project.yml + CHANGELOG.md) is pushed to main.
# It chains the release scripts in five steps with no interactive prompts.
# Before building it requires: branch main, HEAD == origin/main (only pushed commits ship),
# a clean tree of tracked files, gh authenticated, a `## [X.Y.Z]` CHANGELOG.md section,
# no existing tag vX.Y.Z and the Windows release channel set up (release.sh --preflight).
#
# Steps:
#   1. scripts/build-and-notarize.sh - macOS zip + DMG
#   2. scripts/publish-release.sh --yes - tag vX.Y.Z + GitHub Release (macOS assets)
#   3. scripts/publish-appcast.sh - appcast on main; macOS is fully shipped here
#   4. windows/scripts/release.sh --attach vX.Y.Z --yes - the tag pushed in step 2 started
#      windows-release.yml: approve that run, attach its files to the release, commit the feed
#   5. windows/scripts/release.sh --store vX.Y.Z --yes - the same run's Microsoft Store bundle: approve its
#      store-publish job when CI publishes, check the bundle, else print the Partner Center upload steps
#      ("manual Store upload pending"; "Store not set up yet" without the WINDOWS_STORE_* variables;
#      "no Store bundle for this tag" when the run predates them)
# Step 4 can take ~30-60 min (CI build + approval): callers should run ship.sh in the background.
# When step 4 fails, re-run it (or /cf-ship Resume) to finish Windows; when step 5 fails, re-run step 5.
#
# Windows install:
#   Docs : https://ed.dinhanhthi.com/docs.html#install-windows
#   Feed : https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller
#
# Prereqs (one-time - docs/RELEASING.md):
#   - "Developer ID Application" cert in the login Keychain
#   - notarytool credential profile (default "echodiar-notary")
#   - Sparkle EdDSA key (generate_keys --account EchoDiar) + SUPublicEDKey in Info.plist
#   - Xcode 27+ selected (required for the Icon Composer .icon) and xcodegen installed
#   - gh CLI authenticated
#   - Windows release channel: the windows-release-signing environment, its secrets,
#     WINDOWS_PUBLIC_PUBLISHER and windows/packaging/release-cert.sha256 (RELEASING §3.0)
#   - Microsoft Store (optional until set up): the WINDOWS_STORE_* variables, and for CI publishing
#     the windows-store-publish environment and its secrets (RELEASING §3.3)
#
# Usage:
#   scripts/ship.sh
#   ECHODIAR_NOTARY_PROFILE=my-profile scripts/ship.sh
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

BRANCH="$(git rev-parse --abbrev-ref HEAD)"
[[ "$BRANCH" == "main" ]] \
  || { echo "error: releases ship from 'main' (you are on '$BRANCH')." >&2; exit 1; }

# Only what is already on origin/main ships: the release commit is pushed before this runs, and
# publish-appcast.sh later pushes on top of HEAD (a stale HEAD would fail after the Release exists).
git fetch -q origin main || { echo "error: git fetch origin main failed" >&2; exit 1; }
if [[ "$(git rev-parse HEAD)" != "$(git rev-parse origin/main)" ]]; then
  echo "error: HEAD ($(git rev-parse --short HEAD)) is not origin/main ($(git rev-parse --short origin/main))." >&2
  echo "       push the release commit (or fast-forward to origin/main), then re-run." >&2
  exit 1
fi

gh auth status >/dev/null 2>&1 || { echo "error: gh is not authenticated - run 'gh auth login'." >&2; exit 1; }

# Tracked files only: untracked files are ignored.
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
  echo "error: commit or discard changes to tracked files before building a release" >&2
  exit 1
fi
release_sha="$(git rev-parse HEAD)"

# Version from the committed project.yml (tree is clean, so HEAD == working copy).
VERSION="$(git show HEAD:project.yml \
  | sed -nE 's/^[[:space:]]*MARKETING_VERSION:[[:space:]]*"?([0-9]+\.[0-9]+\.[0-9]+)"?.*/\1/p' \
  | head -1)"
[[ -n "$VERSION" ]] || { echo "error: no MARKETING_VERSION found in project.yml" >&2; exit 1; }

# publish-release.sh needs the release notes: check now, not after a notarization.
grep -qE "^## \[$(sed 's/\./\\./g' <<<"$VERSION")\]" CHANGELOG.md 2>/dev/null \
  || { echo "error: no '## [$VERSION]' section in CHANGELOG.md - add the release notes first." >&2; exit 1; }

# Refuse before building: a re-run must never overwrite release/export/ or spend a notarization.
TAG="v$VERSION"
if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null \
   || git ls-remote --exit-code --tags origin "refs/tags/$TAG" >/dev/null 2>&1; then
  echo "error: tag $TAG already exists; resume with /cf-ship (Resume) or see docs/RELEASING.md (rollback)" >&2
  exit 1
fi

# Step 4 needs the Windows release channel: check it now, not after macOS shipped (read-only).
windows/scripts/release.sh --preflight

ZIP="release/export/EchoDiar-$VERSION.zip"

echo "========================================"
echo "  Shipping EchoDiar v$VERSION"
echo "========================================"
echo

# Step 1 - Build, sign, notarize, DMG (Keychain prompt -> Always Allow)
echo "==> Step 1/5: build + notarize"
scripts/build-and-notarize.sh

if [[ "$(git rev-parse HEAD)" != "$release_sha" || -n "$(git status --porcelain --untracked-files=no)" ]]; then
  echo "error: source changed during build; release was not published" >&2
  exit 1
fi

# Step 2 - Tag + GitHub Release (zip + DMG uploaded); the tag push starts windows-release.yml
echo "==> Step 2/5: tag + GitHub Release"
scripts/publish-release.sh --yes "$ZIP"

# Step 3 - Generate + commit + push the appcast
echo "==> Step 3/5: appcast"
scripts/publish-appcast.sh "$ZIP"

# Step 4 - Approve the tag's Windows run, attach its files to the release, commit the feed
echo "==> Step 4/5: Windows (CI build + approval, ~30-60 min)"
if ! windows/scripts/release.sh --attach "$TAG" --yes; then
  echo >&2
  echo "error: the Windows step failed. macOS v$VERSION is fully shipped (release + appcast)." >&2
  echo "       finish Windows by re-running: windows/scripts/release.sh --attach $TAG --yes" >&2
  echo "       (or /cf-ship Resume); troubleshooting: docs/RELEASING.md §3.2" >&2
  exit 1
fi

# Step 5 - The Microsoft Store: approve the run's store-publish job, check the Store bundle, or print the manual upload
echo "==> Step 5/5: Microsoft Store"
if ! windows/scripts/release.sh --store "$TAG" --yes; then
  echo >&2
  echo "error: the Microsoft Store step failed. macOS v$VERSION and the self-signed Windows release are shipped." >&2
  echo "       finish the Store step by re-running: windows/scripts/release.sh --store $TAG --yes" >&2
  echo "       (or /cf-ship Resume); troubleshooting: docs/RELEASING.md §3.3" >&2
  exit 1
fi

echo
echo "========================================"
echo "  EchoDiar v$VERSION shipped!"
echo "  GitHub : https://github.com/dinhanhthi/EchoDiar/releases/tag/v$VERSION"
echo "  Appcast: https://ed.dinhanhthi.com/appcast.xml (live in ~1 min)"
echo "  Docs   : https://ed.dinhanhthi.com/docs.html#install-windows"
echo "  Feed   : https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller (live in a few min)"
echo "  Store  : see step 5 (submitted by CI, manual Store upload pending, Store not set up yet, or no Store bundle for this tag)"
echo "========================================"
