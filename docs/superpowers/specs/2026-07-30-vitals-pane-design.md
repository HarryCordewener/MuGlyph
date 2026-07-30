# The vitals pane: session data in session-scoped geometry

**Date:** 2026-07-30
**Status:** proposed — design only, nothing here is implemented
**Companions:** [`2026-07-30-structured-server-data-design.md`](2026-07-30-structured-server-data-design.md)
supplies everything this pane reads;
[`2026-07-30-status-bar-design.md`](2026-07-30-status-bar-design.md) §3 explains why none of it is on
the status row. Read that section before this one — it is the argument this document assumes.

---

## 1. What this is

A window that renders one session's structured data — vitals first, arbitrary key/values after — placed
in the pane tree like any other window, sized by the user, bound to one character.

The maintainer's framing:

> "Healthbars etc […] are very notably relevant only to a certain Main Output Window. It may be that we
> just introduce a new Pane type for them, which users can put where they might want them, instead of
> the status bar."

---

## 2. Why a pane and not a row of chrome

Three problems dissolve at once.

**Whose vitals?** The status row is app-global. Vitals are per-character. With two characters connected
— which the `connections` demo view exists to render, and which is a normal way to play — a global row
must pick one, and picking "the active one" makes a readout that silently changes meaning as you
navigate between panes. A window bound to a session has no such question: it shows *its* character's
vitals, always, whether or not you are looking at it.

**Width.** The status row has three spare cells at 80 columns with a split
([status bar](2026-07-30-status-bar-design.md) §2.1), and an overflow there wraps a *sticky* band, which
takes a row off the workspace via `SyncInputHeights`' veto. Gauges are wide. A pane has whatever width
the user gave it and its overflow costs nothing but its own rows.

**Placement.** Someone with one character wants a strip under the output. Someone with three wants three
strips down one side. Someone playing a MUSH with no GMCP at all wants none. Chrome cannot express any
of that; the pane tree already expresses all of it, and persists it.

The cost of this choice, stated plainly: **it is more work than a status cell**, it introduces the first
window in this client whose content is not a line buffer, and it makes the workspace tree carry
something that has to be reconstructed from configuration rather than from a session. §7 is about that.

---

## 3. What it is, in the existing model

**It is a `WorkspaceWindow` with a new `WindowKind`, owned by a session.** No new pane concept is
needed. `WorkspaceWindow` already carries `Kind` and a nullable `SessionKey`, and `Workspace.OpenWindow`
already takes both — the web view is `WindowKind.Auxiliary` with no owner, so a non-session window is
precedent, and a spawn window is `WindowKind.Spawn` *with* an owner, so an owned non-main window is
precedent too. A vitals window is the combination: a new kind, with an owner.

Why a new `WindowKind.Vitals` rather than reusing `Auxiliary`:

- `RailWindowLabel` branches on `Kind != Main` to decide what the rail calls a window, and the rail's
  rule is that a row is *what* then *where* with neither column wearing the other's word. A vitals
  window under its character should read `vitals`, which is a third answer.
- `TabTitles` prefixes a non-main window with `Owner: Name` so a window scattered into another pane stays
  visibly tied to its character. A vitals window wants exactly that.
- `SpawnLine` routing, freeze, timestamps, unread badges and the scrollback keys are all things a
  line-buffer window does and this one does not. A distinct kind is what lets those paths refuse it by
  construction rather than by a check that someone forgets.

**It is bound at creation, not to focus.** The window carries `SessionKey`, and it resolves through
`WindowSession(windowId)` — the one resolver, whose two arms are "the session printing into it" and
"the owner the workspace records", with no third arm falling back on `_active`. A vitals window has no
session printing into it, so it resolves through the second arm, which is exactly the arm
`OpenSessionWindow` records ownership for.

A follow-the-focus mode is defensible and is **deliberately not the default**: it is one more thing
keyed to "the active session", in a codebase that has fixed misdelivery through `_active` three times.
If it is ever added it resolves through `WindowSession(ActiveWindowId())` like everything else, and it
is a per-window setting rather than a global mode — a user with one character loses nothing either way,
and a user with three wants three bound panes, not one that follows them around.

**It reports no window size.** `ReportPaneSizes` iterates `_sessionWindows` — the map from a session to
"the window a session's output lands in", written by exactly one method, `AttachSession`, called from
`BindSession` — and resolves the pane hosting *that* window. A vitals window is never passed to
`AttachSession`, so it is not in the map, so it is excluded from NAWS **by construction**, not by a
check. The same absence is what makes `WindowSession` resolve it correctly: `SessionFor(windowId)`
searches that map, finds nothing, and the resolver falls through to the owner the workspace records —
which is the arm a vitals window is *supposed* to take. That is worth stating because it is the safe property and it
would be easy to break: any future change that made `ReportPaneSizes` iterate *panes* instead of
*sessions* would start telling a server its window is the size of a gauge strip.

What a vitals pane *does* legitimately change is the size of its neighbours. Splitting a pane to make
room shrinks the session's output pane, and that session's server is correctly told the smaller
rectangle. **Creating a vitals pane costs the game real estate and the game is told.** That is right,
and it is why the pane is opt-in rather than something the client opens on your behalf when GMCP shows
up.

---

## 4. What it draws

The data is arbitrary key/values from `SessionData` ([protocol doc](2026-07-30-structured-server-data-design.md)
§5). Servers agree on very little. §8.5 replaced this section's first draft with the model Mudlet's
bundled starter UI uses, which is more robust and is the only one in the survey built by someone who had
met a lot of servers.

### 4.1 A ranked source lock

Four sources can supply a reading, ranked (Mudlet's `sourceRanks`, §8.5):

| rank | source |
|---|---|
| 4 | the per-character configuration (§4.4), when there is one |
| 3 | GMCP |
| 2 | MSDP |
| 1 | the server's own prompt (§4.5) |

**The first source to produce a complete reading owns the pane, and only a higher rank may take it
over** — at which point the lower source's data is dropped rather than interleaved. That rule exists
because a server running both GMCP and MSDP will send overlapping, differently-spelled, occasionally
disagreeing versions of the same number, and a pane that shows both is a pane that shows a contradiction.

The pane names its source in a dim trailing chip (`gmcp`, `msdp`, `prompt`), because "where did this
number come from" is the first question anyone asks when it looks wrong, and there is no other surface
that answers it.

### 4.2 Inference: a dialect table first, a heuristic second

**A dialect table, not a parser.** For each well-known stat, a list of aliases for the current value and
a list for the maximum — the shape Mudlet ships (§8.5):

```
hp   HP    current: hp, health, hits          max: maxhp, max_hp, maxHp, healthmax, health_max, maxhits
mp   MP    current: mp, mana, sp, energy      max: maxmp, max_mp, maxMp, maxmana, maxsp, maxenergy
ep   EP    current: ep, endurance             max: maxep, max_ep, maxEp, maxendurance
wp   WP    current: wp, willpower             max: maxwp, max_wp, maxWp, maxwillpower
mv   MV    current: mv, moves, movement       max: maxmv, max_mv, movementmax
```

Matched case-insensitively, and the *package* is matched case-insensitively too — `Char.Vitals` and
`char.vitals` are both live in the wild (protocol doc §2.1).

`mv` is **last on purpose**, and that is a decision rather than an ordering accident: when a server
supplies more stats than the pane has room for, movement is the one to lose (§8.2).

**Then a heuristic**, for names the table has never seen: a numeric key `X` paired with a key matching
`maxX` / `max_X` / `X_max`, case-insensitively. Unpaired numeric and string keys become plain
`label  value` lines. A world using entirely novel names still gets something legible.

**Never invent a maximum.** A gauge is drawn only when a full reading is known — either a ready
percentage, or both a current and a max. Mudlet states it as "maxima are never assumed, so a stat may
wait for a Maxstats message or a score screen" (§8.2). Until then the stat renders as a number, which
§4.6's ladder already provides for.

### 4.3 What the server already rendered

IRE's `Char.Vitals` carries a **pre-formatted one-line summary** beside the parsed values (§8.5):

```
"string": "H:4500/4800 M:1200/2500 E:15000/16000 W:14000/15000 NL:10/100"
```

For a terminal client that is nearly free and hard to beat: no parsing, no alias table, no assumption
about maxima, and it is what the game's own designers chose to show. When `Char.Vitals.string` (or
`Char.Vitals.String`) is present and the pane is too narrow for gauges, **show it verbatim**. It sits
above inference and below explicit configuration in the rank order, because a user who has configured
rows has said what they want.

### 4.4 Configuration

A per-character list of entries, on F5 beside the trigger sets, each naming a path, a label, an optional
max-path, and a form (`gauge` | `number` | `text`). The three forms are BeipMU's — String, Integer,
Range, where only Range gets a bar and is "mostly useful for attributes with known maximums" (§8.2).

An empty list means "infer" (§4.2); a non-empty one replaces inference entirely, because a list that
*added* to inference would leave the user unable to remove a row they did not want.

**Deferred:** whether a user should be able to write an expression (`hp/maxhp*100`) rather than name two
paths. Expressions want a parser, a sandbox and an error surface; naming two paths wants neither.
Settled by whether anybody hits a server where the ratio is not two paths.

### 4.5 The prompt rung

`WorldSession.CurrentPrompt` already exists: the GA/EOR boundary flushes an unterminated line as a
prompt, and the client already tracks the latest one. That is the bottom rung of §4.1's ladder and it is
the answer to "what does this pane show on a MUSH", because it is the answer every terminal client has
reached (§8.4) — TinTin++'s recommended path is literally to pin the game's own prompt to a reserved
row, unmodified, colours intact.

**Stage 1 does the zero-parsing version: show the latest prompt, as it arrived.** That is honest, it is
never wrong, and it is a genuinely useful pane on a game with a `H:42 M:17>` prompt. Parsing it into
gauges is Mudlet's next rung — "self-sufficient prompt lines (labelled cur/max or percent) are trusted
once they recur" (§8.4) — and it is deferred, because a regex against a game's prompt is a per-game
configuration and belongs with §4.4 rather than with inference.

**Explicitly not adopted:** Mudlet's last resort, which sends `score` once — visibly — to learn the
maxima (§8.4). A client that sends commands the user did not type is a client that will one day send one
at the wrong moment, and this project has an established rule that navigation always succeeds but
*sending* needs a target the user chose. If it is ever wanted it is an opt-in per character, never a
default.

### 4.6 Rendering, and what a gauge is in a terminal

A gauge is a row: a label, a bar of filled and unfilled cells, and the figures. It is not exotic in a
terminal: TinTin++ ships `#draw bar` as a primitive with a min/max and a two-colour gradient, and
MUSHclient's shipped `health_bar.xml` builds a ten-cell gauge out of repeated glyphs (§8.2).

```
hp   ████████████████░░░░  990/1200
ep   ████████████████████  400/400
```

The bar's width is derived from the pane's, so the pane is a normal citizen of the split tree rather
than a thing with a minimum size. Degradation, narrowest first, is the same shape as the status row's
ladder:

| pane width | what it shows |
|---|---|
| wide | `HP  ████████░░░░  990/1200` |
| medium | `HP  ████░░  990/1200` — bar shrinks |
| narrow | `HP  990/1200` — bar dropped, figures kept |
| very narrow | `HP  82%` — figures dropped, ratio kept |
| no maximum known | `HP  990` — a number, never a bar (§4.2) |

The figures outrank the bar, deliberately. A bar tells you roughly; a number tells you exactly, and the
thing people actually do with a health readout is decide whether a number is above a threshold. Mudlet
renders exactly these two forms from the same slot — `HP 73%` when it has only a percentage,
`HP 730/1000` when it has both (§8.2).

The last row is the one that is a *type* decision rather than a width one, and it is BeipMU's rule: a
Range gets a bar because it has a known maximum; an Integer does not (§8.2). Width can take a bar away;
so can the absence of a denominator, and the two paths land in the same place.

**Colour** comes from `WorkspacePalette` and the world's accent, not from a new palette. A gauge that
changes colour by fill level (green → amber → red) is the obvious next thing to want and is
**deferred**: thresholds are a game-specific judgement (30% is critical on one MUD and routine on
another), and a client that picks them itself is inventing information the way the latency meter did.
A configurable threshold per entry is the honest version, and it is a stage-3 feature. Precedent for
both halves: MUSHclient's shipped gauge hard-codes a 20% colour change, and TinTin++ configs in the
wild hard-code 33%/66% — which is precisely the disagreement that says the number is the game's, not
the client's (§8.2).

**A stat that stops being sent disappears.** Mudlet marks such rows `transient` and removes the gauge
rather than leaving it at its last value or at zero (§8.2). An opponent's health is the obvious case:
a bar frozen at 40% after the fight ended is worse than no bar.

### 4.7 With no GMCP at all — the common case

Most MUSHes send nothing structured. A permanently empty pane is worse than no pane. §4.5 gives the
pane something real to show in that case — the game's own prompt — which is the answer every terminal
client in the survey reached (§8.4). Only when there is no prompt either does the pane explain itself:

```
  vitals · Corvid

  Nothing yet.

  This character's server has sent no structured data,
  and no prompt has arrived to fall back on.
  GMCP is optional and most MUSHes do not use it.

  /gmcp  shows what has arrived  ·  ⌃P ▸ Show client messages
```

Four states, and two of them are data:

| State | What the pane shows |
|---|---|
| Session not connected | *not connected* — and the pane persists, because it is part of the workspace, not part of the session |
| Connected, structured data present | §4.2–§4.4 |
| Connected, no structured data but a prompt | the prompt, verbatim, with its colours (§4.5) |
| Connected, neither | the block above |

The governing rule is Mudlet's, adopted verbatim: **"Nothing appears until the game sends something to
show."** (§8.4) The pane never draws a gauge it invented, never shows a zero for an absent value, and
never shows a maximum it guessed.

The `/gmcp` command named there is [protocol doc](2026-07-30-structured-server-data-design.md) stage 1
— it is what makes the empty state *actionable* rather than merely apologetic, and it is the reason
that command is in stage 1 rather than being an afterthought.

---

## 5. Update cost

`SessionData.Changed` fires on the telnet read loop, once per message. The pane marshals with `OnUi` and
repaints.

**Repainting is cheap here and that is not an accident.** A vitals window is a `MarkupControl` whose
whole content is rebuilt with `SetContent` — a handful of rows. That is the *expensive* path for an
output pane, where re-`SetContent`-ing the full buffer on every line is explicitly forbidden
(`CLAUDE.md`: appending re-parses the whole control, the parse cache being keyed on a content version).
It is the *cheap* path here precisely because the content is five rows and not five thousand. Stating it
so that nobody later "optimises" this into an append.

**Rate-limit anyway.** A world sending `Char.Vitals` every combat round at four rounds a second, across
several connected characters, is several repaints a second driven entirely by the wire. The established
pattern in this codebase is the per-pane NAWS report: four writes a second with a **trailing flush**, on
the injected clock, so the settled value always arrives. Use exactly that — the same interval, the same
trailing-flush property, and the same reason (repaints stop, clocks do not).

A repaint that is one frame stale is invisible; a repaint that never comes because the last update was
coalesced away is a gauge stuck at the wrong value. The trailing flush is the part that must not be
dropped.

---

## 6. Creating, placing and closing it

The pane composes with what exists rather than adding a mode.

- **⌃P** entry: *Open vitals pane — Corvid*, one per connected character, in the WORLD group beside the
  other per-character actions. This is the discoverable route and the one the empty-state text can
  point at.
- **⌃B** command: a letter in the prefix map, alongside split and close. This is the route someone who
  arranges panes for a living will use, and it must appear in `PrefixPanel` — the which-key panel and
  the terse strip are one source of truth, so adding it in one place is adding it in both.
- **F5**: a per-character *vitals pane at start* setting, in the same family as `ConnectAtStartup`. A
  character that always wants one should not have to open it every session.

Once open it is a window like any other: it lives in a pane, it can be the only tab or one of several,
it moves between panes, it zooms, `⌃W` closes it, and `WorkspaceState.Capture` persists it.

**Restoring it is the one genuinely new problem.** Every window in a resumed workspace today is either
the main window of a session (rebuilt when the session binds) or a spawn window (rebuilt from its
capture rule) or the web view. A vitals window's content is derived from a *live session's* data, which
on resume does not exist yet. So a restored vitals window comes back in the *not connected* state of
§4.4 and fills in when its character connects. That is the correct behaviour and it needs
`WorkspaceState` to carry `Kind` (it already does) and `SessionKey` (it already does) — so the
persistence side needs nothing new, only the rebuild path needs to know the kind.

**Deferred:** whether closing the *last* pane of a character while a vitals window for it is open should
close the vitals window too. Leaving it is defensible (it is a window you asked for); closing it is
defensible (it is about a character you have finished with). Settled by which one is more annoying,
which needs use.

---

## 7. What this costs

Honest accounting, since the brief asks for it.

- **A new `WindowKind`** touches every switch on kind: `RailWindowLabel`, `TabTitles`, the rebuild path,
  the spawn-routing guard. Each is small; there are several. It is also serialised: the workspace is
  persisted as `AppConfiguration.LastSession` in `config.json`, through
  `ConfigurationStore.SerializerOptions`, which registers a `JsonStringEnumConverter` — so a saved
  workspace will contain `"Kind": "Vitals"` in the user's config file. Writing is
  forward-compatible; *reading* it on an older build is not, because that converter throws on a name it
  does not know. This is a downgrade concern rather than an upgrade one, and the config schema is
  already versioned with migration — but it is the kind of thing that is free to handle now and awkward
  later, so the restore path should tolerate an unknown kind by dropping the window rather than failing
  the whole workspace.
- **A window whose content is not a line buffer** is the first of its kind. `_lines` is keyed by window
  id and holds `PaneLine`s; a vitals window has none. This is less dangerous than it sounds, because
  `_lines` is already populated **lazily** — the entry is created on the first append — and the paths
  that read it already cope with its absence (`freeze` reads `_lines.TryGetValue(…) ? buf.Count : 0`;
  the re-feed falls back to an empty list). So a window with no buffer is already inert to the timestamp
  re-render, freeze and the scrollback replay, and none of them need to name the new kind.
  What that inertness rests on is *lazy creation*, not on a rule anyone wrote down — so the invariant
  worth pinning is "a vitals window never acquires a line buffer", not "these five paths check first".
- **NAWS** is unaffected (§3), but only because `ReportPaneSizes` iterates sessions. That invariant
  should get a test — `NawsPaneReportTests` is the file, and the assertion is that opening a vitals pane
  changes no session's reported size except through the geometry its neighbours lost.
- **The input-height veto** is unaffected: the veto counts header and status rows, and a vitals pane is
  inside the workspace, not in the sticky bands. This is the concrete payoff of not putting vitals in
  chrome.
- **A snapshot view.** `--view vitals` (and a `vitals-empty` companion) so the two states are rendered
  rather than reasoned about. `DemoScene` has no live session, so — per the rule this repository has been
  bitten by three times — whatever the demo fakes must be pinned against what the live writer produces,
  the way `RailWindowRowTests` pins the main window's title.

---

## 8. Prior art

Three findings here change the design above; §8.5 lists them and §4 has been rewritten around them.

### 8.1 Vitals live in their own region, not in chrome — including where nothing forces it

The steer in §2 is the consensus, arrived at independently by clients with no width pressure at all.

**Nexus**, Iron Realms' own web client for games where GMCP is guaranteed, has the richest status bar
in the survey — Help, Level, Gold, Bank, Ping, Target, Messages, News, Day/Night, then client toggles —
and **no vitals in it**. HP and mana live in a *Gauges* cluster above the command line, separately
toggleable alongside Buttons, Balances and Avatar.¹

**BeipMU** puts them in dockable **stat panes**², and its GMCP package makes the separation explicit:
the top-level keys of a `beip.stats` payload *are pane titles*, so the server chooses how many panes
there are.³

**Mudlet** has no status bar at all — verified: no `QStatusBar` anywhere in `mudlet.cpp`,
`TConsole.cpp` or `TMainConsole.cpp`. Session identity lives in profile tabs and vitals live in Geyser
gauges the user positions.⁴

**MUSHclient** draws its health bars onto the *info bar*, a row explicitly distinct from the status
line, and the distinction is exactly ours: the status line "is separately maintained for each world",
the info bar "is shared between all worlds".⁵

### 8.2 The gauge/number/nothing ladder is established, all three rungs

**Gauge → number is a type decision, and BeipMU states the rule most cleanly.** Its stat types are
**String** ("displayed exactly as it is"), **Integer**, and **Range** — "there is a lower and upper
value for the range and **this enables showing it as a progress bar. Mostly useful for attributes with
known maximums**".² Its GMCP payload picks per stat: `"range": {"value":823,"max":1000}` renders a bar,
`"int":123456` and `"string":"30/60"` render text.³ Same field, same screen, decided by whether a
maximum exists. That is §4.3's ladder, expressed as a schema.

**Number → nothing is TinyFugue's universal idiom** — every default status field is
`<condition> ? "<text>" : ""`, and the slot's width is held open by padding.⁶ Nothing ever prints a
zero. MUSHclient does the same for connected time.⁷

**Refusing to invent the denominator** is Mudlet's rule, and it is the sharpest statement of the
principle: "a gauge is only painted once a full reading is known: either a ready percentage, or both a
current and a max (**maxima are never assumed**, so a stat may wait for a Maxstats message or a score
screen)."⁸

**Declaring the loser in advance.** Mudlet's bundled starter UI has four gauge slots and five candidate
stats, and the fifth carries a comment: `mv` is "last on purpose: when a game supplies more stats than
there are gauge slots, movement is the one to lose out."⁸ A declared loser is better than an
alphabetical accident.

**Transient stats hide.** The same UI marks its `enemy` row `transient = true`; its gauge disappears
when the value goes away rather than sitting at zero.⁸

And in a terminal specifically, gauges are not exotic: **TinTin++ ships `#draw bar`** as a primitive —
`[HORIZONTAL] BAR {<MIN>;<MAX>;[COLOR]}` over a `row col row col` rectangle, with two 256-colour codes
giving a gradient.⁹ **MUSHclient's `health_bar.xml`** builds a ten-cell gauge out of repeated glyphs
and switches colour below 20%.¹⁰ Both are directly transferable to a `MarkupControl`.

### 8.3 What terminal clients do for a *region*, and how big it may be

**Blightmud** is the closest analogue: `blight.status_height(h)` with `0 <= h <= 5`, and
`blight.status_line(index, line)`.¹¹ Two properties worth copying and one worth avoiding:

- **Client state outranks script content.** "The `(more)` info shown when scrolling will always be
  allowed to occupy space before your custom line when applicable."¹¹ Our equivalent is that the
  status row's scrollback indicator is not negotiable.
- **Overflow truncates**: "Text that is too long will be truncated to fit within the bar."¹²
- **The status area is the first thing dropped for accessibility**: `reader_mode` "Switches to a screen
  reader friendly TUI. **(Does not support status area)**."¹¹ A pane in the split tree does not have
  this problem — it is a pane, and closing it is a normal gesture — which is a small extra argument for
  the pane over a status region.

**TinTin++** reserves regions with `#split {top} {bottom} {left} {right} {input}` — five bars, no
documented row cap — and writes into them with `#prompt`/`#show` at explicit row/column.¹³ Note that
`#split` alone, with the bar left as its default row of dashes, is what many users actually run: the
point is to stop input being clobbered, not to build a dashboard.¹⁴

Both Blightmud's and tmux's ceilings are **five rows**, which is a useful sanity check on how tall a
vitals pane should ever default to.

**Blightmud's bugs are the ones we would hit.** A user wiring `Char.Vitals` to `status_line` reported
the colour running off the end of the bar and, worse, "the cursor would end up on the last character of
the bottom-most status line" — explicitly with no colour codes involved.¹⁵ And resize: "the
status_line updates remain in the middle of my screen… while the actual input and scrolling regions are
updated correctly", with the maintainer conceding "Resizing isn't a common thing. Hence it's flown
under the radar."¹⁶ We inherit neither directly — a pane is arranged by the compositor — but the
lesson is that a hand-placed status region is where terminal clients' resize bugs live, and a pane is
not hand-placed.

### 8.4 The common case: no structured data at all

This is the half of the problem terminal clients are actually built around, and the answer is
unanimous: **the game's own prompt**.

- **TinTin++** ships this as the recommended path — `#split`, then `#prompt`, and "If the `<new text>`
  argument is left empty **the original text is used, including colors**."¹³ Zero parsing. Its manual
  says "To avoid displaying problems it's suggested to use `#prompt` to capture the prompt sent by the
  MUD."
- **TinyFugue's** own documented vitals example parses a *text prompt*, not GMCP:
  `/def -mregexp -h"PROMPT ^H:([^ ]*) M:([^ ]*)> $"` feeding a `hp_mana:7` status field.⁶
- **MUSHclient's** `status_bar_prompt.xml` drives its gauge from `OnPluginPartialLine` — the
  unterminated prompt as it streams in.¹⁰
- **BeipMU's** stat windows are regex-on-server-text first; GMCP `beip.stats` is the optional upgrade.²
- **Mudlet's** bundled starter UI has a four-rung ladder — GMCP, then MSDP, then "self-sufficient
  prompt lines (labelled cur/max or percent) are trusted once they recur", then "as a last resort
  `score` is sent once — visibly — to learn the maxima on games whose prompt only carries current
  values."⁸
- **Even Nexus**, with guaranteed GMCP, keeps `Prompt Lines` and `Last Prompt` display options¹ — and
  IRE's `Char.Vitals` ships a **pre-rendered one-line summary** alongside the parsed values:
  `"string": "H:4500/4800 M:1200/2500 E:15000/16000 W:14000/15000 NL:10/100"`.¹⁷

Two of those are directly actionable for us and §4 now uses both. And Mudlet's governing rule is the
one to adopt verbatim as the empty-state policy: **"Nothing appears until the game sends something to
show."**⁸

### 8.5 What this research changes

**1. The inference rule is replaced by a dialect table.** §4.1 originally proposed detecting paired
numeric keys (`X` / `maxX`). Mudlet's starter UI does something better and much more robust: it carries
an explicit alias list per stat, because servers do not agree on spelling.⁸

```lua
{ key = "mp", label = "MP",
  current = { "mp", "mana", "sp", "energy" },
  max = { "maxmp", "max_mp", "maxMp", "maxmana", "max_mana", "maxMana",
          "maxsp", "max_sp", "maxSp", "maxenergy", "max_energy", "maxEnergy" } },
```

It also watches six spellings of the *package* — `Char.Vitals`, `char.vitals`, `Char.Maxstats`,
`char.maxstats`, `Char.Status`, `char.status` — which is the case-insensitivity requirement from the
[protocol doc](2026-07-30-structured-server-data-design.md) §9 arriving from the other direction. §4.1
is rewritten as alias table first, pairing heuristic as the fallback for names the table has never seen.

**2. A ranked source lock, and a prompt rung under it.** Mudlet's `sourceRanks = { gmcp = 3, msdp = 2,
prompt = 1 }`: the first source to produce a complete reading owns the pane, and only a
higher-ranked source may take it over — at which point the lower source's data is dropped entirely
rather than interleaved.⁸ Our design had GMCP and MSDP in one store with nothing saying which wins;
this settles it. More importantly it opens the rung underneath, which is the answer to §4.4: **this
client already captures prompts** (`WorldSession.CurrentPrompt`, fed by the GA/EOR boundary), so the
no-GMCP case is not necessarily empty.

**3. `Char.Vitals.string` — show what the server already rendered.** IRE ships a formatted one-line
summary in the payload.¹⁷ For a width-constrained terminal client that is close to free: no parsing, no
alias table, no assumption about maxima, and it is what the game's own designers chose to show. It
belongs above inference and below explicit configuration.

**4. Numbers arrive as strings.** IRE sends `"hp": "4500"`; Aardwolf sends `4500`.¹⁷ Confirms the
`TryGetInt` coercion in the [protocol doc](2026-07-30-structured-server-data-design.md) §6.1, and means
the pane must never test the JSON kind to decide whether something is a stat.

### Sources

1. Nexus 3.0 game client and display options: <https://nexus.ironrealms.com/3.0_Game_Client>, <https://nexus.ironrealms.com/3.0_Display_Options>
2. BeipMU stat windows: <https://github.com/BeipDev/BeipMU/blob/master/Documentation/Stat%20Windows.md>
3. BeipMU GMCP (`beip.stats`): <https://github.com/BeipDev/BeipMU/blob/master/Documentation/GMCP.md>, <https://mudstandards.org/gmcp/beip>
4. Mudlet Geyser and gauges: <https://wiki.mudlet.org/w/Manual:Geyser>, <https://wiki.mudlet.org/w/Manual:UI_Functions>, `src/mudlet-lua/lua/geyser/GeyserGauge.lua`
5. MUSHclient `SetStatus` / `Info`: <https://www.gammon.com.au/scripts/doc.php?function=SetStatus>, <https://www.gammon.com.au/scripts/doc.php?function=Info>
6. TinyFugue status line: <https://tf-macros.sourceforge.net/help/tf/topics/status_line.html>; `lib/tf/tfstatus.tf`
7. MUSHclient `doc.cpp` connected-time formatting: <https://github.com/nickgammon/mushclient>
8. Mudlet bundled starter UI, `src/mudlet-lua/lua/base-ui/` (development branch, created 2026-07-25): <https://github.com/Mudlet/Mudlet>
9. TinTin++ `#draw`: <https://tintin.mudhalla.net/manual/draw.php>
10. MUSHclient `plugins/health_bar.xml`, `plugins/status_bar_prompt.xml`, `lua/gauge.lua`: <https://github.com/nickgammon/mushclient>
11. Blightmud `status_area.md` and `settings.md`: <https://raw.githubusercontent.com/Blightmud/Blightmud/dev/resources/help/status_area.md>
12. Blightmud PR #1436: <https://github.com/Blightmud/Blightmud/pull/1436>
13. TinTin++ `#split` and `#prompt`: <https://tintin.mudhalla.net/manual/split.php>, <https://tintin.mudhalla.net/manual/prompt.php>
14. Example minimal config: <https://github.com/amfl/dotfiles/blob/master/conf/init.tin>
15. Blightmud issue #76: <https://github.com/Blightmud/Blightmud/issues/76>
16. Blightmud issue #84: <https://github.com/Blightmud/Blightmud/issues/84>
17. Nexus GMCP `Char.Vitals`: <https://nexus.ironrealms.com/GMCP>; Aardwolf: <https://www.aardwolf.com/wiki/index.php/Clients/GMCP>

**Not verified:** Mudlet's starter UI and Blightmud's tabbed top area are both on development branches;
which released versions ship them is unknown. No user-sentiment data was obtainable.

---

## 9. Staged plan

**Stage 1 — one bound pane, inferred content, no configuration, no gauges.**

- `WindowKind.Vitals`; open via ⌃P only; bound to one character at creation.
- The dialect table and the pairing heuristic (§4.2), the source rank and its chip (§4.1), and the four
  states (§4.7) — **including the prompt rung (§4.5) in its zero-parsing form.**
- Plain `LABEL  value` and `LABEL  cur/max` lines. **No bars.** Numbers are the part people read, and a
  list of numbers is a complete, useful, shippable feature that proves the whole path — store → change
  event → marshal → repaint — with none of the width work.
- `Char.Vitals.string` shown verbatim when present (§4.3) — one lookup, no parsing, and it makes the
  pane immediately correct on every IRE world.
- Rate limit per §5.
- `--view vitals`, `--view vitals-prompt` and `--view vitals-empty`, so all three interesting states are
  rendered rather than reasoned about.

This depends only on stage 1 of the
[protocol document](2026-07-30-structured-server-data-design.md), which is the point: the two stage 1s
together are a client that asks a server for its data and shows you what came back. The prompt rung
means it is also useful on a MUSH that sends nothing, which is the majority case and the one a
GMCP-only stage 1 would have left looking broken.

**Stage 2 — gauges.**

- The bar, the width ladder and the no-maximum rule (§4.6).
- Transient stats disappearing (§4.6).
- ⌃B command and `PrefixPanel` entry.
- Restore-from-workspace-state.

**Stage 3 — configuration.**

- Per-character entry list on F5 (§4.4), with BeipMU's three forms.
- Configurable colour thresholds (§4.6).
- A per-character prompt regex, promoting the prompt rung from "show it" to "parse it" (§4.5).
- *vitals pane at start*.

**Not planned:** follow-the-focus mode, expressions, a shared pane showing several characters at once,
and sending `score` to learn maxima. The first three each reintroduce the "whose" problem this design
exists to remove; the fourth means the client types for you (§4.5). None should be added without a
reason stronger than symmetry.

---

## 10. Decisions deferred

| Decision | What would settle it |
|---|---|
| Follow-the-focus binding as an option (§3) | Whether anyone with several characters wants one pane rather than several. If added, it resolves through `WindowSession(ActiveWindowId())` and is per-window, never a global mode. |
| Expressions instead of two named paths (§4.4) | A real server where the ratio is not two paths. |
| Colour thresholds (§4.6) | They are game-specific — MUSHclient's shipped gauge says 20%, TinTin++ configs say 33/66. A client that picks them invents information; a configurable one does not. Stage 3 either way. |
| Parsing the prompt rather than echoing it (§4.5) | Whether a per-character regex is worth the setting. Mudlet trusts a prompt "once they recur"; that heuristic is cheap to try and easy to get subtly wrong, and echoing is already useful. |
| Whether `mv` is really the right stat to sacrifice (§4.2) | Copied from Mudlet, whose author met more games than we have. Revisit if anyone plays something where movement is the tense number. |
| Closing the pane when its character's last output pane closes (§6) | Use. Both behaviours are defensible and neither is obviously more annoying yet. |
| Whether `Char.Name` should rename the pane's title (and everything else) | Cross-cutting; see [status bar](2026-07-30-status-bar-design.md) §4.3. This pane must not diverge from the rail and the prompt on its own. |
