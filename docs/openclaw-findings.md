# OpenClaw gateway — spike findings

Everything here was measured against one real gateway — OpenClaw **2026.7.1-2**
(`0790d9f`), protocol **4**, running on a Mac mini on the LAN, probed
from a MacBook on the same subnet — unless it says otherwise.
Where something is assumed rather than observed, it says so.

The short version, in the order it was discovered:

1. The planned **ssh tunnel is unnecessary** — the gateway is LAN-bound and
   reachable directly.
2. **Ed25519 signing is unavoidable.** A bearer token alone is refused with
   `DEVICE_IDENTITY_REQUIRED`, and scopes belong to a *paired device*.
3. **The gateway is TLS 1.3-only and .NET on macOS cannot speak TLS 1.3.** This
   overrides (1): the tunnel doesn't help either, because loopback is TLS too.
   The connection has to be made with a managed TLS stack — BouncyCastle, which
   (2) already put in the project.

Everything below is measured. Where a conclusion was later overturned by a
further measurement, both are kept, because the wrong one was reached honestly
and the reason it was wrong is the useful part.

## Reachability — the tunnel is not needed for *this*

The plan assumed a loopback-only gateway reached through
`ssh -N -L`. That premise is false here:

| checked | result |
| --- | --- |
| `gateway.bind` | `lan` — the listener is `*:18789`, not `127.0.0.1:18789` |
| `gateway.mode` | `local` |
| `gateway.tls.enabled` | `true`, cert `~/.openclaw/tls-cert.pem` |
| TCP from the laptop | open, ~3 ms |
| `https://198.51.100.10:18789/` | 200, serves the Control UI |
| `http://…` (plain) | `curl: (52) Empty reply` — TLS only |
| WebSocket upgrade | **101 on every path tried** (`/`, `/ws`, `/gateway`, `/api/ws`, `/socket`) |

So the client connects directly to `wss://198.51.100.10:18789/`. No child
process, no port allocation, no orphan sweep, no `ControlMaster`/
`ExitOnForwardFailure` hazards — the entire `SshTunnel` component and its
shutdown-hook story can be deleted from the plan.

The token is not exposed by dropping the tunnel: TLS is on, so it is encrypted
in transit exactly as it would have been inside ssh.

**Still true after the TLS finding below.** The tunnel stays deleted — not
because TLS made it redundant, but because it would not have helped: the
gateway refuses plain HTTP on loopback as well, so a forwarded port arrives at
the same TLS 1.3 handshake.

### TLS 1.3 only — and .NET on macOS cannot speak it

**This is the finding that decides the transport, and it kills the direct
connection.**

```
openssl -tls1_2 → alert 70, "tlsv1 alert protocol version"
openssl -tls1_3 → New, TLSv1.3, TLS_AES_256_GCM_SHA384
```

The gateway sets `minVersion: "TLSv1.3"` in its own source
(`dist/gateway-*.js`) — hardcoded, not exposed in config, so it cannot be
relaxed without patching OpenClaw.

.NET cannot meet it on this platform:

| from .NET 10.0.9 / osx-arm64 | result |
| --- | --- |
| `cloudflare.com:443`, `Tls12` | OK |
| `cloudflare.com:443`, `Tls13` | **"The requested security protocol is not supported"** |
| gateway, `Tls12` | "bad protocol version" (the server's alert 70) |
| gateway, `Tls13` | "The requested security protocol is not supported" |

Failing against Cloudflare as well is what makes this conclusive: it is a
property of .NET on macOS (SecureTransport tops out at TLS 1.2), not of this
gateway or this network. Windows is unaffected — but macOS is this app's
primary platform.

An ssh tunnel does **not** rescue it. The gateway is TLS-only on loopback too
(`http://127.0.0.1:18789/` → "Empty reply from server"), so forwarding the port
still lands on a TLS 1.3 handshake that .NET cannot complete.

That leaves BouncyCastle's managed TLS 1.3 client — already a dependency for
Ed25519 — with the WebSocket upgrade hand-rolled over it and handed to
`WebSocket.CreateFromStream`.

### The served certificate is not the configured one

`gateway.tls.cert` points at `~/.openclaw/tls-cert.pem`, an mkcert leaf
(`O=mkcert development certificate`, SAN `localhost, 198.51.100.10, 127.0.0.1`,
sha256 `48911757…`). **The listener does not serve it.** What it actually
presents is self-signed with no SAN at all:

```
subject  CN=openclaw-gateway
issuer   CN=openclaw-gateway          (self-signed)
valid    Aug 16 2026 → Aug 13 2036
SAN      (none)
sha256   34da256e1eaedac88e0e01b251d57c56a7a5ae85221a3affa7da0b752b56a617
```

So **pin the fingerprint and skip name validation entirely** — with no SAN there
is no hostname to validate against, for any host or IP. Why the configured cert
isn't being served is unexplained and worth a look if TLS is ever reconfigured.

**Overtaken by a later measurement — do not pin `34da256e…`.** Probed again from
Windows on 2026-08-17, the same listener serves the **mkcert leaf after all**:

```
subject  O=mkcert development certificate, OU=user@host-mini…
issuer   O=mkcert development CA, CN=mkcert user@host-mac-mini…
sha256   48911757b22504ef68e43e681302541b653228ea441e2470205b783ff56b8cff
```

That is the fingerprint of the *configured* cert, so whatever was overriding it
in the paragraph above has stopped — a restart or a TLS reconfiguration in
between is the likely cause, and neither was observed happening.

The conclusion that survives is the one that matters: **no fingerprint belongs in
the source.** Which certificate this gateway serves has now changed once during
development, so a literal in the code would have shipped broken. The client
trusts what it sees on the first connection and stores it in
`openclawFingerprint`, which is why this change cost nothing to absorb. Skipping
name validation also still stands, and for a second reason now — the mkcert leaf
does carry a SAN, but it names `198.51.100.10`, so validation would break the
moment the gateway moved to another address.

Both fingerprints are kept above because the *variability* is the finding.

**Correcting PR #168's scrub claim.** That PR's description said "no credential,
token, key or fingerprint was found anywhere in the tree." Read next to this
section, that is too wide: the two sha256 fingerprints just above are exactly
what the phrase says isn't here. They are not the mistake — a certificate
fingerprint is public key material, the value you pin *against*, not a value
that grants access on its own; publishing one is how pinning gets verified, not
a leak. What the scrub actually found and removed was **secrets** — an account
id, message text, filenames tied to a real conversation — and the claim should
have said that, not the broader word "fingerprint." Nothing here was rescrubbed
as a result; the fingerprints stay, on purpose, for the reason two paragraphs up.

**Method note:** an earlier round of these probes used `timeout 10 openssl …`.
macOS has no `timeout`, so those commands never ran and their empty output read
as "no TLS handshake". Two conclusions were briefly drawn from that and both
were wrong. `timeout` is a GNU coreutils binary; don't reach for it here.

## Ed25519 signing is required — the risk landed

The plan hoped a bearer token alone would authenticate. Measured, in order:

| attempt | result |
| --- | --- |
| `client.id: "claude-buddy-spike"` | rejected — `client.id` is an enum |
| `client.mode: "operator"` (as the docs show) | rejected — `mode` is a different enum from `role` |
| `client: {id: "cli", mode: "node"}` + `auth.token` (the gateway token) | **`hello-ok`** — but `auth.scopes: []` |
| `sessions.list` on that connection | `missing scope: operator.read` |
| `auth.token` = a paired device's token, as `cli`/`cli` | `unauthorized: gateway token mismatch`, `canRetryWithDeviceToken: false` |
| `openclaw-control-ui`/`webchat` identity | `origin not allowed` — browser-only path |
| `auth.deviceToken` (correct field), no `device` block | **`NOT_PAIRED` / `DEVICE_IDENTITY_REQUIRED`** |

**Scopes are granted per paired device, not per connect request.** Asking for
`scopes: ["operator.read"]` in `connect` is ignored; `openclaw devices list`
shows nine paired devices each carrying their own role, scopes and token. The
gateway token gets you *connected* with zero scopes, which is useless — every
read method refuses.

So Claude Buddy must be a paired device, and a paired device must present the
optional-in-schema-but-mandatory-in-practice `device` block. **`BouncyCastle`
goes into the plan**, for the packaging reasons already recorded there (NSec is
libsodium, and a native dylib inside a single-file hardened-runtime bundle is a
signing problem this project has been bitten by before).

### The exact signing format

From `dist/src-DZzKBMa7.js` and `dist/device-identity-UW4cZXf5.js`:

- Keypair: **Ed25519**, generated once and persisted.
- `deviceId` = `sha256(raw 32-byte public key)`, hex — matches the 64-hex ids in
  `devices/paired.json`.
- `publicKey` on the wire = **base64url** of the raw 32 bytes (43 chars, no
  padding). Same encoding for `signature`.
- Signed payload is a pipe-joined string, signed raw (no prehash):

```
v3|deviceId|clientId|clientMode|role|scopes(comma-joined)|signedAtMs|token|nonce|platform|deviceFamily
v2|deviceId|clientId|clientMode|role|scopes|signedAtMs|token|nonce            (older)
```

`nonce` is the one from the `connect.challenge` event.

**`token` is not the device token**, which is the obvious reading and is wrong.
The gateway's `resolveSignatureToken` takes

```js
connectParams.auth?.token ?? connectParams.auth?.deviceToken ?? connectParams.auth?.bootstrapToken ?? null
```

so whenever a gateway token is being sent — which is always, see below — it is
the **gateway** token that gets signed, and `null` becomes `""`. Signing the
device token instead fails as `DEVICE_AUTH_SIGNATURE_INVALID`, which reads like a
broken key rather than a field mix-up and cost an hour to spot.

### All three credentials are required at once

`auth.token` alone → connected, no scopes. Device identity alone →
`AUTH_TOKEN_MISSING`. Both, unapproved → `PAIRING_REQUIRED`. So a working
connect carries the gateway token (*may this client talk to this gateway*), the
signed device block (*and is it who it claims*), and — once paired — the scopes
the gateway holds against that device record.

The device token itself turned out **not** to be needed for a connect that
already carries the gateway token; the approved device record is what grants the
scopes.

**The `requestId` rotates on every attempt.** Each refused connect replaces the
pending entry, so an id read from the pending list is stale as soon as the client
retries — approve with `openclaw devices approve --latest`. The stable
identifier is the `deviceId`.

### Pairing is the on-ramp

`~/.openclaw/devices/bootstrap.json` holds a single-use bootstrap token whose
profile grants exactly what this feature needs:

```
roles  ["node", "operator"]
scopes ["operator.approvals", "operator.read", "operator.talk.secrets", "operator.write"]
```

`redeemedProfile` was empty at probe time, i.e. unredeemed. `connect.params.auth`
accepts `token | bootstrapToken | deviceToken | password`, so the first-run flow
is: generate a keypair → connect presenting `bootstrapToken` + a signed `device`
block → receive a device token → store it → sign with it thereafter.
`openclaw devices approve` is the manual alternative.

**Not yet verified end-to-end**, because redeeming the bootstrap token writes a
new paired device to the gateway. That is the one remaining gap.

### `client.platform` is part of the device identity

Measured on Windows, 2026-08-17, and it is the sort of thing that only shows up
on a second platform. The client sent a hardcoded `"macos"` from every machine,
which *authenticated fine* — the gateway recomputes the signature from the fields
it was given, so a value that is wrong consistently still verifies. Correcting it
to `"windows"` was refused:

```
PAIRING_REQUIRED: pairing required: device identity changed and must be re-approved
```

So `platform` is folded into the paired device identity, alongside role and
scopes, rather than merely logged next to it.

**And the resulting state cannot be cleared by approving.** Measured in this
order, which is the useful part:

| step | result |
| --- | --- |
| paired and connected sending `"macos"` | `Connected`, `operator.read` granted |
| changed to `"windows"`, reconnect | `PAIRING_REQUIRED: device identity changed and must be re-approved` |
| `openclaw devices approve --latest` on the gateway | reports the device **already paired**; nothing offered to approve |
| reconnect again | same refusal |
| reverted to `"macos"`, reconnect | **same refusal** |

So the gateway holds two views at once: its device list says paired, and its
connect path says the identity changed. The pending identity-change is not
visible to `approve --latest`, and reverting the field does not put things back —
the first refused attempt is enough to leave the record unsatisfiable by either
value. The way out is to **remove the device on the gateway and pair from
scratch**.

Two consequences for this code:

- Correcting `platform` is only cheap because **no Windows or Linux device has
  ever been paired** — the value is unshipped. macOS keeps sending the string it
  always sent and notices nothing.
- **Nothing version-specific belongs in that field, and neither does anything
  else that might later be "improved".** `"windows"` is stable; `"windows 11
  26200"` would strand every paired device on every OS update, with a manual
  removal on the gateway as the only remedy. The same caution applies to
  `client.id`, `client.mode`, `role` and the scope set.

**Method note, because it weakens the cleanest version of the claim:** the revert
test was contaminated — by the time it ran, the first refused attempt had already
put the record into the state above, so "`macos` is refused too" does not
distinguish *platform is identity-bearing* from *any pending identity-change
blocks every connect*. Both are consistent with everything observed, and the
remedy is the same either way. What is certain: the refusal began exactly when
the field changed, and the gateway names device identity as the reason.

Also still hardcoded next to it: `client.version` is the literal `"0.3.0"`, which
will disagree with `<Version>` in the csproj the moment that moves. Left alone
because changing it is a re-approval by the paragraph above, so it wants doing
once, deliberately, with a value derived from the assembly.

### Enums, measured not guessed

```
client.mode   webchat | cli | test | probe | ui | backend | node
auth          token | bootstrapToken | deviceToken | password
device        { id, publicKey, signature, signedAt, nonce }   -- optional in the
                                                                 schema, required in practice
```

The published docs say `client.mode: "operator"`; the gateway rejects it. `role`
is the separate field that takes `operator`.

## Numbers the implementation needs

From `hello-ok`:

```
protocol            4
server.version      2026.7.1-2
policy.maxPayload   26214400      (25 MB — the frame-accumulation bound)
policy.tickIntervalMs 30000       (the last-frame watchdog threshold)
snapshot keys       presence, health, stateVersion, uptimeMs, sessionDefaults
```

**Event rate is trivial**: over 25 s on an idle connection, `health` ×2 and
`tick` ×1. This confirms the plan's decision to cut the `Channel<T>` and
coalescing pump — a direct `Dispatcher.UIThread.Post` is ample.

## Pairing works, and `sessions.list` is nothing like a Claude Code session list

Paired as `gateway-client` / `ui` / `operator` with `operator.read`, approved
with `openclaw devices approve --latest`. Two notes on the flow itself:

- **The `requestId` rotates on every connection attempt.** Each refused connect
  replaces the pending entry with a new id, so a request id read from the
  pending list is stale the moment the client retries. `--latest` avoids the
  race; the stable identifier is the `deviceId`.
- The gateway grants scopes from the *paired device record*, so the
  `scopes` in `connect.params` is a request, not a claim.

### 59 sessions, and almost none of them are interesting

This is the finding that changes the feature's shape:

| measure | value |
| --- | --- |
| total sessions | **59** |
| `hasActiveRun` | 0 at rest |
| active in the last 5 minutes | 2 |
| ...last hour | 8 |
| ...last 24 hours | 20 |
| `status` values | `done`=32, absent=24, `failed`=3 |
| `origin.provider` | `discord`=45, absent=12, `webchat`=2 |

A Claude Code status directory holds the handful of sessions actually running.
An OpenClaw gateway holds **every conversation it has ever had** — every Discord
channel and DM, every cron job, every named agent. One orb per session is 59
orbs, which is not a display, it's a wall.

The existing "Keep orbs for" setting is the natural filter, and it needs no new
concept: stamp each entry's `Written` with the session's own `lastActivityAt`
rather than `UtcNow`, and the lifetime timer prunes quiet sessions exactly as it
does for a quiet Claude Code session. This **reverses** the plan's instruction to
stamp `UtcNow` while connected — that rule was written on the assumption that a
listed session is a live one, which is true of the hook's status files and false
here. A session with `hasActiveRun` should be exempt from pruning, the same way
`waiting` already is.

### The fields that matter

```
sessionKey        agent:main:discord:direct:200000000000000001
                  agent:alexis:main | agent:main:cron:<uuid>
                  -> "agent:<name>:<surface>[:<type>:<id>]"; the agent name is
                     in the key and nowhere else useful
label             usually empty — set on cron sessions ("Cron: disney-news-daily")
origin            { label: "#general channel id:...", provider: "discord",
                    surface: "discord", chatType: "direct" | "channel" }
                  -> origin.label is the best human name (47 of 59 have one)
hasActiveRun      bool  -> the "generating" signal
status            "done" | "failed" | absent
lastActivityAt    epoch ms  -> what the stale timer should use
updatedAt         epoch ms
model             "claude-sonnet-4-6"    modelProvider "claude-cli"
totalTokens, runtimeMs, startedAt, endedAt
lastChannel       "discord"
```

There is **no `cwd`**, which retires the plan's worry about position keys
colliding with a local checkout of the same path — an OpenClaw session has no
directory to collide with.

Naming an orb: `origin.label` first, then `label`, then the agent name parsed out
of `sessionKey`. The raw labels are workmanlike (`some-user user id:1000…`), so the
agent name plus surface (`alexis · discord`) may read better than either.

## A live turn: the state signal is the event stream, not the session list

Observed by watching a connection for six minutes while a cron job
(`stalled-session-watchdog`) ran on its own. Event totals for that window:

```
agent 15 | task 4 | session.tool 4 | presence 3 | cron 3 | chat 3 | session.message 1
```

| event | payload |
| --- | --- |
| `agent` | `stream: "thinking" \| "assistant"`, `data.text` (full snapshot) **and** `data.delta`, `runId`, `seq` |
| `session.tool` | `stream: "tool"`, `data.phase: "start" \| "result"`, `data.name`, `args`, `isError` |
| `chat` | `state: "delta"`, `deltaText` |
| `task` | `action: "upserted"`, `task.status: "completed"`, `kind: "cron"` |
| `cron` | `action: "finished"` |

**`session.operation` — which the docs name as the in-flight signal — never
fired.** That is the third documented detail contradicted by the running
gateway, after `client.mode` and the signature token.

Two structural facts:

- **Event `sessionKey`s are run-scoped**: `agent:main:cron:2f54203e…:run:aa07ba9a…`,
  the listed key with `:run:<runId>` appended. Attributing an event to a session
  means stripping that suffix. Run-scoped keys never appear in `sessions.list`
  (checked: 59 sessions, 0 containing `:run:`).
- **`hasActiveRun` never flipped**, across the whole window, through a complete
  run — zero transitions from a 2.5 s poll. So "working" must be driven by
  event arrival, with idle inferred from a terminal event (`task.status:
  "completed"`, `cron action: "finished"`) or a quiet period. This is the better
  design anyway: push-based and immediate, where polling was both laggy and, as
  measured, wrong.

`data.text` carrying a **full snapshot** rather than only a delta is a gift for
the chat panel — it is exactly the "TurnUpdated carries the whole turn, not a
delta" property the panel design asked the transport to guarantee, so a dropped
or coalesced event cannot desync the view.

## A run outside a cron session (CB-169)

The section above was a cron run on the cron job's own session, and the orb's "working" rule was built from it. CB-169 (an OpenClaw orb never glows while its conversation replies) is what a run on any *other* session looks like. Captured 25 Sep 2026 against OpenClaw 2026.9.2 with `dotnet run --project tools/openclaw-probe -- events <seconds> - <out.jsonl> [messagesKey]`, which subscribes exactly as the app does. Two windows: 300 s on `sessions.subscribe` alone, and 400 s with `sessions.messages.subscribe` added for `agent:main:main`.

**What was captured is a heartbeat, not a person.** The run on `agent:main:main` is the gateway's own every-300-s heartbeat job injecting into the agent's main session (`sessionTarget: "main"`, `systemSent: true` on the session record, 4.5 s runtime). No human Discord turn happened in either window, and none was sent to make one — see the end of this section.

### On `sessions.subscribe` alone, a run is two rows

```
+122.29s sessions.changed agent:main:main  phase=message
+122.29s sessions.changed agent:main:main  phase=start  runId=<run>
+126.78s sessions.changed agent:main:main  phase=message
+126.78s sessions.changed agent:main:main  phase=end    runId=<run>
```

That is everything that named the session. There were **no `agent`, `chat` or `session.tool` events at all**, where the cron run in the same window (on its own `…:cron:<job>` session) streamed thinking, assistant, tool and chat events as in the table above. And the app dropped every `sessions.changed` (CB-152), so for a run like this it received nothing it used. That is the whole of CB-169.

The rows that CB-152 was about have a different shape, and it is what tells them apart: `reason: "cron-binding"` or `"placement"`, **no `phase` and no `runId`**, fired for sessions across the roster at once — the capture has one naming a Discord DM, a Discord channel and six cron sessions inside 2.2 s, none of them doing anything. Phase `message` does carry the session but no runId; it is a transcript row being appended, and it arrives immediately before the start *and* before the end.

### `task/upserted` cannot end a run

Every `task` event in both windows has **no top-level `sessionKey`**. The session is `task.childSessionKey`, and `task.runId` is the cron scheduler's id (`cron:<job>:<ms>:<uuid>`), not the agent run's. So the old rule that cleared a session's glow on a task upsert never fired on a real payload, and could not have named the right run if it had. That rules out one of CB-169's three candidates. The run's own `cron` `finished` already ends it, and that one does carry the run-scoped key.

### `sessions.messages.subscribe` does add the stream — not needed, not wired

Same job, same session, same kind of run, with `sessions.messages.subscribe {key: "agent:main:main"}` added (two runs, 300 s apart, identical):

```
+86.21s agent            agent:main:main runId=<run> stream=lifecycle phase=start
+86.21s sessions.changed agent:main:main runId=<run> phase=start
+90.36s agent            agent:main:main runId=<run> stream=lifecycle phase=end
+90.37s chat             agent:main:main runId=<run> state=final
+90.37s sessions.changed agent:main:main runId=<run> phase=end
```

So the per-session subscription is what carries `agent`/`chat` for a non-cron session. The paired control is the capture above: same heartbeat job, same session, no subscription, and none of these events. A heartbeat produces no thinking or assistant text, so whether a real reply's mid-run deltas arrive this way is **still unmeasured**. First measured terminal `chat` state: `"final"`.

Scope, both ways. **Declared** in `dist/method-scopes-K6J_UQGL.js`: `["sessions.messages.subscribe", "sessions-subscriptions", "operator.read", …]`, the same group and scope as `sessions.subscribe`. **Measured** on one token: `{key}` returns `{"subscribed": true}`. The same method with `includeApprovals: true` is refused with "requires a paired device and gateway scope: operator.approvals", and `tasks.flows` with "missing scope: operator.admin". That token held `operator.read` *and* `operator.write`, because reply was on for the machine, so the acceptance was measured at read+write and the read-only claim rests on the declaration.

It is not wired in. The phase start and end rows already light the orb, and a subscription per session would be one more thing to hold open for every session on the roster.

### How long a run lasts — the number behind `RunCeiling`

The start/end pair has nothing between it, so "no event for 20 s means it stopped" (`RunIdle`) would put out a reply's glow 20 s in. An opened run has to wait for its end. The ceiling on that wait came from the live gateway, not a guess:

- The gateway's own declared run timeout is `DEFAULT_AGENT_TIMEOUT_SECONDS = 172800` (two days), which bounds nothing useful.
- `runtimeMs` for the last run of every session on `sessions.list` that reports one (39 of 71): Discord sessions n=25, median 11.2 s, 12 of 25 over 20 s, three over 5 min, **longest 1812 s**. Main sessions n=5, longest 153.6 s. Subagents n=3, longest 199.4 s.

A 10-minute ceiling would have put the longest run out two-thirds of the way through. `RunCeiling` is 45 minutes: the longest measured run with half as much again on top. It is only a safety net. The run's own end clears it at once. A dropped connection clears every run, because an end sent while the socket was down is never seen.

### Still open

- **Whether a person's Discord DM turn produces the same start/end pair on its `…:discord:direct:<id>` key.** Every run measured was a heartbeat on `agent:main:main` or a cron job. The fix assumes the pair is emitted for any session's run. The source supports that reading without proving it. Both `sessions.changed` sites that set `phase` from a run lifecycle are in `dist/server-chat-*.js`. The one read in context returns early only when `parseCronRunScopeSuffix(sessionKey).runId` is set, which is exactly why the cron session in the capture never shows a phase row. No channel condition appears within ten lines of either site. The full functions were not read, because the gateway host stopped answering ssh partway through. So a Discord key's run is expected to produce the pair, and that is not measured.
- **These rows can be dropped.** Every `sessions.changed` broadcast in `server-session-events-*.js` is sent with `dropIfSlow: true`. A client the gateway judges slow can miss a run's end, and that is what `RunCeiling` and the clear-on-disconnect are for. Nothing was sent to provoke one: this is a read-only investigation, and `chat.send` into a real conversation is out of bounds for it. The check is to leave the `events` probe running while someone DMs an agent.

## Reading and writing a conversation

Two methods beyond the session index, both confirmed working against the real
gateway:

**`chat.history { sessionKey, limit }`** returns
`{ sessionKey, sessionId, messages, defaults, sessionInfo, thinkingLevel }`.
Each message is:

```
role       "user" | "assistant"
content    [ { type: "text", text: "…" }, … ]   -- blocks, not a string
timestamp  epoch ms
model / provider / usage / stopReason
```

`content` being a block list matters: a message can hold tool_use blocks
alongside text, so a naive read of the whole array renders JSON at the reader.
Only the `text` blocks are worth showing in a backlog — live tool calls arrive
as their own `session.tool` events and read better as one line each.

**`chat.send { sessionKey, message, idempotencyKey }`** (also accepts
`agentId`, `sessionId`, `thinking`, `deliver`, `timeoutMs`). Needs
`operator.write`, which means the device has to be **re-approved**: changing the
requested scope set makes the gateway treat it as a new pairing, so it lands
back in the pending queue with a fresh `requestId`.

That re-approval is the reason replying is a second, separate switch in the app
rather than part of turning the feature on. Reading what your agents are doing
and being able to make them do things are different powers.

## `sessions.list` lies about recency

The single most misleading thing measured here. `lastActivityAt` and `updatedAt`
are **hours stale for an active Discord conversation**:

| session | claimed age | actually |
| --- | --- | --- |
| `agent:main:discord:direct:…` | 6640 s (1.8 h) | being chatted in at that moment |
| `agent:main:discord:direct:amber` | 9959 s (2.8 h) | recent |
| `agent:main:cron:…` | 135 s | correct — cron updates every 5 min |

So an activity filter built on those fields hides live conversations. Worse, it
hides them *intermittently*: the event stream reveals the session while a reply
is streaming, and the stale timestamp buries it again twenty seconds later,
which reads as a broken feature rather than as a bad timestamp.

A client that wants "is this live" has to keep its own record from the event
stream and take whichever is later. The list is still needed for sessions that
were active before the client connected — there is no better source for those —
which is why the window is a user setting rather than a constant.

## Agents have names, and they are not the ids

`agents.list` (read scope is enough) returns `{ defaultId, mainKey, scope, agents }`
where each agent is `{ id, name, displayName }`:

```
main → Aurora      comfyui → Nova        kubernetes → Amber
alexis → Alexis      social  → Annabel Lee modder     → Sara
```

Session keys are built from the **id**, so a naive title shows `main` for the
agent its owner calls Aurora — and shows the same letter on four different
orbs. `displayName` is empty on all of them here, so `name` is the field that
matters.

Names alone still under-identify: one agent commonly holds a DM with you, a DM
with someone else and two channels at once, all `agent:<id>:discord:*`. The
distinguishing part is `origin.label`, which is written for a log —
`"#general channel id:1900000000000000001"`, `"riverbend user id:2000…"` — and
cleans up to `#general` and `riverbend` by cutting at `" id:"` and dropping the
noun before it.

## Replying to a cron session works

A scheduled job looked like the one session kind with nobody on the other end,
and therefore the one where a typed reply might go nowhere — the panel nearly
shipped with cron sessions read-only for that reason.

Measured instead of assumed: **a cron session accepts a message and responds to
it.** `chat.send` against an `agent:<name>:cron:<uuid>` key behaves as it does
anywhere else. The job's schedule is what starts a run; it is not a restriction
on the conversation, and the session is an ordinary agent conversation that
happens to have been started by a timer rather than by a person.

So the panel treats cron like any other session, gated only by the same
"Allow replying to agents" switch. The badge still says it is a cron job,
because knowing a run was scheduled rather than asked for is worth seeing — but
that is information, not a wall.

## The gateway does not report heartbeats

Asked while adding the beating-heart badge, which needed to know which sessions
the heartbeat drives. The short answer is that **nothing in the protocol says**,
and the badge is derived from where a heartbeat is documented to *land* instead.

What OpenClaw's own docs establish: a heartbeat is a periodic turn the gateway
sends an agent, it runs in the agent's main session (`agent:<id>:main`) unless a
job's `session` override retargets it, the gateway keeps one system-owned
automation job per heartbeat-enabled agent (`openclaw cron list --all` shows it
as `Heartbeat (<agent-id>)`), and it creates **no** background task record.

What a real gateway said, read with `tools/openclaw-probe` — 84 sessions across
8 agents:

| candidate signal | what it actually held |
| --- | --- |
| the string "heartbeat" anywhere in `sessions.list` | **0 occurrences** |
| `kind` (a field this app doesn't read) | only `"direct"` (35) and `"group"` (49) |
| `label` | set on **3** sessions, all of them cron (`"Cron: disney-news-daily"`) |
| `systemSent` | `true` on 51 — including ordinary Discord chats — and *absent* on 5 of the 8 main sessions |
| `cron.list` | 3 jobs, all user-created. The system heartbeat job was not among them |
| `automations.list`, `cron.jobs.list`, `heartbeat.status` | `missing scope: operator.admin` |

So there is no per-session flag, and no way to ask for one without requesting
`operator.admin` — a scope far beyond "draw a badge", and one this app
deliberately does not hold.

**Nor can a heartbeat be recognised from its messages.** The docs note that the
gateway's own Control UI and WebChat "hide heartbeat prompts and OK-only
acknowledgments", and `sessions.history` hides them too: 60 turns of
`agent:main:main` contained an assistant turn reading `"No response requested."`
with **no user message before it**. The prompt that provoked it was filtered out
on the way. Fifteen mentions of "heartbeat" in that history are all an agent
*talking about* its heartbeat, none of them a heartbeat itself.

What is left is positional, and it is enough. Seven of the 8 main sessions had
`lastActivityAt` inside one 32-second window — 21:55:47, 21:55:52, 21:55:57,
21:56:02, 21:56:07, 21:56:12, 21:56:19, one per agent, staggered a few seconds
apart. (The eighth had no `lastActivityAt` at all and `status: "failed"`.) That
is the sweep, and it is the visible symptom the badge exists to
explain: a screenful of orbs going active together with nobody on the other end.

`OpenClawHeartbeat` therefore answers "where does the heartbeat land" — the
agent main sessions — rather than "was this turn a heartbeat". Two consequences,
both **assumed rather than confirmed** and both stated in its own comment: an
agent with heartbeat disabled still gets a heart, and a heartbeat retargeted at
a channel does not. Neither can say anything false about who can read a
conversation, which is the property that made the direction acceptable.

## How far back a conversation goes — measured

Asked because the room view stopped "suspiciously recent" and it was unclear
whether a week-old conversation was a different session. Probed with
`tools/openclaw-probe`, which speaks over the app's own transport.

**Sessions are not per-period. There is exactly one per key.** 70 sessions, 70
distinct `key` values, no repeats — and `agent:kubernetes:discord:channel:…`
holds messages spanning 10–16 August in a single session with one `sessionId`.
So last week's messages are in the same session as today's, and the two
identifiers (`sessionKey`, `sessionId`) do not indicate per-run instances the
way they appear to.

**What limits history is the gateway, not paging.** `chat.history` returns
everything it has in the first page and says so:

| session | limit | returned | `totalMessages` | `hasMore` | span |
| --- | --- | --- | --- | --- | --- |
| `agent:kubernetes:…:1900000000000000003` | 40 | 33 | — | false | 10–16 Aug |
| same | 500 | **33** | — | false | 10–16 Aug |
| `agent:social:…:1900000000000000001` | 500 | **56** | **104** | false | 17–18 Aug |

Asking for 500 returns the same 33 as asking for 40, and any `offset` past the
end returns zero. So the client already receives the whole of what exists after
its first request — a session holding 218,000 tokens of conversation exposes 33
messages. Scrolling cannot reach further because there is nothing further to
reach; the top of a room view is where the gateway's retention ends, not where
a session began.

`totalMessages` (104) exceeding what is returned (56) is the gateway counting
rows it does not hand over — tool calls and scaffolding — rather than a page
boundary. `hasMore` is false in the same response.

This retires two theories that both looked right: that history is scoped to the
current CLI instance and resets on "OpenClaw resumed this CLI session", and that
deeper paging would recover older messages. Neither survives the measurement.

It also explains the unattributable messages in a merged room view for good. An
old message from another agent can only be matched against that agent's own
assistant turn, and beyond the retention window that turn no longer exists
anywhere. No client-side change reaches it.

### Fields `sessions.list` carries that this app does not use

Read off the same probe, and better than what is currently derived:

```
chatType      "channel" | "direct"     -- top level, not only inside origin
kind          "group" | …              -- the gateway's own classification
groupChannel  "#general"               -- the channel name, plainly
displayName   "discord:1900000000000000004#general"
archived, unread, pinned, space, startedAt, endedAt, status
```

`groupChannel` is the channel name without parsing, where `TitleFor`/`Where`
currently reconstruct it from `origin.label` by cutting at " id:" and stripping
nouns. `chatType` at the top level is a more reliable source for
`OpenClawSessionKind` than `origin.chatType`, which is where it looks now.

## Still unknown

- **The amber "needs you" state.** No `session.approval` fired in six minutes of
  watching, because nothing asked for permission in that window. The event
  exists in the protocol and requires `includeApprovals: true` on
  `sessions.messages.subscribe`, which in turn needs `operator.approvals` —
  a scope this device did not request. Unverified end to end.
- Whether a Discord-originated turn *streams* identically to a cron one.
  **Partly answered by CB-169** (see "A run outside a cron session"): it does
  not. A run on a non-cron session sends only a `sessions.changed` start/end
  pair to a `sessions.subscribe` client, with no agent/chat/tool events. That
  was measured on a heartbeat run on a main session, though, and a person's
  Discord DM turn has still not been captured.
- ~~What a terminal `chat` state looks like~~ — `state: "final"`, measured by
  CB-169 on both a cron run and a main-session run.

## Incidental

`~/.openclaw/openclaw.json` also carries `gateway.remote.transport: "ssh"` and
`gateway.tailscale.mode: "off"`. Neither affects this design, but the first
suggests OpenClaw's own remote story is ssh-based, which is presumably where the
plan's original assumption came from.

## Non-goals — measured, and deliberately not chased

Two categories of residue PR #168 left behind on purpose, re-measured here so
the number stops drifting between mentions. Both are occurrence counts — a
plain `grep -c` undercounts a line that repeats the value, so these are
`grep -io | wc -l`. Case-insensitivity is load-bearing, not decoration: on
`ddb32c2`, `host-mbp` matched case-sensitively is 3 files / 3 occurrences;
matched case-insensitively it's 7 files / 18 occurrences, because the same
host gets typed in more than one case across commits — a method that doesn't
say `-i` gets a different number than the one below.

A record that states the occurrence count of a value also has to exclude its
*own* mention of that value, or committing the record changes the very figure
it commits — this section's own row is not residue, it's the record talking
about the residue, and a count that doesn't say so cannot be self-consistent.
That exclusion is narrow, though: it removes only this document's own line,
not every deliberate mention elsewhere. The clone-owner warning in
`CLAUDE.md`/`AGENTS.md`, for instance, stays counted — it is exactly the kind
of occurrence this non-goal is a decision about, not evidence about the
record itself. Excluding it too would look more careful and measure the wrong
thing, which is precisely the mistake that produced a 27 for the figure below
where the correct count is 28.

**Home paths.** `/Users/user`, on `ddb32c2`, excluding only this
document's own mention:

| Ref | Files (any type) | Of which `.cs` | Occurrences |
| --- | --- | --- | --- |
| `933ffb9` | 11 | 8 | 28 |
| `808119c` | 12 | 9 | 29 |
| `ddb32c2` | 11 | 8 | 28 |

It has moved by a file and an occurrence between refs, and not in one
direction — ordinary development touching path examples, neither a scrub nor
a regression. That non-monotonic drift is the actual argument for citing the
ref alongside any count at all: a number with no ref attached is already
stale by the time it's read, and three different figures for this exact
residue have circulated for that reason.

Not chased, and not going to be: a home path reveals a local account name, not
a credential — it grants nothing on its own. It appears mostly in path examples
that are useful documentation of real shapes (`workspace-<agent>/outputs/...`
depths, for instance), and the repository's commit metadata already carries the
author's real name and email on every commit, permanently — scrubbing it from
path strings conceals nothing `git log` doesn't already disclose. Chasing it
across a dozen files would churn far more diff than it protects.

**Machine hostnames.** LAN hostnames of a private network, on `ddb32c2`,
excluding only this document's own mention:

| Hostname | Files | Occurrences |
| --- | --- | --- |
| `avatar.internal` | 6 | 9 |
| `host-mbp` | 6 | 17 |
| `host-mac-mini` | 1 | 6 |

Same reasoning as the home paths: a LAN hostname is not a credential, it names
a machine on a private network with no route to it from outside, and it is
load-bearing in examples about peer discovery and multi-machine behaviour —
including the mkcert issuer strings quoted earlier in this file. Left alone for
the same cost/benefit reason.

## CB-168: starting a new conversation — confirmed working

Asked because CB-168 needs to know, before building a picker, whether the
OpenClaw arm of "start a new chat" is possible at all. Measured against the
same live gateway (OpenClaw 2026.9.2 at time of this probe — the `dist/`
filenames below carry that version's hashes, not 2026.7.1-2's), from this
Mac's own already-paired device (`fa8df53b…`, `operator.read` +
`operator.write`, the scope the app already requests when "Allow replying to
agents" is on).

**The probe first had to be made to compile again.** `ClaudeBuddySettings.cs`
grew a reference to `SpeakScope` (the turn-sounds feature, landed after the
Sep 6 fix that added `ChatZoom`/`StatusDirectory` to this same csproj's
compile list). Same shape as that fix: `SpeakScope` is a two-value enum in
`SpeechSummary.cs`, but that file also pulls in `ClaudeBinary` and
`InternalSessions`, so the enum is restated in `Shims.cs` rather than the
whole file compiled in. `tools/openclaw-probe/OpenClawProbe.csproj` and
`Shims.cs` both changed; no other file did.

**Also confirmed, incidentally: this Mac's own terminal can reach the
gateway.** `dotnet run --project tools/openclaw-probe` connected and returned
real data on the first attempt, no `EHOSTUNREACH`, no `--restart` needed for
Local Network — CLAUDE.md's "the terminal lacks the grant" note evidently
doesn't hold universally on this machine at this time; not chased further, it
made everything below reachable from here without ssh.

### There is a dedicated `sessions.create`, and it needs only `operator.write`

Read straight from the gateway's own `dist/method-scopes-*.js` on the gateway
host (`avatar.internal`, ssh alias `mini`) — the declared policy, not a
reimplementation of it:

```js
function resolveSessionsCreateRequiredScope(params) {
  if (!isRecord(params)) return "operator.write";
  if (params.incognito === true || typeof params.key === "string" && isIncognitoSessionKey(params.key)
      || typeof params.parentSessionKey === "string" && isIncognitoSessionKey(params.parentSessionKey)
      || Object.hasOwn(params, "execNode") || Object.hasOwn(params, "toolOverrides")
      || params.permissionMode === "full") return "operator.admin";
  return "operator.write";
}
```

So an ordinary create — no incognito, no `execNode`/`toolOverrides`, no full
permission mode — needs exactly the scope this app already requests when
replying is on. `SessionsCreateParamsSchema` (`src-BiL5aQto.js`) makes `key`
*and* `agentId` both optional, so **`{agentId: "main"}` alone is a valid
call** — this is the "or with `agentId` and no key" half of the plan's
question, and it is the one that matters: no message required, and no
pre-existing session key to invent.

**Verified live, not just read from source**, via `tools/openclaw-probe raw`:

```
$ dotnet run --project tools/openclaw-probe -- raw sessions.create \
    '{"agentId":"main","label":"CB-168 probe — please ignore"}'
{
  "ok": true,
  "key": "agent:main:dashboard:b4a6ca53-9197-4ebe-b811-776b82cc3027",
  "sessionId": "71032889-…",
  "runStarted": false,
  ...
}
```

`sessions.list` (a second, independent call) then returned that exact key —
the positive half of the pair. `runStarted: false` and no message sent means
this created an inert conversation shell, not a turn: exactly the shape
CB-168 needs, since the dialog itself takes no prompt and the first thing the
user does after Start is send a message into an already-existing key.

**Negative control**, on the *same* device and the *same* method — flipping
one param instead of swapping credentials, which is the safer probe (see
below for why a second credential wasn't used):

```
$ dotnet run --project tools/openclaw-probe -- raw sessions.create \
    '{"agentId":"main","incognito":true,"label":"CB-168 negative control — should be refused"}'
OpenClawRequestException: missing scope: operator.admin
```

Refused exactly as the source predicts: this device holds `operator.write`
but not `operator.admin`, and the gateway's dynamic scope check catches the
`incognito` flag and demands the higher scope, live, not just in the
declaration. That is the paired positive/negative this file's own rules ask
for, sourced from one device and one method, which is what makes it a clean
pair rather than "two different things happened to both come out as
expected."

**Cleanup used `sessions.patch{archived:true}` then `sessions.delete
{archivedOnly:true}`, both at `operator.write`** — a plain `sessions.delete`
on a non-archived session demands `operator.admin` (`resolveSessionsDeleteRequiredScope`
returns `operator.admin` unless `archivedOnly` is already true *and* every
other field is in a small write-safe set), so archive-then-delete is the
scope-appropriate cleanup path for a device that can create sessions but
isn't an admin. Verified: the throwaway session no longer appears in
`sessions.list` afterward.

### `chat.send` on a brand-new key also creates the session, no `sessions.create` first

Tested separately, since the plan asked about this path too: `chat.send`
with `sessionKey: "agent:main:dashboard:cb168-freshkey-test"` — a key that
had never been created — succeeded (`{"status":"started"}`) without a prior
`sessions.create` call, and the key then showed up in `sessions.list`. So
either path works: a dedicated create call, or just sending into an unseen
key. **`sessions.create` is still the better fit for this feature**, because
it needs no message (the dialog has no prompt field) and creates an inert
shell rather than kicking off a real agent turn — the `chat.send` route
necessarily starts a run the moment it's called, which is a heavier and
noisier way to get the same "a key now exists" outcome. This throwaway key
was cleaned up the same way (archive, then delete).

**One correction to CLAUDE.md's own worked example for this ticket**, made
honestly rather than silently: it named `agent:<id>:<new-surface>` as the
fresh-key shape to test. The real gateway generates its own key on
`sessions.create` (`agent:main:dashboard:<uuid>` above) rather than accepting
a caller-chosen surface name; a caller-supplied `key` is accepted too (per
the schema) but wasn't the shape exercised here, since the auto-generated
one is what `OpenClawSessions.StartConversation` will actually receive back
and bind to.

### Why no second credential was used for the negative control

The original plan asked for "the same call with Buddy's read-only
credential" refused. This gateway already has such a device paired
(`gateway-client` at IP matching the gateway host itself, `operator.read`
only) — but its private key lives with whatever client paired it, not with
this one, so it can't be impersonated. The alternative — generating a fresh
device identity in a scratch settings directory, using the same gateway
token, and getting it approved on the live gateway to manufacture a
read-only credential — was attempted and stopped: it would have created a
new permanent entry in this production gateway's paired-device table (nine
real agents, real Discord conversations, real cron jobs behind it) purely to
prove a point the source and the params-based negative control above already
prove. The `incognito`-flag control above tests the same enforcement
mechanism (`authorizeOperatorScopesForRequiredScope`, live, against a real
refusal) without that side effect, so it was preferred instead.

### What this settles for CB-168 Phase 2

- **Build it.** `OpenClawSessions.StartConversation(agentId)` should call
  `sessions.create({agentId})`, bind a `ChatPanel` to the returned `key`, and
  let the first user message go through the existing `chat.send` path
  unchanged — nothing about sending into an already-created key is new.
- Needs `operator.write`, exactly as the plan assumed: disabled with "turn on
  Allow replying to agents" when `OpenClawReplyEnabled` is off, "no gateway
  configured" when there isn't one. No new scope request, no new pairing.
- `agents.list` (already used elsewhere in this file) is confirmed as the
  source for the picker's agent list — `defaultId: "main"` on this gateway,
  eight agents total, names distinct from ids (`main` → Lilibeth, `kubernetes`
  → Amber, etc., same shape already documented above).

## A video another agent made plays under that agent's session (CB-277)

CB-272 and CB-274 stopped at a note for a clip the gateway refused ("Not an image"). The refusal is real but it is a property of *which session the request names*, not of the file.

Measured against openclaw 2026.10.5 on the Mac mini (read-only, `curl -sk` against the gateway's `assistant-media` route with `&meta=1`, one real clip that a cron job delivered to a Discord DM; paths abbreviated to their category):

| Request | Answer | What it shows |
| --- | --- | --- |
| the clip, under the **asking** agent's DM session | `available:false, code:unsupported-media-type, reason:"Not an image"` | today's refusal: the file is outside that agent's roots, and only an image gets the outside-roots exception |
| a PNG from the same folder, same session | `available:true, image/png` | control: the image exception is live, so the refusal above is specific to non-images |
| the clip, under the session of the agent **whose workspace holds it** | `available:true, video/mp4, playback:native, durationMs:12750, 544x800` (size equal to the file on disk) | the gateway serves it, with the full meta CB-272 already parses |
| a session key that does not exist | `404` | control: a made-up key is not accepted |
| a third agent's clip under the wrong agent's session / under its own | refused / `available:true` | control: the rule is "the named session's agent's roots", not "any session works" |
| a clip inside the gateway's own media folder, asking session | `available:true` | a file in `<state>/media` serves under any session — not discoverable, though (the copy's name carries a random uuid recorded nowhere a client can read) |

So there is no supported way to fetch such a clip under the asking session and a supported one under its owner's. The gateway publishes what is needed to pick the owner: `agents.list` carries each agent's `workspace`, and `sessions.list` carries the keys. Orbweaver picks the agent whose workspace is the longest directory-prefix of the path (on a `/` boundary), prefers that agent's session in the same conversation, then its `main` session, and only ever names a key present in `sessions.list`. Anything else keeps the asking key, so every route that works today is byte-identical.

**This reverses CB-274's decision not to try another agent's session.** That plan called it "guessing an identity", which was right when the only candidate was a probe. With `workspace` read from `agents.list` the identity is read, not guessed, and the measurement above is what that plan did not have. The app asks about exactly one file, under exactly one session, and never iterates sessions looking for one that says yes.

What the HTTP fetch is authorised as: since CB-278, the app's HTTP media fetches send this device's own operator token, which the gateway scores as `authMethod: device-token` with exactly the device's approved scopes. They fall back to the gateway's shared-secret bearer, which the gateway scores as `authMethod: token` with the CLI-default operator scopes (including admin), only while no device token is held. The one admin-only switch on this route is `explicitAllow` (a POST with `allow=1`), which this app never uses. The folder policy that decides a video does not depend on scope, and the clip above fetched identically under both credentials (see the CB-278 section below).

Risk, stated plainly: this rests on `resolveAssistantMediaPolicy` building roots from the named session's agent. A later gateway release could narrow it; the failure mode is the note CB-274 draws today, not a wrong clip, and the tooltip on that note now says which session was asked. Re-measure before trusting this on a newer release.

A clip whose directory survives nowhere on its own conversation page or in its cron run record is recovered from a sibling conversation's history (one bounded `chat.history` page per sibling, a basename seen under two directories refused) — see `RecoverFromSiblingHistoryAsync`.

## The media fetches present the device's own token, not the shared secret (CB-278)

Until CB-278 the HTTP media fetches (`FetchMediaAsync`, `FetchLocalMediaMetaAsync`, the only two callers of `OpenClawSocket.GetAsync`) sent the gateway's shared secret. Over HTTP the gateway scores a shared-secret bearer as the CLI's default operator scopes, which include admin: `http-auth-utils` returns `[...CLI_DEFAULT_OPERATOR_SCOPES]` for it (admin, read, write, approvals, questions, pairing, talk.secrets). So whatever the WebSocket pairing said, the HTTP path held admin, and a read-scope measurement taken over it could not tell read scope from admin. That is the "I happen to be over-privileged" trap.

Measured by the CB-278 architect against openclaw 2026.10.5 on the Mac mini (`curl -sk` on its own loopback, every credential held in a shell variable and never printed). The credentials were the shared secret (48 characters), the mini's Orbweaver device token (43 characters, approved `operator.read`), and the MacBook's (43 characters, `operator.read, operator.write`). The fixture was a 1.4 MB PNG under `agent:main:main`:

| Request | Shared secret | Device token, read-only | Device token, read+write |
| --- | --- | --- | --- |
| assistant-media `&meta=1`, then the bytes | 200 `available:true`, then 200 and the whole file | identical | identical |
| `GET /__openclaw__/workspace-icon/main` (admin-gated) | **404**: through the auth gate, no icon exists | **403** `owner access required` | **403** `owner access required` |
| `POST …&meta=1&allow=1` (the route's own admin switch) | **200** `available:true` | **403** `Allowing an outside image requires operator.admin` | — |
| reader scopes in the `mediaTicket` the meta answer mints | `authMethod=token`, the CLI-default set above | `authMethod=device-token`, `[operator.read]` | `[operator.read, operator.write]` |
| CB-277's clip under its owner's session | `available:true video/mp4`, then the whole file | identical | — |

The controls: no `Authorization` header gets a 401 on both meta and bytes. A bogus bearer gets a 401. A made-up session key gets a 404 under both credentials. The clip under the asking, non-owner session is refused `Not an image` under both, which is CB-277's refusal and is independent of scope. Twelve back-to-back device-token requests all returned 200, and six wrong bearers from the same IP did not lock the device token out afterwards.

**The admin-gate rows are the paired negative control.** The credential the app sent passes two different admin gates. A device token is refused at both, with the right reason, and fetches a picture and a clip identically.

The device token was always available. hello-ok carries `auth.deviceToken` on every connect of an approved device, and the gateway's `ensureDeviceToken` reuses the same token while the approved scopes hold. `ConnectAsync` read `auth.scopes` and dropped it, and `OpenClawIdentity.SetTokenFor` had no caller, so `TokenFor(host)` was always null in production. It now stores the token, rewriting the 0600 file only when the value changed, and both fetches pick it through `OpenClawMediaCredential.Pick`. The shared secret remains the fallback while no device token is held, which is the old behaviour. It is never a retry after the device token is refused, because a 401 falling through to an admin credential would undo the change. A held token is never cleared when a hello-ok omits one. A stale one fails as a 401, the picture does not load, and the next connect that carries a token replaces it.

The `auth` block of a real hello-ok from 2026.10.5, captured over the app's own transport on 2026-10-23 as a device approved for `operator.read`, has seven fields: `method` (`token`), `role` (`operator`), `scopes`, `deviceToken` (43 characters), `recoveryScope` (also 43 characters, credential-shaped and not the device token), `recoveryMigrationAllowed` and `issuedAtMs`. The app reads `scopes` and `deviceToken` and nothing else from it. The integration test's fixture is that capture, scrubbed: both 43-character values are same-length placeholders, and the snapshot's presence and health, which name other machines and the Discord bot, are emptied.

Once a token is stored, the next connect sends it back as `auth.deviceToken`, which the code always did whenever one was held. Over the app's own transport that was measured as harmless: the connect result was byte-identical, and so was the result with a deliberately wrong device token, because the shared secret is what this gateway authenticates a connect by.

Two alternatives were rejected. A gateway-minted media ticket is derived from the meta call's bearer and carries its scopes, so with the shared secret it is an admin-scoped capability that only takes the bearer off the byte fetch. It would also add a meta round trip to every picture and a five-minute expiry to cached urls. Documenting the shared secret as acceptable cost more to justify than the fix.

Not measured: whether a connect would fall back to the device token if the shared secret were rotated, and any gateway other than 2026.10.5.
