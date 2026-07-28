# Handoff: MuGlyph multi-pane workspace, spawn windows & settings (M5 UI)

## Overview

A design for MuGlyph's TUI shell at M5 scope: a tmux-style pane tree hosting BeipMU-style
spawn windows, a worlds→characters connection model, trigger sets assignable to characters,
a searchable command surface, and per-tab input drafts.

This covers the UI layer only. It assumes the existing `MuClient.Core` engines
(`TriggerEngine`, `AliasEngine`, `IntervalScheduler`, `ScrollbackBuffer`, `SessionManager`)
and asks for two **schema changes** in `MuClient.Core.Configuration` — see *Schema changes* below.

## About the design files

`Glyph TUI v3.dc.html` is a **design reference written in HTML**, not production code and not
something to port. It is a browser mock of a terminal UI: every "pane border", "block meter"
and "box-drawing glyph" is HTML standing in for what Terminal.Gui v2 will draw as real cells.

The task is to **rebuild these screens in `MuClient.Tui`** using Terminal.Gui v2 views and the
existing `Theme`/`ColorMapper` pipeline. Do not translate the HTML structure; translate the
layout, the interaction model, and the information hierarchy.

Open the file in a browser to interact with it (typing, pane splits, ⌃P, F2–F9 all work).

## Fidelity

**High-fidelity for layout, interaction and information architecture. Deliberately
low-fidelity for colour.**

Every colour in the mock is a literal hex, because HTML has no theme layer. In the real client
these must resolve through `MuClient.Core.Theming.Theme` — do not hardcode the mock's hexes.
The mapping table under *Design tokens* gives the theme field or ANSI index each mock colour
stands for.

Everything else — pane geometry, tab strip behaviour, key bindings, what text appears where,
truncation and overflow rules — is intended as specified.

---

## Schema changes (required before the UI can be built)

Two model gaps between the design and `main`:

### 1. Worlds have characters; a character is the connection

Today `WorldDefinition` carries connection parameters *and* is itself the connection unit.
The design separates them: a world is a **server** (host/port/TLS/encoding), and it holds
**zero or more characters**. A character is what you connect *as* — sessions are keyed
`<world>.<character>`, and one world can have several sessions live at once.

```csharp
public sealed class CharacterDefinition
{
    public string Name { get; set; } = "New Character";
    public string? Password { get; set; }          // keychain-backed; never plain in JSON
    public string? ConnectString { get; set; }     // default: "connect {Name} {Password}"
    public bool AutoLogin { get; set; }
    public string? OnConnect { get; set; }         // ';'-separated commands
    public string? OnDisconnect { get; set; }
    public List<string> TriggerSets { get; set; } = new();  // set names, see below
    public LoggingSettings Logging { get; set; } = new();   // per character, not per world
}
```

`WorldDefinition` keeps `Name/Host/Port/UseTls/AllowInvalidCertificates/LocalEcho` and gains
`List<CharacterDefinition> Characters`. `Triggers`/`Aliases`/`Macros`/`ScriptFiles` move off it
(see below). A world with zero characters is valid and must render as such — it just cannot connect.

`SessionManager.Open` should take `(WorldDefinition, CharacterDefinition, int scrollbackLines)`
and key sessions on `$"{world.Name}.{character.Name}"`.

### 2. Triggers live in named sets, assigned to characters

Today `WorldDefinition.Triggers` is a flat per-world list. The design makes automation a
first-class, world-independent library:

```csharp
public sealed class TriggerSet
{
    public string Name { get; set; } = "New Set";
    public string? Description { get; set; }
    public List<Trigger> Triggers { get; set; } = new();
    public List<Alias> Aliases { get; set; } = new();
    public List<Macro> Macros { get; set; } = new();
    public List<string> ScriptFiles { get; set; } = new();
}
```

`AppConfiguration` gains `List<TriggerSet> TriggerSets`. A character's `TriggerSets` names
select which apply. `TriggerEngine` for a session is composed from the union of its character's
sets — so a "Comms" set can be shared by every character on every world, and a "Trade" set can
be live for one character and dark for another on the same world.

`BeipMuImporter` should emit one set per imported world (named after it) and assign it to that
world's imported character, preserving today's behaviour.

`Trigger.Actions.SpawnTarget` already exists and is what the routing UI edits — no change needed.

---

## Screens

### 1. Main workspace

The whole client. Five regions, top to bottom: header, [rail | pane area], input, status bar.

**Header** (1 row): `☰ glyph·tui` at far left is the menu affordance and opens the command
surface (the caret flips `☰`→`▾` while open). Right side carries a `⌃B` prefix indicator
(shown only while armed), the log indicator (`◉ LOG 1284` / `◉ LOG off`), and a clock.

**Connection rail** (left, ~204 cols wide expanded / 46 collapsed): a two-level tree.

```
┌ CONNECTIONS
▚ Aetherfall
  aetherfall.mux:4201
  ▸ ● Corvid                    3
      ▪ main              p1
      ▪ #public        ✎  3  p2
      ▪ pages             p3
      ▪ +who              p1
    ○ Rookery
▚ Nightmarket
  nightmarket.org:6250
    ○ Sparrow                   2
      ▪ main           closed
      ▪ #trade            2  p2
```

World header carries the world accent as a left spine on the active group. Characters indent
one level with a connected dot (`●`/`○`) and an active marker (`▸`). Windows indent again,
showing unread count, a `✎` if they hold unsent input, and which pane hosts them (or `closed`).
Worlds with no characters print `no characters` rather than rendering empty.

Collapsed (⌃B b, or click the header) it becomes a 46-col strip: per-world separator glyph,
then character initials with status dot and unread count. Clicking still switches character.

**Pane area**: a recursive split tree. Each pane is a bordered box containing a tab strip and
an output view; the focused pane's border takes its character's accent colour.

Tab strip: one tab per window hosted in that pane. Each tab shows a colour dot (its character's
accent), the window name, unread count, `⌁` if the window belongs to a *different* character
than the one currently focused, and `✕` on the active tab only. Tabs keep natural width and the
strip scrolls horizontally when they overflow, with a `»N` counter on the right. Right of that:
`▯▯` split-right, `⌸` split-down, `⤢` zoom.

Below the strip, spawn windows show their capture pattern as a dim line: `⇱ capture ^\[public\]`.

Output view: timestamp column (optional), then styled spans. Trigger-highlighted lines get a
2-col left rule in the trigger's colour plus a tinted background.

Freezing (⌃F) splits the pane horizontally: frozen scrollback above under a
`▲ FROZEN ⌃F` bar, live tail below.

**Input** (grows, min 3 rows): prompt reads `Corvid@aetherfall ›` — bound to the focused
**character**, not the focused pane. Right gutter shows the destination window (`→ main`),
a `✎ pages #public` list of other windows holding drafts, character count, and spellcheck state.

**Status bar** (1 row): connection state, `HP ████░░░░ 78`, `EN ███░░░░░ 54`,
`keepalive ▁▃▅▇ ack 41ms`, then host / encoding / `⌃P palette`.

### 2. Command surface (⌃P, or the header menu)

One surface for both mouse and keyboard; there is no separate menu.

Search field on top (`› type to search commands, windows, characters…`) with a match count
(`12 of 41`). Under it a context strip naming the character every command will act on.
Results are grouped `├ GO TO` / `├ WORLD` / `├ TERMINAL` / `├ LAYOUT`, and ↑↓ walks the
flattened list across group boundaries. ⏎ runs, Esc closes.

The catalog is generated from live state, not static: every non-focused character is a
`Switch to Rookery` entry, every window a `Go to #public` entry subtitled with its owner and
unread count, and stateful commands read their current value (`Pause logging`,
`Unzoom pane`, `Resume scrollback`).

Ranking: substring match beats fuzzy subsequence; a prefix match on the command name ranks highest.

On a narrow terminal it docks to the bottom; otherwise it floats near the top.

### 3. Worlds & Characters (F5) — full screen

Not a dialog. Worlds list on the left (name, address, character count, live count) with
`[+ world]` / `[- del]`. Right side, top to bottom:

- **Header**: world name, address, TLS state, encoding.
- **`├ WORLD`**: name, host, port, security, encoding, keepalive — right-aligned labels,
  bordered value cells.
- **`├ CHARACTERS`**: a table — name, state (`● connected` / `○ offline`), login mode,
  assigned trigger sets — with `[+ add character] [⧉ duplicate] [- remove]`. Empty state:
  *"no characters — this world has nothing to connect with."*
- **`└ CHARACTER · <name>`**: two columns. Left: name, password (keychain), on-connect,
  auto-login, session state. Right: the trigger-set checklist — each row is
  `[x] ▪ Comms — channel + page routing    2 rules`. Toggling assigns/unassigns live.

Footer: `Cancel` / `Save`.

### 4. Triggers & spawn routing (F2)

Two columns. Left: the rule list — enable checkbox, name, pattern, owning set (`▪ Comms`),
and action flags. Right: the editor for the selected rule — pattern field, a **route-to**
list (main inline, or any spawn window), colour swatches, and `[x] highlight line` /
`[x] play sound` / `[ ] gag line`. Editing is live: change a pattern and the next matching
line routes differently.

### 5. Other dialogs

`F3` aliases · `F4` keypad & hotkeys (3×3 keypad grid + binding list) · `F6` timers ·
`F7` text & ANSI · `F8` input & spellcheck · `F9` logging. All are checkbox-list or
table layouts in the same frame; F7/F8/F9 share one options-list body.

---

## Interactions

### Pane management (tmux-style prefix)

`⌃B` arms a prefix — the header shows `⌃B — awaiting | - z o x b m < >` — and the next key acts:

| Key | Action |
|---|---|
| `\|` | split focused pane vertically, moving its non-active tabs into the new pane |
| `-` | split horizontally, same rule |
| `z` | zoom / unzoom focused pane |
| `o` | cycle pane focus |
| `x` | close focused pane |
| `b` | collapse / expand the connection rail |
| `m` | enter **move mode** |
| `<` `>` | reorder the active tab within its pane |

Splitting moves the *other* tabs across rather than duplicating the active one — the common
case is "pull #public out into its own pane".

### Move mode (`⌃B m`) — the keyboard path for window placement

Drag is an accelerator, not the only route. Move mode: the active window lifts, every pane dims
and shows a large target letter (`a`–`j`), and the status bar becomes the prompt
`MOVE #public → [b] split right · a–j pane · ←↑↓→ edge · ⏎ commit · Esc cancel`.

- `a`–`j` or Tab picks the destination pane
- arrows or `hjkl` toggle an edge (splits there instead of adding as a tab); pressing the same
  edge again clears it
- ⏎ commits, Esc cancels

The edge preview reuses the same highlight the drag path draws.

### Mouse

Drag a tab (or a window from the rail) onto a pane: drop in the middle to add it as a tab,
drop within 25% of an edge to split there. Pane dividers drag to resize (min 14% per side).
This requires SGR mouse reporting (modes 1002/1006) — note it degrades on some SSH stacks,
which is exactly why move mode exists.

### Per-tab input drafts

Each tab owns its input buffer. Switching tabs parks the typed text with the tab it was written
in and presents the new tab's buffer; switching back restores it verbatim. Sending clears only
that tab's buffer. Closing a tab keeps its buffer for when the window reopens.

History recall must not destroy a draft: `↑` stashes the live draft before the first recall,
`↓` past the newest entry restores it, and editing a recalled line re-bases it as the draft.
While recalling, the gutter shows `history · ↓ back to draft`.

Held drafts are visible, not silent: `✎` on the tab, `✎` in the rail, and a
`✎ pages #public` list in the input gutter.

### Trigger routing

A line is matched against the union of the session character's trigger sets. First `Gag` wins
and drops the line. Otherwise highlights accumulate, and the last matching `SpawnTarget`
decides the destination window. A line routed to a non-visible window increments its unread
count on the tab, the rail character, and the rail world.

### Other keys

`⌃P` command surface · `⌃F` freeze/resume in focused pane · `⌃L` toggle logging ·
`⌃Tab` next tab in pane · `⌃R` reconnect · `↑`/`↓` history · `F2`–`F9` config · `Esc` close overlay ·
`Shift+Enter` newline in input.

---

## State

Per application:

- `layout` — the pane split tree: `{t:'s', dir:'row'|'col', sizes:[a,b], kids:[…]}` interior
  nodes, `{t:'p', id, tabs:[windowId], active}` leaves. Pruning rule: a pane with no tabs is
  removed, and a split left with one child collapses into that child.
- `focus` — pane id; `conn` — focused session id (`world.character`)
- `zoom` — pane id or null
- `frozen` — per-pane bool
- `drafts` / `stash` — per-window input buffers and the pre-recall stash
- `hIdx` — history cursor, reset to -1 on any tab or pane switch
- `move` — `{windowId, from, target, edge}` while move mode is active
- `railOpen`, `palette` + query + selection, `dialog` / `screen`

Per window: `{id, sessionId, name, kind: main|chan|page|spawn, capturePattern, lines[], unread}`.

## Design tokens

The mock's palette is a stand-in. Map it through `Theme` rather than copying hexes:

| Mock hex | Role | Resolve via |
|---|---|---|
| `#0b0e14` | app background | `Theme.Background` |
| `#c8d0dd` | body text | `Theme.Foreground` |
| `#12161f` | status bar bg | `Theme.StatusBackground` |
| `#8b93a5` | status bar text | `Theme.StatusForeground` |
| `#1e2532` / `#2e394d` | pane + dialog borders | `Theme.Border` |
| `#63c8d8` | accent, prompt, focus ring | `Theme.Prompt` |
| `#98c379` | connected, character names, HP | `Theme.SystemMessage`, ANSI 2 |
| `#8b93a5` on echo | local echo | `Theme.LocalEcho` |
| `#e5c07b` | unread, warnings, patterns, move mode | ANSI 3 |
| `#e06c75` | disconnected, errors, destructive | ANSI 1 |
| `#c678dd` | frozen-split chrome, channel captures | ANSI 5 |
| `#e58fb0` | pages / whispers | ANSI 13 |
| `#d19a66` | poses, second world accent | ANSI 11 |
| `#5b6577` / `#404b5e` / `#3f4859` | dim text, section labels, disabled | derive from `Theme.Foreground` |

Per-world accent colours should be a `WorldDefinition.Accent` field (an ANSI index or `Rgb`),
not hardcoded — the design leans on them to keep windows traceable to their owner once they
scatter across panes.

**Character cells, not pixels.** All mock dimensions are px against a 13px monospace grid;
divide by ~8 for columns, ~20 for rows. Rail 204px ≈ 25 cols expanded, 46px ≈ 6 collapsed.
Header/status/input rows are 1 row each. Minimum pane after a split ≈ 14% of its parent.

**Glyphs used:** `▚ ▸ ▪ ● ○ ✎ ⌁ ✕ ⇱ ▯▯ ⌸ ⤢ ⤡ ▲ █ ░ ▁▃▅▇ ┌ ├ └ »`. All are in the common
box-drawing/geometric ranges; `⌁` and `⇱` are the least safe — substitute if MTTS reports a
narrow charset.

**No rounded corners, gradients, or shadows anywhere.** The mock had them early and they were
removed deliberately: the design must read as a terminal.

## Assets

None. No images, no icon fonts — glyphs only.

## Files

- `Glyph TUI v3.dc.html` — the interactive design reference (open in a browser)
- `support.js` — runtime for the above; not part of the design

Repo files each screen maps to are tabulated in `github.md` at the project root.

## Suggested PR breakdown

1. **Schema** — `CharacterDefinition`, `TriggerSet`, `AppConfiguration.TriggerSets`,
   `SessionManager` keying, `BeipMuImporter` update, migration from v1 config. Tests only.
2. **Pane tree** — split tree model + Terminal.Gui view hosting, dividers, zoom, `⌃B` prefix.
3. **Tab strips & spawn routing** — per-pane tabs, unread, `TriggerEngine` `SpawnTarget` → window.
4. **Rail** — worlds/characters/windows tree, collapse.
5. **Input** — per-tab drafts, draft-safe history, `✎` indicators.
6. **Move mode + mouse drag**.
7. **Command surface**.
8. **Settings screens** — F5 full screen, then F2–F9.

Steps 1 and 2 are the load-bearing ones; everything after is additive.
