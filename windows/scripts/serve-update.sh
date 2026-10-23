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
#   - serves the package, EchoDiar.appinstaller and EchoDiarDev.cer (when the folder has it) over http on port
#     8765 in the foreground until Ctrl+C (a small python3 http.server handler with the App Installer MIME types
#     and HTTP Range support), and prints the command to run in the VM.
#
# Host: the address the VM reaches the Mac on, and the only address the server listens on. The VMPal VM's
# gateway is the Mac's address on the 192.168.128.0/24 bridge (192.168.128.1), so the script picks the Mac's
# address on that subnet; without one (no VM running) it stops: pass --host <IPv4 address> (a host name is
# refused: it is both the bind address and the feed's Uri, so it must be one exact address).
#
# Safety: the folder must hold regular files only (no symlinks, no subfolders) and no private keys
# (.pfx / .p12 / .key / .pvk); the port must be free. Only the three files above are served, checked on each
# request: any other path (the folder itself, a file added while it runs) is a 404, and there is no directory
# listing. The server runs only while the script does.
#
# Range: App Installer and Delivery Optimization download the package in byte ranges. One range per request
# (bytes=a-b, a-, -n) gets a 206 with Content-Range; an unsatisfiable one a 416 with "Content-Range: bytes */size";
# several ranges in one request are answered with the whole file (200), which HTTP allows. Every answer says
# Accept-Ranges: bytes. A client that drops the connection (BrokenPipe / ConnectionReset) is ignored quietly.
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
  # The server binds this exact address: an IPv4 address, not a host name.
  OCTET='(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])'
  [[ "$HOST" =~ ^$OCTET\.$OCTET\.$OCTET\.$OCTET$ ]] \
    || die "--host '$HOST' is not an IPv4 address (the server listens on it; a host name is not accepted)"
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

SERVED=("$NAME" EchoDiar.appinstaller)
[[ -f "$DIR/EchoDiarDev.cer" ]] && SERVED+=(EchoDiarDev.cer)
echo "==> Serving from $DIR on $HOST:$PORT only these files (Ctrl+C stops it):"
printf "    $BASE/%s\n" "${SERVED[@]}"
echo "    in the VM: $INSTALL_CMD"
echo
# The served files are data: an isolated Python (-I) only reads and sends them.
read -r -d '' SERVE_PY <<'PY' || true
import http.server
import mimetypes
import os
import re
import shutil
import sys
from urllib.parse import unquote, urlsplit

folder, host, port, *allowed = sys.argv[1:]
allowed = frozenset(allowed)
CHUNK = 1 << 20
RANGE = re.compile(r"bytes=(\d*)-(\d*)")
for ext, mime in ((".appinstaller", "application/appinstaller"), (".msix", "application/msix"),
                  (".msixbundle", "application/msixbundle"), (".cer", "application/x-x509-ca-cert")):
    mimetypes.add_type(mime, ext)


def parse_range(header, size):
    """(start, end) of a single range, None for no usable Range (the whole file), "bad" when unsatisfiable."""
    if header is None or "," in header:
        return None  # No range, or several: the whole file.
    match = RANGE.fullmatch(header.strip())
    if match is None or match.group(1) == match.group(2) == "":
        return None  # Not a bytes range: ignored, as HTTP allows.
    first, last = match.groups()
    if first == "":
        count = int(last)
        return (max(size - count, 0), size - 1) if count > 0 and size > 0 else "bad"
    start = int(first)
    if last and int(last) < start:
        return None  # Not a valid range: ignored too.
    if start >= size:
        return "bad"
    return start, min(int(last), size - 1) if last else size - 1


class Handler(http.server.BaseHTTPRequestHandler):
    def version_string(self):
        return "EchoDiarFeed"

    def do_GET(self):
        self.serve(body=True)

    def do_HEAD(self):
        self.serve(body=False)

    def serve(self, body):
        name = unquote(urlsplit(self.path).path).lstrip("/")
        path = os.path.join(folder, name)
        # Checked on each request: only the allowed names, still regular files (no symlink) in the folder.
        if name not in allowed or not os.path.isfile(path) or os.path.islink(path):
            self.reply(404, 0)
            return
        try:
            file = open(path, "rb")
        except OSError:
            self.reply(404, 0)
            return
        with file:
            size = os.fstat(file.fileno()).st_size
            span = parse_range(self.headers.get("Range"), size)
            if span == "bad":
                self.reply(416, 0, {"Content-Range": f"bytes */{size}"})
                return
            start, end = span or (0, size - 1)
            length = end - start + 1 if size else 0
            headers = {"Content-Type": mimetypes.guess_type(name)[0] or "application/octet-stream"}
            if span:
                headers["Content-Range"] = f"bytes {start}-{end}/{size}"
            self.reply(206 if span else 200, length, headers)
            if not body:
                return
            file.seek(start)
            while length > 0:
                chunk = file.read(min(CHUNK, length))
                if not chunk:
                    break
                self.wfile.write(chunk)
                length -= len(chunk)

    def reply(self, status, length, headers=None):
        self.send_response(status)
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(length))
        for key, value in (headers or {}).items():
            self.send_header(key, value)
        self.end_headers()


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        if isinstance(sys.exc_info()[1], (BrokenPipeError, ConnectionResetError)):
            return  # The client dropped the connection (App Installer cancels ranges it no longer needs).
        super().handle_error(request, client_address)


with Server((host, int(port)), Handler) as server:
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
PY
exec "$PYTHON" -I -c "$SERVE_PY" "$DIR" "$HOST" "$PORT" "${SERVED[@]}"
