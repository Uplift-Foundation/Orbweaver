using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-276: the three "is my listing authoritative" seams the hidden-orb pruning
// reads, one per remote source. Each answers one of three things: the sessions
// it lists, nothing at all because it cannot say right now, or (by the caller
// comparing an id against a non-null listing) authoritatively absent. Only the
// last leads to a prune, and a wrong one deletes a user's hide silently, so the
// tests lean on the cannot-say side.
//
// The negative control is the same in every group. A seam that always answered
// "an empty listing" would pass every test that expects a session to be absent,
// so each group also holds a case where the source has plainly failed and the
// listing it still holds in memory must not be believed: those fail the moment
// the seam stops returning null for them.
[Collection("Settings")]
public class HiddenOrbSeamsTests : IDisposable
{
    private readonly bool _openClaw = OrbweaverSettings.OpenClawEnabled;
    private readonly bool _cloud = OrbweaverSettings.ClaudeCloudEnabled;
    private readonly bool _peerLink = OrbweaverSettings.PeerLinkEnabled;

    public void Dispose()
    {
        OrbweaverSettings.OpenClawEnabled = _openClaw;
        OrbweaverSettings.ClaudeCloudEnabled = _cloud;
        OrbweaverSettings.PeerLinkEnabled = _peerLink;
        OpenClawSessions.SetListingCurrentForTests(false);
        ClaudeCloudSessions.SetBoardForTests(null);
        RemoteControlSessions.ResetForTests();
    }

    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    // --- OpenClaw ------------------------------------------------------------

    private static long Ms(DateTime when) => new DateTimeOffset(when).ToUnixTimeMilliseconds();

    private static IReadOnlyList<OpenClawSessions.Session> ParseListing(string json)
    {
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawNativeMode = ClusterMode.WithChats;
        // An hour: anything older is dropped from the snapshot, which is the
        // whole reason ListedKeys exists.
        OrbweaverSettings.OpenClawActiveWithinMinutes = 60;

        return OpenClawSessions.Parse(JsonDocument.Parse(json).RootElement, Now).Sessions;
    }

    private static string Listing(long recent, long quiet) => $$"""
        {"sessions":[
          {"key":"agent:alpha:discord:direct:1","classification":"direct","chatType":"direct",
           "origin":{"provider":"discord"},"deliveryContext":{"channel":"discord","to":"user:1"},
           "lastChannel":"discord","lastActivityAt":{{recent}}},
          {"key":"agent:alpha:discord:direct:2","classification":"direct","chatType":"direct",
           "origin":{"provider":"discord"},"deliveryContext":{"channel":"discord","to":"user:2"},
           "lastChannel":"discord","lastActivityAt":{{quiet}}},
          {"key":"agent:alpha:discord:direct:3","classification":"direct","archived":true,
           "origin":{"provider":"discord"},"lastChannel":"discord","lastActivityAt":{{recent}}}
        ]}
        """;

    [Fact]
    public void OpenClawListsAQuietSessionTheSnapshotDropped()
    {
        OrbweaverSettings.OpenClawEnabled = true;
        var snapshot = ParseListing(Listing(Ms(Now.AddSeconds(-5)), Ms(Now.AddDays(-2))));
        OpenClawSessions.SetListingCurrentForTests(true);

        Assert.DoesNotContain(snapshot, s => s.Key == "agent:alpha:discord:direct:2");

        var listed = OpenClawSessions.ListedKeys();
        Assert.NotNull(listed);
        Assert.Contains("agent:alpha:discord:direct:1", listed!);
        Assert.Contains("agent:alpha:discord:direct:2", listed);
    }

    [Fact]
    public void OpenClawAnswersAbsentForAKeyTheGatewayNeverListedAndForAnArchivedOne()
    {
        OrbweaverSettings.OpenClawEnabled = true;
        ParseListing(Listing(Ms(Now.AddSeconds(-5)), Ms(Now.AddDays(-2))));
        OpenClawSessions.SetListingCurrentForTests(true);

        var listed = OpenClawSessions.ListedKeys();
        Assert.NotNull(listed);
        Assert.DoesNotContain("agent:alpha:discord:direct:9", listed!);
        Assert.DoesNotContain("agent:alpha:discord:direct:3", listed);
    }

    [Fact]
    public void OpenClawCannotSayBeforeAnyListingHasBeenPublished()
    {
        OrbweaverSettings.OpenClawEnabled = true;
        ParseListing(Listing(Ms(Now.AddSeconds(-5)), Ms(Now.AddSeconds(-5))));
        OpenClawSessions.SetListingCurrentForTests(false);

        // The keys are sitting in memory from the parse above; they are not an
        // answer until a poll has published them on a live connection.
        Assert.Null(OpenClawSessions.ListedKeys());
    }

    [Fact]
    public void OpenClawCannotSayWhenItIsSwitchedOffHoweverFreshTheListing()
    {
        OrbweaverSettings.OpenClawEnabled = true;
        ParseListing(Listing(Ms(Now.AddSeconds(-5)), Ms(Now.AddSeconds(-5))));
        OpenClawSessions.SetListingCurrentForTests(true);
        Assert.NotNull(OpenClawSessions.ListedKeys());

        OrbweaverSettings.OpenClawEnabled = false;

        Assert.Null(OpenClawSessions.ListedKeys());
    }

    [Fact]
    public void OpenClawAnEmptyListingIsAnAnswerAndIsNotNull()
    {
        OrbweaverSettings.OpenClawEnabled = true;
        ParseListing("{\"sessions\":[]}");
        OpenClawSessions.SetListingCurrentForTests(true);

        var listed = OpenClawSessions.ListedKeys();

        Assert.NotNull(listed);
        Assert.Empty(listed!);
    }

    // --- cloud ---------------------------------------------------------------

    private const string RootA = "/Users/x/.claude";
    private const string RootB = "/Users/x/.claude-seams";

    private sealed class Creds : ICloudCredentialSource
    {
        private readonly string _token = "sk-ant-oat01-CANARY-SEAMS-0123456789";
        public string? Stamp() => "s1";
        public CredentialRead Read() => new(CredentialOutcome.Found, _token, null, "a credential is present");
    }

    private sealed class Api : ICloudApi
    {
        private readonly Func<string, CloudApiResult> _answer;
        internal Api(Func<string, CloudApiResult> answer) => _answer = answer;

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token) =>
            Task.FromResult(_answer(context.Path));
    }

    private static CloudApiResult Ok(string body) => new(CloudOutcomes.OutcomeFor(200, body), body);

    private static CloudApiResult Fail(int status) => new(CloudOutcomes.OutcomeFor(status, null), null);

    private static string Roster(bool hasMore, params string[] ids) =>
        "{\"data\":[" + string.Join(",", ids.Select(id =>
            "{\"id\":\"" + id + "\",\"environment_kind\":\"anthropic_cloud\",\"session_status\":\"running\","
            + "\"status_bucket\":\"running\",\"title\":\"t-" + id + "\",\"updated_at\":\"2026-09-19T10:00:00Z\"}"))
        + "],\"has_more\":" + (hasMore ? "true" : "false") + ",\"last_id\":" + (hasMore ? "\"session_more\"" : "null") + "}";

    private static CloudAccount Account(string root) =>
        new(root, root == RootA ? "default" : ClaudeCliCredentials.SafeLabel(root), new Creds(), new Creds());

    private static Task<ClaudeCloudSessions.StepResult> Step(Func<string, CloudApiResult> answer) =>
        ClaudeCloudSessions.StepAsync(new Api(answer), new Creds(), ClaudeCloudSessions.ArmState.Initial,
            Now, CancellationToken.None, TestBudgets.AlwaysAnswers);

    private static Task<ClaudeCloudSessions.StepResult> GoodStep(params string[] ids) =>
        Step(_ => Ok(Roster(false, ids)));

    [Fact]
    public async Task CloudCannotSayBeforeAnAccountsFirstTickHasLanded()
    {
        var board = new CloudAccountBoard(new[] { Account(RootA) });

        Assert.Null(board.ListedIds());
    }

    [Fact]
    public async Task CloudListsWhatACompleteWalkFound()
    {
        var board = new CloudAccountBoard(new[] { Account(RootA) });

        board.Apply(RootA, await GoodStep("session_1", "session_2"), Now);

        var listed = board.ListedIds();
        Assert.NotNull(listed);
        Assert.Equal(new[] { "session_1", "session_2" }, listed!.OrderBy(i => i, StringComparer.Ordinal));
        Assert.DoesNotContain("session_9", listed);
    }

    [Fact]
    public async Task CloudCannotSayAfterAFailedFetchEvenThoughItKeepsTheOldSessions()
    {
        var board = new CloudAccountBoard(new[] { Account(RootA) });
        board.Apply(RootA, await GoodStep("session_1"), Now);
        Assert.NotNull(board.ListedIds());

        var failed = await Step(_ => Fail(500));
        Assert.Null(failed.Snapshot);
        board.Apply(RootA, failed, Now);

        // The orbs stay (that is what a null snapshot is for) and the listing is
        // no longer believed.
        Assert.Single(board.Merged);
        Assert.Null(board.ListedIds());
    }

    [Fact]
    public async Task CloudCannotSayWhenAnAccountIsHaltedAndPublishesAnEmptyList()
    {
        var board = new CloudAccountBoard(new[] { Account(RootA) });
        board.Apply(RootA, await GoodStep("session_1"), Now);

        var halted = await Step(_ => Fail(401));
        Assert.True(halted.Next.Halted);
        Assert.NotNull(halted.Snapshot);
        Assert.Empty(halted.Snapshot!);
        board.Apply(RootA, halted, Now);

        Assert.Empty(board.Merged);
        Assert.Null(board.ListedIds());
    }

    [Fact]
    public async Task CloudCannotSayAfterAWalkThePageCapCutShort()
    {
        var board = new CloudAccountBoard(new[] { Account(RootA) });

        var cut = await Step(_ => Ok(Roster(true, "session_1")));
        Assert.False(cut.Next.Complete);
        board.Apply(RootA, cut, Now);

        Assert.Single(board.Merged);
        Assert.Null(board.ListedIds());
    }

    [Fact]
    public async Task ACompleteWalkMarksTheArmComplete()
    {
        Assert.True((await GoodStep("session_1")).Next.Complete);
    }

    [Fact]
    public async Task CloudCannotSayAfterAnErrorInTheAccountsLoop()
    {
        var board = new CloudAccountBoard(new[] { Account(RootA) });
        board.Apply(RootA, await GoodStep("session_1"), Now);

        board.ApplyError(RootA, "boom");

        Assert.Null(board.ListedIds());
    }

    [Fact]
    public async Task CloudNeedsEveryAccountAuthoritativeBeforeAnyIdCanReadAbsent()
    {
        var board = new CloudAccountBoard(new[] { Account(RootA), Account(RootB) });
        board.Apply(RootA, await GoodStep("session_1"), Now);

        // B has not answered: a cloud id does not say which account owns it, so
        // an id missing from A's list may simply be B's.
        Assert.Null(board.ListedIds());

        board.Apply(RootB, await GoodStep("session_2"), Now);

        Assert.Equal(new[] { "session_1", "session_2" },
            board.ListedIds()!.OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public void CloudWithNoAccountsCannotSay()
    {
        Assert.Null(new CloudAccountBoard(Array.Empty<CloudAccount>()).ListedIds());
    }

    [Fact]
    public async Task ClaudeCloudSessionsListedIdsFollowsTheBoardAndTheSwitch()
    {
        OrbweaverSettings.ClaudeCloudEnabled = true;
        ClaudeCloudSessions.SetBoardForTests(null);
        Assert.Null(ClaudeCloudSessions.ListedIds());

        var board = new CloudAccountBoard(new[] { Account(RootA) });
        board.Apply(RootA, await GoodStep("session_1"), Now);
        ClaudeCloudSessions.SetBoardForTests(board);

        Assert.Contains("session_1", ClaudeCloudSessions.ListedIds()!);

        OrbweaverSettings.ClaudeCloudEnabled = false;

        Assert.Null(ClaudeCloudSessions.ListedIds());
    }

    // --- Remote Control ------------------------------------------------------

    private static (string Peer, MirrorProtocol.MirrorRosterEntry Entry) Row(
        string peer, string name, string? route = null, string? cluster = null) =>
        (peer, new MirrorProtocol.MirrorRosterEntry(name, MirrorProtocol.CliClaudeCode, true, false,
            Route: route, OpenClawCluster: cluster));

    [Fact]
    public void RemoteControlGroupsEachPeersKeysUnderThePeer()
    {
        var listed = RemoteControlSessions.ListedByPeerFromRoster("work", new[]
        {
            Row("mini", "build", route: "sid:one"),
            Row("mini", "tests", route: "sid:two"),
            Row("windows", "ui"),
        }, Now);

        Assert.Equal(2, listed.Count);
        Assert.Equal(new[] { "rc:work:sid:one", "rc:work:sid:two" },
            listed["mini"].OrderBy(k => k, StringComparer.Ordinal));
        // A row with no route is keyed on its name, the way a Remote's Key is.
        Assert.Equal(new[] { "rc:work:ui" }, listed["windows"]);
        Assert.DoesNotContain("rc:work:sid:nine", listed["mini"]);
    }

    [Fact]
    public void RemoteControlListsARowTheViewersOpenClawSettingsWouldHide()
    {
        var known = new[] { Row("mini", "beat", route: "sid:beat", cluster: "heartbeats") };

        var shown = RemoteControlSessions.ShownByOpenClawSettings(
            known, true, Array.Empty<string>(),
            ClusterMode.Hidden, ClusterMode.WithChats, ClusterMode.WithChats);
        Assert.Empty(shown);

        // Hidden from the screen, still listed by its peer: a setting on this
        // machine is not the session having ended.
        var listed = RemoteControlSessions.ListedByPeerFromRoster("work", known, Now);
        Assert.Contains("rc:work:sid:beat", listed["mini"]);
    }

    [Fact]
    public void RemoteControlAPeerThatListsNothingHasNoEntryAndAnUnclaimedRowIsDropped()
    {
        var listed = RemoteControlSessions.ListedByPeerFromRoster("work", new[]
        {
            Row("", "unclaimed", route: "sid:orphan"),
        }, Now);

        // No peer key to be authoritative about, so nothing is claimed: the
        // caller reads a missing peer as "cannot say".
        Assert.Empty(listed);
    }

    [Fact]
    public void RemoteControlPeerNamesMatchWithoutRegardToCase()
    {
        var listed = RemoteControlSessions.ListedByPeerFromRoster("work",
            new[] { Row("Mini", "build", route: "sid:one") }, Now);

        Assert.True(listed.ContainsKey("mini"));
    }

    [Fact]
    public void RemoteControlListedByPeerIsEmptyUntilALinkPublishesAndWhenTheFeatureIsOff()
    {
        OrbweaverSettings.PeerLinkEnabled = true;
        RemoteControlSessions.ResetForTests();
        Assert.Empty(RemoteControlSessions.ListedByPeer());

        var published = RemoteControlSessions.ListedByPeerFromRoster("work",
            new[] { Row("mini", "build", route: "sid:one") }, Now);
        RemoteControlSessions.SetListedByPeerForTests(published);
        Assert.Contains("rc:work:sid:one", RemoteControlSessions.ListedByPeer()["mini"]);

        // Off means the link is not being read at all: whatever was published
        // before is not an answer.
        OrbweaverSettings.PeerLinkEnabled = false;
        Assert.Empty(RemoteControlSessions.ListedByPeer());
    }

    [Fact]
    public void ResettingRemoteControlForgetsWhatPeersListed()
    {
        OrbweaverSettings.PeerLinkEnabled = true;
        RemoteControlSessions.SetListedByPeerForTests(RemoteControlSessions.ListedByPeerFromRoster("work",
            new[] { Row("mini", "build", route: "sid:one") }, Now));

        RemoteControlSessions.ResetForTests();

        Assert.Empty(RemoteControlSessions.ListedByPeer());
    }
}
