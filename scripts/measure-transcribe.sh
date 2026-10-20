#!/usr/bin/env bash
#
# measure-transcribe.sh - Measure Stop -> transcript latency, footprint, CPU and WER with the DEBUG --measure harness,
# on this Mac (host) or in the VMPal test VM (vm). Nothing is built or installed inside the VM: it only gets the
# zipped Debug app, the fixture and (with --install-models) zipped models from this Mac.
#
# Usage:
#   scripts/measure-transcribe.sh fixture [--minutes 15]
#       Speak scripts/fixtures/measure-meeting.txt with `say` (mic = Samantha on ch0, Daniel and Karen alternating on
#       ch1, quiet turns at -20 dB, -65 dBFS seeded noise on both), loop it and cut it to exactly N minutes:
#       ~/EchoDiarQA/measure/fixture-<N>m.caf (2 ch, 16 kHz, Int16) + reference-<N>m.json (text per channel,
#       turn timeline, sha256 of the caf). A turn that does not fit is dropped from the audio and the reference.
#   scripts/measure-transcribe.sh host [--label L] [--minutes 15] [--runs 1] [-- <harness args>]
#       Build Debug, refuse while EchoDiar runs, then run the built binary R times, one after the other:
#       EchoDiar --background --measure <fixture> --out ~/EchoDiarQA/measure/results/<L>-host-<n> --tag <sha>[-dirty]
#   scripts/measure-transcribe.sh vm [--label L] [--minutes 15] [--runs 1] [--install-models] [-- <harness args>]
#       Build Debug here, copy the app into the VM's ~/EchoDiarQA/measure-<L>/ and the fixture into
#       ~/EchoDiarQA/measure/, launch the harness there R times one after the other (poll every 30 s, 3 h timeout)
#       and copy each result folder back to ~/EchoDiarQA/measure/results/<L>-vm-<n>. --install-models copies
#       whisperkit-large-v3-turbo-626 and pyannote-community-1 from this Mac's models folder into the VM's (once:
#       a model the VM already has is skipped).
#   scripts/measure-transcribe.sh report <result dir>...
#       One Markdown table row per run: Stop -> transcript, Stop -> compressed, step durations, max footprint, CPU
#       after Stop, and word error rate (lowercase, punctuation stripped, word-level Levenshtein) for mic (ch0),
#       system (ch1) and overall (both channels' errors over both channels' reference words).
#
#   Defaults: --label run, --minutes 15, --runs 1. <n> is the first free number for <L>-host / <L>-vm.
#   Everything after -- goes to the harness (e.g. --engine <catalog id>, --diarizer <catalog id>).
#
set -euo pipefail

VM_ID="437EAE75-81A8-4086-BC84-B0A0489788C6"
VMPAL_FALLBACK="/Applications/VMPal.app/Contents/Helpers/VMPalMachine.app/Contents/MacOS/VMPalMachine"
VM_MODELS=(whisperkit-large-v3-turbo-626 pyannote-community-1)
HOST_MODELS="$HOME/Library/Application Support/EchoDiar/Models"
MEASURE_DIR="$HOME/EchoDiarQA/measure"
RESULTS="$MEASURE_DIR/results"
POLL_SECONDS=30
TIMEOUT_SECONDS=$((3 * 3600))

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEXT="$REPO_ROOT/scripts/fixtures/measure-meeting.txt"
cd "$REPO_ROOT"

die() { echo "error: $*" >&2; exit 1; }
usage() { awk 'NR > 2 && /^#/ { sub(/^# ?/, ""); print; next } NR > 2 { exit }' "${BASH_SOURCE[0]}"; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ---------------------------------------------------------------------------------------------------------------
# Python (run with /usr/bin/python3 -I: system Python, no site packages, nothing read from the current directory)

# fixture_py <text> <minutes> <caf> <reference> <workdir>
fixture_py() {
  /usr/bin/python3 -I - "$@" <<'PY'
import array, hashlib, itertools, json, os, random, re, subprocess, sys, wave

text_path, minutes, caf, ref_path, work = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4], sys.argv[5]
RATE = 16000
VOICES = {"mic": "Samantha", "sys1": "Daniel", "sys2": "Karen"}
CHANNEL = {"mic": 0, "sys1": 1, "sys2": 1}
QUIET_DB, NOISE_DBFS, SEED, MIN_MONOLOGUE = -20, -65, 20261020, 45
total = minutes * 60 * RATE


def die(message):
    sys.exit("error: " + message)


if sys.byteorder != "little":
    die("expected a little-endian Mac")

# Parse the script.
items = []
with open(text_path, encoding="utf-8") as f:
    for number, raw in enumerate(f, 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        where = f"{os.path.basename(text_path)}:{number}"
        head, sep, body = line.partition("|")
        body = body.strip()
        if not sep or not body:
            die(f"{where}: expected <speaker>[:flags]|<text> or pause|<seconds>")
        if head == "pause":
            try:
                seconds = float(body)
            except ValueError:
                seconds = 0
            if seconds <= 0:
                die(f"{where}: pause needs a positive number of seconds")
            items.append(("pause", round(seconds * RATE)))
            continue
        speaker, _, flags = head.partition(":")
        if speaker not in VOICES:
            die(f"{where}: unknown speaker {speaker!r} (mic, sys1, sys2)")
        quiet, overlap = False, 0
        for flag in filter(None, flags.split(",")):
            if flag == "quiet":
                quiet = True
            elif flag.startswith("overlap="):
                try:
                    overlap = round(float(flag[len("overlap="):]) * RATE)
                except ValueError:
                    overlap = 0
                if overlap <= 0:
                    die(f"{where}: overlap needs a positive number of seconds")
            else:
                die(f"{where}: unknown flag {flag!r} (quiet, overlap=<s>)")
        if re.search(r"\d", body):
            die(f"{where}: no digits in turns (they break WER normalization)")
        items.append(("turn", {"speaker": speaker, "text": body, "quiet": quiet, "overlap": overlap, "where": where}))
if not any(kind == "turn" for kind, _ in items):
    die(f"{text_path} has no turns")

# Voices.
listing = subprocess.run(["say", "-v", "?"], capture_output=True, text=True, check=True).stdout
for voice in sorted(set(VOICES.values())):
    if not re.search(r"^" + re.escape(voice) + r"\b", listing, re.M):
        die(f"voice {voice} is not installed (System Settings > Accessibility > Spoken Content > System voice)")

# Speak every distinct turn once.
speech = {}
for kind, item in items:
    key = (item["speaker"], item["text"]) if kind == "turn" else None
    if key is None or key in speech:
        continue
    path = os.path.join(work, f"turn-{len(speech)}.wav")
    subprocess.run(["say", "-v", VOICES[item["speaker"]], "--data-format=LEI16@16000", "-o", path, item["text"]],
                   check=True)
    with wave.open(path, "rb") as w:
        if (w.getnchannels(), w.getsampwidth(), w.getframerate()) != (1, 2, RATE):
            die(f"say wrote {w.getnchannels()} ch {8 * w.getsampwidth()} bit {w.getframerate()} Hz, not mono 16 bit 16 kHz")
        samples = array.array("h", w.readframes(w.getnframes()))
    if item["quiet"]:
        gain = 10 ** (QUIET_DB / 20)
        samples = array.array("h", (int(round(s * gain)) for s in samples))
    speech[key] = samples
longest = max(len(s) for s in speech.values()) / RATE
if longest < MIN_MONOLOGUE:
    die(f"the longest turn is {longest:.1f} s; the script needs a monologue of at least {MIN_MONOLOGUE} s")

# Lay the turns on a timeline, looping the script, until one does not fit.
rng = random.Random(SEED)
tracks = [array.array("h", bytes(2 * total)) for _ in range(2)]
track_end = [0, 0]
cursor = 0          # end of the latest turn (frames)
previous_end = None
pending = None      # a pause replaces the next turn gap
turns, cut, loop = [], None, 0
while cut is None:
    for kind, item in items:
        if kind == "pause":
            pending = (pending or 0) + item
            continue
        samples = speech[(item["speaker"], item["text"])]
        if item["overlap"]:
            if previous_end is None:
                die(f"{item['where']}: the first turn cannot overlap")
            start = previous_end - item["overlap"]
        else:
            start = cursor + (pending if pending is not None else round(rng.uniform(0.4, 1.2) * RATE))
        pending = None
        channel = CHANNEL[item["speaker"]]
        if start < 0:
            die(f"{item['where']}: the overlap starts the turn before the start of the audio")
        if start < track_end[channel]:
            die(f"{item['where']}: overlaps a turn on the same channel (crosstalk must be across channels)")
        end = start + len(samples)
        if end > total:
            cut = {"speaker": item["speaker"], "text": item["text"], "start": round(start / RATE, 3)}
            break
        tracks[channel][start:end] = samples
        track_end[channel] = end
        cursor = max(cursor, end)
        previous_end = end
        turns.append({
            "start": round(start / RATE, 3), "end": round(end / RATE, 3),
            "channel": "mic" if channel == 0 else "system", "speaker": item["speaker"], "voice": VOICES[item["speaker"]],
            "quiet": item["quiet"], "overlap": round(item["overlap"] / RATE, 3), "loop": loop, "text": item["text"],
        })
    loop += 1

# Seeded noise on both channels (a tiled ~8 s block: deterministic and fast).
sigma = 32768 * 10 ** (NOISE_DBFS / 20)
for channel in range(2):
    noise_rng = random.Random(SEED + 1 + channel)
    block = [int(round(noise_rng.gauss(0, sigma))) for _ in range(131071)]
    tracks[channel] = array.array(
        "h", (max(-32768, min(32767, s + n)) for s, n in zip(tracks[channel], itertools.cycle(block))))

stereo = array.array("h", bytes(4 * total))
stereo[0::2] = tracks[0]
stereo[1::2] = tracks[1]
wav = os.path.join(work, "fixture.wav")
with wave.open(wav, "wb") as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(RATE)
    w.writeframes(stereo.tobytes())
partial = caf + ".partial"
subprocess.run(["afconvert", "-f", "caff", "-d", "LEI16@16000", "-c", "2", wav, partial], check=True)
os.replace(partial, caf)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


reference = {
    "version": 1,
    "fixture": os.path.basename(caf),
    "sha256": sha256(caf),
    "minutes": minutes,
    "durationSeconds": total / RATE,
    "sampleRate": RATE,
    "script": os.path.basename(text_path),
    "scriptSha256": sha256(text_path),
    "voices": VOICES,
    "quietGainDb": QUIET_DB,
    "noiseDbfs": NOISE_DBFS,
    "seed": SEED,
    "channels": {name: " ".join(t["text"] for t in turns if t["channel"] == name) for name in ("mic", "system")},
    "dropped": cut,
    "turns": turns,
}
with open(ref_path + ".partial", "w", encoding="utf-8") as f:
    json.dump(reference, f, indent=2)
    f.write("\n")
os.replace(ref_path + ".partial", ref_path)

speech_seconds = {name: sum(t["end"] - t["start"] for t in turns if t["channel"] == name) for name in ("mic", "system")}
print(f"turns: {len(turns)} ({sum(t['channel'] == 'mic' for t in turns)} mic, "
      f"{sum(t['channel'] == 'system' for t in turns)} system), {sum(t['quiet'] for t in turns)} quiet, "
      f"{sum(t['overlap'] > 0 for t in turns)} crosstalk, longest {max(t['end'] - t['start'] for t in turns):.1f} s")
print(f"speech: mic {speech_seconds['mic']:.1f} s, system {speech_seconds['system']:.1f} s; "
      f"words: mic {len(reference['channels']['mic'].split())}, system {len(reference['channels']['system'].split())}")
if cut:
    print(f"dropped at {cut['start']} s (does not fit): {cut['speaker']}: {cut['text'][:60]}")
print(f"sha256: {reference['sha256']}")
PY
}

# report_py <measure dir> <result dir>...
report_py() {
  /usr/bin/python3 -I - "$@" <<'PY'
import json, os, re, sys

measure_dir, dirs = sys.argv[1], sys.argv[2:]


def words(text):
    text = text.lower().replace("’", "").replace("'", "")
    return re.sub(r"[^a-z0-9]+", " ", text).split()


def distance(ref, hyp):
    previous = list(range(len(hyp) + 1))
    for i, r in enumerate(ref, 1):
        current = [i] + [0] * len(hyp)
        for j, h in enumerate(hyp, 1):
            current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (r != h))
        previous = current
    return previous[-1]


def load(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def seconds(value):
    return "n/a" if value is None else f"{value:.1f}"


def percent(edits, total):
    return "n/a" if not total else f"{100 * edits / total:.1f}"


def step_durations(steps, stop_to_transcript):
    steps = sorted(steps, key=lambda s: s["t"])
    totals = {}
    for i, step in enumerate(steps):
        if step["step"] in ("finished", "failed", "needsModel"):
            continue
        end = steps[i + 1]["t"] if i + 1 < len(steps) else stop_to_transcript
        if end is not None:
            totals[step["step"]] = totals.get(step["step"], 0) + end - step["t"]
    return ", ".join(f"{name} {value:.1f}" for name, value in totals.items()) or "n/a"


print("| label | where | # | tag | engine | diarizer | Stop→transcript s | Stop→compressed s | load+1st chunk s "
      "| steps s | max MB | CPU after Stop s | WER mic % | WER system % | WER overall % | notes |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for folder in dirs:
    name = os.path.basename(os.path.normpath(folder))
    match = re.fullmatch(r"(.+)-(host|vm)-(\d+)", name)
    label, where, number = match.groups() if match else (name, "?", "")
    notes = []
    result = load(os.path.join(folder, "result.json"))
    if result is None:
        print(f"| {label} | {where} | {number} | | | | | | | | | | | | | no result.json |")
        continue
    if result.get("error"):
        notes.append(f"error: {result['error']}")
    if result.get("otherInstances"):
        notes.append(f"{result['otherInstances']} other EchoDiar running")
    failed = [k for k, v in (result.get("postConditions") or {}).items() if not v]
    if failed:
        notes.append("post-conditions failed: " + ", ".join(sorted(failed)))
    for moment in ("start", "end"):
        conditions = result.get(moment) or {}
        if conditions.get("thermalState") not in (None, "nominal"):
            notes.append(f"thermal {conditions['thermalState']} at {moment}")
        if conditions.get("lowPowerMode"):
            notes.append(f"Low Power Mode at {moment}")
    options = result.get("options") or {}
    for key, default in (("diarization", "on"), ("incremental", "off"), ("pace", 1), ("language", "en")):
        if options.get(key, default) != default:
            notes.append(f"{key} {options[key]}")

    # WER against the reference made with the fixture (found by the fixture's file name: VM runs record a VM path).
    wer = ["n/a"] * 3
    fixture = os.path.basename(options.get("fixture") or "")
    reference = load(os.path.join(measure_dir, re.sub(r"^fixture-(\d+m)\.caf$", r"reference-\1.json", fixture))) \
        if re.fullmatch(r"fixture-\d+m\.caf", fixture) else None
    transcript = load(os.path.join(folder, "transcript.json"))
    if reference is None:
        notes.append(f"no reference for {fixture or 'the fixture'}")
    elif transcript is None:
        notes.append("no transcript.json")
    else:
        duration = (result.get("fixture") or {}).get("durationSeconds")
        if duration is not None and abs(duration - reference["durationSeconds"]) > 0.01:
            notes.append(f"fixture is {duration:.1f} s, reference {reference['durationSeconds']:.1f} s")
        segments = sorted(transcript.get("segments") or [], key=lambda s: s.get("start", 0))
        edits, totals = [], []
        for channel in ("mic", "system"):
            ref = words(reference["channels"][channel])
            hyp = words(" ".join(s.get("text", "") for s in segments if s.get("channel") == channel))
            edits.append(distance(ref, hyp))
            totals.append(len(ref))
        wer = [percent(edits[0], totals[0]), percent(edits[1], totals[1]), percent(sum(edits), sum(totals))]

    cpu = sum(p.get("cpuSeconds", 0) for p in result.get("phases") or [] if p.get("phase") != "recording")
    first = result.get("loadAndFirstChunk") or {}
    cells = [
        label, where, number, result.get("tag") or "", result.get("engine") or "",
        result.get("diarizer") or ("off" if options.get("diarization") == "off" else ""),
        seconds(result.get("stopToTranscript")), seconds(result.get("stopToCompressed")), seconds(first.get("t")),
        step_durations(result.get("steps") or [], result.get("stopToTranscript")),
        seconds(result.get("maxFootprintMB")), seconds(cpu), *wer, "; ".join(notes).replace("|", "/"),
    ]
    print("| " + " | ".join(str(c) for c in cells) + " |")
PY
}

# ---------------------------------------------------------------------------------------------------------------
# Helpers

LABEL="run"
MINUTES=15
RUNS=1
INSTALL_MODELS=0
HARNESS_ARGS=()

# parse_args <subcommand> <args...>: --label, --minutes, --runs, --install-models (vm), -- <harness args>
parse_args() {
  local mode="$1"
  shift
  while (($#)); do
    case "$1" in
      --minutes|--label|--runs)
        [[ $# -ge 2 ]] || die "$1 needs a value"
        [[ "$mode" != fixture || "$1" == --minutes ]] || die "unknown option $1 for fixture"
        case "$1" in
          --minutes) MINUTES="$2" ;;
          --label) LABEL="$2" ;;
          --runs) RUNS="$2" ;;
        esac
        shift ;;
      --install-models)
        [[ "$mode" == vm ]] || die "--install-models is for vm only"
        INSTALL_MODELS=1 ;;
      --)
        [[ "$mode" != fixture ]] || die "fixture takes no harness arguments"
        shift
        HARNESS_ARGS=("$@")
        break ;;
      *) die "unknown option $1 (see $0 --help)" ;;
    esac
    shift
  done
  [[ "$MINUTES" =~ ^[1-9][0-9]*$ ]] || die "--minutes must be a positive whole number"
  [[ "$RUNS" =~ ^[1-9][0-9]*$ ]] || die "--runs must be a positive whole number"
  [[ "$LABEL" =~ ^[A-Za-z0-9._-]+$ ]] || die "--label may only use letters, digits, . _ -"
  FIXTURE="$MEASURE_DIR/fixture-${MINUTES}m.caf"
  REFERENCE="$MEASURE_DIR/reference-${MINUTES}m.json"
}

require_fixture() {
  [[ -f "$FIXTURE" && -f "$REFERENCE" ]] \
    || die "no $FIXTURE or $REFERENCE: run $0 fixture --minutes $MINUTES first"
}

# Same commands as scripts/vm-test.sh (default DerivedData). Sets APP.
build_debug() {
  echo "==> Building (Debug)..."
  xcodegen generate
  xcodebuild build -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug -quiet
  local products
  products="$(xcodebuild -project EchoDiar.xcodeproj -scheme EchoDiar -configuration Debug \
    -showBuildSettings 2>/dev/null \
    | awk -F' = ' '!found && /^ *BUILT_PRODUCTS_DIR = /{print $2; found=1}')"
  APP="$products/EchoDiar.app"
  [[ -d "$APP" ]] || die "built app not found at $APP"
}

# HEAD's short sha, -dirty when the working tree has changes (untracked files included).
git_tag() {
  local sha
  sha="$(git rev-parse --short HEAD)"
  [[ -z "$(git status --porcelain)" ]] || sha+="-dirty"
  echo "$sha"
}

# next_result_dir <where>: the first free $RESULTS/<label>-<where>-<n>, created.
next_result_dir() {
  local n=1
  while [[ -e "$RESULTS/$LABEL-$1-$n" ]]; do n=$((n + 1)); done
  mkdir -p "$RESULTS/$LABEL-$1-$n"
  echo "$RESULTS/$LABEL-$1-$n"
}

refuse_running_host() {
  local pids
  if pids="$(pgrep -x EchoDiar)"; then
    die "EchoDiar is running (pid $(echo "$pids" | tr '\n' ' ')): quit it first, it would skew the measurement"
  fi
}

# run_failed <dir> <what>: the harness's error from result.json, then exit 1.
run_failed() {
  local error=""
  [[ -f "$1/result.json" ]] && error="$(jq -r '.error // empty' "$1/result.json")"
  [[ -z "$error" ]] || echo "harness: $error" >&2
  die "run failed ($2), see $1"
}

vm_exec() { "${VMPAL[@]}" exec "$VM_ID" -- "$@"; }
# vm_sh '<script>' <args...>: a /bin/sh script in the VM, from the account's home, with $1... = args.
vm_sh() {
  local script="$1"
  shift
  vm_exec /bin/sh -c "$script" sh "$@"
}

# ---------------------------------------------------------------------------------------------------------------
# Subcommands

cmd_fixture() {
  parse_args fixture "$@"
  [[ -f "$TEXT" ]] || die "missing $TEXT"
  mkdir -p "$MEASURE_DIR" "$TMP/fixture"
  echo "==> Speaking $TEXT for $MINUTES min..."
  fixture_py "$TEXT" "$MINUTES" "$FIXTURE" "$REFERENCE" "$TMP/fixture"
  afinfo "$FIXTURE" | grep -E 'Data format|estimated duration' || true
  echo "==> $FIXTURE"
  echo "==> $REFERENCE"
}

cmd_host() {
  parse_args host "$@"
  require_fixture
  refuse_running_host
  build_debug
  local bin="$APP/Contents/MacOS/EchoDiar" tag out status i
  local dirs=()
  tag="$(git_tag)"
  for ((i = 1; i <= RUNS; i++)); do
    refuse_running_host
    out="$(next_result_dir host)"
    dirs+=("$out")
    echo "==> Run $i/$RUNS on this Mac -> $out"
    set +e
    "$bin" --background --measure "$FIXTURE" --out "$out" --tag "$tag" ${HARNESS_ARGS[@]+"${HARNESS_ARGS[@]}"} \
      2>&1 | tee "$out/harness.log"
    status="${PIPESTATUS[0]}"
    set -e
    ((status == 0)) || run_failed "$out" "exit $status"
  done
  echo
  cmd_report "${dirs[@]}"
}

cmd_vm() {
  parse_args vm "$@"
  require_fixture

  # 1. VMPal CLI and VM state, as scripts/vm-test.sh
  if command -v vmpal >/dev/null 2>&1; then
    VMPAL=(vmpal)
  elif [[ -x "$VMPAL_FALLBACK" ]]; then
    VMPAL=("$VMPAL_FALLBACK" --cli)
  else
    die "VMPal CLI not found: no vmpal on PATH and no $VMPAL_FALLBACK"
  fi
  echo "==> Checking VM ${VM_ID}..."
  local info
  info="$("${VMPAL[@]}" info "$VM_ID" --json)" || die "VM $VM_ID not found in VMPal"
  [[ "$(jq -r '.agentControl' <<< "$info")" == "true" ]] \
    || die "agent control is off for VM $VM_ID: turn it on in VMPal (VM Settings > AI Agents)"
  if [[ "$(jq -r '.state' <<< "$info")" != "running" ]]; then
    echo "==> Starting VM..."
    "${VMPAL[@]}" start "$VM_ID" --wait --background
  fi
  local vm_home
  vm_home="$(vm_sh 'printf %s "$HOME"' | tr -d '\r\n')"
  [[ "$vm_home" == /* ]] || die "cannot read the VM's home folder (got: $vm_home)"
  if vm_exec pgrep -x EchoDiar >/dev/null; then
    die "EchoDiar is running in the VM: quit it first (${VMPAL[*]} exec $VM_ID -- pkill -x EchoDiar)"
  fi

  # 2. Build here, zip, copy the app and the fixture in
  build_debug
  local tag app_dir="EchoDiarQA/measure-$LABEL" fixture_name
  tag="$(git_tag)"
  fixture_name="$(basename "$FIXTURE")"
  echo "==> Copying EchoDiar.app and $fixture_name to the VM..."
  ditto -c -k --keepParent "$APP" "$TMP/EchoDiar.zip"
  vm_exec mkdir -p "$app_dir/results" EchoDiarQA/measure
  "${VMPAL[@]}" cp "$TMP/EchoDiar.zip" "$VM_ID:$app_dir/"
  "${VMPAL[@]}" cp "$FIXTURE" "$VM_ID:EchoDiarQA/measure/"
  vm_sh 'cd "$1" && rm -rf EchoDiar.app && ditto -x -k EchoDiar.zip . && rm -f EchoDiar.zip && xattr -cr EchoDiar.app' \
    "$app_dir"

  # 3. Models, zipped here (skipped when the VM has them)
  if ((INSTALL_MODELS)); then
    local model
    for model in "${VM_MODELS[@]}"; do
      [[ -d "$HOST_MODELS/$model" ]] || die "$model is not installed on this Mac ($HOST_MODELS)"
      if vm_sh '[ -d "$HOME/Library/Application Support/EchoDiar/Models/$1" ]' "$model"; then
        echo "==> $model: already in the VM, skipped"
        continue
      fi
      echo "==> Copying $model to the VM..."
      ditto -c -k --keepParent "$HOST_MODELS/$model" "$TMP/$model.zip"
      "${VMPAL[@]}" cp "$TMP/$model.zip" "$VM_ID:EchoDiarQA/measure/"
      rm -f "$TMP/$model.zip"
      vm_sh 'd="$HOME/Library/Application Support/EchoDiar/Models" && zip="$HOME/EchoDiarQA/measure/$1.zip" &&
        rm -rf "$d/.partial-$1" && mkdir -p "$d/.partial-$1" && ditto -x -k "$zip" "$d/.partial-$1" &&
        mv "$d/.partial-$1/$1" "$d/$1" && rm -rf "$d/.partial-$1" "$zip"' "$model"
    done
  fi

  # 4. Runs, one after the other
  local i out name vm_out state waited dirs=()
  for ((i = 1; i <= RUNS; i++)); do
    out="$(next_result_dir vm)"
    dirs+=("$out")
    name="$(basename "$out")"
    vm_out="$vm_home/$app_dir/results/$name"
    echo "==> Run $i/$RUNS in the VM -> $out"
    vm_sh 'out="$1" app="$2" && shift 2 && rm -rf "$out" && mkdir -p "$out" &&
      open -n --stdout "$out/stdout.log" --stderr "$out/stderr.log" "$app" --args "$@"' \
      "$vm_out" "$vm_home/$app_dir/EchoDiar.app" \
      --background --measure "$vm_home/EchoDiarQA/measure/$fixture_name" --out "$vm_out" --tag "$tag" \
      ${HARNESS_ARGS[@]+"${HARNESS_ARGS[@]}"}

    waited=0
    while :; do
      sleep "$POLL_SECONDS"
      waited=$((waited + POLL_SECONDS))
      state="$(vm_sh 'if [ -f "$1/result.json" ]; then echo done; elif pgrep -x EchoDiar >/dev/null; then echo running;
        elif [ -f "$1/result.json" ]; then echo done; else echo gone; fi' "$vm_out" | tr -d '\r\n')" || state="unreachable"
      case "$state" in
        done) break ;;
        gone)
          "${VMPAL[@]}" cp "$VM_ID:$app_dir/results/$name/." "$out" || true
          tail -n 20 "$out/stderr.log" "$out/stdout.log" >&2 2>/dev/null || true
          die "EchoDiar exited in the VM without result.json, see $out" ;;
        running) ;;
        *) echo "    warning: cannot reach the VM ($state), still waiting" >&2 ;;
      esac
      ((waited < TIMEOUT_SECONDS)) || die "no result.json after $((TIMEOUT_SECONDS / 3600)) h; EchoDiar is left running in the VM"
      ((waited % 300 != 0)) || echo "    $((waited / 60)) min, $state"
    done
    echo "    result.json after about $((waited / 60)) min"
    # The harness exits right after writing result.json: let it go before the next run.
    for _ in $(seq 30); do
      vm_exec pgrep -x EchoDiar >/dev/null || break
      sleep 2
    done
    "${VMPAL[@]}" cp "$VM_ID:$app_dir/results/$name/." "$out"
    [[ -z "$(jq -r '.error // empty' "$out/result.json")" ]] || run_failed "$out" "harness error"
  done
  echo
  cmd_report "${dirs[@]}"
}

cmd_report() {
  (($#)) || die "usage: $0 report <result dir>..."
  report_py "$MEASURE_DIR" "$@"
}

case "${1:-}" in
  fixture|host|vm|report)
    command="$1"
    shift
    "cmd_$command" "$@" ;;
  -h|--help|help) usage ;;
  *) usage >&2; exit 2 ;;
esac
