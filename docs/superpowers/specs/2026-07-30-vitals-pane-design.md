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
**its own main window id**, written by `BindSession` — and resolves the pane hosting *that* window. A
window that is not a session's main window is never in that map, so a vitals window is excluded from
NAWS **by construction**, not by a check. That is worth stating because it is the safe property and it
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
§5). Servers agree on very little. So the pane has two layers: a small amount of inference so that a
common server works with no configuration, and explicit configuration for everything else.

### 4.1 Inference — the zero-configuration case

Scan `Char.Vitals.*` (and, once MSDP exists, `MSDP.*`) for **paired** numeric keys: a key `X` and a key
matching `maxX` / `X_max` / `XMAX`, case-insensitively. Each pair becomes a gauge. Unpaired numeric and
string keys become a plain `label  value` line.

`hp`/`maxhp`, `mp`/`maxmp`, `ep`/`maxep`, `sp`/`maxsp`, `health`/`healthmax` all fall out of that rule.
A world using entirely different names gets lines rather than gauges, which is legible and not wrong.

Ordering: the same preferred list `GmcpStats` already carries (`hp, maxhp, mp, maxmp, sp, maxsp, level,
xp, gold`) first, then everything else in arrival order. That list is the one thing worth keeping from
`GmcpStats` before it is deleted.

### 4.2 Configuration

A per-character list of entries, on F5 beside the trigger sets, each naming a path, a label, an optional
max-path, and a form (`gauge` | `number` | `text`). An empty list means "infer" (§4.1); a non-empty one
replaces inference entirely, because a list that *added* to inference would leave the user unable to
remove a row they did not want.

**Deferred:** whether a user should be able to write an expression (`hp/maxhp*100`) rather than name two
paths. Expressions want a parser, a sandbox and an error surface; naming two paths wants neither.
Settled by whether anybody hits a server where the ratio is not two paths.

### 4.3 Rendering, and what a gauge is in a terminal

A gauge is a row: a label, a bar of filled and unfilled cells, and the figures.

```
hp   ████████████████░░░░  990/1200
ep   ████████████████████  400/400
```

The bar's width is derived from the pane's, so the pane is a normal citizen of the split tree rather
than a thing with a minimum size. Degradation, narrowest first, is the same shape as the status row's
ladder:

| pane width | what it shows |
|---|---|
| wide | `hp   ████████░░░░  990/1200` |
| medium | `hp  ████░░  990/1200` — bar shrinks |
| narrow | `hp  990/1200` — bar dropped, figures kept |
| very narrow | `hp  82%` — figures dropped, ratio kept |

The figures outrank the bar, deliberately. A bar tells you roughly; a number tells you exactly, and the
thing people actually do with a health readout is decide whether a number is above a threshold.

**Colour** comes from `WorkspacePalette` and the world's accent, not from a new palette. A gauge that
changes colour by fill level (green → amber → red) is the obvious next thing to want and is
**deferred**: thresholds are a game-specific judgement (30% is critical on one MUD and routine on
another), and a client that picks them itself is inventing information the way the latency meter did.
A configurable threshold per entry is the honest version, and it is a stage-3 feature.

### 4.4 With no GMCP at all — the common case

Most MUSHes send nothing. A permanently empty pane is worse than no pane, so the empty state carries its
own explanation:

```
  vitals · Corvid

  Nothing yet.

  This character's server has sent no structured data.
  GMCP is optional and most MUSHes do not use it.

  ⌃P ▸ Show client messages  ·  /gmcp  to see what has arrived
```

Three states, and only the middle one is data:

| State | What the pane shows |
|---|---|
| Session not connected | *not connected* — and the pane persists, because it is part of the workspace, not part of the session |
| Connected, `SessionData.HasAny` false | the block above |
| Connected, data present | §4.1/§4.2 |

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
  id and holds `PaneLine`s; a vitals window has none. Every path that assumes a window has a line buffer
  — the timestamp re-render, freeze, the scrollback segment, the unread badge — needs to not apply to
  it. The safe construction is that those paths key off `_lines.ContainsKey(windowId)` rather than off
  the kind, so a window with no buffer is inert to all of them without any of them naming it.
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

<!-- PRIOR-ART -->

---

## 9. Staged plan

**Stage 1 — one bound pane, inferred content, no configuration.**

- `WindowKind.Vitals`; open via ⌃P only; bound to one character at creation.
- Inference (§4.1) and the three states (§4.4).
- Plain `label  value` lines. **No gauges.** Numbers are the part people read, and a list of numbers is
  a complete, useful, shippable feature that proves the whole path — store → change event → marshal →
  repaint — with none of the width work.
- Rate limit per §5.
- `--view vitals` and `--view vitals-empty`.

This depends only on stage 1 of the
[protocol document](2026-07-30-structured-server-data-design.md), which is the point: the two stage 1s
together are a client that asks a server for its data and shows you what came back.

**Stage 2 — gauges.**

- The bar, and the width ladder in §4.3.
- ⌃B command and `PrefixPanel` entry.
- Restore-from-workspace-state.

**Stage 3 — configuration.**

- Per-character entry list on F5 (§4.2).
- Configurable colour thresholds (§4.3).
- *vitals pane at start*.

**Not planned:** follow-the-focus mode, expressions, and a shared pane showing several characters at
once. Each is a real request someone will make; each reintroduces the "whose" problem this design exists
to remove, and none should be added without a reason stronger than symmetry.

---

## 10. Decisions deferred

| Decision | What would settle it |
|---|---|
| Follow-the-focus binding as an option (§3) | Whether anyone with several characters wants one pane rather than several. If added, it resolves through `WindowSession(ActiveWindowId())` and is per-window, never a global mode. |
| Expressions instead of two named paths (§4.2) | A real server where the ratio is not two paths. |
| Colour thresholds (§4.3) | They are game-specific. A client that picks them invents information; a configurable one does not. Stage 3 either way. |
| Closing the pane when its character's last output pane closes (§6) | Use. Both behaviours are defensible and neither is obviously more annoying yet. |
| Whether `Char.Name` should rename the pane's title (and everything else) | Cross-cutting; see [status bar](2026-07-30-status-bar-design.md) §4.3. This pane must not diverge from the rail and the prompt on its own. |
