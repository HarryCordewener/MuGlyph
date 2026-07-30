# SharpMUTerm — feature comparison and market position

Written 2026-07-30 against `bae09c2`, from two evidence bases:

- an inventory of **what this client actually does**, classified works / partial / wired-but-unreachable
  / config-only / absent, with `file:line` citations
- a survey of the client landscape, including **315 r/MUD threads spanning 2012–July 2026**, the Mudlet
  2025 user survey (162 responses), the icesus.org 2026 client guide, and archived MUSH-side forums

Both are in the session scratchpad. Where they disagree with our own README, the code wins.

---

## 1. The finding that should shape everything else

**Feature count does not drive client choice.** Across fourteen years of r/MUD, nobody chose a client
because it listed GMCP. They chose a client because *someone had written a package for their game*, and
switching cost then locked them in — sometimes twenty-five years past a client's last release. There is
no "I left MUSHclient" thread in the entire corpus.

Two consequences:

- **Protocol bullet points are table stakes to have and near-worthless to advertise.** Corpus frequency
  across 315 threads: GMCP 48, MSDP 11, MCCP 2, NAWS 2. And where servers do emit GMCP there is *no
  schema* — no agreed representation of hit points or mana — so every HUD is bespoke to one game anyway.
- **The same is true of automapping.** It is what made zMUD dominant, it is a top-three Mudlet feature,
  and it is also the feature Mudlet users most often try and abandon. **Not one client in the MUSH/MUCK
  niche has one, and no community page has ever asked for one.**

What *is* cited repeatedly as unmet, specifically by terminal users: **documentation for non-initiates,
flood-proof scrollback, and panes without shelling out to tmux.** All three are quality, not features.

That is the market we are actually in.

---

## 2. Where we stand out

Verified against the landscape survey. "Rare" means few or no comparable clients ship it.

| Capability | Us | The field |
|---|---|---|
| **Native panes, splits and tabs in a terminal client** | Works — split tree, drag-to-split, zoom, move-window-to-pane, per-pane focus | **Rare.** Terminal users are told to use tmux. Named a top-three unmet need in the corpus |
| **Per-pane NAWS** — each session told *its own pane's* rectangle, rate-limited with a trailing flush | Works | **No evidence any other client does this.** Others report the whole window, so a split lies to every server in it |
| **Two characters on the same world, connected simultaneously** | Works — separate sessions, separate windows, separate automation | Uncommon; several clients tie one connection to one window |
| **Link security model** — `<A HREF>` and `<SEND>` are disjoint kinds decided by the parser, scheme-tagged, and a click resolves to the window it came from | Works, end-to-end tested | **Not discussed anywhere**, because most clients have the bug. A world cannot make a link send, and a link in a background pane cannot send to the focused character |
| **Secrets separated from shareable config** — `config.json` holds a meaningless GUID, `secrets.json` is `0600` | Works | Most clients store credentials in the world file. Pasting a config is a known way people leak passwords |
| **Inline images over the Kitty protocol** | Works (half-block fallback universal) | Effectively unique among terminal MU\* clients |
| **CHARSET negotiation with an honest readout** — shows `utf-8`, `utf-8 assumed`, or `iso-8859-1 forced` | Works | Most clients pick an encoding and say nothing |
| **Refusals that explain themselves** — every unavailable command reports why, and a which-key panel dims what cannot run | Works | Rare; the usual failure is silence |

The first three are the defensible position. **Panes-without-tmux is the single clearest fit between
what we have and what the corpus says terminal users cannot get.**

---

## 3. Where we are behind

| Capability | Field | Us | Does it matter? |
|---|---|---|---|
| **Reachable scripting** | Every serious client | **Lua exists and the client never constructs it.** Unreachable | **Yes — this is the gap that matters most.** Packages are the documented adoption driver |
| **Package/plugin ecosystem** | Mudlet one-click packages; MUSHclient plugins | None | **Yes.** This is *the* reason people pick clients, per the corpus |
| **GMCP consumed** | Mudlet, IRE clients, Blightmud | Received, parsed, **dropped**. We never send `Core.Supports.Set`, so most servers send nothing | Yes for MUDs, largely no for MUSHes |
| **Onboarding / in-app help** | Varies | **No F1, no in-app help at all** | **Yes.** "Documentation for non-initiates" is the most-cited unmet need |
| **Automapper** | Mudlet, CMUD, TinTin++ (ASCII) | None | **No** for our niche — nobody in MUSH/MUCK has one or asks |
| **Sound (MSP)** | Most GUI clients | None | Marginal |
| **Speedwalk** | TinTin++, Mudlet, CMUD | None | Marginal for MUSH; expected on MUDs |
| **Tab completion** | Rune (July 2026) shipped it as novel | None | Small but cheap, and visibly modern |
| **Command stacking / separator** | Most clients | None for typed input | Moderate |
| **Copy to clipboard** | All | Paste in works, copy out does not | **Yes** — a terminal client that cannot copy is annoying daily |
| **Screen-reader support** | MUSHclient (mature), VIP Mud | None, untested | Yes if we want that audience — it is small, vocal, and badly served |
| **Prompt display** | All | Parsed via EOR/GA and **never shown** | Yes; prompts are how MUD players read state |

---

## 4. What we claim and do not have

Correct these before any marketing copy exists.

| Claim | Where | Reality |
|---|---|---|
| GMCP-driven status bar with HP/EN meters | `README.md:87`, `docs/PLAN.md:113` | Does not exist. Also the wrong *location* — vitals are session-scoped and belong in a pane |
| Lua scripting with hot-reload | `README.md:78` | Delivered as a library; the client never constructs a host |
| Tab completion | `docs/PLAN.md:95` | Not implemented |
| Inline image viewer, map view, GMCP stat panes | `docs/PLAN.md` M3 | Only the web view draws an image |
| Scrollback deeper than memory | `AppConfiguration.cs:34` | The spill writes to disk and is **never read back**. Effective depth is the in-memory 10,000 lines |

---

## 5. Niche reality

The MUSH/MUCK split is stark and quantified. In the corpus: TinyMUX **0** files, PennMUSH 2, MUCK 4,
MUX 6, MUSH 24 — against Alter Aeon 24, Aardwolf 21, IRE ~30. **There is no MUSH subreddit.** MUSH
questions on r/MUD get answered by referral off-site, and that off-site is disappearing: MU Soapbox
schismed ~2022 and its domain is dead, MUSHList and Mustard are dead, PennMUSH.org went down.

This cuts both ways. The audience we are aimed at has **no venue**, which makes discovery hard — and
also means nobody is serving them. BeipMU is the incumbent and is Windows-only.

Server-side evidence supports narrowing the protocol ambition: **PennMUSH implements SGA, LINEMODE,
NAWS, TTYPE, MSSP, CHARSET, GMCP, Pueblo, WebSockets and TLS — and no MXP, MCCP, MSDP, MSP or EOR**,
with maintainers on record that "a protocol's existence does not merit its implementation." Our MXP and
Pueblo parsers are substantial and Pueblo is the one this niche actually uses.

---

## 6. Where to spend

**Fix what we claim.** Everything in §4 is a promise we are currently failing. Cheapest credibility
available.

**Make Lua reachable.** It is written, tested and sandboxed, and the client never constructs it. This is
the difference between "no scripting" and "scripting", and packages are the documented adoption driver.
Nothing else on this list has a comparable ratio of value to remaining work.

**Send `Core.Supports.Set`.** Until we ask, most servers send nothing, so every GMCP consumer we build
sits idle. One message unblocks the entire category.

**Then lean into panes.** It is our clearest differentiator and it matches a stated unmet need. Vitals
as a *pane type* rather than status-bar chrome is the right shape and is already being designed.

**Write the getting-started documentation**, and add in-app help. The most-cited unmet need in the
corpus, and we have literally none.

**Do not build an automapper.** No client in our niche has one, nobody asks, and it is the feature
Mudlet users most often abandon.

**Reconsider before building:** MSP/sound and speedwalk are MUD conventions, not MUSH ones. Copy-to-
clipboard and tab completion are small, daily, and cheap — better value than either.

---

## 7. Honest caveats

- The r/MUD corpus is **combat-MUD dominant**, so the loudest demands are not necessarily our users'.
- The Mudlet survey is existing Mudlet users answering a Mudlet survey.
- MUSH-side quotes are second-hand: MU Soapbox is dead and Wayback was rate-limiting.
- Public MU\* discussion is in structural decline and moving to Discord. Any sentiment analysis works
  from a shrinking corpus, and this one will age faster than the feature comparison.
- Our own evidence is uneven: the Tui suite is genuinely end-to-end, but `TcpTransport` has **no test
  file at all**, so TLS and IPv6 are build-verified only, and nothing can verify a rendered image.
