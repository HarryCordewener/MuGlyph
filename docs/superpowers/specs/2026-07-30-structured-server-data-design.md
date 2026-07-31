# Structured server data: GMCP, MSDP, MSSP

**Date:** 2026-07-30
**Status:** proposed — design only, nothing here is implemented
**Companions:** [`2026-07-30-status-bar-design.md`](2026-07-30-status-bar-design.md) (what global chrome
shows) and [`2026-07-30-vitals-pane-design.md`](2026-07-30-vitals-pane-design.md) (the first real
consumer of everything below). This document is the supply side; those two are the demand side. Nothing
they ask for is absent here, and nothing here exists only for them.

---

## 1. Problem

The client speaks three structured-data protocols in the sense that its telnet library negotiates
them and hands us parsed messages. It does nothing with any of them, and it never asks a server for
anything.

**Verified, on `main` at `83f7171`:**

| Claim | Evidence |
|---|---|
| GMCP is received, parsed, then dropped | `TelnetSession.OnGmcpAsync` → `WorldSession.GmcpReceived` (`WorldSession.cs:280`) → two subscribers |
| …subscriber 1 is the Lua bridge, which is never constructed | `grep -rn "Scripting" src/SharpMUTerm.Tui/*.cs` returns nothing. The Tui project *references* `SharpMUTerm.Scripting` in its `.csproj` and never touches it. `WorldSessionScriptBridge.Attach` has no caller anywhere in `src/`. |
| …subscriber 2 is `GmcpStats.Update`, whose readers have none | `Summarize()`, `GetInt()` and `HasData` have zero callers (`GmcpStats.cs:68-100`) |
| `GmcpStats` is app-wide, so two characters' vitals merge | `SharpMUTermApp.cs:187` — `private readonly GmcpStats _stats = new();`, fed from *every* session at `:1468` |
| We never send GMCP | `SendGmcpAsync` (`ITelnetSession.cs:46`, `TelnetSession.cs:560`) is called only by the two test doubles |
| No `Core.Hello`, no `Core.Supports.Set` | neither string appears in the repository |
| MSDP and MSSP have zero subscribers | `WorldSession.cs:281-282` re-raise into nothing |
| We state no client name or terminal type | no `TTYPE`/`MTTS`/`TerminalTypes` reference exists in `src/` |

Two documents also promise a feature built on data we throw away — `README.md:87` ("a GMCP-driven
**status bar** with HP/EN meters") and `docs/PLAN.md:113` ("GMCP-driven **stat line**"). `docs/PLAN.md:46`
names a `GmcpRouter` component that was never written. Those are requirements to design, not features to
describe; §11 says what to do with the text.

**The single most consequential gap is that we never speak first.** Most GMCP servers send nothing until
asked. A client that negotiates the option and then stays silent gets an empty stream from the majority
of worlds that support the protocol, which is indistinguishable — from our side — from a world that
does not support it at all.

### 1.1 What the library actually does today, measured

Everything in this subsection was obtained by driving a client-mode `TelnetInterpreter`
(TelnetNegotiationCore 2.6.0, built exactly as `TelnetSession.BuildInterpreterAsync` builds it) with
synthetic server bytes and dumping what it wrote back. It is not read off the source.

**We open with one option and no more.** The only bytes the interpreter emits at build time are
`IAC WILL NAWS` (`FF FB 1F`). We never send `IAC DO GMCP` / `DO MSDP` / `DO MSSP` proactively; those
`DO`s are only ever *replies* to a server's `WILL`. That is defensible for GMCP (servers that support it
almost always offer) but it means a server which waits to be asked, and never volunteers, is never
asked. See §4.1.

**The TTYPE cycle, verbatim:**

```
round 1  IAC WILL TTYPE, then  IAC SB TTYPE IS "TNC"       IAC SE
round 2                        IAC SB TTYPE IS "XTERM"     IAC SE
round 3+                       IAC SB TTYPE IS "MTTS 3853" IAC SE
```

So every server we have ever connected to believes it is talking to a client called **`TNC`** — the
library's initials — on a plain `XTERM`. `3853` decodes to ANSI + UTF-8 + 256 COLORS + TRUECOLOR +
MNES + MSLP + SSL. It does **not** claim MOUSE TRACKING (16), which we support, and it does claim
MSLP (1024), which we do not implement. `TelnetInterpreter.TerminalTypes` is public-get and
**private-set**, so this cannot be corrected through the library's public API at all. See §7.

**GMCP receive works.** `IAC SB 201 Char.Vitals {"hp":"1000","maxhp":"1200"} IAC SE` arrives as the
tuple `("Char.Vitals", "{\"hp\":\"1000\",\"maxhp\":\"1200\"}")`. GMCP *send* is public and already
wrapped: `TelnetInterpreter.SendGMCPCommand(string, string)`.

**But a GMCP message is silently truncated at 8192 bytes, and this is not a corner case.** Measured, by
feeding oversized subnegotiations and reading the delivered payload length:

| sent | delivered |
|---|---|
| `Ab ` + 20 000 bytes | package `Ab`, payload **8189** bytes — 3 + 8189 = 8192 |
| `Abcdefghijk ` + 20 000 bytes | package `Abcdefghijk`, payload **8180** bytes — 12 + 8180 = 8192 |
| `Ab ` + exactly 8189 bytes | unchanged |
| `Ab ` + 8190 bytes | truncated to 8189 |

The cap is **8192 bytes for the whole message**, package name and separator included, and the excess is
dropped with no error, no log line and no signal of any kind. `TelnetInterpreter.MaxBufferSize` — public,
settable, default 5 MiB — is a *different* limit and does not govern this.

The consequence is that **truncated, invalid JSON is the normal outcome for a large package, not an
exceptional one.** `Char.Items.List` on a MUD with a full inventory, a room player list on a busy MUD, or
any map package will exceed 8 KiB routinely. So the malformed-JSON path in §9 is not defensive
programming against a hostile server; it is the path a well-behaved server puts us on. Two things follow:
our own size cap is moot (the library caps below anything we would choose), and the message that goes to
the client message log should say *truncated* where the length is exactly 8192, because "invalid JSON"
would send someone hunting for a server bug that is ours.

**And an empty payload never reaches us at all.** `IAC SB 201 Char.Vitals IAC SE` — a package name with
no payload, which is a legitimate signal — raises no callback. Whatever we design for that case is
unreachable through this library, so it is not designed for; §9 records it as swallowed rather than
handled.

**Four more measured behaviours, each a real server divergence:**

| sent | what we get |
|---|---|
| `Char.Vitals{"hp":1}` — **no space** between package and payload | **nothing.** No callback at all. Some servers omit the separator; against those we are deaf, and it is indistinguishable from a server that sends nothing. |
| `char.vitals {"hp":1}` | delivered with case preserved — so package comparison must be case-insensitive on our side (§9) |
| `Char.Vitals  {"hp":1}` — two spaces | payload delivered as `" {\"hp\":1}"`, leading space included. Trim before parsing. |
| `Char.Items.List ["a","b"]` | delivered; a top-level array is a legitimate payload and the store must hold one (§5.2) |

The no-separator case is the sharpest of these because the failure is *silent and total*: a world that
spells GMCP that way looks exactly like a world with no GMCP, and the only way to tell is a packet
capture. It is worth an upstream report alongside the truncation.

One more, not a library defect but worth knowing: the GMCP payload is decoded with the interpreter's
`CurrentEncoding`, so a non-ASCII payload is mangled if that has not been seeded. `TelnetSession`
already seeds it (`SeedInterpreterEncoding`, and `CLAUDE.md` explains why at length) — but a harness
that builds an interpreter without doing so will see `René` come back as `Ren??`, which is a confusing
way to discover the store is fine and the transport was not.

**MSDP receive works, and nesting survives.** Feeding

```
IAC SB 69 VAR "ROOM" VAL TABLE_OPEN
            VAR "VNUM"  VAL "6008"
            VAR "EXITS" VAL ARRAY_OPEN VAL "n" VAL "e" ARRAY_CLOSE
          TABLE_CLOSE IAC SE
```

yields `{"ROOM":{"EXITS":["n","e"],"VNUM":"6008"}}`. The library normalises MSDP's byte framing into
JSON for us, which is a genuine convenience: it means one storage model can hold GMCP and MSDP alike
(§5).

This contradicts `CLAUDE.md`, which lists MSDP under "**MCCP, MSDP, MXP, and Pueblo are our own app
layer**" while its own dependency notes two sections later say the library "negotiates MCCP/MSDP/MXP
itself". The dependency note is the accurate one for MSDP and MCCP: both are negotiated and handled by
TelnetNegotiationCore, and MSDP payloads arrive parsed. MXP and Pueblo *payload* parsing genuinely is
ours (`Core/Protocols/MxpParser.cs`, `PuebloParser.cs`) — the library only negotiates the MXP option.
The locked-decisions line should be corrected to say so, because a plan that budgets for writing an
MSDP parser is budgeting for work that is already done, and the work that is actually missing (an MSDP
*sender*, §4.4) is not on it.

**MSDP send does not exist.** `MSDPProtocol` exposes exactly one member of interest, `OnMSDPMessage`.
The `MSDPServerHandler` / `MSDPServerModel` pair with `Report`/`UnReport`/`ResetAsync` is the *server*
half of the protocol — it is what a MUD written against this library would use to answer a client. There
is no client-side `SendMSDPCommand`. Without one we cannot send `LIST`, `REPORT`, `SEND` or `RESET`,
which is to say we cannot use MSDP for anything. Three ways out, in §4.4.

**MSSP parsing in the library is lossy, and silently so.** Sending eighteen well-formed MSSP variables
returned only the scalar string/int ones. Dropped without a word:

- every **list-valued** official field — `CODEBASE`, `FAMILY`, `REFERRAL`;
- every **boolean** field — `ANSI`, `UTF-8`, `XTERM 256 COLORS`, `PUEBLO`, `MSP`, …;
- every **unknown** variable — `DISCORD`, and anything a server invents.

`MSSPConfig.Extended` is a `Dictionary<string, object>` that looks like it exists to catch the last
category. It is never populated; it came back empty in every run. Minimal repro: send `NAME`,
`CODEBASE`, `ANSI`, `WIBBLE` and only `Name` survives. On top of that our own `MsspConfigReader.Render`
flattens any `IEnumerable` with `string.Join(", ", …)`, so a populated `Extended` would render as
`[WIBBLE, made up], [DISCORD, https://…]` — one string, in one cell, under one key.

`CODEBASE` and `FAMILY` are two of the five things anyone actually wants from MSSP. This settles a
question §8 would otherwise have had to leave open: **we parse MSSP ourselves.**

---

## 2. Prior art

Every claim here is cited. Where clients or servers disagree, the disagreement *is* the finding — GMCP
is a de-facto standard, and "what a client must tolerate" is mostly a list of things one server does
that the spec does not mention.

**Housekeeping first:** `tintin.mudhalls.net`, the domain this project's earlier notes reference, does
not resolve. `tintin.sourceforge.io/protocols/*` now 301s to **`https://mudhalla.net/tintin/protocols/*`**,
which is where every Scandum spec cited below lives.

### 2.1 GMCP is two specs, and you need both

The **transport** spec — negotiation, framing, message format — is
<https://mudhalla.net/tintin/protocols/gmcp>, and it *deliberately refuses to define `Core.*`*: "Each
MUD server is expected to define and document its own packages."¹

The **package** spec — `Core.Hello`, `Core.Supports.*`, `Core.Ping` — came from the defunct
mudstandards.org and survives at <https://www.gammon.com.au/gmcp>² (Nick Gammon's preservation of Mike
Potter's original), at <https://nexus.ironrealms.com/GMCP>³, and in the
[Achaea GMCP Spec PDF (2014-03-11)](https://www.achaea.com/local/Achaea_GMCP_Spec_20140311.pdf)⁴. A
revived <https://mudstandards.org/> carries the most current package index.⁵

That split matters here because a client that implements only the first has no `Core.Hello`, which is
approximately our current situation.

Negotiation, verbatim from the transport spec: option **201**; `IAC WILL GMCP` from the server,
`IAC DO GMCP` from us; "The client should never initiate a negotiation, however, if this happens the
server should abide by the state change."¹ Framing is
`IAC SB GMCP <package.subpackage.command> <data> IAC SE`, and — importantly for §1.1 — "The `<data>`
field is optional and should be separated from the package field with a space. **When sending a command
without a data section the space should be omitted.**"¹

**Package names are case-insensitive; JSON keys are case-sensitive.** All four sources say this
identically.¹⁻⁴ In the wild both spellings ship: IRE sends `Char.Vitals`, Aardwolf sends
`char.vitals`.⁶

**The payload is not always a JSON object.** Gammon enumerates the legal forms — `null`, `12345`,
`99.95`, `true`, `"Hello World"`, an array, an object, or *nothing at all* — and warns: "Some JSON
parsers allow primitive Value data outside of arrays and objects, but some do not. If your JSON parser
does not allow primitive Values outside of arrays or objects, this can be easily solved by adding `[`
and `]` characters around the `<data>` value."² Both MUSHclient GMCP plugins do literally that:
`if not string.match (params, "^[%[{]") then params = "[" .. params .. "]" end`.⁷

And there is genuinely non-JSON traffic. Aardwolf accepts client→server commands that are bare
unquoted words — `request char`, `group on`, `config autoexit` — and its own wiki says "Most of the
standard tags such as 'core.' are in JSON format, most of the Aardwolf specific tags are not."⁶

**IRE ships numbers as strings.** `Char.Vitals { "hp": "4500", "maxhp": "4800", … }`, with the wiki
noting "It is generally safe to assume that the known values are numbers (even though encoded as
strings), but other datatypes can be present".³ Aardwolf sends real JSON numbers.⁶ Any client that
reads a vitals figure must accept both.

**`Comm.Channel.Text` embeds raw ANSI** as `` escapes.³ Mudlet's parser rewrites a literal ESC
byte to `\\u001B` before parsing, with the comment "replace ANSI escape character with escaped version,
to handle improperly passed ANSI codes" — i.e. some servers send the raw byte, which is not valid JSON.⁸

### 2.2 `Core.Supports.Set`: the number after the package is contested

This is the single biggest interop hazard in GMCP and it is worth stating flatly.

| Source | What the number means |
|---|---|
| Nexus wiki³, mudstandards.org⁵ | the module **version** — "a positive non-zero integer" |
| Achaea GMCP Spec PDF⁴ | "the module name and **whether it is enabled**" — and Remove: "any appended 1 or 0 is ignored" |
| Aardwolf, empirically⁶ | strictly `0` or `1`; its debug output is `GMCP Error: core.supports.set -> setting should be 0 or 1 - received 3` |

**Send `1`.** It is the only value that is correct under all three readings. Mudlet is the one client
that ships anything else — `Char.Login 2` — and it does so against IRE servers specifically.⁸

Set/Add/Remove semantics from the package spec:² `Set` replaces any earlier list wholesale; `Add`
behaves like `Set` if none was sent, and "the new version number takes precedence over the previously
sent one, **even if the newly sent number is lower**"; `Remove` ignores version numbers entirely.

### 2.3 What each client sends on connect

| Client | `Core.Hello` | `Core.Supports.Set` |
|---|---|---|
| **Mudlet**⁸ | `{ "client": "Mudlet", "version": "<ver><build>" }` | `["Char 1", "Char.Skills 1", "Char.Items 1", "Room 1", "IRE.Rift 1", "IRE.Composer 1", "Client.Media 1", "Char.Login 2"]` (+ `External.Discord 1` conditionally) |
| **MUSHclient** (Gammon plugin)⁷ | `{ "client": "MUSHclient", "version": "<Version()>" }` | `["Char 1", "Comm 1", "Room 1"]`, hard-coded |
| **MUSHclient** (Aardwolf plugin)⁷ | same plus `"ident": "<persistent GUID>"` | identical hard-coded three |
| **BeipMU**⁹ | `{"client":"Beip", "version":"<build>"}` | `["WebView 1", "Beip.Stats 1", "Beip.Tilemap 1", "Beip.Id 1", "Client.Media 1"]` |
| **Nexus**³ | `{ "client": "Nexus", "version": "3.1.90" }` | — |
| **Blightmud**¹⁰ | `{"version":"<ver>","client":"blightmud"}` | **never sends `Set`** — only per-module `Core.Supports.Add ["<mod> 1"]`, and no modules by default |
| **TinTin++**¹¹ | **nothing** — no built-in GMCP at all; a script must send it |

Two observations that bear directly on §4.3. Every client that ships a default set ships a *small* one
— three to eight packages, `Char` and `Room` in all of them, `Comm.Channel` in none of them. And
Mudlet's is the only one that is not hard-coded at the call site; its `gmod` layer
(<https://github.com/Mudlet/Mudlet/blob/development/src/mudlet-lua/lua/GMCP.lua>) is a
**reference-counted module manager** so two scripts cannot disable each other's packages, and
`enableModule("IRE.Rift")` sends *every prefix of the path*: `Core.Supports.Add ["IRE 1","IRE.Rift 1"]`.⁸

Both MUSHclient plugins carry the same apologetic comment about their hard-coded list — "This
hard-coded block may need to be made into a config table as we add more message types."⁷ That is the
argument for §4.3's per-world setting, written by someone who did not build it.

### 2.4 Storage and dispatch: Mudlet is the model, with one correction to make

**Mudlet** writes into a per-profile Lua global `gmcp`, splitting the dotted path and auto-creating
nested tables, **preserving the server's exact case** — so `gmcp.char.vitals` and `gmcp.Char.Vitals`
both exist in the wild depending on the server.⁸ It raises **one event per path prefix**; the source
comment is unambiguous:

> `// events: for key "foo.bar.top" we raise: gmcp.foo, gmcp.foo.bar and gmcp.foo.bar.top`
> `// with the actual key given as parameter e.g. event=gmcp.foo, param="gmcp.foo.bar"`⁸

**And it replaces subtrees rather than merging them, by default.** This is the correction to make,
because it is the opposite of what §5.2 originally proposed:

> `// only merge tables (instead of replacing them) if the key has been registered as a`
> `// need to merge key by the user default is Char.Status only`⁸

`Host.cpp` seeds that allowlist with exactly one entry: `mGMCP_merge_table_keys.append("Char.Status")`.
The reason is documented on the IRE side — `Char.Status` sends deltas, "messages only contain changed
values"³ — while everything else sends complete state.

Replacement is not merely the default, it is **required** for collection-valued packages. Aardwolf's
wiki says so directly: "As with room exits, group information needs to be **cleared with each GMCP data
refresh** so that members who have left the group do not remain in the data."⁶ The Aardwolf MUSHclient
plugin goes further and nukes the whole `room` subtree on `room.info`, with a comment explaining it was
added to stop exits accumulating across rooms.⁷

§5.2 is rewritten accordingly.

Other clients, briefly:

- **MUSHclient** has no built-in GMCP; a plugin answers `OnPluginTelnetRequest(201, "WILL")` and sends
  the handshake from the `SENT_DO` callback.¹² There is **no `TelnetSubnegotiationSend` function** in
  MUSHclient's 418-function API, so plugins IAC-stuff by hand and call `SendPkt`.⁷ The Aardwolf
  plugin's model is unusual and instructive: `BroadcastPlugin` announces **only which message
  arrived**, and consumers pull the data back with `CallPlugin(id, "gmcpdata_as_string", …)`.⁶
- **TinTin++** — and this corrects a common belief — has **no built-in `$gmcp{}` table and does not
  auto-accept GMCP or MSDP.** Its negotiation table has parsers for `IAC SB GMCP`/`MSDP` but no entry
  for `IAC WILL GMCP`, so a script must reply `#EVENT {IAC WILL GMCP} { #SEND {\xFF\xFD\xC9}; }`.¹¹ ¹³
  Everything else is events: `IAC SB GMCP` with `%0 module %1 data %2 plain data`.¹⁴ `$gmcp{}` is a
  *user-script convention*, not a feature. Note also "**Only one event can be defined for each event
  type**".¹⁴
- **Blightmud** implements GMCP/MSDP/MSSP/TTYPE in bundled Lua over generic Rust primitives; scripts
  get `gmcp.on_ready`, `gmcp.register(module)`, `gmcp.receive(module, cb)`, `gmcp.send`.¹⁰ Data reaches
  scripts as a **raw JSON string with the leading space retained**, and `gmcp.receive` **replays the
  last cached body immediately on registration** — a nice property for a pane that is created after the
  data arrived.
- **BeipMU** sends its handshake eagerly on `WILL GMCP` and offers `Connection.SetOnGMCP(fn)` (one
  callback per connection, every message as one undivided raw string) plus a WebView-scoped
  prefix-filtered variant.⁹ Its prefix matching requires a following dot, returns on first match, and
  is case-sensitive — three behaviours worth *not* copying.
- **Nexus** has a single global `onGMCP` reflex with `gmcp_method` and `gmcp_args`, and no persistent
  table at all; a script that wants to keep something calls `client.set_variable`.¹⁵

**No client filters inbound packages against its own `Core.Supports.Set`.** Mudlet, both MUSHclient
plugins, Blightmud and BeipMU all accept and process whatever arrives. §3's principle 5 is the
consensus, not a preference.

### 2.5 Multi-session isolation — TinTin++ has the model to copy

This is the question `GmcpStats` gets wrong, and the two clients that support several simultaneous
worlds answer it the same way.

**TinTin++**: variables are per-session throughout (`ses->list[LIST_VARIABLE]`), a new session gets a
**copy** of the global session's lists (gated on `#CONFIG {INHERITANCE}`), and events are dispatched
with a session pointer, so a GMCP message in one session fires only that session's handlers.¹¹ Crossing
sessions is **explicit syntax**: `#{name} {command}` runs a command in a named session without changing
the active one, and `@<name>{text}` parses text in another session and prints the result here.¹⁶ So
`@sessionB{$gmcp[Char][Vitals][hp]}` is how you read another session's data.

**Mudlet**: one profile per window, each `Host` owning its own Lua interpreter, so `gmcp`, `msdp` and
`mssp` are per-profile globals in per-profile states. `raiseEvent` is profile-local; crossing profiles
is opt-in via `raiseGlobalEvent` — which carries an explicit warning that "execution control is handed
to the receiving profiles so that means that long running events may lock up the profile that raised
the event".¹⁷ That is a synchronous cross-profile design worth *not* copying.

**MUSHclient**: one world per window, plugins per world, plugin state saved "on a per-plugin, per-world
basis"; cross-world access requires `GetWorld`/`GetWorldById`.¹²

The shape is unanimous: **per-session state by default, explicit and awkward syntax to cross.** §5.3
is that shape, with `WindowSession(windowId)` as the "explicit syntax".

### 2.6 MSDP

Spec: <https://mudhalla.net/tintin/protocols/msdp>.¹⁸ Option **69**; `MSDP_VAR` 1, `MSDP_VAL` 2,
`TABLE_OPEN` 3, `TABLE_CLOSE` 4, `ARRAY_OPEN` 5, `ARRAY_CLOSE` 6. "Variables and values cannot contain
the NUL, MSDP_VAL, MSDP_VAR, MSDP_TABLE_OPEN, MSDP_TABLE_CLOSE, MSDP_ARRAY_OPEN, MSDP_ARRAY_CLOSE, or
IAC byte."

**MSDP's advantage over GMCP is that it is self-describing**, and the GMCP spec concedes the point:
"Unlike GMCP, MSDP provides the client with a list of supported variables when requested, so it's not a
requirement for MUD servers to provide documentation on the packages it supports."¹ `LIST` takes
`COMMANDS`, `LISTS`, `CONFIGURABLE_VARIABLES`, `REPORTABLE_VARIABLES`, `REPORTED_VARIABLES`,
`SENDABLE_VARIABLES`. The five commands the spec calls essential are `LIST`, `REPORT` (subscribe,
push-on-change), `UNREPORT`, `SEND` (one-shot pull) and `RESET`.¹⁸

**A parser trap that §4.4's design must accommodate.** Client→server argument lists are deliberately
looser than server→client arrays: "To minimize the implementation burden for servers, the client is
expected to send command arguments as simple value reassignments" — repeated `MSDP_VAL` with **no**
`ARRAY_OPEN`/`CLOSE` — while "When responding to the client, the server should use a proper array if
applicable."¹⁸ Mudlet's decoder carries a flag literally named `no_array_marker_bug` for this.⁸ We only
send, so we only need the loose form; but anything we write must not assume the strict one.

Standard variables include `CHARACTER_NAME`, `HEALTH`, `HEALTH_MAX`, `MANA`, `MANA_MAX`, `MOVEMENT`,
`MOVEMENT_MAX`, `EXPERIENCE`, `OPPONENT_HEALTH`, `ROOM` (a table of `VNUM`/`NAME`/`AREA`/`COORDS`/
`TERRAIN`/`EXITS`), and the client-settable `CLIENT_NAME`, `CLIENT_VERSION`, `PLUGIN_ID`.¹⁸ Note that
`HEALTH` "may be absolute or a percentage depending on the MUD" — the spec says "Use 0-100 for
percentages" without requiring it, so a consumer cannot assume.¹⁸

**Who runs which.** MSDP is the Diku/Merc/ROM/SocketMUD lineage; GMCP/ATCP2 is Rapture/IRE plus
Aardwolf, MUME, Avatar and Genesis.¹ Both can coexist through **MSDP-over-GMCP**, implemented in
Scandum's server-side MTH snippet¹⁹ — the MSDP payload wrapped in a GMCP package literally named
`MSDP`, where uniquely "the package name is considered case sensitive and MSDP must be fully
capitalized".¹ In practice, though, Mudlet's manual says plainly: "some servers don't both send MSDP
and GMCP at the same time, so even if you enable both in Mudlet, the server will choose to send only
one of them."²⁰ That supports §4.4's staging: MSDP buys server coverage, not new capability.

Client support: **Mudlet** (off by default; on enabling it sends `LIST COMMANDS` and its
`CLIENT_NAME`/`CLIENT_VERSION`)⁸, **Blightmud** (full, decoded to Lua tables, auto-sends
`LIST REPORTABLE_VARIABLES`, with `msdp.get/set/register/report/unreport/list/send`)¹⁰, **TinTin++**
(parser + events only)¹⁴. **MUSHclient: zero occurrences of `MSDP` in the source. BeipMU: option 69
exists only as a debug pretty-print label.**⁹ ¹²

### 2.7 MSSP — and the finding that changes what our INFO screen is

Spec: <https://mudhalla.net/tintin/protocols/mssp>.²¹ Option **70**; `MSSP_VAR` 1, `MSSP_VAL` 2. Sent
**on connect, before login**. Required fields `NAME`, `PLAYERS`, `UPTIME`; then a long generic,
categorisation, world, protocol, commercial and hiring set.

Three rules that shape a parser and a screen:

- **A variable may repeat, and "the last reported value should be used as the default value…
  multiple values should be ordered from least to most relevant."**²¹ So `PORT` legitimately arrives
  three times. A dictionary of `name → single value` loses this; §8.3's `name → list` does not.
- **"Variable names should exist of upper case letters and may contain spaces."**²¹ `CRAWL DELAY`,
  `XTERM 256 COLORS`, `PAY TO PLAY`. Clients "can substitute spaces with underscores as the recommended
  solution" — but must accept the spaces on the wire.
- **`-1` means "not available"** for numeric world variables.²¹ An INFO screen must render that as
  *unknown*, never as `-1 rooms`.

**Who consumes MSSP: crawlers, overwhelmingly.** The spec's own framing is "a transparant protocol
**for MUD crawlers**".²¹ Live and crawling as of today: MudVerse²², MUD MSSP Stats (Iberia)²³,
MudStats.com²⁴ (which the spec page still mislabels "defunct"), Grapevine²⁵. Grapevine's behaviour is
worth knowing anyway: it announces TTYPE `Grapevine`, waits 10 s for `IAC WILL MSSP`,
falls back to a plaintext `mssp-request\n`, gives up at 20 s, and **stops the instant MSSP arrives** —
it never logs in.²⁵ Note also that `CRAWL DELAY` exists and **no live crawler was found that honours
it**.

**Who displays MSSP: almost nobody, and nobody in a server browser.** The spec's Clients list has two
entries.²¹ In detail:

- **Mudlet** — enabled by default, exposes a Lua `mssp` table, `lua mssp` dumps it, Ctrl+9 prints it.
  **`dlgConnectionProfiles.cpp` contains no MSSP references at all** — the connection dialog and the
  bundled game list show nothing from it.⁸ Its one genuinely user-facing use is excellent and we should
  steal it: `cTelnet::promptTlsConnectionAvailable()` reads the `TLS`/`SSL` and `HOSTNAME` fields and
  offers *"A more secure connection on port %1 is available… Update to port %1 and connect with
  encryption?"*⁸
- **TinTin++** — accepts MSSP **only in debug mode**. `client_recv_will_mssp()` sends `IAC DO MSSP`
  only inside `if (HAS_BIT(ses->telopts, TELOPT_FLAG_DEBUG))`.¹¹ With debug off it never asks.
- **Blightmud** — supported; sets an `MSSP` tag in the top bar, plus `mssp.get()` / `mssp.print()`.¹⁰
- **MUSHclient, BeipMU** — not supported at all.⁹ ¹²
- **No client anywhere has a `/mssp` command or an MSSP-driven server browser.**

So the INFO screen §8.4 designs is, as far as this survey can tell, **genuinely novel**. That is a
reason to be careful rather than pleased: nobody has discovered the ergonomics for us, and §8.4's three
states are guesses that want a user in front of them.

**PennMUSH implements MSSP**, which matters twice over — it is the SharpMUSH family's lineage and the
most likely server anyone tests this against. `report_mssp()` in `src/bsd.c` auto-sends nine fields:
`NAME`, `PLAYERS`, `UPTIME`, `PORT`, `SSL`, `PUEBLO`, `CODEBASE` (`"PennMUSH %sp%s"`), `FAMILY`
(hardcoded `"TinyMUD"`), `WEBSITE`; everything else comes from `mush.cnf` `mssp NAME/value` lines.²⁶
Two gotchas for the crawler: Penn probes with `IAC DO LINEMODE` first and only sends its telnet-option
block once the client proves telnet-awareness (60 s timeout), so a raw-socket crawler never sees
`IAC WILL MSSP`; and its plaintext fallback compares `!strcmp(command, "MSSP-REQUEST")` —
**case-sensitive uppercase** — while Grapevine and MudVerse both send lowercase `mssp-request`.²⁶

### 2.8 TTYPE / MTTS

Spec: <https://mudhalla.net/tintin/protocols/mtts>.²⁷ Note the polarity inversion relative to
GMCP/MSDP/MSSP: the **server** sends `IAC DO TTYPE` and the **client** replies `IAC WILL TTYPE`.

Three rounds, then a repeat. Round 1 is the client name, "preferably in all caps. Appending the client
version is optional." Round 2 is the terminal type: "Console clients should report the name of the
terminal emulator, other clients should report one of the four most generic terminal types" — `DUMB`,
`ANSI`, `VT100`, `XTERM`, optionally with `-256COLOR` or `-TRUECOLOR`. Round 3+ is `MTTS <bitvector>`,
and "Receiving the same terminal type twice indicates to the server that the end of the list of
available terminal types has been reached."²⁷ `IAC DONT TTYPE` resets the cycle.

```
1 ANSI    2 VT100    4 UTF-8    8 256 COLORS    16 MOUSE TRACKING
32 OSC COLOR PALETTE    64 SCREEN READER    128 PROXY
256 TRUECOLOR    512 MNES    1024 MSLP    2048 SSL
```

| Client | round 1 | round 2 | MTTS |
|---|---|---|---|
| **TinTin++**¹¹ | `TINTIN++` | `$TERM` | computed; VT100 claimed only when *not* split; mouse sets 16 **and** 1024; MNES always |
| **Mudlet**⁸ | `MUDLET` (+ version only if opted in) | `ANSI-TRUECOLOR` | 2349 typical |
| **Blightmud**¹⁰ | `BLIGHTMUD` | `$TERM` uppercased (default `XTERM-256COLOR`) | 271 |
| **BeipMU**⁹ | user preference | `ANSI` | 269 |
| **us, today** | `TNC` | `XTERM` | 3853 |

**Why round 1 is load-bearing**, in Mudlet's own words:

> "Some servers use KaVir's protocol snippet, which expects the client to provide both its name and a
> decimal version number during Telnet TTYPE negotiation. However, including a version number is not in
> accordance with the relevant RFCs as the period character is not permitted therein; so since 2024,
> Mudlet has stopped sending it by default. **As a result, servers that rely on this information may
> assume Mudlet is version 1.0 or earlier, and consequently restrict color support to 16 colors instead
> of enabling 256-color mode.**"⁸

Mudlet now auto-detects the KaVir snippet and re-enables the version. That is a concrete case of the
client name changing what a server sends, and it is the answer to "does anyone actually look at this".
RFC 1010's constraint is real and Mudlet enforces it: up to 40 characters from uppercase letters,
digits, hyphen and slash — so Mudlet maps `.`→`/` and space→`-` and truncates.⁸

Two of our current claims are wrong on their face against that table: **nobody else claims MSLP (1024)
except TinTin++, and only as a side effect of mouse tracking**, and we claim it while implementing
nothing. And every other terminal client reports a *real* terminal type — `$TERM`, uppercased —
where we report a constant `XTERM`.

### Sources

1. GMCP transport spec: <https://mudhalla.net/tintin/protocols/gmcp>
2. GMCP package spec (mudstandards preservation): <https://www.gammon.com.au/gmcp>
3. Nexus GMCP wiki: <https://nexus.ironrealms.com/GMCP>
4. Achaea GMCP Spec, 2014-03-11: <https://www.achaea.com/local/Achaea_GMCP_Spec_20140311.pdf>
5. mudstandards.org (revived): <https://mudstandards.org/gmcp/core>, <https://mudstandards.org/gmcp/core_hello>
6. Aardwolf GMCP: <https://www.aardwolf.com/wiki/index.php/Clients/GMCP>
7. MUSHclient GMCP plugins: <https://raw.githubusercontent.com/fiendish/aardwolfclientpackage/MUSHclient/MUSHclient/lua/gmcphelper.lua>, and `plugins/GMCP_handler_NJG.xml` in <https://github.com/nickgammon/mushclient>
8. Mudlet source: `src/ctelnet.cpp`, `src/TLuaInterpreter.cpp`, `src/Host.cpp`, `src/mudlet-lua/lua/GMCP.lua`, `src/mudlet-lua/lua/LuaGlobal.lua` — <https://github.com/Mudlet/Mudlet>
9. BeipMU: <https://github.com/BeipDev/BeipMU/blob/master/Documentation/GMCP.md>, `src/Telnet.cpp`, `src/WebView.cpp`
10. Blightmud: `resources/lua/gmcp.lua`, `msdp.lua`, `mssp.lua`, `ttype.lua`, `resources/help/gmcp.md` — <https://github.com/Blightmud/Blightmud>
11. TinTin++ source: `src/telopt_client.c`, `src/variable.c`, `src/session.c` — <https://github.com/scandum/tintin>
12. MUSHclient plugin callbacks: <https://www.gammon.com.au/mushclient/doc/general/plugin_callbacks.html>; function index: <https://www.gammon.com.au/mushclient/functions/>
13. GMCP in TinTin++ tutorial: <https://www.legendmud.org/index.php/GMCP_TinTin++_Tutorial>
14. TinTin++ `#event`: <https://mudhalla.net/tintin/manual/event.php>
15. Nexus GMCP data: <https://nexus.ironrealms.com/GMCP_Data>
16. TinTin++ `#session`: <https://mudhalla.net/tintin/manual/session.php>; `#config`: <https://mudhalla.net/tintin/manual/config.php>
17. Mudlet `raiseGlobalEvent`: <https://wiki.mudlet.org/w/Manual:Lua_Functions#raiseGlobalEvent>
18. MSDP spec: <https://mudhalla.net/tintin/protocols/msdp>
19. MTH (MUD Telopt Handler): <https://github.com/scandum/mth>; KaVir's snippet: <https://github.com/scandum/msdp_protocol_snippet_by_kavir>
20. Mudlet supported protocols: <https://wiki.mudlet.org/w/Manual:Supported_Protocols>
21. MSSP spec: <https://mudhalla.net/tintin/protocols/mssp>; news log: <https://mudhalla.net/tintin/protocols/mssp/news.php>
22. MudVerse MSSP: <https://www.mudverse.com/mssp>
23. MUD MSSP Stats: <https://iberia.jdai.pt/mudstats/mudlist>
24. MudStats: <http://mudstats.com/>
25. Grapevine MSSP: <https://grapevine.haus/mssp>
26. PennMUSH `src/bsd.c`, `src/conf.c`, `game/mushcnf.dst`: <https://github.com/pennmush/pennmush>
27. MTTS spec: <https://mudhalla.net/tintin/protocols/mtts>

---

## 3. Principles

These fall out of the codebase's own history as much as from the protocols.

1. **Ask, then listen.** A client that negotiates an option it never uses is worse than one that
   refuses it: it costs the server a handshake and tells it we want data we will discard.
2. **Storage is per session, always.** `GmcpStats` is the counter-example already in the tree. Two
   characters on the same world are two sessions; two characters on *different* worlds may use the same
   package names for entirely different things.
3. **A pane's chrome resolves through `WindowSession(windowId)`, never `_active`.** This codebase has
   fixed misdelivery through `_active` three separate times (a link clicked in a background pane, pane
   selection moving without the command line following, and ⏎ itself). Structured data is a fourth
   opportunity to make the same mistake, so the API must make the right thing the easy thing: the
   accessor takes a session, and there is no parameterless overload.
4. **The read loop is not free.** GMCP arrives on `TelnetSession.ReadLoopAsync`. A world that sends
   `Char.Vitals` every combat round at 4 rounds/second, across six connected characters, is 24
   parse-and-store operations a second. Everything in §6 is sized against that, and §10 states the cost
   honestly rather than claiming there isn't one.
5. **Unknown is not invalid.** A package we did not subscribe to, a variable we have never heard of, a
   JSON shape we did not expect — all are stored and none are errors. Discarding them makes the client
   useless on any world we did not personally test against, and makes a Lua script strictly less
   capable than the client it runs inside.
6. **Nothing is decided at receive time that describes data already received.** The same rule the
   timestamp gutter taught (`CLAUDE.md`): a display decision baked in on arrival reaches only rows yet
   to arrive. Consumers read the store and render; the store stores.

---

## 4. What we say on connect

### 4.1 The option handshake

Keep the current posture — answer `WILL` with `DO`, do not open with a barrage of `DO`s — with one
addition. Today we volunteer only `IAC WILL NAWS`. Volunteering `IAC DO GMCP` as well costs three bytes
and covers the server that supports GMCP but waits to be asked. `DO MSDP` and `DO MSSP` are *not*
volunteered: MSDP is the rarer protocol and MSSP is offered by every server that has it (it exists to
be scraped, §8), so the byte is wasted.

**Deferred:** whether `DO GMCP` should be per-world configurable. It becomes a question the moment a
server is found that behaves badly on an unsolicited `DO`; nothing known does. Settled by a report.

### 4.2 `Core.Hello`

Sent once, immediately on GMCP becoming enabled, before anything else:

```json
Core.Hello { "client": "SharpMUTerm", "version": "<informational assembly version>" }
```

Two fields, both strings. `version` should be the informational assembly version — a server matching on
it wants to know which release, not which build.

**There is currently no version to send.** Neither `Directory.Build.props` nor any `.csproj` sets
`<Version>`, and `.github/workflows/release.yml` — which triggers on `v*` tags — passes no version into
the build, so every published binary reports the SDK default `1.0.0.0`. Sending that would be worse than
sending nothing: it is a number that looks like an answer and will still say `1.0.0.0` after the tenth
release. Setting a real version, derived from the tag in the release workflow, is a prerequisite for
this field and is worth doing on its own account. One string, one place, reaching this field and the
MTTS client-name round (§7).

**When is "GMCP becoming enabled"?** `ProtocolPluginManager.IsPluginEnabled<GMCPProtocol>()` is public.
Poll it after each read batch, exactly where `ReportEncodingIfChanged()` already runs, and send on the
false→true transition. That is a boolean read on a path that already does one; it needs no new hook, no
reflection, and no upstream change. It is also the *only* correct moment: sending earlier is sending
into an unnegotiated option, and sending on a timer is a race.

**And it must be a transition, not a once-per-connection flag**, because GMCP is torn down and
re-established mid-session. The spec is explicit about copyover: "before the actual copyover, the MUD
server should send `IAC WONT GMCP`, the client in turn should fully disable GMCP" (§2.1). A
false→true edge detector handles that for free; a `_helloSent` boolean does not, and would leave us
silently unsubscribed after every server reboot. `IAC DONT TTYPE` resets the terminal-type cycle the
same way (§2.8), and the same reasoning applies there.

### 4.3 `Core.Supports.Set`

Sent immediately after `Core.Hello`, in the same batch.

The cost model is the reason this needs an argument rather than a list. Subscribing to a package is
subscribing to **every message that package will ever send**, for the life of the connection, on the
read loop. Asking for everything on a busy world is a real cost — `Comm.Channel` on a large MUD is the
public channel firehose, and `Room.Players` re-sends on every arrival and departure. Asking for too
little means the client has no data and looks broken.

The proposed opening set, and why each is on it:

| Package | Why |
|---|---|
| `Char 1` | The umbrella under which `Char.Vitals`, `Char.Status`, `Char.Name` all arrive. This is the vitals pane's entire supply. Every client surveyed asks for it (§2.3). |
| `Char.Vitals 1` | Named explicitly as well as under `Char`, because some servers match the exact string rather than the prefix — and because Mudlet's `gmod` sends every prefix of a path for the same reason (§2.3). Redundant on a correct server and free. |
| `Room 1` | Room name and exits. Cheap (one message per movement) and the obvious second consumer after vitals. In every client's default set (§2.3). |

**The number must be `1`.** Its meaning is genuinely contested — the Nexus wiki and mudstandards.org
call it a version, the Achaea spec calls it an enable flag, and Aardwolf hard-rejects anything else with
`setting should be 0 or 1 - received 3` (§2.2). `1` is the only value correct under all three readings.
Mudlet is the sole client shipping anything higher (`Char.Login 2`) and does so against IRE
specifically. Do not treat this field as a version negotiation; it is not one, reliably.

And what is deliberately **off** at stage 1:

| Package | Why not |
|---|---|
| `Comm.Channel` | The firehose. Worth having once something consumes it — routing channel traffic to a spawn window is the obvious use — and worth nothing before that. |
| `Char.Items` | Inventory deltas on every pick-up and drop. Large payloads, no consumer. |
| `IRE.*`, `Client.*` | Vendor packages for features (a GUI map, a button bar) this client does not have. Subscribing would make a server render work for a client that will drop it. |

**Configurability.** The set is a per-world setting with the above as the default, editable on F5. That
is not gold-plating: it is the only honest answer to a protocol where the useful package list is a
property of the *server*, and it is the difference between "the client works on my MUD" and "the client
works on the three MUDs its author tested".

**Deferred:** whether to follow `Core.Supports.Set` with `Core.Supports.Add` when a world's set is
edited mid-session, or to require a reconnect. Adding is strictly better if servers honour it; whether
they all do is a question for testing against real worlds, not for reasoning.

### 4.4 MSDP

MSDP needs `LIST`, then `REPORT`, and we cannot send either through the library's public API (§1.1).
Three routes:

1. **Our own plugin.** `ITelnetProtocolPlugin` and `TelnetProtocolPluginBase` are public,
   `TelnetInterpreterBuilder.AddPlugin(ITelnetProtocolPlugin)` is public, and
   `IProtocolContext.SendNegotiationAsync(ReadOnlyMemory<byte>)` is the raw subnegotiation writer. A
   small plugin that claims no option and exists only to emit `IAC SB 69 … IAC SE` is entirely within
   the supported surface. **This is the recommended route.**
2. **Write to the transport directly.** `TelnetSession` owns its `ITransport` and could emit the frame
   itself. It works — MCCP compresses the server→client direction only, so nothing on the outbound path
   is framed by the library — but it races with the library's own writes and puts protocol bytes in a
   class that has so far only ever handed protocol bytes *to* the library. Fallback, not plan.
3. **Upstream.** A `SendMSDPCommand(string variable, string value)` on `TelnetInterpreter`, mirroring
   `SendGMCPCommand`. The repository owner is the library's author, and `CLAUDE.md` says to extend it by
   PR rather than work around it. This is the right long-term answer and it does not block route 1.

The MSDP conversation, once we can speak it:

```
→ MSDP_VAR "LIST" MSDP_VAL "REPORTABLE_VARIABLES"
← MSDP_VAR "REPORTABLE_VARIABLES" MSDP_VAL ARRAY_OPEN … ARRAY_CLOSE
→ MSDP_VAR "REPORT" MSDP_VAL "HEALTH"          (one REPORT per variable)
→ MSDP_VAR "REPORT" MSDP_VAL "HEALTH_MAX"
…
```

We ask what is reportable rather than assuming the standard names, and then subscribe to the
intersection of what the server offers with what we want. A server that reports a variable we did not
ask for is stored anyway (principle 5).

**MSDP is stage 3.** It is the rarer protocol, it is the one that needs new library capability, and
every consumer it would feed is also fed by GMCP. It earns its place by making the client work on the
Diku/Merc/ROM lineage, not by unlocking anything new.

---

## 5. Storage

### 5.1 Where it lives

A new Core type, `SessionData`, owned by `WorldSession` — one instance per session, created with the
session and dying with it. Not per *connection*: a reconnect to the same character should start clean,
because the values it holds are statements about a live character, and a stale HP figure from before a
dropped connection is a lie with a plausible face. `WorldSession.ConnectAsync` already disposes and
rebuilds the telnet session; clearing `SessionData` belongs there.

It lives in `SharpMUTerm.Core/Protocols/` — beside `MxpParser` and `PuebloParser`, which are the other
things in this codebase that turn a world's structured output into a model — with no UI dependency. The
architecture rule in `CLAUDE.md` is not negotiable, and this is exactly the "GMCP/MSDP routing" the rule
names. `Core/Telnet/` is the wrong home: that folder is the transport-facing wrapper around
TelnetNegotiationCore, and `SessionData` is a consumer of it, not part of it.

`GmcpStats` (`src/SharpMUTerm.Tui/GmcpStats.cs`) is deleted. It is in the wrong project, it is
app-scoped, its readers have no callers, and everything it does is a subset of what `SessionData` does.

### 5.2 The model

One tree per session, addressed by **dotted path**, holding JSON-ish scalars, objects and arrays.

```
Char.Vitals.hp      = "1000"
Char.Vitals.maxhp   = "1200"
Char.Status.class   = "Ranger"
Room.Info.name      = "The Grand Plaza"
Room.Info.exits     = ["north", "east"]
```

Three properties matter and each is a decision:

**Replace the subtree by default; merge only for configured paths.** This reverses an earlier draft of
this document, which said "merge, always", on the reasoning that a partial `Char.Vitals` would otherwise
blank a gauge mid-fight. The research says that is the wrong default and names the reason:

- **Mudlet replaces**, and merges only for an allowlist whose default has exactly one entry. Its own
  source comment: *"only merge tables (instead of replacing them) if the key has been registered as a
  need to merge key by the user default is `Char.Status` only"* (§2.4). `Char.Status` is on the list
  because IRE documents it as a delta package — "messages only contain changed values" — and nothing
  else is.
- **Merging is actively wrong for collection-valued packages.** Aardwolf's wiki: "As with room exits,
  group information needs to be **cleared with each GMCP data refresh** so that members who have left
  the group do not remain in the data." A merged `Room.Info.exits` accumulates the exits of every room
  you have ever visited; a merged group list never loses a member. The Aardwolf MUSHclient plugin nukes
  the whole `room` subtree on `room.info` for exactly this reason (§2.4).

So: on a message for package `P`, the subtree at `P` is **discarded and rebuilt** from the payload,
unless `P` is on a per-world **merge allowlist**, in which case the payload's keys are copied over the
existing ones and absent keys survive.

The allowlist ships with `Char.Status` on it, matching Mudlet, and is editable per world beside the
`Core.Supports.Set` list (§4.3) — because which packages a server sends as deltas is a fact about the
server, and the one client that got this right made it configurable rather than guessing.

The cost of replacement is the failure mode the first draft feared, and it is real: a server that sends
`Char.Vitals` deltas and is *not* on the allowlist will make a gauge flicker. That is a visible,
diagnosable, one-setting fix. The cost of merging is a value that is silently and permanently wrong,
which is not.

**One namespace for GMCP and MSDP.** MSDP's `{"ROOM":{"VNUM":"6008"}}` becomes `MSDP.ROOM.VNUM`; GMCP's
`Char.Vitals` becomes `Char.Vitals.*`. They are prefixed apart rather than mapped onto each other, because
a mapping table between MSDP variable names and GMCP package names is a guess that will be wrong on some
server, and being wrong there is worse than making a consumer read two paths. §9 says how a consumer
that wants "health, whatever protocol it came from" gets it without either.

**Values keep their JSON kind.** `hp` is a string on some servers and a number on others — the same
server sometimes changes between versions. The store keeps what arrived and the accessor coerces
(§6.1). `GmcpStats.Update` already flattens everything to strings and it is the reason `GetInt` exists
and has to re-parse.

### 5.3 Lifetime and reach

| Question | Answer |
|---|---|
| Who owns it? | `WorldSession`, as `public SessionData Data { get; }` |
| When is it cleared? | On `ConnectAsync`, before the socket opens |
| How does a pane's chrome read another session's values? | `WindowSession(windowId)?.Data` — the established resolver, no new path |
| How does the status row read them? | It does not. See [`status-bar-design`](2026-07-30-status-bar-design.md) §3 |
| Is there an app-wide store? | No. There is deliberately no `SharpMUTermApp.Data` and no `SessionData.Current`. |

That last row is the whole point. An app-wide accessor is how `GmcpStats` became wrong, and the fix is
not to make the app-wide one smarter — it is for the type to be unreachable without naming a session.

---

## 6. The consumer API

Three consumers, with genuinely different needs: a status row that wants one number occasionally, a
pane that redraws whenever a value moves, and a Lua script that wants to be told things.

### 6.1 Reading

```csharp
// SharpMUTerm.Core.Data
public sealed class SessionData
{
    // Exact-path reads. Null when absent — never a default, because "0 hp" and
    // "this server does not send hp" must not render the same.
    public DataValue? Get(string path);
    public bool TryGetInt(string path, out int value);
    public bool TryGetDouble(string path, out double value);
    public string? GetString(string path);

    // Subtree reads, for a consumer that wants to render whatever is there.
    public IReadOnlyDictionary<string, DataValue> GetAll(string prefix);
    public IReadOnlyCollection<string> Paths { get; }

    // Has this session produced any structured data at all? The question the
    // vitals pane and the status row both ask before deciding what to show.
    public bool HasAny { get; }
    public bool Has(string prefix);
}
```

`TryGetInt` accepts a JSON number *or* a numeric string, because servers send both. That coercion is in
one place precisely so that no consumer has to know which kind of server it is talking to.

### 6.2 Change notification

The vitals pane redraws on every update, so a snapshot getter alone is not enough.

```csharp
public event EventHandler<SessionDataChangedEventArgs>? Changed;

public sealed class SessionDataChangedEventArgs : EventArgs
{
    public string Package { get; }                      // "Char.Vitals"
    public IReadOnlyCollection<string> ChangedPaths { get; }  // "Char.Vitals.hp"
    public DataSource Source { get; }                   // Gmcp | Msdp
}
```

Three things about this shape:

- **It carries what changed, not the whole store.** A pane that redraws a two-gauge strip does not want
  to diff forty paths to find out that `hp` moved.
- **It is raised once per message, not once per path.** A `Char.Vitals` with six fields raises one
  event listing six paths. Six events would be six UI marshals.
- **It fires on the read loop**, like every other session event. Consumers marshal (`OnUi`) exactly as
  they do for `LinePrinted`. That is not a new hazard; it is the existing one, and stating it here means
  nobody has to rediscover it.
- **It fires whether or not the payload parsed.** Mudlet does the same, and it is deliberate there: the
  prefix events are raised outside the branch that stores the value, so "a handler firing does not
  guarantee the table was updated" (§2.4). A consumer that needs to know whether anything landed reads
  `ChangedPaths`, which is empty when nothing did.

**One event with a package name, not one event per path prefix.** Mudlet raises `gmcp.foo`,
`gmcp.foo.bar` and `gmcp.foo.bar.top` for a single message (§2.4), which is what package authors expect
*in Mudlet* — but it is an artefact of Lua's flat event namespace, where the only way to subscribe to a
subtree is to name every ancestor. We already have prefix matching in the subscriber
(`ScriptHost.PackageMatches` matches a registered name or any `name.` prefix), so raising three events
where one will do would be three UI marshals for one message. The observable behaviour is the same and
the cost is a third. Where a Mudlet script would register on `gmcp.Char`, ours registers on `Char` and
gets `Char.Vitals`.

**Coalescing is a consumer concern, not the store's.** A store that batched updates would delay the
one consumer that wants them promptly, and a pane that redraws too often is a pane that should
rate-limit — the per-pane NAWS report already establishes the pattern (four writes a second with a
trailing flush). Sizing that limit for the vitals pane belongs in the vitals pane document; §5 of it
does that.

### 6.3 Sending

```csharp
// WorldSession
public ValueTask SendGmcpAsync(string package, string json, CancellationToken ct = default);
public ValueTask SendMsdpAsync(string variable, string value, CancellationToken ct = default);
```

`WorldSession` is the right level: it is where a script and the shell both already reach, and it is what
holds the `ITelnetSession` the call ends at. `SendGmcpAsync` exists on `ITelnetSession` already and just
needs surfacing. `SendMsdpAsync` needs §4.4's plugin behind it.

### 6.4 Lua

Lua is unreachable in the shipped client today, and that is a defect rather than a decision — but the
API must not be designed as though the status bar were its only reader, or it will need rewriting the
day the bridge is wired up. The Lua surface follows the store one-for-one:

```lua
gmcp.on("Char.Vitals", function(json) … end)     -- exists today, prefix-matching
gmcp.get("Char.Vitals.hp")                       -- new: read the store
gmcp.all("Char.Vitals")                          -- new: read a subtree as a table
gmcp.send("Core.Supports.Add", '["Comm.Channel 1"]')
msdp.on("HEALTH", function(v) … end)
msdp.report("HEALTH")
```

Two notes. `gmcp.on` today matches a registered prefix against an incoming package
(`ScriptHost.PackageMatches`), which is the right semantics and should not change. And a script's
handlers are per-`ScriptHost`, which is per-session once `WorldSessionScriptBridge` is actually
constructed — so the isolation the store provides is not undone by the scripting layer.

**Deferred, and named as such:** whether `gmcp.get` should hand Lua a live table that mutates under it
(Mudlet's model) or a snapshot copy. A live table is more convenient and makes a script's read racy
against the read loop; a copy is safe and allocates per call. Settled by whether the bridge ends up
marshalling script callbacks onto the UI thread — if it does, a live table is safe and free.

---

## 7. TTYPE / MTTS

We currently identify as `TNC` on an `XTERM` claiming `MTTS 3853` (§1.1). What we should say:

```
round 1   SHARPMUTERM
round 2   the terminal's own $TERM, uppercased
round 3+  MTTS <bitvector>
```

**Round 1: uppercase, and no version number by default.** The spec asks for all caps and every client
complies — `TINTIN++`, `MUDLET`, `BLIGHTMUD` (§2.8). RFC 1010 permits only uppercase letters, digits,
hyphen and slash, up to 40 characters, which is why a version is awkward: the `.` is illegal, and
Mudlet stopped sending one in 2024 for that reason — *and then discovered that servers running KaVir's
protocol snippet consequently assume version 1.0 and restrict colour support to 16 colours* (§2.8).
Mudlet's answer is to auto-detect the snippet and re-enable the version. Ours should be the same
posture: send the bare name, and treat "some servers downgrade us without it" as a known, reported
problem rather than something to pre-emptively work around. **Deferred:** whether to copy Mudlet's
auto-detection; it needs a server that does this to test against.

**Round 2: report the real terminal.** Every terminal client in the survey reports `$TERM`, uppercased
— TinTin++ and Blightmud both do (§2.8) — and the spec says console clients *should*: "Console clients
should report the name of the terminal emulator, other clients should report one of the four most
generic terminal types." We are a console client running inside Kitty, WezTerm or Ghostty, and telling
a server `XTERM` when the terminal says `xterm-kitty` throws away the only piece of information the
field carries. Fall back to `XTERM-256COLOR` when `$TERM` is unset or illegal under RFC 1010.

**Round 3+: claim what is true.** Today's `3853` is wrong in both directions. It does not claim
**MOUSE TRACKING (16)**, which this client implements (`ScrollPaneUnderPointer`, pane drag-and-drop,
clickable rail rows and links). It does claim **MSLP (1024)**, which we do not implement at all — and
no other client claims it except TinTin++, and only as a side effect of its mouse-tracking bit (§2.8).
Whether UTF-8 (4) is claimed should follow the CHARSET outcome rather than being a constant, because a
world pinned to Latin-1 is a world where we are not sending UTF-8. Whether SSL (2048) is claimed should
follow whether the transport supports TLS, which it does.

The obstacle is that `TelnetInterpreter.TerminalTypes` is public-get / private-set. Two routes, and the
same pair as everywhere else in this stack: reflection at connect time — the seam `TelnetSession`
already uses twice, for `CallbackOnByteAsync` and `CurrentEncoding`, both with comments explaining
themselves — or an upstream `WithTerminalTypes(...)` on the builder, which is a five-line change and the
better answer. Reflection is what unblocks stage 1; the PR is what removes it.

**Why it matters.** Servers gate features on client name: an IRE world enables its own GUI packages for
clients it recognises, and several codebases log the client string for support. Identifying as the name
of a telnet library means we are recognised as nothing.

---

## 8. MSSP

### 8.1 What it is, and who it is for

MSSP arrives once, during negotiation, before login, and describes the **server**: its name, player
count, uptime, codebase, contact, website. It is a machine-readable "about this MUD" page. Its primary
consumers are crawlers and listing sites, not interactive clients.

That is why the question "is this worth surfacing?" was open. **It is now settled: yes.** The
maintainer's requirement is an **INFO** affordance on a world in the F5 Worlds & Characters screen,
opening a read-only MSSP report for that world. §8.4 designs it.

It is worth knowing that this puts us ahead of every client surveyed. Mudlet parses MSSP and exposes it
to Lua but shows none of it in its connection dialog; TinTin++ only asks for it in debug mode; Blightmud
shows a tag; MUSHclient and BeipMU do not implement it at all; and no client anywhere has an MSSP-driven
server browser (§2.7). Being first is a reason for caution, not confidence — nobody has found the
ergonomics for us, and §8.4's three states are reasoned guesses that want a user in front of them.

### 8.2 The other use of MSSP, which is better than the screen

Mudlet found the one genuinely load-bearing use of MSSP and it is not a report: `TLS`/`SSL` and
`HOSTNAME` tell you that the server you have just connected to *in plaintext* also offers an encrypted
port. Mudlet's `promptTlsConnectionAvailable()` offers "A more secure connection on port %1 is
available… Update to port %1 and connect with encryption?" (§2.7).

We should do this, and it is smaller than the screen. The pieces already exist: `WorldDefinition`
carries `UseTls` and a port, F5 edits both, and `Notice` is the surface for something that is news
rather than state. On connect, if MSSP reports an SSL port and the world is not using TLS, raise a
notice naming the port and what would change it. It does **not** reconnect on its own — a client that
silently redirects your connection somewhere else is a client you cannot trust — and it does not nag: once
per world, recorded in the client message log.

**Deferred:** whether the notice should offer a one-key action (reconnect on the secure port, and offer
to save it) or only report. Offering is better if the action is unambiguous; it is one more thing that
can fire at a bad moment. Settled by trying it.

### 8.3 Parsing

> **Superseded, 2026-07-30.** An MSSP **crawler** — a standalone tool that connected to servers, read
> MSSP and followed `REFERRAL` to find more — was built and then deleted: the maintainer's call was
> *"drop the crawler aspect; focus on the MSSP info and build that in"*. The screen is the only
> consumer now, so the "two implementations would drift" argument below no longer applies, and the
> three crawler-specific gotchas at the end of this section are kept as **prior art rather than
> requirements**. What was implemented also differs from the sketch below in one substantive way: the
> library's MSSP reader was fixed upstream (TelnetNegotiationCore 2.6.5 / 2.7.0), so the
> "we parse MSSP ourselves" conclusion was overtaken — `MsspData` in
> `SharpMUTerm.Core/Telnet/Mssp/` is a *projection* of `MSSPConfig.Variables` with no parsing in it.
> `MsspConfigReader` was deleted as this section proposed.

**The parsing and the model live in `SharpMUTerm.Core/Protocols/Mssp/`, and both consume it.**
Concretely:

```csharp
// SharpMUTerm.Core.Protocols.Mssp
public sealed record MsspReport(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Variables,  // every variable, in arrival order
    DateTimeOffset ObservedAt);

public static class MsspVariables
{
    // The official set, its display order, and which are lists.
    public static IReadOnlyList<MsspVariable> Official { get; }
    public static bool IsList(string name);
    public static bool IsBoolean(string name);
}
```

A dictionary of **name → list of values**, not a typed record with a property per field. Three reasons,
and the spec supports all of them (§2.7): a variable may legitimately repeat — "the same variable can be
sent more than once with different values, in which case **the last reported value should be used as the
default value**… multiple values should be ordered from least to most relevant", which `PORT` and
`REFERRAL` both exercise; a typed record cannot hold a variable nobody has heard of; and §1.1 is a
demonstration of what a typed record does to the fields it does not know about.

Two more rules the model must carry, both from the spec:

- **Names may contain spaces** — `CRAWL DELAY`, `XTERM 256 COLORS`, `PAY TO PLAY`. The spec suggests
  clients "substitute spaces with underscores as the recommended solution", but that is a display and
  lookup convenience; the wire form is the space, and lookups must normalise rather than the parse
  rewriting what arrived.
- **`-1` means "not available"** for numeric world variables. The screen renders that as *unknown*,
  never as `-1 rooms`. This is the same rule as §4's "no fact, no cell", arriving from the protocol
  rather than from us.

**We do not use `MSSPConfig`.** The library's model drops every list, every boolean and every unknown
variable, and never populates its own `Extended` bag (§1.1). Since `MSSPProtocol` hands us a parsed
`MSSPConfig` and nothing else, first-class MSSP requires one of:

- **an upstream fix** — populate `Extended`, and map lists and booleans. This is the correct answer, it
  helps every consumer of the library, and the repo owner is its author.
- **our own MSSP framing read.** The wire format is trivial — `IAC SB 70 (VAR name VAL value)* IAC SE`
  with `VAR`=1 and `VAL`=2 — and our own plugin (the §4.4 route) can read it before the library's does.

**Recommend the upstream fix, with our own parse as the unblocker**, and either way the `MsspReport`
above is what Core exposes and what both the screen and the crawler consume. `MsspConfigReader` — which
also flattens dictionaries and lists into comma-joined strings — is deleted with it.

**Three things a crawler would need that the screen does not.** Recorded as prior art — nothing in the
repository consumes them now, and the first is the one that also bears on the *client*:

- **PennMUSH will not talk to a raw socket.** It probes with `IAC DO LINEMODE` and only sends its
  telnet-option block — including `IAC WILL MSSP` — once the client has proved it understands telnet,
  with a 60-second timeout (§2.7). A crawler that opens a socket and waits gets nothing from the entire
  Penn family, which is the family this client's sibling server belongs to.
- **The plaintext fallback is case-sensitive in Penn.** Penn compares `!strcmp(command, "MSSP-REQUEST")`
  — uppercase — while Grapevine and MudVerse both send lowercase `mssp-request` (§2.7). Send uppercase.
- **`CRAWL DELAY` exists and nobody honours it.** No live crawler was found that respects it (§2.7).
  Honouring it would cost nothing and would make ours the polite one.

And the property that makes the crawler cheap: MSSP arrives **before login**, and Grapevine's crawler
disconnects the instant it has the data without ever authenticating (§2.7). There is no credential
handling anywhere in this.

### 8.4 The INFO screen

**What it is.** A read-only report of the last MSSP a world sent us, reached from the world's row on F5.

**Read-only is the shape, not an omission.** Every other settings screen exists to change something,
and this project's affordance rule is that a field well means editable and its absence means it is not.
A whole screen of well-less rows is therefore exactly right — and must be *deliberately* well-less, so
it does not read as a form whose wells failed to render. Concretely it should look like the client
message viewer, not like F5: a label column and a value column, no cursor stops on the values, and a
footer that offers only `Esc close` rather than `⏎ edit`.

**Three states, and the empty ones must not look broken.**

| State | What it says |
|---|---|
| Never connected to this world | *No server information yet — connect once and this fills in.* |
| Connected; server sent MSSP | The report (below) |
| Connected; server sent no MSSP | *This server does not publish MSSP. It is optional, and most MUSHes do not.* plus what we do know from the world's own configuration (host, port, TLS) |

The third row is the common case on MUSHes and it is the one worth getting right. Saying "no data" is
what makes a client look broken; saying *"this is optional and normally absent, and here is what we do
know"* is the same information and the opposite impression.

**Persistence.** In-memory only, the screen is empty until you connect — which is exactly when you least
need it, since you are already there. Persisting is right, and the cost is not free:

- **Where.** Not `config.json`. A config write per connect is a write to a file the user hand-edits, on
  a path that is otherwise driven by deliberate keystrokes, and it would make a *connection* dirty the
  user's settings. A separate cache file (`mssp.json`) beside the config, keyed by `host:port`, written
  once per connect. That is the same shape as the scrollback spill: derived data, ephemeral, and a write
  failure degrades to in-memory rather than failing anything.
- **Staleness must be labelled.** Every report carries `ObservedAt`, and the screen says *"as of 3 days
  ago"* in its header. A player count from last Tuesday presented as fact is the fabricated-latency-meter
  mistake in a different costume.
- **Keyed by `host:port`, not by world name.** Two worlds pointed at one server share a report, and
  renaming a world does not lose it.

**The fields.** First-class rows, in this order, because these are what someone browsing a world list
wants: `NAME`, `PLAYERS`, `UPTIME` (rendered as a duration, and as "since <date>"), `CODEBASE`,
`FAMILY`, `HOSTNAME`:`PORT`, `SSL`, `CONTACT`, `WEBSITE`, `LANGUAGE`, `LOCATION`, `STATUS`. Then a
**Server capabilities** block for the boolean fields (`ANSI`, `UTF-8`, `XTERM 256 COLORS`, `MXP`,
`MSP`, `PUEBLO`, `GMCP`, `MSDP`, …) rendered as a checkbox-shaped list that is explicitly not
interactive. Then **Everything else** — every remaining variable, official or not, name and value as
sent, in arrival order.

That last block is the one that matters most for a protocol whose entire purpose is servers describing
themselves. Dropping unknown variables would be the wrong default; a `DISCORD` URL or an
`INTERMUD` network name is exactly the kind of thing a world's owner puts there and a player wants.

**Where the button goes — and it must not be a chip.** In the WORLDS pane, after `[+ world]`, drawn
only when a world is selected.

The obvious design — a `[i info]` chip you walk the cursor onto — is **wrong here, and this screen
already knows why**. `ScreenModel.Sizes` states the rule:

> an action with no target needs a cursor stop; an action with a target must not steal the cursor from
> the thing it acts on

because a pane is a list followed by its buttons, and reaching a button with ↑↓ means walking the cursor
over every row of the list on the way, which drags the selection to the last one. That is the documented
"only the last world can be deleted" bug, and an INFO chip reached the same way could only ever show
information for the last world in the list.

INFO has a target. So it is drawn the way Delete is: a **key-hint row** naming the key and what it acts
on — `i  info on Aetherfall` sitting beside `Del  removes Aetherfall` — and it is run by that key on the
selected row. Nothing is lost, because the row is a true reading of what the key would do, and
`ScreenChrome.DeleteHint`'s derivation (advertise the key only where there is something for it to act
on) is the pattern to copy.

Three consequences:

- **`ScreenButtonKind` needs a third member**, and `Stops()` needs to trim it. Today `Stops()` walks
  back from the end of a pane's rows while the trailing button is `Kind: Remove`, and its comment says
  *"this is a count and not a set of holes — the cursor never has to skip a row in the middle"*. A
  non-stop INFO row therefore has to be **appended among the trailing non-stops**, so the pane reads
  `list → [+ world] → i info … → Del removes …`. Put it anywhere else and the cursor gains a hole and
  `ScreenSelection` stops being a plain clamp. `ScreenReachabilityTests` pins that shape on all eight
  screens and will fail loudly if this is got wrong, which is the right outcome.
- **The key must be safe.** The WORLDS pane has no editable field, so a plain letter is available there
  the way `Del` and `Space` already are — but it is only safe *while the cursor is in that pane*, and
  the CHARACTER detail pane does have fields. Scope it to the pane, and advertise it in the screen's
  header hint the way `Del remove` is advertised.
- **Rows past the end of a list.** `WorldsScreenRenderer` carries a comment about exactly this: at
  100×24 two worlds and their buttons ran to twelve rows in a ten-row column, and the rows lost off the
  bottom were `[+ world]` and the Delete row — cursor stops that were never drawn. A third row makes
  that one row worse. It goes through `ScreenChrome.Window` like the others, and the narrow case is
  checked with a rendered frame, not by counting.

By mouse, the key-hint row is clickable in the same way the rail's rows are — but the key is the
primary route and the row must read correctly with no pointer at all.

**Deferred:** whether the INFO screen is a full-screen overlay of its own (a tenth `SettingsScreen`,
with a `--view mssp` for snapshots) or an overlay *over* F5 that Esc returns from. The first composes
with everything and gets snapshot coverage for free; the second keeps the world you were looking at.
Settled by whether anything else ever wants to open a read-only report — if the client message viewer
and this one end up the same shape, they should be one mechanism, and that argues for the first.

---

## 9. What arrives that we did not ask for

All of it lands on the telnet read loop, and all of it must be survivable there.

| Case | Behaviour |
|---|---|
| **Unsolicited package** — a server sends `Comm.Channel` we never subscribed to | Stored. It costs one path and one dictionary entry, and refusing it would mean a Lua script cannot see data the client received. |
| **Malformed JSON** | The message is dropped, the store is untouched, and one line goes to the client message log (`ClientDiagnostics`, ⌃P ▸ *Show client messages*) — **not** to the output pane, which is the server's stream and the character's transcript. Rate-limited: a server emitting broken JSON per line must not fill the log. |
| **Truncated payload** — the 8192-byte cap in §1.1 | The commonest cause of the row above, and it must not be reported as the row above. When the whole message measures exactly 8192 bytes, say *truncated at the telnet layer's 8 KiB limit* and name the package. Verified: `Char.Big` + 300 KiB arrives as 8183 payload bytes with no error. |
| **A bare JSON primitive** — `Package 42`, `Package true`, `Package "hello"`, `Package null` | **Legal, and explicitly enumerated by the spec** (§2.1). `System.Text.Json` accepts top-level primitives, so no workaround is needed — but the standard fix if a parser did not is Gammon's: wrap the payload in `[` `]` before parsing, which is what both MUSHclient plugins do. Stored as a scalar at the package path. |
| **A payload that is not JSON at all** — Aardwolf's `request char`, `group on` | Stored as a string at the package path. Not an error. Those are *client→server* in Aardwolf's case (§2.1), but a server sending the same shape must not break us; verified that `Char.Vitals 42` arrives as the two-byte string `42`. |
| **A raw ESC byte in the payload** | Not valid JSON, and servers do it — `Comm.Channel.Text` carries ANSI, normally as `` escapes but not always. Mudlet rewrites a literal ESC to `\\u001B` before parsing, with a comment saying it is there "to handle improperly passed ANSI codes" (§2.1). Copy that: it turns a parse failure into data. |
| **`IAC WONT GMCP` mid-session** | Tear down: the subscription is gone and the store's GMCP paths are stale. This is the documented copyover sequence (§2.1), and §4.2's false→true edge detector re-sends `Core.Hello` when it comes back. |
| **Empty payload** — `Package` with nothing after it | **Unreachable.** The library raises no callback for it (§1.1). Nothing is designed for a case we cannot observe; if a future library version starts delivering it, treat it as a signal — raise `Changed` with an empty path set, store nothing. |
| **Huge payload** | Already capped below anything we would choose, at 8192 bytes, by the library (§1.1). A cap of our own would never fire, and an unreachable guard claiming to be a safety net is worse than none — the same reasoning `TelnetSessionOptions.ResolveKeepalive` gives for having no minimum clamp. If the 8 KiB cap is ever lifted upstream, a cap here becomes necessary in the same change. |
| **Path explosion** — a server using a unique path per object | A per-session path cap (proposed: 4096) above which new paths are refused and one warning is logged. Existing paths keep updating. Without a cap the store is an unbounded leak driven by the wire. |
| **High-frequency updates** — vitals every combat round | No throttle in the store (§6.2). Cost is stated in §10 and mitigated at the consumer. |
| **A package that is a prefix of another** | Both stored; `Char` and `Char.Vitals` coexist. `GetAll("Char")` returns the subtree. |
| **Case** | Paths are stored as sent and compared **case-insensitively**. Servers are inconsistent about `Char.Vitals` vs `char.vitals`, and a consumer that has to guess is a consumer that breaks on one world. |

---

## 10. Cost

Stated plainly, because the brief asks for it and because "it's just a dictionary" is not true on a
read loop.

**Per GMCP message we do not currently pay for:** a UTF-8 decode of the payload (already paid — the
library does it), a `JsonDocument.Parse`, one walk of the parsed tree, one dictionary lookup and
possibly one insert per leaf, and one `EventHandler` invocation. For a six-field `Char.Vitals`
that is roughly one `JsonDocument` rental plus six dictionary operations. At four combat rounds a
second across six connected characters — a deliberately pessimistic figure — that is 24 parses and
~150 dictionary operations a second, on a thread that is otherwise blocked on a socket. It is not
free, and it is not close to significant.

**What is not free is the event.** Every `Changed` handler runs on the read loop until it marshals.
A consumer that does work before `OnUi` puts that work on the network thread for every connected
session. The vitals pane's rate limit exists for this reason as much as for repaint cost.

**Allocation.** `JsonDocument.Parse` + `Clone()` is the shape `GmcpStats` uses today and it allocates
per message. Parsing straight into the store (via `Utf8JsonReader` over the payload bytes, without
materialising a `JsonDocument`) avoids the clone and is worth doing at stage 2, not stage 1 — stage 1
should be obviously correct.

**What this does *not* cost:** nothing here touches layout, so none of it interacts with
`SyncInputHeights`' veto or with per-pane NAWS. Those costs belong to the consumers, and the
[status bar](2026-07-30-status-bar-design.md) and [vitals pane](2026-07-30-vitals-pane-design.md)
documents own them.

---

## 11. Staged plan

**Stage 1 — say hello. Small, useful, independently shippable.**

- `SessionData` in Core: store, dotted paths, merge semantics, `Changed`, the caps in §9.
- `WorldSession.Data`; cleared on `ConnectAsync`.
- Send `Core.Hello` + `Core.Supports.Set` on GMCP enable, detected by polling
  `IsPluginEnabled<GMCPProtocol>()` after each read batch.
- Delete `GmcpStats`.
- A `/gmcp` command that prints the focused window's session store into the pane.

That last line is what makes stage 1 shippable on its own: with it, a user can connect to a world and
*see whether it sends anything*, which is a question the client currently cannot answer at all. Nothing
in stage 1 touches the status row, the workspace, or layout.

`/gmcp` follows the shape `/graphics` and `/triggers` already have: resolved from the *window* the
command was typed in (`SendTarget`'s rule), and **appended to that window** rather than routed through
the session — both existing commands carry the same comment, and the reason is that they must still
answer when nothing is connected, which is exactly when someone is asking. `/gmcp` earns it twice over:
"this world sends me nothing" and "I am not connected" are the two answers it exists to distinguish.

**Stage 2 — identity and the first real consumer.**

- TTYPE/MTTS: state `SHARPMUTERM`, the real `$TERM`, an honest bitvector (§7).
- Per-world `Core.Supports.Set` and merge-allowlist editing on F5.
- The [vitals pane](2026-07-30-vitals-pane-design.md), which is the first thing that renders any of this.
- Volunteer `IAC DO GMCP`.

**Stage 3 — MSSP.**

- `MsspReport` + `MsspVariables` in Core; upstream fix or own parse (§8.3).
- **The TLS-upgrade notice (§8.2)** — first, because it is the smallest piece and the only one with a
  security payoff.
- The `mssp.json` cache.
- The INFO button and the read-only report screen (§8.4).

**Stage 3 shipped**, ahead of stages 1 and 2 and without the TLS notice: `MsspData` (a projection of
the library's own reader, not our parse), `MsspCache` writing `mssp.json` beside `config.json` keyed by
`host:port`, and the F5 ▸ `i` INFO screen with the three empty states of §8.4. The TLS-upgrade notice
(§8.2) remains unbuilt and is still the smallest piece with a security payoff.

**Stage 4 — MSDP.**

- The send plugin (or the upstream `SendMSDPCommand`).
- `LIST REPORTABLE_VARIABLES` → `REPORT` subscription.
- `MSDP.*` paths in the same store.

**Stage 5 — Lua.**

- Construct `WorldSessionScriptBridge` per session (it exists, it works, nothing builds it).
- `gmcp.get` / `gmcp.all` / `gmcp.send`, `msdp.*`.

**Documentation to correct, at stage 2 not before:** `README.md:87` and `docs/PLAN.md:113` promise a
GMCP-driven status bar with HP/EN meters. Under this design that promise is wrong in its **location**
as well as unimplemented — vitals go in a pane, not in global chrome, for the reasons the
[vitals pane document](2026-07-30-vitals-pane-design.md) §2 gives. Both lines should be rewritten when
the pane exists, and not before, because until then they would be replaced by a different unkept
promise. `docs/PLAN.md:46`'s `GmcpRouter` should be renamed to `SessionData` in the same pass.

---

## 12. Decisions deferred

| Decision | What would settle it |
|---|---|
| Per-world control of whether we volunteer `IAC DO GMCP` | A server that misbehaves on an unsolicited `DO`. None is known. |
| `Core.Supports.Add` mid-session vs. requiring a reconnect | Testing against real worlds; the spec permits Add but adoption is uneven. |
| Live Lua table vs. snapshot copy for `gmcp.get` | Whether the script bridge marshals callbacks onto the UI thread. If it does, a live table is safe and free. |
| ~~INFO as its own full-screen overlay vs. an overlay over F5~~ | **Settled: over F5, one window, content swapped over a one-deep stack.** It keeps the world you were looking at and Esc goes *back* to it; it costs no global F-key; and it stays snapshot- and test-reachable, which a second modal window would not be — `EditReviewOverlay` already documents why two `PreviewKeyPressed` modals cannot be driven headlessly. The convergence argument still stands and is now cheaper to act on: anything else wanting a read-only report reuses `SettingsOverlay.OpenDetail` and a `ScreenButtonKind.Detail` row. |
| Upstream PR vs. reflection for `TerminalTypes` | Not either/or: reflection unblocks stage 2, the PR removes it. The question is only whether stage 2 waits, and it should not. |
| Whether the 8 KiB GMCP truncation (§1.1) is a bug or a deliberate limit | Ask upstream. It is silent either way, which is the part that is certainly wrong: a message that loses its tail should say so. This is the highest-value upstream report of the three named in this document, because it makes a *correct* server look like a broken one, and no amount of care on our side can recover the dropped bytes. |
| Whether MSSP should also be shown at connect, in the output pane | Nobody has asked for it, and it is the server's stream. Deliberately not designed here. |
| Whether the TLS-upgrade notice offers an action or only reports (§8.2) | Trying it. Offering is better if the action is unambiguous; it is one more thing that can fire at a bad moment. |
| Whether to copy Mudlet's KaVir-snippet auto-detection for the TTYPE version number (§7) | A server that does this, to test against. Until then, send the bare uppercase name and treat a 16-colour downgrade as a reportable bug rather than something to pre-empt. |
| ~~Whether an MSSP report should be keyed by `host:port` or by resolved IP~~ | **Settled: `host:port`.** It is what the user typed and what the config holds; a resolved IP moves under a report that has not changed. |
| ~~Whether we honour `CRAWL DELAY` in the sibling crawler~~ | **Moot.** There is no crawler; see §8.3. |
