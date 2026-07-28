# MuGlyph

A hyper-modern, cross-platform **TUI client for MU\*** (MUSH / MUCK / MUD) worlds, built for
GPU-accelerated terminals (Kitty, WezTerm, Ghostty) on **Windows and Linux**.

The goal is [BeipMU](https://beipdev.github.io/BeipMU/)-class feature parity in a terminal-native
client: rich truecolor text, inline graphics, powerful automation, and full MU\* protocol support.

> **Status:** milestone **M1 delivered** (usable text client foundation) with substantial work
> from M2–M4 in place — automation engines, inline-graphics subsystem, Lua scripting, and theming.
> `MuClient.Core` is fully unit-tested (387 tests across the solution). See
> [`docs/PLAN.md`](docs/PLAN.md) for the full architecture and roadmap.

## Why a TUI

"GPU acceleration" is a property of the terminal *emulator*, not the app. Any TUI running inside
Kitty / WezTerm / Ghostty gets GPU-accelerated glyph rendering for free. MuGlyph focuses on emitting
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
| `MuClient.Core` | Transport, telnet, ANSI/MXP/Pueblo parsers, GMCP/MSDP routing, scrollback, trigger/alias/macro engines, logging (UI-agnostic) |
| `MuClient.Graphics` | Kitty graphics protocol, capability probe, Sixel + half-block fallbacks, `GraphicsView` |
| `MuClient.Scripting` | MoonSharp host + scripting API |
| `MuClient.Tui` | SharpConsoleUI application (windows, panes, settings, wiring) |
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
  script), **aliases** (capture-group expansion, multi-command), **macros/keybinds**, and a
  recurring/one-shot **timer** scheduler. User regexes run with a ReDoS match-timeout guard.
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
- **TUI** — a [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx) app: truecolor markup output
  pane with clickable MXP/Pueblo/web links (styled spans → Spectre-style markup), a command prompt
  with history, a GMCP-driven status line, `Ctrl+Q` quit, and NAWS re-advertised on terminal resize.
  The compositor gives split layouts, tabs, resizable/mouse windows, and native inline images for the
  multi-pane workspace (layered on the `Core.Workspaces` model) as it lands.
- **Web view** — an in-TUI text-mode browser (`MuClient.Web`, AngleSharp): fetch a URL or
  follow an MXP/Pueblo/HTML link and read the page as styled, word-wrapped text with clickable
  links you can navigate in-pane. `<img>` shows as a labelled link (graphics-terminal image
  rendering reuses the Kitty/Sixel/half-block pipeline).
- **Packaging** — self-contained single-file publishing for Linux/Windows (see
  [`docs/PACKAGING.md`](docs/PACKAGING.md)); a tagged release workflow builds the binaries.

## Building

Requires the **.NET 10 SDK**.

```bash
dotnet build MuGlyph.slnx -c Release
```

## Running

```bash
dotnet run --project src/MuClient.Tui -- <host> <port> [--tls] [--insecure] [--name NAME]
muglyph --help    # once published
```

In-app: **Up/Down** input history · **Ctrl+Q** quit. (The window and its panes are mouse-resizable.)

## Testing

The test projects use [TUnit], which runs on the Microsoft.Testing.Platform. Run each directly
(the classic `dotnet test`/VSTest path is not used by MTP on .NET 10):

```bash
dotnet run --project tests/MuClient.Core.Tests
dotnet run --project tests/MuClient.Graphics.Tests
dotnet run --project tests/MuClient.Scripting.Tests
dotnet run --project tests/MuClient.Web.Tests
dotnet run --project tests/MuClient.Tui.Tests
```

## License

[MIT](LICENSE) © 2026 Harry Cordewener

[TelnetNegotiationCore]: https://www.nuget.org/packages/TelnetNegotiationCore/
[TUnit]: https://github.com/thomhurst/TUnit
