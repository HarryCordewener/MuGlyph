# Screenshots & demos (headless)

MuGlyph can render its UI to an image **without a terminal or a live connection**, so
documentation images and CI visual checks work anywhere the .NET build runs.

## How it works

SharpConsoleUI ships a `HeadlessConsoleDriver` that renders to a captured buffer instead
of a real console. `muglyph --snapshot` builds the app on that driver, loads a
representative demo scene (a room, a `Chat` spawn window with unread, an input draft),
renders one frame, and writes the raw ANSI to stdout (or `--out file`).

`tools/ansi_frame_to_image.py` parses that frame — cursor-addressed truecolor SGR — into
a character grid and emits a **self-contained SVG** (great for embedding in Markdown) or
**HTML** (`.html` output, or `--html`). No external dependencies.

```bash
# one-shot
muglyph --snapshot --size 100x30 | python3 tools/ansi_frame_to_image.py > shot.svg

# regenerate the committed screenshots
tools/make-screenshots.sh
```

The frame is deterministic (the desktop panels/clock are disabled under the headless
driver), so `muglyph --snapshot` output can also serve as a **golden file** for CI.

## Animated demos (VHS)

For animated GIFs/MP4s of real usage (typing, spawn windows appearing), use
[charmbracelet/vhs](https://github.com/charmbracelet/vhs): write a `.tape` script and run
it in CI with [`charmbracelet/vhs-action`](https://github.com/charmbracelet/vhs-action).
VHS drives the published `muglyph` binary in a headless terminal and records the result —
complementary to the static SVG snapshots above.
