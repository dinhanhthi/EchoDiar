#!/usr/bin/env bash
# Generates the Windows app's MSIX visual assets and EchoDiar.ico from assets/logo_square.png (the green square
# app icon, the same as macOS). Runs on the Mac with sips and python3 (stdlib, for the ICO container) only.
#
# Usage: bash windows/tools/make-icons.sh [--src <png>] [--check]
#   --src      the source PNG (default assets/logo_square.png). Pass a copy of the committed file
#              (git show HEAD:assets/logo_square.png > <tmp>) when the working tree has uncommitted logo edits.
#   (no flag)  replaces every .png and the .ico in windows/src/EchoDiar.App/Assets with a fresh set.
#   --check    generates into a temp dir and compares byte for byte with the committed Assets; exit 1 on any
#              difference, missing or extra file. sips output is deterministic on one macOS version but may change
#              with a macOS update: if --check then fails without a logo change, regenerate and commit.
#
# Fill: plated tiles and Square44x44Logo.scale-* 80 % of the canvas, Wide310x150Logo 75 % of the height,
# SplashScreen 50 % of the height, unplated targetsizes full bleed with a size/32 margin (at least 1 px).
# Sizes per scale are round(base * scale / 100), halves rounded up (as the Visual Studio asset generator does).
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
src="$root/assets/logo_square.png"
assets="$root/windows/src/EchoDiar.App/Assets"
check=0
while (( $# )); do
  case "$1" in
    --check) check=1; shift ;;
    --src) [[ $# -ge 2 ]] || { echo "usage: $0 [--src <png>] [--check]" >&2; exit 2; }; src=$2; shift 2 ;;
    *) echo "usage: $0 [--src <png>] [--check]" >&2; exit 2 ;;
  esac
done
[[ -f "$src" ]] || { echo "no source PNG at $src" >&2; exit 2; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
out="$work/out"
mkdir -p "$out"

# One RGBA master to resample from (the source may be a palette PNG).
master="$work/master.png"
sips -s format png "$src" --out "$master" >/dev/null
src_size=$(sips -g pixelHeight "$master" | awk '/pixelHeight/ { print $2 }')

scales=(100 125 150 200 400)
targetsizes=(16 20 24 30 32 36 40 48 60 64 72 80 96 256)
ico_sizes=(16 20 24 32 40 48 64 256)

scaled() { echo $(( ($1 * $2 + 50) / 100 )); }

# render <width> <height> <logo size> <file>: the logo, <logo size> square, centred on a transparent canvas.
render() {
  local w=$1 h=$2 l=$3 file=$4
  (( l > src_size )) && l=$src_size
  sips -z "$l" "$l" "$master" --out "$work/logo.png" >/dev/null
  sips --padToHeightWidth "$h" "$w" "$work/logo.png" --out "$out/$file" >/dev/null
}

# tile <base width> <base height> <fill % of the height> <name>: the scale-100..400 set.
tile() {
  local bw=$1 bh=$2 fill=$3 name=$4 s w h
  for s in "${scales[@]}"; do
    w=$(scaled "$bw" "$s"); h=$(scaled "$bh" "$s")
    render "$w" "$h" "$(( (h * fill + 50) / 100 ))" "$name.scale-$s.png"
  done
}

tile 44 44 80 Square44x44Logo
tile 71 71 80 SmallTile
tile 150 150 80 Square150x150Logo
tile 310 150 75 Wide310x150Logo
tile 310 310 80 LargeTile
tile 50 50 80 StoreLogo
tile 620 300 50 SplashScreen

# Target sizes (taskbar, Start, title bar): the same full-bleed art plated, unplated and light unplated.
for t in "${targetsizes[@]}"; do
  margin=$(( t / 32 )); (( margin < 1 )) && margin=1
  render "$t" "$t" "$(( t - 2 * margin ))" "Square44x44Logo.targetsize-$t.png"
  cp "$out/Square44x44Logo.targetsize-$t.png" "$out/Square44x44Logo.targetsize-${t}_altform-unplated.png"
  cp "$out/Square44x44Logo.targetsize-$t.png" "$out/Square44x44Logo.targetsize-${t}_altform-lightunplated.png"
done

# EchoDiar.ico for AppWindow.SetIcon: PNG-compressed frames (valid since Windows Vista), smallest first.
frames=()
for t in "${ico_sizes[@]}"; do frames+=("$out/Square44x44Logo.targetsize-${t}_altform-unplated.png"); done
python3 -I - "$out/EchoDiar.ico" "${frames[@]}" <<'PY'
import struct, sys

out, paths = sys.argv[1], sys.argv[2:]
pngs = [open(p, "rb").read() for p in paths]
header = struct.pack("<HHH", 0, 1, len(pngs))
entries, offset = b"", 6 + 16 * len(pngs)
for data in pngs:
    width, height = struct.unpack(">II", data[16:24])
    entries += struct.pack("<BBBBHHII", width % 256, height % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
with open(out, "wb") as f:
    f.write(header + entries + b"".join(pngs))
PY

if (( check )); then
  if diff -rq "$out" "$assets"; then
    echo "Assets are up to date."
  else
    echo "Assets differ from $src: run bash windows/tools/make-icons.sh" >&2
    exit 1
  fi
else
  rm -f "$assets"/*.png "$assets"/*.ico
  cp "$out"/* "$assets"/
  echo "Wrote $(ls "$out" | wc -l | tr -d ' ') files to $assets"
fi
