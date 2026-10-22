#!/usr/bin/env bash
#
# qa-fixtures.sh — Make the Windows QA fixtures on this Mac.
#
# Writes ${ECHODIAR_QA_DIR:-~/Downloads/EchoDiarQA} (the VM sees it as Z:\Downloads\EchoDiarQA):
#   sample.wav     one voice, "This is a sample recording for EchoDiar..." (16 kHz mono LEI16)
#   two-voice.wav  two voices alternating, at least 20 s (16 kHz mono LEI16)
#   models/        ggml-tiny.bin, model.onnx (pyannote segmentation 3.0) and the campplus
#                  speaker embedding onnx, pinned to Hugging Face commits and SHA-256 checked.
# Same text and voices as scripts/vm-test.sh --fixtures. Safe to run again: models with the
# right hash are skipped, the audio is rewritten.
#
set -euo pipefail

OUT="${ECHODIAR_QA_DIR:-$HOME/Downloads/EchoDiarQA}"
MODELS="$OUT/models"

die() { echo "error: $*" >&2; exit 1; }

for tool in say afconvert afinfo curl shasum /usr/bin/python3; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool not found"
done

mkdir -p "$OUT" "$MODELS"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

to_wav() { afconvert -f WAVE -d LEI16@16000 -c 1 "$1" "$2"; }

# 1. sample.wav
echo "==> Writing $OUT/sample.wav"
say -o "$TMP/sample.aiff" "This is a sample recording for EchoDiar. One, two, three, four, five."
to_wav "$TMP/sample.aiff" "$TMP/sample.wav"
mv -f "$TMP/sample.wav" "$OUT/sample.wav"

# 2. two-voice.wav: two speakers of different gender, alternating turns
echo "==> Writing $OUT/two-voice.wav"
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
  say -v "$voice" -o "$TMP/$i.aiff" "$text"
  to_wav "$TMP/$i.aiff" "$TMP/$i.wav"
  PARTS+=("$TMP/$i.wav")
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
    w.close()
out.close()
' "$TMP/two-voice.wav" "${PARTS[@]}"
DURATION="$(afinfo "$TMP/two-voice.wav" | awk '/estimated duration/{print int($3)}')"
((DURATION >= 20)) || die "two-voice.wav is ${DURATION}s, needs at least 20s"
mv -f "$TMP/two-voice.wav" "$OUT/two-voice.wav"
echo "    ${DURATION}s"

# 3. Models: repo|commit|path|sha256 (sha256 = the HF tree API lfs.oid at that commit)
MODEL_LIST=(
  "ggerganov/whisper.cpp|5359861c739e955e79d9a303bcbc70fb988958b1|ggml-tiny.bin|be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"
  "csukuangfj/sherpa-onnx-pyannote-segmentation-3-0|9403a6902bb58e3d5ae8c7e77c3422de279db2e0|model.onnx|220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079"
  "csukuangfj/speaker-embedding-models|0743f301363dec56491a490f6d6cbc9d67f9a3bf|3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx|aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2"
)
sha256() { shasum -a 256 "$1" | awk '{print $1}'; }

for entry in "${MODEL_LIST[@]}"; do
  IFS='|' read -r repo rev path hash <<< "$entry"
  dest="$MODELS/$path"
  if [[ -f "$dest" ]]; then
    if [[ "$(sha256 "$dest")" == "$hash" ]]; then
      echo "==> Skipping $path (hash OK)"
      continue
    fi
    echo "==> $path has a wrong hash, downloading again"
  fi
  echo "==> Downloading $path from $repo@${rev:0:8}"
  curl -fL --retry 3 -o "$TMP/$path" "https://huggingface.co/$repo/resolve/$rev/$path"
  got="$(sha256 "$TMP/$path")"
  [[ "$got" == "$hash" ]] || die "$path: SHA-256 $got, expected $hash"
  mv -f "$TMP/$path" "$dest"
done

echo "==> Fixtures ready in $OUT"
