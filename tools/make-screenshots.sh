#!/usr/bin/env bash
# Generate MuGlyph UI screenshots headlessly — no terminal required.
#
# Renders demo frames with `muglyph --snapshot` (SharpConsoleUI's HeadlessConsoleDriver)
# and converts each ANSI frame to an SVG (and HTML) with tools/ansi_frame_to_image.py.
# Runs anywhere the .NET build runs, including CI.
#
# Usage: tools/make-screenshots.sh [output-dir]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/docs/screenshots}"
mkdir -p "$OUT"

echo "Building muglyph…"
dotnet build "$ROOT/src/MuClient.Tui/MuClient.Tui.csproj" -c Release >/dev/null

DLL="$ROOT/src/MuClient.Tui/bin/Release/net10.0/muglyph.dll"

render() {
  local name="$1" size="$2"
  echo "Rendering $name ($size)…"
  dotnet "$DLL" --snapshot --size "$size" --out "$OUT/$name.ansi"
  python3 "$ROOT/tools/ansi_frame_to_image.py" "$OUT/$name.ansi" "$OUT/$name.svg"
  python3 "$ROOT/tools/ansi_frame_to_image.py" "$OUT/$name.ansi" "$OUT/$name.html"
}

render muglyph-demo 96x28

echo "Done. See $OUT/*.svg"
