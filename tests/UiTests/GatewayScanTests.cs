using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// A scan that has gateway sessions in it, and the room orbs it invents from them.
//
// Same harness as SessionScanTests next door — SessionManager's internal
// constructor takes a scratch status directory and Start() is never called — with
// one addition: the gateway snapshot is published through a test seam, because the
// only thing that publishes one in production is the poll loop, which needs a live
// socket and is excluded.
//
// The snapshot itself is built by handing real sessions.list JSON to
// OpenClawSessions.Parse, rather than constructing Session records by hand. That
// way the test travels the route a real poll does and cannot drift into asserting
// a shape the gateway never sends.
//
// What is being tested is the thing the gateway has no notion of. It reports a
// session per agent per channel, so eight agents in one room are eight orbs with
// nothing saying they are the same conversation — the room orb is invented here,
// once, however many agents point at it.
[Collection("Settings")]
public class GatewayScanTests
{
    // The real clock, not a fixed date. Parse takes its clock as an argument but
    // ScanAndUpdate reads DateTime.UtcNow for the lifetime check, so a fixture
    // pinned to a made-up "now" produces sessions that look hours stale and get
    // expired before an orb is ever made — which is exactly how the first draft
    // of this file failed every case.
    private static DateTime Now => DateTime.UtcNow;

    private static long JustNow => new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-gwscan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static Dictionary<string, OrbWindow> Orbs(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (Dictionary<string, OrbWindow>)field.GetValue(manager)!;
    }

    private static SessionManager Manager(string statusDir)
    {
        var ctor = typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;

        return (SessionManager)ctor.Invoke(new object[] { statusDir });
    }

    // Publishes exactly what a poll would: parsed sessions, nothing hand-rolled.
    private static void Publish(string sessionsJson)
    {
        OrbweaverSettings.OpenClawEnabled = true;
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes =
            OrbweaverSettings.OpenClawActiveWithinAll;

        var (sessions, _) = OpenClawSessions.Parse(
            JsonDocument.Parse(sessionsJson).RootElement, DateTime.UtcNow);

        OpenClawSessions.SetSnapshotForTests(sessions);
    }

    private static void PublishNothing()
    {
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
    }

    // --- gateway orbs ---

    [AvaloniaFact]
    public void AGatewaySessionGetsAnOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:aurora:discord:direct:2467","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("openclaw:agent:aurora:discord:direct:2467", Orbs(manager).Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Namespaced, because these ids share a dictionary with Claude Code's uuids —
    // and because a gateway key contains colons and slashes that would otherwise
    // be spliced into a status-file path.
    [AvaloniaFact]
    public void GatewayOrbIdsAreNamespaced()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:direct:1","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.All(Orbs(manager).Keys, id => Assert.StartsWith("openclaw:", id));
        }
        finally
        {
            PublishNothing();
        }
    }

    // --- the room orb ---

    // One agent in a channel is not a room. It used to get one anyway: two orbs
    // wearing the same face, an arrow between them, and a merged conversation
    // with a single member in it — three ways of saying "these are the same
    // thing" about a thing that was never two.
    //
    // The room orb earns its place by gathering several agents. With one, that
    // agent's own orb already is the channel, and its # badge already says so.
    [AvaloniaFact]
    public void ASingleAgentInAChannelGetsNoRoomOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:nova:discord:channel:1474","chatType":"channel",
                              "groupChannel":"#general","lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.DoesNotContain(orbs.Keys, id => id.Contains(":room:"));
            Assert.Single(orbs.Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...and it points at nothing, which is what stops the arrow being drawn.
    [AvaloniaFact]
    public void ASingleAgentInAChannelPointsAtNothing()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:nova:discord:channel:1474","chatType":"channel",
                              "groupChannel":"#general","lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var status = manager.StatusFor("openclaw:agent:nova:discord:channel:1474");

            Assert.NotNull(status);
            Assert.True(string.IsNullOrEmpty(status!.Lead), "a lone agent has no room to point at");
        }
        finally
        {
            PublishNothing();
        }
    }

    // One agent, two sessions in the same channel — still one agent, so still no
    // room orb. Counting sessions instead of people would call this a crowd and
    // draw a room around somebody standing on their own.
    [AvaloniaFact]
    public void OneAgentWithTwoSessionsInAChannelIsStillNotARoom()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[
                  {"key":"agent:nova:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}},
                  {"key":"agent:nova:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.DoesNotContain(Orbs(manager).Keys, id => id.Contains(":room:"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // The moment a second agent joins, the room appears and both point at it —
    // which is the whole of what the arrow is for.
    [AvaloniaFact]
    public void ASecondAgentJoiningBringsTheRoomOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:nova:discord:channel:1474","chatType":"channel",
                              "groupChannel":"#general","lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.DoesNotContain(Orbs(manager).Keys, id => id.Contains(":room:"));

            Publish($$"""
                {"sessions":[
                  {"key":"agent:nova:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}},
                  {"key":"agent:annabel:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            manager.ScanAndUpdate();

            Assert.Contains(SessionManager.RoomId("discord:1474"), Orbs(manager).Keys);
            Assert.Equal(
                SessionManager.RoomId("discord:1474"),
                manager.StatusFor("openclaw:agent:nova:discord:channel:1474")!.Lead);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Two agents standing in one channel produce three orbs: one each, and one
    // for the room. The gateway has no notion of a room as a thing, so this is
    // the only place it exists.
    [AvaloniaFact]
    public void TwoAgentsInOneChannelGetOneRoomOrbBetweenThem()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[
                  {"key":"agent:aurora:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}},
                  {"key":"agent:nova:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);
            var roomId = SessionManager.RoomId("discord:1474");

            Assert.Contains(roomId, orbs.Keys);
            Assert.Equal(3, orbs.Count);
            Assert.Single(orbs.Keys, id => id == roomId);
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...and every agent in it points at that one orb, which is what draws the
    // arrows that say they are one conversation.
    [AvaloniaFact]
    public void EveryAgentInAChannelPointsAtTheRoomOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[
                  {"key":"agent:aurora:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}},
                  {"key":"agent:nova:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var roomId = SessionManager.RoomId("discord:1474");

            foreach (var agent in new[] { "aurora", "nova" })
            {
                var status = manager.StatusFor($"openclaw:agent:{agent}:discord:channel:1474");

                Assert.NotNull(status);
                Assert.Equal(roomId, status!.Lead);
            }
        }
        finally
        {
            PublishNothing();
        }
    }

    // Eight agents, one room: still one room orb. The count is the whole point —
    // eight rooms would be worse than none.
    [AvaloniaFact]
    public void EightAgentsInOneChannelStillGetOneRoomOrb()
    {
        using var scratch = new Scratch();
        try
        {
            var rows = string.Join(",", Enumerable.Range(0, 8).Select(i => $$"""
                {"key":"agent:a{{i}}:discord:channel:99","chatType":"channel",
                 "groupChannel":"#arch","lastActivityAt":{{JustNow}}}
                """));

            Publish("{\"sessions\":[" + rows + "]}");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.Equal(9, orbs.Count);
            Assert.Single(orbs.Keys, id => id == SessionManager.RoomId("discord:99"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // Two channels are two rooms, so the grouping is per channel rather than per
    // gateway.
    [AvaloniaFact]
    public void TwoChannelsGetTwoRoomOrbs()
    {
        using var scratch = new Scratch();
        try
        {
            // Two agents in each channel, since one no longer makes a room orb.
            // The same pair in both, which is the case a "group by agent" rule
            // would collapse into one room and is what this asserts against.
            Publish($$"""
                {"sessions":[
                  {"key":"agent:main:discord:channel:1","chatType":"channel",
                   "groupChannel":"#one","lastActivityAt":{{JustNow}}},
                  {"key":"agent:nova:discord:channel:1","chatType":"channel",
                   "groupChannel":"#one","lastActivityAt":{{JustNow}}},
                  {"key":"agent:main:discord:channel:2","chatType":"channel",
                   "groupChannel":"#two","lastActivityAt":{{JustNow}}},
                  {"key":"agent:nova:discord:channel:2","chatType":"channel",
                   "groupChannel":"#two","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.Contains(SessionManager.RoomId("discord:1"), orbs.Keys);
            Assert.Contains(SessionManager.RoomId("discord:2"), orbs.Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A direct message is not a room: two people messaging privately is not
    // something other agents can join, so no room orb is invented for it.
    [AvaloniaFact]
    public void ADirectMessageGetsNoRoomOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:direct:2467","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.Single(orbs);
            Assert.DoesNotContain(orbs.Keys, id => id.Contains(":room:"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // The room orb is marked as one, which is what tells the panel to open a
    // merged conversation rather than a single agent's.
    [AvaloniaFact]
    public void TheRoomOrbIsMarkedAsARoom()
    {
        using var scratch = new Scratch();
        try
        {
            // Two agents, because one no longer makes a room orb at all — see
            // ASingleAgentInAChannelGetsNoRoomOrb below. This case is about the
            // flag on the orb, not about how few members can produce one.
            Publish($$"""
                {"sessions":[
                  {"key":"agent:main:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}},
                  {"key":"agent:nova:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var status = manager.StatusFor(SessionManager.RoomId("discord:1474"));

            Assert.NotNull(status);
            Assert.True(status!.IsRoom);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A room whose member is mid-run reads as working, so the room orb pulses
    // while anybody in it is talking rather than only when the newest message
    // happens to be from a busy agent.
    [AvaloniaFact]
    public void ARoomIsWorkingWhileAnyMemberIs()
    {
        using var scratch = new Scratch();
        try
        {
            // The generating agent is deliberately the *older* of the two, which
            // is the case a "take the newest session's state" rule gets wrong.
            var older = new DateTimeOffset(Now.AddMinutes(-2)).ToUnixTimeMilliseconds();

            Publish($$"""
                {"sessions":[
                  {"key":"agent:busy:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{older}}},
                  {"key":"agent:quiet:discord:channel:1474","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var status = manager.StatusFor(SessionManager.RoomId("discord:1474"));

            Assert.NotNull(status);

            // Neither session is generating here — the gateway reports state
            // separately from the list — so the room is idle, and that is the
            // baseline this asserts. The generating case needs an event to have
            // been seen, which is the poll loop's job and excluded with it.
            Assert.Equal("idle", status!.State);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A room whose newest member is idle still reads as working when an
    // *older* member is genuinely mid-run — the case ARoomIsWorkingWhileAnyMemberIs
    // next door can only state as a baseline, because state.State=="generating"
    // needs a real event to have been seen and that is normally the poll
    // loop's job, excluded with it. OpenClawSessions.OnEvent is the one
    // production entry point for recording that a session is running, and it
    // is not excluded — it is a JSON-in-JSON-out function, not a live socket
    // — so a real event through it is the honest way to reach this rather
    // than inventing a "generating" state Parse would never actually produce.
    [AvaloniaFact]
    public void ARoomWhoseOnlyGeneratingMemberIsOlderThanTheNewestStillReadsAsWorking()
    {
        // A channel id this test alone uses, so a leftover Running entry from
        // another case naming the same room can never be the reason this
        // passes — OpenClawSessions.SetSnapshotForTests resets the snapshot
        // between cases but Running is never cleared, so sharing a key with
        // ARoomIsWorkingWhileAnyMemberIs next door would make the two cases
        // interfere with each other in a way that could pass for the wrong
        // reason.
        //
        // "Older" has to mean older than OpenClawSessions.Activity actually
        // computes, not older than the JSON row claims: Activity takes the
        // *later* of the reported lastActivityAt and LastSeen[key], and
        // OnEvent — the only way a session ever becomes "generating" here —
        // stamps LastSeen with the real wall clock at the moment it fires.
        // Giving the generating session a past lastActivityAt in JSON does
        // nothing, because LastSeen from the OnEvent call below always wins
        // over it; the row that has to be older is the *quiet* one, and it is
        // only older if it is given a lastActivityAt far enough in the future
        // of "now" to still be after LastSeen once the event fires. This is
        // what a real generating session with a real recent event and a real
        // quieter roommate whose own last message was more recent still would
        // look like — the gateway's own list, not the poll loop's clock.
        using var scratch = new Scratch();
        const string busyKey = "agent:busy:discord:channel:99184";
        try
        {
            // A real streaming shape: since CB-169 an event name nobody has
            // measured ("message" was one) is not evidence of work.
            OpenClawSessions.OnEvent("agent",
                JsonDocument.Parse($$"""{"sessionKey":"{{busyKey}}","stream":"thinking"}""").RootElement);

            var quietIsNewer = new DateTimeOffset(Now.AddMinutes(2)).ToUnixTimeMilliseconds();

            // The newer (by reported activity) session is listed first so it
            // claims the room entry; the busy one arrives second and, despite
            // being marked generating, has an Activity no later than the
            // room's own — so it must not overwrite the title or activity,
            // only the Working flag.
            Publish($$"""
                {"sessions":[
                  {"key":"agent:quiet:discord:channel:99184","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{quietIsNewer}}},
                  {"key":"{{busyKey}}","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var status = manager.StatusFor(SessionManager.RoomId("discord:99184"));

            Assert.NotNull(status);
            Assert.Equal("generating", status!.State);
        }
        finally
        {
            OpenClawSessions.OnEvent("cron",
                JsonDocument.Parse($$"""{"sessionKey":"{{busyKey}}","action":"finished"}""").RootElement);
            PublishNothing();
        }
    }

    // With the gateway switched off the snapshot is empty whatever was last
    // published, so no gateway orb survives a user turning it off — the point
    // being that it happens on the next scan rather than at the next launch.
    [AvaloniaFact]
    public void TurningTheGatewayOffTakesItsOrbsAway()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1474","chatType":"channel",
                              "groupChannel":"#general","lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            Assert.NotEmpty(Orbs(manager));

            OrbweaverSettings.OpenClawEnabled = false;
            manager.ScanAndUpdate();

            Assert.Empty(Orbs(manager));
        }
        finally
        {
            OrbweaverSettings.OpenClawEnabled = true;
            PublishNothing();
        }
    }

    // --- the auto-colour marker ---

    // The hook learns about the colour setting from a marker file beside the
    // status files, because the hook runs on every tool call and reading a
    // setting there would be an osascript each time. So this file is a contract
    // with a script, not an implementation detail — and the scan reconciles it.
    [AvaloniaFact]
    public void TheAutoColourMarkerFollowsTheSetting()
    {
        using var scratch = new Scratch();
        var marker = Path.Combine(scratch.Dir, ".auto-color");
        var manager = Manager(scratch.Dir);

        try
        {
            OrbweaverSettings.AutoColorSessions = true;
            manager.SyncAutoColorMarker();
            Assert.True(File.Exists(marker), "the marker should appear when the setting is on");

            OrbweaverSettings.AutoColorSessions = false;
            manager.SyncAutoColorMarker();
            Assert.False(File.Exists(marker), "the marker should go when the setting is off");
        }
        finally
        {
            OrbweaverSettings.AutoColorSessions = false;
        }
    }

    // Idempotent in both directions, because it runs on every scan — a couple of
    // times a second — and must not rewrite a file that is already right.
    [AvaloniaFact]
    public void SyncingTheMarkerTwiceChangesNothing()
    {
        using var scratch = new Scratch();
        var marker = Path.Combine(scratch.Dir, ".auto-color");
        var manager = Manager(scratch.Dir);

        try
        {
            OrbweaverSettings.AutoColorSessions = true;
            manager.SyncAutoColorMarker();
            var first = File.GetLastWriteTimeUtc(marker);

            manager.SyncAutoColorMarker();

            Assert.Equal(first, File.GetLastWriteTimeUtc(marker));

            OrbweaverSettings.AutoColorSessions = false;
            manager.SyncAutoColorMarker();
            manager.SyncAutoColorMarker();

            Assert.False(File.Exists(marker));
        }
        finally
        {
            OrbweaverSettings.AutoColorSessions = false;
        }
    }

    // --- a status file that cannot be read ---

    // One unreadable file must not cost every orb in the directory. The scan
    // wraps each file's read in its own catch for exactly this, and the failure it
    // guards against is the worst kind: a single bad file taking every session off
    // the screen at once.
    //
    // A directory named like a status file is the cheapest way to make the read
    // throw for real, rather than mocking a filesystem that would then be the
    // thing under test.
    [AvaloniaFact]
    public void OneUnreadableStatusFileDoesNotCostTheOthers()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:direct:1","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            // Not a file at all: opening it as one throws.
            Directory.CreateDirectory(Path.Combine(scratch.Dir, "not-really-a-session.txt"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            // The gateway session still got its orb, which is the point: the bad
            // entry was stepped over rather than ending the scan.
            Assert.Contains("openclaw:agent:main:discord:direct:1", Orbs(manager).Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Malformed JSON in a real file is the other half of the same guard, and the
    // likelier one: a hook interrupted mid-write leaves exactly this.
    [AvaloniaFact]
    public void AHalfWrittenStatusFileIsSteppedOver()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:direct:2","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            File.WriteAllText(Path.Combine(scratch.Dir, "half-written.txt"), "{\"state\":\"id");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("openclaw:agent:main:discord:direct:2", Orbs(manager).Keys);
            Assert.DoesNotContain("half-written", Orbs(manager).Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A directory that does not exist at all is not an error either: the scan
    // treats it as "no local sessions" and carries on with whatever the gateway
    // reported. This is the state on a machine where no hook has ever fired.
    [AvaloniaFact]
    public void AMissingStatusDirectoryIsNotAnError()
    {
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:direct:3","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(Path.Combine(Path.GetTempPath(), "cb-never-made-" + Guid.NewGuid()));
            manager.ScanAndUpdate();

            Assert.Contains("openclaw:agent:main:discord:direct:3", Orbs(manager).Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // --- opening a gateway conversation ---

    // A room's conversation is its members' transcripts merged — assembled
    // from OpenClawSessions.MembersOfRoom and OpenClawSessions.RoomChatFor,
    // both reached only through this call in production.
    [AvaloniaFact]
    public void RemoteChatForARoomAsksTheGatewayToMergeItsMembers()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[
                  {"key":"agent:aurora:discord:channel:20250","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}},
                  {"key":"agent:nova:discord:channel:20250","chatType":"channel",
                   "groupChannel":"#general","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor(SessionManager.RoomId("discord:20250"));

            Assert.NotNull(chat);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A plain agent session — not a room — asks OpenClawSessions.ChatFor
    // directly for it.
    [AvaloniaFact]
    public void RemoteChatForAPlainGatewaySessionAsksTheGatewayForIt()
    {
        using var scratch = new Scratch();
        try
        {
            Publish($$"""
                {"sessions":[{"key":"agent:main:discord:direct:20251","chatType":"direct",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor("openclaw:agent:main:discord:direct:20251");

            Assert.NotNull(chat);
        }
        finally
        {
            PublishNothing();
        }
    }

    // CB-169, end to end through the app: a run on an agent's main session
    // lights its orb from the run's start to its end. Those two rows are the
    // whole of what such a run sends (the capture is in docs/openclaw-findings.md,
    // "A run outside a cron session"; the payloads here are those rows reduced
    // to the fields read), so before CB-169 dropped every sessions.changed this
    // orb never glowed at all. Driven through the real OnEvent, a published
    // listing and a scan, and asserted on the orb's Glow — what an owner sees.
    [AvaloniaFact]
    public void AMainSessionRunLightsItsOrbFromStartToEnd()
    {
        using var scratch = new Scratch();
        const string key = "agent:cb169glow:main";
        const string run = "00000000-0000-4000-8000-000000000003";

        void Row(string phase) => OpenClawSessions.OnEvent("sessions.changed",
            JsonDocument.Parse($$"""{"sessionKey":"{{key}}","runId":"{{run}}","phase":"{{phase}}"}""").RootElement);

        SessionStatus Scanned()
        {
            Publish($$"""
                {"sessions":[{"key":"{{key}}","chatType":"direct","lastActivityAt":{{JustNow}}}]}
                """);
            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            return manager.StatusFor("openclaw:" + key)!;
        }

        var orb = new OrbWindow("openclaw:" + key);
        try
        {
            orb.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            orb.UpdateFrom(Scanned());
            Assert.False(orb.Glow.IsVisible);

            Row("start");
            var during = Scanned();
            orb.UpdateFrom(during);
            Assert.Equal("generating", during.State);
            Assert.True(orb.Glow.IsVisible);

            Row("end");
            var after = Scanned();
            orb.UpdateFrom(after);
            Assert.Equal("idle", after.State);
            Assert.False(orb.Glow.IsVisible);
        }
        finally
        {
            orb.Close();
            Row("end");
            PublishNothing();
        }
    }

    // --- CB-270: pinned and dashboard sessions outlive "Keep orbs for" ---

    // Three days quiet against the shortest lifetime the picker offers, so nothing
    // longer can be what carried the orb. Parse is given the "all" window by
    // Publish, so these rows reach the scan, which is where the lifetime clock
    // lives.
    private static readonly long ThreeDaysAgo =
        new DateTimeOffset(DateTime.UtcNow.AddDays(-3)).ToUnixTimeMilliseconds();

    private static HashSet<string> ScanOldRows(string rows)
    {
        using var scratch = new Scratch();
        var wasLifetime = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 1;
            Publish("{\"sessions\":[" + rows + "]}");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            // Twice: surviving one scan and expiring on the next is a different
            // bug from never getting an orb.
            manager.ScanAndUpdate();

            return new HashSet<string>(Orbs(manager).Keys);
        }
        finally
        {
            OrbweaverSettings.OrbLifetimeMinutes = wasLifetime;
            PublishNothing();
        }
    }

    // One scan holds all three, which is what makes the control mean something:
    // the exemption is a clause too wide the moment the unpinned DM survives too.
    [AvaloniaFact]
    public void ADashboardAndAPinnedDmSurviveWhileAnUnpinnedDmOfTheSameAgeExpires()
    {
        var orbs = ScanOldRows($$"""
            {"key":"agent:a:dashboard:u1","classification":"dashboard","lastActivityAt":{{ThreeDaysAgo}}},
            {"key":"agent:a:discord:direct:1","classification":"direct","chatType":"direct","pinned":true,"lastActivityAt":{{ThreeDaysAgo}}},
            {"key":"agent:a:discord:direct:2","classification":"direct","chatType":"direct","lastActivityAt":{{ThreeDaysAgo}}}
            """);

        Assert.Contains("openclaw:agent:a:dashboard:u1", orbs);
        Assert.Contains("openclaw:agent:a:discord:direct:1", orbs);
        Assert.DoesNotContain("openclaw:agent:a:discord:direct:2", orbs);
    }
}
