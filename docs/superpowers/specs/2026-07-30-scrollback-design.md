# Scrollback: where history lives and what the pane holds

**Date:** 2026-07-30
**Status:** proposed — design only, nothing implemented

## Problem

The client has two scrollbacks and they are not connected.

One is the **pane buffer**: `SharpMUTermApp._lines`, a `Dictionary<string, List<PaneLine>>` keyed by
window id, trimmed to `AppConfiguration.ScrollbackLines` (default 10,000) on every append
(`SharpMUTermApp.cs:81`, `:1712`). This is the only history the client can actually navigate — PgUp,
Shift+↑/↓, the wheel and ⌃Home all move a `ScrollablePanelControl` viewport over a `MarkupControl`
that this buffer fed.

The other is **`Core.ScrollbackBuffer` + `FileScrollbackSpill`**: a capped in-memory ring of
`StyledLine` with absolute line indices, backed by a segmented, CRC-32-checked, per-platform-cache
disk store bounded at 200,000 lines / 64 MB. It is 394 + 959 lines with 12 + 22 tests, it holds every
record's offset in memory so a read is one seek, it reclaims space by unlinking whole segments, it
deletes itself on dispose, and it never throws for an I/O reason.

**Nothing reads it back.** Verified: across `src/` outside `Core/Text/`, `ScrollbackBuffer.GetRange`
has zero callers, `GetTail` has zero callers, and `LinesAppended` has zero subscribers. The only
production read of the buffer at all is `SharpMUTermApp.cs:1447`, `session.Scrollback.Snapshot()` —
and `Snapshot()` returns the **in-memory window only**, by construction. Lines are encoded, framed,
checksummed and written to disk, and never retrieved. Meanwhile `AppConfiguration.cs:34` tells the
user the spill exists "so the view can scroll further back than memory holds", which is not true of
any code path that exists.

So the effective scroll depth is the in-memory 10,000, held twice — once as `StyledLine` in the
session's buffer, once as markup in the app's — and the disk store is dead weight with a docstring
that lies.

Behind that sits the reason the number is 10,000 and not the 20,000 it used to be.
`MarkupControl`'s parse cache is keyed on a content version (`MarkupControl.ParseCache.cs:39,55`),
and every content mutator bumps it (`OnContentAppended` → `BumpContentVersion`,
`MarkupControl.cs:511-519`). So **one arriving line re-parses everything the control holds**:
~11 ms at 1,000 lines, ~50 ms at 5,000, 88–116 ms at 20,000, measured against the pinned build. A
busy room at 20,000 lines drops the client below a frame a second. Halving the cap bought time; it
did not remove the coupling.

This document decides how scrollback works end to end given a control that cannot cheaply hold a
large buffer.

## The answer, in short

| | |
|---|---|
| **Authoritative history** | One per **window**, in Core, on the existing `ScrollbackBuffer` — not per session, because a session prints into several windows and a window's content is not always a session's |
| **What the control holds** | A bounded tail while pinned to the live output; a wide slice while scrolled back; never the whole buffer |
| **The disk spill** | **Lives.** Re-pointed from the session to the window, and it is what decouples the depth the user sets from the cost the client pays |
| **Depth** | `ScrollbackLines` stops meaning "what is in memory" and starts meaning "how far back you can go". Memory shrinks; depth grows |
| **The record** | `StyledLine` + arrival instant, with markup as a memo — so theme, highlight and format changes can reach history the way the timestamp toggle now does |
| **Search** | In scope. A match list over `StyledLine.Text`, with a time budget and an honest partial-result indicator |
| **First shippable step** | Cap the append cost. Bounded tail, widen on scroll-away, narrow on ⌃End. No storage changes, no depth changes |

---

## What is actually there today

Verified against the tree at `83f7171`.

### The seam

`AppendWindowLine` (`SharpMUTermApp.cs:1700`) and `FeedRange` (`:1744`) are the whole seam between
the line buffer and the controls that draw it. `AppendWindowLine` appends a `PaneLine`, trims to the
cap, and calls `MarkupControl.AppendLine(Compose(buffer[^1]))`. `FeedRange` composes a slice and
calls `SetContent`. Nothing else hands pane content to a control.

`Compose` (`:1688`) glues the timestamp gutter on at render time, which is why *show timestamps* can
repaint history. `RepaintPanes` (`:1803`) is the only whole-buffer re-feed, and it is affordable only
because it is bounded by one deliberate keystroke — its single caller is `SetTimestamps`.

### The record

`PaneLine` is `(string Markup, string? Stamp)` — markup plus, held apart from it, when the line
arrived (`PaneLine.cs:35`). The separation is the fix for the reported timestamp bug: baked into the
markup at append time, the toggle reached only lines yet to arrive.

### Scrolling

`ScrollViewFor` (`:4968`) wraps every output region in a `ScrollablePanelControl` with
`HorizontalScrollMode.None`, **`ShowScrollbar = false`** (two columns of every pane, permanently,
and per-pane NAWS is derived from the pane rectangle), and `AutoScroll` true for a live tail / false
for the web document. The panels are kept in `_paneScrolls` across pane-area rebuilds so a reader's
scroll position survives a split, a tab change or a freeze.

`AutoScroll` re-pins to the bottom on **any repaint** while enabled
(`ScrollablePanelControl.Rendering.cs:127-133`), detaches when `ScrollVerticalBy` is called upward,
and re-attaches when it reaches the bottom (`.Scrolling.cs:145-152`). It moves the offset *during
paint*, so the discovering frame is one frame stale and the panel schedules its own relayout.
`ScrollToTop`/`ScrollToBottom` do not touch `AutoScroll`; only `ScrollVerticalBy` treats itself as a
user gesture. `SyncScrollbackState` (`:2887`) mirrors `AutoScroll` into
`WorkspaceWindow.ScrolledBack`, which is what makes unread badging count output arriving below a
scrolled-back viewport.

### Freeze

⌃F records `_freezePoints[windowId] = buffer.Count` and `BuildFrozenContent` (`:5479`) lays out two
`MarkupControl`s over one buffer: `FeedRange(frozen, buffer, 0, split)` above a `▲ FROZEN ⌃F` bar,
`FeedRange(live, buffer, split, …)` below it. Both regions have their own viewport; the page keys aim
at the pinned half (`ScrollTarget`, `:2742`). `AppendWindowLine` walks the freeze point down as the
buffer trims.

### The two buffers already disagree about the setting

Not a hypothetical. `AppendWindowLine` re-reads `_config.ScrollbackLines` on **every append**
(`:1712`), so the pane buffer resizes live and trims itself the moment the setting is committed.
`ScrollbackBuffer.Capacity` is get-only, fixed by the constructor, and `SessionManager.Open` is
handed `config.ScrollbackLines` once at open time. So raising or lowering the setting on a running
client changes one buffer and not the other, and they stay diverged until the session is reopened.

That is precisely TinyFugue's decade-old bug — `/histsize` resizes the history and, per an
`XXX resize corresponding screen` comment still in its source, not the screen — and it is the third
instance in this codebase of the failure D1 exists to remove. It is invisible today only because
nothing reads the session buffer past `Snapshot()`.

### Two things the existing comments get wrong

- **A spawn window's history is not markup-only at the seam.** `OnSpawnLine` (`:1871`) is handed a
  `StyledLine` and throws it away into `_formatter.ToMarkup(line)` on the very next statement. The
  claim in `PaneLine`'s docs and in `CLAUDE.md` — "there are no `StyledLine`s left to re-render" — is
  true of the current *storage* and not of what is available at the seam. That matters: it means a
  `StyledLine`-backed per-window history is possible for spawn windows too, not just session windows.
- **An append costs two full parses of the pane, not one.** The pane's `MarkupControl` is constructed
  with no alignment (`PaneContentFor`, `:4916`), so it is `HorizontalAlignment.Left`
  (`BaseControl.cs:37`). `ComputeParseWidth` then takes the non-Stretch branch and calls
  `NaturalContentWidth()`, which is `EnsureParsed(int.MaxValue, …)` — a *second* parse key, distinct
  from the fitted width `PaintDOM` parses at (`MarkupControl.ParseCache.cs:245-287`). An append
  invalidates both. The measured 11/50/116 ms figures therefore already include this doubling; making
  the pane `Stretch` is a candidate free halving and is listed under *what to measure*, not asserted.

---

## How other clients do it

Six MU\* clients, read from their own source and issue trackers rather than from hearsay. Three
widely-repeated numbers turned out to be wrong and are corrected inline.

| Client | Default depth | Ceiling | Survives restart | Search | Scrolled-back presentation |
|---|---|---|---|---|---|
| **TinTin++** | 10,000 lines/session, RAM | 1,000,000 — quantised to 100 / 1k / 10k / 100k / 1M | No (`#buffer write` exports) | `#buffer find` — regex, skip-count | **Hard freeze**: incoming text is not displayed at all while scrolled |
| **TinyFugue 5** | history 1,000/world; the scrollable *window* is a separate 1,000, hardcoded | `/histsize`, memory-bound | No, but `/quote -dexec /recordline` replays a log back into history | `/recall` — regex, time ranges, grep-style `-A/-B/-C` | **Per-socket virtual windows** with a remembered position and a divider line; a More prompt showing both how far back you are and how many unseen lines have queued |
| **Mudlet** | **100,000** main console (since Sept 2025); `TBuffer`'s own default is still 10,000 for miniconsoles | RAM-derived (80 % of physical, ×0.8) | No | Ctrl+F search bar over the console | **Auto split screen** on any scroll-up, draggable divider, auto-closes at the bottom, middle-click / Ctrl+Enter to merge |
| **MUSHclient** | 5,000 (`max_output_lines`) | 500,000, min 200 | No | Find dialog **and** Recall, which materialises hits into a separate Notepad window | **Freeze** (`auto_pause` on by default) — frozen ≡ "scrollbar is not at the bottom", recomputed per scroll. Dragging a selection also freezes |
| **BeipMU** | **10,000 per text window** | 999,999 (six-digit UI field) | **Yes — restore logs.** `restore.dat`, 256 KB default, **on by default** | Ctrl+F find dialog (regex) and `/recall`; Find forces the window paused | **Split on page up** (opt-in) *and* pause/scroll-lock, with a pause glyph in a fixed corner and an unread-line counter in the status bar |
| **Blightmud** | 32,768 (32 × 1024) | `blight.history_capacity()` | No | `/search` (regex), Ctrl+↑/↓ through matches | **Auto split** when the terminal is taller than 20 rows; a **fixed 10-line** live tail; a green `━ (scroll) ━━━` divider |

Corrections worth recording, because all three circulate as fact: TinTin++'s default is 10,000, not
the 5,000 the top search results claim (5,000 is `auto_tab`, the tab-completion scan depth);
MUSHclient's option is `max_output_lines`, not `scrollback_lines`; TinyFugue has no `more_lines`
setting at all (it is the `%more` flag, default *off*, plus `moresize()`/`morescroll()`).

### What this changes about our design

**Our 10,000 default is exactly BeipMU's**, and BeipMU is the parity target. That is reassuring about
the number and damning about the reason: ours is 10,000 because a bigger one is slow, theirs is
10,000 because it is a sensible default with a six-digit field behind it. Fixing the reason is D4.

**The split-with-live-tail is the consensus.** Mudlet, BeipMU and Blightmud all converged on it;
TinTin++'s hard freeze ("while scrolling through the scrollback buffer incoming text is not
displayed") and MUSHclient's freeze are the older generation. We already have the modern shape in
⌃F — but ours is a *deliberate mode*, and in all three of those clients **scrolling up is itself the
gesture that splits**, with reaching the bottom closing it. That is a real UX question D4 forces
open: our scroll-away already changes the pane's feed policy, so making it also split is a small
increment. Deferred, below, because it is a taste call and not a mechanism one.

**Two parallel line stores diverge. Every time.** TinyFugue keeps a `History` and a `Screen` per
world; `/histsize` resizes the first and, per an `XXX resize corresponding screen` comment still in
the source, not the second. The result is [SourceForge bug #20, "Worlds lose PageUp history"][tf20] —
**open and unresolved since 2010**: PgUp gives a near-black screen, `/recall` is unaffected, only a
restart fixes it, and one reporter notes "the page-up buffer shrinks, not all at once, but it seems
to get smaller over time." We have exactly this shape today — `WorldSession.Scrollback` and
`SharpMUTermApp._lines`, both capped by `ScrollbackLines`, kept in step by nothing. That bug is the
best available argument for D1.

**Batch eviction racing the renderer is the number-one bug in this space**, and it is worth spelling
out because it is the failure mode our design is already immune to. Blightmud drains 1,024 lines at
once when its 32,768-line `Vec` fills, and has crashed twice on it — [#1042][bm1042]
(`index out of bounds: the len is 32259 but the index is 32259`) and [#416][bm416]
(`len is 31772 but the index is 32656`), both "scrolling through the buffer while lots of new text is
coming in", both panicking in `split_screen.rs`. Mudlet's [#2963][mud2963] is the same race in a
non-fatal register: when the buffer is full and batch-deleting, the console miscounts how many
on-screen lines changed, so only the last line or two repaint and everything above goes stale.
Mudlet's [#6366][mud6366] asks for a `sysBatchDeletionEvent` because scripts tracking line numbers
silently desync when eviction fires.

This is precisely what **absolute line indices** eliminate. `ScrollbackBuffer`'s index 0 is the first
line the buffer ever saw and an index never comes to mean a different line; `OldestIndex` advances
and `GetRange` clamps rather than throwing, so a scroll position that eviction has invalidated
returns fewer lines instead of indexing off the end of an array. A design built on offsets-into-a-
`List` — which `_freezePoints` currently is, and which is why `AppendWindowLine` has to walk the
freeze point down — is the design that produces those two crashes. Moving to absolute indices (D3) is
not tidiness; it is the fix for a bug class two of our peers ship.

**Search: everyone has it, and the shape we can afford is the shape MUSHclient chose.** MUSHclient's
Recall searches the output buffer and dumps matching lines into a *separate Notepad window* rather
than navigating in place. That is independently the conclusion D6 reached from the windowed feed, and
it is good to find a shipping client that made the same trade. Mudlet's [#6442][mud6442] is the
cautionary tale for the in-place version: its search and `TTextEdit::scrollTo` land the target *in
the gap between the two panes*, because at scroll time you do not yet know how tall the bottom pane
will be — the same "you cannot place an offset before the metrics refresh" hazard as D4's hazard (1),
shipped and open.

**Don't show the same line in both halves.** BeipMU's [#177][bm177] asks for it explicitly — "the
upper split would only include text that is not visible on the primary, so you can't see the same
text on the top and bottom splits" — and Mudlet fights the same thing. Our freeze splits one buffer
at an index, so the two halves are disjoint by construction. Keep it that way.

**⌃End should un-freeze as well as scroll.** BeipMU's [#79][bm79] is precisely this: End scrolled to
the bottom but did not cancel the split, and users expected both. Ours has the same gap — `BackToLive`
re-arms `AutoScroll` and does not call `ToggleFreeze`. That is a small, independent bug fix, not part
of this design, and it should be raised as one.

**The live-tail height should not be a constant.** Blightmud hardcodes `SCROLL_LIVE_BUFFER_SIZE = 10`
and [#1097][bm1097] is a user asking for it to be configurable or proportional: "on a mobile device,
ten lines can be a lot, and on a 4K screen it's a little low."

**Nobody else spills to disk** — and that is the strongest argument against D3, so it is stated here
rather than buried. Every client above keeps its scrollback in RAM. Mudlet simply holds 100,000
lines, and its maintainers know the cost: the author of the PR that raised the default writes that
"if you have more than 1 console set to maximum size, you will use more memory than your system can
handle, which some folks have done in the past and complained about it." MUSHclient's author goes
further — "no particular reason not to just make it large anyway, as the memory is allocated
dynamically." So "hold 50,000 in RAM and delete the spill" is a position with real industry backing.
D3 answers it.

**But BeipMU — the parity target — is the one client whose scrollback survives a restart**, and that
reframes what `FileScrollbackSpill` is for. Its restore logs are on by default, sized in kilobytes
(`RestoreBufferSize = 256`), stored in `restore.dat`, and replayed into the window with a
"Content restored" message on reconnect. Note the failure mode of sizing a ring in *bytes*: BeipMU's
own changelog records "a hang when sending enormous blocks of text (>64K) and using restore logs — if
what is sent is larger than the restore log size it would get stuck trying to make room forever."
Size it in lines.

`FileScrollbackSpill` is already most of a restore log: indexed, CRC-32'd, segmented, bounded,
reclaimed by unlinking. The single thing that makes it ephemeral is that it deletes itself on
dispose. **That is a much better justification for keeping it than "the windowed feed needs deep
paging"**, and it is a genuine parity feature nobody but BeipMU has.

[tf20]: https://sourceforge.net/p/tinyfugue/bugs-and-support/20/
[bm1042]: https://github.com/Blightmud/Blightmud/issues/1042
[bm416]: https://github.com/Blightmud/Blightmud/issues/416
[bm177]: https://github.com/BeipDev/BeipMU/issues/177
[bm79]: https://github.com/BeipDev/BeipMU/issues/79
[bm1097]: https://github.com/Blightmud/Blightmud/issues/1097
[mud2963]: https://github.com/Mudlet/Mudlet/issues/2963
[mud6442]: https://github.com/Mudlet/Mudlet/issues/6442
[mud6366]: https://github.com/Mudlet/Mudlet/issues/6366

Primary sources: TinTin++ [`#buffer`](https://tintin.mudhalla.net/manual/buffer.php) and
`src/main.c` / `src/buffer.c` / `src/config.c`; TinyFugue
[`ingwarsw/tinyfugue`](https://github.com/ingwarsw/tinyfugue) `src/tfio.c` / `src/history.c` /
`src/socket.c` and `help history` / `help scrollback` / `help /recall`; Mudlet
[`setConsoleBufferSize`](https://wiki.mudlet.org/w/Manual:Mudlet_Object_Functions),
[Manual:General Features](https://wiki.mudlet.org/w/Manual:General_Features), `src/TBuffer.cpp`,
`src/Host.h`, [PR #8222](https://github.com/Mudlet/Mudlet/pull/8222); MUSHclient
[`scriptingoptions.cpp`](https://github.com/nickgammon/mushclient/blob/master/scriptingoptions.cpp),
`doc.h`, `mushview.cpp`; BeipMU
[`src/Root.prp`](https://github.com/BeipDev/BeipMU/blob/master/src/Root.prp),
[`Documentation/GettingStarted.md`](https://github.com/BeipDev/BeipMU/blob/master/Documentation/GettingStarted.md),
[`Documentation/DataLoss.md`](https://github.com/BeipDev/BeipMU/blob/master/Documentation/DataLoss.md),
`Assets/Changes.txt`; Blightmud
[`Blightmud/Blightmud`](https://github.com/Blightmud/Blightmud) `src/ui/history.rs`,
`src/ui/split_screen.rs`, `src/ui/scroll_data.rs`, `resources/help/scrolling.md`.

## How terminals do it

Terminal emulators and multiplexers live under exactly our constraint — an enormous line buffer
against a small viewport — and have done for decades. Read from source.

Two premises in the brief were wrong and are corrected here: **kitty's `scrollback_lines` default is
2,000, not 10,000**, and **`scrollback_pager_history_size` does not spill to disk** — it is a plain
in-memory byte ring (`PagerHistoryBuf` over `3rdparty/ringbuf`). kitty's only disk cache serves
images. The real on-disk-scrollback implementations are **Konsole** and **VTE**, and they turn out to
matter more to us than kitty does.

| | Backing store | Line address | Append cost | Viewport cost |
|---|---|---|---|---|
| **kitty** | segmented `mmap` ring, 2,048 lines/segment, 32 B/cell fixed | `(start_of_data + i) % ynum` | O(cols) | `lines + 1` rows |
| **tmux** | growable flat array, 5 B/cell packed + a side table for "hard" cells | `linedata[hsize - oy + y]` | O(cols), amortised over a 10 %-batch trim | `sy` rows |
| **GNU screen** | ring of five-plane pointer structs | `(w_histidx + y) % w_histheight` | **O(1) pointer swaps** | `l_height` rows |
| **Konsole** | three append-only files — cells, a per-line offset index, line flags — with an adaptive `mmap` window | `_index[n]` → byte offset into `_cells` | O(cols) write | viewport rows |
| **VTE** | 64 kB blocks, **compressed and AES-GCM encrypted**, in a three-region "snake" | logical byte offset → snake → block | buffered append | viewport rows |

Defaults are strikingly *lower* than the MU\* clients': kitty 2,000, tmux 2,000
(`history-limit`), GNU screen **100** (`DEFAULTHISTHEIGHT`). Terminals get depth from cheap storage,
not from a big default.

### The one thing they all do, which we cannot

**No terminal lets the widget own the history.** Every one of them hands the renderer an accessor and
a line count, and the renderer touches exactly viewport rows. kitty's
`render_line_for_virtual_y()`, tmux's `#define grid_view_y(gd, y) ((gd)->hsize + (y))` and screen's
`WIN(y)` macro are the same three-line function. Scrolling is then "assign an integer, set a dirty
flag" — O(1) at any history depth. kitty makes the consequence explicit: its GPU vertex buffer is
`sizeof(GPUCell) * render_lines * columns`, so **nothing scales with scrollback**, and an idle
scrolled-back view uploads nothing at all because the whole cell-data pass is gated on
`reload_all_gpu_data || scroll_changed || is_dirty || resized`.

That is shape (a) in D4 — the one rejected. It is the industry answer, and the *only* reason we
cannot take it is that we do not own the renderer: `MarkupControl` has no line-accessor API and
`ScrollablePanelControl` is where the wheel, the drag-selection and the clamping live. **If one
upstream change could be wished for, it is this** — a `MarkupControl` that takes
`Func<int, string> LineAt` plus a `LineCount` instead of a `List<string>`, and paints only the rows
it was arranged for. That is a much larger PR than the two in *Upstream* below, and it is the right
long-term shape. Named here so the eventual answer is on record even though this design routes
around it.

### Patterns worth taking

**A two-tier store with a dumb cold tier.** kitty's pager history is the cleanest statement of it,
and the design conversation ([issue #970][k970]) settles it in one sentence from the maintainer: *"The
secondary scrollback should just be a **dumb byte buffer**. When the user triggers a read of the
secondary scrollback it can be re-interpreted for wrapping etc."* Evicted lines are rendered to
ANSI-escaped UTF-8, appended to a ring, and **never reflowed** — `historybuf_finish_rewrap` only sets
`rewrap_needed`, and the rewrap runs lazily on read. Our markup-memo-behind-a-`StyledLine` (D2) is
the same trade in the other direction; the lesson to take is that the cold tier is allowed to be
cheap and dumb, and to defer all its work to the moment someone reads it.

**Konsole is our spill, already shipped.** `HistoryScrollFile` is an append-only `_cells` stream plus
an `_index` stream of one `qint64` start-offset per line, so `getLineLen(n)` is
`(startOfLine(n+1) - startOfLine(n))` and reading line *N* is one seek. That is
`FileScrollbackSpill`'s design, arrived at independently by a mainstream terminal. Two details worth
copying and one worth arguing about:

- The history file is a `QTemporaryFile` that is **`unlink()`ed immediately after opening**, so it is
  reachable only through the open descriptor and cannot survive a crash. That is a deliberate privacy
  mitigation, and it is a direct argument *against* the restore-log idea in D3: making scrollback
  survive a restart means leaving decrypted session text on disk between sessions, which Konsole
  specifically refuses to do. VTE goes the other way and **encrypts** its spill (AES-GCM, per-block
  overwrite counter for IV uniqueness) precisely so that it can afford to have one. If we want
  restore logs, encryption is the honest price, and that changes the size of the feature.
- Konsole caps reflow: `const int MAX_REFLOW_LINES = 20000`, and reflow starts at
  `getLines() - MAX_REFLOW_LINES`. Bounding an O(history) operation to a recent window is a shipped,
  deliberate trade — the same shape as `MaxRangeLines`.
- Its own source carries a standing TODO that `mmap`ing the entire file "will cause problems if the
  history file becomes exceedingly large… should only map in sections at a time". Our spill reads by
  offset and never maps the whole thing, so we are already on the right side of that one.

**tmux's copy-mode is a snapshot, not a detach — and its reconciliation is the pattern for our
re-window.** `window_copy_init` **clones the grid** (`grid_duplicate_lines` over `hsize + sy` lines);
the live pane keeps writing its own grid while copy mode reads a private copy. Entering copy mode is
therefore O(entire history), and current master mitigates that with `window_copy_sync_backing()`,
which keeps three counters on the grid — `scroll_added`, `scroll_collected`, `scroll_generation` —
copies only the delta, and **falls back to a full clone whenever the arithmetic fails to reproduce
the new size**. That "compute the incremental update, then check it reproduces the expected result,
else rebuild wholesale" pattern is directly applicable to D4's re-window: it is how you get an
incremental fast path you are allowed to trust.

**kitty compensates a scrolled-back view by counting lines**, not rows: when output arrives while
scrolled back it does `scrolled_by = MIN(scrolled_by + history_line_added_count, count)`, so the view
stays pinned to the same *content*. It can do that because its lines are fixed height — reflow is a
separate, explicit O(history) pass. Ours soft-wrap, which is exactly why D4's hazard (1) is a hazard
and kitty does not have it. Under D4's detached policy we sidestep it by feeding nothing while
scrolled back, which is the same outcome by a different route.

**Compact at the eviction boundary, and know what that does not cover.** tmux calls
`grid_compact_line()` at the moment a line scrolls off, rebuilding its extended-cell table with only
the slots still referenced — [PR #1062][t1062] took a 50,000-line truecolor history from **1.6 GB to
170 MB**. But [issue #4859][t4859] is the same bug in a line that never scrolls off: a tool emitting
~78 kB/s of clear-to-EOL sequences drove **48 GB** across three panes of ~1,000 lines each, because
each RGB-background clear allocated a fresh extended slot. Compaction on eviction bounds cold lines
only; a hot line can still grow without bound in place. Our analogue is the markup memo in D2 — it
must be bounded by the record's lifetime, not accumulate variants.

**Budget the O(history) operations and degrade rather than block.** tmux's all-matches search
highlight runs under a **200 ms** budget (`WINDOW_COPY_SEARCH_ALL_TIMEOUT`); if it blows, it re-scans
**visible lines only** and reports an approximate count bucketed to 1000 / 100 / 10, which is the `+`
in its `X/Y+` indicator. A hard **10 s** cap (`WINDOW_COPY_SEARCH_TIMEOUT`) abandons marks entirely.
The mark bitmap is `xcalloc(sx, sy)` — **viewport-sized, not history-sized**; the full-history pass
exists purely to count. Two more cheap wins worth stealing verbatim: a repeated search with an
unchanged term sets `visible_only` so `n` does not re-scan history, and a "regex" with no
metacharacters (`str[strcspn(str, "^$*+()?[].\\")] == '\0'`) is downgraded to a substring search.

**Reflow is their biggest cost and none of ours — because we store logical lines.** Every terminal
here stores a wrapped *cell grid*, so a column change has to rewrap the history: kitty allocates an
entire new `HistoryBuf` and copies every line (its docs warn against large scrollback for exactly
this reason); Konsole caps the work at `MAX_REFLOW_LINES = 20000`; GNU screen's rewrap is *lossy* in
two ways — narrowing drops the oldest lines the rewrap expanded past capacity, and its line-length
scan discards unattributed trailing whitespace on every pass. We store the logical line the wire
sent and let the control wrap it at paint time, so a terminal resize costs us **nothing**: no
rewrap, no loss, no O(history) pass. That is a real advantage of the D2 record and it should not be
given up casually — it is the reason not to "optimise" storage by caching pre-wrapped rows.

The corollary is the one place we are *worse* off. Because wrapping is deferred, we do not know how
many display rows a record occupies until it is parsed, which is D4's hazard (1) in one sentence.
A per-record `(width → rowCount)` memo would remove the settling frame entirely — but computing it
means running the parser we do not own. That is a third argument for the line-accessor control
below; screen gets the same information for free from a sentinel cell at index `w_width`, because it
is the thing doing the wrapping.

**Nobody indexes scrollback for search.** The three strategies in the wild are: delegate (kitty pipes
the whole history into `less` and sends `/`); linear scan over a *virtual* flat address space (GNU
screen runs Boyer–Moore over `l_width * (histheight + height)` characters, reading position `q` as
`WIN(q / w)->image[q % w]` — **no flattened buffer is ever materialised**); or scan with a deadline
and degrade (tmux). D6's "page it back through `GetRange` first, index only if a measurement asks"
is screen's strategy with our paging primitive in place of its ring macro, and it is the right
starting position.

[k970]: https://github.com/kovidgoyal/kitty/issues/970
[t1062]: https://github.com/tmux/tmux/pull/1062
[t4859]: https://github.com/tmux/tmux/issues/4859

Primary sources: kitty [`kitty/history.c`](https://github.com/kovidgoyal/kitty/blob/master/kitty/history.c),
[`kitty/screen.c`](https://github.com/kovidgoyal/kitty/blob/master/kitty/screen.c),
[`kitty/resize.c`](https://github.com/kovidgoyal/kitty/blob/master/kitty/resize.c),
[`kitty/text-cache.c`](https://github.com/kovidgoyal/kitty/blob/master/kitty/text-cache.c),
[`options/definition.py`](https://github.com/kovidgoyal/kitty/blob/master/kitty/options/definition.py),
[conf docs](https://sw.kovidgoyal.net/kitty/conf/); tmux
[`grid.c`](https://github.com/tmux/tmux/blob/master/grid.c),
[`grid-view.c`](https://github.com/tmux/tmux/blob/master/grid-view.c),
[`window-copy.c`](https://github.com/tmux/tmux/blob/master/window-copy.c),
[`options-table.c`](https://github.com/tmux/tmux/blob/master/options-table.c); GNU screen
[`src/ansi.c`](https://git.savannah.gnu.org/cgit/screen.git/tree/src/ansi.c),
[`src/search.c`](https://git.savannah.gnu.org/cgit/screen.git/tree/src/search.c),
[`src/resize.c`](https://git.savannah.gnu.org/cgit/screen.git/tree/src/resize.c),
[manual](https://www.gnu.org/software/screen/manual/screen.html); Konsole
[`HistoryScrollFile.cpp`](https://invent.kde.org/utilities/konsole/-/blob/master/src/history/HistoryScrollFile.cpp),
[`HistoryFile.cpp`](https://invent.kde.org/utilities/konsole/-/blob/master/src/history/HistoryFile.cpp); VTE
[`src/vtestream-file.h`](https://gitlab.gnome.org/GNOME/vte/-/blob/master/src/vtestream-file.h),
[`src/vtestream.h`](https://gitlab.gnome.org/GNOME/vte/-/blob/master/src/vtestream.h).

Not covered, and so not claimed: Alacritty, foot and WezTerm.

---

## Decisions

### D1 — The authoritative history is per **window**, and it lives in Core

Not per session. The mapping between sessions and panes is many-to-many in both directions: one
session prints into its main window *and* into every spawn window its triggers route to, and a
window's content is not always a session's (client replies from `/graphics` and `/triggers`, the
capture header, the demo scene, the web view). A per-session buffer can never be the authoritative
history for a spawn window, and a per-window buffer can be authoritative for everything.

So: **one history per window**, created with the window, destroyed with it (`CloseWindow` already
drops `_lines[id]` and `_freezePoints[id]`, `:6899`).

Keeping two parallel line stores in step is also the thing that reliably goes wrong. TinyFugue keeps
a `History` and a `Screen` per world and lets them diverge; the resulting "worlds lose PageUp
history" bug has been open since 2010 (see the client survey). We have the same shape today —
`WorldSession.Scrollback` and `SharpMUTermApp._lines`, both capped by the same setting, kept in step
by nothing at all.

`WorldSession.Scrollback` then has one honest job left — being the thing a session hands to a window
when the two are bound — and one dishonest one, being a second full copy of the same lines. The
replay at `:1447` exists because a session may hold lines before a window is bound to it. Under D1
the window's history is created by `PaneContentFor`/`RouteSpawn` and the session appends into it
directly, so the replay is only needed on paths where a session genuinely outlives its window. *That
set of paths must be enumerated before the session buffer is removed* — see deferred decisions.

### D2 — The record is `StyledLine`-shaped; markup is a derived cache

This is the general form of the timestamp fix, and it is the decision with the longest tail.

A `PaneLine` is already-rendered markup. Everything baked into it at append time is a setting that
cannot describe lines that have already arrived — which is exactly the shape of the reported
timestamp bug. Today the list of such settings is: the timestamp column (fixed, held apart), and
everything else (not fixed, because nothing else is currently a setting).

The things that will want to be render-time decisions, in rough order of likelihood:

| Decision | Why it must be render-time | What baking it costs |
|---|---|---|
| Timestamp gutter | Already is. `PaneLine.Stamp` | (the bug we already had) |
| Theme / palette change | Colours are resolved into `#rrggbb` by `MarkupFormatter` | Changing theme leaves history in the old palette for ever |
| Highlight rule added or edited | `StyledLine.RuleColor` is set by a highlight trigger *before* formatting | A new rule marks nothing already on screen — indistinguishable from a dead rule on a quiet connection |
| Link policy (`InteractionKind` handling, "don't make MXP `SEND`s clickable") | `[link=…]` spans are written into the markup by `MarkupFormatter` | A user who turns clickable sends off still has clickable history |
| Timestamp *format* (`HH:mm` vs `HH:mm:ss` vs date) | `PaneLine.Stamp` is pre-formatted, not a `DateTimeOffset` | A format change reaches only new lines — the same bug, one layer down |
| ANSI-to-theme mapping (16-colour remap) | Resolved at format time | Same as theme |
| Encoding | **Not** a render-time decision. Bytes are decoded once, at the wire, by `SessionEncoding`; a `StyledLine` is already text | — |

Note `Stamp` is *already* pre-formatted (`StampNow()` returns `"HH:mm"`), so the format is baked even
though the gutter is not. That is a live instance of the same bug class, sitting inside the fix for
it. Storing the `DateTimeOffset` and formatting in `Compose` closes it.

The decision: the stored record carries the `StyledLine` where one exists, plus the arrival instant,
plus the pre-rendered markup as a **cache** that is invalidated wholesale when any render-time
setting changes. One rule about that cache, learned from tmux: **it must be bounded by the record's
lifetime and must never accumulate variants.** tmux's extended-cell table appended a fresh slot every
time a cell was promoted and reused none; compacting on eviction took a 50,000-line history from
1.6 GB to 170 MB, but a line that is *redrawn in place* still grew without bound — a tool emitting
clear-to-EOL sequences drove 48 GB across three panes of about a thousand lines each. One memo per
record, replaced not appended. Sketch:

```
readonly record struct PaneRecord(
    StyledLine? Source,      // null only for lines that never had one
    string? Baked,           // markup for those; never both null
    DateTimeOffset? Arrived) // null for client chatter — the gutter describes a world's output
```

Rendering is `Source is { } s ? Format(s) : Baked!`, with the formatter's output memoised per record
and the memo dropped on a settings change. The memo is what keeps `FeedRange` cheap; the `Source` is
what keeps the setting honest.

The cost is memory: a `StyledLine` is a `StyledSpan[]` plus a lazy text projection, against one
string. On mostly-unstyled MU\* output that is roughly 2–4× per line. **That cost is why D3 and D4
exist**: with the feed windowed and the depth on disk, the in-memory line count drops by more than
the per-line size rises. Measure it (below) rather than assuming the ratio.

### D3 — The disk spill **lives**, re-pointed from the session to the window

Plainly: **it stays.** It is not deleted.

The case for keeping it is not sunk cost. It is that the spill is a correct, tested implementation of
precisely the thing D4 needs and cannot otherwise have. Read `ScrollbackBuffer`'s own doc comments:
absolute line indices that never come to mean a different line, `GetRange` clamping rather than
throwing so a stale scroll position degrades instead of failing, and `MaxRangeLines = 4096`
documented as "a windowed view asks for the rows it is about to draw, and a caller that wants
everything must page for it". This machinery was designed against the windowed feed. It has no
consumer because the consumer was never built, not because it is the wrong shape.

And once the feed is windowed, **memory depth and scroll depth decouple**, which is the whole prize:
the in-memory window shrinks to what the control and one page of paging need, and the depth the user
can reach grows to whatever the disk bound allows. Deleting the spill would be deleting the answer to
the question this document was asked.

What changes:

- The buffer is constructed per window, not per session (`WorldSession.cs:88` moves).
- `IScrollbackSpill` widens from `StyledLine` to the D2 record. The codec already exists
  (`StyledLineCodec`); it gains the arrival instant and a discriminator for baked-markup lines. The
  record framing (length + CRC-32 + payload) is unchanged.
- `LinesAppended` gets its first subscriber: the window's feed controller.
- `GetRange`/`GetTail` get their first production callers.

The terminal survey settles the "is a disk-backed scrollback a reasonable thing to build" question
outright, and not via kitty — kitty's pager history turns out to be an in-memory byte ring, not a
disk store. **Konsole ships our design**: `HistoryScrollFile` is an append-only cell stream plus an
index stream of one offset per line, giving O(1) access to line *N*, which is `FileScrollbackSpill`
arrived at independently. **VTE ships a more ambitious version** — 64 kB blocks, compressed and
AES-GCM encrypted. So the machinery is not exotic; it is what two mainstream terminals do with
exactly our problem.

And there is a second reason the survey turned up, which is stronger than the first: **the spill is
most of a BeipMU restore log.** BeipMU is the parity target and is the only client surveyed whose
scrollback survives a restart — `restore.dat`, on by default, replayed into the window with a
"Content restored" message. `FileScrollbackSpill` is already indexed, checksummed, segmented,
bounded and reclaimed; the *only* thing making it ephemeral is that it deletes itself on dispose.
Turning that into a choice is a small change to a file we would otherwise be deleting, and it buys a
feature no other client in the survey has. It also fixes BeipMU's own mistake in passing: their ring
is sized in kilobytes and their changelog records a hang when a single block larger than the ring
arrives, because it "would get stuck trying to make room forever." Ours is bounded by lines *and*
bytes and reclaims a whole segment at a time, so it cannot livelock the same way.

**But the terminals disagree with the restore log, and their reason is good.** Konsole's history file
is a temporary file `unlink()`ed *immediately after opening*, so it is reachable only through the
open descriptor and cannot survive a crash — a deliberate privacy mitigation, and the same instinct
behind our deleting the spill on dispose. VTE is the counter-example that shows the price: it can
afford a persistent spill because it **encrypts** it. So the honest statement is that restore logs
are not "stop deleting the file"; they are "stop deleting the file, *and* encrypt it, *and* decide
what happens to passwords typed into a world that echoes them." Blightmud's own logging help already
warns "typed passwords and usernames will be logged, don't share your logs without thinking", and we
have a `secrets.json` at `0600` precisely because this client takes that seriously. That is a
feature-sized decision, not a flag, and it is deferred as one.

What honestly argues against keeping it, stated so the decision is reversible on evidence:

- **Nobody else does this.** Every client surveyed keeps scrollback in RAM. Mudlet holds 100,000
  lines and MUSHclient's author's position is "no particular reason not to just make it large anyway,
  as the memory is allocated dynamically". "Hold 50,000 records in RAM and delete 959 lines of disk
  store" is a defensible answer with industry backing, and measurement (4) is what decides between
  them.
- It is **ephemeral today**, so as it stands it buys depth within a session only. The restore-log
  argument above is a *future* justification, not a current one, and if that feature is never built
  the spill is carrying its weight on paging alone.
- 200,000 lines and 64 MB **per window** is a lot of churn; the bounds were written for a per-session
  store and want revisiting per-window (see D5). Mudlet's memory complaints are the warning here —
  their maintainer notes people have already set more than one console to maximum "and complained
  about it."
- If the answer to "how deep" turns out to be "20,000 lines, in memory, once the feed is windowed",
  the spill earns nothing on paging. If measurement (4) says 50,000 in-memory records fit comfortably,
  **the decision reduces to the restore-log question alone** — and should be re-argued on that basis
  rather than kept out of loyalty.

What is *not* negotiable either way is `ScrollbackBuffer` itself. The ring, the absolute indices and
the clamping `GetRange` are what D4 needs and what makes the Blightmud crash class impossible; the
spill sits behind a pluggable `IScrollbackSpill` that is already null-by-default. Deleting the disk
implementation would not touch any of that.

### D4 — The feed is windowed, and the window is a function of `AutoScroll`

The tension is real: the framework's scroll behaviour is good and free, but it only scrolls what the
control holds; feed the control one screenful and there is nothing to scroll.

Three shapes were considered.

**(a) The control holds exactly the viewport; we own the scroll position.** This is what every
terminal in the survey does, and it is rejected here **only because we do not own the renderer**. It
throws away `ScrollablePanelControl` entirely — the wheel, the drag-select across rows, the clamping,
the `Scrolled` event that `SyncScrollbackState` is built on, and `CanScrollUp`/`CanScrollDown` which
`ScrollbackStatus` and `ToOldest` both read. Worse, it makes us compute "one page" in *display rows*
over content that **soft-wraps** (`MarkupControl.Wrap` defaults true, `MarkupControl.cs:58`), and the
number of display rows a logical line occupies is not knowable without parsing it at the current
width. Terminals do not have that problem because their lines are a fixed height and reflow is a
separate, explicit pass. We would be reimplementing the part they spend the most code on. Don't —
but note that the clean version of (a) is an *upstream* change (a line-accessor `MarkupControl`), and
that it remains the right long-term shape.

**(b) The control holds a large fixed slice; the framework scrolls within it; we re-window at the
edges.** The classic virtualised list. Correct in principle, but the re-window has to *compensate*
the scroll offset so the content does not jump, and the compensation is in display rows we do not
know until after a paint. Workable; every re-window is a hazard.

The tool for the compensation exists and is better than expected:
**`ScrollablePanelControl.ScrollToPosition(vertical, horizontal = 0)` is public**
(`.Scrolling.cs:275`), it re-syncs viewport metrics from the arranged bounds before clamping, and it
routes through the private `ScrollVerticalTo` — so, unlike `ScrollVerticalBy`, it does **not** touch
`AutoScroll`. That is exactly the setter a re-window needs. One caveat, and it is the whole of hazard
(1) below: unlike `ScrollToBottom` it does **not** call `RefreshContentHeightIfLaidOut()`
(`.Scrolling.cs:180`), so its clamp reads a `_contentHeight` that a just-issued `SetContent` has not
refreshed. A re-window must therefore place the offset on the frame *after* the feed — or upstream
should add that one call, which is a second, genuinely one-line PR alongside the incremental-append
one.

**(c) Adaptive: a bounded tail while pinned, a wide slice while scrolled back.** Recommended. This is
(b) with a policy that makes the hazardous case rare and the frequent case constant-cost, and it
hangs the policy off the one bit the framework already maintains.

`AutoScroll` **is** the "showing the live tail" bit. `SyncScrollbackState` already mirrors it rather
than keeping a second copy. Hang the window policy off the same bit:

| State | What the control holds | Append behaviour | Cost |
|---|---|---|---|
| **Pinned** (`AutoScroll == true`) | The newest `TailWindow` records — viewport height plus one page of margin | Append, and trim the head by the same count | O(TailWindow), a constant we choose |
| **Detached** (`AutoScroll == false`) | A wide slice — up to `MaxRangeLines` (4,096) records around the scroll position | **Nothing.** The reader is not looking at the tail; the line goes into the history and badges the tab unread, which is already what happens | Zero |
| Transition pinned → detached | Widen once, on the `Scrolled` event | One `SetContent` of the wide slice, with the offset placed so the visible rows do not move | One deliberate gesture — the `RepaintPanes` budget |
| Transition detached → pinned (⌃End, or scrolling back to the bottom) | Narrow to the tail window | One `SetContent` | One deliberate gesture |
| Scrolling past the wide slice's edge | Re-window (shape (b)) with offset compensation | Rare: every ~4,000 records, not every screenful | One gesture, hazardous — see below |

Why the margin can be small: the reader cannot scroll up without a gesture, and the gesture *is* the
signal to widen. `ScrollVerticalBy` with `lines < 0` detaches `AutoScroll` and raises `Scrolled`
(`.Scrolling.cs:145-152`), so the first PgUp both detaches and gives us the hook to widen before the
next paint. The margin only has to cover the rows that gesture moves, i.e. one page.

Why the wide slice can be large: while detached there are no appends to re-parse, and every scroll
within the slice is a **cache hit** (the parse key does not change, only the panel's offset). The
parse cost is paid once, at the gesture, and never again until the reader leaves the slice.

The two hazards, named:

1. **Offset placement on a re-window.** Both widen and re-window change what the control holds, which
   changes `TotalContentHeight` in display rows by an amount that depends on wrapping. The visible
   rows must not move. `ScrollToPosition` is the right setter, but it clamps against a content height
   that the feed has not yet refreshed, so the sequence is *feed → let a frame settle → place* rather
   than *feed → place*. That is the same class of problem `SettleScroll`/`RenderWholeFrame` already
   exist for headlessly. This is the part of the design that needs a prototype before it is trusted,
   and measurement (6) is that prototype.
2. **`ViewportHeight` is a frame-late quantity.** `TailWindow` is derived from it, and a freshly
   arranged panel reports 0. The window size must have a floor and must never collapse to nothing on
   an unarranged panel, or the first frame of a new pane shows one line.

Both are reasons the wide slice should be generous rather than tight: fewer re-windows, fewer
chances to get the compensation wrong.

And both want tmux's discipline around them: `window_copy_sync_backing` computes the incremental
update from generation counters, **then checks that the arithmetic reproduces the expected size**,
and falls back to a full rebuild when it does not. The equivalent here is cheap — after a re-window,
the anchor and the held count predict a content height; if the settled frame disagrees, drop the
optimisation and re-feed from the anchor with the offset placed by measurement. An incremental path
you are allowed to trust is one that checks itself.

Sketch — one of these per output *region*, so a frozen pane has two and the web view has none:

```
sealed class PaneFeed                     // lives beside the viewport in _paneScrolls
{
    long   Anchor;                        // absolute index of the first held record
    int    Held;                          // how many records the control holds
    bool   Pinned => _panel.AutoScroll;   // never a second copy of this bit

    void OnAppended(long index);          // pinned: shift the tail. detached: nothing.
    void OnScrolled();                    // crossed an edge, or re-attached → re-window
    void JumpTo(long index);              // ⌃Home, a search hit, a freeze point
}
```

The two methods that touch a control are still `AppendWindowLine` and `FeedRange` — this replaces
what decides *which* range, not the seam itself.

### D5 — Depth: `ScrollbackLines` becomes the depth, and the memory window stops being it

Today `ScrollbackLines` means three things at once: what the session buffer keeps in memory, what the
pane buffer keeps in memory, and how far the user can scroll. Under D3+D4 they separate:

| Quantity | Today | Proposed |
|---|---|---|
| What the control holds | Everything the pane buffer holds (up to 10,000) | `TailWindow` (~viewport + one page) pinned; ≤ `MaxRangeLines` detached |
| In-memory records per window | 10,000 (twice: `StyledLine` + markup) | A ring sized for paging without disk — candidate 2,000–4,000, **to be measured** |
| Retrievable depth (`ScrollbackLines`) | 10,000 | The user-facing number. Candidate default 50,000; the old 20,000 stops being dangerous because the control never holds it |
| Spill bound | 200,000 lines / 64 MB per **session** | Per **window**, and the bounds want lowering — a client with eight windows is eight of these. Candidate 50,000 / 16 MB per window with a global ceiling |

The point of the table is that the number the user sets stops being the number that makes the client
slow. That is the entire behavioural change this design buys, and it is why raising the default is
safe *after* D4 and reckless before it.

### D6 — Search belongs here, and it belongs in Core

We have no scrollback search. Every client surveyed has one and it is the single most-cited feature
in the complaints (see research). It belongs in this design and not in a later one, for a specific
reason: **search is the thing that decides the representation.** A search over baked markup has to
strip markup per line and will match on `#rrggbb`; a search over `StyledLine.Text` — a projection
that already exists and is already lazily cached (`StyledLine.cs`) — is exact and free. D2 is
partly justified by D6.

It also decides the storage. Searching content that is partly on disk means either paging it back
through `GetRange` (correct, and bounded — the spill's whole design is one seek per record) or
keeping a plain-text side index. Page it back first; an index is an optimisation with a measurement
behind it, not a starting position.

**Shape — and windowing decides it.** `tmux`'s copy-mode is the reference implementation of
scrollback search in a terminal, and its behaviour is *in-place incremental*: type, and every match
highlights where it sits in the pane. That behaviour is exactly the one a windowed feed cannot cheaply
give, because highlighting all matches requires the matches to be *in the control*, and the control
holds a slice. Jumping to a match means re-windowing (D4's hazard) on every keystroke.

So the first cut is a **match list**, not in-place highlighting — and the client already has the
architecture for it. `HistorySearchPrompt` (the pure "what does this keystroke mean" type) and
`HistorySurface` (the host that owns nothing but framework calls) are the ⌃R command-history surface,
backed by `Core.Input.HistorySearch.Match`, which already returns
`HistoryMatch(text, index, matchStart, matchLength)` — an index into a corpus plus the span that
matched. Scrollback search is the same three pieces with a paged corpus: a `ScrollbackSearch` in Core
returning absolute line indices and match spans, the same prompt semantics, the same
`history-search`-shaped snapshot views, and ⏎ jumping the pane to that absolute index (one
`GetRange`, one re-window — a deliberate gesture, once).

In-place highlight-all stays on the table as a second cut once the wide slice is proven: within the
held slice it is free (mark the records, drop the memos, re-feed the slice), and outside it the match
list is still the honest answer. Say so in the UI rather than pretending the pane is searching what it
cannot see. Note that this is what tmux actually does: its mark bitmap is `xcalloc(sx, sy)` —
**viewport-sized, not history-sized** — and the full-history pass exists only to produce a count.

**Budget it, and degrade rather than block**, which is the other thing tmux gets right. Its
all-matches highlight runs under a 200 ms budget and, when that blows, re-scans visible lines only
and reports an approximate count (`X/Y+`); a 10 s hard cap abandons marks entirely. A search over
50,000 records, some of them on disk, on a UI thread that is also drawing a live MU\* session, needs
the same discipline: a deadline, a partial answer, and a truthful indicator that the answer is
partial. Two of its cheap wins transfer verbatim — a repeated search with an unchanged term does not
re-scan history, and a "regex" containing no metacharacters is downgraded to a substring search.

The corpus-scan itself needs no cleverness. GNU screen runs Boyer–Moore over a *virtual* flat address
space, reading character `q` as `WIN(q / w)->image[q % w]` and never materialising a flattened
buffer; `GetRange` is our `WIN` macro. No terminal or client in either survey builds a search index,
and we should not start with one.

---

## Seams

**Jump to the oldest line (⌃Home).** Today `ToOldest` calls `ScrollToTop` on the control and detaches
`AutoScroll` (`:2836`) — the oldest line *the control holds*. Under D4 that becomes the oldest line
of the wide slice, which is wrong. ⌃Home must mean "the oldest record this window can retrieve":
re-window to `[OldestIndex, OldestIndex + slice)` and scroll to the top of it. That is a single
`GetRange` and one `SetContent`, and it is the first thing that will exercise the disk path.

**Freeze (⌃F).** Freeze gets *cleaner* under D4, not harder. It is already two regions over one
buffer split at `_freezePoints[windowId]`; under D4 it is two regions with the two policies we
already have — the pinned half is permanently "detached" (a wide slice, no appends, this is exactly
what freeze means) and the live tail is permanently "pinned" (a bounded tail window). The freeze
point stops being an index into a trimming `List` and becomes an absolute line index, which removes
the walk-the-freeze-point-down code in `AppendWindowLine` (`:1713-1721`) entirely: an absolute index does
not move when the head is reclaimed. That is a small simplification falling out of the same change.

**Spawn windows.** They get a real history for the first time. `OnSpawnLine` already holds the
`StyledLine` (`:1871`); under D2 it stores it instead of discarding it, and under D3 a spawn window
spills like any other. This is the change that makes the per-window bound in D5 matter — a client
with a Chat spawn, a Combat spawn and three worlds is six histories, not one.

**The web view.** Out of scope, deliberately. `ShowWeb` sets page markup straight onto the control
and `RepaintPanes` already skips `WebWindowId` (`:1807`). It is a document, not a tail: `AutoScroll`
is false, its content is bounded by the page, and it has no arrival times and no history. It keeps
its `_paneScrolls` entry and its viewport and is simply never windowed. The one thing to check is
that `ScrollTarget`/`ScrollbackStatus` still read it correctly when the window policy is a per-region
property rather than a global one.

**Splits and pane rebuilds.** `ScrollViewFor` keeps viewports across rebuilds precisely so scroll
position survives; under D4 the *window* must survive with it, or a split would silently jump the
reader to the tail. The window state (anchor index + policy) belongs beside the viewport in
`_paneScrolls`, not in the control.

**Per-pane NAWS.** Windowing is NAWS-neutral **by construction, and only because of where the
rectangle comes from**: `PaneOutputRects` (`:6654`) derives from `RealisedPanes()`, i.e. the
`TabControl`'s arranged rectangle less its margin and header — never from the content control. So the
markup control's self-sizing width (it is `HorizontalAlignment.Left`) changing as long lines enter
and leave the held window cannot move a pane rectangle and cannot re-announce a terminal size. This
is worth a pin: `FocusIndicationTests.TypingDoesNotMoveAnyPaneRectangle` has the right shape, and the
counterpart is *a burst of output, and a scroll, do not move any pane rectangle*. It is also the
reason `ShowScrollbar` must stay false — a scrollbar that appears when the buffer passes one
screenful would do exactly what windowing must not.

**The input-height veto.** `SyncInputHeights` counts chrome rows and can shorten a pane; a shorter
pane means a smaller `ViewportHeight` and so a smaller `TailWindow`. The interaction is benign in one
direction (the window shrinks, appends get cheaper) and hazardous in the other: the veto runs from
`PaintStatus` at runtime, so a status-line length change can resize the viewport *between* a window
being sized and being fed. The floor in D4's hazard (2) covers this; the derived window must be
recomputed from the panel's arranged metrics on the frame it is used, not cached across frames.

---

## Upstream

Three things, in increasing size: a one-line fix on our critical path, an incremental append that is
nice to have, and the change that would make this whole design unnecessary.

### Incremental append in the parse cache

Worth doing, **not on the critical path**, and smaller in effect than the ~15-line estimate suggests.

The hook exists: `OnContentAppended` (`MarkupControl.cs:511`) already knows the mutation was an
append, and `ParsedContent.LineRowCounts.Length` is exactly the number of content entries the cached
parse was built from. So a cache entry can be recognised as a *prefix* of the current content. The
PR shape:

1. Split the version counter in two — a **structural** version bumped by `SetContent`, `Text=` and
   any removal, and a **content** version bumped by appends. Key the cache on the structural one plus
   the parsed prefix length.
2. On a miss, look for an entry matching on everything except length whose `LineRowCounts.Length <=
   snapshot.Count`. Extend it: parse groups from that length forward and append to `Rows`,
   `RowSourceLine`, `RowIsSoftWrapContinuation`, `RowLinks`; extend `LineRowCounts` and re-run the
   `RowPrefix` sum.
3. Guard the `[markdown]` coalescing: an appended entry can join a group opened by the previous
   entry. `MarkupParser.HasUnclosedMarkdownRegion` over the last kept entry is the cheap check; if it
   is open, fall back to a full parse. (Our panes never emit `[markdown]`, but the control is shared.)

Two honest caveats:

- `ParsedContent`'s collections are `required init` and `_cached` aliases the newest LRU slot, so the
  extension **must copy** the lists rather than mutate them, or one slot's growth corrupts another's
  view. That makes an append O(N) *reference copies* instead of O(N) *parses* — a very large constant
  factor (parsing a line is orders of magnitude more work than copying a reference), but not O(1).
  True O(1) needs a shared-extendable row structure, which is a much bigger PR.
- Our panes are non-`Stretch`, so they hold **two** live parse keys (fitted width and `int.MaxValue`
  from `NaturalContentWidth`). Both would need the same treatment to see the full benefit.

Because it is O(N)-copies rather than O(1), it makes a *large held window* cheaper but does not
remove the reason to window. **Do D4 first; offer this upstream afterwards, sized honestly.** Realistic
size: 40–60 lines plus tests, not 15.

### `ScrollToPosition` should refresh the content height

The genuinely one-line upstream PR: **`ScrollToPosition` should call
`RefreshContentHeightIfLaidOut()`** the way `ScrollToBottom` does (`.Scrolling.cs:262-269`), so a
position set immediately after a content change clamps against the new content rather than the old.
Without it, every re-window in D4 costs a settling frame. That is worth offering first — it is
smaller, it is arguably a bug, and it is on our critical path in a way the parse cache is not.

### The change that would make all of this unnecessary

For the record, because the terminal survey makes it unavoidable: the correct fix is a
**line-accessor `MarkupControl`**. Every terminal emulator and multiplexer examined gives its
renderer a `line_at(index)` function and a line count, and paints exactly the rows it was arranged
for; scrolling is then an integer assignment and nothing scales with history. A `MarkupControl` that
took `Func<int, string> LineAt` + `int LineCount` in place of a `List<string>`, parsed only the
arranged rows, and keyed its cache on `(firstRow, rowCount, width, …)` would delete D4 entirely — no
window policy, no anchor, no re-window, no offset compensation.

It is not proposed as the plan because it is a large change to a shared control's core, it interacts
with soft wrap (the row↔line mapping the current `RowPrefix` prefix-sum provides would have to be
computed lazily and cached), and we would be asking a maintainer to restructure a control on our
behalf. But it is the shape the whole industry converged on, this design is a workaround for its
absence, and if the maintainer ever asks what SharpConsoleUI most needs for text-heavy applications,
this is the answer.

---

## What to measure

The 11 / 50 / 116 ms figures came from a standalone harness driving the real control. That harness is
the model for everything below; each item says what would settle the argument.

1. **Parse amplification, directly.** `MarkupControl.TotalParseCount` is *public* and documented as
   "stops climbing once the parse cache is warm" (`MarkupControl.ParseCache.cs:31`). It counts one per
   logical line parsed. So the harness does not need a stopwatch: feed N lines into a pane holding M,
   read the counter delta, and the amplification factor falls out. This is also assertable as a
   **regression pin** — *appending one line to a pane whose window holds 300 records costs at most
   ~600 parses (two keys), not 10,000* — which is the test that stops D4 being un-done later.
2. **Does batching help at all?** `OnLine` is wired per line (`session.LinePrinted += … OnUi(…)`,
   `:1452`), so a 40-line read batch is 40 UI marshals and 40 version bumps. But the re-parse happens
   lazily in `EnsureParsed` at the next measure/paint, so 40 bumps between two paints may cost *one*
   parse. Measure with `TotalParseCount` before assuming coalescing (or `MarkupControl.AppendLines`,
   which already exists upstream and bumps once) is a win. If the render loop drains the UI queue
   before painting, this is worth nothing and should not be Stage 1.
3. **Does `Stretch` halve the append cost?** Making the pane control `HorizontalAlignment.Stretch`
   collapses `ComputeParseWidth` to one key and should halve every figure above. It also changes the
   control's arranged width, which affects background fill and link hit-testing — so this needs a
   `scrollback` snapshot compared cell-for-cell, not just a counter.
4. **The memory cost of D2.** Hold N `StyledLine`-backed records versus N markup strings on real MU\*
   output (mostly-unstyled prose with occasional colour) and measure the ratio. This is the number
   that decides D5's in-memory ring size and, if it comes back small enough, could reopen D3.
5. **Disk read latency on the paging path.** One `GetRange` of a page from the spill, cold. The store
   is one seek per record by design; confirm that a ⌃Home into a 50,000-line history is a single
   frame's work and not a visible stall. If it is not, the answer is a smaller `MaxRangeLines` for
   the disk-backed case, not a different store.
6. **Re-window offset error.** The prototype for D4's hazard (1): widen a pane at a known scroll
   position with wrapped content, settle a frame, and assert the same logical record is under the same
   screen row. Headless, through `FrameGrid.Decode`, in the manner `CaretOnScreen()` already reads the
   paint rather than the function.
7. **Search latency over the full depth, so the budget can be set from data.** tmux chose 200 ms for
   its all-matches pass and 10 s as a hard cap; ours should be chosen the same way — scan 50,000
   records (with the last 40,000 on disk) for a substring and for a regex, and see where the knee is.
   The output is two constants and a decision about what the partial-result indicator says.

---

## Staged plan

### Stage 1 — Cap the append cost. Nothing else.

Independently shippable, user-visible, no Core changes, no new storage, no new depth.

- Give each pane region a **window state** beside its viewport in `_paneScrolls`: an anchor and a
  policy derived from `AutoScroll`.
- While pinned, `AppendWindowLine` feeds the control the newest `TailWindow` records and trims its
  head. While detached, it feeds nothing.
- On the `Scrolled` event, widen to a slice around the position; on ⌃End (`BackToLive`), narrow.
- ⌃Home widens to the head of `_lines` (still the in-memory list — no disk yet).
- Ship it with the parse-amplification pin from *what to measure* (1) and a `scrollback` /
  `scrollback-up` / `freeze-scrollback` snapshot diff proving the frames are unchanged.

This alone removes the reason the default was halved, and it is the only stage that has to be right
about scroll-offset compensation. Everything after it is storage and depth.

Also in Stage 1, because they are small fixes in files the stage touches: correct
`AppConfiguration.cs:34` (it currently describes a feature that has no consumer), correct the
`PaneLine` / `CLAUDE.md` claim that a spawn window has no `StyledLine` behind it, and make **⌃End
un-freeze as well as scroll** — `BackToLive` re-arms `AutoScroll` and leaves the pane frozen, which
is BeipMU's [#79][bm79] verbatim and was fixed there because users expected both.

### Stage 2 — Unify the record

- Introduce the D2 record; store `StyledLine` where one exists (`OnLine`, `OnSpawnLine`), baked
  markup where one does not, and the arrival **instant** rather than a pre-formatted stamp.
- Memoise the formatter's output per record; drop the memo on a render-time settings change.
- `RepaintPanes` becomes "drop the memos and re-feed the *held window*" — which under Stage 1 is a
  few hundred records rather than the whole buffer, so the one expensive keystroke stops being
  expensive.
- Fold the timestamp-format bug (D2) closed in the same change.

### Stage 3 — One history per window, on the existing buffer

- Construct a `ScrollbackBuffer` per window; widen `IScrollbackSpill` and the codec to the D2 record.
- The feed controller subscribes to `LinesAppended` and reads through `GetRange`/`GetTail` — the
  consumers those members were written for.
- Retire the session's second copy once the paths where a session outlives its window are enumerated
  (deferred, below).
- Re-bound the spill per window (D5) and raise `ScrollbackLines`' default.

### Stage 4 — Search

- A `ScrollbackSearch` in Core over `StyledLine.Text`, returning absolute line indices and match
  spans — the paged counterpart of `Core.Input.HistorySearch.Match`.
- A match-list surface in the `HistorySearchPrompt`/`HistorySurface` split; ⏎ jumps the pane to the
  absolute index.
- Paging search through `GetRange` for the spilled portion; no plain-text index until measurement (5)
  asks for one.
- New snapshot views (`scrollback-search`, `scrollback-search-hit`) in the manner of
  `history-search`/`history-search-filter`.
- Highlight-all *within the held slice* is a follow-on, not part of this stage.

### Stage 5 — Upstream

Two PRs to `nickprotop/ConsoleEx`, in this order:

1. **`ScrollToPosition` refreshes the content height** before clamping, matching `ScrollToBottom`.
   One line; removes a settling frame from every re-window. Offer it as soon as Stage 1 proves it
   matters — it can ship against the clone immediately and reach a package later, exactly as the
   cursor-bounds fix did.
2. **Incremental append in the parse cache**, sized as above. Not blocking; the client is correct and
   fast without it.

---

## Deferred decisions

| Decision | What would settle it |
|---|---|
| Whether `WorldSession.Scrollback` is deleted or kept as a session-scoped feed | Enumerate every path where a session holds lines before a window is bound to it — startup ordering, `AdoptSessionOf`, reconnect, character switch. If the set is empty, delete it; if not, it stays as a hand-off queue and not a history |
| The in-memory ring size per window | Measurement (4): the byte cost of a D2 record on real output |
| Whether the spill survives at all | Measurement (4) again. If 50,000 records fit in the memory budget, D3 is reopened — say so then rather than keeping 959 lines out of loyalty |
| `TailWindow` and the wide-slice size | Measurements (1) and (6). Big enough that re-windows are rare, small enough that the widen gesture stays under a frame |
| Whether the pane control becomes `Stretch` | Measurement (3) plus a cell-for-cell snapshot diff |
| Whether scrollback search searches *all* windows or the focused one | User question, not a technical one. `tmux` searches the pane you are in; the client survey below shows no consensus. Ask the maintainer |
| Whether a spill can be promoted to a transcript ("save scrollback to file") | Adjacent feature, genuinely useful, and the reason not to do it casually is that `PlainTextLogSink`/`HtmlLogSink` already own that surface and the spill is deliberately not a log |
| Global vs per-window disk bound | Falls out of D5's per-window numbers once spawn windows have histories |
| **Restore logs** (BeipMU parity: scrollback survives a restart) | Maintainer's call, and it is a feature not a flag. Mechanically it starts at "stop deleting the spill on dispose", but Konsole `unlink()`s its history file *specifically* so it cannot survive, and VTE can afford a persistent spill only because it encrypts it. So the real question is whether we are willing to hold decrypted session text — including anything a world echoed back — between sessions, and at what protection. Size it in lines, not BeipMU's kilobytes |
| Whether scrolling up should itself split the pane (⌃F automatically) | Taste, and the consensus is against us: Mudlet, BeipMU and Blightmud all split on scroll-up and merge at the bottom, where ours is a deliberate mode. D4 already makes scroll-away a policy change, so wiring it to freeze is a small increment. Ask the maintainer |
| **What changing `ScrollbackLines` at runtime does** | Must be decided by Stage 3, because that is when the setting starts governing a buffer whose capacity is fixed at construction. The survey offers three answers and all three are bad: TinTin++ *wipes* the session's scrollback (`init_buffer` frees every line unless the size is unchanged — a realloc, not a resize); TinyFugue resizes one store and not the other, which is the bug above; GNU screen reallocates and rewraps the whole buffer and refuses the command outright while in copy mode. Ours should trim on a decrease and grow in place on an increase, losing nothing either way — the ring already grows geometrically, so only the shrink path is new |
| Whether the frozen pane's live-tail height is configurable | Blightmud hardcodes 10 rows and has an open complaint about it. Ours is whatever the layout gives, which may already be the better answer — check against a 4K terminal and an 80×24 one |

---

## Honest costs

- **Stage 1 owns scroll-position compensation.** Today the framework owns the offset entirely and we
  only read it. After Stage 1 we change what the control holds while the framework holds an offset
  into it, and getting that wrong is a visible jump on every PgUp. It is the riskiest stage and it is
  first on purpose — it is also the smallest, and it can be reverted without unwinding any storage.
- **A frame of latency at the transitions.** Widening and narrowing both change content during a
  gesture, and `AutoScroll` already moves the offset during paint and schedules its own relayout. The
  transitions will settle a frame late by construction; that is acceptable on a deliberate keystroke
  and would not be on every line, which is why appends must never trigger one.
- **D2 costs memory per line and buys correctness per setting.** It is a straight trade and the ratio
  is unmeasured.
- **Per-window spilling multiplies disk churn by the number of open windows**, and spawn windows are
  cheap to open. The per-window bounds in D5 are a guess until someone runs a client with six windows
  for an evening.
- **None of this survives a restart as designed**, and for stages 1–4 that is deliberate: deep
  scrollback is a within-session convenience and the transcript is `PlainTextLogSink`/`HtmlLogSink`,
  opt-in and kept. But the parity target does not agree — BeipMU's restore logs are on by default and
  are the one thing in the survey we would not have. That is a real gap, it is named in the deferred
  decisions, and it is the argument that would keep `FileScrollbackSpill` alive even if paging alone
  did not.
