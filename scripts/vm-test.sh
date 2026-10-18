#!/usr/bin/env bash
#
# vm-test.sh — Build EchoDiar on this Mac and install + launch it in the VMPal test VM.
# Nothing is built or installed inside the VM: it only gets the zipped app (and fixtures).
#
# Usage:
#   scripts/vm-test.sh [--fixtures] [--reset-tcc] [--args "<app args>"]
#     --fixtures    also make sample.m4a, two-voice.m4a, two-voice-stereo.m4a and copy them to ~/EchoDiarQA
#     --reset-tcc   reset the app's Microphone / AudioCapture grants in the VM before launch
#     --args        arguments passed to the app (split on spaces)
#
set -euo pipefail

VM_ID="437EAE75-81A8-4086-BC84-B0A0489788C6"
BUNDLE_ID="dinh.thi.EchoDiar"
VMPAL_FALLBACK="/Applications/VMPal.app/Contents/Helpers/VMPalMachine.app/Contents/MacOS/VMPalMachine"

FIXTURES=0
RESET_TCC=0
APP_ARGS=()
while (($#)); do
  case "$1" in
    --fixtures) FIXTURES=1 ;;
    --reset-tcc) RESET_TCC=1 ;;
    --args)
      [[ $# -ge 2 ]] || { echo "error: --args needs a value" >&2; exit 2; }
      read -r -a APP_ARGS <<< "$2"
      shift ;;
    *) echo "error: unknown option $1" >&2; exit 2 ;;
  esac
  shift
done

die() { echo "error: $*" >&2; exit 1; }

# 1. VMPal CLI
if command -v vmpal >/dev/null 2>&1; then
  VMPAL=(vmpal)
elif [[ -x "$VMPAL_FALLBACK" ]]; then
  VMPAL=("$VMPAL_FALLBACK" --cli)
else
  die "VMPal CLI not found: no vmpal on PATH and no $VMPAL_FALLBACK"
fi

# 1b. VM state (from info --json; the exit code of status is not used)
echo "==> Checking VM ${VM_ID}..."
INFO="$("${VMPAL[@]}" info "$VM_ID" --json)" || die "VM $VM_ID not found in VMPal"
[[ "$(jq -r '.agentControl' <<< "$INFO")" == "true" ]] \
  || die "agent control is off for VM $VM_ID: turn it on in VMPal (VM Settings > AI Agents)"
if [[ "$(jq -r '.state' <<< "$INFO")" != "running" ]]; then
  echo "==> Starting VM..."
  "${VMPAL[@]}" start "$VM_ID" --wait --background
fi

# 2. Build on this Mac (default DerivedData)
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
echo "==> Building (Debug)..."
xcodegen generate
xcodebuild build -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug -quiet
PRODUCTS="$(xcodebuild -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug \
  -showBuildSettings 2>/dev/null \
  | awk -F' = ' '!found && /^ *BUILT_PRODUCTS_DIR = /{print $2; found=1}')"
APP="$PRODUCTS/EchoDiar.app"
[[ -d "$APP" ]] || die "built app not found at $APP"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# 3. Zip and copy into ~/EchoDiarQA
echo "==> Copying EchoDiar.app to the VM..."
ditto -c -k --keepParent "$APP" "$TMP/EchoDiar.zip"
"${VMPAL[@]}" exec "$VM_ID" -- mkdir -p EchoDiarQA
"${VMPAL[@]}" cp "$TMP/EchoDiar.zip" "$VM_ID:EchoDiarQA/"

# 5. Fixtures, made on this Mac
if ((FIXTURES)); then
  echo "==> Making fixtures..."
  FX="$TMP/fixtures"
  mkdir -p "$FX/parts"
  say -o "$FX/parts/sample.aiff" "This is a sample recording for EchoDiar. One, two, three, four, five."
  afconvert -f m4af -d aac "$FX/parts/sample.aiff" "$FX/sample.m4a"

  # Two speakers of different gender, alternating turns.
  TURNS=(
    "Samantha|Good morning. Thanks for joining the weekly planning meeting."
    "Daniel|Morning. I have the numbers from last week ready to share."
    "Samantha|Great. Let's start with the release schedule and then talk about hiring."
    "Daniel|The release is on track. We fixed the last two crashes on Thursday."
    "Samantha|Good news. What about the new recording feature, is it ready for testing?"
    "Daniel|Almost. We still need to check it on older machines before Friday."
    "Samantha|Okay. Please send me the test plan by tomorrow afternoon."
    "Daniel|Will do. I will also add the notes from the customer calls."
  )
  PARTS=()
  for i in "${!TURNS[@]}"; do
    voice="${TURNS[$i]%%|*}"
    text="${TURNS[$i]#*|}"
    say -v "$voice" --data-format=LEI16@16000 -o "$FX/parts/$i.wav" "$text"
    PARTS+=("$FX/parts/$i.wav")
  done
  /usr/bin/python3 -I -c '
import sys, wave
out = wave.open(sys.argv[1], "wb")
for i, path in enumerate(sys.argv[2:]):
    w = wave.open(path, "rb")
    if i == 0:
        out.setparams(w.getparams())
    else:
        out.writeframes(b"\0\0" * (w.getframerate() * 4 // 10))  # 0.4 s between turns
    out.writeframes(w.readframes(w.getnframes()))
out.close()
' "$FX/parts/two-voice.wav" "${PARTS[@]}"
  afconvert -f m4af -d aac "$FX/parts/two-voice.wav" "$FX/two-voice.m4a"
  afconvert -f m4af -d aac -m 0 0 "$FX/parts/two-voice.wav" "$FX/two-voice-stereo.m4a"
  DURATION="$(afinfo "$FX/two-voice.m4a" | awk '/estimated duration/{print int($3)}')"
  ((DURATION >= 20)) || die "two-voice.m4a is ${DURATION}s, needs at least 20s"
  rm -rf "$FX/parts"
  "${VMPAL[@]}" cp "$FX/." "$VM_ID:EchoDiarQA"
fi

# 4. Unzip, optionally reset TCC, launch (open returns at once)
echo "==> Installing and launching in the VM..."
REMOTE='pkill -x EchoDiar; i=0; while pgrep -x EchoDiar >/dev/null && [ $i -lt 50 ]; do sleep 0.2; i=$((i + 1)); done
cd ~/EchoDiarQA && rm -rf EchoDiar.app && ditto -x -k EchoDiar.zip . && xattr -cr EchoDiar.app || exit 1'
if ((RESET_TCC)); then
  REMOTE+="
tccutil reset Microphone $BUNDLE_ID && tccutil reset AudioCapture $BUNDLE_ID || tccutil reset All $BUNDLE_ID"
fi
REMOTE+='
if [ $# -gt 0 ]; then open ~/EchoDiarQA/EchoDiar.app --args "$@"; else open ~/EchoDiarQA/EchoDiar.app; fi'
"${VMPAL[@]}" exec "$VM_ID" -- /bin/sh -c "$REMOTE" sh ${APP_ARGS[@]+"${APP_ARGS[@]}"}

echo "==> EchoDiar launched in VM $VM_ID"
