#!/usr/bin/env python3
"""Convert a MuGlyph headless snapshot (`muglyph --snapshot`) into an SVG image.

The headless SharpConsoleUI driver emits a full-screen ANSI frame: per-row cursor
moves (`ESC[row;colH`), truecolor SGR runs (`ESC[...;38;2;r;g;b;48;2;r;g;b;...m`),
and text. This parses that into a character grid with per-cell fg/bg/bold and emits
a self-contained SVG (monospace, background rects + text) — no external deps, so it
runs anywhere and the output embeds directly in Markdown/READMEs.

Usage:
    muglyph --snapshot --size 100x30 | python3 tools/ansi_frame_to_svg.py > shot.svg
    python3 tools/ansi_frame_to_svg.py frame.ansi shot.svg
"""
import base64
import os
import sys

CELL_W = 8.4
CELL_H = 18.0
FONT_SIZE = 15
PAD = 12
# A little extra headroom above the first row so a focused header highlight or a tall Nerd glyph
# isn't clipped against the top edge, and clear breathing room below the last row so previews never
# look cut off against the bottom edge.
PAD_TOP = 20
PAD_BOTTOM = 28
DEFAULT_BG = (24, 24, 28)
DEFAULT_FG = (238, 238, 238)

# A subset JetBrainsMono Nerd Font Mono (OFL), embedded so the Nerd Font icons + box drawing render
# anywhere the SVG is opened — no font install required on the viewer's side. Falls back to generic
# monospace when the file is absent (icons then show as tofu, same as before).
FONT_NAME = "MuGlyph Mono Nerd"
FONT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fonts", "MuGlyphMonoNerd.woff")
FONT_FAMILY = f"'{FONT_NAME}',ui-monospace,Menlo,Consolas,monospace"


def font_face_style():
    """Returns an inline <style> embedding the bundled font as base64, or empty if it's missing."""
    if not os.path.exists(FONT_PATH):
        return ""
    with open(FONT_PATH, "rb") as fh:
        b64 = base64.b64encode(fh.read()).decode("ascii")
    return (
        "<defs><style>"
        f"@font-face{{font-family:'{FONT_NAME}';"
        f"src:url(data:font/woff;base64,{b64}) format('woff');"
        "font-weight:normal;font-style:normal;}"
        "</style></defs>"
    )


class Cell:
    __slots__ = ("ch", "fg", "bg", "bold")

    def __init__(self):
        self.ch = " "
        self.fg = DEFAULT_FG
        self.bg = DEFAULT_BG
        self.bold = False


def parse(data):
    grid = {}
    row = col = 1
    fg, bg, bold = DEFAULT_FG, DEFAULT_BG, False
    i, n = 0, len(data)
    maxr = maxc = 0
    while i < n:
        c = data[i]
        if c == "\x1b" and i + 1 < n and data[i + 1] == "[":
            j = i + 2
            while j < n and not data[j].isalpha():
                j += 1
            seq, fin = data[i + 2:j], data[j] if j < n else ""
            if fin == "H":
                parts = seq.split(";")
                row = int(parts[0] or 1)
                col = int(parts[1] or 1) if len(parts) > 1 else 1
            elif fin == "m":
                fg, bg, bold = apply_sgr(seq, fg, bg, bold)
            i = j + 1
            continue
        if c == "\x1b" and i + 1 < n and data[i + 1] == "]":
            j = i + 2
            while j < n and data[j] != "\x07":
                j += 1
            i = j + 1
            continue
        if c == "\x1b":
            i += 2
            continue
        if c == "\n":
            row += 1
            col = 1
        elif c == "\r":
            col = 1
        elif c >= " ":
            cell = Cell()
            cell.ch, cell.fg, cell.bg, cell.bold = c, fg, bg, bold
            grid[(row, col)] = cell
            maxr, maxc = max(maxr, row), max(maxc, col)
            col += 1
        i += 1
    return grid, maxr, maxc


def apply_sgr(seq, fg, bg, bold):
    codes = [int(x) if x else 0 for x in seq.split(";")] or [0]
    k = 0
    while k < len(codes):
        c = codes[k]
        if c == 0:
            fg, bg, bold = DEFAULT_FG, DEFAULT_BG, False
        elif c == 1:
            bold = True
        elif c == 22:
            bold = False
        elif c == 38 and k + 4 < len(codes) and codes[k + 1] == 2:
            fg = (codes[k + 2], codes[k + 3], codes[k + 4])
            k += 4
        elif c == 48 and k + 4 < len(codes) and codes[k + 1] == 2:
            bg = (codes[k + 2], codes[k + 3], codes[k + 4])
            k += 4
        k += 1
    return fg, bg, bold


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def hexc(rgb):
    return "#%02x%02x%02x" % rgb


def to_svg(grid, maxr, maxc):
    width = PAD * 2 + maxc * CELL_W
    height = PAD_TOP + maxr * CELL_H + PAD_BOTTOM
    out = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width:.0f}" height="{height:.0f}" '
        f'viewBox="0 0 {width:.0f} {height:.0f}" font-family="{FONT_FAMILY}" '
        f'font-size="{FONT_SIZE}">',
        font_face_style(),
        # Square corners — a terminal frame, no rounding.
        f'<rect width="{width:.0f}" height="{height:.0f}" fill="{hexc(DEFAULT_BG)}"/>',
    ]
    # Background rects: merge horizontal runs of equal bg per row.
    for r in range(1, maxr + 1):
        c = 1
        while c <= maxc:
            cell = grid.get((r, c))
            bg = cell.bg if cell else DEFAULT_BG
            start = c
            while c <= maxc:
                nxt = grid.get((r, c))
                if (nxt.bg if nxt else DEFAULT_BG) != bg:
                    break
                c += 1
            if bg != DEFAULT_BG:
                x = PAD + (start - 1) * CELL_W
                y = PAD_TOP + (r - 1) * CELL_H
                out.append(
                    f'<rect x="{x:.1f}" y="{y:.1f}" width="{(c - start) * CELL_W:.1f}" '
                    f'height="{CELL_H:.1f}" fill="{hexc(bg)}"/>'
                )
    # Text: merge horizontal runs of equal fg/bold per row.
    for r in range(1, maxr + 1):
        y = PAD_TOP + (r - 1) * CELL_H + FONT_SIZE - 1
        c = 1
        while c <= maxc:
            cell = grid.get((r, c))
            if not cell or cell.ch == " ":
                c += 1
                continue
            fg, bold = cell.fg, cell.bold
            start, run = c, []
            while c <= maxc:
                nxt = grid.get((r, c))
                if not nxt or nxt.ch == " " or nxt.fg != fg or nxt.bold != bold:
                    break
                run.append(nxt.ch)
                c += 1
            x = PAD + (start - 1) * CELL_W
            weight = ' font-weight="bold"' if bold else ""
            out.append(
                f'<text x="{x:.1f}" y="{y:.1f}" fill="{hexc(fg)}"{weight} '
                f'xml:space="preserve">{esc("".join(run))}</text>'
            )
    out.append("</svg>")
    return "\n".join(out)


def to_html(grid, maxr, maxc):
    """Emit a self-contained HTML page: the grid as a coloured <pre>."""
    rows = []
    for r in range(1, maxr + 1):
        spans, c = [], 1
        while c <= maxc:
            cell = grid.get((r, c))
            fg = cell.fg if cell else DEFAULT_FG
            bg = cell.bg if cell else DEFAULT_BG
            bold = cell.bold if cell else False
            start, run = c, []
            while c <= maxc:
                nxt = grid.get((r, c))
                nfg = nxt.fg if nxt else DEFAULT_FG
                nbg = nxt.bg if nxt else DEFAULT_BG
                nbold = nxt.bold if nxt else False
                if (nfg, nbg, nbold) != (fg, bg, bold):
                    break
                run.append(nxt.ch if nxt else " ")
                c += 1
            style = f"color:{hexc(fg)}"
            if bg != DEFAULT_BG:
                style += f";background:{hexc(bg)}"
            if bold:
                style += ";font-weight:bold"
            spans.append(f'<span style="{style}">{esc("".join(run))}</span>')
        rows.append("".join(spans))
    body = "\n".join(rows)
    face = font_face_style().replace("<defs>", "").replace("</defs>", "")
    return (
        "<!doctype html><meta charset=\"utf-8\"><title>MuGlyph</title>"
        f"{face}"
        f"<body style=\"margin:0;background:{hexc(DEFAULT_BG)}\">"
        f"<pre style=\"font:15px/1.2 {FONT_FAMILY};"
        f"padding:12px;color:{hexc(DEFAULT_FG)};background:{hexc(DEFAULT_BG)}\">"
        f"{body}</pre></body>"
    )


def main():
    args = [a for a in sys.argv[1:]]
    data = open(args[0], encoding="utf-8", errors="replace").read() if args else sys.stdin.read()
    grid, maxr, maxc = parse(data)
    out_path = args[1] if len(args) > 1 else None
    html = "--html" in args or (out_path is not None and out_path.endswith(".html"))
    text = to_html(grid, maxr, maxc) if html else to_svg(grid, maxr, maxc)
    if out_path:
        open(out_path, "w", encoding="utf-8").write(text)
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
