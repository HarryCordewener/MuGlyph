# CLAUDE.md — SharpMUTerm agent brief

Guidance for any Claude agent working in this repository. Read this first, then read
[`docs/PLAN.md`](docs/PLAN.md) — the plan is the authoritative architecture + roadmap.

## What this project is

**SharpMUTerm** is a cross-platform TUI **MU\*** (MUSH / MUCK / MUD) client in **C# / .NET 10**,
targeting feature parity with [BeipMU](https://beipdev.github.io/BeipMU/), running inside
GPU-accelerated terminals (Kitty, WezTerm, Ghostty) on **Windows and Linux**.

"GPU acceleration" is a property of the terminal *emulator*, not this app. Our job is to emit
rich truecolor/styled text and use the **Kitty graphics protocol** (with Sixel + half-block
fallbacks) for inline images/maps.

## Locked decisions (do not relitigate without asking)

- **Target framework:** `net10.0`.
- **TUI base:** **SharpConsoleUI** (`nickprotop/ConsoleEx`, stable, net8/9/10) — a compositor-based
  framework with split layouts, tabs, resizable/mouse-draggable windows, Spectre-style markup, and
  a **native Kitty graphics protocol** (+ Sixel/half-block) for inline images. Replaced Terminal.Gui
  v2 (which was prerelease with an `[Obsolete]` mid-migration API); the switch is contained to
  `SharpMUTerm.Tui` because `SharpMUTerm.Core` is UI-agnostic.
- **Scripting:** Lua via **MoonSharp** (pure-managed, sandboxed).
- **Inline graphics:** in scope from day one (Kitty Unicode placeholders → Sixel → half-block).
- **Protocols:** aim for all common MU\* protocols. GMCP/MSSP/CHARSET/NAWS/MTTS/EOR via
  TelnetNegotiationCore; **MCCP, MSDP, MXP, and Pueblo are our own app layer.**
- **Config:** fresh JSON schema of our own (worlds hold characters; automation lives in shared
  named trigger sets), versioned with automatic migration between schema revisions.
- **License:** MIT.

## Repository state

**M1 delivered, plus substantial M2–M4 work.** `SharpMUTerm.slnx` builds all ten projects on
`net10.0`, with the full test suite passing. In place:

- **Core** — `AnsiParser` (SGR 16/256/truecolor), styled-line + `ScrollbackBuffer` model (a capped
  in-memory ring plus a **file-backed spill**, `FileScrollbackSpill`, so history deeper than memory is
  paged off an ephemeral per-session cache under `$XDG_CACHE_HOME`; absolute line indices, ranged
  reads capped at `MaxRangeLines`, and any disk failure degrades to memory-only. Emphatically **not**
  the session log — that stays `PlainTextLogSink`/`HtmlLogSink`, opt-in and kept),
  `TcpTransport` (TLS + IPv6), `TelnetSession` (wraps TelnetNegotiationCore **2.5.3**),
  trigger/alias/macro engines + `IntervalScheduler`, plain-text + HTML logging, versioned JSON
  config (worlds → characters + shared trigger sets, with migration),
  `Theme`/`ThemeLibrary`, and `WorldSession`/`SessionManager` orchestration.
- **Graphics** — Kitty encoder + Unicode placeholders, Sixel + half-block fallbacks, capability
  probe, and `InlineImagePolicy` — the Kitty → Sixel → half-block → text degradation chain (no UI
  dependency). Inside the TUI the *pixels* are drawn by SharpConsoleUI's `ImageControl`; ours
  supplies the policy, because only the framework's renderer can put an image into compositor cells.
  **Sixel inside the compositor is blocked upstream:** at SharpConsoleUI 2.5.14 `IImageRenderer` is
  `internal` and `ImageControl.ResolveRenderer()` is private, so no Sixel back-end can be injected —
  and the framework ships none. Reopening it needs an upstream PR making `IImageRenderer` public and
  `ResolveRenderer` overridable; nothing on our side unblocks it. `InlineImagePolicy` therefore
  degrades a Sixel-only terminal to half-block explicitly rather than pretending.
- **Scripting** — sandboxed MoonSharp `ScriptHost` (world/output/trigger/alias/timer/gmcp/log).
- **Tui** — **SharpConsoleUI** app: a `TabControl` of output windows (main + trigger-routed **spawn
  windows** + web view, with unread badges), each a `MarkupControl` in a `ScrollablePanelControl`
  viewport (PgUp/PgDn, Shift+↑/↓, ⌃Home/⌃End, wheel; unread badges count output arriving below a
  scrolled-back viewport), fed StyledLine → Spectre-style
  markup via `MarkupFormatter` (clickable `[link=…]` MXP/Pueblo/web spans); an `InputBarControl`
  command line (wrapping, auto-growing, per-window drafts, plus an optional per-window second bar),
  status line, `Ctrl+Q` quit, per-pane NAWS (every connected session is told its own pane's output
  rectangle, on every resize and layout change, rate-limited to four writes a second with a trailing
  flush). The tab/pane set is driven by the tested `Core.Workspaces` model, with **splits** (thin
  single-line dividers) and the **connection rail** now rendered as well — and **clickable**: a world,
  character or window row switches to it, dispatched through the rail control's *own* `LinkClicked`
  (never the output panes' handler, so a world cannot drive the client's UI from the wire).

## Building and testing

- **.NET 10 SDK**: install via `apt-get install -y dotnet-sdk-10.0` (the Microsoft CDN is often
  blocked; Ubuntu's repo works). NuGet (`api.nuget.org`) is reachable.
- **Tests are TUnit on Microsoft.Testing.Platform** (`Exe` projects, not xUnit). `dotnet test` does
  **not** work — .NET 10 dropped VSTest. Run each suite directly, and keep the `</dev/null`: it
  detaches stdin so the test host doesn't hang waiting on it.
  ```bash
  dotnet run -c Release --project tests/SharpMUTerm.Core.Tests </dev/null
  ```
  There are five: Core, Graphics, Scripting, Web, Tui. Primary signal is
  `dotnet build SharpMUTerm.slnx` plus all five green and warning-free.
- **Building against the local SharpConsoleUI clone surfaces 2 NuGet advisory warnings** for
  AngleSharp. They are the framework's, not ours; a build against the package has none.

## Visual verification — the snapshot pipeline

A headless environment can't run `NetConsoleDriver` or render Kitty graphics, but the TUI is *not*
therefore unverifiable: it renders real frames headlessly.

```bash
dotnet run -c Release --project src/SharpMUTerm.Tui --no-build -- \
  --snapshot --demo-config --view <name> --size 120x32 --out frame.ansi
python3 tools/ansi_frame_to_image.py frame.ansi frame.html   # or .svg
```

- **`--demo-config` is not optional for verification work.** Without it the snapshot renders
  whatever config is on the machine, and a saved `~/.config/SharpMUTerm/` quietly replaces the demo
  worlds — you end up checking your own data and calling it the demo.
- **Views:** `worlds`/`settings`, `triggers`, `route`, `highlight`, `aliases`, `timers`, `keypad`,
  `set`, `textansi`, `input`, `logging`, `password`, `freeze`, `spawn`, `split`, `move`, `drag`,
  `history`, `history-search`, `history-search-filter`, `draft`, `draft2`, `menu`, `menu-split`,
  `messages`, `quit`, `deletions`, `web`, `scrollback`, `scrollback-up`, `freeze-scrollback`, plus the
  default workspace
  (no `--view`). Any settings screen also takes a `-edit` suffix, which opens it and drives real
  keys in so the frame shows a field mid-edit. State toggles: `collapsed`, `prefix`, `timestamps`.
- **A snapshot never writes configuration.** `SharpMUTermApp` takes its `save` action from the caller and
  the snapshot path passes none, so an app that isn't the live entry point owns no file. That matters
  because the settings screens persist each committed change as it is made: without the gate, a
  `--demo-config` frame that drove a key into a field (`logging-edit`, `keypad-edit`, `deletions`) would
  write the demo worlds straight over your own `config.json`.
- **The three `scroll*` views are the only ones with more output than a pane holds.** Every other view
  fits, which is exactly why no snapshot caught the panes being unable to scroll at all. Reach for one
  of these (or `LoadLongScene`) whenever a change touches the output area.
- **Send the user the `.svg`.** For your *own* inspection render the `.html` — Chromium clips the
  bottom of a bare `.svg` through aspect-ratio scaling, which will make you chase a layout bug that
  isn't there.
- **Decoding a frame precisely:** the `.ansi` is cursor-addressed SGR. To check exact column widths
  or which background band covers which row, walk it into a `{row:{col:ch}}` grid tracking
  `48;2;r;g;b` (background) — note `48`, not `38`, or you will read foreground and conclude wrongly.

## SharpConsoleUI — the traps that cost the most time

Package `SharpConsoleUI`, repo `nickprotop/ConsoleEx`, pinned at **2.5.14**. A sibling clone at
`../SharpConsoleUI` is referenced by project when present, else the package
(`-p:UseSharpConsoleUIPackage=true` forces the package). Read the source there rather than guessing.

App shape: `ConsoleWindowSystem(new NetConsoleDriver(RenderMode.Buffer), new ConsoleWindowSystemOptions())`;
fluent `WindowBuilder`/`Controls` factories; `AddControl` is builder-time, so keep refs and mutate at
runtime. Marshal background work with `system.EnqueueOnUIThread`; global keys via
`RegisterGlobalShortcut`; `system.Run()` blocks, `RequestExit(code)` ends it. Text is Spectre-style
markup (`[bold #rrggbb on #rrggbb]…[/]`, `[[`/`]]` escaping, `[link=url]…[/]` → `LinkClicked`).

- **Controls default to `HorizontalAlignment.Left`, which self-sizes to content** instead of filling
  the slot. This is the single biggest cause of "why doesn't this fill the width?" Use
  `.WithAlignment(HorizontalAlignment.Stretch)`. A `.Flex(n)` column only fills if the grid is
  arranged at full width *and* the child in it is Stretch.
- **Nothing focuses a control for you, and nothing keeps it focused.** A click in the output pane, a
  click on a tab strip, ⇥, and every overlay's `SetIsActive` all move focus. Typing is routed
  explicitly from `PreviewKeyPressed` and so survives that; **paste is not — it follows
  `FocusManager`**, which is why paste broke after any click while typing appeared fine.
  `FocusChanged → PinFocusToArmedBar()` makes "which bar ⏎ sends from", "what the framework pastes
  into" and "where the caret is drawn" one fact. Keep the pin; don't re-sync three places.
- **Ask the driver for the terminal size, never a literal.** `_system.ConsoleDriver.ScreenSize` is
  correct from the moment the window system exists — before any window does. Chrome built in the
  app constructor against a literal wrapped the header on the first frame of any narrower terminal,
  and snapshots never saw it because every render path rebuilds the header on the way past. Same
  shape as gating chrome on `headless`: right in a snapshot, wrong in a terminal. Test the class of
  bug by reading chrome width off a *freshly constructed* app.
- **Vertical space at the window root is sticky-first, Fill-last** (`Layout/WindowContentLayout.cs`).
  Sticky-top and sticky-bottom children are measured first and then trusted; Fill children divide
  what remains. A flow control therefore *cannot* starve a sticky one — so "the workspace is greedy
  and squeezed the input bars" is a diagnosis this layout cannot produce. But there is no
  `MinHeight` concept and **nothing checks the two sticky bands fit each other**: at 80×6 they
  over-commit and the status line is arranged off-screen. `SyncInputHeights`' veto counts chrome
  rows to prevent it, and `PaintStatus` re-runs it because the status line's length changes at
  runtime. Assert layout with **arranged bounds** (`ActualHeight` after a real frame), not arithmetic.
- **The desktop panels are off unconditionally** (`ShowTopPanel: false, ShowBottomPanel: false`).
  They restate our own header and trim window titles to fifteen cells — on this app, one row reading
  `SharpMU...lient`. They were once hidden in headless only, so no snapshot showed them and the
  truncated title survived to a real terminal.
- **`WindowBuilder.Centered()` must come *after* `WithSize()`** — it reads `_bounds` and falls back
  to 80×25, so centring first positions the window as if it were that size.
- **`PromptControl` is not the command line** — `InputBarControl` is. The framework's prompt is
  single-line by construction (`SetInput` replaces `\n` with a space) and unfocuses itself on ⏎
  (`UnfocusOnEnter`, default true, not settable through the builder).
- **Settings screens have no `IPasteTarget` and cannot easily have one.** They are markup rebuilt
  wholesale every key, with the edited field a buffer in `SettingsSession`. `SettingsOverlay`
  listens at `IConsoleDriver.Paste` instead. That can't double-fire *only* because no control on
  those windows accepts paste — add a focusable `IPasteTarget` there and both paths will.
- **The framework's own `ExitKey` defaults to Ctrl+Q** and calls `RequestExit` with nothing in
  between. Ours won only because application shortcuts are tried first. It is set to `null`.
- **`MarkupControl` does not scroll and does not bottom-anchor.** `PaintDOM` paints rows from index 0
  until the box runs out, with no offset of its own — so a control holding 100 lines in a 10-row box
  renders lines 1–10 for ever and everything appended lands off-screen. Scrolling lives in
  `ScrollablePanelControl`, and every output region in this app is now wrapped in one
  (`SharpMUTermApp.ScrollViewFor`). Two things about it:
  - **`AutoScroll` is not "on AddControl"**, whatever the property doc says: it re-pins to the bottom on
    *any* repaint while enabled, detaches when the user scrolls up and re-attaches at the bottom
    (`ScrollablePanelControl.Rendering.cs:125-133`, `.Scrolling.cs:145-152`). That is terminal
    behaviour; don't reimplement it. But it moves the offset **during paint**, after the children were
    arranged, so the frame that discovers new content is one frame stale — a headless snapshot or test
    must render a settling frame (`SettleScroll` / `RenderWholeFrame`).
  - **`ScrollToTop`/`ScrollToBottom` do not touch `AutoScroll`.** Only `ScrollVerticalBy` treats itself
    as a user gesture. A jump-to-top that leaves auto-scroll armed is undone by the next repaint.
  - **A disposed `ScrollablePanelControl` clears its children**, and `RebuildPaneArea` disposes the whole
    old tree. The kept viewports therefore refill themselves; markup controls survive disposal (they
    override nothing), which is why the same one is re-parented for the life of the app.
- **Only `MarkupControl.AppendLine` and `FeedRange` hand pane content to a control** — the seam a
  windowed feed replaces. Appending re-parses the whole control (the parse cache is keyed on a content
  version), so never "refresh" a pane by re-`SetContent`-ing the full buffer on a scroll or a frame.
- **The scrollback keys are routed from `PreviewKeyPressed`** (`TryScrollKey`), and the wheel from the
  driver (`ScrollPaneUnderPointer`), for the same reason everything else in this window is: focus is
  pinned to the armed bar, so `ScrollablePanelControl.ProcessKey` — which returns false unless it has
  focus — would never see a key.
- **Control chords collapse onto their ASCII bytes, so some are unbindable.** `AnsiInputParser`
  decodes no CSI-u and enables no `modifyOtherKeys`: `Ctrl+H` arrives as `Backspace` with
  `control: false` (byte 0x08), and I/M/J are Tab/Enter/Enter — the app cannot even tell the modifier
  was held, so binding those breaks the plain key instead. `Alt+Backspace` is not available either:
  ESC followed by a *control* byte is emitted as **two** keys (Escape, then Backspace), so only
  `ESC` + a printable byte becomes an Alt chord. `MacroKeys.Verdict` is the readable form of all this.
- **A global shortcut runs before any window**, so a chord in `MacroKeys.AppShortcuts` can never reach
  a control's own key table. That is why the command line has no ⌃W (`CloseActiveWindow` claims it) and
  why `InputBuffer.KillWordLeft` currently has no chord that can reach it.

## Other dependency notes

- **TelnetNegotiationCore 2.6.0** (repo owner is its author — extend it by PR rather than working
  around it). Fluent builder API; negotiates MCCP/MSDP/MXP itself; ships the keepalive interpreter
  (`WithKeepAlive(TimeSpan?, …)`, default 30s, clamped to 1s–24h). `TelnetSession` sets the
  init-only `CallbackOnByteAsync` reflectively to see raw bytes including unterminated prompts — a
  first-class `OnByte` builder hook remains a good upstream PR. It handles the option handshake
  (TELOPT, GA, TTYPE/MTTS, EOR, NAWS, CHARSET, MSSP, GMCP) — **Pueblo and all ANSI/MXP/Pueblo
  payload _parsing_ stay our layer.**
- **MoonSharp** — package id `MoonSharp`, pure-managed, no native deps.
- **Serilog** behind `Microsoft.Extensions.Logging` (`ClientDiagnostics`) feeds a capped in-memory
  `ClientMessageLog` (⌃P ▸ *Show client messages*) and a rolling file kept **separate from session
  transcripts**. Never add a console sink — it would paint over the TUI.

## Architecture rule (non-negotiable)

`SharpMUTerm.Core` stays **UI-agnostic and fully unit-testable**. All transport, telnet, parsing
(ANSI/MXP/Pueblo), GMCP/MSDP routing, scrollback, and trigger/alias/macro engines live there.
SharpConsoleUI is referenced **only** from `SharpMUTerm.Tui`.

Planned solution layout:

| Project | Responsibility |
|---|---|
| `SharpMUTerm.Core` | Transport, telnet, ANSI/MXP/Pueblo parsers, GMCP/MSDP routing, scrollback, engines, logging (no UI deps) |
| `SharpMUTerm.Graphics` | Kitty/Sixel encoders, capability probe, half-block fallback, `InlineImagePolicy` (no UI deps) |
| `SharpMUTerm.Scripting` | MoonSharp host + scripting API |
| `SharpMUTerm.Tui` | SharpConsoleUI application |
| `*.Tests` (Core, Graphics, Scripting, Web, Tui) | TUnit |

## Milestone M1 — first task (delivered)

Kept for context; **M1 is done** (see *Repository state* above). As originally scoped:

1. Create `SharpMUTerm.slnx` with the projects above targeting `net10.0`, plus the TUnit test projects.
2. Add NuGet references (see *Other dependency notes*).
3. Runnable stub: connect over TCP (+ optional TLS via `SslStream`, IPv6-capable), pipe received
   bytes through a first-pass `AnsiParser` (SGR: 16 / 256 / 24-bit color), render colored output
   in a SharpConsoleUI window with an input line + history.
4. Unit-test `AnsiParser` and the telnet-session wrapper in `SharpMUTerm.Core.Tests`.

## Verification

- Primary signal: `dotnet build SharpMUTerm.slnx` plus all five suites (see *Building and testing*).
  Keep coverage in `SharpMUTerm.Core.Tests` — ANSI/SGR parser, telnet round-trips, engines.
- **The TUI is verifiable headlessly** via the snapshot pipeline above; a claim about layout or
  chrome should be backed by a rendered frame you actually looked at, not by reading the markup.
- **Kitty graphics cannot be rendered here.** Treat that layer as build-verified and
  capability-probed, never visually confirmed, and make sure it degrades cleanly when no protocol is
  available — this environment is exactly that case. `SHARPMUTERM_GRAPHICS=halfblock` makes the
  `web` view draw a real decoded picture as half-block cells, which is the closest available look.

## Working conventions

- Branch from `main`; open a **PR**. Do **not** commit directly to `main`.
- Follow `.editorconfig`: file-scoped namespaces, 4-space C#, LF line endings.
- Keep commits focused with clear messages.
