#!/usr/bin/env bash
#
# measure.sh - Measure EchoDiar's RAM/CPU with `top` and check them against the targets.
#
# Samples the running EchoDiar with `top -l N -s 1 -stats pid,command,mem,cpu`
# (MEM = memory footprint). The first sample (no CPU delta) is dropped and the
# next 10 are discarded as warm-up, so no sleep is needed after launching the app.
# Run it on a host Release build, e.g.:
#   open -n <Release app> --args --background
#
# Usage:
#   scripts/measure.sh idle [seconds]          # default 30 samples after warm-up
#   scripts/measure.sh recording [seconds]     # during a recording, live transcript off
#   scripts/measure.sh transcribing [seconds]  # [seconds] = idle baseline length, default 10
#
# Targets (initial, tune after the first run; never loosen them silently):
#   idle          avg MEM <= 40 MB and avg %CPU <= 0.5
#   recording     avg MEM <= 60 MB and avg %CPU <= 3   (live transcript off)
#   transcribing  MEM back to <= baseline + 15 MB within 120 s after the job (peak reported)
#
# transcribing: start the script with the app idle. After warm-up it averages MEM over
# [seconds] samples (the baseline) and prints "start the transcription now". The job
# starts at the first sample with %CPU >= 20 or MEM > baseline + 15 MB (FAIL if none
# within 300 s), and ends at the first of 5 consecutive samples with %CPU < 5. PASS at
# the first sample, from the job end, with MEM <= baseline + 15 MB; FAIL if that does
# not happen within 120 s after the job end.
#
# Env:
#   ECHODIAR_PROCESS=<name>   measure another process (exact name, as `pgrep -x`), for testing
#
# Exit: 0 PASS, 1 FAIL, 2 EchoDiar not running (or exited while sampled), 64 usage.
#
set -euo pipefail

WARMUP=10

usage() {
  echo "usage: $0 idle|recording|transcribing [seconds]   (seconds: positive integer)" >&2
  exit 64
}

MODE="${1:-}"
case "$MODE" in
  idle|recording) SAMPLES="${2:-30}" ;;
  transcribing) SAMPLES="${2:-10}" ;;
  *) usage ;;
esac
if [[ $# -gt 2 ]] || ! [[ "$SAMPLES" =~ ^[0-9]+$ ]] || (( SAMPLES < 1 )); then
  usage
fi

NAME="${ECHODIAR_PROCESS:-EchoDiar}"
ALL_PIDS="$(pgrep -x "$NAME" || true)"
if [[ -z "$ALL_PIDS" ]]; then
  echo "$NAME is not running." >&2
  echo "Start the Release build, e.g.: open -n <Release app> --args --background" >&2
  exit 2
fi
PID="$(pgrep -n -x "$NAME" || true)"
if [[ -z "$PID" ]]; then
  echo "$NAME is not running." >&2
  exit 2
fi
if [[ "$(wc -l <<<"$ALL_PIDS" | tr -d ' ')" -gt 1 ]]; then
  echo "warning: several $NAME processes ($(tr '\n' ' ' <<<"$ALL_PIDS")); measuring the newest, $PID" >&2
fi

# Shared awk helpers: MEM column to MB (K/M/G/B units, optional +/- suffix).
AWK_LIB='
  function mb(v,   n, u) {
    gsub(/[+-]$/, "", v)
    u = substr(v, length(v), 1)
    n = substr(v, 1, length(v) - 1) + 0
    if (u == "K") return n / 1024
    if (u == "M") return n
    if (u == "G") return n * 1024
    if (u == "B") return n / 1048576
    return v / 1048576                      # plain bytes
  }
'

if [[ "$MODE" != "transcribing" ]]; then
  if [[ "$MODE" == "idle" ]]; then MAX_MEM=40; MAX_CPU=0.5; else MAX_MEM=60; MAX_CPU=3; fi

  echo "==> $MODE: sampling $NAME ($PID) for $((SAMPLES + WARMUP + 1)) s (first sample + $WARMUP warm-up ignored)..."
  TOP_OUT="$(top -l "$((SAMPLES + WARMUP + 1))" -s 1 -stats pid,command,mem,cpu -pid "$PID")"

  awk -v pid="$PID" -v skip="$((WARMUP + 1))" -v want="$SAMPLES" \
      -v max_mem="$MAX_MEM" -v max_cpu="$MAX_CPU" -v mode="$MODE" "$AWK_LIB"'
    /^PID[ \t]+COMMAND/ { sample++; next }
    sample > skip && $1 == pid {
      m = mb($(NF - 1)); c = $NF + 0
      mem += m; cpu += c; cnt++
      if (m > peak_mem) peak_mem = m
      if (c > peak_cpu) peak_cpu = c
    }
    END {
      if (!cnt) { print "No samples: the process exited." > "/dev/stderr"; exit 2 }
      am = mem / cnt; ac = cpu / cnt
      printf "\n%-10s %8s %8s %8s %8s %8s\n", "MODE", "AVG MB", "PEAK MB", "AVG CPU", "PEAK CPU", "SAMPLES"
      printf "%-10s %8.1f %8.1f %8.2f %8.2f %5d/%d\n", mode, am, peak_mem, ac, peak_cpu, cnt, want
      printf "target     avg MEM <= %s MB, avg %%CPU <= %s\n", max_mem, max_cpu
      pass = (am <= max_mem) && (ac <= max_cpu) && (cnt == want)
      if (cnt < want) print "Process exited during sampling."
      print ""
      print (pass ? "PASS" : "FAIL")
      exit (cnt < want ? 2 : (pass ? 0 : 1))
    }
  ' <<<"$TOP_OUT" || exit $?
  exit 0
fi

# transcribing: stream `top` (one sample per second) until a decision is reached.
echo "==> transcribing: $NAME ($PID); warm-up $((WARMUP + 1)) s, then a $SAMPLES s idle baseline..."
set +o pipefail          # awk exits on a decision; top then dies of SIGPIPE (141)
STATUS=0
top -l 0 -s 1 -stats pid,command,mem,cpu -pid "$PID" | awk \
    -v pid="$PID" -v skip="$((WARMUP + 1))" -v base_n="$SAMPLES" "$AWK_LIB"'
  BEGIN {
    margin = 15; start_cpu = 20; quiet_cpu = 5; quiet_n = 5
    start_timeout = 300; recover_timeout = 120
    state = "warmup"
  }
  function summary(verdict, code) {
    printf "\nbaseline %.1f MB, limit %.1f MB, job %d s\n", base, base + margin, job_len
    printf "after baseline: avg MEM %.1f MB, peak MEM %.1f MB, avg %%CPU %.2f, peak %%CPU %.2f, %d samples\n", \
      jn ? jmem / jn : 0, peak_mem, jn ? jcpu / jn : 0, peak_cpu, jn
    print ""
    print verdict
    decided = 1
    exit code
  }
  /^PID[ \t]+COMMAND/ {
    sample++
    if (sample > 1 && !seen) { if (++missing >= 3) { print "Process exited." > "/dev/stderr"; decided = 1; exit 2 } }
    else missing = 0
    seen = 0
    next
  }
  $1 == pid {
    seen = 1
    if (sample <= skip) next
    m = mb($(NF - 1)); c = $NF + 0
    if (state == "warmup") state = "baseline"
    if (state == "baseline") {
      bsum += m; bcnt++
      if (bcnt == base_n) {
        base = bsum / bcnt
        printf "baseline %.1f MB (limit %.1f MB) -- start the transcription now\n", base, base + margin
        fflush()
        state = "wait"; waited = 0
      }
      next
    }
    if (m > peak_mem) peak_mem = m
    if (c > peak_cpu) peak_cpu = c
    jmem += m; jcpu += c; jn++
    if (state == "wait") {
      if (c >= start_cpu || m > base + margin) {
        state = "job"; job_len = 0; quiet = 0
        printf "job started (%.1f MB, %.1f%% CPU)\n", m, c; fflush()
      } else if (++waited >= start_timeout) {
        print "No transcription job observed within " start_timeout " s."
        summary("FAIL", 1)
      } else next
    }
    if (state == "job") {
      job_len++
      if (c < quiet_cpu) qm[quiet++] = m; else quiet = 0
      if (quiet < quiet_n) next
      job_len -= quiet_n
      printf "job ended after %d s\n", job_len; fflush()
      state = "recover"; rec = quiet_n - 1
      # the quiet samples already belong to the recovery window
      for (i = 0; i < quiet_n; i++) if (qm[i] <= base + margin) {
        printf "memory back to %.1f MB within %d s\n", qm[i], i + 1
        summary("PASS", 0)
      }
      next
    }
    if (state == "recover") {
      rec++
      if (m <= base + margin) {
        printf "memory back to %.1f MB within %d s\n", m, rec + 1
        summary("PASS", 0)
      }
      if (rec + 1 >= recover_timeout) {
        printf "memory still %.1f MB %d s after the job\n", m, recover_timeout
        summary("FAIL", 1)
      }
    }
  }
  END {
    if (!decided) { print "Sampling ended before a decision." > "/dev/stderr"; exit 2 }
  }
' || STATUS=$?
exit "$STATUS"
