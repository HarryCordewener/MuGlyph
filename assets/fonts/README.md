# SharpMUTerm icon font

`SharpMUTerm.ttf` is a single-glyph font containing the SharpMUTerm mark (from `docs/logo.svg`) at
codepoint **U+E000** (Private Use Area). It's meant to be merged into your terminal font (or a
Nerd-Font-style patch) so the logo can appear in the TUI header as a normal character.

Regenerate it from the SVG with:

```bash
pip install fonttools
python3 tools/make-glyph-font.py
```

The generator scales the 512-unit SVG viewBox to a 1000-unit em and flips the Y axis (SVG is
Y-down, fonts are Y-up). To use a different codepoint or em size, edit the constants at the top
of `tools/make-glyph-font.py`.

> Terminals render text in the user's configured font, so a TUI can't ship its own glyph — this
> font is for users who want the mark in their terminal. The header falls back to a text label
> when the glyph isn't present.
