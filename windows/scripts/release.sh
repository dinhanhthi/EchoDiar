#!/usr/bin/env bash
#
# release.sh - Build, check and publish the Windows app of EchoDiar (docs/RELEASING.md).
#
# Runs on the Mac. The build happens on GitHub Actions (.github/workflows/windows-release.yml), started by a tag push.
# Its artifact EchoDiar-<tag> holds four files: EchoDiar_X.Y.Z.0.msixbundle, the App Installer feed
# EchoDiar.appinstaller, the public certificate (EchoDiar.cer; EchoDiarDev.cer on the dev channel) and
# SHA256SUMS-windows.txt. Modes:
#   --dev        pushes the tag windows-vX.Y.Z-dev at HEAD (any branch: a tag rather than a dispatch, because GitHub
#                dispatches only workflows that are on the default branch), watches the run, downloads the artifact
#                into release/windows/<tag>/ (gitignored) and checks it. With --publish it then creates the GitHub
#                prerelease of the tag with the four files (never Latest, which stays the release the website links).
#   --preflight  read-only check of the release channel's one-time setup (docs/RELEASING.md §3.0): the
#                windows-release-signing environment, its reviewer, tag policy and secrets, the variable
#                WINDOWS_PUBLIC_PUBLISHER and the pinned release certificate. /cf-ship runs it before it builds.
#                The Microsoft Store setup (the WINDOWS_STORE_* variables; with WINDOWS_STORE_PUBLISH 'true', the
#                windows-store-publish environment's secrets) only warns (docs/RELEASING.md §3.3).
#   --attach     the Windows step of scripts/ship.sh, once the release vX.Y.Z is published. Its tag, pushed by
#                publish-release.sh, started the workflow on the release channel: this approves that run's
#                windows-release-signing deployment (running /cf-ship is the approval), waits for it, downloads the
#                artifact into release/windows/vX.Y.Z/, checks it, uploads the assets the release lacks and commits
#                the feed windows/EchoDiar.appinstaller on main (pages.yml serves it). Idempotent: re-run it to finish.
#                It judges only the jobs it needs (prepare, the sideload package legs, sign, assemble): the Store jobs
#                of the run (package store legs, store, store-publish) failing or waiting never block it. To rerun a
#                failed needed job it rejects a waiting windows-store-publish deployment (GitHub reruns only a
#                completed run): that run then never submits to the Store, and --store gives the manual upload steps.
#   --store      the Microsoft Store step of scripts/ship.sh, after --attach. In the same run of the tag, approves the
#                windows-store-publish deployment of store-publish when it waits (CI publishes only when the variable
#                WINDOWS_STORE_PUBLISH is 'true'), downloads the artifact EchoDiar-store-<tag> into
#                release/windows/vX.Y.Z/store/ (gitignored) and checks it, then prints "Submitted to the Microsoft
#                Store by CI" or the Partner Center upload steps ending with "manual Store upload pending" (also when
#                store-publish failed or was rejected); without the WINDOWS_STORE_* variables it prints "Store not set
#                up yet", and for a run that built no Store bundle (older than the Store variables) "no Store bundle
#                for this tag". Idempotent: re-run it to finish.
#
# The checks (dev and attach) read the downloaded files as data, with an isolated Python (-I), and never run them:
# exactly the four files and no private key; SHA256SUMS-windows.txt; the bundle (signed, Name, Version, Publisher,
# x64 and arm64, both inner packages signed) and the feed (Version, Publisher, both Uris, update settings); the
# certificate's subject is the Publisher. Any self-signed key can claim a subject, so the signer is pinned too: the
# certificate and the signers of the bundle and of each inner package must have the DER SHA-256 pinned at HEAD in
# windows/packaging/dev-cert.sha256 (dev) or windows/packaging/release-cert.sha256 (attach). The Store check (--store)
# reads its .msixupload the same way: one unsigned bundle with the Store identity (Name, Publisher and
# PublisherDisplayName), x64 and arm64, no packageManagement.
#
# Version: X.Y.Z gives the 4-part package version X.Y.Z.0 (each part 0-65535, no leading zeros). Dev builds use
# 0.0.N, with N above the 0.0.<run>.0 of the windows.yml CI builds; a release uses the version of its tag.
#
# Usage:
#   windows/scripts/release.sh --dev --version 0.0.900                    # dev: build + check, no release
#   windows/scripts/release.sh --dev --version 0.0.900 --publish [--yes]  # ... then create the prerelease
#   windows/scripts/release.sh --preflight                                # check the release channel's setup
#   windows/scripts/release.sh --attach v1.2.0 [--yes]                    # attach Windows to the release v1.2.0
#   windows/scripts/release.sh --store v1.2.0 [--yes]                     # the Microsoft Store step of v1.2.0
# --yes answers every prompt (scripts/ship.sh and unattended runs).
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GH_REPO="dinhanhthi/EchoDiar"
WORKFLOW="windows-release.yml"
WORKFLOW_PATH=".github/workflows/$WORKFLOW"
RELEASE_ENV="windows-release-signing"
STORE_ENV="windows-store-publish"
STORE_VARS=(WINDOWS_STORE_IDENTITY_NAME WINDOWS_STORE_PUBLISHER WINDOWS_STORE_PUBLISHER_DISPLAY_NAME
  WINDOWS_STORE_PRODUCT_ID)
STORE_SECRETS=(STORE_TENANT_ID STORE_SELLER_ID STORE_CLIENT_ID STORE_CLIENT_SECRET)
DEV_PUBLISHER="CN=EchoDiar Dev"
APPINSTALLER_URI="https://ed.dinhanhthi.com/windows/EchoDiar.appinstaller"
APP_CSPROJ="windows/src/EchoDiar.App/EchoDiar.App.csproj"
FEED="windows/EchoDiar.appinstaller"
DEV_PIN_FILE="windows/packaging/dev-cert.sha256"
RELEASE_PIN_FILE="windows/packaging/release-cert.sha256"
SETUP_DOC="docs/RELEASING.md §3.0"
STORE_DOC="docs/RELEASING.md §3.3"
SUMS="SHA256SUMS-windows.txt"
RUN_TIMEOUT=7200      # seconds one run attempt may take, approval included, before --attach or --store gives up
APPROVAL_TIMEOUT=1800 # seconds --attach or --store waits for a manual approval when it cannot approve itself
PYTHON=/usr/bin/python3

USAGE="usage: windows/scripts/release.sh --dev --version 0.0.N [--publish] [--yes]
       windows/scripts/release.sh --preflight
       windows/scripts/release.sh --attach vX.Y.Z [--yes]
       windows/scripts/release.sh --store vX.Y.Z [--yes]
  --dev        build and check a dev build (tag windows-v0.0.N-dev at HEAD); --publish creates its prerelease
  --preflight  read-only check of the release channel's one-time setup ($SETUP_DOC; Store setup: warnings only)
  --attach     approve, check and attach the Windows build of the published release vX.Y.Z; commit its feed
  --store      approve the Store publish of vX.Y.Z's run, download and check its Store bundle
               (release/windows/vX.Y.Z/store/), or print the Partner Center upload steps ($STORE_DOC)
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
    --store) (( $# > 1 )) || usage; set_mode store; TAG="$2"; shift ;;
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
summed = sorted([bundle_name, "EchoDiar.appinstaller", cer_name])
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

# The four files, flat, and never a private key. The folder store/ holds the Store bundle of --store (its own check).
entries = sorted(name for name in os.listdir(out)
                 if not (name == "store" and os.path.isdir(path(name)) and not os.path.islink(path(name))))
keys = [name for name in entries if name.lower().endswith((".pfx", ".p12"))]
if keys:
    fail(f"private key in the artifact: {', '.join(keys)}")
if entries != expected or not all(os.path.isfile(path(name)) and not os.path.islink(path(name)) for name in entries):
    fail(f"the artifact holds {entries}, expected the files {expected}")

# GNU format (lowercase hash, two spaces, name, LF) for exactly the three other files.
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

# The certificate. The Publisher is a Windows name (", " between parts), the subject RFC 2253.
cer = read(cer_name)

def dn(value):
    return re.sub(r"\s*,\s*", ",", value.strip())

checks = [
    (f"{cer_name} subject", dn(cer_subject), dn(publisher)),
    (f"{cer_name} SHA-256", hashlib.sha256(cer).hexdigest(), pin),
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
print(f"    OK: bundle and feed {version}, publisher '{publisher}'; the certificate and {len(signers)} signers "
      f"match the pin {pin[:16]}...")
PY

# Checks the artifact in $1 (CHECK_PY). $2 the certificate's name, $3 the Publisher, $4 the pin, $5 the bundle Uri,
# $6 the feed's own Uri.
check_artifact() {
  local subject
  # RFC 2253 (OpenSSL "subject=CN=...", LibreSSL "subject= CN=..."); empty when it is no certificate.
  subject="$(openssl x509 -inform DER -in "$1/$2" -noout -subject -nameopt RFC2253 2>/dev/null || true)"
  subject="${subject#subject=}"
  "$PYTHON" -I -c "$CHECK_PY" "$1" "$VERSION4" "$3" "$6" "$5" "$2" "$4" "$subject" \
    || die "the artifact check failed (see above)"
}

# The value of the repository variable argv[1] (stdin: gh variable list --json name,value); empty when it is not
# set or holds a line break (the workflow's prepare job counts such a value as not set). Fails on unreadable JSON.
read -r -d '' VARS_PY <<'PY' || true
import json
import sys

try:
    variables = {v.get("name"): v.get("value") for v in json.load(sys.stdin)}
except (ValueError, AttributeError, TypeError):
    sys.exit("error: cannot read the repository variables")
value = variables.get(sys.argv[1])
print(value if isinstance(value, str) and "\r" not in value and "\n" not in value else "")
PY

# Prints the value of the repository variable $2 from the listing $1 (VARS_PY).
var_value() { "$PYTHON" -I -c "$VARS_PY" "$2" <<<"$1"; }

# --- 2. --preflight: the release channel's one-time setup (read-only) -------
pf_ok() { echo "  ok    $*"; }
pf_fail() {
  if [[ -n "$*" ]]; then echo "  FAIL  $*" >&2; fi
  echo "error: the Windows release channel is not set up: see $SETUP_DOC" >&2
  exit 1
}

# The environment (stdin) and its deployment policies (argv): reviewer, self review, tag policy. With argv[3] "warn"
# (and argv[4] the doc to point to), each failed check prints a warning instead and the exit status is 0.
read -r -d '' ENV_PY <<'PY' || true
import json
import sys

me, policies_json = sys.argv[1:3]
warn = sys.argv[3:4] == ["warn"]
doc = sys.argv[4] if len(sys.argv) > 4 else ""
env = json.load(sys.stdin)
name = env.get("name")
label = f"{name}: " if warn else ""

def ok(message):
    print(f"  ok    {label}{message}", flush=True)

def fail(message):
    if warn:
        print(f"  warn  {message} ({doc})", flush=True)
        return
    print(f"  FAIL  {message}", file=sys.stderr)
    sys.exit(1)

rules = [r for r in env.get("protection_rules") or [] if r.get("type") == "required_reviewers"]
logins = [(r.get("reviewer") or {}).get("login") for rule in rules for r in rule.get("reviewers") or []
          if r.get("type") == "User"]
if not rules:
    fail(f"{name} has no required reviewers")
elif me not in logins:
    fail(f"the required reviewers of {name} ({', '.join(map(str, logins)) or 'no user'}) do not list you ({me})")
else:
    ok(f"required reviewer: {me}")
if rules:
    if any(rule.get("prevent_self_review") is not False for rule in rules):
        fail(f"prevent_self_review is on in {name}: you could not approve your own run")
    else:
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
else:
    ok("deployment policy: the tag pattern v* only")
PY

preflight() {
  local me env_json policies secrets name publisher pin ids id include covered=false vars missing=()
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
  # The Microsoft Store: warnings only, never a failure (the self-signed release ships without it).
  if ! vars="$(gh variable list --repo "$GH_REPO" --json name,value 2>/dev/null)"; then
    echo "  warn  cannot list the repository variables: the Microsoft Store setup was not checked ($STORE_DOC)"
  else
    for name in "${STORE_VARS[@]}"; do
      [[ -n "$(var_value "$vars" "$name" 2>/dev/null)" ]] || missing+=("$name")
    done
    if (( ${#missing[@]} > 0 )); then
      echo "  warn  not set: ${missing[*]}: releases build no Microsoft Store bundle ($STORE_DOC)"
    else
      pf_ok "the Microsoft Store variables are set: ${STORE_VARS[*]}"
    fi
    if [[ "$(var_value "$vars" WINDOWS_STORE_PUBLISH 2>/dev/null)" == true ]]; then
      echo "  info  WINDOWS_STORE_PUBLISH is true: CI submits each release to the Store (environment $STORE_ENV)"
      # Names only: secret values are never readable.
      if ! secrets="$(gh secret list --env "$STORE_ENV" --repo "$GH_REPO" --json name -q '.[].name' 2>/dev/null)"; then
        echo "  warn  the environment $STORE_ENV does not exist or its secrets cannot be listed: CI cannot publish ($STORE_DOC)"
      else
        missing=()
        for name in "${STORE_SECRETS[@]}"; do
          grep -qxF "$name" <<<"$secrets" || missing+=("$name")
        done
        if (( ${#missing[@]} > 0 )); then
          echo "  warn  secrets of $STORE_ENV not set: ${missing[*]}: CI cannot publish ($STORE_DOC)"
        else
          pf_ok "secrets of $STORE_ENV are set: ${STORE_SECRETS[*]}"
        fi
      fi
      # Its protection (reviewer, self review, tag policy) as for the release environment, warnings only.
      if env_json="$(gh api "repos/$GH_REPO/environments/$STORE_ENV" 2>/dev/null)"; then
        policies="$(gh api "repos/$GH_REPO/environments/$STORE_ENV/deployment-branch-policies" 2>/dev/null || true)"
        "$PYTHON" -I -c "$ENV_PY" "$me" "$policies" warn "$STORE_DOC" <<<"$env_json" 2>/dev/null \
          || echo "  warn  cannot read the protection of $STORE_ENV ($STORE_DOC)"
      fi
    else
      echo "  info  WINDOWS_STORE_PUBLISH is not true: upload each release's Store bundle in Partner Center (--store)"
    fi
  fi
  echo "Preflight OK: the Windows release channel is set up."
}

# --- 3. --dev: tag, watch, download, check, publish -------------------------
dev_release() {
  local dirty branch sha start discard run pin bundle bundle_uri feed_uri out notes files=()
  [[ -n "$VERSION" ]] || die "--dev needs --version 0.0.N"
  set_version "$VERSION"
  TAG="windows-v$VERSION-dev"
  bundle="EchoDiar_$VERSION4.msixbundle"
  bundle_uri="https://github.com/$GH_REPO/releases/download/$TAG/$bundle"
  # A dev feed's Uri is its own prerelease asset (App Installer always reads the feed from its Uri).
  feed_uri="https://github.com/$GH_REPO/releases/download/$TAG/EchoDiar.appinstaller"
  out="$REPO_ROOT/release/windows/$TAG"
  files=("$out/$bundle" "$out/EchoDiar.appinstaller" "$out/EchoDiarDev.cer" "$out/$SUMS")

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
  check_artifact "$out" EchoDiarDev.cer "$DEV_PUBLISHER" "$pin" "$bundle_uri" "$feed_uri"
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

Install:
1. Open EchoDiarDev.cer > Install Certificate > Local Machine > Place all certificates in the following store > Trusted People.
2. Open EchoDiar.appinstaller > Install (it installs from this prerelease's own feed).

Checksums: $SUMS."

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

# The run's jobs (stdin: actions/runs/<id>/jobs?filter=latest; argv[1] the run's status). --attach needs only the
# self-signed jobs (prepare, the sideload package legs, sign, assemble; a tag older than the Store variant names its
# legs "package (x64)"); the Store jobs (the store package legs, store, store-publish) never block it. The jobs
# endpoint may list a job only once it is created, so "ok" needs assemble done or the whole run completed. Prints
# seven lines:
#   1. running (a needed job has not completed), ok (all succeeded) or failed
#   2. what a rerun targets when failed: --job=<id> when the earliest failed needed job is sign or assemble (that job
#      and its dependents only), else --failed (a failed package leg or prepare skips the Store jobs, so they never
#      fail on their own then); "-" otherwise
#   3. the needed jobs that did not succeed, 4. the Store jobs that failed, 5. the Store jobs not completed yet
#      ("; "-separated names, or "-")
#   6. the store job's, 7. the store-publish job's conclusion (or status while not completed; "absent")
read -r -d '' JOBS_PY <<'PY' || true
import json
import re
import sys

run_status = sys.argv[1]
try:
    jobs = json.load(sys.stdin)["jobs"]
    if not isinstance(jobs, list):
        raise TypeError("jobs is not a list")
except (ValueError, KeyError, TypeError) as error:
    sys.exit(f"error: cannot read the jobs of the run ({error})")
jobs = [j for j in jobs if isinstance(j, dict) and isinstance(j.get("name"), str)]

def is_store(name):
    return name in ("store", "store-publish") or re.match(r"package \(.*\bstore\b", name) is not None

def done(job):
    return job.get("status") == "completed"

def state(name):
    job = next((j for j in jobs if j["name"] == name), None)
    if job is None:
        return "absent"
    return (job.get("conclusion") if done(job) else job.get("status")) or "unknown"

def names(items):
    return "; ".join(j["name"] for j in items) or "-"

ORDER = {"prepare": 0, "sign": 2, "assemble": 3}  # package legs and anything else: 1
needed = [j for j in jobs if not is_store(j["name"])]
store = [j for j in jobs if is_store(j["name"])]
bad = sorted((j for j in needed if done(j) and j.get("conclusion") != "success"),
             key=lambda j: ORDER.get(j["name"], 1))
if any(not done(j) for j in needed):
    verdict = "running"
elif run_status == "completed":
    verdict = "failed" if bad or not needed else "ok"
elif bad:
    verdict = "failed"
else:
    verdict = "ok" if state("assemble") == "success" else "running"
rerun = "-"
if verdict == "failed":
    first = bad[0] if bad else None
    rerun = f"--job={first['id']}" if first and first["name"] in ("sign", "assemble") else "--failed"
print(verdict)
print(rerun)
print(names(bad))
print(names([j for j in store if done(j) and j.get("conclusion") not in ("success", "skipped", "neutral")]))
print(names([j for j in store if not done(j)]))
print(state("store"))
print(state("store-publish"))
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

# origin/main's feed (stdin; empty when there is none) against the package version argv[1]: prints "none", or
# "older", "same" or "newer" and the feed's Version. Fails when the feed or its Version cannot be read.
read -r -d '' FEED_PY <<'PY' || true
import re
import sys
import xml.etree.ElementTree as ET

def parts(version, what):
    if not re.fullmatch(r"(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){3}", version or ""):
        sys.exit(f"error: {what} {version!r} is not a four-part version")
    return tuple(int(part) for part in version.split("."))

data = sys.stdin.buffer.read()
if not data.strip():
    print("none")
    sys.exit(0)
try:
    current = ET.fromstring(data).get("Version")
except ET.ParseError as error:
    sys.exit(f"error: origin/main's feed is not well-formed: {error}")
mine, theirs = parts(sys.argv[1], "the package version"), parts(current, "origin/main's feed Version")
print("older" if theirs < mine else "same" if theirs == mine else "newer", current)
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

# Prints RUN's status ("unknown" when gh fails).
run_status() { gh run view "$RUN" --repo "$GH_REPO" --json status -q .status 2>/dev/null || echo unknown; }

# Prints JOBS_PY's seven lines on RUN's jobs; $1 is the run's status. Fails when the jobs cannot be listed or read.
run_jobs() {
  local json
  json="$(gh api "repos/$GH_REPO/actions/runs/$RUN/jobs?filter=latest&per_page=100" 2>/dev/null)" || return 1
  "$PYTHON" -I -c "$JOBS_PY" "$1" <<<"$json"
}

# Waits for the current attempt of RUN: $1 = needed (until the jobs --attach needs are done, JOBS_PY; the Store
# jobs may still run or wait) or run (until the whole run completes). On the way, approves the APPROVE_ENV
# deployment of this run only (a new attempt waits for approval again). When it cannot (not allowed, or the approval
# fails), it prints the run's URL and waits up to APPROVAL_TIMEOUT for a manual approval. With $1 = run, a waiting
# deployment of OTHER_ENV, which this mode never approves, stops it with OTHER_HINT; with $2 = reject, it rejects
# that deployment instead (when the user may, else it stops) and sets OTHER_REJECTED=true.
wait_run() {
  local until="$1" on_other="${2:-}" started=$SECONDS manual_since="" status last="" printed=0 pending env_id
  local can_approve verdict other_id other_can
  while :; do
    status="$(run_status)"
    [[ "$status" == completed ]] && return 0
    if [[ "$until" == needed ]]; then
      verdict="$(run_jobs "$status" 2>/dev/null)" || verdict=running
      [[ "${verdict%%$'\n'*}" == running ]] || return 0
    fi
    pending="$(gh api "repos/$GH_REPO/actions/runs/$RUN/pending_deployments" 2>/dev/null || echo '[]')"
    if [[ "$until" == run ]]; then
      read -r other_id other_can <<<"$("$PYTHON" -I -c "$PENDING_PY" "$OTHER_ENV" <<<"$pending" || true)" || true
      if [[ -n "${other_id:-}" && "${OTHER_REJECTED:-}" != true ]]; then
        [[ "$on_other" == reject ]] \
          || die "run $RUN waits for the $OTHER_ENV approval, which --$MODE does not give: $OTHER_HINT"
        [[ "$other_can" == true ]] \
          && gh api -X POST "repos/$GH_REPO/actions/runs/$RUN/pending_deployments" -F "environment_ids[]=$other_id" \
            -f state=rejected \
            -f comment="Rejected by release.sh --attach to rerun the self-signed jobs; use release.sh --store $TAG for the Store build" \
            >/dev/null \
          || die "run $RUN waits for the $OTHER_ENV approval and --$MODE cannot reject it: $OTHER_HINT"
        OTHER_REJECTED=true
        echo "    rejected the $OTHER_ENV deployment of run $RUN, so that its failed jobs can be rerun: this attempt"
        echo "    does not submit $TAG to the Microsoft Store (windows/scripts/release.sh --store $TAG gives the manual"
        echo "    upload steps, or approves store-publish if a rerun queues it again)"
      fi
    fi
    read -r env_id can_approve <<<"$("$PYTHON" -I -c "$PENDING_PY" "$APPROVE_ENV" <<<"$pending" || true)" || true
    if [[ -z "${env_id:-}" ]]; then
      manual_since=""
    elif [[ -z "$manual_since" && "$can_approve" == true ]] \
        && gh api -X POST "repos/$GH_REPO/actions/runs/$RUN/pending_deployments" -F "environment_ids[]=$env_id" \
          -f state=approved -f comment="Approved by /cf-ship for $TAG" >/dev/null; then
      echo "    approved the $APPROVE_ENV deployment of run $RUN"
    else
      if [[ -z "$manual_since" ]]; then
        manual_since=$SECONDS
        echo "    cannot approve the $APPROVE_ENV deployment: approve it at $RUN_URL (waiting up to $(( APPROVAL_TIMEOUT / 60 )) min)" >&2
      fi
      (( SECONDS - manual_since < APPROVAL_TIMEOUT )) \
        || die "run $RUN was not approved within $(( APPROVAL_TIMEOUT / 60 )) min: $RUN_URL; re-run --$MODE after approving"
    fi
    (( SECONDS - started < RUN_TIMEOUT )) \
      || die "run $RUN did not complete within $(( RUN_TIMEOUT / 60 )) min: $RUN_URL; re-run --$MODE later"
    if [[ "$status" != "$last" ]] || (( SECONDS - printed >= 300 )); then
      echo "    run $RUN: $status ($(( (SECONDS - started) / 60 )) min)"
      last="$status"
      printed=$SECONDS
    fi
    sleep 30
  done
}

# Sets SHA to the commit of the tag TAG on origin (the peeled line of an annotated tag, else the tag's own line).
resolve_tag_sha() {
  local refs
  refs="$(git ls-remote --tags origin "refs/tags/$TAG" "refs/tags/$TAG^{}")" || die "git ls-remote origin failed"
  [[ -n "$refs" ]] || die "tag $TAG is not on origin."
  SHA="$(awk -v ref="refs/tags/$TAG^{}" '$2 == ref { print $1 }' <<<"$refs")"
  [[ -n "$SHA" ]] || SHA="$(awk -v ref="refs/tags/$TAG" '$2 == ref { print $1 }' <<<"$refs")"
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
  local dirty subject publisher pin bundle bundle_uri out name assets missing feed_main feed_state feed_version
  local reran_failed=false reran_all=false live expired upload=() hashes=() files=()
  local verdict state rerun_arg failed_jobs store_failed="-" store_pending="-"

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
  resolve_tag_sha
  [[ "$(gh release view "$TAG" --repo "$GH_REPO" --json isDraft -q .isDraft 2>/dev/null || true)" == false ]] \
    || die "the GitHub release $TAG does not exist or is not published yet (publish-release.sh creates it)."
  git cat-file -e "$SHA^{commit}" 2>/dev/null || git fetch -q origin "refs/tags/$TAG" \
    || die "cannot fetch the commit of $TAG"
  # The approval below lets the tag's workflow see the release signing key: only main's code may get it, never a
  # tag planted on a side branch's commit (origin/main was fetched above).
  git merge-base --is-ancestor "$SHA" origin/main \
    || die "the commit $SHA of $TAG is not on origin/main: --attach approves the signing run of main's code only."
  subject="$(git log -1 --format=%s "$SHA")"
  if ! git cat-file -e "$SHA:$APP_CSPROJ" 2>/dev/null; then
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
  files=("$bundle" "EchoDiar.appinstaller" "EchoDiar.cer" "$SUMS")

  echo "About to attach the Windows app to the release $TAG:"
  echo "  commit  : $SHA (on main)"
  echo "  subject : $subject"
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
    # Only the jobs this release needs count (JOBS_PY): a Store job failing, or store-publish waiting for its
    # approval (--store gives it), never blocks the attach.
    APPROVE_ENV="$RELEASE_ENV"
    OTHER_ENV="$STORE_ENV"
    OTHER_HINT="approve or reject it at $RUN_URL (windows/scripts/release.sh --store $TAG approves it), then re-run --attach"
    OTHER_REJECTED=false
    while :; do
      wait_run needed
      verdict="$(run_jobs "$(run_status)")" || die "cannot list the jobs of run $RUN: $RUN_URL"
      { read -r state; read -r rerun_arg; read -r failed_jobs; read -r store_failed; read -r store_pending; } <<<"$verdict"
      if [[ "$state" == running ]]; then
        sleep 15
        continue
      fi
      if [[ "$state" != ok ]]; then
        [[ "$reran_failed" == false ]] \
          || die "run $RUN failed again ($failed_jobs): gh run view $RUN --repo $GH_REPO --log-failed"
        # GitHub reruns only a completed run: let its Store jobs end first, and reject a waiting store-publish
        # (store-publish needs only prepare and store, so it can wait while the failed job ended the rest).
        if [[ "$(run_status)" != completed ]]; then
          echo "==> Run $RUN failed in: $failed_jobs; waiting for its Store jobs to end before rerunning"
          wait_run run reject
        fi
        # A rerun of the failed jobs downloads the earlier jobs' intermediate artifacts (msix-sideload-*, msix-<arch>
        # before the Store variant, signed-<tag>): once one of them has expired, only a full rerun can rebuild it.
        expired="$(gh api "repos/$GH_REPO/actions/runs/$RUN/artifacts?per_page=100" \
          --jq "[.artifacts[] | select(.expired == true and (((.name | startswith(\"msix-\")) and (.name | startswith(\"msix-store-\") | not)) or .name == \"signed-$TAG\"))] | length")" \
          || die "cannot list the artifacts of run $RUN"
        if (( expired > 0 )); then
          echo "==> Run $RUN failed in: $failed_jobs, and its intermediate artifacts have expired: rerunning the whole run once"
          if [[ "$OTHER_REJECTED" == true ]]; then
            echo "    the full rerun queues store-publish again: windows/scripts/release.sh --store $TAG approves it"
          fi
          rerun
        else
          echo "==> Run $RUN failed in: $failed_jobs: rerunning once ($rerun_arg)"
          if [[ "$OTHER_REJECTED" == true && "$rerun_arg" == --job=* ]]; then
            echo "    the Microsoft Store publish of run $RUN was rejected: this run will not submit $TAG to the Store;"
            echo "    windows/scripts/release.sh --store $TAG gives the manual upload steps (its Store bundle stays valid)"
          elif [[ "$OTHER_REJECTED" == true ]]; then
            echo "    the Microsoft Store publish of run $RUN was rejected: windows/scripts/release.sh --store $TAG approves it"
            echo "    if the rerun queues it again, else gives the manual upload steps"
          fi
          rerun "$rerun_arg"
        fi
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

    if [[ "$store_failed" != - ]]; then
      echo "warning: the Store jobs of run $RUN did not succeed ($store_failed): the self-signed release is not affected;" >&2
      echo "         see windows/scripts/release.sh --store $TAG" >&2
    fi
    if [[ "$store_pending" != - ]]; then
      echo "    Store jobs still running or waiting: $store_pending (windows/scripts/release.sh --store $TAG finishes them)"
    fi

    echo "==> Downloading EchoDiar-$TAG into $out"
    # Everything but store/ (the Store bundle of --store, unrelated to this artifact).
    if [[ -d "$out" ]]; then
      find "$out" -mindepth 1 -maxdepth 1 ! -name store -exec rm -rf {} +
    fi
    mkdir -p "$out"
    gh run download "$RUN" --repo "$GH_REPO" -n "EchoDiar-$TAG" -D "$out" || die "gh run download failed"
  fi

  # Checks (before any upload).
  check_artifact "$out" EchoDiar.cer "$publisher" "$pin" "$bundle_uri" "$APPINSTALLER_URI"

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
    echo "==> The release $TAG already has the four Windows assets"
  fi

  # The feed on main (pages.yml serves it at $APPINSTALLER_URI).
  git fetch -q origin main || die "git fetch origin main failed"
  feed_main="$(git show "origin/main:$FEED" 2>/dev/null || true)"
  # Never roll the feed back: attaching an older tag after a newer one leaves main's feed as it is.
  feed_state="$("$PYTHON" -I -c "$FEED_PY" "$VERSION4" <<<"$feed_main")" \
    || die "cannot read the Version of origin/main's $FEED (see above): fix it, then re-run --attach"
  read -r feed_state feed_version <<<"$feed_state"
  if [[ "$feed_state" == same || "$feed_state" == newer ]]; then
    echo "==> origin/main's $FEED already has $feed_version (not older than $VERSION4): not committing the feed"
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
  echo "  docs  : https://ed.dinhanhthi.com/docs.html#install-windows"
  echo "  feed  : $APPINSTALLER_URI"
  echo "  next  : curl -fsSI $APPINSTALLER_URI   (Pages deploys the feed within minutes)"
}

# --- 5. --store: the Microsoft Store step of vX.Y.Z ----------------------------
# The downloaded Store artifact is data, read by an isolated Python (-I), never run. Arguments: the artifact folder,
# the package version, the Store Identity Name, Publisher and PublisherDisplayName (the repo variables). The folder
# holds exactly the file EchoDiar_<version>_x64_arm64.msixupload, a zip holding exactly one unsigned
# EchoDiar_<version>_x64_arm64.msixbundle: the Store identity at the version, x64 and arm64, each inner package
# unsigned, the same identity, the Store PublisherDisplayName and no packageManagement capability (the Store signs
# the bundle).
read -r -d '' STORE_PY <<'PY' || true
import io
import os
import sys
import zipfile
import xml.etree.ElementTree as ET

out, version, name, publisher, display_name = sys.argv[1:6]
upload_name = f"EchoDiar_{version}_x64_arm64.msixupload"
bundle_name = f"EchoDiar_{version}_x64_arm64.msixbundle"

def fail(message):
    sys.exit(f"error: {message}")

def local(tag):
    return tag.rsplit("}", 1)[-1]

def child(element, tag):
    return next((e for e in element if local(e.tag) == tag), None)

def open_zip(source, what):
    try:
        return zipfile.ZipFile(io.BytesIO(source) if isinstance(source, bytes) else source)
    except (zipfile.BadZipFile, OSError) as error:
        fail(f"{what} is not a zip file ({error})")

def read_entry(archive, entry, what):
    try:
        return archive.read(entry)
    except (zipfile.BadZipFile, OSError, KeyError, RuntimeError) as error:
        fail(f"cannot read {entry} in {what} ({error})")

def read_xml(archive, entry, what):
    if entry not in archive.namelist():
        fail(f"{what} has no {entry}")
    try:
        return ET.fromstring(read_entry(archive, entry, what))
    except ET.ParseError as error:
        fail(f"{entry} in {what} is not well-formed: {error}")

# Exactly the upload file, flat (so never a private key).
entries = sorted(os.listdir(out))
upload_path = os.path.join(out, upload_name)
if entries != [upload_name] or not os.path.isfile(upload_path) or os.path.islink(upload_path):
    fail(f"the Store artifact holds {entries}, expected only the file {upload_name}")

# The .msixupload holds exactly the bundle.
with open_zip(upload_path, upload_name) as upload:
    if upload.namelist() != [bundle_name]:
        fail(f"{upload_name} holds {upload.namelist()}, expected only {bundle_name}")
    bundle_data = read_entry(upload, bundle_name, upload_name)

# The bundle: unsigned, the Store identity, x64 and arm64 at the tag's version.
checks = []
with open_zip(bundle_data, bundle_name) as bundle:
    names = set(bundle.namelist())
    if "AppxSignature.p7x" in names:
        fail(f"{bundle_name} is signed (AppxSignature.p7x): the Store bundle must be unsigned")
    manifest = read_xml(bundle, "AppxMetadata/AppxBundleManifest.xml", bundle_name)
    identity = child(manifest, "Identity")
    if identity is None:
        fail("AppxBundleManifest.xml has no Identity")
    packages = [e for e in manifest.iter() if local(e.tag) == "Package" and e.get("Type") == "application"]
    checks += [
        ("bundle Name", identity.get("Name"), name),
        ("bundle Publisher", identity.get("Publisher"), publisher),
        ("bundle Version", identity.get("Version"), version),
        ("bundle packages", sorted((e.get("Architecture"), e.get("Version")) for e in packages),
         [("arm64", version), ("x64", version)]),
    ]
    # Each inner package: unsigned, the same identity, the Store PublisherDisplayName, no packageManagement.
    for package in packages:
        file_name = package.get("FileName") or ""
        if file_name not in names:
            fail(f"{bundle_name} has no {file_name!r}, which its manifest lists")
        with open_zip(read_entry(bundle, file_name, bundle_name), file_name) as inner:
            if "AppxSignature.p7x" in inner.namelist():
                fail(f"{file_name} in {bundle_name} is signed: the Store packages must be unsigned")
            root = read_xml(inner, "AppxManifest.xml", file_name)
        inner_id = child(root, "Identity")
        properties = child(root, "Properties")
        if inner_id is None or properties is None:
            fail(f"AppxManifest.xml of {file_name} has no Identity or Properties")
        shown = child(properties, "PublisherDisplayName")
        capabilities = {e.get("Name") for e in root.iter() if local(e.tag) == "Capability"}
        checks += [
            (f"{file_name} Name", inner_id.get("Name"), name),
            (f"{file_name} Publisher", inner_id.get("Publisher"), publisher),
            (f"{file_name} Version", inner_id.get("Version"), version),
            (f"{file_name} ProcessorArchitecture", inner_id.get("ProcessorArchitecture"), package.get("Architecture")),
            (f"{file_name} PublisherDisplayName", shown.text if shown is not None else None, display_name),
            (f"{file_name} packageManagement capability", "packageManagement" in capabilities, False),
        ]
bad = [f"{what} is {actual!r}, expected {wanted!r}" for what, actual, wanted in checks if actual != wanted]
if bad:
    fail("; ".join(bad))
print(f"    OK: {upload_name} holds only the unsigned {bundle_name}: {name} {version} '{publisher}', "
      f"x64 and arm64, PublisherDisplayName '{display_name}', no packageManagement")
PY

# Checks the Store artifact folder $1 (STORE_PY) against $2 the Identity Name, $3 the Publisher and $4 the
# PublisherDisplayName.
check_store() { "$PYTHON" -I -c "$STORE_PY" "$1" "$VERSION4" "$2" "$3" "$4"; }

store_release() {
  local vars store_name store_publisher store_display store_product out upload_name verdict state rerun_arg failed_jobs store_failed store_pending
  local store_job publish_job live

  # Guards. Nothing but fetches happens before the prompt. No branch or clean tree needed: nothing is committed.
  [[ "$TAG" =~ ^v(.+)$ ]] || die "--store takes the release tag vX.Y.Z, not '$TAG'"
  set_version "${BASH_REMATCH[1]}"
  gh auth status >/dev/null 2>&1 || die "gh is not authenticated - run 'gh auth login'."
  check_origin
  git fetch -q origin main || die "git fetch origin main failed"
  resolve_tag_sha
  git cat-file -e "$SHA^{commit}" 2>/dev/null || git fetch -q origin "refs/tags/$TAG" \
    || die "cannot fetch the commit of $TAG"
  # The approval below lets the tag's workflow see the Store credentials: only main's code may get them.
  git merge-base --is-ancestor "$SHA" origin/main \
    || die "the commit $SHA of $TAG is not on origin/main: --store approves the Store publish of main's code only."
  if ! git cat-file -e "$SHA:$APP_CSPROJ" 2>/dev/null; then
    echo "$TAG has no Windows app (macOS-only release): nothing to submit to the Store"
    exit 0
  fi
  # One listing: a failed call must not pass for variables that are not set.
  vars="$(gh variable list --repo "$GH_REPO" --json name,value)" || die "cannot list the repository variables"
  store_name="$(var_value "$vars" WINDOWS_STORE_IDENTITY_NAME)" || die "cannot read the repository variables"
  store_publisher="$(var_value "$vars" WINDOWS_STORE_PUBLISHER)" || die "cannot read the repository variables"
  store_display="$(var_value "$vars" WINDOWS_STORE_PUBLISHER_DISPLAY_NAME)" || die "cannot read the repository variables"
  store_product="$(var_value "$vars" WINDOWS_STORE_PRODUCT_ID)" || die "cannot read the repository variables"
  # The workflow builds the Store bundle only with all four set (prepare).
  if [[ -z "$store_name" || -z "$store_publisher" || -z "$store_display" || -z "$store_product" ]]; then
    echo "No Store bundle for $TAG: set WINDOWS_STORE_* variables ($STORE_DOC)"
    echo "Store not set up yet"
    exit 0
  fi

  out="$REPO_ROOT/release/windows/$TAG/store"
  upload_name="EchoDiar_${VERSION4}_x64_arm64.msixupload"
  echo "About to finish the Microsoft Store step of $TAG:"
  echo "  commit   : $SHA (on main)"
  echo "  identity : $store_name $VERSION4, publisher '$store_publisher' ('$store_display')"
  echo "  output   : $out/$upload_name"
  echo "  then     : approve this tag's store-publish job ($STORE_ENV) if it waits, check the Store bundle"
  echo
  confirm "Continue?" || { echo "aborted."; exit 1; }

  # The run: --attach found or started it; this mode never dispatches or reruns.
  echo "==> Looking for the $WORKFLOW run of $TAG (${SHA:0:7})"
  RUN=""
  find_run || die "no $WORKFLOW run for $TAG yet: windows/scripts/release.sh --attach $TAG --yes starts one"
  RUN_URL="https://github.com/$GH_REPO/actions/runs/$RUN"
  echo "    run $RUN: $RUN_URL"
  APPROVE_ENV="$STORE_ENV"
  OTHER_ENV="$RELEASE_ENV"
  OTHER_HINT="approve or reject it at $RUN_URL (windows/scripts/release.sh --attach $TAG --yes approves it), then re-run --store"
  wait_run run
  verdict="$(run_jobs completed)" || die "cannot list the jobs of run $RUN: $RUN_URL"
  { read -r state; read -r rerun_arg; read -r failed_jobs; read -r store_failed; read -r store_pending
    read -r store_job; read -r publish_job; } <<<"$verdict"

  # Download (a folder that already passes the check, from an earlier --store, is reused as it is).
  if [[ -d "$out" ]] && check_store "$out" "$store_name" "$store_publisher" "$store_display" >/dev/null 2>&1; then
    echo "==> $out already holds a checked $upload_name: not downloading again (delete it to download again)"
  else
    case "$store_job" in
      success) ;;
      absent|skipped)
        if [[ "$state" == ok ]]; then
          echo "Run $RUN built no Store bundle (it predates the Store variables): nothing to submit for $TAG"
          echo "no Store bundle for this tag"
          exit 0
        fi
        die "run $RUN built no Store bundle: its build failed ($failed_jobs). Finish --attach first (windows/scripts/release.sh --attach $TAG --yes), then re-run --store" ;;
      *)
        die "the Store build of run $RUN ended '$store_job' ($store_failed): gh run view $RUN --repo $GH_REPO --log-failed; rerun it (gh run rerun $RUN --repo $GH_REPO --failed), then re-run --store" ;;
    esac
    live="$(gh api "repos/$GH_REPO/actions/runs/$RUN/artifacts?name=EchoDiar-store-$TAG" \
      --jq '[.artifacts[] | select(.expired == false)] | length')" || die "cannot list the artifacts of run $RUN"
    (( live > 0 )) || die "the artifact EchoDiar-store-$TAG of run $RUN has expired (kept 14 days): re-run the workflow for $TAG (gh run rerun $RUN --repo $GH_REPO; its $RELEASE_ENV deployment then waits: approve or reject it at $RUN_URL), then re-run --store"
    echo "==> Downloading EchoDiar-store-$TAG into $out"
    rm -rf "$out"
    mkdir -p "$out"
    gh run download "$RUN" --repo "$GH_REPO" -n "EchoDiar-store-$TAG" -D "$out" || die "gh run download failed"
  fi
  check_store "$out" "$store_name" "$store_publisher" "$store_display" || die "the Store bundle check failed (see above)"

  echo
  if [[ "$publish_job" == success ]]; then
    echo "Submitted to the Microsoft Store by CI (certification can take up to 3 business days)."
    echo "  run    : $RUN_URL"
    echo "  bundle : $out/$upload_name"
    return 0
  fi
  case "$publish_job" in
    absent|skipped)
      echo "CI does not submit $TAG to the Store (WINDOWS_STORE_PUBLISH is not 'true'): upload it in Partner Center." ;;
    *)
      # A rejected deployment fails the job: --attach rejects a waiting one to rerun the self-signed jobs.
      if [[ "$(gh api "repos/$GH_REPO/actions/runs/$RUN/approvals" \
          --jq "any(.[]; .state == \"rejected\" and any(.environments[]?; .name == \"$STORE_ENV\"))" \
          2>/dev/null || true)" == true ]]; then
        echo "warning: the $STORE_ENV deployment of run $RUN was rejected (release.sh --attach rejects it to rerun the self-signed jobs): CI did not submit $TAG" >&2
      else
        echo "warning: the store-publish job of run $RUN ended '$publish_job' (a submission still in certification blocks the next): $RUN_URL" >&2
      fi
      echo "Upload it in Partner Center instead." ;;
  esac
  echo "  1. https://partner.microsoft.com/dashboard > Apps and games > EchoDiar > Start update"
  echo "     (or open the pending submission)"
  echo "  2. Packages > upload $out/$upload_name"
  echo "  3. Save, then Submit to the Store (certification can take up to 3 business days)"
  echo "manual Store upload pending"
}

case "$MODE" in
  dev) dev_release ;;
  preflight) preflight ;;
  attach) attach ;;
  store) store_release ;;
esac
