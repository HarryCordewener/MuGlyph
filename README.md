<p align="center">
  <img src="docs/logo.svg" alt="SharpMUTerm logo" width="120" height="120">
</p>

<h1 align="center">SharpMUTerm</h1>

<p align="center">A hyper-modern, cross-platform <strong>TUI client for MU*</strong> (MUSH / MUCK / MUD) worlds, built for GPU-accelerated terminals (Kitty, WezTerm, Ghostty) on <strong>Windows and Linux</strong>.</p>

Part of the [SharpMUSH](https://github.com/SharpMUSH) family: **SharpMUSH** is the server,
[**SharpClient**](https://github.com/SharpMUSH/SharpClient) is the graphical client, and
**SharpMUTerm** is the terminal one. It speaks plain telnet, so it connects to any MU\* world —
not just SharpMUSH ones.

The goal is [BeipMU](https://beipdev.github.io/BeipMU/)-class feature parity in a terminal-native
client: rich truecolor text, inline graphics, powerful automation, and full MU\* protocol support.

> **Status:** milestone **M1 delivered** (usable text client foundation) with substantial work
> from M2–M4 in place — automation engines, inline-graphics subsystem, Lua scripting, and theming.
> `SharpMUTerm.Core` is fully unit-tested (514 tests across the solution). See
> [`docs/PLAN.md`](docs/PLAN.md) for the full architecture and roadmap.

## Why a TUI

"GPU acceleration" is a property of the terminal *emulator*, not the app. Any TUI running inside
Kitty / WezTerm / Ghostty gets GPU-accelerated glyph rendering for free. SharpMUTerm focuses on emitting
rich truecolor/styled text and using the **Kitty graphics protocol** (with Sixel and half-block
fallbacks) for inline images and maps.

## Tech stack

- **.NET 10** / C#
- **[SharpConsoleUI](https://github.com/nickprotop/ConsoleEx)** — compositor-based TUI framework: split layouts, tabs, resizable/mouse windows, Spectre-style markup, and a native Kitty graphics protocol (+ Sixel/half-block)
- **[TelnetNegotiationCore](https://www.nuget.org/packages/TelnetNegotiationCore/)** — telnet option negotiation (NAWS, MTTS, CHARSET, EOR/GA, MSSP, GMCP)
- **[MoonSharp](https://www.moonsharp.org/)** — embedded, sandboxed Lua scripting
- Custom app-layer parsers for **ANSI** (256 + 24-bit), **MXP**, and **Pueblo**

## Planned feature set (BeipMU parity)

Multi-world tabs · regex triggers · aliases · macros/keybinds · spawns · maps · stat panes ·
inline image viewer · puppets · multiple input windows · scripting (Lua) · TLS + IPv6 ·
HTML logging · GMCP / MSDP / MSSP / MCCP · MXP + Pueblo · Unicode/emoji.

## Solution layout (planned)

| Project | Responsibility |
|---|---|
| `SharpMUTerm.Core` | Transport, telnet, ANSI/MXP/Pueblo parsers, GMCP/MSDP routing, scrollback, trigger/alias/macro engines, logging (UI-agnostic) |
| `SharpMUTerm.Graphics` | Kitty graphics protocol, capability probe, Sixel + half-block fallbacks, `GraphicsView` |
| `SharpMUTerm.Scripting` | MoonSharp host + scripting API |
| `SharpMUTerm.Tui` | SharpConsoleUI application (windows, panes, settings, wiring) |
| `*.Tests` | TUnit test projects |

## What works today

- **Transport & telnet** — TCP with optional TLS (`SslStream`) and IPv6, wrapping
  [TelnetNegotiationCore] for NAWS/MTTS/CHARSET/EOR/GA/GMCP/MSSP/MSDP/MCCP negotiation.
  Prompt (GA/EOR) detection surfaces prompts separately from scrollback.
- **ANSI parser** — incremental SGR parsing: 16-colour, 256-colour, and 24-bit truecolour
  (semicolon and colon forms), with the usual rendition attributes; non-SGR CSI/OSC sequences
  are recognised and discarded.
- **Scrollback** — bounded, thread-safe styled-line model with change events.
- **Automation** — regex **triggers** (gag / highlight / rewrite / respond / spawn-route /
  script), **aliases** (capture-group expansion, multi-command), **macros/keybinds** (F-keys and
  Ctrl/Alt chords — the numpad is not deliverable through the terminal, and F4 says so per
  binding), and a recurring/one-shot **timer** scheduler. User regexes run with a ReDoS
  match-timeout guard.
- **MXP & Pueblo** — first-class parsers for both markup protocols: tags → styled spans, with
  **clickable** `<SEND>`/`<A>` links and commands (`SpanInteraction`), colours, entities, and
  line breaks. Selectable per world.
- **Emoji** — optional emoticon (`:)` → 🙂) and `:shortcode:` (`:fire:` → 🔥) substitution.
- **Logging** — plain-text and styled **HTML** session logs.
- **Config** — a fresh JSON schema: worlds (servers) hold **characters**, and automation lives in
  shared, named **trigger sets** that characters opt into; versioned with automatic migration.
- **Inline graphics** — Kitty graphics-protocol encoder (incl. Unicode placeholders), Sixel and
  half-block fallbacks, and a capability probe that degrades cleanly when no protocol is present.
- **Scripting** — sandboxed **Lua** (MoonSharp) exposing `world`/`output`/`trigger`/`alias`/
  `timer`/`gmcp`/`log`, with hot-reload.
- **Theming** — yazi-style named themes (Dark / Light / Solarized Dark) with a 16-colour palette
  override and semantic UI colours, serialised to the config as hex.
- **TUI** — a [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx) app rendering the multi-pane
  workspace design over the `Core.Workspaces` model: a **connection rail** (worlds → characters →
  windows with connected/active/unread markers), **split panes** with tabbed windows (tmux-style
  split/close/zoom), a **command surface** (`Ctrl+P`) with ranked GO TO / WORLD / TERMINAL / LAYOUT
  actions, per-world **accent colours**, a GMCP-driven **status bar** with HP/EN meters, and a
  character-bound input prompt + destination/draft gutter. Output is truecolor markup with clickable
  MXP/Pueblo/web spans; NAWS is re-advertised on resize. `Ctrl+N` next window · `Ctrl+O` next pane ·
  `Ctrl+W` close · `Ctrl+Q` quit.
- **Web view** — an in-TUI text-mode browser (`SharpMUTerm.Web`, AngleSharp): fetch a URL or
  follow an MXP/Pueblo/HTML link and read the page as styled, word-wrapped text with clickable
  links you can navigate in-pane. `<img>` shows as a labelled link (graphics-terminal image
  rendering reuses the Kitty/Sixel/half-block pipeline).
- **Packaging** — self-contained single-file publishing for Linux/Windows (see
  [`docs/PACKAGING.md`](docs/PACKAGING.md)); a tagged release workflow builds the binaries.

## Building

Requires the **.NET 10 SDK**.

```bash
dotnet build SharpMUTerm.slnx -c Release
```

## Running

```bash
dotnet run --project src/SharpMUTerm.Tui -- <host> <port> [--tls] [--insecure] [--name NAME]
sharpmuterm --help    # once published
```

In-app: **Up/Down** input history · **Ctrl+N** next window · **Ctrl+W** close window · **Ctrl+Q** quit. (Windows/panes are mouse-resizable; each window keeps its own input draft.)

## Testing

The test projects use [TUnit], which runs on the Microsoft.Testing.Platform. Run each directly
(the classic `dotnet test`/VSTest path is not used by MTP on .NET 10):

```bash
dotnet run --project tests/SharpMUTerm.Core.Tests
dotnet run --project tests/SharpMUTerm.Graphics.Tests
dotnet run --project tests/SharpMUTerm.Scripting.Tests
dotnet run --project tests/SharpMUTerm.Web.Tests
dotnet run --project tests/SharpMUTerm.Tui.Tests
```

## License

[MIT](LICENSE) © 2026 Harry Cordewener

[TelnetNegotiationCore]: https://www.nuget.org/packages/TelnetNegotiationCore/
[TUnit]: https://github.com/thomhurst/TUnit
