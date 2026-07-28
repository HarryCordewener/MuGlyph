#!/usr/bin/env python3
"""Build a single-glyph font from docs/logo.svg.

Produces assets/fonts/SharpMUTerm.ttf containing the SharpMUTerm mark at U+E000 (a Private
Use Area codepoint), so it can be merged into a terminal font / Nerd Font patch and
shown in the TUI header. Run: python3 tools/make-glyph-font.py
"""
import re
from pathlib import Path

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.svgLib.path.parser import parse_path

ROOT = Path(__file__).resolve().parent.parent
SVG = ROOT / "docs" / "logo.svg"
OUT = ROOT / "assets" / "fonts" / "SharpMUTerm.ttf"

UPM = 1000
VIEWBOX = 512
CODEPOINT = 0xE000  # Private Use Area
GLYPH_NAME = "sharpmuterm"


def svg_path_data(svg_text: str):
    return re.findall(r'<path[^>]*\bd="([^"]+)"', svg_text)


def build():
    svg_text = SVG.read_text(encoding="utf-8")
    paths = svg_path_data(svg_text)
    if not paths:
        raise SystemExit(f"No <path d=...> found in {SVG}")

    # SVG is Y-down in a 512 viewBox; fonts are Y-up. Scale to the em and flip Y:
    #   x' = s*x ; y' = s*(VIEWBOX - y)
    s = UPM / VIEWBOX
    transform = (s, 0, 0, -s, 0, s * VIEWBOX)

    pen = TTGlyphPen(None)
    tpen = TransformPen(pen, transform)
    for d in paths:
        parse_path(d, tpen)
    glyph = pen.glyph()

    glyph_order = [".notdef", GLYPH_NAME]
    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap({CODEPOINT: GLYPH_NAME})

    notdef = TTGlyphPen(None).glyph()
    fb.setupGlyf({".notdef": notdef, GLYPH_NAME: glyph})
    fb.setupHorizontalMetrics({".notdef": (UPM, 0), GLYPH_NAME: (UPM, 0)})
    fb.setupHorizontalHeader(ascent=UPM, descent=0)
    fb.setupNameTable({
        "familyName": "SharpMUTerm",
        "styleName": "Regular",
        "psName": "SharpMUTerm-Regular",
        "version": "1.0",
    })
    fb.setupOS2(sTypoAscender=UPM, sTypoDescender=0, usWinAscent=UPM, usWinDescent=0)
    fb.setupPost()

    OUT.parent.mkdir(parents=True, exist_ok=True)
    fb.save(str(OUT))
    print(f"Wrote {OUT.relative_to(ROOT)} — {len(paths)} contours at U+{CODEPOINT:04X}")


if __name__ == "__main__":
    build()
