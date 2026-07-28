# Plan: Hyper-Modern Cross-Platform TUI MU\* Client (BeipMU-class)

## Context

BeipMU is the best-in-class **Windows-only** MU\* (MUSH/MUCK/MUD) client, but it's Win32/WPF and doesn't run natively on Linux or in modern GPU-accelerated terminals. The goal is a new client, written in **C#/.NET**, that reaches BeipMU feature parity while running as a **TUI inside GPU-accelerated terminals** (Kitty, WezTerm, Ghostty) on **Windows and Linux**.

Key reframing from research: **"GPU-enabled" is a property of the terminal emulator, not our app.** Any TUI running inside Kitty/WezTerm/Ghostty gets GPU-accelerated glyph rendering for free. Our job is to (a) emit rich truecolor/styled text and (b) use the **Kitty graphics protocol** (escape sequences) for inline images/maps, with graceful fallbacks. Both are fully achievable from managed C#.

### Locked decisions (from planning Q&A)
- **Rendering base:** Terminal.Gui v2 for windows/input/layout/scrollback + a custom placeholder-based `GraphicsView` for images.
- **Scripting:** Lua via **MoonSharp** (pure-managed, no native deps).
- **Inline graphics:** must-have from day one.
- **Scope:** broad BeipMU parity (phased into milestones below).
- **Target framework:** **.NET 10**.
- **Protocol coverage:** aim for compatibility with *all* common MU\* protocols; **MXP** is first-class, and **Pueblo** (and its enhancements) are explicitly in scope alongside GMCP/MSDP/MSSP/MCCP.
- **Config:** **fresh JSON** schema of our own — worlds (servers) hold **characters**; automation lives in shared, named **trigger sets** that characters opt into by name — versioned with automatic migration between schema revisions.

---

## Research findings (cited)

- **TelnetNegotiationCore 1.0.0** (the requested package) implements: TELOPT (RFC855), GOAHEAD/GA (RFC858), TTYPE/terminal-type (RFC1091), **MTTS**, **EOR** (RFC885), **NAWS** (RFC1073), **CHARSET** (RFC2066), **MSSP**, and **GMCP**. It is a *negotiation* library only — it explicitly does **not** do ANSI, MXP, or Pueblo (those are our app layer). Gaps vs. a full MU\* client: **MCCP2/3 compression**, **MSDP**, and **MXP/Pueblo** are not provided. Repo is described as young/rough. Sources: [NuGet](https://www.nuget.org/packages/TelnetNegotiationCore/1.0.0).
- **Terminal.Gui v2** (gui-cs) is the mature .NET TUI toolkit (Miguel de Icaza). Currently **beta** (v2.0.0-beta.218 era, ~2026), with truecolor and Kitty **keyboard** protocol support. It models the screen as a **cell grid** and has no native concept of the Kitty **graphics** layer — hence the custom `GraphicsView`. Sources: [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui), [V2 milestone](https://github.com/gui-cs/Terminal.Gui/milestone/7).
- **Kitty graphics protocol**: escape-sequence based (`APC _G ... ST`). The **Unicode placeholder** mechanism (rune `U+10EEEE`, image id encoded in cell fg color, position via combining diacritics) lets images live in *real text cells* so they scroll/clip/layout like text — the approach `ratatui-image` and Textual use. Supported by Kitty, WezTerm, Ghostty. Fallbacks: **Sixel**, then Unicode half-block/quadrant. Sources: [Kitty graphics protocol](https://sw.kovidgoyal.net/kitty/graphics-protocol/).
- **BeipMU feature set** to match: regex triggers, aliases, macros/keybinds, spawns (custom routed windows), flexible mapping, stat panes, image viewer, multiple input windows, puppets, ANSI 256 + 24-bit color, Unicode/emoji, HTML logging, SSL/TLS, IPv6, tab autocompletion, scripting. Sources: [BeipMU](https://beipdev.github.io/BeipMU/), [GitHub](https://github.com/BeipDev/BeipMU).
- **MU\* protocol landscape** we must cover at the app layer: ANSI/xterm-256/truecolor, GMCP, MSDP, MSSP, MCCP, MXP, NAWS, MTTS, charset. Sources: [Mudlet supported protocols](https://wiki.mudlet.org/w/Manual:Supported_Protocols).

---

## Architecture

Layered, with a strict separation between **protocol/session** (headless, unit-testable) and **UI**.

```
+-----------------------------------------------------------+
|  UI layer (Terminal.Gui v2)                               |
|   WorldTabView | OutputPane | InputPane | GraphicsView    |
|   MapView | StatPane | SpawnWindow | Dialogs/Settings     |
+-----------------------------------------------------------+
|  ViewModel / App services                                 |
|   TriggerEngine | AliasEngine | MacroEngine | Logger      |
|   ScriptHost(MoonSharp) | MapModel | SessionManager       |
+-----------------------------------------------------------+
|  Session layer (headless, testable)                       |
|   TelnetSession (TelnetNegotiationCore wrapper)           |
|   AnsiParser | MxpParser | GmcpRouter | McccpStream        |
|   LineBuffer / ScrollbackModel                            |
+-----------------------------------------------------------+
|  Transport: TcpClient + SslStream (TLS), IPv6             |
+-----------------------------------------------------------+
```

### Solution structure (proposed)
- `MuClient.Core` — transport, telnet, ANSI/MXP parsers, GMCP/MSDP routing, scrollback model, trigger/alias/macro engines, logging. **No UI deps.**
- `MuClient.Scripting` — MoonSharp host + the scripting API surface (world, output, triggers, timers, gmcp).
- `MuClient.Graphics` — Kitty graphics protocol encoder, capability probe, Sixel + half-block fallbacks, `GraphicsView`.
- `MuClient.Tui` — Terminal.Gui v2 app: windows, panes, key routing, settings UI, wiring.
- `MuClient.Core.Tests` / `MuClient.Graphics.Tests` — xUnit.
- Target **.NET 10** (confirm TelnetNegotiationCore + Terminal.Gui v2 support net10.0; if a dep lags, reference it via `net8.0` compat and keep our own projects on net10.0).

---

## Protocol layer

- **Wrap TelnetNegotiationCore** behind a `TelnetSession` interface (so we can swap/extend it). Use it for: option negotiation, NAWS (report terminal size on resize), MTTS/TTYPE (advertise as e.g. `MUCLIENT`/`XTERM-256COLOR` + MTTS bitvector incl. 256/truecolor/UTF-8/MOUSE), CHARSET (UTF-8), EOR/GA (prompt detection), MSSP, and **GMCP** (routed to a `GmcpRouter` that dispatches JSON packages to subscribers + scripts).
- **Fill the gaps** TelnetNegotiationCore lacks:
  - **MCCP2/3**: intercept the `IAC SB COMPRESS2` negotiation and wrap the inbound stream in a `System.IO.Compression.DeflateStream` (zlib). Design `TelnetSession` so decompression sits *below* the telnet parser.
  - **MSDP**: implement as a subnegotiation handler if the target servers need it (many use GMCP instead — make it optional).
  - **MXP / Pueblo**: first-class app-layer parsers in `Core`. MXP (line-tagged HTML-ish markup → styled spans, clickable links/commands/`<SEND>`, inline images via the graphics layer, secure/open/locked line modes) and **Pueblo** (its HTML-subset predecessor + enhancements) both targeted. Clickable/link infrastructure shared with the graphics layer.
- **ANSI parser** (`AnsiParser`): SGR incl. 256-color and 24-bit truecolor, cursor/erase handling relevant to MU\* output, producing styled spans for the scrollback model. This is ours, not the toolkit's.
- **Prompt handling**: use EOR/GA to keep prompts on the input line rather than scrollback (BeipMU-style).

---

## Rendering & graphics

- **Text UI**: Terminal.Gui v2 provides the window manager, tabbed worlds, dockable panes, scrollback view, multi-input, focus, and truecolor cell rendering. Advertise UTF-8 + truecolor to servers.
- **`GraphicsView`** (`MuClient.Graphics`): renders images (maps, avatars, inline media) using **Kitty Unicode placeholders** so images occupy real cells and scroll/clip via Terminal.Gui's layout. Pipeline: probe capability → upload image once (base64-chunked `APC _G` transmit) → paint placeholder runes/colors into the view's cells → manage image lifecycle (`a=d` delete on close/replace).
- **Capability probe + fallbacks**: query terminal for Kitty graphics; else **Sixel**; else Unicode **half-block/quadrant** approximation; else a text placeholder. Selection is per-session and user-overridable in settings.
- **Map rendering**: `MapModel` (rooms/exits/z-levels) rendered either as box-drawing/Unicode vector art in a normal view *or* as a rasterized image through `GraphicsView` — start with box-drawing (works everywhere), add rasterized mode where graphics are available.

---

## Scripting (MoonSharp / Lua)

- `ScriptHost` embeds MoonSharp with a **sandboxed** environment (no raw IO/OS by default).
- Expose an API: `world.Send()`, `output.Print()/PrintStyled()`, `trigger.Add()`, `alias.Add()`, `timer.Every()`, `gmcp.On()`, `map.*`, `spawn.To()`. Triggers/aliases can be pure-regex *or* call Lua callbacks.
- Per-world script files + a shared global profile; hot-reload on save.
- Trigger/alias/macro engines live in `Core` and are usable without scripting (regex + substitution), with Lua as the power layer.

---

## Milestones (broad parity, phased so each is usable)

**M1 — Usable text client**
Transport (TCP + SslStream TLS + IPv6); TelnetSession over TelnetNegotiationCore (NAWS/MTTS/charset/EOR/GA/GMCP); `AnsiParser` (256+truecolor); scrollback model + OutputPane; InputPane with history + tab completion; tabbed **multi-world** connections; connect/session manager; plaintext + **HTML logging**. MCCP2 decompression.

**M2 — Automation**
`TriggerEngine` (regex, gag/highlight/rewrite/spawn actions), `AliasEngine`, `MacroEngine`/keybinds, timers. Settings UI for all of them. Per-world profiles (JSON).

**M3 — Graphics day-one payoff**
`MuClient.Graphics`: Kitty placeholder `GraphicsView` + Sixel/half-block fallbacks + capability probe. Inline **image viewer**; **map** view (box-drawing first, rasterized where supported); **stat panes** driven by GMCP.

**M4 — Scripting**
MoonSharp `ScriptHost`, scripting API, Lua-backed triggers/aliases, GMCP subscriptions from Lua, hot-reload.

**M5 — Full parity & polish**
**Spawns** (route matched output to named windows), **puppets**, **multiple input windows**, **MXP + Pueblo** parsers (clickable links/commands/`<SEND>`, inline images via graphics layer), MSDP, **BeipMU config importer**, Unicode emoji + `:)`→🙂, smooth-scroll/appearance options, theming, packaging (dotnet single-file for Windows + Linux; optional distro packages).

### M5 progress (delivered)
- **MXP** and **Pueblo** parsers in `Core` (`ILineParser`), selectable per world via
  `WorldDefinition.ContentFormat`; links/commands surface as `SpanInteraction` and are clickable
  in the TUI. **Emoji** substitution (`EmojiSubstitutor`), opt-in per world. GMCP-driven **stat
  line**, **spawn** capture, ReDoS-guarded regex engines, and self-contained **single-file
  packaging** (`docs/PACKAGING.md`) + a tagged release workflow.
- **In-TUI web view** (`MuClient.Web` + `WebView`): fetch a URL or follow an MXP/Pueblo/HTML link
  and read the page as styled, word-wrapped text with clickable in-pane navigation (AngleSharp →
  `StyledLine`s, reusing `SpanInteraction`). `<img>` renders as a labelled link today.

### Still open (M5+)
- Dedicated **spawn windows** and **multiple input windows** (capture + routing hooks exist),
  **puppets**, MSDP-driven stat panes, and the **map** view.
- **Web view enhancements:** render `<img>` inline through the existing `InlineImageRenderer`
  (Kitty → Sixel → half-block) in graphics-capable terminals, and an optional high-fidelity mode
  that snapshots the page with headless Chromium (Playwright) and displays the image via the
  graphics layer.

---

## Key risks & mitigations
- **Terminal.Gui v2 is beta** → pin a known-good beta; wrap it behind our own view interfaces so churn is contained; keep `Core` UI-agnostic so a renderer swap is possible.
- **Graphics/cell-grid friction** → committed to the Unicode-placeholder approach specifically because images become real cells the toolkit already manages; degrade gracefully when unsupported.
- **TelnetNegotiationCore immaturity / missing MCCP-MSDP-MXP** → wrapped behind `TelnetSession`; gaps implemented in our layer; the wrapper lets us replace the library entirely if needed.
- **Terminal capability variance** → probe + user override; never hard-require Kitty graphics for core text use.

---

## Verification
- **Unit tests** (`Core.Tests`): ANSI/SGR parser (256 + truecolor + edge sequences), telnet negotiation round-trips, MCCP decompression against captured zlib streams, trigger/alias regex + action application, GMCP JSON routing.
- **Graphics tests**: Kitty placeholder-sequence encoder golden-output tests; capability-probe fallback selection.
- **Manual/integration**: connect to a public test MU\* (and a local throwaway server) from **Kitty, WezTerm, Ghostty, and a non-graphics terminal**; verify truecolor, prompts on input line, logging, triggers firing, an inline image rendering under Kitty and degrading to half-block elsewhere. Run on both **Windows and Linux**.
- Use `dotnet test` in CI (GitHub Actions matrix: windows-latest + ubuntu-latest).

---

## Open items to confirm before/at M1
- Confirm TelnetNegotiationCore + Terminal.Gui v2 both build against **net10.0** (fallback: consume via net8.0 compat).
- Which servers you actually play on (helps prioritize protocol edge cases; all are targeted regardless).
- Final project/repo **name** (currently scaffolded as `MuGlyph` — trivially renamable).
