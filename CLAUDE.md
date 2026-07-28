# CLAUDE.md — MuGlyph agent brief

Guidance for any Claude agent working in this repository. Read this first, then read
[`docs/PLAN.md`](docs/PLAN.md) — the plan is the authoritative architecture + roadmap.

## What this project is

**MuGlyph** is a cross-platform TUI **MU\*** (MUSH / MUCK / MUD) client in **C# / .NET 10**,
targeting feature parity with [BeipMU](https://beipdev.github.io/BeipMU/), running inside
GPU-accelerated terminals (Kitty, WezTerm, Ghostty) on **Windows and Linux**.

"GPU acceleration" is a property of the terminal *emulator*, not this app. Our job is to emit
rich truecolor/styled text and use the **Kitty graphics protocol** (with Sixel + half-block
fallbacks) for inline images/maps.

## Locked decisions (do not relitigate without asking)

- **Target framework:** `net10.0`.
- **TUI base:** Terminal.Gui **v2** (prerelease) for windows/input/layout/scrollback, plus a
  custom placeholder-based `GraphicsView` for images.
- **Scripting:** Lua via **MoonSharp** (pure-managed, sandboxed).
- **Inline graphics:** in scope from day one (Kitty Unicode placeholders → Sixel → half-block).
- **Protocols:** aim for all common MU\* protocols. GMCP/MSSP/CHARSET/NAWS/MTTS/EOR via
  TelnetNegotiationCore; **MCCP, MSDP, MXP, and Pueblo are our own app layer.**
- **Config:** fresh JSON schema of our own (worlds hold characters; automation lives in shared
  named trigger sets), versioned with automatic migration between schema revisions.
- **License:** MIT.

## Repository state

**M1 delivered, plus substantial M2–M4 work.** `MuGlyph.slnx` builds all nine projects on
`net10.0`; the solution has **325 passing tests**. In place:

- **Core** — `AnsiParser` (SGR 16/256/truecolor), styled-line + `ScrollbackBuffer` model,
  `TcpTransport` (TLS + IPv6), `TelnetSession` (wraps TelnetNegotiationCore **2.5.3**),
  trigger/alias/macro engines + `IntervalScheduler`, plain-text + HTML logging, versioned JSON
  config (worlds → characters + shared trigger sets, with migration),
  `Theme`/`ThemeLibrary`, and `WorldSession`/`SessionManager` orchestration.
- **Graphics** — Kitty encoder + Unicode placeholders, Sixel + half-block fallbacks, capability
  probe (no UI dependency).
- **Scripting** — sandboxed MoonSharp `ScriptHost` (world/output/trigger/alias/timer/gmcp/log).
- **Tui** — Terminal.Gui v2 app (truecolor `OutputView`, `CommandInput`, theming, key routing).

### Notes for future agents (learned the hard way)
- **.NET 10 SDK**: install via `apt-get install -y dotnet-sdk-10.0` (the Microsoft CDN is often
  blocked; Ubuntu's repo works). NuGet (`api.nuget.org`) is reachable.
- **Tests use TUnit**, not xUnit — projects are `Exe` on Microsoft.Testing.Platform. Run them with
  `dotnet run --project <testproj>`; `dotnet test` is **not** wired up (MTP opt-in doesn't work on
  this SDK).
- **TelnetNegotiationCore 2.5.3** has a fluent builder API (not the 1.0.0 the plan assumed) and now
  provides MCCP/MSDP/MXP negotiation itself. `TelnetSession` sets the init-only
  `CallbackOnByteAsync` reflectively to get raw data bytes (incl. unterminated prompts) — a
  first-class `OnByte` builder hook is a good upstream PR.
- **Terminal.Gui v2** (2.4.x-develop) dropped `Toplevel`/`TabView`; use `IRunnable`/`Window` and
  override `OnDrawingContent(DrawContext)`. The static `Application` API is `[Obsolete]` mid-migration
  (suppressed via `NoWarn` in the Tui project).

## Architecture rule (non-negotiable)

`MuClient.Core` stays **UI-agnostic and fully unit-testable**. All transport, telnet, parsing
(ANSI/MXP/Pueblo), GMCP/MSDP routing, scrollback, and trigger/alias/macro engines live there.
Terminal.Gui is referenced **only** from `MuClient.Tui`.

Planned solution layout:

| Project | Responsibility |
|---|---|
| `MuClient.Core` | Transport, telnet, ANSI/MXP/Pueblo parsers, GMCP/MSDP routing, scrollback, engines, logging (no UI deps) |
| `MuClient.Graphics` | Kitty graphics protocol, capability probe, Sixel + half-block fallbacks, `GraphicsView` |
| `MuClient.Scripting` | MoonSharp host + scripting API |
| `MuClient.Tui` | Terminal.Gui v2 application |
| `MuClient.Core.Tests`, `MuClient.Graphics.Tests` | xUnit |

## Milestone M1 — first task

1. Create `MuGlyph.sln` with the four projects above targeting `net10.0`, plus the xUnit test projects.
2. Add NuGet references (see version notes below).
3. Runnable stub: connect over TCP (+ optional TLS via `SslStream`, IPv6-capable), pipe received
   bytes through a first-pass `AnsiParser` (SGR: 16 / 256 / 24-bit color), render colored output
   in a Terminal.Gui window with an input line + history.
4. Unit-test `AnsiParser` and the telnet-session wrapper in `MuClient.Core.Tests`.

## Dependency notes / traps

- **.NET 10 SDK** may need installing in the sandbox (currently RC, e.g. `10.0.100-rc.1`).
- **Terminal.Gui v2 is a prerelease** — add with `dotnet add package Terminal.Gui --prerelease`.
  v1 (stable) has a completely different API; do not use it.
- **TelnetNegotiationCore 1.0.0** provides negotiation only (TELOPT, GA, TTYPE/MTTS, EOR, NAWS,
  CHARSET, MSSP, GMCP). It does **not** provide MCCP, MSDP, MXP, Pueblo, or ANSI parsing — those
  are our layer. Do not assume APIs for them exist. (Note: the repo owner authored this library,
  so extending it directly is on the table — propose it via PR rather than assuming.)
- **MoonSharp** — package id `MoonSharp`, pure-managed, no native deps.

## Verification

- Primary signal: `dotnet build` + `dotnet test`. Keep coverage in `MuClient.Core.Tests`
  (ANSI/SGR parser, telnet round-trips, engines).
- A headless sandbox **cannot** visually verify a TUI and **cannot** render Kitty graphics.
  Treat the graphics layer as build-verified + capability-probed, never visually confirmed;
  ensure it degrades cleanly when no graphics protocol is available (the sandbox is exactly
  that case). Real terminal testing happens on the maintainer's machine.

## Working conventions

- Branch from `main`; open a **PR**. Do **not** commit directly to `main`.
- Follow `.editorconfig`: file-scoped namespaces, 4-space C#, LF line endings.
- Keep commits focused with clear messages.
