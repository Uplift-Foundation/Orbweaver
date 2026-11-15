# CB-329 — Open an existing OpenClaw conversation from New Chat

Architect: Nadia Okafor. Ticket: CB-329 (Feature, Epic CB-270). The
refinement comment of 2026-11-14 is final and is not reopened here.

## PM rulings (Jennifer Voss, final; folded in below)

1. **Exclusion uses the real predicate.** A row is excluded only by the real
   `OrbClusters.Visible(OrbClusters.Of(heartbeat, kind, native), HeartbeatMode, CronMode, NativeMode)`.
   That covers heartbeat, cron *and* native, and it is not reimplemented.
2. **Rows with an orb are always listed.** That includes pinned rows and
   rows kept for an approval, even when their cluster is Hidden, matching
   `Parse`.
3. **ReplyDisabled.** "Open existing" is enabled read-only and "New
   conversation" is disabled. NoGateway shows today's reason.
4. **Loading.** Until a poll completes the list shows "Loading
   conversations…". "No conversations" appears only after a completed poll
   lists zero rows.
5. **The fresh-activity seam is tested explicitly** (see §6, cases 10–13).
   A reopened stale session survives the next `Parse`, then drops once the
   window lapses. It is not pinned, and nothing is written to settings or
   disk.
   - **Amended (approval):** the one write allowed is removing an existing
     CB-276 hide (amendment 1 below).

## Approval amendments (team lead, final)

1. **Re-opening un-hides.** `Reopen(row)` removes the row's ids from the
   CB-276 hidden set, through the same store that "Hide this orb" and the
   Settings → Hidden orbs restore use. The orb then appears, and the watch
   opens the panel as usual.
   - For a room, every member *and* the room orb id are un-hidden.
   - This is the user's explicit action on existing state, not new persisted
     state.
2. **Height guard.** With a gateway configured, the dialog's fixed height must
   be **≤ 700 dip**, so it fits a 720 px work area.
   - If it doesn't fit, shrink the list (aim for about 6 rows); don't raise
     the bound.
   - The PR body states the measured heights before and after.

**Everything under "Read in code" was read at `origin/develop` = `d8b3aa48`**
(the merge of PR #252) in a dedicated worktree, never the main clone. Anything
not read there is under "Assumed", and engineers should check it before
building on it.

## PM rulings on QA G1 and G2 (team lead, final; supersede the G1/G2 text below where they differ)

### G1: the unreachable state

**Text and timing.**
- Loading lasts at most the start watch's 20 s budget.
- If no poll has completed by then, show the headline **"Can't reach the
  OpenClaw gateway"**.
- When a connect failure is known, show its detail beneath the headline. The
  detail is the text `OpenClawGateway.ExplainConnectFailure` produced, which is
  what carries the macOS Local Network hint.
- Keep polling, so the list replaces the message when the gateway comes back.

**Interface (hana).** `EmptyText(bool loaded, bool timedOut, int total, int shown)`.
The argument is renamed from `gaveUp` to `timedOut`; the order is unchanged.
It has four states:

| loaded | timedOut | result |
| --- | --- | --- |
| false | true | "Can't reach the OpenClaw gateway" |
| false | false | "Loading conversations…" |
| true | — | total==0 → "No conversations"; shown==0 → "No conversations match"; otherwise null |

**Where the detail comes from (hana).** Read at d8b3aa48:
`ExplainConnectFailure`'s text reaches the poll as `ConnectResult.Detail`
(`OpenClawGateway.cs` ~L306), and only as part of `Describe(result)` in
`_state` (`OpenClawSessions.cs` ~L1294). Nothing keeps it on its own.

- Add `private static string? _lastConnectFailure`.
  - Set it to `result.Detail` beside that `Report(Describe(result))`, for a
    non-`Connected` outcome.
  - Clear it to null where `_listingCurrent` is set true.
- Add `internal static string? LastConnectFailure()`, which returns null when
  OpenClaw is off.
- Add the seam `SetLastConnectFailureForTests(string?)`.
- The set site is inside the excluded poll. If you factor it, factor it into a
  covered `RecordConnectFailure(ConnectResult)`. Otherwise name the line in the
  PR body.

**The UI (juno).**
- **Detail line.** A second `TextBlock`, `ExistingEmptyDetail`, sits under the
  empty text. It is visible only in the unreachable state, and only with a
  non-empty `LastConnectFailure()`. It is wrapped and at most 3 lines.
- **Height.** It sits inside the list's fixed-height cell, so CB-207 is
  untouched.
- **Seam.** `LastConnectFailureForTests: Func<string?>?`.
- **Clock.** The timer is driven by `NowForTests` and `OnReopenLoadTick`; no
  real sleeps.

**Tests.**
- **U-G1** (hana): `EmptyText` in all four states, plus each `shown`/`total`
  edge.
- **UI-G1a** (juno): rows null, then a tick at +21 s. The headline shows, and
  the detail shows the seam's failure text.
  - A variant with a null failure shows the headline and hides the detail.
- **UI-G1b** (juno): the control. After that, rows arrive and the next tick
  shows the list. The unreachable text is gone and the timer is stopped.
- Plus the existing control: rows arriving at +5 s never show the unreachable
  text.

### G2: A1 becomes a tested claim (hana owns it)

**U-G2 / UI-13b** moves to WP A and is owned by hana. Run it wherever a real
`SessionManager` can be scanned without a window: UnitTests if it can,
otherwise UiTests.

- **Setup.** Two stale, non-hidden DMs. Publish them through the real `Parse`
  with `withinMinutes: 5`.
- **Act.** `MarkActive` one of them only, then `Parse`, then a real
  `SessionManager` scan.
- **Assert.** `OrbFor` / `StatusFor("openclaw:" + marked)` is non-null.
- **Control.** The unmarked twin yields none.

If `SessionManager` cannot be driven through one scan without a window, **add
the seam in WP A**: a scan entry point that returns the scan's statuses. That
is not a reason to skip the test.

UI 13 (the hidden variant) stays with juno, but it reuses hana's scan seam.

## QA review resolutions (wren-asare, against 43f689d9)

All nine gaps are accepted. G1 and G3 needed a decision, made here. The rest
add cases to §6, numbered as below. Each one is owned by the WP that owns the
code it exercises.

### G1: a dead gateway must not show "Loading" forever

**Decision: (b).**

- **The bound.** The load timer gives up after the same 20 s budget the orb
  watch uses.
- **Giving up.** The list then shows "Can't reach the gateway. Check
  Settings → OpenClaw."
- **Recovery.** The timer **keeps polling** after giving up, so a gateway that
  comes back still fills the list without reopening the dialog.
- **Why not (c)?** The NoGateway reason is for "not configured". A configured
  but unreachable gateway is a different fact, and only (b) can say so.

**Interface change (A).** The empty-state helper gains one argument:
`EmptyText(bool loaded, bool gaveUp, int total, int shown)`.
- `!loaded && gaveUp` → the can't-reach text.
- `!loaded && !gaveUp` → "Loading conversations…".
- Everything else is unchanged.

**Seam (B).** `NowForTests` already drives `OnReopenLoadTick`'s deadline.

**Cases.**
- **U-G1** (unit, A): `EmptyText` with `gaveUp` both ways.
- **UI-G1a:** null rows, tick at +21 s → can't-reach text.
  - Control: rows that arrive at +5 s show the list, and the can't-reach text
    never appears.
- **UI-G1b (recovery):** after giving up, the seam returns rows, then a tick →
  the list shows, then the timer stops.

### G2: A1 measured on the plain path

**UI-13b** (B). A stale, **non-hidden** DM.
- **Act.** Reopen it through the real `Reopen` and run a real `SessionManager`
  scan, using the same scan seam as UI 13.
- **Assert.** `OrbFor("openclaw:" + key)` is non-null.
- **Control.** An un-reopened stale twin has no orb.

This is what turns A1 from an assumption into a measurement. If it fails,
`SessionManager`'s lifetime is a second filter and the plan needs revising.
Tell me before working around it.

### G3: room traps and badge truth through the real Parse

New unit cases (A), all through the real `Parse`, then `ListedSnapshot`, then
`Build`:

- **U-G3a.** The trap key `agent:main:discord:channel:agent:ea-hope:discord:channel:1538…`
  becomes its own Session row. No `openclaw:room:` row exists.
- **U-G3b.** Two DMs with one agent → two rows. Two different channels, each
  with two agents → two Room rows (the control against over-merging).
- **U-G3c.** A WhatsApp group whose `chatType` is "direct" and whose
  `classification` is "group" → `Kind == Channel` on the row, `Badge == "Channel"`.

**Badge decision (deliberate; revised after reviewing 351a95d7).** The
ticket's "main or native" both appear, with distinct meanings.

**Read in code at d8b3aa48.** `OpenClawHeartbeat.Is` returns true for
**every** `agent:<id>:main` key (`OpenClawHeartbeat.cs` ~L61). An agent's main
session is the heartbeat's default target. hana hit this while writing her
WhatsApp case.

So "Heartbeat first" would badge every agent's main session "Heartbeat", and
the "Main" badge would never appear.

The `Session` record's own comment draws the line this needs: *"a heartbeat is
how a session gets woken, not what kind of conversation it is."* The badge
says what kind of conversation a row is. The cluster *filter* (`ClusterHides`)
still uses `Heartbeat` exactly as the scan does, so under Heartbeats = Hidden
an unpinned main is still excluded (ruling 1).

Order:

1. `Native == Dashboard` (a New Chat conversation, with no surface) → "Native"
2. `Kind == Main` (the agent's own session, classification-main included) → "Main"
3. Heartbeat → "Heartbeat" (a cron labelled as the heartbeat beats Cron, as
   in `OrbClusters.Of`)
4. Cron → "Cron"
5. Direct → "DM"
6. Channel → "Channel"
7. Unknown with `Native != None` → "Native"
8. Otherwise ""

This **replaces** §1's order and the first G3 order.

- **U-G3e.** Through the real `Parse`: `agent:nova:main` → `Heartbeat == true`
  **and** `Badge == "Main"`.
  - Under Heartbeats Hidden it is not listed unless pinned. Pinned, it is
    listed with "Main".
  - A cron row labelled "Cron: Heartbeat (main)" → "Heartbeat".

- **U-G3d.** Through the real `Parse`: one dashboard row, one
  classification-main row and one `agent:x:main` row → "Native", "Main" and
  "Main".

### G4: no second sessions.list, and the snapshot's lifecycle

**U-G4a** (A, in UiTests or IntegrationTests where the fake-socket gateway
already lives).
- **Setup.** Build an `OpenClawGateway` with the fake `Connector` pattern in
  `tests/UiTests/ChatPanelApprovalAdversarialTests.cs:48`, counting the frames
  written, and pass it to `SetGatewayForTests`.
- **Act.** `Parse` once. Then call `ListedSnapshot()`, `Build`, and the
  window's open-existing path 5 times.
- **Assert.** Zero frames were written.
- **Control.** `StartConversationAsync` writes one frame through the same
  counter, which proves the counter can count.

**U-G4b.** A second `Parse` with a different payload replaces
`ListedSnapshot()` wholesale.

**U-G4c.** The `Restart` path and the disconnect path each leave
`ListedSnapshot()` null.
- Both clears live in excluded methods. Either call the excluded method with
  no gateway, as the existing CB-276 `_listingCurrent` tests do (find them), or
  factor the clear into one covered `ClearListingLocked()` that both call.
  Prefer the factoring.

### G5: every added line in §2 gets a case

- **U-G5a.** Pin a cluster-hidden row (heartbeat under Hidden) through the
  `_listed` edit in `SetGatewayPinnedAsync`. It becomes listed by `Build`
  before any poll.
  - If the edit sits behind the excluded network await, factor the
    state-change half into a covered helper shared with the `_snapshot` edit.
- **U-G5b.** End removes the key from `ListedSnapshot()` before the next poll.
  Same factoring rule.
- **U-G5c.** Clearing on `Restart` and on disconnect is covered by U-G4c.
- **`RestoreHidden`'s three arms.**
  - The seam arm: unit 16 sets `RestoreHiddenForTests`.
  - The `SessionManager.Instance` arm: UI 13 and 13b. Say so in a comment on
    those tests.
  - The `OrbweaverSettings` arm: unit 15.

### G6: Reopen returning null, and a key that has vanished

- **UI-G6a** (B). `ReopenOpenClawForTests` returns null → "Couldn't open that
  conversation.", and no watch is armed.
  - Control: a non-null result → "Opening <Title>. Waiting for its orb…".
- **U-G6b** (A). Real `Reopen` on a key absent from `_deliveries`, meaning it
  vanished between the poll and the click.
  - It must not throw.
  - The result is a chat with `Delivery == null`, because `ChatFor` reads
    the key from the snapshot and creates a chat for any key; `SetGatewayForTests(null)`
    makes `LoadHistoryAsync` fail quietly.
  - **Open question, checked by the test:** what does that panel show? If it
    is an empty panel with no error, that is acceptable for a key the gateway
    no longer lists. The next poll will not list it, so its orb never appears
    and the watch times out with its existing message.
  - Measure all of this; don't assume it.
- **U-G6c.** `Reopen` with OpenClaw off returns null and writes nothing.

### G7: A2 measured on the real session, not a fake

**U-G7** (A, beside the existing `OpenClawChatSendTests`, using the
counting fake gateway from U-G4a).
- **Act.** Turn `OpenClawReplyEnabled` off. Then `Reopen` returns a real
  `OpenClawChatSession`, and `SendAsync("hi")` is called on it.
- **Assert.** A System turn is added, and zero frames are written. This is the
  gate at `OpenClawChatSession.cs` ~L466, read at d8b3aa48.
- **Room variant.** Same assertions on the `OpenClawRoomChatSession` that
  `Reopen` returns for a room. If the room does **not** gate, that is a
  pre-existing defect: report it to me, don't fix it inside CB-329.
- **Control.** Reply on → one frame written.

UI 4b stays as written for the window half.

### G8: mode visibility follows the CLI

**UI-G8** (B). Two checks:
- With each local CLI selected, the Open existing radio and pane are not
  visible.
- Select OpenClaw, then Open existing, then a local CLI: the Existing pane is
  hidden, the Start label reads "Start", and the height is unchanged.
  Selecting OpenClaw again restores Open existing as it was left.

### G9: sort uses the real activity signal

- **U-G9a** (A, through the real `Parse`). A row with a stale `lastActivityAt`
  and a fresh `updatedAt` sorts by the fresh one, because `Activity` takes the
  max.
- **U-G9b.** `MarkActive` on the oldest row, then `Parse`, then `Build` → that
  row comes first.

## Read in code (at d8b3aa48)

1. **The recency rule** is in `OpenClawSessions.Parse` (`OpenClawSessions.cs`
   ~L1681). A row is dropped when all four hold:
   - `state` is not `generating`/`waiting`,
   - `!OpenClawRetention.KeptRegardlessOfAge(pinned, native)`,
   - `ActiveWithin is { } limit`,
   - `asOf - activity > limit`.

   `activity` is `Activity(s, key)` (~L2023): the later of the row's
   `max(lastActivityAt, updatedAt)` and `LastSeen[key]`. `LastSeen` is the
   private `Dictionary<string, DateTime>` filled by `OnEvent` (~L2114).
2. **The cluster rule** comes after recency. A row is dropped (into
   `_hiddenByCluster`) when
   `!pinned && !OrbClusters.Visible(OrbClusters.Of(heartbeat, kind, native != None), HeartbeatMode, CronMode, NativeMode)`,
   unless it is `waiting` while approving is on. `ClusterMode` is
   `Hidden | WithChats | OwnShape`.
3. **Archived rows** (`archived: true`) are skipped at the top of the loop,
   before anything is recorded (CB-170). `OnEvent` also forgets `LastSeen` for
   an archive arriving as `sessions.changed`.
4. **Pre-filter tables already exist**: `_roomMembers`, `_deliveries`,
   `_natives`, `_runStates`. They are swapped whole under `Gate` at the end of
   `Parse`. There is **no** pre-filter list of `Session` records:
   `new Session(...)` is built only after the recency check.
5. **Room orbs** are made in `SessionManager` (~L2489–2600). A session joins a
   room orb only when `OpenClawSessions.AgentsInRoom(room).Count > 1`.
   `AgentsInRoom` reads `_roomParticipants`, which holds the members that
   *passed* both filters. The room orb id is
   `SessionManager.RoomId(roomKey) = "openclaw:room:" + roomKey`, and its title
   is `SessionManager.RoomTitle(session.Title)` (the text after `" — "`).
   - A room orb's click goes through `RemoteChatFor` →
     `OpenClawSessions.RoomChatFor(sessionId, title, MembersOfRoom(roomKey))`.
     `MembersOfRoom` is the wide, pre-filter list.
   - A single session's click goes through `OpenClawSessions.ChatFor("openclaw:" + key, status.Title)`.
6. **`ChatFor` already handles any listed key.** It reads the delivery from
   `_deliveries` (pre-filter) and fires `LoadHistoryAsync` (`chat.history`).
7. **`LastSeen` survives a reconnect** (`Restart`, ~L1190); `_snapshot` does
   not.
8. **`NewChatWindow`** (995 lines):
   - The OpenClaw row is the fourth radio, tagged `OpenClawTag`. It is enabled
     only when `OpenClawNewChat.AvailabilityFor(...) == Ready`. `ReplyDisabled`
     and `NoGateway` both disable it, with a reason line.
   - The Agent section shares a `ReservedSlot` with Folder. CB-207 rule: the
     window height is fixed before show and must never change after it, so
     every slot holds a ghost of each shape it can contain.
     `NewChatWindowHeightTests` pins this.
   - `StartOpenClawClicked` → `StartConversationAsync` → `StartOpenClawWatch(session)`.
     A 1 s `DispatcherTimer` polls `OrbForSession(session.SessionId)` for 20 s,
     then calls `ChatPanel.OpenFor(orb, session)`.
   - Seams: `OpenClawAvailabilityForTests`, `KnownAgentsForTests`,
     `StartOpenClawConversationForTests`, `OrbForTests`,
     `ArmOpenClawWatchForTests`, `OnWatchTick`.
9. **Existing helpers.** `OpenClawSessionKind.RoomOf(key)` is pure: it returns
   `"<surface>:<id>"` for channels only. `OpenClawSessions.AgentIdOf(key)` and
   `KnownAgents()` (sorted by name) already exist. `OpenClawNativeKind` is
   `None | Main | Dashboard`.

## Assumed (not verified, check before relying on it)

- **A1.** `SessionManager`'s own "keep orbs for" lifetime uses
  `ScanEntry.LastActivity`, which is `session.LastActivity` = `Activity(...)`.
  If so, a marked session passes it too. I did not read the lifetime code. The
  unit test in §3 proves the `Parse` half. A UI test or a manual run should
  confirm the orb itself appears.
- **A2.** A `ChatPanel` bound to an OpenClaw session with the reply switch off
  is read-only. I assumed this from the ticket ("sending still depends on the
  reply switch, as today"); I did not read it in `ChatPanel`.
- **A3.** `sessions.list` (no limit) returns at most 100 rows
  (`DefaultPageSize`, read from the gateway source by CB-276, not live).
  **The picker can therefore list no more than the poll saw.** That is accepted
  here: the ticket forbids an extra `sessions.list` per open, and paging the
  poll is a separate change. Name it in the PR body.

## 1. The pure list builder

New file **`OpenClawReopenList.cs`** (namespace `Orbweaver`). Pure: no window,
no settings read, no statics read. It must not touch `OpenClawSessions`'
tables either, apart from the pure static helpers `AgentIdOf`,
`OpenClawSessionKind.RoomOf` and `SessionManager.RoomTitle`.

```csharp
internal enum OpenClawReopenTarget { Session, Room }

internal sealed record OpenClawReopenRow(
    OpenClawReopenTarget Target,
    string OrbSessionId,               // "openclaw:<key>"  or  "openclaw:room:<roomKey>"
    string Title,                      // what the row shows: RoomTitle(session.Title) — "#general", "wtvamp", "Daily digest"
    string PanelTitle,                 // what ChatFor/RoomChatFor get as displayName: session.Title for a session, Title for a room
    SessionKind Kind,                  // Room rows are SessionKind.Channel
    OpenClawNativeKind Native,
    bool Heartbeat,
    IReadOnlyList<string> AgentIds,    // 1 for a session; ≥2 distinct for a room, most-recent first
    IReadOnlyList<string> MemberKeys,  // gateway keys to mark active: [key], or every member of the room in this list
    DateTime LastActivity);            // Session.LastActivity (= Activity(s,key)); a room's is its members' max

internal static class OpenClawReopenList
{
    // listed = OpenClawSessions.ListedSnapshot(): every non-archived row of the last poll.
    // approveEnabled = OrbweaverSettings.OpenClawApproveEnabled, passed in (ruling 2).
    internal static IReadOnlyList<OpenClawReopenRow> Build(
        IReadOnlyList<OpenClawSessions.Session> listed,
        ClusterMode heartbeats, ClusterMode crons, ClusterMode native,
        bool approveEnabled);

    // The text shown in place of the list, or null when the list has rows to show
    // (ruling 4, QA G1).
    // !loaded && timedOut  → "Can't reach the OpenClaw gateway" (detail: LastConnectFailure())
    // !loaded && !timedOut → "Loading conversations…"
    // total==0           → "No conversations"
    // shown==0           → "No conversations match"
    internal static string? EmptyText(bool loaded, bool timedOut, int total, int shown);

    // agentId null = all agents. search null/blank = no text filter.
    // A room matches an agent filter if any of its AgentIds does.
    // Search is case-insensitive substring over Title, PanelTitle, every agent's name, and Badge(row).
    internal static IReadOnlyList<OpenClawReopenRow> Filter(
        IReadOnlyList<OpenClawReopenRow> rows, string? agentId, string? search,
        Func<string, string> agentName);

    // "DM" | "Channel" | "Main" | "Native" | "Cron" | "Heartbeat" | "" — the badge text.
    internal static string Badge(OpenClawReopenRow row);

    // Approximate, never exact (activity times are unreliable — findings doc):
    // < 1 min or in the future → "just now"; < 60 min → "N min ago"; < 24 h → "N h ago";
    // < 48 h → "yesterday"; < 7 d → "N days ago"; else "MMM d" (invariant culture, local date).
    internal static string Ago(DateTime activityUtc, DateTime nowUtc);
}
```

`Build`'s rules, in order:

1. **Cluster filter (PM rulings 1 and 2).** Drop a session when all of these
   hold:

   ```
   !s.GatewayPinned
   && !(s.State == "waiting" && approveEnabled)
   && !OrbClusters.Visible(OrbClusters.Of(s.Heartbeat, s.Kind, s.Native != OpenClawNativeKind.None),
                           heartbeats, crons, native)
   ```

   - These are the same three conditions as the cluster block in `Parse`, in
     the same order. The predicate is the scan's own one, called rather than
     rewritten.
   - The result: a pinned or approval-kept row is listed under Hidden, because
     it has an orb; every other row the scan would cluster-hide is not.
   - There is no recency check of any kind. That is the whole point of the
     feature.
   - **Keep the two in step.** Factor the three-clause condition into one
     `internal static bool ClusterHides(Session s, ClusterMode hb, ClusterMode cron, ClusterMode native, bool approveEnabled)`.
     Both `Parse` and `Build` call it, so they cannot drift.
   - `Parse` keeps its separate `keptByApproval` bookkeeping; only the
     predicate is shared.
   - This is a refactor of `Parse`, so the existing approval-hidden-bypass
     tests (`OpenClawApprovalHiddenBypassTests`) are its regression net.
2. **Room grouping.** Group the survivors by `OpenClawSessionKind.RoomOf(key)`.
   - **≥ 2 distinct agents** (`AgentIdOf`, case-insensitive, the same rule as
     `OpenClawSessions.Distinct`): one `Room` row.
     - `OrbSessionId = "openclaw:room:" + roomKey`.
     - `Title` and `PanelTitle` = `RoomTitle(most recent member's Title)`.
     - `MemberKeys` = every member key in the group.
     - `AgentIds` = distinct, ordered most-recent first.
     - `LastActivity` = max of the members.
   - **1 agent**: one `Session` row for that agent's most recent session in the
     room. This matches the scan, which draws no room orb for a lone agent.
   - Sessions with `RoomOf == null` become one `Session` row each.
3. **Sort.** `LastActivity` descending, then `OrbSessionId` ordinal. The second
   key keeps the order stable across polls, for the same reason as `Ordered`
   in `Parse`.

`Badge` order: **superseded by the QA G3 badge decision above.**

## 2. Where the unfiltered snapshot lives

In **`OpenClawSessions.cs`**, beside `_natives`, for the same stated reason.

- **The field.** `private static IReadOnlyList<Session> _listed = Array.Empty<Session>();`
- **How `Parse` fills it.** Build the `Session` record **once, before** the
  recency check (move `kind`, `heartbeat`, `pinned` and `new Session(...)` up;
  they read nothing the filter changes). Add it to a local `listed` list. Then
  run the existing filters against the same record, so `result` still gets
  the identical object. Swap `_listed = listed` inside the existing
  `lock (Gate)` block.
- **Behaviour must not change.** `Parse`'s return value must stay identical
  for every existing fixture: `OpenClawSessionsParseTests` and the approval
  tests are the regression net. `StateFor`, `Activity`, `DeliveryFor` and
  `TitleFor` are each called the same number of times per row as today.
- **Clearing it.** Clear `_listed` wherever `_snapshot` is cleared:
  `Restart` (~L1185) and the disconnect path (~L1361). Also mirror the two
  in-place edits:
  - `SetGatewayPinnedAsync` (~L2376) rewrites `GatewayPinned`, which the
    cluster rule reads.
  - `EndConversationAsync` (~L2536) removes the key; without that, a
    conversation the user just ended would still be listed until the next
    poll.

  The `RefreshSnapshotState` writes in `OpenClawSessions.Approvals.cs` need
  nothing, because they only move rows between the filtered views.
- **The accessor.**
  `internal static IReadOnlyList<Session>? ListedSnapshot()`.
  - It returns **null** when no poll has completed on the current connection
    (`!_listingCurrent`), or when OpenClaw is off. Null means "Loading", not
    empty (ruling 4).
  - Otherwise it returns `_listed`. That field is replaced whole and never
    mutated, so the read needs no lock.
  - `_listingCurrent` is already set exactly where a poll publishes, and
    cleared on disconnect and restart (CB-276). It is the right signal, and no
    new flag is needed.
- **Test seam.** `internal static void SetListedForTests(IReadOnlyList<Session> sessions)`,
  in the shape of `SetSnapshotForTests`. Use it together with the existing
  `SetListingCurrentForTests`.

There is no second `sessions.list`. The dialog reads whatever the last 5 s
poll stored.

## 3. The "fresh activity" seam

The scan decides an orb exists from `Activity(s, key)` vs `ActiveWithin`. The
smallest seam is therefore **the one the event stream already uses: stamp
`LastSeen`**. No new state, nothing persisted. It ages out on exactly the
ordinary rule, because `asOf - LastSeen > limit` becomes true again one window
later. It also survives a reconnect, as real activity does.

In `OpenClawSessions.cs`:

```csharp
// CB-329: the picker's "counts as fresh activity". Exactly what an event does
// to LastSeen and nothing else — no Running entry, so the orb is idle, not working.
internal static void MarkActive(IEnumerable<string> keys, DateTime now)
{
    lock (Gate) foreach (var key in keys) LastSeen[key] = now;
}

// CB-329: what choosing a picker row does. Marks the row's members active, then
// returns the panel's session: RoomChatFor for a room (with the WIDE member list,
// MembersOfRoom, as SessionManager.RemoteChatFor does), ChatFor otherwise.
// Null when OpenClaw is off (both callees already answer null then).
public static IRemoteChatSession? Reopen(OpenClawReopenRow row)
{
    // Amendment 1: un-hide before marking, so the scan the restore schedules
    // already sees the session as fresh.
    foreach (var id in ReopenHiddenIds(row)) RestoreHidden(id);
    MarkActive(row.MemberKeys, DateTime.UtcNow);
    return row.Target == OpenClawReopenTarget.Room
        ? RoomChatFor(row.OrbSessionId, row.PanelTitle,
              MembersOfRoom(row.OrbSessionId["openclaw:room:".Length..]))
        : ChatFor(row.OrbSessionId, row.PanelTitle);
}
```

**A room needs every member marked, not one.** The room orb exists only while
`AgentsInRoom > 1` over the members that *passed* recency. Marking only the
most recent member would open the room panel and draw one agent's orb, with
no room orb at all.

**The un-hide ids, pure** (amendment 1):

```csharp
// Session row: ["openclaw:<key>"].
// Room row: "openclaw:room:<roomKey>", then "openclaw:" + each MemberKey.
// The room id is included because HiddenOrbs.Effective hides a member whose
// *lead* is hidden, and a room member's lead is the room orb (SessionManager
// sets status.Lead = RoomId(room)). Un-hiding the members alone would leave
// them hidden through their lead.
internal static IReadOnlyList<string> ReopenHiddenIds(OpenClawReopenRow row);

// Exactly SettingsWindow's restore (SettingsWindow.cs ~L2685, read at d8b3aa48):
// SessionManager.RestoreHiddenOrb when the manager exists (it also schedules a
// scan), otherwise OrbweaverSettings.RestoreHiddenOrb.
// RestoreHiddenOrb returns false and writes nothing for an id that was not
// hidden, so re-opening a non-hidden row still writes nothing.
internal static Action<string>? RestoreHiddenForTests;
private static void RestoreHidden(string id)
{
    if (RestoreHiddenForTests is { } seam) { seam(id); return; }
    if (SessionManager.Instance is { } manager) manager.RestoreHiddenOrb(id);
    else OrbweaverSettings.RestoreHiddenOrb(id);
}
```

**Latency.** The orb appears at the next poll's `Parse` (≤ 5 s) plus the next
`SessionManager` scan. That is inside the watch's existing 20 s budget, so I
deliberately do **not** inject into `_snapshot` synchronously the way
`RefreshSnapshotState` does for approvals.

**What the seam does not do (PM ruling 5, with amendment 1).**

- It does not touch `Running`, so the orb is idle rather than working.
- It does not call `sessions.patch`, so the session is not pinned on the
  gateway.
- It does not write to `OrbweaverSettings`, with one exception: removing a
  CB-276 hide entry that already exists for one of the row's ids. A row with
  no hide entry writes no file at all.
- `LastSeen` is a process-memory dictionary. It survives a reconnect (as real
  activity does) but not an app restart, which matches "ordinary orb, no new
  persisted state".

**Hidden orbs (amendment 1).** These are no longer a limit, because
`Reopen` un-hides them (see above).

## 4. NewChatWindow UI changes

All the changes are in `NewChatWindow.cs`. The availability helper comes in
`OpenClawNewChat.cs`.

**Availability.** `ReplyDisabled` now enables the OpenClaw radio.

- Inside the section, **"New conversation" is disabled** and shows the
  existing `ReasonFor(ReplyDisabled)` text beneath it. **"Open existing" is
  enabled and preselected.** Reading needs only `operator.read`; the panel
  stays read-only (A2).
- `NoGateway` still disables the whole row, as today.
- Add `OpenClawNewChat.CanOpenExisting(availability) => availability != NoGateway`
  (pure) and `CanStartNew(availability) => availability == Ready`.
- The existing tests that assert "ReplyDisabled → row disabled" must be
  updated deliberately, not deleted. Each one's new assertion is that the row
  is enabled and New conversation is disabled.

**Layout of the Agent section** (window stays 420 wide; top to bottom):

```
( ) New conversation   ( ) Open existing          ← RadioButtons, GroupName "openclaw-mode"
[ New pane ]  Agent  [ combo ▾ ]                  ← today's _agentCombo, unchanged
[ Existing pane ]
  [ All agents ▾ ]  [ Search…                 ]   ← ComboBox (~140) + TextBox (Watermark "Search")
  ┌──────────────────────────────────────────┐
  │ (av) #general                    2 h ago │    ← ListBox, fixed Height 220
  │      Aurora, Nova · Channel              │
  │ (av) wtvamp                    yesterday │
  │      Aurora · DM                         │
  │ (av) Nova                     3 days ago │
  │      Nova · Main                         │
  └──────────────────────────────────────────┘
       "No conversations"                          ← centred TextBlock in the same Grid cell,
                                                     shown instead of the list when rows are empty
```

- **Row template** (`FuncDataTemplate<OpenClawReopenRow>`):
  - A 24 px avatar: `OpenClawSessions.AvatarForSession("openclaw:" + firstMemberKey)`
    for a session, or `RoomAvatar(roomKey)` for a room. Fall back to the
    `OrbGlyph` initials chip.
  - The `Title` (SemiBold).
  - A second line, `"<agent names joined ', '> · <Badge>"`, at opacity 0.7.
  - `Ago(LastActivity, now)` right-aligned, at opacity 0.7.
  - Each item's `AutomationProperties.Name` is `"<Title>, <Badge>, <Ago>"`.
- **The agent filter** holds "All agents" (null), then `KnownAgents()`.
- **Empty states.** All three come from the pure `OpenClawReopenList.EmptyText`.
  - **Before the first poll completes** (`ListedSnapshot()` is null), show
    "Loading conversations…".
  - **A completed poll with zero rows** shows "No conversations".
  - **When the filter or search empties a non-empty list**, show
    "No conversations match".
- **When rows are built.** Rows come from `ListedSnapshot` through `Build`,
  given the three current cluster settings and `OpenClawApproveEnabled`.
  - They are built when the dialog opens and each time Open existing is
    selected. They do not update live while the dialog sits open.
  - Filter and search re-run `Filter` on the cached rows, with no rebuild.
  - **While Loading**, a 1 s `DispatcherTimer` (`_reopenLoadTimer`, separate
    from the orb watch) re-asks `ListedSnapshot` until it is non-null, then
    builds and stops.
  - The timer stops in `Forget()`, as `_watchTimer` does.
  - Its tick is a named, `internal` `OnReopenLoadTick`, so a test drives it
    directly without waiting.
- **The Start button** reads **"Open"** in Open existing mode and "Start"
  otherwise. Changing the content changes width, never height.
  - Double-tap on a row, or Enter in the list, does the same as Open.
  - With no row selected, Open sets the status line to "Choose a conversation first."
- **Open** calls `ReopenOpenClawForTests ?? OpenClawSessions.Reopen`.
  - Null result: status "Couldn't open that conversation."
  - Otherwise: status "Opening <Title>. Waiting for its orb…" and then
    **`StartOpenClawWatch(session)`, unchanged**. The watch finds
    `OrbForSession(session.SessionId)`, which is the room id for a room panel,
    and calls `ChatPanel.OpenFor(orb, session)`. The existing timeout text
    still applies.
- **The list height** starts at 220 px but answers to amendment 2's ≤ 700 dip
  bound. Shrink it (aim for about 6 rows) if the dialog doesn't fit.
- **CB-207.** Add an `_existingGhost`: a ghost of the whole Existing pane,
  meaning the radio row, the filter row, a 220 px block and the empty text.
  - Put it in the same `ReservedSlot` as the Folder and Agent ghosts.
  - Also add the mode-radio row to the existing `_agentGhost`, so the New pane
    measures right.
  - Reserve the Existing ghost **only when `CanOpenExisting`** (decided once,
    in `BuildCliList`, the same as the account ghost). A machine with no
    gateway sees a dialog exactly as tall as today.
  - **Cost:** with a gateway configured, every New chat window is about 280 pt
    taller. Same trade-off CB-207 already made; the PR body should say so.
- **New seams**, in the existing style:
  - `ReopenRowsForTests : Func<IReadOnlyList<OpenClawReopenRow>?>?`, which
    replaces the `ListedSnapshot` + `Build` call. Null means Loading.
  - `ReopenOpenClawForTests : Func<OpenClawReopenRow, IRemoteChatSession?>?`
  - `NowForTests : Func<DateTime>?` for the `Ago` text.
  - Internal accessors: `OpenExistingRadio`, `NewConversationRadio`,
    `ExistingList`, `ExistingAgentFilter`, `ExistingSearch`, `ExistingEmpty`,
    `ExistingGhost`.

## 5. Room rows open a room panel

Covered by `Reopen` (§3). The panel's `SessionId` is
`"openclaw:room:<roomKey>"`, the same object `Rooms` caches for a room orb's own
click. When the orb arrives, the watch binds `ChatPanel.OpenFor(roomOrb, room)`,
so a later click on that orb finds the same `OpenClawRoomChatSession`.

## 6. Tests

### Unit: `tests/UnitTests/OpenClawReopenListTests.cs` (A)

Build from hand-made `Session` records, the same shape `SetSnapshotForTests`
takes.

1. `IncludesSessionsTheRecencyFilterDrops`: through the real `Parse`.
   - Use the `OpenClawSessionsParseTests` helper with `withinMinutes: 5`.
     Feed it three rows, one with `updatedAt` two days before `now`.
   - Assert `Parse`'s result lacks the stale row. Assert `ListedSnapshot()`
     contains it. Assert `Build` emits a row for it.
2. `ExcludesArchivedSessions`: `Parse` with an `archived:true` row. It is
   absent from `ListedSnapshot()` and from `Build`.
3. `CollapsesRoomMembersIntoOneRoomRow`:
   - Input: three agents in `agent:*:discord:channel:900…`.
   - Output: one Room row with `OrbSessionId == "openclaw:room:discord:900…"`.
   - `AgentIds` are most-recent first. `MemberKeys` holds all three.
   - `LastActivity` is the max. `Title` is the RoomTitle form.
4. `RoomWithOneAgentIsASessionRow`: also cover two sessions of one agent in
   the same channel, which gives one row for the newer session.
5. Hidden-cluster rule:
   - `HiddenHeartbeatsAreExcluded` and `HiddenCronsAreExcluded`.
   - `WithChatsAndOwnShapeAreListed`, one case per mode.
   - `PinnedHeartbeatIsListedEvenWhenHidden`.
   - `HiddenNativeIsExcluded`.
   - Plus a negative control: the same heartbeat row **is** listed when only
     *crons* are Hidden.
6. `SortsByActivityDescendingThenId`, including a tie.
7. Filter:
   - `FilterByAgentMatchesARoomByAnyMember`.
   - `SearchIsCaseInsensitiveOverTitleAgentAndBadge`.
   - `BlankSearchKeepsEverything`.
8. `AgoBuckets`: each boundary at exactly 59 s, 60 s, 59 min, 60 min, 23 h 59,
   24 h, 47 h 59, 48 h, 6 d 23 h and 7 d. Also a future timestamp.
9. `BadgeForEveryKind`: Heartbeat beats Cron, native Main gives "Native",
   Unknown gives "".
10. **`MarkActiveKeepsAStaleRowForOneWindowOnly`**. This runs the real rule;
    it is not a paraphrase.
    - Set up `Parse` with `withinMinutes: 5`, a row with `updatedAt` = T−2 days,
      and `MarkActive([key], T)`.
    - `Parse(now: T+1 min)` includes the row, with `LastActivity == T`.
    - `Parse(now: T+5 min+1 s)` drops it.
    - The negative control is the same row without `MarkActive` at T+1 min,
      which is dropped.
    - Reset `LastSeen` between cases. Add `ForgetLastSeenForTests()` if no seam
      exists.
11. `MarkActiveDoesNotMakeItWorking`: after `MarkActive`, `StateFor(key)` is
    still "idle".
12. **`ReopenedSessionIsNotPinnedAndWritesNothing`** (PM ruling 5). This
    covers rows with **no** hide entry, which is why the hash check holds
    after amendment 1. Run it
    against a scratch `ORBWEAVER_SETTINGS_DIR`; it belongs in `IntegrationTests`
    if the unit bootstrap has no real dir.
    - **Setup.** Snapshot the directory: every file's name, length and SHA-256.
      Use `SetGatewayForTests(null)`, so any gateway request would have to go
      through a null gateway and could not reach a live one.
    - **Act.** Call `Reopen(row)` for a Session row, then for a Room row. Then
      run `Parse` again with the same payload.
    - **Assert.**
      - The directory hashes are identical.
      - Every re-parsed `Session` still has `GatewayPinned == false` and
        `KeptRegardlessOfAge == false`.
      - `OrbweaverSettings` hidden-orb entries are unchanged.
    - **Negative control.** In the same test, write a settings key through a
      real setter and show that the hash check catches it. That proves the
      check can fail.
13. **`ClusterHidesAgreesWithParse`.** Hand-build every combination: heartbeat
    / cron / native / chat, each with pinned, waiting and approving true or
    false, under every `ClusterMode`.
    - Assert that `Build` keeps a row **iff** `Parse` with
      `withinMinutes: All` keeps it in its snapshot.
    - This is the property behind ruling 2 ("the picker never hides something
      that has an orb") and its converse.
15. **`ReopenUnhidesAHiddenStaleDm`** (amendment 1). Use the real path, with
    no `RestoreHiddenForTests` seam: a scratch settings dir, `SessionManager`
    absent, and `withinMinutes: 5`.
    - **Setup.** Two stale DMs, A and B, are both hidden with
      `OrbweaverSettings.HideOrb`.
    - **Act.** `Reopen(rowA)`, then `Parse`.
    - **Assert.**
      - `"openclaw:" + A` is gone from `HiddenOrbIds()`, and A is in the parsed
        snapshot.
      - **Control:** B is still in `HiddenOrbIds()`, and still out of the
        snapshot.
    - **Orb appears.** Covered in UiTests (UI case 13), where a
      `SessionManager` can be scanned.
16. `ReopenHiddenIdsForRoomIncludesTheRoomAndEveryMember`: the session-row and
    room-row shapes. Plus `ReopenUnhidesAHiddenRoomLead`: hide only the room
    id; after `Reopen`, the room id and all members are absent from the
    effective hidden set (`HiddenOrbs.Effective` with the room as lead).
14. `EmptyTextStates`:
    - `(loaded:false, …)` gives "Loading conversations…".
    - `(true, 0, 0)` gives "No conversations".
    - `(true, 5, 0)` gives "No conversations match".
    - `(true, 5, 2)` gives null.

### UI: `tests/UiTests/NewChatWindowReopenTests.cs` (B)

Join `[Collection("Settings")]` and drive everything through the seams.

1. `OpenExistingShownWhenReady`: both radios are present, and New is selected
   by default.
2. `ReplyDisabledOffersOnlyOpenExisting`: the OpenClaw row is enabled, New is
   disabled with the reason text, and Existing is selected. Also update the
   existing ReplyDisabled assertions in `NewChatWindowTests`.
3. `NoGatewayStillDisablesTheRow`: the negative control.
4. `EmptyGatewayShowsNoConversations`: `ReopenRowsForTests = () => []`. The
   empty text is visible with "No conversations", and the list is hidden.
4a. `BeforeFirstPollShowsLoading`:
    - `ReopenRowsForTests = () => null` shows "Loading conversations…", and
      the text is not "No conversations".
    - Switch the seam to return rows, then call `OnReopenLoadTick`. The list
      appears and the timer stops.
4b. `ReplyDisabledOpenExistingLoadsHistoryReadOnly`: with `ReplyDisabled`,
    open a row through `ReopenOpenClawForTests`, which returns a
    `FakeChatSession`. Then bind it.
    - The panel shows history.
    - Its send affordance is gated the same way it is for an orb opened under
      ReplyDisabled today. Assert on the same control the existing
      reply-switch panel test asserts on; find that test, don't invent one.
5. `RowsShowTitleAgentBadgeAndAgo`: use `NowForTests`.
6. `AgentFilterNarrows`, `SearchNarrows`, `NoMatchShowsNoConversationsMatch`.
7. `OpenWithoutSelectionAsksForOne`.
8. **`OpeningARowBindsAChatPanelToThatKey`**:
   - `ReopenOpenClawForTests` returns a `FakeChatSession` with
     `SessionId = "openclaw:agent:main:discord:direct:1"`.
   - Click Open and assert the seam got that row.
   - Set `OrbForTests` to return a real `OrbWindow` for that id, then call
     `OnWatchTick`.
   - Assert that a `ChatPanel` is open for the orb and bound to that session.
     Follow the existing `NewChatWindowTests` OpenClaw watch test.
9. `OpeningARoomRowWatchesTheRoomOrb`: the watch is keyed on
   `openclaw:room:…`.
10. `DoubleTapOpens`.
11. `ReopenThroughTheRealPathMarksAndBinds`. No seam: OpenClaw is enabled in
    settings and `SetListedForTests` is used.
    - `OpenClawSessions.Reopen(row)` returns an `OpenClawChatSession` whose
      `SessionId == "openclaw:" + key`.
    - The key passes `Parse`'s recency rule at `now`.
    - The room variant returns an `OpenClawRoomChatSession` with all members.
13. **`ReopenedHiddenOrbAppears`** (amendment 1). Use whatever `SessionManager`
    seam the existing HiddenOrbs UI tests scan with; find it, don't invent
    one.
    - **Setup.** A stale DM is hidden.
    - **Act.** Reopen through the real `OpenClawSessions.Reopen`.
    - **Assert.** After the scan, `OrbFor("openclaw:" + key)` is non-null and
      the id is no longer hidden.
    - **Control:** an un-reopened hidden twin has no orb.
14. **`DialogFitsA720WorkArea`** (amendment 2).
    - **Setup.** `Ready`, with the Existing ghost reserved.
    - **Assert.** The measured fixed height is ≤ 700 dip.
    - If it fails, shrink the list's `Height` (about 6 rows), never the bound.
    - Report the before (develop) and after heights in the PR body, measured
      on both rids if CI prints them.
12. `NewChatWindowHeightTests`: extend it. The height stays constant across
    every combination of local CLI ↔ OpenClaw ↔ New ↔ Existing ↔ empty ↔
    filled. With `NoGateway`, the height equals today's.

### Screenshot: `tests/UiScreenshots/NewChatWindowScreenshots.cs` (B)

`NewChatOpenExistingPicker` (plus `NewChatOpenExistingLoading`, a cheap third capture):

- **Setup.** `Ready`, `KnownAgentsForTests` with two agents, and
  `ReopenRowsForTests` returning:
  - a **DM** row (Aurora, wtvamp, 2 h ago);
  - a **channel room** row (#general, Aurora + Nova, yesterday);
  - a **Main** row (Nova, 3 days ago).
- **Then.** Open existing is selected, with a fixed `NowForTests`.
- **Rids.** It is captured on both rids by CI as usual. A second capture,
  `NewChatOpenExistingEmpty`, is cheap and worth having.

### Coverage

Hold the branch's added lines to 100% (`./tools/coverage.sh --base origin/develop`,
check that it says `merged 4`). The only expected exclusions are the existing
`[ExcludeFromCodeCoverage]` OS halves; no new exclusions are planned.

## 7. Files touched

| File | WP | Change |
| --- | --- | --- |
| `OpenClawReopenList.cs` (new) | A | record, `Build`, `Filter`, `Badge`, `Ago` |
| `OpenClawSessions.cs` | A | `_listed` in `Parse` + clears, `ListedSnapshot`, `SetListedForTests`, `MarkActive`, `Reopen`, `ForgetLastSeenForTests` if needed |
| `tests/UnitTests/OpenClawReopenListTests.cs` (new) | A | §6 unit cases |
| `tests/UnitTests/OpenClawSessionsParseTests.cs` | A | `ListedSnapshot` parity assertions if convenient |
| `OpenClawNewChat.cs` | B | `CanOpenExisting`, `CanStartNew` (+ unit cases in `OpenClawNewChatAvailabilityTests.cs`) |
| `NewChatWindow.cs` | B | §4 |
| `tests/UiTests/NewChatWindowReopenTests.cs` (new) | B | §6 UI cases |
| `tests/UiTests/NewChatWindowTests.cs`, `NewChatWindowHeightTests.cs` | B | ReplyDisabled update, height matrix |
| `tests/UiScreenshots/NewChatWindowScreenshots.cs` | B | two captures |
| `README.md` | B | one sentence under New chat / OpenClaw |
| `.github/workflows/ci.yml` | — | **no change**; don't move the UiTests floor |

Nothing touches the hook scripts, installers or packaging. Parity check
(CLAUDE.md): `build-macos-dmg.sh`, `build-macos-app.sh`, `Orbweaver.iss` and
`install-hooks.*` need no change, because no new file is shipped outside the
assembly.

## 8. Work packages

The work splits cleanly. **A owns everything below the window, and B owns the
window.** The interface between them is §1's record plus the three `OpenClawSessions` members:

```csharp
OpenClawReopenRow / OpenClawReopenTarget                      // §1, exactly as written
OpenClawReopenList.Build(listed, heartbeats, crons, native, approveEnabled)  // §1
OpenClawReopenList.Filter(rows, agentId, search, agentName)    // §1
OpenClawReopenList.Badge(row) / Ago(activityUtc, nowUtc)       // §1
OpenClawReopenList.EmptyText(loaded, timedOut, total, shown) : string?  // §1, ruling 4, PM G1
OpenClawSessions.ListedSnapshot() : IReadOnlyList<Session>?    // §2 — null = Loading
OpenClawSessions.Reopen(row) : IRemoteChatSession?             // §3
```

- **A (core).** The very first step is to push a commit with the record, the
  enum and every signature above, with bodies that compile (they may return
  empty or throw `NotImplementedException`). B then builds against real types
  from the start. After that come `Parse`'s `_listed`, `MarkActive`/`Reopen`,
  and the unit suite.
- **B (UI).** Builds `NewChatWindow` against the interface. It uses the
  `ReopenRowsForTests` and `ReopenOpenClawForTests` seams, so none of B's
  tests waits on A's bodies. The only exceptions are UI case 11 and the real
  `Badge`/`Ago` text, which need A's bodies before they go green.
- **Who calls what.** B calls `Build` with
  `OrbweaverSettings.OpenClawHeartbeatMode/CronMode/NativeMode` and
  `OpenClawApproveEnabled`. A must not read settings inside `Build`.
- **A also owns `ClusterHides`**, the predicate shared with `Parse`, plus
  `ReopenHiddenIds`, `RestoreHidden` (amendment 1) and unit cases 10–16.
- **B owns** UI cases 13 and 14 (the hidden orb appears; the dialog fits a
  720 px work area).

## 9. Windows vs macOS

No platform-specific code. `NewChatWindow` is one Avalonia window on both
platforms, the builder is pure, and `MacOSActivation` is already a no-op off
macOS.

Three things to check on both rids:

- **Fonts differ.** The CB-207 height test and both screenshots must be
  checked on `win-x64` as well as `osx-arm64`. A ghost that measures shorter
  under Segoe UI than under SF would shift the window on Windows only.
- **The `TextBox` watermark** renders through the Fluent template on both
  platforms. Check it in the Windows capture.
- **Enter** in the list must not also trigger a default-button press on
  Windows. The window has no `IsDefault` button today; keep it that way.

Install on the Windows box and open a real picker against the gateway. CI
proves nothing about the real list there.
