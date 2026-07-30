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
- **The command line is ours** (`InputBarControl` + `InputBuffer` + `InputLayout`), and stays ours.
  The framework has two text controls and neither is the answer. `PromptControl` genuinely cannot
  do it — single-line by construction (its setter turns `\n` into a space), one row tall, scrolls
  sideways, and unfocuses on ⏎ with no way to switch that off. `MultilineEditControl` is a capable
  *editor* (wrap, undo, find/replace, mouse, paste, a pluggable gutter) and is worth knowing about,
  but a command line is not an editor: ⏎ has to send rather than insert, the bar grows to its
  content between a floor and a ceiling, it carries a prompt the text indents past, and two of them
  share one caret with per-window drafts and history recall behind it. Expanding ours is the call.
  Offering it upstream once it is genuinely finished and problem-free is a someday, not a plan —
  do not treat it as pending work.
- **License:** MIT.

## Repository state

**M1 delivered, plus substantial M2–M4 work.** `SharpMUTerm.slnx` builds all ten projects on
`net10.0`, with the full test suite passing. In place:

- **Core** — `AnsiParser` (SGR 16/256/truecolor), styled-line + `ScrollbackBuffer` model (a capped
  in-memory ring plus a **file-backed spill**, `FileScrollbackSpill`, so history deeper than memory is
  paged off an ephemeral per-session cache under `$XDG_CACHE_HOME`; absolute line indices, ranged
  reads capped at `MaxRangeLines`, and any disk failure degrades to memory-only. Emphatically **not**
  the session log — that stays `PlainTextLogSink`/`HtmlLogSink`, opt-in and kept),
  `TcpTransport` (TLS + IPv6), `TelnetSession` (wraps TelnetNegotiationCore **2.6.0**),
  trigger/alias/macro engines + `IntervalScheduler`, plain-text + HTML logging, versioned JSON
  config (worlds → characters + shared trigger sets, with migration),
  `Theme`/`ThemeLibrary`, and `WorldSession`/`SessionManager` orchestration.
  - **Automation is live, and that is a push rather than a read-through.** Each engine holds its rules in
    two lists: *configured* (what the active `TriggerSet`s contribute, swapped wholesale by
    `ReplaceConfigured`) and *runtime* (what the Lua bridge's `Triggers.Add` contributed, which a reload
    must not delete). `WorldSession.ReloadAutomation(sets)` re-points all three engines, and
    `SharpMUTermApp.SaveConfiguration` — the single funnel every settings screen commits through
    (`ScreenEdits`) — calls it, so adding a rule on F2 or assigning a set on F5 reaches a *connected*
    session on its next line. It is **not** read-through to the configuration: `Process` runs on the telnet
    read loop and those lists are mutated on the UI thread, so enumerating them there would throw. Reading
    a rule's own fields per match (`Trigger.Pattern` drops its compiled regex on write) is safe and is a
    different thing from reading its membership. A timer's *period* still applies at the next connect —
    re-periodising a running one resets every other timer's phase, and this runs on every committed change.
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
- **Every `[link=…]` payload a pane carries is scheme-tagged by `InteractionKind`** (`LinkPayload`:
  `mux:send:` / `mux:prompt:` / `mux:web:`), and the panes' handler takes the *window id* the click
  came from. Both are security properties, not tidiness. The tagging is disjoint because the
  **parser** decides the kind (`<SEND>` → `SendCommand`, `<A HREF>` → `Hyperlink`) and a world cannot
  choose that — while the hyperlink case passed `href` through bare, `<A HREF="mux:send:@shutdown">`
  was byte-identical to a real `<SEND>` and the click sent it. Never re-introduce a bare passthrough,
  and never add a "probably a URL" fallback for an untagged payload. The window id is what stops a
  link clicked in a background pane sending to whichever character is focused.

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
- **The demo has no live session, so anything a session *writes* has to be written into `DemoScene` by
  hand — and it has to match.** Its saved main-window title is `Corvid` because that is what
  `BindSession` writes (`SessionTitle(session)`); it used to say `main`, and that one word of divergence
  hid the rail repeating the world's name under the character for as long as the rail has existed. Three
  separate bugs have now hidden in this gap. `RailWindowRowTests.TheDemoScenesMainWindowIsTitledTheWayA…`
  holds the two sides together; when you add state the demo fakes, pin it against the live writer the
  same way.
- **Views:** `worlds`/`settings`, `triggers`, `route`, `highlight`, `aliases`, `timers`, `keypad`,
  `set`, `textansi`, `input`, `logging`, `password`, `freeze`, `spawn`, `split`, `move`, `drag`,
  `history`, `history-search`, `history-search-filter`, `draft`, `draft2`, `menu`, `menu-split`,
  `messages`, `quit`, `connections` (**two connections on one world** — the one view where the header's
  fraction, the rail's dots and the quit prompt's count are all visible together and all have to agree;
  every other view has at most one character connected per world, which is what hid a header dividing
  connections by *worlds* and a quit prompt reducing them to distinct world names), `deletions`, `web`,
  `rail-long`, `scrollback`, `scrollback-up`, `freeze-scrollback`,
  `focus`/`focus-moved` (a split *and* a second command line — the one geometry showing a focused pane
  beside an unfocused one and an armed bar above an idle one, before and after a real ⌃→), plus the
  default workspace
  (no `--view`). Any settings screen also takes a `-edit` suffix, which opens it and drives real
  keys in so the frame shows a field mid-edit. State toggles: `collapsed`, `prefix`, `timestamps`.
- **A snapshot never writes configuration.** `SharpMUTermApp` takes its `save` action from the caller and
  the snapshot path passes none, so an app that isn't the live entry point owns no file. That matters
  because the settings screens persist each committed change as it is made: without the gate, a
  `--demo-config` frame that drove a key into a field (`logging-edit`, `keypad-edit`, `deletions`) would
  write the demo worlds straight over your own `config.json`. It now also protects a **second** file:
  character passwords are saved in `secrets.json` beside the config (`SecretsStore`, `0600`), with
  `config.json` carrying only a meaningless `passwordRef` GUID, so a save writes a secret-bearing file too.
  `CommandSurfaceSettingsTests.AnAppWithNoSaveActionPersistsNothing` is the pin.
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
- **The rail's width is derived from its widest row, so the rail must be re-measured whenever its rows
  change — not only when the pane area is rebuilt.** `RefreshRail` recomputes it and resizes the sidebar's
  own grid column (`ApplyRailWidth`); the width was once computed only in `BuildWorkspaceRow`, so the
  startup retitle poured longer rows into a column sized for shorter ones and the framework **wrapped**
  them. A wrapped rail row is what got reported as "the sidebar looks broken". Rows are also **elided**
  to `RailMaxWidth - RailMargin` before they are measured (`RailRenderer.Render`'s `maxWidth`), because the
  width is *clamped*: without that, any label past the clamp — a web page's title, most easily — wraps no
  matter how carefully the column is sized. The width feeds per-pane NAWS through the pane rectangles, and
  that report rides the frame (`PostBufferPaint → ReportPaneSizes`), so nothing needs to announce it here.
- **A rail window row is `what` then `where`, and neither column may wear the other's word.** A
  character's own session window reads `main` (`RailWindowLabel`); its title names the *connection*, which
  the row's own ancestors — its character, under its world — have already said. The hosting-pane column
  exists only in a split (one pane, one possible answer) and spells panes `pane N`, because it used to call
  the first pane `main` too and `▪ main   main` is two meanings in one line.
- **Focus is indicated by recolouring what is already drawn — never by spending a cell.** Per-pane NAWS
  is derived from the pane rectangle (`PaneOutputRects`), so a border, gutter or marker column that only
  the focused pane has would re-announce a different terminal size to every connected server on every
  focus change and reflow the game's own output. The cues are the pane's own plane
  (`WorkspacePalette.Focus`), the active tab's chip colour (`TabControl.Active*BackgroundColor`), and a
  `▌` in the tab *title* — all zero-cost. `FocusIndicationTests.MovingFocusDoesNotMoveAnyPaneRectangle`
  is the test that stops this being "improved" into a border. Colours live in `WorkspacePalette`, whose
  constants are all derived from a `ScreenPalette` pair so the workspace and the settings screens share
  one idea of what focus looks like; the focus step is `CursorBg ÷ EditBg`.
- **The Ctrl+arrows move pane *selection*, not keyboard focus — but selection carries the session.** The
  pin (`FocusChanged → PinFocusToArmedBar`) is untouched: typing always lands in the armed command line
  wherever you have navigated to. That is a fact about which *control* gets a keystroke, and it says
  nothing about **which character the bar talks to**; the first cut of these keys reasoned "it never moves
  focus, so no third piece of state is needed" and left the command line pointed at the world you had
  navigated away from. Keep the two separate and keep both.
  `TryFocusKey` sits in `HandleWindowKey` **after** `DispatchMacro` (so `MacroKeys.Verdict` reporting a
  macro on `Ctrl+Left` as live stays true) and **before** `TryScrollKey`/`TryRecallKey` and the command
  line (which would otherwise eat them — `TryRecallKey` ignores modifiers). Word movement moved from
  `Ctrl+←/→` to `Alt+←/→` to make room. Vertically the panes and the bars are one ladder: ⌃↓ off the
  last pane arms the second command line, ⌃↑ leaves it (the second bar is per *window*, so the ladder is
  taken from a pane whose window has one).
- **`SharpMUTermApp.Activate` is the one activation path, and activating a window activates its session.**
  Every gesture that brings a window forward goes through it: a tab click (`OnTabChanged`), a rail or ⌃P
  entry, a character switch, an MXP `PROMPT`, the web view, and both movers of pane selection (`FocusPane`
  for ⌃arrows and the ⌃P `Focus pane …` entries, `CyclePane` for ⌃O and ⌃B o). They were five paths and
  they disagreed — the pane movers and the tab click reloaded the drafts but left `_active` behind, so
  typing after navigating went to the world you had left. It does four things: resolve and adopt the
  session (`AdoptSessionOf`), select the pane's tab, `ChangeWindow()` (drafts, second bar, history
  cursors), and `SyncToFocusedPane()` (indicator, scrollback segment, NAWS). Re-entrancy through the
  framework's own `TabChanged` is guarded by `_activating`.
- **Whose window is this? One resolver, `WindowSession`: the session printing into it, else the owner the
  workspace records, else refuse.** There is no third arm falling back on `_active` — that fallback is the
  bug, in both shapes it has had (a link clicked in a background pane sending to the focused character; a
  pane selection moving without the bar following). A window that resolves to nothing keeps the bar where
  it is and `Notice()`s that it did, naming where ⏎ still goes — bounded through `Snippet`, because a
  window title can be a *world's* text (the web view is titled from the page it loaded). It is quiet only
  when there is no redirect to report: a window already owned by the active character, or no active
  session at all. **Ownership is recorded on every path that binds or adopts a window** (`BindSession` and
  `OpenSessionWindow`), because the main window is built before any session exists and the rail and this
  resolver both read ownership.
- **The scrollback keys are routed from `PreviewKeyPressed`** (`TryScrollKey`), and the wheel from the
  driver (`ScrollPaneUnderPointer`), for the same reason everything else in this window is: focus is
  pinned to the armed bar, so `ScrollablePanelControl.ProcessKey` — which returns false unless it has
  focus — would never see a key.
- **Control chords collapse onto their ASCII bytes, so some are unbindable.** `AnsiInputParser`
  decodes no CSI-u and enables no `modifyOtherKeys`: `Ctrl+H` arrives as `Backspace` with
  `control: false` (byte 0x08), and I/M/J are Tab/Enter/Enter — the app cannot even tell the modifier
  was held, so binding those breaks the plain key instead. **`Ctrl+⏎` and `Shift+⏎` are the same
  problem and cannot be bound at all**: CR (0x0D) and LF (0x0A) both become a bare `ConsoleKey.Enter`
  with no modifier bits. They stay in `InputBarControl`'s key table because the Windows
  `Console.ReadKey` path does report them, but **no surface may advertise them** — that is
  test-enforced (`AdvertisedKeyHonestyTests`). `MacroKeys.Verdict` is the readable form of all this.
- **ESC + a control byte arrives as two keys, and that is a chord you can reassemble.** Only
  `ESC` + a *printable* byte becomes a single Alt chord; `ESC` + a control byte is emitted as
  **two** key events (`AnsiInputParser.ProcessEscape`) — which is why `Alt+Backspace` is not
  available. **`Alt+⏎` is, though**, and it is the newline chord: `SharpMUTermApp.TryAltEnter` pairs
  an Escape with an Enter arriving inside the framework's own `UnixStdinReader.EscTimeoutMs` (50 ms)
  and hands the bar a synthetic Alt+Enter. It is safe because Escape in the command line is a genuine
  no-op and every other meaning of Escape is handled earlier in `HandleWindowKey`; and it is reliable
  because both halves land in *one* read, one parse and one dispatch batch (a terminal writes `ESC CR`
  in a single write), so the observed gap is microseconds, not milliseconds. ⌃L is kept as the second
  spelling. **Getting `Ctrl+⏎`/`Shift+⏎` properly needs the Kitty keyboard protocol, and that cannot
  be done consumer-side** — see below.
- **The input stack cannot be extended from here.** Enabling the Kitty keyboard protocol is trivial
  (`IConsoleDriver.WriteClipboardOsc52` is a de-facto public raw-escape emitter, and `Start`/`Stop`
  already pair `CSI ?2004h`/`l` for bracketed paste). *Decoding* it is the wall: `AnsiInputParser`,
  `UnixStdinReader`, `InputEvent` and `TerminalRawMode` are all `internal`; `NetConsoleDriver` has
  **zero** virtual members, a private `WriteOutput`, field-like events a subclass cannot raise, and it
  constructs its parser and reader as *locals* inside `Start()`. So enabling reporting without a
  matching decoder makes the affected keys **vanish silently** (`DispatchCsi`'s `default:` emits
  `UnknownSequenceEvent`, which `UnixStdinReader` drops). Owning input means a from-scratch
  `IConsoleDriver` (~900–1400 lines re-authoring internal termios + parser logic). The cheap unblock is
  upstream: make `AnsiInputParser`/`InputEvent` public and add an `UnknownSequenceHandler` hook, or add
  an input-reader factory to `NetConsoleDriverOptions`. ~15 lines there; do not try it from here.
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
- **Text encoding is CHARSET's answer, not a setting** (`SessionEncoding`, `TelnetSession.CurrentEncoding`).
  A world's `encoding` is `auto` by default — state the app's `CharsetOrder`, decode with whatever RFC
  2066 settles on — and naming one is an *override*: still offered at the head of the order so a
  cooperative server agrees, but used regardless of what it says. Four things about this library will
  bite you, and all four already have:
  - **`TelnetInterpreter.CurrentEncoding` defaults to `Encoding.ASCII`**, and that default is not inert:
    it is handed to `CallbackOnByteAsync`/`CallbackOnSubmitAsync` for every byte and used for GMCP/MSDP/
    MSSP and everything we send. On a server that never negotiates CHARSET — most MU\* servers — every
    byte above 0x7F became `?`. `TelnetSession` seeds that property (reflectively, `internal set`, the
    same way `CharsetProtocol` itself writes it) with the head of the stated order.
  - **The encodings we state must be the platform provider's own instances** (`Encoding.UTF8`, *not*
    `new UTF8Encoding(false)`). `CharsetProtocol` ranks a server's offer by `IndexOf` over our list
    against encodings from `Encoding.GetEncodings()`, and `UTF8Encoding.Equals` compares the BOM flag —
    so a BOM-less instance matched nothing, scored −1, and sorted *below* every charset that did match.
    A `GetBytes` never emits a preamble, so the BOM that instance was avoiding was never at risk.
  - **The interpreter's `CurrentEncoding` is updated *after* the read batch returns**, so polling it is a
    batch late; `CharsetProtocol.OnCharsetChange`'s own argument is the prompt, authoritative signal.
    But that callback is **only raised when the server offers a list and we choose** — the direction
    where we offer and the server accepts updates the interpreter silently. Both arms are needed.
    (An `OnCharsetChange` on the accepted path is a good upstream PR.)
  - The seed is a `Clone()` for a reason: it doubles as the "nothing has negotiated" marker by reference,
    and seeding a provider instance would make a successful negotiation of that same charset look like
    the seed.
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
