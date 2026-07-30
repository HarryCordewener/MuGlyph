# The status bar: what the client says about itself

**Date:** 2026-07-30
**Status:** proposed — design only, nothing here is implemented
**Companions:** [`2026-07-30-structured-server-data-design.md`](2026-07-30-structured-server-data-design.md)
(where session data comes from) and [`2026-07-30-vitals-pane-design.md`](2026-07-30-vitals-pane-design.md)
(where session data goes). This document owns exactly one row of the screen and argues, mostly, about
what does *not* belong on it.

---

## 1. Why this is three documents and not two

The brief asked for two: the protocol layer and the status bar, with the status bar as the protocol
layer's main consumer. Partway through, the maintainer changed the shape:

> "A quick callout to the matter of Healthbars etc, they are very notably relevant only to a certain
> Main Output Window. It may be that we just introduce a new Pane type for them, which users can put
> where they might want them, instead of the status bar."

That is right, and it dissolves the hardest open question rather than answering it. "Whose vitals does a
shared status row show when two characters are connected?" has no good answer, because every answer is
wrong for somebody; the question only exists because session-scoped data was being put in app-global
chrome. Move it to a pane and the question does not arise.

But it also means the status bar is no longer the protocol layer's main consumer — the pane is. Folding
the pane into this document would bury it under a document whose thesis is *"an immutable fact does not
deserve live real estate"*, and that thesis would then read as though it applied to gauges, which it
does not: a gauge is the most volatile thing on the screen. Two theses, two documents.

So: **three documents, one design.** This one is the smallest and the most conservative, and after this
section it is almost entirely about restraint.

---

## 2. Where we stand — measured

The status row today, from rendered frames (`--snapshot --demo-config`, decoded to a cell grid):

```
80 cols, no split:
● Corvid connected                                 utf-8   15 chars   ⌃P palette

80 cols, one split:
● Corvid connected      utf-8   15 chars   ⌃←→↑↓ pane · ⌃⇧←→↑↓ size   ⌃P palette

80 cols, split + second command line:
● Corvid connected           utf-8   15 chars   ⌃←→↑↓ pane · ⇥ line   ⌃P palette
```

Seven things can appear, in two clusters. Left, pinned: a world-accent dot, the character name, the
connection state. Right, right-aligned: a scrollback indicator (only while the focused pane is scrolled
back), the encoding in force, a character count (which becomes a *back to draft* hint while recalling
history), an optional focus hint, and `⌃P palette`.

Three things have been **removed** from this row in the last two days, and the reasons are the design:

- **A fabricated latency meter** — a heartbeat glyph, a sparkline and a round-trip figure, every part
  of it invented from literals. Removed because the client cannot measure what it was pretending to:
  the keepalive is `IAC NOP`, which by design draws no reply, and telnet's only round-trip primitive is
  TIMING-MARK (RFC 860), negotiated nowhere in this stack.
- **The graphics readout** — the terminal's image protocol. Fixed for the life of the process.
- **`host:port`** — a per-world setting that cannot change while you are connected to that world.

One principle covers all three: **an immutable fact does not deserve live real estate.** A cell that
says the same thing for the whole session is a cell that could have been a gap, and a cell that says
something we cannot actually know is worse than empty.

That principle is not yet fully applied. `NotConnectedMarkup()` still reads
`not connected · Graphics <protocol> · ⌃P palette · ⌃Q quit` — the graphics readout survives on the
not-connected row, which is the one row a new user looks at longest. §6 proposes removing it.

### 2.1 The row's width budget, in cells

| | cells |
|---|---|
| Left cluster (`● Corvid connected`) | 18 |
| Right cluster at rest (`utf-8   15 chars   ⌃P palette`) | 29 |
| `MinStatusGap` | 3 |
| **Floor** | **50** |
| Spare at 80 columns, no split | **30** |
| Spare at 80 columns, with a split | **3** |

That last number is the one that matters. At 80 columns with a split the row is *already full*: the
frame shows a six-cell gap where three is the minimum. And the split+second-bar frame shows the row
**already degrading** — `FocusHints()` returns candidates longest-first and the caller takes the first
that fits, so at 80 columns the `⌃⇧←→↑↓ size` half is dropped and `⌃←→↑↓ pane · ⇥ line` is kept.

There is therefore already a working precedent in this codebase for graceful degradation of this row,
and §5 generalises it rather than inventing something.

### 2.2 What an overflow costs

The status row is a **sticky** band. `SyncInputHeights` derives how tall the two command lines may grow
from `ChromeRows()`, which counts the rows the header and the status line *wrap to*
(`InputLayout.WrappedRows(MarkupWidth(_statusBar.Text), HeaderWidth())`). `PaintStatus` re-runs the veto
on every paint precisely because the row's length changes at runtime.

So a status row that overflows to two rows takes a row off the input area, and at the extreme (80×6) the
two sticky bands over-commit and the status line is arranged off-screen entirely. **An overflow is not
cosmetic. It is a row of the user's workspace.**

### 2.3 The row is the last surface keyed to `_active`

`StatusBarMarkup` reads `_active` — through `ActiveWorld()` for the accent dot and the character name,
and through `EffectiveEncoding()` (`_active?.CurrentEncoding`) for the encoding cell. Meanwhile
`PromptLabel()` reads `SendTarget()`, which is `WindowSession(ActiveWindowId())`.

Those differ in exactly one state, and it is a state the client reaches routinely: a focused pane whose
window has no session of its own — which is every pane of a resumed workspace at startup.
`AdoptSessionOf` deliberately leaves `_active` on the previous world in that case, so the command line
correctly says `no connection ›` while **the status row goes on naming, and colouring itself for, the
character in the other pane.**

This is the same defect class the codebase has fixed three times through `_active`, one surface later.
§6 fixes it.

### 2.4 Whose row is it, with two characters connected?

The `connections` demo view has two characters connected on one world. The header says `2/3 characters`,
the rail marks both, and the status row names **only** the active one. That is not wrong for the things
the row currently shows — the encoding in force *is* a property of one session — but it is exactly why
vitals cannot live here. See §3.

---

## 3. What the row does not show

**Vitals do not go on this row.** Not a gauge, not a number, not a compact `hp 990/1200`. Three reasons,
in order of weight:

1. **Whose?** The row is app-global chrome; vitals are session-scoped. With two characters connected
   there is no correct answer, and "the active one" makes a row that silently changes meaning as you
   navigate — the worst kind of readout, because it is right often enough to be trusted.
2. **Width.** §2.1: three spare cells at 80 columns with a split. `hp 990/1200  ep 400/400` is 24 cells.
   The row would wrap, and §2.2 says what a wrap costs.
3. **It would be empty on most worlds.** Most MUSHes send no GMCP at all. A cell that is blank on the
   majority of connections is a cell that reads as broken.

Vitals go to [the vitals pane](2026-07-30-vitals-pane-design.md), which is bound to one session, has
whatever width the user gave it, and can say why it is empty.

**Nothing else from `SessionData` goes here either.** Room name, exits, channel activity: all
session-scoped, all subject to the same three objections. The status row's relationship to the protocol
layer is that it **has none** — and that is the design, arrived at deliberately, not an oversight.

The one exception under consideration is §4.3.

---

## 4. What earns a cell

The test, applied to everything currently on the row and everything proposed for it:

> **Does it change during a session, is it about the client rather than the world, and is there
> anywhere else on screen that says it?**

| Cell | Changes? | About the client? | Said elsewhere? | Verdict |
|---|---|---|---|---|
| Accent dot | on switch | yes (which world you are looking at) | rail, header | **keep** — it is the colour key the rest of the chrome uses |
| Character name | on switch | yes | header, rail, prompt | **keep**, but see §6 — it must name the *focused* pane's character |
| Connection state | yes | yes | rail dot | **keep** — the rail says *that* it is connected, this says *what state* (connecting / faulted) |
| Scrollback indicator | yes | yes | **nowhere** — the panes carry no scrollbar | **keep**; the most important thing the row says |
| Encoding | yes (on negotiation) | yes | nowhere | **keep** — and its qualifier is the point (`utf-8` vs `utf-8 assumed` vs `iso-8859-1 forced`) |
| Character count | every keystroke | yes | nowhere | **keep** |
| `⌃P palette` | never | yes | nowhere | **keep** — a discoverability anchor is the one immutable thing worth a cell, because it is how everything else is found |
| Focus hint | contextual | yes | ⌃P, `--help` | **keep, conditional** — already only drawn when it fits |
| `Graphics <protocol>` | never | yes | ⌃P | **remove** from the not-connected row (§6) |
| Latency | — | — | — | **stays removed** until it can be measured |
| `host:port` | never | no (the world's) | F5, rail | **stays removed** |
| Vitals | yes | **no — the world's** | — | **never** (§3) |

The `⌃P palette` row deserves its exception stated rather than smuggled. It never changes, so by the
letter of the rule it should go. It stays because it is the entry point to every other surface,
including the ones that would tell you what was removed from this row — a discoverability anchor is a
different kind of thing from a readout, and there is exactly one of them.

### 4.1 Two additions worth considering

**Logging.** A character can be writing its output to a transcript (`WorldSession.IsLogging`). That is
volatile, it is about the client, and it is currently visible only on F9. A one-glyph cell (`Glyphs.Log`)
that appears while the focused pane's session is logging passes the test on all three counts — and
importantly, it costs *nothing* when it is not there, which the rail's reserved-field rule
(`RailRenderer.UnsentFieldWidth`) says is the standard to meet. But this row is right-aligned rather than
reserved-field, so a glyph appearing shifts the cluster left by two cells and nothing else moves. Safe.

**Nothing else.** Unread counts are on the tabs and the rail. Trigger-set membership is on F2 and
`/triggers`. Draft state is the `✎` on the rail. Each of those already has a surface, and the third
column of the test exists to stop this row becoming a summary of the other four.

### 4.2 Two things that would be honest and are not yet possible

- **Time since the last byte.** The latency meter was removed because we cannot measure round trips.
  What *is* measurable with no new negotiation is how long since anything arrived — a liveness signal,
  not a latency one. It earns a cell only when a session has been silent long enough to be worth
  reporting, which makes it a conditional cell rather than a permanent one. **Deferred:** what threshold,
  and whether "quiet" is even interesting on a MUSH where nothing happening is normal.
- **MCCP.** `OnCompressionAsync` fires and logs. Whether compression is on is fixed for the session and
  therefore fails the test outright. Not proposed.

### 4.3 The one place structured data might touch this row

`WorldSession` knows the character's *configured* name. GMCP `Char.Name` knows what the server calls
them, and on some worlds those differ (a puppet, a renamed character, a login that connected to a
different body than the config expected).

If the row is to name a character, naming the one the server thinks it is talking to is more honest than
naming the one the config file guesses. **But it fails the "said elsewhere" column** — the prompt, the
rail and the header all use the configured name, and having the status row alone disagree with three
other surfaces is worse than being consistently approximate.

**Deferred, and it is a real question:** should `Char.Name` *replace* the configured name everywhere —
prompt, rail, header, tab titles — once a session has one? That is a coherent design and a much larger
change than this row. It is settled by whether the divergence is common enough to matter, which is a
question about real worlds. Named here so it is not silently decided by leaving the row alone.

---

## 5. Width and degradation

The row must never wrap (§2.2). Today one segment degrades (`FocusHints`); the rest either fit or
overflow. Generalise it into an explicit priority, evaluated at paint:

**Never dropped, in order:**

1. Accent dot + character + state (left cluster)
2. Scrollback indicator, when scrolled back
3. `⌃P palette`

**Dropped in this order as the row narrows:**

4. Focus hint (already conditional; already degrades within itself, longest-first)
5. Character count → drop entirely. It is the least load-bearing thing on the row, and its information
   is in the bar you are looking at.
6. Encoding: `utf-8 assumed` → `utf-8` → drop. The qualifier goes before the name.
7. Connection state word: `connected` → drop (the accent dot and the rail both still say it). Never drop
   `connecting` or `faulted` — a transitional or broken state is the one worth the cells.

Below that the left cluster is elided to fit, which is the rail's existing answer (`RailRenderer` elides
rows to `RailMaxWidth - RailMargin` *before* measuring, because a clamped width plus an unelided label
wraps no matter how carefully the column is sized). Same rule here.

**At 80 columns** with a split, and with the scrollback indicator up, the row would be:

```
● Corvid connected   ⇈ scrollback 42 · ⌃End live   utf-8   ⌃P palette
```

(`⇈` stands in for `Glyphs.Scrollback`, which is a Nerd Font codepoint; nothing here proposes changing
it.) That fits, with the character count and the focus hint dropped. That is the design target: **at 80
columns everything that cannot be learned elsewhere survives, and everything that can is dropped.**

**A note on measurement.** `MarkupWidth` is what the veto and the right-alignment both use, and the row
contains Nerd Font glyphs (`Glyphs.Scrollback`) whose terminal cell width is a property of the font, not
of Unicode. This is already true today and is not made worse by anything here — but a degradation ladder
that computes "does it fit" is more sensitive to a width that is wrong by one than a row that merely
right-aligns. Any change here should be verified with a decoded frame at 80 columns, not with
arithmetic.

---

## 6. The `_active` fix, and the not-connected row

Two corrections that belong to this row and are independent of everything else in this document:

**The row must resolve through the focused window.** `StatusBarMarkup` should read
`WindowSession(ActiveWindowId())` — the same resolver `SendTarget()`, `OnLinkClicked` and
`AdoptSessionOf` use — rather than `_active`. Three arms, matching `PromptLabel()`'s exactly:

| State | Row says |
|---|---|
| Focused window has a session | that session's dot, character, state, encoding |
| Focused window has none, but the client has a session somewhere | *no connection* — dimmed, no dot, no encoding cell |
| Client has nothing connected | the not-connected row |

The middle arm is new, and it is the whole point: today that state shows another pane's character in
another world's colour while the command line one row below says `no connection ›`.

**The not-connected row loses the graphics readout.** It currently reads

```
not connected · Graphics Kitty · ⌃P palette · ⌃Q quit
```

`Graphics Kitty` is a fact fixed for the life of the process, discoverable from ⌃P, and it is on the one
row a new user reads longest. It fails the same test that removed it from the connected row. Removing it
leaves `not connected · ⌃P palette · ⌃Q quit`, which is the two keys someone in that state needs.

---

## 7. Prior art

The point of this section is that almost every decision above has been made before, usually by a
terminal client, and usually the same way. Where the precedent is better than what §4–§6 propose, §7.6
says so and the proposal changes.

### 7.1 The layout model is already the consensus one

**TinyFugue** is the deepest terminal status-line implementation in existence and its model is: fields
anchored to the left, fields anchored to the right, and **exactly one elastic field absorbing the
remainder**. From the manual: "the fields on its left are pushed to the left side of the screen, the
fields on its right are pushed to the right side of the screen, and the `@world` field uses whatever
space remains in the middle."¹ In `src/output.c` the width of a zero-width field is literally
`columns - status_right[row] - status_left[row]`.²

**tmux** is the same shape — "three parts: configurable left and right sections… and a central window
list"³ — and so is ours: `left` pinned, `rightBar` right-aligned, `gap` in the middle. We arrived at it
independently; it is worth knowing it is the standard answer and not a local quirk.

Both cap the status area at **five rows** (tmux `status 2|3|4|5`;³ Blightmud `0 <= height <= 5`⁴). Ours
is one row and a veto that fights to keep it there, which is the right choice for a client whose whole
screen is the workspace — but the number five is a useful sanity check on any future "let's make it two
rows".

### 7.2 Truncate, never wrap — and how each one does it

There is no doctrine document anywhere that says "this row must not wrap". The property is achieved by
construction, three ways, all citable:

- **Hard-truncate to the field width before writing.** TinyFugue: `if (scratch->len > width)
  Stringtrunc(scratch, width);`² tmux bakes it into the default format itself —
  `#{T;=/#{status-left-length}:status-left}`⁵ — with documented defaults of **10 columns left and 40
  right**.³ Blightmud: "Text that is too long will be truncated to fit within the bar."⁶
- **Reserve the row out of the scroll region**, so ordinary output physically cannot reach it.
  TinTin++'s `#split` carves top/bottom/left/right bars out of the scrolling area.⁷ Ours is a sticky
  band in SharpConsoleUI's `WindowContentLayout`, which is the same idea expressed by a compositor.
- **Author the content to fit.** TinyFugue's manual makes this explicit about its own `@more` field:
  "The expression was carefully written so that it will never be more than 8 characters, because it
  would be confusing to generate something like `More:12345` and then have it truncated to `More:123`."¹

That third one is the discipline this row already keeps — `ScrollbackStatus()` carries a comment saying
a wordier phrasing wrapped the row at 120 columns — and it is worth recognising as a named practice
rather than a scar.

### 7.3 Priority-ordered dropping is real, and airline is the model

§5 proposes dropping segments in a fixed order as the row narrows. **vim-airline** does exactly this,
declaratively:⁸

```vim
let g:airline#extensions#default#section_truncate_width = {
    \ 'b': 79, 'x': 60, 'y': 80, 'z': 45, 'warning': 80, 'error': 80,
    \ }
```

Read as a priority list: line/col survives to 45 columns, filetype to 60, branch and encoding need
~80. **lualine** expresses the same thing as a predicate — `cond = nil`, "the component is loaded when
the function returns `true`", which the community's `hide_in_width` pattern uses as
`function() return vim.fn.winwidth(0) > 80 end`.⁹ Our `FocusHints()` is already the lualine form; §5
generalises it to the airline form.

Two neighbouring techniques worth knowing about, because they are alternatives to dropping:

- **Compress instead of dropping.** TinyFugue's `/status_abbr` is a graded abbreviator — "try to keep
  capitals, beginnings of words, and digits; discard everything else as needed" — and it abbreviates
  the left of a `foo:bar` pair before the right.¹⁰ airline does `foo/bar/baz → f/b/baz`;⁸ lualine does
  `apple/orange → a/orange`;⁹ tmux appends a marker with `#{=/5/...:pane_title}`.³
- **Scroll instead of dropping.** tmux's variable-length window list does not shed items; it keeps the
  focused one visible and marks both trimmed edges: "`list=focus` is the part of the list that should
  be kept in focus if the entire list won't fit… `list=left-marker` and `list=right-marker` mark the
  text to be used to mark that text has been trimmed".³ Defaults are literally `<` and `>`.⁵

Neither applies to our row today — nothing on it is a list — but if the row ever carries a per-session
cluster, tmux's answer is the one to copy, not "drop the least important session".

**vim's numeric rule is the one genuinely novel idea in this area** and it is worth remembering: a
too-wide *number* is not chopped, it is rendered lossily-but-honestly. "Numeric items will be shifted
down to `maxwid-2` digits followed by `>`number where number is the amount of missing digits."¹¹ Our
character count is the only numeric cell and §5 drops it rather than shortening it; that is defensible
because the count is the least load-bearing thing on the row, but the technique exists.

### 7.4 Absence renders as blank, never as a zero — and better, as an explanation

This is the strongest cross-client consensus in the survey and it is exactly the principle §2 derives
from three removals.

**TinyFugue**: every default status field is `<condition> ? "<text>" : ""`, and the slot's width is held
open by `%status_pad` (default `_`).¹² Nothing ever prints a zero. With no session at all the world
field reads `(no world)`; with a world whose socket is closed it prefixes `!`.¹²

**MUSHclient**'s connected-time pane is empty rather than `0s` when not connected, and elides by
magnitude when it is — `%Dd %Hh %Mm %Ss` → `%Hh %Mm %Ss` → `%Mm %Ss` → `%Ss`.¹³

**Mudlet has the single best precedent in the survey**, and it is about the exact problem that removed
our latency meter. Mudlet's latency readout needs telnet GA to measure a round trip. When there is no
GA it does not render `0.000` and does not go blank — it renders the literal token `<no GA>`:¹⁴

```cpp
if (mpHost->mTelnet.mGA_Driver) {
    mpLineEdit_networkLatency->setText(tr("N:%1 S:%2").arg(...));
} else {
    mpLineEdit_networkLatency->setText(tr("<no GA> S:%1").arg(processT, 0, 'f', 3));
}
```

**Degrade to an explanation, not a placeholder.** That is a third option this document did not consider
and §7.6 acts on it.

### 7.5 What the row is *for*, and where vitals actually live

The per-session/global split §3 argues for has an architectural precedent. **MUSHclient** has two rows
and divides them by exactly that axis: the status line "is separately maintained for each world",¹⁵
while the info bar "is shared between all worlds. Switching worlds will not change its contents."¹⁶ One
surface for "this session", one for "everything" — the same distinction, drawn one row apart.

And the decisive data point for §3: **Nexus, Iron Realms' own web client for games where GMCP is
guaranteed, puts no vitals in its status bar at all.** Its status bar is the richest single row
surveyed — Help, Level, Gold, Bank, Ping, Target, Messages, News, Day/Night, then client toggles — and
HP/mana live in *gauges above the command line*, in a separately toggleable cluster.¹⁷ A first-party
client with guaranteed structured data, no width pressure, and every incentive to show vitals
prominently, still did not put them in the status row.

Nobody shows *vitals* for a non-focused session. Several show *activity*:

| client | non-focused session shows |
|---|---|
| TinTin++ | nothing — "only output sent to the active session is displayed"¹⁸ |
| TinyFugue | `@active` = count of sockets with unseen text; `activity_status.tf` upgrades it to a per-world list, each label abbreviated to `status_width(field) / worlds - 5` columns¹⁰ |
| Blightmud | per-tab unread — `[main] │ (chat·3) │ (combat)`⁶ |
| Mudlet | per-profile tab dot (green/amber/grey, red **triangle** for error — a shape change, not colour alone) plus bold on new data¹⁹ |
| BeipMU | per-tab badges: red count (`9+` above 9), logging dot, `⚡` disconnected, `🔇` muted²⁰ |

Ours is the same: the rail's dots, the tabs' unread badges, the header's fraction. The status row is
correct to say nothing about the sessions it is not looking at.

Two implementation details from that table worth stealing. Mudlet's error state is a **triangle**, not
a red dot — a shape change survives a colourblind reader and a terminal theme that flattens hues, and
this project already applies the same reasoning to the armed/idle command line (bold vs dim, "the
second cue the band colour is not asked to carry on its own"). And **BeipMU spends its indicator width
before the label**, then ellipsises the label into what is left, with explicit width accounting.²⁰ That
is the ordering §5's ladder uses.

### 7.6 What this research changes

Four things.

**1. A new hazard: the bottom-right cell.** Our row right-aligns its cluster so the last segment ends at
the final column. On a terminal with automatic margins, writing the last cell of the last row triggers
autowrap and scrolls the screen. ncurses documents three workarounds precisely because there is no
portable way to just do it: write directly if the terminal has no auto-margins; turn auto-margins off
temporarily if it can; otherwise write one cell early and *insert* a character to push the update into
the corner.²¹ **Blightmud hit exactly this** — a user writing a status line reported "the cursor would
end up on the last character of the bottom-most status line", explicitly with no colour codes
involved.²² Whether SharpConsoleUI's `NetConsoleDriver` handles the corner is **not known and is not
something this document can assert**; it is a question for a real terminal, not a snapshot, because a
headless frame has no autowrap. Added to §9 as a deferred item, and it is the one item there that could
turn out to be a visible bug today rather than a future one.

**2. The latency cell comes back — as an explanation.** §4.2 deferred a "quiet since" cell on the
grounds that no threshold is obviously meaningful. Mudlet's `<no GA>` is the better answer to the same
shape of problem: state *why* the number is absent, in the slot where the number would be. Concretely,
if a latency cell is ever wanted, the honest first version is a cell that reads `no timing` — because
telnet's only round-trip primitive is TIMING-MARK (RFC 860) and this stack negotiates it nowhere — and
which becomes a real figure only if TIMING-MARK is ever added. That is a cell that can never lie, it
costs nine columns, and it fails the immutability test in §4, so **it is proposed only as a diagnostic
the ⌃P client-message viewer prints, not as a permanent cell.** The row keeps nothing.

**3. §5's ladder gets thresholds, not just an order.** airline's `section_truncate_width` is a better
shape than "drop until it fits", because a declared threshold is testable at that width and an implicit
one is only testable by bisection. The ladder in §5 should be expressed as a table of
`(segment, minimum row width)` and the snapshot tests should render at exactly those widths and one
column below each.

**4. The not-connected row gains a phrase, not just loses one.** §6 removes `Graphics <protocol>`,
leaving `not connected · ⌃P palette · ⌃Q quit`. TinyFugue's `(no world)` versus `!WorldName` — no
session at all, versus a session whose socket is closed — is a distinction ours collapses. We have the
same two states (`_sessions` empty, versus a session in `Faulted`/`Disconnected`), and the second is
the one where someone wants to know that ⌃D reconnects. Worth one word.

### Sources

1. TinyFugue manual, *status line*: <https://tf-macros.sourceforge.net/help/tf/topics/status_line.html>; in-repo <https://github.com/ingwarsw/tinyfugue/blob/main/lib/tf/tf-help>
2. TinyFugue `src/output.c` (`status_width`, `format_statusfield`): <https://github.com/ingwarsw/tinyfugue/blob/main/src/output.c>
3. tmux(1), *status line* options and FORMATS/STYLES: <https://man.openbsd.org/tmux.1>
4. Blightmud `status_area.md`: <https://raw.githubusercontent.com/Blightmud/Blightmud/dev/resources/help/status_area.md>
5. tmux `options-table.c`: <https://github.com/tmux/tmux/blob/master/options-table.c>
6. Blightmud top-area/tabs, PR #1436 and #1444: <https://github.com/Blightmud/Blightmud/pull/1436>, <https://github.com/Blightmud/Blightmud/pull/1444>
7. TinTin++ `#split`: <https://tintin.mudhalla.net/manual/split.php>; `#prompt`: <https://tintin.mudhalla.net/manual/prompt.php>
8. vim-airline docs: <https://github.com/vim-airline/vim-airline/blob/master/doc/airline.txt>
9. lualine: <https://github.com/nvim-lualine/lualine.nvim>
10. TinyFugue `lib/tf/activity_status.tf`: <https://github.com/ingwarsw/tinyfugue/blob/main/lib/tf/activity_status.tf>
11. Vim `'statusline'`: <https://github.com/vim/vim/blob/master/runtime/doc/options.txt>
12. TinyFugue `lib/tf/tfstatus.tf`: <https://github.com/ingwarsw/tinyfugue/blob/main/lib/tf/tfstatus.tf>
13. MUSHclient `mainfrm.cpp` / `doc.cpp`: <https://github.com/nickgammon/mushclient/blob/master/mainfrm.cpp>
14. Mudlet `src/TMainConsole.cpp`; `getNetworkLatency()`: <https://wiki.mudlet.org/w/Manual:Networking_Functions>
15. MUSHclient `SetStatus`: <https://www.gammon.com.au/scripts/doc.php?function=SetStatus>
16. MUSHclient `Info` / `ShowInfoBar`: <https://www.gammon.com.au/scripts/doc.php?function=Info>
17. Nexus 3.0 game client: <https://nexus.ironrealms.com/3.0_Game_Client>; display options: <https://nexus.ironrealms.com/3.0_Display_Options>
18. TinTin++ session names: <https://tintin.mudhalla.net/manual/sessionname.php>
19. Mudlet `src/TTabBar.cpp` (`TabConnectionIndicator`); <https://wiki.mudlet.org/w/Manual:Profile_Preferences>
20. BeipMU `src/Wnd_Taskbar.cpp`, `src/Connection.cpp`: <https://github.com/BeipDev/BeipMU>
21. ncurses `curs_addch(3x)`, lower-right corner: <https://invisible-island.net/ncurses/man/curs_addch.3x.html>
22. Blightmud issue #76: <https://github.com/Blightmud/Blightmud/issues/76>; resize/tmux corruption #84: <https://github.com/Blightmud/Blightmud/issues/84>

**Not verified, and deliberately not cited above:** no user-sentiment data was obtainable (Reddit and
the MUD forums were unreachable), so nothing here rests on "users complain that…". Blightmud's top-area
tabs and Mudlet's bundled starter UI are both on development branches; which released versions ship
them is unknown. BlowTorch/Mukluk and wxMUSH could not be checked at all.

---

## 8. Staged plan

**Stage 1 — corrections only. No new cells, no new data, independently shippable.**

- `StatusBarMarkup` resolves through `WindowSession(ActiveWindowId())`, with the three arms in §6.
- Drop `Graphics <protocol>` from the not-connected row.
- A test pinning that the row's identity and the prompt's identity agree in all three states — the
  counterpart of `FocusIndicationTests`' pins, and the thing whose absence let them diverge.

Nothing in stage 1 depends on
[`structured-server-data-design`](2026-07-30-structured-server-data-design.md) at all. That is
deliberate: it is a bug fix that happens to live in the file this document is about, and it should not
wait for a protocol layer.

**Stage 2 — the degradation ladder.**

- §5's explicit priority, replacing "everything fits or it doesn't".
- Frames decoded at 80, 100 and 120 columns, with and without a split, with and without the scrollback
  indicator, as the evidence. Not arithmetic.

**Stage 3 — the logging glyph** (§4.1), if wanted. Smallest possible addition, and the one addition that
passes the test cleanly.

**Not planned here:** vitals, room data, channel activity, latency. The first three are
[the pane's](2026-07-30-vitals-pane-design.md); the fourth is not measurable.

---

## 9. Decisions deferred

| Decision | What would settle it |
|---|---|
| A "quiet since" liveness cell (§4.2) | A threshold that is meaningful on a MUSH, where silence is normal. Needs real use, not reasoning. |
| Whether GMCP `Char.Name` should replace the configured name across every surface (§4.3) | How often the two actually diverge on real worlds. This row must not diverge alone either way. |
| Whether the logging glyph is worth two cells (§4.1) | Whether anyone has been surprised by a transcript they forgot was running. |
| Whether `MarkupWidth` is correct for Nerd Font glyphs at all | A frame decoded on a terminal with and without a Nerd Font build. Pre-existing; surfaced here because §5 makes the row more sensitive to it. tmux distinguishes `#{n:…}` (length) from `#{w:…}` (display width) for exactly this reason.³ |
| **Whether the bottom-right cell is safe** (§7.6) | A real terminal, not a snapshot — a headless frame has no autowrap. This row right-aligns to the final column of the final row, which is the cell ncurses documents three separate workarounds for²¹ and which Blightmud shipped a bug in.²² If it is not safe, the fix is to reserve the last column, which costs one cell and is cheaper than any of the alternatives. This is the only item in this table that might already be a visible defect. |
| Whether `not connected` should distinguish "nothing configured" from "socket closed" (§7.6) | TinyFugue distinguishes them (`(no world)` vs `!WorldName`).¹² Ours does not. One word, and it is the state where ⌃D is the answer. |
