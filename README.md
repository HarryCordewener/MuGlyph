# MuGlyph

A hyper-modern, cross-platform **TUI client for MU\*** (MUSH / MUCK / MUD) worlds, built for
GPU-accelerated terminals (Kitty, WezTerm, Ghostty) on **Windows and Linux**.

The goal is [BeipMU](https://beipdev.github.io/BeipMU/)-class feature parity in a terminal-native
client: rich truecolor text, inline graphics, powerful automation, and full MU\* protocol support.

> **Status:** early scaffolding. See [`docs/PLAN.md`](docs/PLAN.md) for the architecture and roadmap.

## Why a TUI

"GPU acceleration" is a property of the terminal *emulator*, not the app. Any TUI running inside
Kitty / WezTerm / Ghostty gets GPU-accelerated glyph rendering for free. MuGlyph focuses on emitting
rich truecolor/styled text and using the **Kitty graphics protocol** (with Sixel and half-block
fallbacks) for inline images and maps.

## Tech stack

- **.NET 10** / C#
- **[Terminal.Gui v2](https://github.com/gui-cs/Terminal.Gui)** — windows, tabs, panes, input, scrollback
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
| `MuClient.Tui` | Terminal.Gui v2 application (windows, panes, settings, wiring) |
| `*.Tests` | xUnit test projects |

## Building

```bash
dotnet build
```

Requires the .NET 10 SDK. (Nothing to build yet — projects land in milestone M1.)

## License

TBD.
