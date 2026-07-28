# Bundled screenshot font

`SharpMUTermMonoNerd.woff` is a **subset** of **JetBrainsMono Nerd Font Mono**, reduced to only the
codepoint ranges SharpMUTerm's snapshots use (ASCII, Latin-1, box drawing, block/geometric shapes,
arrows, and the Nerd Font icon PUA ranges). It is embedded as a base64 `@font-face` by
`tools/ansi_frame_to_image.py` so the generated SVG/HTML shows the Nerd Font icons and box drawing
on any viewer, with no font install required.

It is **not** used by the application itself — only by the documentation screenshot pipeline.

## Licensing

- **JetBrainsMono** — SIL Open Font License 1.1 (© JetBrains).
- **Nerd Fonts** patch / glyphs — MIT (© Ryan L McIntyre) with bundled icon sets under their own
  permissive licenses.

The SIL OFL permits bundling and redistribution (including subsets) provided the font is not sold on
its own; this repository redistributes it only as a screenshot asset.

The complete license texts travel with the font in this directory, as SIL OFL
1.1 requires for any redistributed copy (including subsets):

- [`OFL.txt`](OFL.txt) — the subset notice plus the full, canonical SIL Open
  Font License 1.1 text governing JetBrainsMono.
- [`LICENSE-NerdFonts.txt`](LICENSE-NerdFonts.txt) — the full Nerd Fonts license
  (MIT plus the OFL notice) for the patch/glyph contributions.

See also <https://github.com/ryanoasis/nerd-fonts> and
<https://www.jetbrains.com/lp/mono/>.
