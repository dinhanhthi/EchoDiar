#!/usr/bin/env bash
#
# serve-update.sh - Local App Installer feed to test EchoDiar's Windows updates in the VMPal VM (plan 28.5).
#
# Runs on the Mac. Takes a folder (default ~/Downloads/EchoDiar-update/) holding exactly one signed package to
# offer: an .msixbundle (from windows-release.yml) or a single .msix (e.g. the EchoDiar-win-<arch> Debug artifact
# of windows.yml). It:
#   - reads Name / Publisher / Version (and ProcessorArchitecture for an .msix) from the package's own manifest,
#     never from its file name;
#   - renders windows/packaging/EchoDiar.appinstaller.template into <folder>/EchoDiar.appinstaller, whose own
#     Uri and package Uri are http://<host>:8765/... (UpdateSettings as in the template). An .msix gets a
#     <MainPackage> with its ProcessorArchitecture instead of the template's <MainBundle> (App Installer accepts
#     either; the script swaps the element name, the template stays the one of the release workflow);
#   - serves the folder over http on port 8765 in the foreground until Ctrl+C (python3 http.server, with the
#     App Installer MIME types), and prints the command to run in the VM.
#
# Host: the address the VM reaches the Mac on. The VMPal VM's gateway is the Mac's address on the
# 192.168.128.0/24 bridge (192.168.128.1), so the script picks the Mac's address on that subnet; without one
# (no VM running) it stops: pass --host <ip>.
#
# Safety: only the folder is served. It must hold regular files only (no symlinks, no subfolders) and no
# private keys (.pfx / .p12 / .key / .pvk); the port must be free. The server runs only while the script does.
#
# Usage:
#   windows/scripts/serve-update.sh                          # ~/Downloads/EchoDiar-update/, auto host
#   windows/scripts/serve-update.sh --folder DIR --host IP   # another folder / address
#   windows/scripts/serve-update.sh --render-only            # only write EchoDiar.appinstaller
# Then, in the VM (dev certificate already trusted, e.g. vm-install.ps1 -TrustCert):
#   Add-AppxPackage -AppInstallerFile http://<host>:8765/EchoDiar.appinstaller
# To offer version N+1: stop the server, replace the package in the folder, run the script again.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TEMPLATE="$REPO_ROOT/windows/packaging/EchoDiar.appinstaller.template"
PORT=8765
VM_SUBNET="192.168.128"
PYTHON=/usr/bin/python3

usage() { echo "usage: windows/scripts/serve-update.sh [--folder DIR] [--host IP] [--render-only]" >&2; exit 1; }
die() { echo "error: $*" >&2; exit 1; }

# --- 0. Args & tools -------------------------------------------------------
FOLDER="$HOME/Downloads/EchoDiar-update"
HOST=""
RENDER_ONLY=false
while (( $# > 0 )); do
  case "$1" in
    --folder) (( $# > 1 )) || usage; FOLDER="$2"; shift ;;
    --host) (( $# > 1 )) || usage; HOST="$2"; shift ;;
    --render-only) RENDER_ONLY=true ;;
    *) usage ;;
  esac
  shift
done

for tool in unzip ifconfig lsof "$PYTHON"; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool not found"
done
[[ -f "$TEMPLATE" ]] || die "$TEMPLATE not found"

# --- 1. Folder checks ------------------------------------------------------
[[ -d "$FOLDER" ]] || die "$FOLDER is not a folder; put the package to offer there."
DIR="$(cd -P "$FOLDER" && pwd)"
[[ "$DIR" != "/" && "$DIR" != "$HOME" && "$DIR" != "$HOME/Downloads" ]] \
  || die "$DIR is too broad to serve; use a folder that holds only the package."

OTHER="$(find "$DIR" -mindepth 1 ! -type f)"
[[ -z "$OTHER" ]] || die "$DIR must hold regular files only (no symlinks or subfolders):"$'\n'"$OTHER"
KEYS="$(find "$DIR" -type f \( -iname '*.pfx' -o -iname '*.p12' -o -iname '*.key' -o -iname '*.pvk' \))"
[[ -z "$KEYS" ]] || die "$DIR holds private keys; move them out first:"$'\n'"$KEYS"

PACKAGES=()
while IFS= read -r f; do PACKAGES+=("$f"); done \
  < <(find "$DIR" -maxdepth 1 -type f \( -iname '*.msix' -o -iname '*.msixbundle' \) | sort)
(( ${#PACKAGES[@]} == 1 )) \
  || die "$DIR must hold exactly one .msix or .msixbundle, found ${#PACKAGES[@]}${PACKAGES[*]:+: ${PACKAGES[*]##*/}}"
PACKAGE="${PACKAGES[0]}"
NAME="${PACKAGE##*/}"
[[ "$NAME" =~ ^[A-Za-z0-9._-]+$ ]] || die "'$NAME': rename it to letters, digits, '.', '_' and '-' only (it goes in a URL)."
case "$NAME" in
  *.[Mm][Ss][Ii][Xx][Bb][Uu][Nn][Dd][Ll][Ee]) KIND=bundle; MANIFEST=AppxMetadata/AppxBundleManifest.xml ;;
  *) KIND=package; MANIFEST=AppxManifest.xml ;;
esac
# The listing goes to a variable first: with pipefail, grep -q closing the pipe early would fail unzip.
LISTING="$(unzip -l "$PACKAGE")" || die "$NAME is not a zip (MSIX) file"
grep -q ' AppxSignature\.p7x$' <<<"$LISTING" || die "$NAME is not signed"

# --- 2. Host -----------------------------------------------------------------
if [[ -z "$HOST" ]]; then
  HOST="$(ifconfig | awk -v net="$VM_SUBNET." '$1 == "inet" && index($2, net) == 1 { print $2; exit }')"
  [[ -n "$HOST" ]] || die "no address of this Mac on $VM_SUBNET.0/24 (is the VM running?); pass --host <ip>."
  echo "==> Host $HOST (this Mac on $VM_SUBNET.0/24, the VMPal VM's gateway)"
else
  [[ "$HOST" =~ ^[A-Za-z0-9.-]+$ ]] || die "--host '$HOST' is not an address or host name"
  echo "==> Host $HOST (--host)"
fi
BASE="http://$HOST:$PORT"
APPINSTALLER="$DIR/EchoDiar.appinstaller"

# --- 3. Render & check the .appinstaller ------------------------------------
# The package is data: its manifest is parsed by an isolated Python (-I) with this script's code, never run.
# Reads the manifest on stdin, writes the .appinstaller, prints "<version> <publisher>".
read -r -d '' RENDER_PY <<'PY' || true
import re
import sys
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

kind, template_path, out_path, appinstaller_uri, package_uri = sys.argv[1:6]

def fail(message):
    sys.exit(f"error: {message}")

def local(tag):
    return tag.rsplit("}", 1)[-1]

def attr(value):
    return escape(value, {'"': "&quot;"})

try:
    manifest = ET.fromstring(sys.stdin.buffer.read())
except ET.ParseError as error:
    fail(f"the package manifest is not well-formed: {error}")
identity = next((e for e in manifest if local(e.tag) == "Identity"), None)
if identity is None:
    fail("the package manifest has no Identity")
name, publisher, version = (identity.get(k, "") for k in ("Name", "Publisher", "Version"))
arch = identity.get("ProcessorArchitecture", "") if kind == "package" else ""
if name != "dinh.thi.EchoDiar":
    fail(f"the package is {name!r}, not dinh.thi.EchoDiar")
if not publisher or not version or (kind == "package" and not arch):
    fail(f"the package Identity lacks Publisher / Version{' / ProcessorArchitecture' if kind == 'package' else ''}")

with open(template_path, encoding="utf-8") as f:
    text = f.read()
for placeholder, value in (("@VERSION@", version), ("@PUBLISHER@", publisher),
                           ("@APPINSTALLER_URI@", appinstaller_uri), ("@BUNDLE_URI@", package_uri)):
    text = text.replace(placeholder, attr(value))
main_tag = "MainBundle"
if kind == "package":
    main_tag = "MainPackage"
    if text.count("<MainBundle") != 1:
        fail("the template has no single <MainBundle to turn into a MainPackage")
    text = text.replace("<MainBundle", f'<MainPackage ProcessorArchitecture="{attr(arch)}"')

root = ET.fromstring(text.encode("utf-8"))
children = {local(e.tag): e for e in root}
main = children.get(main_tag)
update = children.get("UpdateSettings")
if local(root.tag) != "AppInstaller" or main is None or update is None:
    fail(f"the rendered .appinstaller has no AppInstaller / {main_tag} / UpdateSettings")
update_children = {local(e.tag): e for e in update}
on_launch = update_children.get("OnLaunch")
checks = [
    ("AppInstaller Version", root.get("Version"), version),
    ("AppInstaller Uri", root.get("Uri"), appinstaller_uri),
    (f"{main_tag} Name", main.get("Name"), name),
    (f"{main_tag} Publisher", main.get("Publisher"), publisher),
    (f"{main_tag} Version", main.get("Version"), version),
    (f"{main_tag} Uri", main.get("Uri"), package_uri),
    ("ProcessorArchitecture", main.get("ProcessorArchitecture") or "", arch),
    ("OnLaunch HoursBetweenUpdateChecks", on_launch.get("HoursBetweenUpdateChecks") if on_launch is not None else None, "0"),
    ("AutomaticBackgroundTask", "AutomaticBackgroundTask" in update_children, True),
    ("leftover placeholders", re.findall(r"@[A-Z_]+@", text), []),
]
bad = [f"{label} is {actual!r}, expected {expected!r}" for label, actual, expected in checks if actual != expected]
if bad:
    fail("; ".join(bad))
with open(out_path, "w", encoding="utf-8") as f:
    f.write(text)
print(version, publisher)
PY
SUMMARY="$(unzip -p "$PACKAGE" "$MANIFEST" | "$PYTHON" -I -c "$RENDER_PY" \
  "$KIND" "$TEMPLATE" "$APPINSTALLER" "$BASE/EchoDiar.appinstaller" "$BASE/$NAME")" \
  || die "could not render the .appinstaller (see above)"
echo "==> Wrote $APPINSTALLER"
echo "    offers $NAME ($KIND, version ${SUMMARY%% *}, publisher '${SUMMARY#* }')"

INSTALL_CMD="Add-AppxPackage -AppInstallerFile $BASE/EchoDiar.appinstaller"
if [[ "$RENDER_ONLY" == true ]]; then
  echo "    in the VM, once served: $INSTALL_CMD"
  exit 0
fi

# --- 4. Serve ----------------------------------------------------------------
if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  die "port $PORT is in use:"$'\n'"$(lsof -nP -iTCP:"$PORT" -sTCP:LISTEN)"
fi

echo "==> Serving $DIR on 0.0.0.0:$PORT (Ctrl+C stops it):"
find "$DIR" -mindepth 1 -maxdepth 1 -type f -exec basename {} \; | sort | sed "s|^|    $BASE/|"
echo "    in the VM: $INSTALL_CMD"
echo
# python3 -m http.server, with the MIME types App Installer expects (Python knows none of them).
exec "$PYTHON" -I -c '
import mimetypes, runpy, sys
for ext, mime in ((".appinstaller", "application/appinstaller"), (".msix", "application/msix"),
                  (".msixbundle", "application/msixbundle")):
    mimetypes.add_type(mime, ext)
sys.argv = ["http.server", "--bind", "0.0.0.0", "--directory", sys.argv[1], sys.argv[2]]
runpy.run_module("http.server", run_name="__main__", alter_sys=True)
' "$DIR" "$PORT"
