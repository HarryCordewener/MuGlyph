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
- **TUI base:** **SharpConsoleUI** (`nickprotop/ConsoleEx`, stable, net8/9/10) — a compositor-based
  framework with split layouts, tabs, resizable/mouse-draggable windows, Spectre-style markup, and
  a **native Kitty graphics protocol** (+ Sixel/half-block) for inline images. Replaced Terminal.Gui
  v2 (which was prerelease with an `[Obsolete]` mid-migration API); the switch is contained to
  `MuClient.Tui` because `MuClient.Core` is UI-agnostic.
- **Scripting:** Lua via **MoonSharp** (pure-managed, sandboxed).
- **Inline graphics:** in scope from day one (Kitty Unicode placeholders → Sixel → half-block).
- **Protocols:** aim for all common MU\* protocols. GMCP/MSSP/CHARSET/NAWS/MTTS/EOR via
  TelnetNegotiationCore; **MCCP, MSDP, MXP, and Pueblo are our own app layer.**
- **Config:** fresh JSON schema of our own (worlds hold characters; automation lives in shared
  named trigger sets), versioned with automatic migration between schema revisions.
- **License:** MIT.

## Repository state

**M1 delivered, plus substantial M2–M4 work.** `MuGlyph.slnx` builds all ten projects on
`net10.0`; the solution has **391 passing tests**. In place:

- **Core** — `AnsiParser` (SGR 16/256/truecolor), styled-line + `ScrollbackBuffer` model,
  `TcpTransport` (TLS + IPv6), `TelnetSession` (wraps TelnetNegotiationCore **2.5.3**),
  trigger/alias/macro engines + `IntervalScheduler`, plain-text + HTML logging, versioned JSON
  config (worlds → characters + shared trigger sets, with migration),
  `Theme`/`ThemeLibrary`, and `WorldSession`/`SessionManager` orchestration.
- **Graphics** — Kitty encoder + Unicode placeholders, Sixel + half-block fallbacks, capability
  probe (no UI dependency).
- **Scripting** — sandboxed MoonSharp `ScriptHost` (world/output/trigger/alias/timer/gmcp/log).
- **Tui** — **SharpConsoleUI** app: a `TabControl` of output windows (main + trigger-routed **spawn
  windows** + web view, with unread badges), each a `MarkupControl` fed StyledLine → Spectre-style
  markup via `MarkupFormatter` (clickable `[link=…]` MXP/Pueblo/web spans); a `PromptControl` input,
  status line, `Ctrl+Q` quit, NAWS-on-resize. The tab/pane set is driven by the tested `Core.Workspaces`
  model, with **splits** (thin single-line dividers) and the **connection rail** now rendered as well.

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
- **SharpConsoleUI** (package `SharpConsoleUI`, repo `nickprotop/ConsoleEx`): app is
  `ConsoleWindowSystem(new NetConsoleDriver(RenderMode.Buffer), new ConsoleWindowSystemOptions())`;
  build windows/controls with the fluent `WindowBuilder`/`Controls` factories; `AddControl` is
  builder-time (keep control refs and mutate at runtime). Marshal background work with
  `system.EnqueueOnUIThread`; global keys via `RegisterGlobalShortcut`; `system.Run()` blocks the
  loop, `RequestExit(code)` ends it. Text is Spectre-style markup (`[bold #rrggbb on #rrggbb]…[/]`,
  `[[`/`]]` escaping, `[link=url]…[/]` → `MarkupControl.LinkClicked`). A **headless** sandbox can't
  run `NetConsoleDriver` (no console) — the Tui is build-verified + unit-tested (`MarkupFormatter`);
  visual verification is on the maintainer's machine.

## Architecture rule (non-negotiable)

`MuClient.Core` stays **UI-agnostic and fully unit-testable**. All transport, telnet, parsing
(ANSI/MXP/Pueblo), GMCP/MSDP routing, scrollback, and trigger/alias/macro engines live there.
SharpConsoleUI is referenced **only** from `MuClient.Tui`.

Planned solution layout:

| Project | Responsibility |
|---|---|
| `MuClient.Core` | Transport, telnet, ANSI/MXP/Pueblo parsers, GMCP/MSDP routing, scrollback, engines, logging (no UI deps) |
| `MuClient.Graphics` | Kitty graphics protocol, capability probe, Sixel + half-block fallbacks, `GraphicsView` |
| `MuClient.Scripting` | MoonSharp host + scripting API |
| `MuClient.Tui` | SharpConsoleUI application |
| `*.Tests` (Core, Graphics, Scripting, Web, Tui) | TUnit |

## Milestone M1 — first task (delivered)

Kept for context; **M1 is done** (see *Repository state* above). As originally scoped:

1. Create `MuGlyph.slnx` with the projects above targeting `net10.0`, plus the TUnit test projects.
2. Add NuGet references (see version notes below).
3. Runnable stub: connect over TCP (+ optional TLS via `SslStream`, IPv6-capable), pipe received
   bytes through a first-pass `AnsiParser` (SGR: 16 / 256 / 24-bit color), render colored output
   in a SharpConsoleUI window with an input line + history.
4. Unit-test `AnsiParser` and the telnet-session wrapper in `MuClient.Core.Tests`.

## Dependency notes / traps

- **.NET 10 SDK** may need installing in the sandbox (currently RC, e.g. `10.0.100-rc.1`).
- **SharpConsoleUI** — stable release, multi-targets net8/9/10; MIT. Provides split layouts, tabs,
  resizable/mouse windows, and native Kitty graphics, so the multi-pane workspace and inline images
  ride on the framework rather than being hand-drawn.
- **TelnetNegotiationCore 2.5.3** (the version in use) has a fluent builder API and now negotiates
  MCCP/MSDP/MXP itself, on top of the base negotiation (TELOPT, GA, TTYPE/MTTS, EOR, NAWS, CHARSET,
  MSSP, GMCP). **Pueblo and the ANSI/MXP/Pueblo _parsing_ remain our layer** — the library does the
  option handshake, not the payload parsing. `TelnetSession` sets the init-only `CallbackOnByteAsync`
  reflectively to see raw bytes (incl. unterminated prompts); a first-class `OnByte` builder hook is a
  good upstream PR. (Note: the repo owner authored this library, so extending it directly is on the
  table — propose it via PR rather than assuming.)
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
