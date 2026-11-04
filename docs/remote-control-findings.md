# Remote Control (`/rc`) sessions — spike findings

> **Status (CB-238, Oct 2026): historical record.** The relay these findings were measured for was deleted in `937de9ec` (30 Aug 2026), and CB-238 removed the code it left behind — the `ListAgents` peer-list parser, the relay prompts, the health and stall readers, and the relay table in `RemoteControlSessions`. Remote orbs now come only over the direct link from a Claude Buddy on the far machine; a machine without Buddy shows nothing. The formats below are kept because they are still what Claude Code's Remote Control tools produce, and the section at the end, "The peer-list format, recorded after its parser was deleted", collects what the deleted parser knew.

Everything here was measured on 23 Aug 2026 against two real machines on one
account: a MacBook at `198.51.100.11` running the bridge, and a Mac mini
(`avatar.internal`, `198.51.100.10`) running the session being controlled.
Both used Claude Code `2.1.241` and the `~/.claude-board` CLI profile
(`CLAUDE_CONFIG_DIR`), which is a second Anthropic account on the same box.
Where something is assumed rather than observed, it says so.

The premise being tested: Buddy cannot talk to Anthropic's Remote Control relay
directly — there is no third-party API for it. But a Claude Code session that
*itself* has RC connected is given peer tools (`ListAgents`, `SendMessage`) that
reach the account's other sessions. So Buddy can launch its own hidden **bridge**
session and use it as the doorway. That premise held.

The short version, in the order it was discovered:

1. **`claude agents --json` is local-only** — it never sees another machine, so
   there is no cheap way to skip the bridge for discovery.
2. **The bridge works, detached and unattended.** `--remote-control` is a real
   flag; RC came up in a `tmux new-session -d` with no client attached and no
   trust or login prompt.
3. **Cross-machine control works end to end.** The bridge listed the mini's
   session and got a reply back from it.
4. **The protocol does not need the model to format anything.** Both discovery
   and replies land in the bridge's own transcript in deterministic, parseable
   shapes — which removed a planned fenced-JSON convention entirely.

## `claude agents --json` cannot do this

The cheapest imaginable design was to skip the bridge and just enumerate. It
does not work: every row carries a local `pid`, and the two machines never see
each other.

| run | result |
| --- | --- |
| MacBook, `~/.claude` | 8 rows — 6 local `interactive` tmux panes, 2 local `background`; all with a live local `pid` |
| MacBook, `~/.claude-board` | 1 row — a `background` job from ~29 days earlier with **no** `pid` and a local `cwd`: a stale orphan record, not a remote session |
| mini, `~/.claude` | `[]` |
| mini, `~/.claude-board` | 2 rows — `resumes-2b`, `job-hunter`; both local to the mini |

That `pid`-less row on the MacBook briefly looked like a remote session. It is
not — it is a month-old dead background job. Recorded because it is exactly the
kind of thing that would make someone conclude enumeration works when it
doesn't. **`--json` is a local process listing.** The bridge is required.

## The bridge: launching it

`claude --help` documents the flag, so nothing has to be typed interactively:

```
--remote-control [name]                    Start an interactive session with Remote Control enabled (optionally named)
--remote-control-session-name-prefix <prefix>   Prefix for auto-generated Remote Control session names (default: hostname)
```

What worked, in a detached tmux session with **no attached client**:

```sh
tmux new-session -d -s rc-bridge -x 200 -y 50 -c <cwd>
tmux send-keys -t rc-bridge \
  'TMPDIR=<private> CLAUDE_CONFIG_DIR=$HOME/.claude-board \
   CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1 <claude> --remote-control <name>' Enter
```

Two things about that command are load-bearing:

- **It must not be piped.** The first attempt appended `| tee log`, and Claude
  Code correctly decided it wasn't a TTY: `Error: Input must be provided either
  through stdin or as a prompt argument when using --print`. The bridge needs a
  real pty, which is what tmux is for. Capture output with `capture-pane` or
  `pipe-pane`, never a shell pipe on the launch line.
- **`tmux new-session -d` is a new primitive for this repo.** Existing code only
  ever calls `new-window` into a session that already has a client
  (`AgentTeamViewer.PlaceInTmux`). Detached creation works fine and needs no
  client — which is what makes a hidden bridge possible.

RC came up with no trust prompt, no login prompt, and no interactive step.

### The private-`TMPDIR` trick works

The bridge must not show up as an orb. Since `ClaudeBuddyHook.sh` writes its
status file to `$TMPDIR/claude_buddy/<session-id>.txt`, pointing the bridge at a
private `TMPDIR` sends its hook output somewhere only Buddy reads.

Observed exactly as designed. The private directory received:

```json
{"state":"idle","cli":"claude","cwd":"/Users/user/Source/Claude-Buddy",
 "title":"","color":"","term_program":"tmux","term_id":"","tty":"ttys014",
 "tmux_socket":"/private/tmp/tmux-501/default","tmux_pane":"%17",
 "tmux_bin":"/opt/homebrew/bin/tmux","session_pid":7184,
 "transcript_path":"/Users/user/.claude-board/projects/-Users-user-Source-Claude-Buddy/ec074105-d5c0-46cd-99f5-bab441847ccd.jsonl"}
```

and that session id was **absent** from the real `$TMPDIR/claude_buddy/`, so no
orb appeared. This is better than synthesising a status file by hand: the hook
hands over the session id, the pane, the socket, the binary and the transcript
path for free. Readiness detection is just "wait for a file to appear here".

## Discovery: `ListAgents` returns text, not JSON

This is the finding that changed the design. The raw `tool_result` in the
bridge's transcript is human-readable and **deterministic**:

```
This session is claude-buddy-52 [30e947] — the name other sessions use to message it (it is not listed below; a message to it would be a message to yourself).

Peer sessions (1):
  job-hunter [94f106]  ·  Remote Control  ·  idle
```

Row shape is `  <name> [<ref>]  ·  <kind>  ·  <status>`, separated by ` · `.
Consequences:

- **`kind` is the discriminator.** The peer is labeled literally
  `Remote Control`. On the other account, whose peers were all local, the same
  field read `interactive` or `bg`. That is how a remote session is told apart
  from a local one.
- **Only RC-connected sessions are peers.** `resumes-2b` was running and *busy*
  on the mini at the time and did **not** appear — it simply wasn't RC-connected.
  This is the right scope for the feature, and it means Buddy shows exactly the
  sessions a user could reach from their phone.
- **Self-exclusion is automatic** ("it is not listed below"). The planned
  "filter the bridge's own session out of the snapshot" step is unnecessary.
- The name passed to `--remote-control` did **not** become the peer name: the
  session self-named `claude-buddy-52`, which looks derived from the working
  directory. **Chased down on 24 Aug 2026 and confirmed — see "What actually
  names a relay" at the bottom of this file.** The guess here was right, it is
  the working directory, and it turned out to matter far more than "recognisable
  in a user's own session list": two separate features key on that name's prefix
  and neither could work until the relay was run from a directory named after
  itself.
- Peer rows carry **no machine or hostname**. Asked directly, the bridge
  answered `"machine":"unknown"`. Orb titles will have to use the session name,
  or get the hostname another way.

## Control: `SendMessage` round trip

Sent to the mini's `job-hunter`; it replied with its hostname,
`avatar.internal`, and that reply reached the bridge. The full path works.

The `tool_use` input shape:

```json
{"to":"job-hunter","summary":"bridge connectivity test, asking for hostname",
 "message":"Bridge connectivity test from Claude Buddy - reply with your hostname only.",
 "type":"message","recipient":"job-hunter","content":"…"}
```

The `tool_result` — note it names the transport explicitly, and returns an id:

```json
{"success":true,
 "message":"“bridge connectivity test, asking for hostname” → job-hunter (a Claude session on another machine, over Remote Control)",
 "msg_id":"e547dcf7-4510-4992-b14e-faa5b95e1872"}
```

**Replies are asynchronous and arrive on a later turn**, as a `role=user`
transcript row wrapping a tag:

```
Another Claude session sent a message:
<cross-session-message from="bridge:session_01SX9H3aCQbpjVN9hM4njAXd" from-name="job-hunter" from-mode="prompting">
avatar.internal
</cross-session-message>
```

`from-name` is the correlation key. (`from` is the relay's own session id, whose
`bridge:` prefix is the relay's terminology and coincidental to ours.)

The bridge's own narration of the gap is worth keeping, because it is the timing
contract stated plainly: *"a reply from job-hunter would arrive asynchronously as
a `<cross-session-message>` on a later turn, and nothing has arrived. I can't
fill in spike-002 without inventing content."* It then reported the reply
correctly on the following turn once it landed.

## What this changes in the design

The plan assumed the bridge would be driven by a strict fenced-JSON convention
with model-generated `request_id`s, because a model-mediated relay is
nondeterministic. **That is not needed.** Both directions land in the transcript
in machine-parseable form:

| need | deterministic source |
| --- | --- |
| list remote sessions | the `Peer sessions (N):` block in the `ListAgents` `tool_result` row |
| confirm a send | `msg_id` in the `SendMessage` `tool_result` row |
| receive a reply | `<cross-session-message from-name="…">body</…>` row |

So `BridgeProtocol` parses transcript rows and correlates on `from-name` and
`msg_id`. The only model-mediated step left is getting the bridge to *call* the
tool at all. Dropping fenced JSON removes the feature's main nondeterminism risk.

It also confirms a planned correction: since a reply can arrive on *any* later
turn, the bridge needs **one continuous transcript tail, demultiplexed by
`from-name`** — not a tail per chat session.

For the record, the model did obey a strict fenced-JSON instruction on the first
try (`{"request_id":"spike-001","agents":[…]}`, no surrounding prose). It works;
it is just unnecessary, and parsing the tool result is strictly more reliable.

## Startup banners — what `BridgeState` can read

A second bridge launch captured the header, which is where readiness and health
are actually legible:

```
 ⚠ 2 MCP servers need authentication · run /mcp
 ⚠ Your login expires in 3 days · run /login to renew
  /remote-control is active · Continue here, on your phone, or at https://claude.ai/code/session_01XfZfJnPe9EGxapEmtzrhBL
```

- **`/remote-control is active`** is the readiness signal, and it carries the
  relay session URL. Worth preferring over the private status file for
  confirming *RC specifically* came up: the status file proves the session
  started, this proves RC attached.
- **`Your login expires in N days · run /login to renew`** is a real
  degradation pattern — the one failure mode observed in the wild. An expired
  login is the likeliest way a long-lived bridge dies quietly.
- Banner lines are prefixed `⚠` and use ` · ` as an internal separator, the same
  convention as the peer rows.

Still not captured: quota exhaustion and RC-dropped-mid-session. `Degraded`
classification for those remains guesswork.

Note `daemon-auth-status.json` in the profile read `{"status":"auth_required"}`
throughout, while RC itself worked normally — so **daemon auth state is not a
proxy for RC health** and should not be used as one.

## Assumed, not confirmed

- **Whether `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` is required — unresolved,
  and deliberately made moot.** Both profiles on this machine set it in
  `settings.json`'s `env`, so it could not be isolated from an existing profile;
  relaunching with `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=0` on the launch line
  still produced a working `ListAgents`, which only shows that a process-env `0`
  does not beat a settings-file `1` — not that the flag is unnecessary. Rather
  than keep chasing it, **Buddy sets the flag unconditionally on the bridge it
  launches.** Setting it is harmless if redundant and load-bearing if not, which
  retires the risk without needing the answer. A clean-profile test would still
  be worth doing before claiming the feature works for users who have never
  enabled agent teams.
- **Windows.** Untested and out of scope for v1; the bridge as designed is
  tmux-shaped, and chat-send is already macOS-only in this app.

## What a peer row does and does not carry

The whole of it: `name`, a bracketed `ref`, `kind`, `status`. That is the
budget for everything shown about a remote session, and two consequences are
worth stating because both look like bugs from the outside.

**No machine name.** Asked directly, the bridge answered `"machine":"unknown"`.
So a remote orb's title is the session's own name and nothing else — there is
nowhere to get "which computer" from, and inventing one would be a guess
presented as a fact.

**No colour, and it cannot be derived.** A local orb's colour comes from the
hook, which gets it one of two ways, neither reachable from here:

- `/color` writes `{"type":"agent-color","agentColor":…}` into *that session's*
  transcript — on the other machine's disk.
- auto-colour hashes the session's **CWD** (`ClaudeBuddyHook.sh`, the
  `cksum` line) — and the peer row carries no cwd either.

So Buddy derives a colour by hashing the remote session's *name*. It is stable
for a given session and deliberately unrelated to whatever colour that session
wears on its own machine. Checked rather than assumed: the tempting theory was
that hashing the name would coincidentally agree with auto-colour, and it
cannot, because auto-colour hashes a different input.

Making the colours agree means asking the remote session for its own, and that
is now what happens — a message per session, once per run of Buddy, carrying the
command list as well (see the next section, which is why it is one question and
not two).

The marker is the part worth knowing about. A reply arrives as an ordinary
cross-session message, indistinguishable from an answer meant for whoever is
reading the panel, so the question asks for a fixed prefix (`CB-INFO:`) to be
echoed. That makes the answer identifiable and lets it be swallowed instead of
appearing as a chat bubble saying "green" in response to a question the person
never asked. Answers are swallowed whether or not they parse, for the same
reason.

Cached in memory rather than persisted: saving it would spare a message per
launch but go stale the moment someone runs `/color` on the other machine, and a
wrong colour that never corrects itself is worse than one extra message at
startup. Asked at most once per session per run, and only for sessions already
judged worth an orb, so nothing is spent on relays or dead registrations.

Verified against the mini: `job-hunter` answered `none` for its colour, which is
a real answer — it has no `/color` set — and correctly falls back to the derived
colour. The positive path (a session that *does* have one) is covered by
fixtures rather than live, since setting a colour on someone's working session
to prove it would be a poor trade.

**A remote session's slash commands cannot be guessed, and the built-ins are
exactly the wrong guess.** The panel first offered Claude Code's own built-in
list — `/agents`, `/color`, `/compact` and the rest — on the reasoning that
every Claude Code session has them. Every one of them fails, and the reason is
the same reason this whole feature works at all: a peer message is delivered to
the **model**, not typed into the receiving CLI's input line. Only that CLI's
own command handler can run a built-in, and it never sees the text.

Measured, not reasoned about. Sending `/color green` to the mini came back:

> I can't run `/color` — it's not one of my available skills/tools … only the
> harness's own command handler can set that.

while `/update-inbox` — a **custom** command, which is just a file of
instructions the model reads — worked. So the rule inverts what you would
expect: the universal commands are the impossible ones, and the bespoke
per-project ones are the ones that work.

That makes the list unguessable from here, so it is asked for. `CB-INFO` carries
`color=` and `commands=` in one reply, because both facts are wanted about the
same session at the same moment and a second message would double the cost for
nothing. Until a session answers, its panel offers **no** completions at all —
an empty list is honest, and a suggestion that does nothing when accepted is
worse than no suggestion. Capped at 60, since this becomes an autocomplete
popup and a session with a hundred skills would push it off the screen.

**No streaming, either.** Peer messaging delivers one message when the remote
session chooses to send it; there is nothing to subscribe to and its transcript
is on the other machine. The closest honest substitute is the `status` field —
`running` while it works — which is why the poll drops to five seconds while
something is in flight and why the panel says "…is working" rather than
pretending to show progress.

## Three things only a stronger test found

All three were found by tightening a live test, and all three are worth
recording because each had *already passed* a weaker version of it.

**The status line hid its own answer.** It was written `warning ?? count`, so
anyone with a warning never learned whether the relay had found anything — and
since the login-expiry notice starts three days out, that is eventually
everybody. Now composed from both facts: `1 remote session · Your login expires
in 3 days`.

**A test can pass while measuring nothing.** The first live test of the
tray path waited for the status to leave `"starting"` and passed in 3 seconds,
which felt like success. It wasn't: the state goes to `connected` the moment the
process is up, which is *before* the peer list has been asked for once. So the
test would have kept passing if polling broke entirely. It now waits on a
`HasPolled` flag, and on doing so immediately failed — a 2-minute timeout with
the relay sitting at `connected`, never polling.

The cause was startup awaiting `Dispatcher.UIThread.InvokeAsync` to create the
poll timer, one line before the first poll. In the real app a dispatcher is
pumping and that completes; in a test host none is, so it blocked forever. So
this was a *testability* failure rather than a user-facing one — but the fix
(post the timer, run the first poll inline) is better regardless: the recurring
poll is a convenience, the first one is the point, and it should not be queued
behind the UI thread being free.

**And the same mistake again, one section later.** The live test for the
capabilities question asserted that a `CB-INFO:` marker came back. It did, and
the test passed — while the parser read **zero** of the twenty-seven commands
the mini had just listed. The reply was:

```
CB-INFO: color=none; commands=apply,apply-ic,cold-intro,daily-run,…
```

Not one slash. The parser required them, because the question asks about slash
commands and the answer is a list of slash commands — so obviously each item
would carry a slash. The session was asked what it can run and it named them,
which is a perfectly good answer to the question actually asked. This is the
fixture rule in `CLAUDE.md` earning its place for the second time in this
feature: the parser was written against an invented reply and agreed with
itself.

The slash is now added on this side, where it cannot be forgotten. Which cannot
mean "every word after `commands=` is a command", or a session answering in a
sentence would offer `/I`, `/can` and `/run` — so the shape of the answer picks
the reading. If anything in it wears a slash, only slashed names count, because
that session punctuates and the bare words around them are prose. Otherwise it
must be a list: split on commas and semicolons, and every piece has to be a bare
name with no spaces in it.

The lesson worth keeping is the ordering, and it is the same one all three
times: the weak assertion was chosen because it was the only thing observable
from outside, and the right move was to make the *result* observable rather than
to trust the nearest available proxy for it. "The marker came back" was a proxy
for "the answer was understood". So was "the status left `starting`" for "the
relay polled".

## One product-shaped warning: the bridge is not actually hidden

Text kept appearing in the bridge's input box that the spike did not send
("which machine is job-hunter on?", "send job-hunter a message"). The banner
explains it: *"Continue here, on your phone, or at https://claude.ai/code/…"*.
Enabling RC on the bridge **publishes it to the account's own RC surface**, so it
shows up on the user's phone and web session list, and they can type into it.
During this spike the user did exactly that, from a diner.

Two consequences, and the first is a real design problem:

1. **The private `TMPDIR` hides the bridge from Buddy's orb scan, not from the
   user.** They will see an unexplained session appear in their own Claude
   session list whenever Buddy starts the bridge. It needs a name that says what
   it is — which makes the unresolved `--remote-control <name>` behaviour (the
   name passed was ignored in favour of a cwd-derived `claude-buddy-52` /
   `claude-buddy-a8`) worth fixing rather than shrugging at. **Resolved on
   24 Aug 2026 — see "What actually names a relay" at the bottom of this file.**
   The observation here was right: the name passed is ignored. What names a
   session is its working directory.
2. **Injected prompts and a human's typing interleave in the same pty.** Buddy
   can paste a prompt into the same input line a user is mid-sentence in. So the
   bridge must be a session Buddy owns exclusively, and Buddy should tolerate
   unexpected turns in the transcript rather than assuming every row is a reply
   to something it sent — which the `from-name`/`msg_id` correlation already
   handles, and is another reason not to rely on turn ordering.

---

# The verbatim mirror (bugfix/remote-chat-verbatim-mirror)

Everything above describes a **messaging channel**, and everything above is
still true of one. What follows is what was added when that channel turned out
not to be enough, and — importantly — what about it has and has not been
confirmed on real machines.

## The bug, stated precisely

A remote session's panel showed **a model's second draft of a conversation, in a
window that looked exactly like the one showing a real conversation.** That is
worse than showing less, because nothing about it reads as a summary.

The cause is in the transport and was already written down, at the top of
`BridgeProtocol.SendMessagePrompt`: a peer message reaches the far session's
*model*, which then composes a reply *for a peer*. Watched side by side, the
remote session's own chat said "Summary for you: 6 messages scanned…" while the
reply it relayed said something different in different words. Both were its own
writing; neither was wrong; the person reading the relayed one could not tell
they were seeing a second draft.

`FidelityRequest` — a parenthetical asking it to give its complete result rather
than a summary — was the mitigation, and it is a request to a model, not a
guarantee. The same mechanism is why `/color` came back *"I can't run /color …
only the harness's own command handler can set"* it: nothing typed into that
panel ever reached the far CLI's input line.

## What was added

When the far machine is **also running Claude Buddy**, the two Buddies talk to
each other through the relays they already run, using framed `CB-MIRROR:`
messages that ride inside the same `<cross-session-message>` bodies. The far
Buddy reads its session's transcript **off its own disk** and types input into
its **own tmux pane**. The relay model in the middle is demoted from author to
courier.

- **Identity is exact, not guessed.** `claude agents --json` (captured live —
  see `AgentRosterTests`) reports `name`, `pid` **and `sessionId`** per
  registration, and the session id is what Buddy's own status files are named
  after. So peer name → registration → status file is a join, not a
  resemblance. An ambiguous name (two sessions, one name) refuses rather than
  picking; the panel then says "no live view", which is visible and honest,
  where a wrong pick would silently show someone's other conversation.
- **Verify or refuse.** Every frame carries a SHA-256 of its payload and the
  last carries one of the whole. An unverified payload is *nulled at the parse
  boundary* so no later code can reach it by forgetting to check. One bad piece
  is re-requested twice; after that the transfer fails and the panel shows an
  error and nothing else. It deliberately does **not** fall back to the
  messaging-channel version on failure — that would substitute a summary at
  precisely the moment integrity failed.
- **Base64, standard alphabet, not url-safe.** Two reasons, both load-bearing:
  standard base64 cannot contain `<` or `>`, so a payload can never close the
  `</cross-session-message>` tag it is travelling inside; and it cannot contain
  `_`, so a payload can never spell `msg_id`, which is the string
  `RemoteControlBridge.AskAsync` waits for to decide a send has been receipted.
  Both are covered by tests.

## Two bugs found on the way, worth recording

1. **`ParseInboundMessage` used `Match`, not `Matches`.** A transcript row
   carrying two `<cross-session-message>` tags — two sessions answering in one
   turn — silently dropped all but the first. No error, no gap, just a message
   that never arrived. Rare with hand-typed messages; ordinary once frames are
   in flight.
2. **The relay's own narration was being delivered as a chat message.**
   `Route` fed *every* `text` block to `ParseInboundMessage`, and the relay
   model quotes the tag back while narrating what it just did. That quote is its
   own writing — sometimes abridged, sometimes reworded — so the panel could
   show a summary of a message beside the message. Fixed by delivering only from
   `user`-type rows, which is where a genuine inbound message lands (captured
   above: *"Replies … arrive on a later turn, as a `role=user` transcript
   row"*). Tool results still come through from any row, since they answer
   requests rather than being read.

## Confirmed vs assumed

**Confirmed**, by the automated suites (`MirrorProtocolTests`,
`AgentRosterTests`, `MirrorRoundTripTests`, `RemoteMirrorChatSessionTests`,
`RemoteMirrorPanelScreenshots`):

- A transcript on disk arrives at the panel byte-identical to the rows a local
  panel would have been given, through chunking, gzip, base64, framing,
  reassembly and window alignment — including a file large enough to need many
  frames.
- A payload altered in flight is refused, not shown, at every level: the parser,
  the assembler, the client, and the panel.
- Window alignment matches `LocalCliChatSession`'s, including the step-over rule
  for a window that lands entirely inside one enormous row.
- Typing is refused when the *far* machine has replying switched off, or that
  session has no pane. The far machine's setting is what decides.
- `/clear` on the far side bumps a generation counter and the client re-anchors
  instead of appending to a file that no longer exists.

**Assumed, and needing a real two-machine run** — these are the honest gaps:

- ~~That `--remote-control <name>` sticks.~~ **Settled — measured, and the
  answer was no.** See "What actually names a relay" below; the code changed as
  a result.
- ~~The size ceiling on a `SendMessage` body.~~ **Measured — 8KB is fine.**
- ~~Whether the relay model reliably relays base64 verbatim, and at what rate.~~
  **Measured — it does, byte for byte.** Both in "Relaying a frame, measured"
  below, along with what it costs, which was the finding that changed the
  design.
- **The residual trust boundary.** Requests are only served to a peer whose name
  wears the relay prefix. Everything on the account shares one namespace, so
  this is a guard against confusion, not against a determined actor with access
  to the account. Typing into a session remains gated by the far machine's own
  reply setting.
- **Windows.** No new surface: Remote Control is gated off entirely by
  `RemoteControlBridge.IsSupported`, and local chat-send already requires a tmux
  pane. The protocol, unit and integration suites still run on the Windows CI
  leg.


## What actually names a relay (measured 24 Aug 2026)

The one thing this document had left open turned out to be load-bearing, and the
answer is not what either the notes above or the code assumed. Probed directly on
this machine rather than reasoned about.

**Both naming flags are ignored.** The CLI advertises two:

```
--remote-control [name]                        Start an interactive session with
                                               Remote Control enabled (optionally named)
--remote-control-session-name-prefix <prefix>  Prefix for auto-generated Remote Control
                                               session names (default: hostname)
```

Neither had any effect. Run from `~`, with each in turn:

```
claude --remote-control cb-probe-explicit-name
  → This session is user-9b [676a8f] — the name other sessions use to message it

claude --remote-control --remote-control-session-name-prefix claude-buddy-rc--claude-probe
  → This session is user-9b [676a8f]
```

The hostname is `host-mbp.localdomain`, so the documented hostname default is
not what happened either.

**The working directory is what names it.** `user` is the basename of
`~`. The pattern holds across every session in the registry — `.../Source/Placement`
is `placement-41`, `.../GTD/Evidence` is `evidence` — and it explains the spike's
`claude-buddy-52`, which was the repo directory all along, not a coincidence.

**Confirmed by construction.** Running the relay from a directory named after
itself produces exactly the name everything is looking for:

```
cwd .../Library/Caches/ClaudeBuddy/rc-cwd/claude-buddy-rc
  → This session is claude-buddy-rc-43 [b57bc7]
```

No folder-trust prompt was raised by a fresh directory under `~/Library/Caches`.

**Two bugs this was silently causing**, both of which the fix cures:

1. `BridgeProtocol.IsOwnRelay` matches the `claude-buddy-rc-` prefix, and until
   now **it matched nothing**. A relay left registered by a dead process shows up
   as an offline peer, so it was worth an orb after all — a phantom orb named
   after Buddy's own plumbing, which is the exact failure that test was written
   to prevent.
2. The verbatim mirror finds a far Buddy by the same prefix, so it could never
   have found one. Every remote panel would have stayed a messaging channel with
   nothing on screen to say why — degraded, never wrong, and completely silent.

**Also worth recording: the peer-row format has gained fields.** The capture at
the top of this file shows three; a row now carries up to five:

```
persist chat window resize [ae1bcf]  ·  bg  ·  busy  ·  started 15h ago
job-lawyer [34984f]  ·  interactive  ·  idle  ·  tmux testpane:@9.%10  ·  started 4d ago
```

`BridgeProtocol.ParseAgents` tolerates this without change — its `status` group
is `.+?` to end of line, so the extra segments land in `status`, and `Working`
and `IsOffline` both test by substring. Worth knowing before anyone tightens that
regex: the trailing segments are not stable and `tmux <session>:@<win>.%<pane>`
exposes the tmux session name, which is a second, independent way to recognise a
relay if the cwd trick ever stops working.

**And one thing settled for free:** the name in `claude agents --json` and the
name a peer sees in `ListAgents` are **the same name**. `user-9b`
appeared in both, as did `claude-buddy-rc-43`. That is what makes AgentRoster's
name → sessionId join valid — it is joining across one namespace, not two.


## Relaying a frame, measured (24 Aug 2026)

Three things were being carried as assumptions: whether a `SendMessage` body
would hold an 8KB frame, whether a model would relay base64 verbatim, and what a
mirror costs. All three are answered, and the third moved the design.

Measured with two Remote Control sessions on this machine — the transport is the
same whether the peer is on another machine or this one, and a receiver does not
have to *reply* for fidelity to be checked: the message lands in its transcript
and can be read off disk.

### Fidelity: yes, byte for byte

A real `CB-MIRROR` frame — 6144 bytes of incompressible payload, 8372 characters
of frame — was relayed and arrived **byte-identical**:

```
cross-session-messages found: 1
--- row type=user body_chars=8372 first_line_chars=8372
   payload_bytes=6144 hash_matches=True
   byte_identical_to_sent=True
```

Note `type=user`. That is the row type `RemoteControlBridge.Deliver` now requires
before treating anything as an inbound message, confirmed independently here.

### Size: 8KB is not the limit

The 8372-character frame went through as one `SendMessage` with no complaint
(`inputchars=8572` including the envelope). `MirrorProtocol.ChunkBytes` is not
near a ceiling.

### The permission classifier blocks it, and silently

**The first attempt failed, and this is the finding most likely to bite
someone.** A relay inheriting the user's default permission mode — auto — had
its `SendMessage` refused outright:

```
Permission for this action was denied by the Claude Code auto mode classifier.
Reason: Blocked by classifier.
```

The session's own reading of it is worth quoting, because it is not going to
stop being true: *"a ~9KB opaque base64 blob being relayed to another agent
session looks like an exfiltration/injection pattern to the classifier."* It
does, and that is what the classifier is for.

Nobody is watching a detached relay, so nothing can approve anything: in auto
mode every frame is denied, and in the default mode it would sit on a prompt
until the request timed out. The mirror would carry nothing and say nothing.

Fixed by launching the relay with `--permission-mode acceptEdits --allowedTools
SendMessage ListAgents` — specific rather than permissive, and emphatically not
`--dangerously-skip-permissions`. With that, the same frame went straight
through.

### Cost: this is what changed the design

**~9,100 output tokens and roughly a minute and a half, for one 8KB frame.** The
sender has to *emit* the entire base64 blob as a tool argument, so the cost is
proportional to the frame and there is no way around it.

That is survivable for one frame. It was not survivable for the payload the
mirror was actually sending. Raw transcript rows, across eight real transcripts,
for one panel open:

```
 rows   rowKB  rowGz rowFr | turns  turnKB turnGz turnFr
  127   333.5   74.8    13 |    60    25.9    8.6      2
  185   408.8   99.0    17 |    60    13.4    4.6      1
   99   248.9   79.1    14 |    57    38.5   11.6      2
   54   315.8  178.7    30 |    30    10.8    4.1      1
  268   447.3   94.2    16 |    60    11.9    3.7      1
```

**13 to 30 frames**, so roughly 120,000 to 270,000 output tokens and twenty
minutes to an hour to open one panel. Unusable, and the estimate in the original
design ("typically 1-5 chunks") was simply wrong.

The reason is in the right-hand columns. An assistant row is enormous and almost
none of it is shown — tool_use blocks, thinking, tool results — while
`ChatTranscript.Map` renders a tool call as a single summary line and drops
thinking entirely. The row costs ten to thirty times the turn it produces.

**So the wire now carries turns, not rows: one or two frames, worst case two.**
The far Buddy reads the file off its own disk and parses it with the *same*
`ChatTranscript` this app uses locally, then sends the result. Nothing composed
by a model is in the path, the hash still proves the turns arrive exactly as
that Buddy produced them, and what is displayed is still what a local panel
would display — the parse simply happens on the side that has the file, which is
the side that had to be trusted anyway.

## Teams (CB-223)

A team of agents running on another machine used to draw, at most, its lead here. The cause is in two places, and two different sources describe it, both measured on 2026-10-02 on the MacBook, from inside a live 15-member agent team:

| source | what it lists for a team |
| --- | --- |
| `claude agents --json` (the agent registry, same `CLAUDE_CONFIG_DIR`) | **no members at all** — one row, a background daemon, while `ps` showed fifteen `claude` processes carrying `--agent-name`, `--team-name` and `--parent-session-id` |
| the relay's `ListAgents` peer list (Wren Asare's read) | one `Remote Control` row **per member**, every one named with the lead's session title, told apart only by the bracketed ref |

They are not in tension; they answer different questions. The registry is what a far Buddy's direct link builds its roster from (`RemoteMirrorServer.HelloAsync` walks it, then joins each name to a status file), so a far Buddy never offered a member at all — **and that was the whole of the live symptom.** The peer list is what the relay path used to read, but the relay poll was deleted on 30 Aug 2026 (937de9ec, "Delete the relay"): nothing in the app calls `RemotesFrom`, `BridgeProtocol.ParseAgents` or `ListAgentsPrompt` any more (checked against a positive control — the same search finds `RemotesFromRoster`'s live caller), and remote orbs come from the direct link alone. A far machine without Claude Buddy draws no orbs today, team or not. Found by Bryn Kowalczyk while starting the relay half the plan had assumed.

### Direct link (mirror half)

- **The far Buddy knew all along.** Each member is a Claude Code process with its own status file, and `AgentTeam` reads its lead and team name off the process's command line. The live scan has always done this. `SessionManager.HeadlessSnapshot`, which is what a peer is served from, now does too: one batched `AgentTeam.OfAll` for the Claude Code pids it kept, and none when it kept none (on Windows that read is a WMI query; the snapshot runs on the link's read loop, not the UI thread, and is memoised for two seconds).
- **Members are offered from status files, on the complete roster only, and only when their lead is offered.** Not every team is meant to be visible: `HelloAsync` deliberately does not make visible a session the far machine's own Remote Control setting hides, and a member whose lead is hidden stays hidden. A member that leads its own team makes *its* members eligible, so the rule runs to a fixpoint; a lead cycle adds nothing neither half already had.
- **Three trailing optional fields on `MirrorRosterEntry`:** `lead` (the lead's **route**, never its title — every member inherits the title, and a route is what the near side keys the lead's orb by), `agent` (the member's name in its team) and `agentColor` (the colour Claude Code assigned it). `lead` is set only when that route is in the same roster. Absent fields serialise as absent, so a roster with no team in it is the same bytes and keeps the same CB-216 hash; an older Buddy reads a new roster as having no teams.
- **The near side pairs on its own keys.** A member's `Lead` becomes `rc:<account>:<lead route>`, which is exactly the lead orb's key, so `TeamLinks` pairs them through the same rule it uses for a local team and `SetTeamRole` draws the member smaller. Colour follows the local precedence: the session's own `/color`, then the team's assignment, then a hash of the name.

**Measured:** the two sources in the table. **Covered by tests, not run across two machines yet:** the exchange through a real `RemoteMirrorServer` and `RemoteMirrorClient` (members offered with lead routes; a member of a hidden lead not offered; a team-free roster carrying none of the fields), and the near-side scan drawing linked member orbs with an unlinked control.

## The peer-list format, recorded after its parser was deleted (CB-238)

`BridgeProtocol.ParseAgents` and the `RemoteAgent` record were removed in CB-238 because nothing reachable called them after the relay went (`937de9ec`). What they encoded about Claude Code's `ListAgents` output is kept here, as measured fact rather than as code.

**Row shape.** A peer row matched this, and nothing looser:

```
^\s+(?<name>\S(?:.*?\S)?)\s+\[(?<ref>[^\]]+)\]\s+·\s+(?<kind>[^·]+?)\s+·\s+(?<status>.+?)\s*$
```

- **Anchored to the leading indent on purpose.** The header line (`This session is <name> [<ref>] — …`) is flush left and carries a name and a ref in exactly the same shape; the indent is the only thing telling a peer row from it.
- **`status` runs to the end of the line**, so the extra segments rows gained later (`· tmux <session>:@<win>.%<pane>`, `· started 4d ago` — see "Also worth recording" above) landed in `status`. The busy and offline tests were substring tests, so they kept working. Measured again during CB-223 (Hana Moriyama, 2 Oct 2026, against a captured 16-row peer list): a `bg` row parsed as kind `bg` with status `busy · started 22m ago`; a `Remote Control` row with a `· started 5m ago` suffix still read as Remote Control, not offline, and worth an orb; offline rows read as offline with or without the suffix; a `Contains("running")` status test tolerated the suffix.
- **`kind` is a raw, open label** — `Remote Control`, `interactive`, `bg` have been seen. It was kept as text rather than an enum so that a new label shows up as itself.

**What made a row worth an orb**, when there was a relay to draw from: kind `Remote Control`, and not offline (`status` contains `offline` — a registration outlives its process, so dead sessions linger as offline peers), and not one of Buddy's own relays (`MachineNames.IsRelayName` on the name; that test survives in `MachineNames.LooksLikeALeftoverRelay`, which still hides relays left running from before the upgrade).

**Addressing a peer whose name is not unique.** `SendMessage` accepts `name [ref]` as well as a bare name. Several live sessions can share a name — every member of an agent team lists under its lead's session title (CB-223) — and a bare name then reaches only one of them. The deleted `BridgeProtocol.AddressFor` sent to the bare name when at most one *live* peer had it, and to `name [ref]` (the first live namesake's ref) when two or more did. Offline namesakes did not count, since one live session wearing several stale registrations of its own is the usual way a name looks duplicated.

**Self-exclusion** is the tool's own promise: the session asking is named in the header and "not listed below". Nothing needed filtering out.

# Slash commands over the bridge (CB-306)

Measured 2026-11-03, Claude Code 2.1.317, with two throwaway `claude --bg` jobs in a scratch folder holding one custom command (`.claude/commands/cb306probe.md`), one started with `--remote-control` and one without, both stopped afterwards; the probe is `tools/claude-cloud-probe`.

**Every `--bg` job registers a bridge, asked for or not.** Both registry files (`~/.claude/sessions/<pid>.json`, the file `SessionRegistry` reads) carried a `bridgeSessionId` — a `session_` id — beside `kind: "bg"`; so did the interactive 2.1.316 session already running on this Mac. `GET /v1/code/sessions/<that id>` answered 200 with `environment_kind: "bridge"` and `connection_status: "connected"`; `/v2/ccr-sessions/<id>` answered 404 about 25 s after the spawn and 200 at 60 s, with the bogus-token control giving 401 both times. **So the correlation key the plan asked for is `bridgeSessionId` in the job's own registry file** — no roster search is needed; `DesktopSessionRecords.BridgeFor` stays the Desktop-only path and the registry is the general one. Not measured: whether a `-p`/SDK session, or a teammate spawned without `--bg`, gets one.

**A user event POSTed to the bridge is a peer message.** Sends of `/compact`, `/cb306probe arg-one`, `/help`, `/color pink`, `/rename …` and `/version` all answered 200; every one arrived in the job's transcript as a `user` row with `userType: "external"`, `turnOrigin: "peer"`, `origin: {kind:"peer", from:"unknown"}`, `queueSkipSlashCommands: true`, and the content "Another Claude session sent a message:\n/compact\n\nThis came from another Claude session — not typed by your user…". The model answered that built-ins are not its to run. The custom command differed only in that the model chose to invoke the matching skill itself (a `tool_use` of the skill, then its PROBE-OK) — the dispatcher never ran it, and a model deciding to honour a request is not a route. This confirms CB-266's "arrives as a peer message" comment and refutes the plan's reading that the bridge slash branch would see our text.

The bridge slash branch, for the record (chunk `rx7mcg39`, `resolveBridgeSlashOverride`): for bridge-carried input it resolves the command; a prompt-type command runs unless it sets `disableBridgeInvocation`; a built-in runs if it is in the allowlist `ftr` — `compact, auto-mode-setup, autocompact, clear, usage, context, config, output-style, goal, exit, stop, version, usage-credits, extra-usage, rename, color (the non-interactive variant; the interactive local-jsx one is rewritten to it), fast, focus, effort, advisor, model, mcp, skill-doctor, reload-skills, reload-plugins` — `restart` is in the set and explicitly excluded; anything else is refused with "`/x` isn't available over Remote Control." The names were read off the command objects in the same chunk (no chunk header between them, and `restart`'s exclusion matches its own refusal string). None of that is reachable from this app, because our events are not bridge-carried input.

**Control requests run, and do things.** `set_color pink` → `success`; `rename_session` → `success`; `set_model default`, `set_permission_mode default` and `set_max_thinking_tokens null` → `success`; `set_color not-a-colour` → `error` "Unknown color … Available: red, blue, green, yellow, purple, orange, pink, cyan, default", which is the control showing the handler evaluates the field. Each is answered by a `control_response` row carrying the request's `request_id`.

**init on the bridge.** The bridge publishes `system/init` durably after every turn; the one read carried `slash_commands[108]`, the custom command and this machine's skills included, and `terminal_slash_commands` = `doctor, color, exit, focus, reload-plugins, statusline`. The stream opened from sequence 0 replays them all.

**Reading a control request's answer back (CB-306 product-readiness round).** Measured 2026-11-03 on this Mac, Claude Code 2.1.318, against a throwaway `claude --bg --remote-control` job in a scratch folder, stopped afterwards. Three control requests were posted to its bridge with `tools/claude-cloud-probe control … --bridge`: `set_color not-a-colour`, `set_color default`, and a subtype that does not exist. Then the events were read with `v1-events --save` (`GET /v1/code/sessions/<bridge>/events?limit=20&sort_order=desc`). Each request was answered by a `control_response` row, `event_type: control_response`, `source: worker`, 160–220 ms after its `control_request` row, newest first. The payload was `{type:"control_response", session_id, uuid, response:{subtype, request_id, error?}}`, and every `response.request_id` matched the request's. The answers were `error` "Unknown color "not-a-colour". Available: red, blue, green, yellow, purple, orange, pink, cyan, default", then `success`, then `error` "REPL bridge does not handle control_request subtype: cb306_no_such_subtype". So the POST's 2xx says only that the event was stored, and the CLI's verdict is a separate row a client has to go and read. `CloudTurnSender.PostControlAndReadAnswerAsync` now does that for the bridge path: four reads 500 ms apart, with no answer in that time reported as sent, as before. The redacted page is the fixture in `tests/UnitTests/ControlAnswerTests.cs`. Not measured: the same read against a cloud session's `/v1` events (the cloud panel does not use it), and how long an answer takes on a session that is mid-turn.
