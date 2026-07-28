# Naming: MuGlyph → SharpMUTerm

**Date:** 2026-07-28
**Status:** decided

## Problem

The project carried three names for one product:

| Surface | Name |
|---|---|
| Repo / product | `MuGlyph` |
| Assemblies | `MuClient.*` |
| Binary | `muglyph` |

None of them associated the client with the SharpMUSH family, and none of them said what the
thing *is*. The repo also lived at `harryCordewener/MuGlyph`, outside the `SharpMUSH` org.

## Criterion

In `SharpMUSH`, **Sharp** means "written in C#" and **MUSH** says what it is — a MUSH server.
That second half is what makes the name work. `SharpClient`, the sibling GUI client, is the
counter-example the maintainer flagged: it is a client written in C#, but "Client" doesn't say
what it does or what it connects to.

So the name had to **pay its own rent**: the name alone answers "what is this", with no tagline
doing the work. For this project that means saying *terminal* — the terminal is the actual
differentiator against `SharpClient`, since "client" is already taken.

## Decision

**`SharpMUTerm`** — Sharp (C#) + MU (the worlds) + Term (the terminal).

`MU` rather than `MUSH` is deliberate: the client speaks plain telnet and connects to any MU\*
world (MUSH / MUCK / MUD), so the narrower `MUSH` would both overclaim and, by echoing the
server's name, invite reading it as a server.

## Alternatives considered

| Name | Why not |
|---|---|
| `SharpMUSH.Terminal` | Reads as a first-party component of the server rather than a client in its own right, and the embedded MUSH implies MUSH-only. |
| `SharpMUGlyph` | Keeps the heritage and the existing mark, but "Glyph" evokes rather than states — it fails the rent criterion. |
| `SharpMUTTY` | The PuTTY echo is charming and does pay the rent, but reads as "mutty". |
| `SharpMUSHell` | MUSH + shell is a genuinely good pun; the embedded MUSH is the same server-misread problem. |
| `SharpTerm` | Clean, but on its own reads as a terminal *emulator* — nothing says MU\*. |
| `SharpGlyph`, `SharpScroll`, `SharpRune`, `SharpTome`, `SharpLantern` | Evocative, not descriptive. |
| `SharpConsole` | Collides with the `SharpConsoleUI` dependency. |
| `SharpMU` | Reads as a server; too close to `SharpMUSH`. |
| `SharpPortal` | SharpMUSH already ships a Portal. |
| `MUTermSharp`, `MUSHTermSharp` | `<Thing>Sharp` is .NET's convention for *bindings* (SkiaSharp, GtkSharp). |

## Resulting surface

| Surface | Value |
|---|---|
| Product | `SharpMUTerm` |
| Repo | `SharpMUSH/SharpMUTerm` |
| Assemblies | `SharpMUTerm.Core`, `.Graphics`, `.Scripting`, `.Tui`, `.Web` (+ `.Tests`) |
| Solution | `SharpMUTerm.slnx` |
| Binary | `sharpmuterm` / `sharpmuterm.exe` |
| Config | `~/.config/SharpMUTerm/config.json` |
| Env vars | `SHARPMUTERM_GRAPHICS`, `SHARPMUTERM_SIXEL` |
| Header brand chip | `muterm` |

No published releases existed at rename time, so none of these required compatibility shims.

## Open follow-ups

- **Logo.** `docs/logo.svg` is an octagonal **G** — G for Glyph — whose rationale no longer
  applies. Deliberately left unchanged for now. A caret-and-block-cursor mark (`>▌`) drawn in
  SharpMUSH's vocabulary (45° chamfered heavy strokes, `#00f5b7`, 512×512 flat fill) was
  prototyped and deferred, not rejected.
- **`main`-side assets.** `assets/fonts/MuGlyph.ttf` and `assets/fonts/README.md` were added to
  `main` by PR #3 and do not exist on PR #2's branch, so this rename cannot reach them. They
  need renaming on `main` after PR #2 merges.
- **`SharpClient`.** Renaming it to something that says what it does (`SharpMUApp`?
  `SharpMUMobile`?) would make the client family coherent. Out of scope here; the maintainer's
  call.
