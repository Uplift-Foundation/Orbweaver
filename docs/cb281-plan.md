# CB-281 plan: auto-group orbs by attribute

Architect: Idris Belanger. Read against `origin/develop` at `9093041de` in a detached worktree, never the main clone. Nothing here was built or run: every "confirmed" below means "read in the source at that sha", and anything I did not read is marked assumed. The ticket's decisions (one attribute, Off by default, explicit snap beats auto, predefined beats auto, manual groups never gain members, live regroup, both platforms) are settled and not reopened.

## 1. What exists today (confirmed by reading)

- **Group of an orb** is decided in one place: `SessionManager.GroupIdOf(tree, id)` → `OrbGroupMenu.ContextFor(root, …)` → `OrbGroups.GroupIdFor(membership, cluster, hb, cron, native)`. Explicit membership wins, then a CB-14 cluster in `OwnShape` mode, else `main`. It is always resolved from the team **root** (T1).
- **The table** (`OrbGroupTable`, owned by `OrbweaverSettings`, JSON key `orbGroups { groups, members }`) holds every group's name, shape, anchor and style. Membership is a separate `PositionKey → group id` map. A non-predefined group is pruned when its last explicit member leaves (`PruneIfEmpty`).
- **Who is laid out:** `SessionManager.IsUserGroup(id)` is `!OrbGroups.IsPredefined(id)`. Any non-predefined group is live in both free and arranged mode, laid out at its own anchor with its own backdrop and caption (CB-268 F1–F4, CB-282). Predefined groups are laid out only while arranged.
- **Slots** are positions in `OrbweaverSettings.OrbGroupList()` (predefined four, then the rest by ordinal id). `LayoutSubset` builds its own `slotOf` map per call, so a new id prefix needs no slot bookkeeping.
- **Settings UI** already has a "Group outlines" card (`SettingsWindow.GroupOutlineRows`) and a combo pattern (`ClusterModePicker`).

### Where each attribute value is available per orb

| Attribute | Source today | Sessions with no value |
| --- | --- | --- |
| **CLI** | `SessionStatus.Source` (`ClaudeCode`, `Codex`, `Grok`, `OpenClaw`, `ClaudeCloud`), and `RemoteCli` (`claude`/`codex`/`grok`, `MirrorProtocol`) for `RemoteControl`. | None. Always known. |
| **Directory** | `SessionStatus.Cwd` via `SessionManager.DirectoryKeyFor`. Hook-written for the three local CLIs. | OpenClaw, RemoteControl and ClaudeCloud: `Cwd` is empty, deliberately (the scan code says so). |
| **Claude profile** | `ClaudeConfigRoots.RootOfTranscript(status.TranscriptPath)` (pure, already used by `LocalCliChatSession` and `RemoteMirrorServer`) for local Claude Code. For a cloud session the row's `OwnerRoot` (`ClaudeCloudSessions`), which is **not on `SessionStatus` today**. | Codex, Grok, OpenClaw, RemoteControl (not Claude profiles). A Claude session whose hook predates `transcript_path`. |
| **Account** | Not held per orb anywhere. Derivable for Claude from the config root: `UsageAccounts.AccountFilePath(home, configDir)` + `UsageAccounts.EmailFrom/LabelFrom` (a disk read of `.claude.json`). | OpenClaw, RemoteControl. Codex and Grok: see below. |

Said plainly:

- **Repo root is not available.** Nothing in the app resolves a cwd to its git root. Directory grouping in this plan is by cwd, with one pure fold (a Claude Code worktree path `<repo>/.claude/worktrees/<name>` groups with `<repo>`). Two sessions in different subdirectories of one repo get two groups. A real repo fold needs an upward `.git` walk behind a cached seam; it is a follow-on, see Risks.
- **Account for Codex and Grok is assumed, not confirmed.** `CodexUsageAccounts`/`GrokUsageAccounts` can label a home directory, but I did not confirm that a Codex or Grok session's `TranscriptPath` reliably yields its `CODEX_HOME`/`GROK_HOME`. Plan: the engineer checks `docs/codex-findings.md` and `docs/grok-findings.md`; where the home can be read purely off the path, those sessions get an account, otherwise they classify as unknown. The PR says which it was.

## 2. Model

Auto groups are **ordinary entries in `OrbGroupTable`** with a derived id, and **membership that is computed, never stored**. That is the same shape as the predefined clusters (a slot the classifier fills), and it reuses anchors, shapes, styles, captions, backdrops, snapping and the layout without a second code path.

### Ids

`a-<attr>-<12 lowercase hex>`, where `<attr>` is `dir`, `cli`, `profile` or `account` and the hex is the first 6 bytes of SHA-256 over the value's normalised key. The id is a function of the value alone, so it is the same next launch and after any number of setting toggles. That is what keeps a group's anchor, shape, style and rename.

`a-` sorts before `g-`, so auto groups take the slots after the predefined four. Nothing indexes user groups by a fixed slot number (confirmed in `LayoutSubset`).

### The classifier (pure, new file `OrbAutoGroups.cs`)

```csharp
internal enum AutoGroupBy { Off, Directory, Cli, Profile, Account }

// Everything the classifier may look at, gathered by the caller. No disk, no settings.
internal readonly record struct AutoGroupFacts(
    SessionSource Source, string RemoteCli, string Cwd,
    string? ConfigRoot,      // Claude config root: RootOfTranscript, or a cloud row's OwnerRoot
    string? AccountKey,      // stable identity, e.g. "claude\nowner@example.org"; null = unknown
    string? AccountLabel);   // what to show, e.g. the email's local part

internal readonly record struct AutoGroupValue(string Key, string Name);

internal static class OrbAutoGroups
{
    // null = this orb has no value for this attribute (or by == Off): not auto-grouped.
    internal static AutoGroupValue? Classify(AutoGroupBy by, AutoGroupFacts facts, string home);

    internal static string IdFor(AutoGroupBy by, string key);       // "a-dir-1a2b3c4d5e6f"
    internal static bool IsAuto(string? id);                        // starts with "a-"
    internal static AutoGroupBy? AttributeOf(string? id);           // from the id's middle segment

    // settings wire format, like OrbClusters.Name/Parse: unknown text reads as Off
    internal static string Name(AutoGroupBy by);                    // "off" "directory" "cli" "profile" "account"
    internal static AutoGroupBy Parse(string? value);
}
```

Per attribute:

- **Cli** — key `claude`/`codex`/`grok`/`openclaw`; name `Claude`, `Codex`, `Grok`, `OpenClaw` (reuse `CliMark` names). `ClaudeCloud` is `claude`. `RemoteControl` uses `RemoteCli`; an unrecognised `RemoteCli` is `claude` (the scan already defaults it to that).
- **Directory** — key: `Cwd` with trailing separators trimmed, the worktree fold applied, separators unified and lower-cased (the membership map is already `OrdinalIgnoreCase` for the same reason). Name: the leaf folder. Empty `Cwd` → null.
- **Profile** — Claude only (`ClaudeCode`, `ClaudeCloud`). Key: `ConfigRoot` normalised as above. Name: `default` when the root is `<home>/.claude`, else `UsageAccounts.FallbackLabel(root)` (`.claude-board` → `board`). Null root or a non-Claude source → null.
- **Account** — key `AccountKey`, name `AccountLabel`. Null → null. The full email only ever goes into the hash; the name is the local part, the same thing the usage orbs already show.

### Precedence (exact, resolved from the team root)

1. **Explicit membership** of the root (its key, then the Claude-only bare-directory fallback), naming a group that still exists. Any group, `main` included.
2. **Predefined cluster in `OwnShape`**: heartbeats, crons, native, by `OrbClusters.GroupOf`.
3. **Auto group**: `IdFor(by, value.Key)` when the setting is not Off and `Classify` gave a value.
4. **`main`.**

A heartbeat or cron in `WithChats` mode falls through 2 and is auto-grouped like any chat, which is what "with the chats" means. Members always follow their root (T1), so a teammate in a worktree sits in its lead's group whatever its own cwd says.

`GroupIdFor` gains one **required** parameter (required for the reason `ModeOf`'s native mode is: the compiler finds every caller):

```csharp
internal static string GroupIdFor(string? membership, OrbCluster cluster,
    ClusterMode heartbeats, ClusterMode crons, ClusterMode native, string? autoGroupId);

// OrbGroupMenu.ContextFor / Resolve gain: Func<SessionStatus, string?> autoGroupOf
```

**Manual groups never gain members**: the classifier can only ever emit an `a-` id, and a `g-` id is reachable only through step 1. (CB-268's T3 still gives a late *teammate* its root's manual group; that is team membership, not attribute matching, and is unchanged.) One nuance for the PM, not a question: a new session with the same directory and title as an orb that was snapped has the same `PositionKey`, so CB-268 treats it as the same orb and it returns to the manual group. That is CB-268's restart behaviour and this plan does not touch it.

### Table changes (`OrbGroupTable`)

```csharp
// Creates the group if missing (name, Main's shape, no anchor). Never renames or reshapes an existing one.
// When creating would exceed MaxAutoGroups (64), first deletes dormant auto groups
// (no explicit member, id not in `live`), ordinal id order.
internal bool EnsureAuto(string id, string name, IReadOnlySet<string> live);
```

- `PruneIfEmpty` skips `a-` ids: an auto group has no explicit members by design, and pruning it would throw away its anchor.
- `Delete`, `Rename`, `SetShape`, `SetStyle`, `SetAnchor` work on auto groups unchanged. A rename sticks, because `EnsureAuto` never overwrites.
- Name collision on create (two directories with the same leaf): the second is created as `parent/leaf`.
- `OrbweaverSettings.EnsureAutoOrbGroups(IReadOnlyList<(string Id, string Name)> wanted)` wraps it in the lock and one `Save` (only when something was created).

### Setting

- JSON key **`autoGroupBy`**, string, one of `off` `directory` `cli` `profile` `account`. Absent or unrecognised reads as `off`, so existing users see no change. Added to `KnownKeys`.
- `public static AutoGroupBy AutoGroupBy { get; set; }` on `OrbweaverSettings`, immediate `Save` (one deliberate change, like the cluster modes).
- Downgrade: an older build keeps `autoGroupBy` through `_unknownKeys` and reads `a-…` entries as memberless user groups it never draws. Harmless; assumed from reading `ReadJson`, pinned by an integration test.

### Shape and anchor persistence

- Kept in the table under the stable id, saved by the existing CB-211 path (`PlaceGroups` → `SetOrbGroupAnchor`) the first time the group is drawn.
- **Off, or a switch to another attribute, deletes nothing.** The old attribute's groups stay in the table, dormant, and come back where they were when the setting returns. The cap of 64 bounds the file (directories are the only attribute that can grow).
- A dormant auto group that someone hand-snapped an orb into still has that explicit member, so it stays on screen as what it now is: a hand-made group. That follows from step 1 with no extra code.

## 3. SessionManager mechanics

- **Facts**: `AutoGroupFactsFor(SessionStatus)` builds the record. `ConfigRoot` is `RootOfTranscript(TranscriptPath)`, or `OwnerRoot` for a cloud id (add a `[JsonIgnore] string ConfigRoot` to `SessionStatus`, filled where the cloud status is built, so nothing looks a row up later). Account comes from a seam, `Func<string? configRoot, SessionSource, (string Key, string Label)?> _accountOf`, defaulting to a small cached reader (`AutoGroupAccounts`, 30 s hold like `ClaudeConfigRoots.DiscoveredCached`, clock and file-read injectable) over `UsageAccounts.AccountFilePath/EmailFrom/LabelFrom`. Tests inject a dictionary.
- **`GroupIdOf`** passes `status => OrbAutoGroups.Classify(by, FactsFor(status), home)` mapped to an id. `GroupMenuContextFor` passes the same function, so menu, drag and layout agree.
- **`SyncOrbGroups`** (every scan) gains a fourth step: for each on-screen root with a value, `EnsureAutoOrbGroups`. It also runs a pure `OrbGroupSync.GroupTracker.Observe(id → group id)` (same shape as `KeyTracker`); when any orb's resolved group changed since the last scan (a cwd that moved, an account that logged in), it requests a regroup. Arranged mode needs this: `AbsorbIntoArrangement` returns early when no orb arrived.
- **`public void ReapplyAutoGroups()`**, called by the Settings row: ensure the groups, release orbs that just became loose, then settle with the existing spring (`SettleAfterDrop` with no arrivals: re-lays the current mode and glides only orbs whose slot changed). No restart, both modes.
- **Becoming loose (setting → Off, free mode)**: an orb leaving an auto group goes back to its saved pin (`OrbPositionFor` its key) if it has one, otherwise unpins into the stack. I did not read how an orb leaving a deleted user group is handled today; the engineer reads `DeleteOrbGroup`'s path first and reuses it if it already does this.
- **`IsUserGroup` stays as is.** Auto groups are non-predefined, so they are live in free mode. This is deliberate: free mode is the default, and a setting that only showed while arranged is the CB-268 F1 bug again.
- **Dragging out of an auto group in free mode** (`LeaveToFree`): today that is `UnassignOrbGroup`, which would do nothing and let the classifier pull the orb straight back. In `OrbDragCommit`, when the group left is an auto group the team is instead assigned to `main` explicitly (the same write as the menu's "Leave group"). The orb stays loose where it was dropped. That is "explicit beats auto" applied to the gesture.
- **Dropping an orb onto the auto group it classifies into** clears its explicit membership (`UnassignOrbGroup` for the team keys) rather than writing one, so it is automatic again. Dropping onto any other group writes membership as today.
- **Unsnapping from a manual group** (`LeaveToFree`, unchanged): membership is removed, the classifier decides, and the orb glides to its auto group. With auto-grouping on, an orb with a value is never loose unless it was explicitly put in `main`.

### Naming and display

The group's `Name` is the value's name, shown by CB-282's caption when "Show group names" is on and in the orb's Group menu ("In: board"). No new drawing. The Settings help text tells the user to turn on outlines/names to see the groups, since both are off by default.

### Edge cases

| Case | Behaviour |
| --- | --- |
| Value unknown | Not auto-grouped: falls to `main` (loose in free mode). No catch-all "Other" group, which would draw one outline around unrelated orbs. |
| Value changes mid-session | Next scan's `GroupTracker` sees the new id and regroups with a glide; the new group is ensured first. |
| Snapped orb, then unsnapped | Goes to its auto group. |
| Many distinct values | One group each, singletons included (a lone orb's outline is a circle). Layout is CB-268's best-effort `PlaceClear`/`SeparateGroups`; on a crowded screen groups may overlap, the known F-mode limit. Table capped at 64 dormant-evicting auto groups. |
| Team across values | Whole team in the root's group (T1). |
| Hidden cluster | Unchanged; visibility is decided before grouping. |
| Hash collision | Two values would share one group. 48 bits over at most 64 groups; accepted, named in the PR. |

## 4. Settings UI

One row at the top of the existing "Group outlines" card (`SettingsWindow.GroupOutlineRows`), built like `ClusterModePicker`:

- Label **"Auto-group orbs by"**, a `ComboBox` with `Off`, `Directory`, `CLI`, `Claude profile`, `Account`.
- `internal Control AutoGroupPicker()` and `internal void OnAutoGroupByChanged(AutoGroupBy by)` (sets the setting, calls `SessionManager.Instance?.ReapplyAutoGroups()`), both `internal` so a test drives them directly.
- Help text: what each choice groups by, that a group you snap by hand is never changed, that orbs with no value stay ungrouped, and that outlines and names are the switches below.
- `SettingsFilter` search terms for the row.

## 5. Tests (100% of added lines)

**Unit (`tests/UnitTests`)**
- `OrbAutoGroupsTests.cs` — `Classify` per attribute × every source, including missing/unknown (empty cwd, null root, non-Claude profile, null account, Off); worktree fold; case and separator normalisation; `IdFor` stability and distinctness; `IsAuto`/`AttributeOf`; `Name`/`Parse` round trip and junk → Off.
- `OrbGroupsTests.cs` (extend) — `GroupIdFor` precedence: explicit beats auto, explicit `main` beats auto, each `OwnShape` cluster beats auto, `WithChats` cluster takes auto, null auto → `main`.
- `OrbGroupTableAutoTests.cs` — `EnsureAuto` create/idempotent/no rename, name collision, cap eviction (dormant only, never one with a member, never one in `live`), `PruneIfEmpty` exemption, JSON round trip.
- `OrbGroupMenuTests.cs` (extend) — `ContextFor` with an `autoGroupOf`, root-resolved for a team.
- `OrbGroupSyncTests.cs` (extend) — `GroupTracker`.
- `AutoGroupAccountsTests.cs` — the cached reader: hit, miss, expiry, unreadable file.
- `OrbDragCommitTests` (existing file for `Release`; extend) — `LeaveToFree` from an auto group writes `main`; join onto own auto group unassigns.

**Integration (`tests/IntegrationTests`)**
- `AutoGroupSettingsRoundTripTests.cs` — `autoGroupBy` through a real file, default Off when absent, junk value, `a-` groups and anchors surviving a save/load, unknown-key survival.

**UI (`tests/UiTests`, `[Collection("Settings")]`)**
- `SettingsAutoGroupRowTests.cs` — the picker shows the saved value, selecting each item writes the setting.
- `SessionManagerAutoGroupTests.cs` — live regroup with hand-built statuses in each mode: Off → CLI forms one group per CLI; → Directory regroups; → Off returns orbs to stack/pin; a manual `g-` group and its members are byte-identical before and after each change; a new matching session lands in the auto group, not the manual one; a snapped orb stays out; predefined `OwnShape` heartbeat stays put; mid-session cwd change regroups; anchors survive Off → on.

**Screenshots (`tests/UiScreenshots`)**
- `AutoGroupScreenshots.cs` — orbs auto-grouped by CLI (three groups, outlines and names on) next to one hand-made group; and the Settings card with the new row.

The console suites are not extended: no geometry, transcript or glyph code changes. Run `tests/UiTests` in Release with CB-119's guard before pushing.

## 6. Install and packaging

No touchpoints. No hook change, no new asset, no new file outside the app assembly, nothing platform-specific in the code path (paths are normalised for both separators and case). Checked by reasoning, not by installing: `build-macos-*.sh`, `Orbweaver.iss` and the hook installers copy nothing this adds. The README's settings section gets the new row. The three-machine install before CI still applies, and Windows is where the path normalisation gets its real test.

## 7. Work streams (three engineers, parallel after one contracts commit)

**Step 0 — contracts commit (E1, first, small):** `OrbAutoGroups.cs` with the enum, records and signatures (bodies throwing), `GroupIdFor`/`ContextFor` new parameters with callers passing null, `OrbweaverSettings.AutoGroupBy` and `EnsureAutoOrbGroups` stubs, `SessionManager.ReapplyAutoGroups()` empty. Everything compiles; the other two branch from it.

| | Owns | Builds against |
| --- | --- | --- |
| **E1 — model and settings** | `OrbAutoGroups.cs`, `OrbGroups.cs`, `OrbGroupTable.cs`, `OrbweaverSettings.cs`, `AutoGroupAccounts.cs`; unit tests for those; the integration test | nothing |
| **E2 — live grouping** | `SessionManager.cs`, `OrbGroupMenu.cs`, `OrbGroupSync.cs`, `OrbDragCommit.cs`, the `SessionStatus.ConfigRoot` field; `SessionManagerAutoGroupTests`, drag and tracker tests | E1's signatures in §2 |
| **E3 — Settings UI, capture, docs** | `SettingsWindow.cs`, `SettingsFilter.cs`, `README.md`; `SettingsAutoGroupRowTests`, `AutoGroupScreenshots` | `OrbweaverSettings.AutoGroupBy`, `SessionManager.ReapplyAutoGroups()` |

Contracts between them are the signatures in §2–§4. A change to one goes to the architect and the other owner before it is pushed. Only QA runs full suites; engineers run their own classes with `--filter-class`.

Suggested models: implement on `sonnet` (E1, E3) and `opus` or `sonnet` for E2, which carries the `SessionManager` risk.

## 8. Risks and open points

1. **Directory means cwd, not repo.** Recommendation: ship cwd plus the worktree fold, and say so in the help text. A git-root fold is a disk walk per new cwd behind a cached seam; small, but it is new behaviour worth its own tests. If the requester's "repo" was literal, add it to E1 now rather than later.
2. **Auto groups are live in free mode**, so turning the setting on pulls every orb with a value out of the stack column and out of its hand-dragged pin, into group shapes. That is what the feature is, and Off restores it, but it is the most visible effect and the PM should expect it in the screenshots.
3. **Singleton groups.** Group by Directory with ten projects is ten one-orb circles. The ticket says one group per distinct value, so the plan does that. A "only group values with two or more orbs" rule is a one-line classifier change if the screenshots look wrong.
4. **Account for Codex/Grok** is unconfirmed (§1). Worst case they stay ungrouped under Account and the PR says so.
5. **Not read by me:** how an orb leaving a user group regains its pin today; `SettleAfterDrop`'s body (taken from `docs/cb268-contracts.md`); `OrbDragCommit.Release`'s exact shape. E2 reads all three before building on them.
