#!/usr/bin/env bash
#
# release.sh - Build, check and publish the Windows app of EchoDiar (docs/RELEASING.md).
#
# Runs on the Mac. The build happens on GitHub Actions (.github/workflows/windows-release.yml), started by a tag push.
# Its artifact EchoDiar-<tag> holds five files: EchoDiar_X.Y.Z.0.msixbundle, the App Installer feed
# EchoDiar.appinstaller, the public certificate (EchoDiar.cer; EchoDiarDev.cer on the dev channel), EchoDiar-Setup.exe
# (which embeds that certificate) and SHA256SUMS-windows.txt. Modes:
#   --dev        pushes the tag windows-vX.Y.Z-dev at HEAD (any branch: a tag rather than a dispatch, because GitHub
#                dispatches only workflows that are on the default branch), watches the run, downloads the artifact
#                into release/windows/<tag>/ (gitignored) and checks it. With --publish it then creates the GitHub
#                prerelease of the tag with the five files (never Latest, which stays the release the website links).
#   --preflight  read-only check of the release channel's one-time setup (docs/RELEASING.md §3.0): the
#                windows-release-signing environment, its reviewer, tag policy and secrets, the variable
#                WINDOWS_PUBLIC_PUBLISHER and the pinned release certificate. /cf-ship runs it before it builds.
#   --attach     the Windows step of scripts/ship.sh, once the release vX.Y.Z is published. Its tag, pushed by
#                publish-release.sh, started the workflow on the release channel: this approves that run's
#                windows-release-signing deployment (running /cf-ship is the approval), waits for it, downloads the
#                artifact into release/windows/vX.Y.Z/, checks it, uploads the assets the release lacks and commits
#                the feed windows/EchoDiar.appinstaller on main (pages.yml serves it). Idempotent: re-run it to finish.
#
# The checks (dev and attach) read the downloaded files as data, with an isolated Python (-I), and never run them:
# exactly the five files and no private key; SHA256SUMS-windows.txt; the bundle (signed, Name, Version, Publisher,
# x64 and arm64, both inner packages signed) and the feed (Version, Publisher, both Uris, update settings); the
# certificate's subject is the Publisher; Setup starts with MZ and embeds the certificate. Any self-signed key can
# claim a subject, so the signer is pinned too: the certificate and the signers of the bundle and of each inner
# package must have the DER SHA-256 pinned at HEAD in windows/packaging/dev-cert.sha256 (dev) or
# windows/packaging/release-cert.sha256 (attach).
#
# Version: X.Y.Z gives the 4-part package version X.Y.Z.0 (each part 0-65535, no leading zeros). Dev builds use
# 0.0.N, with N above the 0.0.<run>.0 of the windows.yml CI builds; a release uses the version of its tag.
#
# Usage:
#   windows/scripts/release.sh --dev --version 0.0.900                    # dev: build + check, no release
#   windows/scripts/release.sh --dev --version 0.0.900 --publish [--yes]  # ... then create the prerelease
#   windows/scripts/release.sh --preflight                                # check the release channel's setup
#   windows/scripts/release.sh --attach v1.2.0 [--yes]                    # attach Windows to the release v1.2.0
# --yes answers every prompt (scripts/ship.sh and unattended runs).
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GH_REPO="dinhanhthi/EchoDiar"
WORKFLOW="windows-release.yml"
WORKFLOW_PATH=".github/workflows/$WORKFLOW"
RELEASE_ENV="windows-release-signing"
DEV_PUBLISHER="CN=EchoDiar Dev"
APPINSTALLER_URI="https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller"
SETUP_CSPROJ="windows/src/EchoDiar.Setup/EchoDiar.Setup.csproj"
FEED="windows/EchoDiar.appinstaller"
DEV_PIN_FILE="windows/packaging/dev-cert.sha256"
RELEASE_PIN_FILE="windows/packaging/release-cert.sha256"
SETUP_DOC="docs/RELEASING.md §3.0"
SUMS="SHA256SUMS-windows.txt"
RUN_TIMEOUT=7200      # seconds one run attempt may take, approval included, before --attach gives up
APPROVAL_TIMEOUT=1800 # seconds --attach waits for a manual approval when it cannot approve itself
PYTHON=/usr/bin/python3

USAGE="usage: windows/scripts/release.sh --dev --version 0.0.N [--publish] [--yes]
       windows/scripts/release.sh --preflight
       windows/scripts/release.sh --attach vX.Y.Z [--yes]
  --dev        build and check a dev build (tag windows-v0.0.N-dev at HEAD); --publish creates its prerelease
  --preflight  read-only check of the release channel's one-time setup ($SETUP_DOC)
  --attach     approve, check and attach the Windows build of the published release vX.Y.Z; commit its feed
  --yes        answer every prompt"

usage() { echo "$USAGE" >&2; exit 1; }
die() { echo "error: $*" >&2; exit 1; }

# Asks before a step with side effects; --yes answers yes.
confirm() {
  [[ "$YES" == true ]] && return 0
  local reply
  read -r -p "$1 [y/N] " reply
  [[ "$reply" =~ ^[Yy]$ ]]
}

# --- 0. Args & tools -------------------------------------------------------
MODE=""
VERSION=""
TAG=""
PUBLISH=false
YES=false
set_mode() { [[ -z "$MODE" ]] || usage; MODE="$1"; }
(( $# > 0 )) || usage
while (( $# > 0 )); do
  case "$1" in
    --dev) set_mode dev ;;
    --version) (( $# > 1 )) || usage; VERSION="$2"; shift ;;
    --publish) PUBLISH=true ;;
    --preflight) set_mode preflight ;;
    --attach) (( $# > 1 )) || usage; set_mode attach; TAG="$2"; shift ;;
    --yes|-y) YES=true ;;
    -h|--help) echo "$USAGE"; exit 0 ;;
    *) usage ;;
  esac
  shift
done
[[ -n "$MODE" ]] || usage
if [[ "$MODE" != dev ]] && [[ -n "$VERSION" || "$PUBLISH" == true ]]; then
  usage   # --version and --publish belong to --dev
fi

cd "$REPO_ROOT"

for tool in gh git openssl shasum "$PYTHON"; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool not found"
done

# --- 1. Shared helpers -------------------------------------------------------
# Checks that $1 is X.Y.Z (each part 0-65535, no leading zeros) and sets VERSION and VERSION4.
set_version() {
  local part='(0|[1-9][0-9]{0,4})' p
  [[ "$1" =~ ^$part\.$part\.$part$ ]] || die "version '$1' is not X.Y.Z (0-65535, no leading zeros)"
  for p in "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}"; do
    (( p <= 65535 )) || die "version part $p is above 65535"
  done
  VERSION="$1"
  VERSION4="$VERSION.0"
}

check_origin() {
  local url
  url="$(git remote get-url origin 2>/dev/null || true)"
  [[ "$url" =~ ^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)dinhanhthi/EchoDiar(\.git)?/?$ ]] \
    || die "origin is '${url:-unset}', expected github.com/$GH_REPO."
}

# Prints the pinned certificate SHA-256 of the file $1 at HEAD; fails unless it holds exactly 64 lowercase hex
# characters (and a final newline).
read_pin() {
  local pin
  pin="$(git show "HEAD:$1" 2>/dev/null)" || return 1
  [[ "$pin" =~ ^[0-9a-f]{64}$ ]] || return 1
  printf '%s\n' "$pin"
}

# Downloaded files are data: checked by an isolated Python (-I) with this script's code, never run. Arguments: the
# artifact folder, the package version, the Publisher, the feed Uri, the bundle Uri, the certificate's name, the
# pinned SHA-256 and the certificate's subject (read by openssl; empty when it is not a certificate).
read -r -d '' CHECK_PY <<'PY' || true
import hashlib
import io
import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

out, version, publisher, appinstaller_uri, bundle_uri, cer_name, pin, cer_subject = sys.argv[1:9]
bundle_name = f"EchoDiar_{version}.msixbundle"
sums_name = "SHA256SUMS-windows.txt"
summed = sorted([bundle_name, "EchoDiar.appinstaller", cer_name, "EchoDiar-Setup.exe"])
expected = sorted(summed + [sums_name])

def fail(message):
    sys.exit(f"error: {message}")

def local(tag):
    return tag.rsplit("}", 1)[-1]

def path(name):
    return os.path.join(out, name)

def read(name):
    with open(path(name), "rb") as file:
        return file.read()

def sha256(name):
    digest = hashlib.sha256()
    with open(path(name), "rb") as file:
        for chunk in iter(lambda: file.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()

def der_items(data, start, end):
    """The DER elements in data[start:end], as (tag, element start, value start, value end)."""
    items = []
    while start < end:
        if start + 2 > end:
            raise ValueError("truncated element")
        tag, size, value = data[start], data[start + 1], start + 2
        if size & 0x80:
            count = size & 0x7F
            if not 1 <= count <= 4 or value + count > end:
                raise ValueError("unsupported length")
            size = int.from_bytes(data[value:value + count], "big")
            value += count
        if value + size > end:
            raise ValueError("truncated element")
        items.append((tag, start, value, value + size))
        start = value + size
    return items

def children(data, item, tag):
    if item[0] != tag:
        raise ValueError(f"tag 0x{item[0]:02x} where 0x{tag:02x} was expected")
    return der_items(data, item[2], item[3])

SIGNED_DATA = bytes.fromhex("2a864886f70d010702")  # the OID 1.2.840.113549.1.7.2

def signer_sha256(p7x, what):
    """SHA-256 of the DER of the certificate that signed an AppxSignature.p7x (a 4-byte PKCX header, then PKCS #7
    SignedData). The certificate set may also hold others (a chain, a timestamp authority): the signer is the one
    with the SignerInfo's issuer and serial."""
    if p7x[:4] != b"PKCX":
        fail(f"{what}: AppxSignature.p7x has no PKCX header")
    der = p7x[4:]
    try:
        content_info = der_items(der, 0, len(der))
        if len(content_info) != 1:
            raise ValueError("data after the signature")
        oid, explicit = children(der, content_info[0], 0x30)[:2]
        if oid[0] != 0x06 or der[oid[2]:oid[3]] != SIGNED_DATA:
            raise ValueError("not SignedData")
        fields = children(der, children(der, explicit, 0xA0)[0], 0x30)
        certificates = [cert for field in fields if field[0] == 0xA0 for cert in children(der, field, 0xA0)]
        signer_infos = children(der, fields[-1], 0x31)
        if len(signer_infos) != 1:
            raise ValueError(f"{len(signer_infos)} signers")
        issuer, serial = children(der, children(der, signer_infos[0], 0x30)[1], 0x30)[:2]
        wanted = (der[issuer[1]:issuer[3]], der[serial[1]:serial[3]])
        signers = []
        for cert in certificates:
            tbs = children(der, children(der, cert, 0x30)[0], 0x30)
            first = 1 if tbs[0][0] == 0xA0 else 0  # skip the [0] version
            cert_serial, cert_issuer = tbs[first], tbs[first + 2]
            if (der[cert_issuer[1]:cert_issuer[3]], der[cert_serial[1]:cert_serial[3]]) == wanted:
                signers.append(der[cert[1]:cert[3]])
    except (ValueError, IndexError) as error:
        fail(f"{what}: AppxSignature.p7x is not a PKCS #7 signature ({error})")
    if len(signers) != 1:
        fail(f"{what}: {len(signers)} certificates of AppxSignature.p7x are its signer, expected one")
    return hashlib.sha256(signers[0]).hexdigest()

# The five files, flat, and never a private key.
entries = sorted(os.listdir(out))
keys = [name for name in entries if name.lower().endswith((".pfx", ".p12"))]
if keys:
    fail(f"private key in the artifact: {', '.join(keys)}")
if entries != expected or not all(os.path.isfile(path(name)) and not os.path.islink(path(name)) for name in entries):
    fail(f"the artifact holds {entries}, expected the files {expected}")

# GNU format (lowercase hash, two spaces, name, LF) for exactly the four other files.
lines = read(sums_name).split(b"\n")
if lines[-1] != b"":
    fail(f"{sums_name} does not end with a newline")
listed = {}
for line in lines[:-1]:
    match = re.fullmatch(rb"([0-9a-f]{64})  ([A-Za-z0-9._-]+)", line)
    if match is None or match.group(2).decode() in listed:
        fail(f"{sums_name}: bad line {line!r}")
    listed[match.group(2).decode()] = match.group(1).decode()
if sorted(listed) != summed:
    fail(f"{sums_name} lists {sorted(listed)}, expected {summed}")
bad = [name for name in summed if sha256(name) != listed[name]]
if bad:
    fail(f"{sums_name} does not match {', '.join(bad)}")

# The certificate, and Setup around it. The Publisher is a Windows name (", " between parts), the subject RFC 2253.
cer = read(cer_name)
setup = read("EchoDiar-Setup.exe")

def dn(value):
    return re.sub(r"\s*,\s*", ",", value.strip())

checks = [
    (f"{cer_name} subject", dn(cer_subject), dn(publisher)),
    (f"{cer_name} SHA-256", hashlib.sha256(cer).hexdigest(), pin),
    ("EchoDiar-Setup.exe header", setup[:2], b"MZ"),
    (f"EchoDiar-Setup.exe embeds {cer_name}", cer in setup, True),
]

# The bundle: signed, its manifest, and each inner package signed; every signer is the pinned certificate.
try:
    bundle = zipfile.ZipFile(path(bundle_name))
except zipfile.BadZipFile:
    fail(f"{bundle_name} is not a zip (MSIX bundle) file")
with bundle:
    names = set(bundle.namelist())
    for required in ("AppxSignature.p7x", "AppxMetadata/AppxBundleManifest.xml"):
        if required not in names:
            fail(f"{bundle_name} has no {required}" + (" (it is not signed)" if required == "AppxSignature.p7x" else ""))
    signers = {bundle_name: signer_sha256(bundle.read("AppxSignature.p7x"), bundle_name)}
    try:
        manifest = ET.fromstring(bundle.read("AppxMetadata/AppxBundleManifest.xml"))
    except ET.ParseError as error:
        fail(f"AppxBundleManifest.xml is not well-formed: {error}")
    identity = next((e for e in manifest if local(e.tag) == "Identity"), None)
    if identity is None:
        fail("AppxBundleManifest.xml has no Identity")
    packages = [e for e in manifest.iter() if local(e.tag) == "Package" and e.get("Type") == "application"]
    checks += [
        ("bundle Name", identity.get("Name"), "dinh.thi.EchoDiar"),
        ("bundle Version", identity.get("Version"), version),
        ("bundle Publisher", identity.get("Publisher"), publisher),
        ("bundle packages", sorted((e.get("Architecture"), e.get("Version")) for e in packages),
         [("arm64", version), ("x64", version)]),
    ]
    for package in packages:
        file_name = package.get("FileName") or ""
        if file_name not in names:
            fail(f"{bundle_name} has no {file_name!r}, which its manifest lists")
        try:
            inner = zipfile.ZipFile(io.BytesIO(bundle.read(file_name)))
        except zipfile.BadZipFile:
            fail(f"{file_name} in {bundle_name} is not a zip (MSIX) file")
        with inner:
            if "AppxSignature.p7x" not in inner.namelist():
                fail(f"{file_name} in {bundle_name} is not signed")
            signers[file_name] = signer_sha256(inner.read("AppxSignature.p7x"), file_name)
checks += [(f"signer of {name}", digest, pin) for name, digest in signers.items()]

# The feed.
try:
    root = ET.parse(path("EchoDiar.appinstaller")).getroot()
except ET.ParseError as error:
    fail(f"EchoDiar.appinstaller is not well-formed: {error}")
feed = {local(e.tag): e for e in root}
main = feed.get("MainBundle")
update = feed.get("UpdateSettings")
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
bad = [f"{name} is {actual!r}, expected {wanted!r}" for name, actual, wanted in checks if actual != wanted]
if bad:
    fail("; ".join(bad))
print(f"    OK: {', '.join(expected)}")
print(f"    OK: bundle and feed {version}, publisher '{publisher}'; the certificate, Setup's copy and "
      f"{len(signers)} signers match the pin {pin[:16]}...")
PY

# Checks the artifact in $1 (CHECK_PY). $2 the certificate's name, $3 the Publisher, $4 the pin, $5 the bundle Uri.
check_artifact() {
  local subject
  # RFC 2253 (OpenSSL "subject=CN=...", LibreSSL "subject= CN=..."); empty when it is no certificate.
  subject="$(openssl x509 -inform DER -in "$1/$2" -noout -subject -nameopt RFC2253 2>/dev/null || true)"
  subject="${subject#subject=}"
  "$PYTHON" -I -c "$CHECK_PY" "$1" "$VERSION4" "$3" "$APPINSTALLER_URI" "$5" "$2" "$4" "$subject" \
    || die "the artifact check failed (see above)"
}

# --- 2. --preflight: the release channel's one-time setup (read-only) -------
pf_ok() { echo "  ok    $*"; }
pf_fail() {
  if [[ -n "$*" ]]; then echo "  FAIL  $*" >&2; fi
  echo "error: the Windows release channel is not set up: see $SETUP_DOC" >&2
  exit 1
}

# The environment (stdin) and its deployment policies (argv): reviewer, self review, tag policy.
read -r -d '' ENV_PY <<'PY' || true
import json
import sys

me, policies_json = sys.argv[1:3]
env = json.load(sys.stdin)
name = env.get("name")

def ok(message):
    print(f"  ok    {message}", flush=True)

def fail(message):
    print(f"  FAIL  {message}", file=sys.stderr)
    sys.exit(1)

rules = [r for r in env.get("protection_rules") or [] if r.get("type") == "required_reviewers"]
if not rules:
    fail(f"{name} has no required reviewers")
logins = [(r.get("reviewer") or {}).get("login") for rule in rules for r in rule.get("reviewers") or []
          if r.get("type") == "User"]
if me not in logins:
    fail(f"the required reviewers of {name} ({', '.join(map(str, logins)) or 'no user'}) do not list you ({me})")
ok(f"required reviewer: {me}")
if any(rule.get("prevent_self_review") is not False for rule in rules):
    fail("prevent_self_review is on: you could not approve your own release run")
ok("prevent_self_review is off")
branch_policy = env.get("deployment_branch_policy") or {}
try:
    policies = json.loads(policies_json) if policies_json else {}
except ValueError:
    policies = {}
names = [(p.get("type"), p.get("name")) for p in policies.get("branch_policies") or []]
if branch_policy.get("custom_branch_policies") is not True or branch_policy.get("protected_branches") is not False \
        or names != [("tag", "v*")]:
    fail(f"the deployment policy of {name} is {branch_policy or 'none'} {names}, expected exactly the tag pattern v*")
ok("deployment policy: the tag pattern v* only")
PY

preflight() {
  local me env_json policies secrets name publisher pin ids id include covered=false
  echo "==> Preflight of the Windows release channel (read-only)"
  gh auth status >/dev/null 2>&1 || pf_fail "gh is not authenticated - run 'gh auth login'"
  me="$(gh api user --jq .login 2>/dev/null)" || pf_fail "cannot read the authenticated GitHub user"
  env_json="$(gh api "repos/$GH_REPO/environments/$RELEASE_ENV" 2>/dev/null)" \
    || pf_fail "the environment $RELEASE_ENV does not exist"
  pf_ok "environment $RELEASE_ENV exists"
  policies="$(gh api "repos/$GH_REPO/environments/$RELEASE_ENV/deployment-branch-policies" 2>/dev/null || true)"
  "$PYTHON" -I -c "$ENV_PY" "$me" "$policies" <<<"$env_json" || pf_fail   # ENV_PY printed the failed check
  # Names only: secret values are never readable.
  secrets="$(gh secret list --env "$RELEASE_ENV" --repo "$GH_REPO" --json name -q '.[].name' 2>/dev/null)" \
    || pf_fail "cannot list the secrets of $RELEASE_ENV"
  for name in WINDOWS_RELEASE_CERT_PFX WINDOWS_RELEASE_CERT_PASSWORD; do
    grep -qxF "$name" <<<"$secrets" || pf_fail "the secret $name of $RELEASE_ENV is not set"
  done
  pf_ok "secrets WINDOWS_RELEASE_CERT_PFX and WINDOWS_RELEASE_CERT_PASSWORD are set"
  publisher="$(gh variable get WINDOWS_PUBLIC_PUBLISHER --repo "$GH_REPO" 2>/dev/null || true)"
  [[ -n "$publisher" ]] || pf_fail "the variable WINDOWS_PUBLIC_PUBLISHER is not set"
  [[ "$publisher" != "$DEV_PUBLISHER" ]] || pf_fail "WINDOWS_PUBLIC_PUBLISHER is the dev subject $DEV_PUBLISHER"
  pf_ok "WINDOWS_PUBLIC_PUBLISHER: $publisher"
  git cat-file -e "HEAD:$WORKFLOW_PATH" 2>/dev/null || pf_fail "$WORKFLOW_PATH is not at HEAD"
  pf_ok "$WORKFLOW_PATH is at HEAD"
  pin="$(read_pin "$RELEASE_PIN_FILE")" \
    || pf_fail "$RELEASE_PIN_FILE at HEAD is missing or not exactly 64 lowercase hex characters"
  pf_ok "$RELEASE_PIN_FILE pins $pin"
  # Warning only: a ruleset is defense in depth on top of the environment's tag policy.
  ids="$(gh api "repos/$GH_REPO/rulesets" --jq '.[] | select(.target == "tag" and .enforcement == "active") | .id' \
    2>/dev/null || true)"
  # Listings go to variables first: with pipefail, grep -q closing the pipe early would fail the command before it.
  for id in $ids; do
    include="$(gh api "repos/$GH_REPO/rulesets/$id" --jq '.conditions.ref_name.include[]?' 2>/dev/null || true)"
    if grep -qxE 'refs/tags/v\*|~ALL' <<<"$include"; then
      covered=true
    fi
  done
  if [[ "$covered" == true ]]; then
    pf_ok "an active tag ruleset covers refs/tags/v*"
  else
    echo "  warn  no active tag ruleset covers refs/tags/v*: anyone with push access can create a release tag ($SETUP_DOC)"
  fi
  echo "Preflight OK: the Windows release channel is set up."
}

# --- 3. --dev: tag, watch, download, check, publish -------------------------
dev_release() {
  local dirty branch sha start discard run pin bundle bundle_uri out notes files=()
  [[ -n "$VERSION" ]] || die "--dev needs --version 0.0.N"
  set_version "$VERSION"
  TAG="windows-v$VERSION-dev"
  bundle="EchoDiar_$VERSION4.msixbundle"
  bundle_uri="https://github.com/$GH_REPO/releases/download/$TAG/$bundle"
  out="$REPO_ROOT/release/windows/$TAG"
  files=("$out/$bundle" "$out/EchoDiar.appinstaller" "$out/EchoDiarDev.cer" "$out/EchoDiar-Setup.exe" "$out/$SUMS")

  # Pre-flight. Tracked files only, as scripts/ship.sh; uncommitted changes only warn, because CI builds the
  # pushed tag's commit, never the working copy (as scripts/publish-release.sh).
  dirty="$(git status --porcelain --untracked-files=no)"
  if [[ -n "$dirty" ]]; then
    echo "warning: tracked files have uncommitted changes - the tag will point at HEAD, not your working copy:" >&2
    printf '%s\n' "$dirty" | sed 's/^/  /' >&2
  fi
  branch="$(git rev-parse --abbrev-ref HEAD)"
  [[ "$branch" != "HEAD" ]] || die "detached HEAD; check out a branch"
  gh auth status >/dev/null 2>&1 || die "gh is not authenticated - run 'gh auth login'."
  check_origin
  git fetch -q origin "$branch" || die "git fetch origin $branch failed"
  git merge-base --is-ancestor HEAD "origin/$branch" \
    || die "HEAD $(git rev-parse --short HEAD) is not on origin/$branch - push it first."
  if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
    die "tag $TAG already exists locally (git tag -d $TAG) - or pick another version."
  fi
  if git ls-remote --exit-code --tags origin "refs/tags/$TAG" >/dev/null 2>&1; then
    die "tag $TAG already exists on origin - pick another version."
  fi
  if gh release view "$TAG" --repo "$GH_REPO" >/dev/null 2>&1; then
    die "a GitHub release $TAG already exists."
  fi
  [[ ! -e "$out" ]] || die "$out already exists; move or delete it first."
  pin="$(read_pin "$DEV_PIN_FILE")" || die "$DEV_PIN_FILE at HEAD is missing or not 64 lowercase hex characters"
  sha="$(git rev-parse HEAD)"

  # Confirm, tag, watch.
  echo "About to build:"
  echo "  channel : dev"
  echo "  version : $VERSION (package $VERSION4)"
  echo "  tag     : $TAG  (at ${sha:0:7} on branch $branch)"
  echo "  output  : $out"
  echo "  publish : $PUBLISH"
  echo
  confirm "Push the tag $TAG (starts $WORKFLOW)?" || { echo "aborted."; exit 1; }

  echo "==> Tagging $TAG"
  git tag -a "$TAG" -m "EchoDiar for Windows $VERSION (dev)"
  # Runs of an earlier, deleted tag of the same name are older: only a run created after the push counts (60 s
  # of slack for the Mac's clock; BSD date).
  start="$(date -u -v-60S +%Y-%m-%dT%H:%M:%SZ)"
  git push origin "refs/tags/$TAG"

  discard="git push origin :refs/tags/$TAG && git tag -d $TAG"

  echo "==> Waiting for the $WORKFLOW run of $TAG"
  run=""
  # gh answers 404 until the first run exists while the workflow is not on the default branch: keep polling.
  for _ in $(seq 1 20); do
    run="$(gh run list --repo "$GH_REPO" --workflow "$WORKFLOW" --event push --limit 20 \
      --json databaseId,headBranch,createdAt \
      -q ".[] | select(.headBranch == \"$TAG\" and .createdAt >= \"$start\") | .databaseId" 2>/dev/null | head -1)" || run=""
    [[ -n "$run" ]] && break
    sleep 15
  done
  [[ -n "$run" ]] || die "no $WORKFLOW run for $TAG after 5 minutes. Discard the tag: $discard"
  echo "    run $run: https://github.com/$GH_REPO/actions/runs/$run"
  if ! gh run watch "$run" --repo "$GH_REPO" --exit-status --interval 30; then
    echo "error: run $run failed: gh run view $run --repo $GH_REPO --log-failed" >&2
    echo "       discard the tag before re-running: $discard" >&2
    exit 1
  fi

  # Download & check.
  echo "==> Downloading EchoDiar-$TAG into $out"
  mkdir -p "$out"
  gh run download "$run" --repo "$GH_REPO" -n "EchoDiar-$TAG" -D "$out"
  check_artifact "$out" EchoDiarDev.cer "$DEV_PUBLISHER" "$pin" "$bundle_uri"
  ls -l "$out"

  # Publish (only with --publish, after a confirmation).
  if [[ "$PUBLISH" == false ]]; then
    echo
    echo "Built and checked; not published (no --publish). The tag $TAG stays on origin."
    echo "  publish : re-run is not possible (the tag exists); create the prerelease by hand:"
    echo "            gh release create $TAG --repo $GH_REPO --verify-tag --latest=false --prerelease \\"
    echo "             $(printf ' "%s"' "${files[@]}")"
    echo "  discard : $discard"
    exit 0
  fi

  notes="EchoDiar for Windows $VERSION (dev channel, package $VERSION4), x64 and arm64, signed with the dev certificate ($DEV_PUBLISHER).

Install: run EchoDiar-Setup.exe --feed https://github.com/$GH_REPO/releases/download/$TAG/EchoDiar.appinstaller (it trusts EchoDiarDev.cer after a UAC prompt). Checksums: $SUMS."

  echo
  echo "About to create the GitHub prerelease $TAG (dev, not Latest) with:"
  printf '  %s\n' "${files[@]}"
  confirm "Create the prerelease?" || { echo "not published. The tag $TAG stays; discard it with: $discard"; exit 1; }

  gh release create "$TAG" "${files[@]}" --repo "$GH_REPO" --verify-tag --latest=false --prerelease \
    --title "EchoDiar for Windows $VERSION" --notes "$notes"

  echo
  echo "Done. Published $TAG -> https://github.com/$GH_REPO/releases/tag/$TAG"
}

# --- 4. --attach: approve, check and attach the Windows build of vX.Y.Z ------
# The newest windows-release.yml run of the tag's commit (stdin: actions/runs?head_sha=...), started by the tag push
# or a dispatch on the tag. `gh run list --json` has no path field.
read -r -d '' RUNS_PY <<'PY' || true
import json
import sys

tag, sha, path = sys.argv[1:4]
runs = [r for r in json.load(sys.stdin).get("workflow_runs") or []
        if r.get("path") == path and r.get("head_branch") == tag and r.get("head_sha") == sha
        and r.get("event") in ("push", "workflow_dispatch")]
if runs:
    print(max(runs, key=lambda r: r.get("created_at") or "")["id"])
PY

# The deployment of the environment argv[1] that the run waits for (stdin: its pending_deployments): prints its id
# and whether the authenticated user can approve it.
read -r -d '' PENDING_PY <<'PY' || true
import json
import sys

try:
    pending = json.load(sys.stdin)
except ValueError:
    pending = []
for item in pending if isinstance(pending, list) else []:
    env = item.get("environment") or {}
    if env.get("name") == sys.argv[1] and isinstance(env.get("id"), int):
        print(env["id"], "true" if item.get("current_user_can_approve") is True else "false")
        break
PY

# The release's assets (stdin: gh release view --json assets) against name=sha256 arguments: prints the names it
# lacks; fails when one it has differs (its digest is "sha256:<hex>").
read -r -d '' ASSETS_PY <<'PY' || true
import json
import sys

assets = {a.get("name"): a for a in json.load(sys.stdin).get("assets") or []}
missing, bad = [], []
for arg in sys.argv[1:]:
    name, digest = arg.split("=", 1)
    if name not in assets:
        missing.append(name)
    elif (assets[name].get("digest") or "").replace("sha256:", "", 1) != digest:
        bad.append(name)
if bad:
    sys.exit(f"error: assets of the release differ from SHA256SUMS-windows.txt (or have no digest): {', '.join(bad)}")
print("\n".join(missing))
PY

# Sets RUN to the newest run of the tag (RUNS_PY); fails when there is none yet.
find_run() {
  local json
  json="$(gh api "repos/$GH_REPO/actions/runs?head_sha=$SHA&per_page=50" 2>/dev/null)" || return 1
  RUN="$("$PYTHON" -I -c "$RUNS_PY" "$TAG" "$SHA" "$WORKFLOW_PATH" <<<"$json")" || return 1
  [[ -n "$RUN" ]]
}

# Polls find_run every 15 s for 5 minutes.
poll_run() {
  local waited
  for (( waited = 0; ; waited += 15 )); do
    find_run && return 0
    (( waited < 300 )) || return 1
    echo "    no run yet (${waited} s)"
    sleep 15
  done
}

# Waits until the current attempt of RUN completes. On the way, approves the release environment's deployment of
# this run only (a new attempt waits for approval again). When it cannot (not allowed, or the approval fails), it
# prints the run's URL and waits up to APPROVAL_TIMEOUT for a manual approval.
wait_run() {
  local started=$SECONDS manual_since="" status last="" printed=0 pending env_id can_approve
  while :; do
    status="$(gh run view "$RUN" --repo "$GH_REPO" --json status -q .status 2>/dev/null || echo unknown)"
    [[ "$status" == completed ]] && return 0
    pending="$(gh api "repos/$GH_REPO/actions/runs/$RUN/pending_deployments" 2>/dev/null || echo '[]')"
    read -r env_id can_approve <<<"$("$PYTHON" -I -c "$PENDING_PY" "$RELEASE_ENV" <<<"$pending" || true)" || true
    if [[ -z "${env_id:-}" ]]; then
      manual_since=""
    elif [[ -z "$manual_since" && "$can_approve" == true ]] \
        && gh api -X POST "repos/$GH_REPO/actions/runs/$RUN/pending_deployments" -F "environment_ids[]=$env_id" \
          -f state=approved -f comment="Approved by /cf-ship for $TAG" >/dev/null; then
      echo "    approved the $RELEASE_ENV deployment of run $RUN"
    else
      if [[ -z "$manual_since" ]]; then
        manual_since=$SECONDS
        echo "    cannot approve the $RELEASE_ENV deployment: approve it at $RUN_URL (waiting up to $(( APPROVAL_TIMEOUT / 60 )) min)" >&2
      fi
      (( SECONDS - manual_since < APPROVAL_TIMEOUT )) \
        || die "run $RUN was not approved within $(( APPROVAL_TIMEOUT / 60 )) min: $RUN_URL; re-run --attach after approving"
    fi
    (( SECONDS - started < RUN_TIMEOUT )) \
      || die "run $RUN did not complete within $(( RUN_TIMEOUT / 60 )) min: $RUN_URL; re-run --attach later"
    if [[ "$status" != "$last" ]] || (( SECONDS - printed >= 300 )); then
      echo "    run $RUN: $status ($(( (SECONDS - started) / 60 )) min)"
      last="$status"
      printed=$SECONDS
    fi
    sleep 30
  done
}

# Reruns RUN (gh run rerun "$@") and waits until its new attempt has started.
rerun() {
  local attempt
  attempt="$(gh run view "$RUN" --repo "$GH_REPO" --json attempt -q .attempt)" || die "gh run view $RUN failed"
  gh run rerun "$RUN" --repo "$GH_REPO" "$@" || die "gh run rerun $RUN $* failed: $RUN_URL"
  for _ in $(seq 1 20); do
    (( $(gh run view "$RUN" --repo "$GH_REPO" --json attempt -q .attempt 2>/dev/null || echo 0) > attempt )) && return 0
    sleep 6
  done
  die "the rerun of run $RUN did not start: $RUN_URL"
}

attach() {
  local dirty refs publisher pin bundle bundle_uri out name assets missing feed_main
  local reran_failed=false reran_all=false conclusion live upload=() hashes=() files=()

  # Guards, in this order. Nothing but fetches happens before the prompt.
  [[ "$TAG" =~ ^v(.+)$ ]] || die "--attach takes the release tag vX.Y.Z, not '$TAG'"
  set_version "${BASH_REMATCH[1]}"
  [[ "$(git rev-parse --abbrev-ref HEAD)" == main ]] \
    || die "--attach runs on 'main' (you are on '$(git rev-parse --abbrev-ref HEAD)'): it commits the feed there."
  gh auth status >/dev/null 2>&1 || die "gh is not authenticated - run 'gh auth login'."
  check_origin
  git fetch -q origin main || die "git fetch origin main failed"
  [[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] \
    || die "HEAD $(git rev-parse --short HEAD) is not origin/main $(git rev-parse --short origin/main) - pull or push first."
  dirty="$(git status --porcelain --untracked-files=no)"
  if [[ -n "$dirty" ]]; then
    echo "error: tracked files have uncommitted changes (--attach commits the feed on main; commit, stash or discard them):" >&2
    printf '%s\n' "$dirty" | sed 's/^/  /' >&2
    exit 1
  fi
  refs="$(git ls-remote --tags origin "refs/tags/$TAG" "refs/tags/$TAG^{}")" || die "git ls-remote origin failed"
  [[ -n "$refs" ]] || die "tag $TAG is not on origin."
  # The commit: the peeled line of an annotated tag, else the tag's own line.
  SHA="$(awk -v ref="refs/tags/$TAG^{}" '$2 == ref { print $1 }' <<<"$refs")"
  [[ -n "$SHA" ]] || SHA="$(awk -v ref="refs/tags/$TAG" '$2 == ref { print $1 }' <<<"$refs")"
  [[ "$(gh release view "$TAG" --repo "$GH_REPO" --json isDraft -q .isDraft 2>/dev/null || true)" == false ]] \
    || die "the GitHub release $TAG does not exist or is not published yet (publish-release.sh creates it)."
  git cat-file -e "$SHA^{commit}" 2>/dev/null || git fetch -q origin "refs/tags/$TAG" \
    || die "cannot fetch the commit of $TAG"
  if ! git cat-file -e "$SHA:$SETUP_CSPROJ" 2>/dev/null; then
    echo "$TAG has no Windows app (macOS-only release): nothing to attach"
    exit 0
  fi
  publisher="$(gh variable get WINDOWS_PUBLIC_PUBLISHER --repo "$GH_REPO" 2>/dev/null || true)"
  [[ -n "$publisher" && "$publisher" != "$DEV_PUBLISHER" ]] \
    || die "WINDOWS_PUBLIC_PUBLISHER is not the release certificate's subject: run --preflight ($SETUP_DOC)"
  pin="$(read_pin "$RELEASE_PIN_FILE")" \
    || die "$RELEASE_PIN_FILE at HEAD is missing or not 64 lowercase hex characters: run --preflight ($SETUP_DOC)"

  bundle="EchoDiar_$VERSION4.msixbundle"
  bundle_uri="https://github.com/$GH_REPO/releases/download/$TAG/$bundle"
  out="$REPO_ROOT/release/windows/$TAG"
  files=("$bundle" "EchoDiar.appinstaller" "EchoDiar.cer" "EchoDiar-Setup.exe" "$SUMS")

  echo "About to attach the Windows app to the release $TAG:"
  echo "  commit  : ${SHA:0:7}"
  echo "  version : $VERSION4, publisher '$publisher'"
  echo "  output  : $out"
  echo "  then    : approve this tag's $WORKFLOW run ($RELEASE_ENV), upload the missing assets, commit $FEED on main"
  echo
  confirm "Attach?" || { echo "aborted."; exit 1; }

  # Run & download. A folder that already verifies (an earlier --attach) is reused as it is: a rebuild would sign
  # new bytes, which no longer match assets already uploaded.
  if [[ -f "$out/$SUMS" ]] && (cd "$out" && shasum -a 256 -c --status --strict "$SUMS") >/dev/null 2>&1; then
    echo "==> $out already verifies against its $SUMS: not downloading again (delete it to download again)"
  else
    echo "==> Looking for the $WORKFLOW run of $TAG (${SHA:0:7})"
    RUN=""
    if ! poll_run; then
      echo "==> No run after 5 minutes: dispatching $WORKFLOW on $TAG (channel release)"
      gh workflow run "$WORKFLOW" --repo "$GH_REPO" --ref "$TAG" -f "version=$VERSION" -f channel=release \
        || die "gh workflow run failed"
      poll_run || die "no $WORKFLOW run for $TAG after the dispatch: https://github.com/$GH_REPO/actions/workflows/$WORKFLOW"
    fi
    RUN_URL="https://github.com/$GH_REPO/actions/runs/$RUN"
    echo "    run $RUN: $RUN_URL"
    while :; do
      wait_run
      conclusion="$(gh run view "$RUN" --repo "$GH_REPO" --json conclusion -q .conclusion)"
      if [[ "$conclusion" != success ]]; then
        [[ "$reran_failed" == false ]] \
          || die "run $RUN failed again ($conclusion): gh run view $RUN --repo $GH_REPO --log-failed"
        echo "==> Run $RUN ended '$conclusion': rerunning its failed jobs once"
        rerun --failed
        reran_failed=true
        continue
      fi
      live="$(gh api "repos/$GH_REPO/actions/runs/$RUN/artifacts?name=EchoDiar-$TAG" \
        --jq '[.artifacts[] | select(.expired == false)] | length')" || die "cannot list the artifacts of run $RUN"
      (( live > 0 )) && break
      [[ "$reran_all" == false ]] || die "run $RUN has no artifact EchoDiar-$TAG after a full rerun: $RUN_URL"
      echo "==> The artifact EchoDiar-$TAG of run $RUN has expired: rerunning the whole run"
      rerun
      reran_all=true
    done

    echo "==> Downloading EchoDiar-$TAG into $out"
    rm -rf "$out"
    mkdir -p "$out"
    gh run download "$RUN" --repo "$GH_REPO" -n "EchoDiar-$TAG" -D "$out" || die "gh run download failed"
  fi

  # Checks (before any upload).
  check_artifact "$out" EchoDiar.cer "$publisher" "$pin" "$bundle_uri"

  # Upload what the release lacks; what it has must be these bytes (never --clobber).
  for name in "${files[@]}"; do
    hashes+=("$name=$(shasum -a 256 "$out/$name" | cut -d' ' -f1)")
  done
  assets="$(gh release view "$TAG" --repo "$GH_REPO" --json assets)" || die "gh release view $TAG failed"
  missing="$("$PYTHON" -I -c "$ASSETS_PY" "${hashes[@]}" <<<"$assets")" || die "nothing was uploaded (see above)"
  while IFS= read -r name; do
    [[ -n "$name" ]] && upload+=("$out/$name")
  done <<<"$missing"
  if (( ${#upload[@]} > 0 )); then
    echo "==> Uploading to $TAG: ${missing//$'\n'/, }"
    gh release upload "$TAG" "${upload[@]}" --repo "$GH_REPO" || die "gh release upload failed; re-run --attach"
  else
    echo "==> The release $TAG already has the five Windows assets"
  fi

  # The feed on main (pages.yml serves it at $APPINSTALLER_URI).
  git fetch -q origin main || die "git fetch origin main failed"
  feed_main="$(git show "origin/main:$FEED" 2>/dev/null || true)"
  if grep -qF "Version=\"$VERSION4\"" <<<"$feed_main"; then
    echo "==> origin/main's $FEED already has $VERSION4"
  else
    [[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] \
      || die "main moved during the attach: git pull --ff-only, then re-run --attach to commit the feed"
    echo "==> Committing $FEED ($VERSION4) on main"
    cp "$out/EchoDiar.appinstaller" "$FEED"
    git add -- "$FEED"
    git commit -q -m "chore(release): windows appinstaller $TAG" -- "$FEED"
    git push origin main || die "git push origin main failed: push the feed commit (git push origin main)"
  fi

  echo
  echo "Done. Windows is attached to $TAG -> https://github.com/$GH_REPO/releases/tag/$TAG"
  echo "  setup : https://github.com/$GH_REPO/releases/download/$TAG/EchoDiar-Setup.exe"
  echo "  feed  : $APPINSTALLER_URI"
  echo "  next  : curl -fsSI $APPINSTALLER_URI   (Pages deploys the feed within minutes)"
}

case "$MODE" in
  dev) dev_release ;;
  preflight) preflight ;;
  attach) attach ;;
esac
