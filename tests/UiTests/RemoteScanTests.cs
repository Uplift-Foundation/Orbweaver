using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// A scan with sessions from another machine in it.
//
// Same harness as SessionScanTests and GatewayScanTests: SessionManager's internal
// constructor takes a scratch status directory, Start() is never called, and the
// snapshot is published through a test seam because the only thing that publishes
// one in production drives a live relay.
//
// A remote session is thin by nature. The peer list gives a name and a status word
// and nothing else — no hostname, no path, no transcript — so what this scan does
// is translate that into the few things an orb draws, and every one of those
// translations has a reason recorded beside it.
[Collection("Settings")]
public class RemoteScanTests
{
    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-rcscan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static SessionManager Manager(string statusDir)
    {
        var ctor = typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;

        return (SessionManager)ctor.Invoke(new object[] { statusDir });
    }

    private static Dictionary<string, OrbWindow> Orbs(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (Dictionary<string, OrbWindow>)field.GetValue(manager)!;
    }

    private static void Publish(params RemoteControlSessions.Remote[] remotes)
    {
        OrbweaverSettings.PeerLinkEnabled = true;
        RemoteControlSessions.SetSnapshotForTests(remotes);
    }

    private static void PublishNothing() =>
        RemoteControlSessions.SetSnapshotForTests(Array.Empty<RemoteControlSessions.Remote>());

    private static RemoteControlSessions.Remote Remote(
        string name, string status = "idle", string account = ".claude", string? colour = null) =>
        new(name, "bridge:session_01", status, DateTime.UtcNow, account, colour);

    // Skipped where the bridge cannot run at all — it is tmux-based, so a Windows
    // runner has no remote sessions to scan and Snapshot answers empty whatever
    // is published. Asserting otherwise there would be asserting the skip.
    // Every transport this app has is cross-platform now, so there is no
    // longer a platform to gate on. Kept as a constant rather than removed,
    // because the tests that read it are asserting "the feature is available",
    // which is still a real question and may acquire a new answer.
    private static bool Supported => true;

    // --- CB-223: a team on the far machine, over the direct link --------------

    private static bool IsTeamMember(OrbWindow orb) =>
        (bool)typeof(OrbWindow).GetField("_isTeamMember", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(orb)!;

    private static List<(OrbWindow Member, OrbWindow Lead)> LinkPairs() =>
        (List<(OrbWindow Member, OrbWindow Lead)>)typeof(SessionManager).Assembly.GetType("Orbweaver.TeamLinks")!
            .GetField("Pairs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static SessionStatus StatusOf(SessionManager manager, string key) =>
        ((Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!)[key];

    // The near half of the round trip: rows a far Buddy sent with team shape
    // become a lead orb and member orbs — members drawn as members, wearing
    // their own agent names and team colours, and linked to their lead by
    // the same TeamLinks pairing a local team gets, with nothing translated.
    [AvaloniaFact]
    public void AFarTeamDrawsMembersLinkedToTheirLead()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(
                new RemoteControlSessions.Remote("backlog status check", "far-mac", "running", DateTime.UtcNow,
                    ".claude", Route: "sid:lead"),
                new RemoteControlSessions.Remote("backlog status check", "far-mac", "idle", DateTime.UtcNow,
                    ".claude", Route: "sid:a", LeadRoute: "sid:lead", Agent: "wren-asare", AgentColor: "blue"),
                new RemoteControlSessions.Remote("backlog status check", "far-mac", "idle", DateTime.UtcNow,
                    ".claude", Color: "green", Route: "sid:b", LeadRoute: "sid:lead", Agent: "hana-moriyama",
                    AgentColor: "purple"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);
            const string lead = "rc:.claude:sid:lead", a = "rc:.claude:sid:a", b = "rc:.claude:sid:b";
            Assert.Contains(lead, orbs.Keys);

            Assert.False(IsTeamMember(orbs[lead]));
            Assert.True(IsTeamMember(orbs[a]));
            Assert.True(IsTeamMember(orbs[b]));

            Assert.Equal(lead, StatusOf(manager, a).Lead);
            Assert.Equal("wren-asare", StatusOf(manager, a).Agent);

            // The team colour fills in where the session has none of its own;
            // a session's own colour outranks it, as it does locally.
            Assert.Equal("blue", StatusOf(manager, a).Color);
            Assert.Equal("green", StatusOf(manager, b).Color);

            var pairs = LinkPairs();
            Assert.Contains(pairs, p => p.Member == orbs[a] && p.Lead == orbs[lead]);
            Assert.Contains(pairs, p => p.Member == orbs[b] && p.Lead == orbs[lead]);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Hana's world: a local team and two remote teams, every one titled the
    // same and the two remote ones using the very same routes, in one scan.
    // Keys are `<session id>` locally and `rc:<account>:<route>` remotely, so a
    // collision looks impossible by construction — this is what makes it a
    // measurement: all six orbs draw, and each member links only to its own
    // team's lead.
    [AvaloniaFact]
    public async Task SameNamedTeamsHereAndOnTwoPeersEachLinkOnlyWithinThemselves()
    {
        using var scratch = new Scratch();

        // A second live process for the local member: two status files on one
        // pid would be one session to the scan (Superseded), not two.
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "ping" : "sleep",
            Arguments = OperatingSystem.IsWindows() ? "-n 120 127.0.0.1" : "120",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        try
        {
            void Local(string id, int pid) => File.WriteAllText(Path.Combine(scratch.Dir, id + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle", Title = "backlog status check", Cwd = "/Users/user/project",
                    SessionPid = pid, TermProgram = "iTerm.app", Tty = "/dev/ttys004",
                }));
            Local("lead", Environment.ProcessId);
            Local("a", child.Id);

            OrbweaverSettings.ClaudeCodeEnabled = true;
            Publish(
                new RemoteControlSessions.Remote("backlog status check", "peer-1", "idle", DateTime.UtcNow,
                    ".claude", Route: "sid:lead"),
                new RemoteControlSessions.Remote("backlog status check", "peer-1", "idle", DateTime.UtcNow,
                    ".claude", Route: "sid:a", LeadRoute: "sid:lead", Agent: "wren-asare"),
                new RemoteControlSessions.Remote("backlog status check", "peer-2", "idle", DateTime.UtcNow,
                    ".claude-board", Route: "sid:lead"),
                new RemoteControlSessions.Remote("backlog status check", "peer-2", "idle", DateTime.UtcNow,
                    ".claude-board", Route: "sid:a", LeadRoute: "sid:lead", Agent: "hana-moriyama"));

            var manager = new SessionManager(
                scratch.Dir,
                () => new Dictionary<string, string>(StringComparer.Ordinal),
                () => new HashSet<string>(StringComparer.Ordinal),
                dependents: _ => SessionDependents.Nothing,
                paneOwners: claims => claims.ToDictionary(TmuxPaneKey.Of, _ => (string?)null),
                agentViewer: _ => null,
                teams: pids => pids.ToDictionary(p => p, p => p == Environment.ProcessId
                    ? new AgentTeam.Membership("lead", "red", "boss")
                    : new AgentTeam.Membership("lead", "blue", "local-member")));

            await manager.ScheduleScan();

            var orbs = Orbs(manager);
            string[] keys = { "lead", "a", "rc:.claude:sid:lead", "rc:.claude:sid:a",
                "rc:.claude-board:sid:lead", "rc:.claude-board:sid:a" };
            Assert.All(keys, k => Assert.Contains(k, orbs.Keys));
            Assert.Equal(keys.Length, keys.Select(k => orbs[k]).Distinct().Count());

            var pairs = LinkPairs();
            Assert.Contains(pairs, p => p.Member == orbs["a"] && p.Lead == orbs["lead"]);
            Assert.Contains(pairs, p => p.Member == orbs["rc:.claude:sid:a"] && p.Lead == orbs["rc:.claude:sid:lead"]);
            Assert.Contains(pairs, p => p.Member == orbs["rc:.claude-board:sid:a"] && p.Lead == orbs["rc:.claude-board:sid:lead"]);
            Assert.Equal(3, pairs.Count(p => keys.Select(k => orbs[k]).Contains(p.Member)));
        }
        finally
        {
            try { child.Kill(); } catch { }
            PublishNothing();
        }
    }

    // A lead cycle among remote rows — not something AgentTeam produces
    // locally, but a far machine is not this one — draws both, links both,
    // and returns: neither the pairing nor the arrangement follows a chain.
    [AvaloniaFact]
    public void ARemoteLeadCycleDrawsAndReturns()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(
                new RemoteControlSessions.Remote("x", "far-mac", "idle", DateTime.UtcNow, ".claude",
                    Route: "sid:x", LeadRoute: "sid:y", Agent: "x"),
                new RemoteControlSessions.Remote("y", "far-mac", "idle", DateTime.UtcNow, ".claude",
                    Route: "sid:y", LeadRoute: "sid:x", Agent: "y"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);
            Assert.True(IsTeamMember(orbs["rc:.claude:sid:x"]));
            Assert.True(IsTeamMember(orbs["rc:.claude:sid:y"]));
            var pairs = LinkPairs();
            Assert.Contains(pairs, p => p.Member == orbs["rc:.claude:sid:x"] && p.Lead == orbs["rc:.claude:sid:y"]);
            Assert.Contains(pairs, p => p.Member == orbs["rc:.claude:sid:y"] && p.Lead == orbs["rc:.claude:sid:x"]);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Every member wears its lead's title, so nothing may find a member's
    // session by name. A member orb's chat panel is built from its key, which
    // for a direct-link row is the route — so it asks for `sid:a`, never for
    // the title three orbs share.
    [AvaloniaFact]
    public void AMemberOrbsPanelAddressesItsSessionByRouteNotTitle()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(
                new RemoteControlSessions.Remote("backlog status check", "far-mac", "idle", DateTime.UtcNow,
                    ".claude", Route: "sid:lead"),
                new RemoteControlSessions.Remote("backlog status check", "far-mac", "idle", DateTime.UtcNow,
                    ".claude", Route: "sid:a", LeadRoute: "sid:lead", Agent: "wren-asare"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor("rc:.claude:sid:a");
            Assert.NotNull(chat);
            var addressed = (string)chat!.GetType()
                .GetField("_remoteName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(chat)!;
            Assert.Equal("sid:a", addressed);
        }
        finally
        {
            PublishNothing();
        }
    }

    // The control: the same rows with no team shape — an older far Buddy, or
    // a relay-only far machine — are flat orbs with no links between them.
    [AvaloniaFact]
    public void TheSameRowsWithoutTeamShapeAreFlatAndUnlinked()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(
                new RemoteControlSessions.Remote("backlog status check", "far-mac", "running", DateTime.UtcNow,
                    ".claude", Route: "sid:lead"),
                new RemoteControlSessions.Remote("backlog status check", "far-mac", "idle", DateTime.UtcNow,
                    ".claude", Route: "sid:a", Agent: "wren-asare", AgentColor: "blue"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);
            Assert.False(IsTeamMember(orbs["rc:.claude:sid:a"]));
            Assert.Equal("", StatusOf(manager, "rc:.claude:sid:a").Lead);
            Assert.Equal("", StatusOf(manager, "rc:.claude:sid:a").Agent);
            Assert.DoesNotContain(LinkPairs(), p => p.Member == orbs["rc:.claude:sid:a"]);
        }
        finally
        {
            PublishNothing();
        }
    }

    [AvaloniaFact]
    public void ARemoteSessionGetsAnOrb()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("rc:.claude:mac-mini", Orbs(manager).Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // The roster reconciliation above this scan removes rows the peer no
    // longer offers. A later scan must remove the already-created window too;
    // otherwise the wire is clean while its ghost remains visible.
    [AvaloniaFact]
    public void ARemoteSessionMissingFromTheNextSnapshotLosesItsOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Remote("gone-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            Assert.Contains("rc:.claude:gone-mini", Orbs(manager).Keys);

            PublishNothing();
            manager.ScanAndUpdate();

            Assert.DoesNotContain("rc:.claude:gone-mini", Orbs(manager).Keys);
            Assert.Null(manager.StatusFor("rc:.claude:gone-mini"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // Two accounts can hold identically-named sessions — the same person naming
    // things the same way twice is the normal case — so the account is part of the
    // key. Without it they collapse onto one orb and one chat panel, with messages
    // going to whichever the dictionary happened to hold.
    [AvaloniaFact]
    public void TwoAccountsWithTheSameSessionNameGetTwoOrbs()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(
                Remote("mac-mini", account: ".claude"),
                Remote("mac-mini", account: ".claude-work"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.Contains("rc:.claude:mac-mini", orbs.Keys);
            Assert.Contains("rc:.claude-work:mac-mini", orbs.Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // The peer list's own word, translated into the two states an orb draws.
    // "running" is the one that matters and the one the first version missed: the
    // vocabulary is not `claude agents --json`'s, which prints "busy", so a remote
    // session sat still for the entire time a machine elsewhere was working.
    [AvaloniaFact]
    public void AWorkingRemoteSessionReadsAsGenerating()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("busy-box", status: "running"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("generating", manager.StatusFor("rc:.claude:busy-box")!.State);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Anything not recognisably work counts as idle: an orb that spins forever
    // because a label changed upstream is worse than one that never spins.
    [AvaloniaFact]
    public void AnUnrecognisedStatusReadsAsIdle()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("quiet-box", status: "some-word-from-a-later-cli"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("idle", manager.StatusFor("rc:.claude:quiet-box")!.State);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Its name on the other machine is all the peer list gives, and it is
    // deliberately not padded out with a guess.
    [AvaloniaFact]
    public void TheTitleIsTheNameOnTheOtherMachine()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("mac-mini", manager.StatusFor("rc:.claude:mac-mini")!.Title);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Marked Remote, which is what draws the two-way arrow badge — the exception
    // worth marking, because almost every orb on screen is local and clicking a
    // remote one opens a chat instead of jumping to a terminal.
    [AvaloniaFact]
    public void ARemoteSessionIsMarkedAsRemote()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal(SessionKind.Remote, manager.StatusFor("rc:.claude:mac-mini")!.Kind);
        }
        finally
        {
            PublishNothing();
        }
    }

    // What the session itself said, when it has been asked and answered. A remote
    // colour cannot be derived here: a peer row carries neither the transcript
    // /color writes into nor the cwd auto-colour hashes.
    [AvaloniaFact]
    public void AnAnsweredColourIsUsed()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", colour: "green"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("green", manager.StatusFor("rc:.claude:mac-mini")!.Color);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A finding, asserted as it behaves rather than as its comment promises.
    //
    // The call site says the colour "falls back to a colour hashed from the name,
    // which is stable per session ... better than every remote orb being
    // identical while the answer is still in flight, or if it never comes." It
    // does not. The fallback is OpenClawSessions.ColourForAgent, which is a lookup
    // into the colours dealt out over the *last gateway listing* — and a remote
    // session's name is never in one, because it comes from a peer list on another
    // machine and not from the gateway at all. So it answers "" every time, and
    // every remote orb with no answered colour is identical: exactly the outcome
    // the comment says it avoids.
    //
    // Left as it is rather than fixed here. It is cosmetic, the intent is written
    // down, and changing what colour a user's orbs are drawn in is a visible
    // behaviour change that belongs in its own ticket rather than riding along in
    // a coverage pass. This test is the record, and it will start failing the day
    // somebody implements the fallback — which is the right moment to notice.
    [AvaloniaFact]
    public void AnUnansweredColourIsEmptyDespiteTheCommentPromisingAFallback()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"), Remote("linux-box"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var one = manager.StatusFor("rc:.claude:mac-mini")!.Color;
            var two = manager.StatusFor("rc:.claude:linux-box")!.Color;

            Assert.True(string.IsNullOrEmpty(one));
            Assert.True(string.IsNullOrEmpty(two));
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...and an answered colour still arrives, which is what makes the gap above
    // cosmetic rather than total: a remote session that has been asked and
    // answered is coloured correctly.
    [AvaloniaFact]
    public void AnAnsweredColourStillArrivesForEachSession()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", colour: "green"), Remote("linux-box", colour: "blue"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("green", manager.StatusFor("rc:.claude:mac-mini")!.Color);
            Assert.Equal("blue", manager.StatusFor("rc:.claude:linux-box")!.Color);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Turning the feature off takes the orbs away on the next scan rather than at
    // the next launch, which is what the switch promises.
    [AvaloniaFact]
    public void TurningRemoteControlOffTakesItsOrbsAway()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            Assert.NotEmpty(Orbs(manager));

            OrbweaverSettings.RemoteControlEnabled = false;
        // Both transports, because "off" is now two switches. A test that
        // turns one off and leaves the other to whatever the last test set
        // is asserting about a state it did not arrange — and settings here
        // persist through ReloadForTests, since the setter writes the file.
        OrbweaverSettings.PeerLinkEnabled = false;
            manager.ScanAndUpdate();

            Assert.Empty(Orbs(manager));
        }
        finally
        {
            OrbweaverSettings.PeerLinkEnabled = true;
            PublishNothing();
        }
    }

    // --- talking to a remote session's conversation ---

    private static IDictionary<string, SessionStatus> Statuses(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IDictionary<string, SessionStatus>)field.GetValue(manager)!;
    }

    // Cached like the local ones: the conversation exists only in memory, so
    // rebuilding it on every click would throw the whole exchange away rather
    // than merely losing scroll position.
    [AvaloniaFact]
    public void RemoteChatForARemoteSessionIsCachedOnceCreated()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", account: ".claude"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor("rc:.claude:mac-mini");
            Assert.NotNull(chat);
            Assert.Equal("mac-mini", chat!.DisplayName);

            Assert.Same(chat, manager.RemoteChatFor("rc:.claude:mac-mini"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // The id's own "rc:<account>:<name>" shape is what the scan always mints,
    // but the account/name split falls back to the status's title and the
    // default profile dir for an id that does not have a second colon in it
    // — which nothing on the scan path produces today, but the source
    // comment is explicit that the split is "the first separator after the
    // prefix", implying one might not be there.
    [AvaloniaFact]
    public void RemoteChatForAnIdWithNoAccountSeparatorFallsBackToTheTitleAndDefaultAccount()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        var manager = Manager(scratch.Dir);

        Statuses(manager)["rc:onlyname"] = new SessionStatus
        {
            Source = SessionSource.RemoteControl,
            Title = "fallback-name",
        };

        var chat = manager.RemoteChatFor("rc:onlyname");

        Assert.NotNull(chat);
        Assert.Equal("fallback-name", chat!.DisplayName);
    }

    // --- CB-307: a parked remote orb is drawn dimmed, and clears -------------

    // The host's roster said nobody has spoken to this session for a while, so
    // the near side draws the orb dimmed rather than not at all — and the next
    // roster without the flag brings it back to full brightness. Control: the
    // very same row, unparked, is Present, so the dimming is the flag's doing.
    [AvaloniaFact]
    public void AParkedRemoteSessionIsDrawnDimmedAndClearsWhenTheNextRosterDropsTheFlag()
    {
        using var scratch = new Scratch();
        try
        {
            OrbweaverSettings.ClaudeCodeEnabled = true;
            Publish(new RemoteControlSessions.Remote("lawyer-search", "far-mac", "idle", DateTime.UtcNow,
                ".claude", Route: "sid:quiet", Parked: true));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            const string key = "rc:.claude:sid:quiet";
            Assert.Contains(key, Orbs(manager).Keys);
            Assert.Equal(OrbPresence.Parked, StatusOf(manager, key).Presence);
            Assert.Equal(OrbPresence.Parked, Orbs(manager)[key].Presence);
            Assert.Equal("parked", Orbs(manager)[key].PresenceLabel);

            Publish(new RemoteControlSessions.Remote("lawyer-search", "far-mac", "idle", DateTime.UtcNow,
                ".claude", Route: "sid:quiet"));
            manager.ScanAndUpdate();

            Assert.Equal(OrbPresence.Present, StatusOf(manager, key).Presence);
            Assert.Equal(OrbPresence.Present, Orbs(manager)[key].Presence);
            Assert.Null(Orbs(manager)[key].PresenceLabel);
        }
        finally
        {
            PublishNothing();
        }
    }

}
