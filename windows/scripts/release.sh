#!/usr/bin/env bash
#
# release.sh - Build, check and (with --publish) publish a Windows release of EchoDiar (docs/RELEASING.md).
#
# Runs on the Mac. The build happens on GitHub Actions (.github/workflows/windows-release.yml): this script
# pushes the tag windows-vX.Y.Z (public channel) or windows-vX.Y.Z-dev (dev channel) at HEAD, which starts the
# workflow, like scripts/publish-release.sh tags the macOS releases. It pushes a tag rather than dispatching the
# workflow because GitHub dispatches only workflows that are on the default branch, and a tag works from any
# branch. Then it:
#   - watches the run and downloads its artifact EchoDiar-<tag> into release/windows/<tag>/ (gitignored);
#   - checks it: the .msixbundle (signed, its version and publisher), EchoDiar.appinstaller (well-formed XML,
#     Version / Publisher / both Uris), and EchoDiarDev.cer on the dev channel;
#   - only with --publish, and after you confirm, creates the GitHub release of the tag with those files
#     (a prerelease on the dev channel; never marked Latest, which stays the macOS release the website links).
#
# Version: X.Y.Z gives the 4-part package version X.Y.Z.0 (each part 0-65535, no leading zeros). The public
# channel defaults to MARKETING_VERSION of the committed project.yml (the macOS version); the dev channel
# needs --version 0.0.N, with N above the 0.0.<run>.0 of the windows.yml CI builds.
# The public channel needs Trusted Signing, which the workflow does not implement yet: its run fails at once.
#
# Requires: a clean tree of tracked files, HEAD pushed to origin/<current branch> (main for public), gh
# authenticated, no existing tag or release for the version.
#
# Usage:
#   windows/scripts/release.sh --dev --version 0.0.900              # dev: build + check, no release
#   windows/scripts/release.sh --dev --version 0.0.900 --publish    # ... then create the prerelease (asks)
#   windows/scripts/release.sh [--version X.Y.Z] [--publish]        # public channel (from main)
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GH_REPO="dinhanhthi/EchoDiar"
WORKFLOW="windows-release.yml"
APPINSTALLER_URI="https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller"
PYTHON=/usr/bin/python3

usage() { echo "usage: windows/scripts/release.sh [--dev] [--version X.Y.Z] [--publish]" >&2; exit 1; }
die() { echo "error: $*" >&2; exit 1; }

# --- 0. Args & tools -------------------------------------------------------
CHANNEL=public
VERSION=""
PUBLISH=false
while (( $# > 0 )); do
  case "$1" in
    --dev) CHANNEL=dev ;;
    --version) (( $# > 1 )) || usage; VERSION="$2"; shift ;;
    --publish) PUBLISH=true ;;
    *) usage ;;
  esac
  shift
done

cd "$REPO_ROOT"

for tool in gh git unzip "$PYTHON"; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool not found"
done

# --- 1. Version, tag and expected values -------------------------------------
if [[ -z "$VERSION" ]]; then
  [[ "$CHANNEL" == public ]] || die "the dev channel needs --version 0.0.N"
  VERSION="$(git show HEAD:project.yml \
    | sed -nE 's/^[[:space:]]*MARKETING_VERSION:[[:space:]]*"?([0-9]+\.[0-9]+\.[0-9]+)"?.*/\1/p' | sort -u)"
  [[ -n "$VERSION" && "$(printf '%s\n' "$VERSION" | wc -l | tr -d ' ')" == "1" ]] \
    || die "no single MARKETING_VERSION in HEAD:project.yml; pass --version"
fi
part='(0|[1-9][0-9]{0,4})'
[[ "$VERSION" =~ ^$part\.$part\.$part$ ]] || die "version '$VERSION' is not X.Y.Z (0-65535, no leading zeros)"
for p in "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}"; do
  (( p <= 65535 )) || die "version part $p is above 65535"
done
VERSION4="$VERSION.0"
if [[ "$CHANNEL" == dev ]]; then
  TAG="windows-v$VERSION-dev"
  PUBLISHER="CN=EchoDiar Dev"
else
  TAG="windows-v$VERSION"
  PUBLISHER=""   # the Trusted Signing certificate subject; read from the rendered .appinstaller
fi
BUNDLE="EchoDiar_$VERSION4.msixbundle"
BUNDLE_URI="https://github.com/$GH_REPO/releases/download/$TAG/$BUNDLE"
OUT="$REPO_ROOT/release/windows/$TAG"

# --- 2. Pre-flight checks --------------------------------------------------
# Tracked files only, as scripts/ship.sh. Every change counts, also ones unrelated to the Windows app: list them.
DIRTY="$(git status --porcelain --untracked-files=no)"
if [[ -n "$DIRTY" ]]; then
  echo "error: tracked files have uncommitted changes (the release builds HEAD, so commit, stash or discard them):" >&2
  printf '%s\n' "$DIRTY" | sed 's/^/  /' >&2
  exit 1
fi

BRANCH="$(git rev-parse --abbrev-ref HEAD)"
[[ "$BRANCH" != "HEAD" ]] || die "detached HEAD; check out a branch"
if [[ "$CHANNEL" == public && "$BRANCH" != "main" ]]; then
  die "public releases are tagged from 'main' (you are on '$BRANCH'); use --dev for a dev release"
fi

gh auth status >/dev/null 2>&1 || die "gh is not authenticated - run 'gh auth login'."

ORIGIN_URL="$(git remote get-url origin 2>/dev/null || true)"
[[ "$ORIGIN_URL" =~ ^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)dinhanhthi/EchoDiar(\.git)?/?$ ]] \
  || die "origin is '${ORIGIN_URL:-unset}', expected github.com/$GH_REPO."
git fetch -q origin "$BRANCH" || die "git fetch origin $BRANCH failed"
git merge-base --is-ancestor HEAD "origin/$BRANCH" \
  || die "HEAD $(git rev-parse --short HEAD) is not on origin/$BRANCH - push it first."

if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
  die "tag $TAG already exists locally (git tag -d $TAG) - or pick another version."
fi
if git ls-remote --exit-code --tags origin "refs/tags/$TAG" >/dev/null 2>&1; then
  die "tag $TAG already exists on origin - pick another version."
fi
if gh release view "$TAG" --repo "$GH_REPO" >/dev/null 2>&1; then
  die "a GitHub release $TAG already exists."
fi
if [[ -e "$OUT" ]]; then
  die "$OUT already exists; move or delete it first."
fi
SHA="$(git rev-parse HEAD)"

# --- 3. Confirm, tag, watch ----------------------------------------------
echo "About to build:"
echo "  channel : $CHANNEL"
echo "  version : $VERSION (package $VERSION4)"
echo "  tag     : $TAG  (at ${SHA:0:7} on branch $BRANCH)"
echo "  output  : $OUT"
echo "  publish : $PUBLISH"
echo
read -r -p "Push the tag $TAG (starts $WORKFLOW)? [y/N] " reply
[[ "$reply" =~ ^[Yy]$ ]] || { echo "aborted."; exit 1; }

echo "==> Tagging $TAG"
git tag -a "$TAG" -m "EchoDiar for Windows $VERSION ($CHANNEL)"
# Runs of an earlier, deleted tag of the same name are older: only a run created after the push counts (60 s
# of slack for the Mac's clock; BSD date).
START="$(date -u -v-60S +%Y-%m-%dT%H:%M:%SZ)"
git push origin "refs/tags/$TAG"

DISCARD="git push origin :refs/tags/$TAG && git tag -d $TAG"

echo "==> Waiting for the $WORKFLOW run of $TAG"
RUN=""
# gh answers 404 until the first run exists while the workflow is not on the default branch: keep polling.
for _ in $(seq 1 20); do
  RUN="$(gh run list --repo "$GH_REPO" --workflow "$WORKFLOW" --event push --limit 20 \
    --json databaseId,headBranch,createdAt \
    -q ".[] | select(.headBranch == \"$TAG\" and .createdAt >= \"$START\") | .databaseId" 2>/dev/null | head -1)" || RUN=""
  [[ -n "$RUN" ]] && break
  sleep 15
done
[[ -n "$RUN" ]] || die "no $WORKFLOW run for $TAG after 5 minutes. Discard the tag: $DISCARD"
echo "    run $RUN: https://github.com/$GH_REPO/actions/runs/$RUN"
if ! gh run watch "$RUN" --repo "$GH_REPO" --exit-status --interval 30; then
  echo "error: run $RUN failed: gh run view $RUN --repo $GH_REPO --log-failed" >&2
  echo "       discard the tag before re-running: $DISCARD" >&2
  exit 1
fi

# --- 4. Download & check -------------------------------------------------
echo "==> Downloading EchoDiar-$TAG into $OUT"
mkdir -p "$OUT"
gh run download "$RUN" --repo "$GH_REPO" -n "EchoDiar-$TAG" -D "$OUT"

[[ -f "$OUT/$BUNDLE" ]] || die "$BUNDLE missing from the artifact"
[[ -f "$OUT/EchoDiar.appinstaller" ]] || die "EchoDiar.appinstaller missing from the artifact"
if [[ "$CHANNEL" == dev ]]; then
  [[ -f "$OUT/EchoDiarDev.cer" ]] || die "EchoDiarDev.cer missing from the artifact"
fi
if find "$OUT" -type f \( -iname '*.pfx' -o -iname '*.p12' \) | grep -q .; then
  die "private key in the artifact"
fi
unzip -l "$OUT/$BUNDLE" | grep -q ' AppxSignature\.p7x$' || die "$BUNDLE is not signed"

# Downloaded files are data: parsed by an isolated Python (-I) with this script's code, never run. Reads the
# bundle manifest on stdin; prints the publisher (the expected one, or the bundle's on the public channel).
read -r -d '' CHECK_PY <<'PY' || true
import sys
import xml.etree.ElementTree as ET

path, version, publisher, appinstaller_uri, bundle_uri = sys.argv[1:6]
manifest_xml = sys.stdin.buffer.read()

def fail(message):
    sys.exit(f"error: {message}")

def local(tag):
    return tag.rsplit("}", 1)[-1]

try:
    bundle = ET.fromstring(manifest_xml)
except ET.ParseError as error:
    fail(f"AppxBundleManifest.xml is not well-formed: {error}")
identity = next((e for e in bundle if local(e.tag) == "Identity"), None)
if identity is None:
    fail("AppxBundleManifest.xml has no Identity")
publisher = publisher or identity.get("Publisher", "")
packages = sorted(
    (e.get("Architecture"), e.get("Version"))
    for e in bundle.iter() if local(e.tag) == "Package" and e.get("Type") == "application")
checks = [
    ("bundle Name", identity.get("Name"), "dinh.thi.EchoDiar"),
    ("bundle Version", identity.get("Version"), version),
    ("bundle Publisher", identity.get("Publisher"), publisher),
    ("bundle packages", packages, [("arm64", version), ("x64", version)]),
]

try:
    root = ET.parse(path).getroot()
except ET.ParseError as error:
    fail(f"EchoDiar.appinstaller is not well-formed: {error}")
children = {local(e.tag): e for e in root}
main = children.get("MainBundle")
update = children.get("UpdateSettings")
if local(root.tag) != "AppInstaller" or main is None or update is None:
    fail("EchoDiar.appinstaller has no AppInstaller / MainBundle / UpdateSettings")
update_children = {local(e.tag): e for e in update}
on_launch = update_children.get("OnLaunch")
checks += [
    ("AppInstaller Version", root.get("Version"), version),
    ("AppInstaller Uri", root.get("Uri"), appinstaller_uri),
    ("MainBundle Name", main.get("Name"), "dinh.thi.EchoDiar"),
    ("MainBundle Publisher", main.get("Publisher"), publisher),
    ("MainBundle Version", main.get("Version"), version),
    ("MainBundle Uri", main.get("Uri"), bundle_uri),
    ("OnLaunch HoursBetweenUpdateChecks", on_launch.get("HoursBetweenUpdateChecks") if on_launch is not None else None, "0"),
    ("AutomaticBackgroundTask", "AutomaticBackgroundTask" in update_children, True),
]
bad = [f"{name} is {actual!r}, expected {expected!r}" for name, actual, expected in checks if actual != expected]
if bad:
    fail("; ".join(bad))
print(publisher)
PY
PUBLISHER="$(unzip -p "$OUT/$BUNDLE" AppxMetadata/AppxBundleManifest.xml | "$PYTHON" -I -c "$CHECK_PY" \
  "$OUT/EchoDiar.appinstaller" "$VERSION4" "$PUBLISHER" "$APPINSTALLER_URI" "$BUNDLE_URI")" \
  || die "the artifact check failed (see above)"

echo "    OK: $BUNDLE (bundle and appinstaller $VERSION4, publisher '$PUBLISHER')"
ls -l "$OUT"

# --- 5. Publish (only with --publish, after a confirmation) ---------------
if [[ "$PUBLISH" == false ]]; then
  echo
  echo "Built and checked; not published (no --publish). The tag $TAG stays on origin."
  echo "  publish : re-run is not possible (the tag exists); create the release by hand:"
  echo "            gh release create $TAG --repo $GH_REPO --verify-tag --latest=false$([[ $CHANNEL == dev ]] && echo ' --prerelease') \\"
  echo "              \"$OUT/$BUNDLE\" \"$OUT/EchoDiar.appinstaller\"$([[ $CHANNEL == dev ]] && echo " \"$OUT/EchoDiarDev.cer\"")"
  echo "  discard : $DISCARD"
  exit 0
fi

FILES=("$OUT/$BUNDLE" "$OUT/EchoDiar.appinstaller")
FLAGS=(--latest=false)
if [[ "$CHANNEL" == dev ]]; then
  FILES+=("$OUT/EchoDiarDev.cer")
  FLAGS+=(--prerelease)
fi
NOTES="EchoDiar for Windows $VERSION ($CHANNEL channel, package $VERSION4), x64 and arm64.

Install: download EchoDiar.appinstaller and open it (App Installer keeps the app up to date)."
[[ "$CHANNEL" == dev ]] && NOTES+="
Dev build: first trust EchoDiarDev.cer (Local Machine > Trusted People)."

echo
echo "About to create the GitHub release $TAG ($CHANNEL$([[ $CHANNEL == dev ]] && echo ', prerelease'), not Latest) with:"
printf '  %s\n' "${FILES[@]}"
read -r -p "Create the release? [y/N] " reply
[[ "$reply" =~ ^[Yy]$ ]] || { echo "not published. The tag $TAG stays; discard it with: $DISCARD"; exit 1; }

gh release create "$TAG" "${FILES[@]}" --repo "$GH_REPO" --verify-tag "${FLAGS[@]}" \
  --title "EchoDiar for Windows $VERSION" --notes "$NOTES"

echo
echo "Done. Published $TAG -> https://github.com/$GH_REPO/releases/tag/$TAG"
if [[ "$CHANNEL" == public ]]; then
  echo "Next: copy $OUT/EchoDiar.appinstaller to windows/EchoDiar.appinstaller, commit it on main and"
  echo "      deploy the site (pages.yml) so $APPINSTALLER_URI serves it."
fi
