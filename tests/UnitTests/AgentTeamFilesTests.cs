using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// AgentTeamFiles: a session's team, read from its transcript and team config
// for the sessions that have no pid to ask about — a Claude Code running in
// WSL, whose status file records session_pid 0.
//
// The fixtures are shaped from a real run, not remembered: a member's
// transcript head and a team's config.json captured from a WSL session on
// 2026-10-28, with the long lines cut down. Two facts they pin are the whole
// reason the fix works. The first user entry names the team and the agent, and
// the entry after it is enormous (it carries the member's CLAUDE.md), which is
// why only the head of the file is ever read. And a lead's own transcript
// carries neither field.
public class AgentTeamFilesTests
{
    private const string LeadId = "0a1b2c3d-1111-4222-8333-444455556666";

    private const string MemberHead =
        "{\"type\":\"mode\",\"mode\":\"normal\",\"sessionId\":\"5e6f7a8b-9c0d-4e1f-8a2b-3c4d5e6f7a8b\"}\n" +
        "{\"type\":\"permission-mode\",\"permissionMode\":\"auto\",\"sessionId\":\"5e6f7a8b-9c0d-4e1f-8a2b-3c4d5e6f7a8b\"}\n" +
        "{\"parentUuid\":null,\"isSidechain\":false,\"teamName\":\"session-0a1b2c3d\",\"agentName\":\"probe_live\",\"type\":\"user\"," +
        "\"message\":{\"role\":\"user\",\"content\":\"<teammate-message teammate_id=\\\"team-lead\\\">\\nRun sleep 40\\n</teammate-message>\"}}\n" +
        "{\"parentUuid\":\"eebef7be\",\"isSidechain\":false,\"teamName\":\"session-0a1b2c3d\",\"agentName\":\"probe_live\",\"attachment\":{\"type\":\"instructions\"";

    private const string LeadHead =
        "{\"type\":\"mode\",\"mode\":\"normal\",\"sessionId\":\"" + LeadId + "\"}\n" +
        "{\"type\":\"permission-mode\",\"permissionMode\":\"auto\",\"sessionId\":\"" + LeadId + "\"}\n" +
        "{\"parentUuid\":null,\"isSidechain\":false,\"promptId\":\"72a12745\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n";

    private const string TeamConfig = @"{
  ""name"": ""session-0a1b2c3d"",
  ""leadAgentId"": ""team-lead@session-0a1b2c3d"",
  ""leadSessionId"": ""0a1b2c3d-1111-4222-8333-444455556666"",
  ""members"": [
    { ""agentId"": ""team-lead@session-0a1b2c3d"", ""name"": ""team-lead"", ""agentType"": ""team-lead"", ""backendType"": ""in-process"" },
    { ""agentId"": ""probe_live@session-0a1b2c3d"", ""name"": ""probe_live"", ""color"": ""orange"", ""backendType"": ""tmux"" }
  ]
}";

    // --- IdentityIn ---------------------------------------------------------------

    [Fact]
    public void AMemberIsNamedByTheFirstEntryThatCarriesBothFields()
    {
        var who = AgentTeamFiles.IdentityIn(MemberHead);

        Assert.Equal("session-0a1b2c3d", who.Team);
        Assert.Equal("probe_live", who.Agent);
    }

    [Fact]
    public void ALeadsTranscriptNamesNobody()
    {
        // Negative control for the case above, and the property the arrow
        // depends on: the lead reads as "not a member", as it does by process.
        Assert.Equal(AgentTeamFiles.Nobody, AgentTeamFiles.IdentityIn(LeadHead));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingReadIsNobody(string? head)
    {
        Assert.Equal(AgentTeamFiles.Nobody, AgentTeamFiles.IdentityIn(head));
    }

    [Fact]
    public void AnEntryNamingOnlyOneOfTheTwoIsNotBelieved()
    {
        // A team with no member name cannot be looked up in its config, and a
        // name with no team cannot be placed.
        const string head =
            "{\"teamName\":\"session-0a1b2c3d\",\"type\":\"user\"}\n" +
            "{\"agentName\":\"probe_live\",\"type\":\"user\"}\n";

        Assert.Equal(AgentTeamFiles.Nobody, AgentTeamFiles.IdentityIn(head));
    }

    [Fact]
    public void ADamagedOrCutOffLineIsSkippedAndTheNextOneIsRead()
    {
        // HeadBytes cuts the file mid-line, so the last line is routinely not
        // JSON; a damaged one in the middle must not stop the read either.
        const string head =
            "{\"teamName\":\"session-0a1b2c3d\",\"agentNa\n" +
            "not json at all\n" +
            "[1,2,3]\n" +
            "{\"type\":\"user\"}\n" +
            "{\"teamName\":\"session-0a1b2c3d\",\"agentName\":\"probe_live\"}\n";

        Assert.Equal(new AgentTeamFiles.Identity("session-0a1b2c3d", "probe_live"), AgentTeamFiles.IdentityIn(head));
    }

    // --- ConfigPath ---------------------------------------------------------------

    [Theory]
    [InlineData("/home/user/.claude-uplift/projects/-home-user-project/5e6f7a8b.jsonl",
        "/home/user/.claude-uplift/teams/session-0a1b2c3d/config.json")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\.claude\projects\-home-user\5e6f7a8b.jsonl",
        @"\\wsl.localhost\Ubuntu\home\user\.claude\teams\session-0a1b2c3d\config.json")]
    [InlineData(@"C:\Users\user\.claude\PROJECTS\C--Users-user\5e6f7a8b.jsonl",
        @"C:\Users\user\.claude\teams\session-0a1b2c3d\config.json")]
    public void TheConfigIsBesideTheProjectsFolderInTheSeparatorTheTranscriptUsed(string transcript, string expected)
    {
        Assert.Equal(expected, AgentTeamFiles.ConfigPath(transcript, "session-0a1b2c3d"));
    }

    [Theory]
    [InlineData("/home/user/.claude/notprojects/-home-user/5e6f7a8b.jsonl")]   // not under a projects folder
    [InlineData("/projects/p/x.jsonl")]                                          // nothing above projects
    [InlineData("5e6f7a8b.jsonl")]                                               // no directory at all
    [InlineData("")]
    [InlineData(null)]
    public void ATranscriptNotShapedLikeOneGivesNoConfigPath(string? transcript)
    {
        Assert.Null(AgentTeamFiles.ConfigPath(transcript, "session-0a1b2c3d"));
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData(null)]
    public void ATeamNameThatCouldLeaveTheTeamsFolderGivesNoConfigPath(string? team)
    {
        // The team name is the one part of the path a process chooses.
        Assert.Null(AgentTeamFiles.ConfigPath("/home/user/.claude/projects/p/x.jsonl", team));
    }

    // --- MembershipIn -------------------------------------------------------------

    [Fact]
    public void AMemberGetsItsLeadFromTheConfigAndItsColourFromItsOwnEntry()
    {
        var membership = AgentTeamFiles.MembershipIn(TeamConfig, "probe_live");

        Assert.Equal(new AgentTeam.Membership(LeadId, "orange", "probe_live"), membership);
    }

    [Fact]
    public void AMemberTheConfigDoesNotListStillHasItsLeadButNoColour()
    {
        // The transcript says it is on this team, and the config says who leads
        // the team, so the arrow can be drawn; only the colour is unknown.
        var membership = AgentTeamFiles.MembershipIn(TeamConfig, "someone_else");

        Assert.Equal(new AgentTeam.Membership(LeadId, "", "someone_else"), membership);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"members\":[]}")]                           // no lead named
    [InlineData("{\"leadSessionId\":\"has space\"}")]          // a lead that is not a session id
    [InlineData("{\"leadSessionId\":42}")]                     // not a string
    public void AConfigThatNamesNoUsableLeadIsNotATeam(string? json)
    {
        Assert.Equal(AgentTeam.None, AgentTeamFiles.MembershipIn(json, "probe_live"));
    }

    [Fact]
    public void NoAgentNameIsNotATeam()
    {
        Assert.Equal(AgentTeam.None, AgentTeamFiles.MembershipIn(TeamConfig, ""));
        Assert.Equal(AgentTeam.None, AgentTeamFiles.MembershipIn(TeamConfig, null));
    }

    [Fact]
    public void OddMembersAndAnUnusableColourAreIgnoredNotFatal()
    {
        // Members that are not objects are skipped, a members value that is not
        // an array is no colour, and a colour that fails Sanitize is dropped —
        // the same treatment a colour off a command line gets.
        var json = "{\"leadSessionId\":\"" + LeadId + "\",\"members\":[7,{\"name\":\"probe_live\",\"color\":\"or ange\"}]}";
        Assert.Equal(new AgentTeam.Membership(LeadId, "", "probe_live"), AgentTeamFiles.MembershipIn(json, "probe_live"));

        var notAnArray = "{\"leadSessionId\":\"" + LeadId + "\",\"members\":{\"name\":\"probe_live\",\"color\":\"red\"}}";
        Assert.Equal(new AgentTeam.Membership(LeadId, "", "probe_live"), AgentTeamFiles.MembershipIn(notAnArray, "probe_live"));
    }

    // --- Locate -------------------------------------------------------------------

    private static readonly IReadOnlyList<string> Distros = new[] { "Ubuntu", "Debian" };

    [Fact]
    public void ALinuxPathOnWindowsIsOpenedThroughTheDistroThatHasIt()
    {
        var opened = AgentTeamFiles.Locate(
            "/home/user/.claude/projects/p/x.jsonl", onWindows: true, Distros,
            exists: path => path == @"\\wsl.localhost\Debian\home\user\.claude\projects\p\x.jsonl");

        Assert.Equal(@"\\wsl.localhost\Debian\home\user\.claude\projects\p\x.jsonl", opened);
    }

    [Fact]
    public void TheOlderWslDollarSpellingIsTriedWhenTheLocalhostOneIsNotThere()
    {
        var opened = AgentTeamFiles.Locate(
            "/home/user/x.jsonl", onWindows: true, Distros,
            exists: path => path == @"\\wsl$\Ubuntu\home\user\x.jsonl");

        Assert.Equal(@"\\wsl$\Ubuntu\home\user\x.jsonl", opened);
    }

    [Fact]
    public void ALinuxPathNoDistroHasCannotBeOpenedFromWindows()
    {
        Assert.Null(AgentTeamFiles.Locate("/home/user/x.jsonl", onWindows: true, Distros, exists: _ => false));
        Assert.Null(AgentTeamFiles.Locate("/home/user/x.jsonl", onWindows: true, Array.Empty<string>(), exists: _ => true));
    }

    [Theory]
    [InlineData(@"C:\Users\user\.claude\projects\p\x.jsonl", true)]    // already a Windows path
    [InlineData("/Users/user/.claude/projects/p/x.jsonl", false)]       // macOS: a Linux-shaped path is native
    [InlineData("", true)]
    public void APathThatIsAlreadyReadableIsReturnedAsItIs(string transcript, bool onWindows)
    {
        Assert.Equal(transcript, AgentTeamFiles.Locate(
            transcript, onWindows, Distros, exists: _ => throw new InvalidOperationException("must not look")));
    }

    // --- Reader -------------------------------------------------------------------

    private const string Transcript = "/home/user/.claude/projects/p/5e6f7a8b.jsonl";
    private const string ConfigAt = "/home/user/.claude/teams/session-0a1b2c3d/config.json";

    // A reader over in-memory files that counts what it is asked, on a clock the
    // test moves.
    private sealed class Rig
    {
        public Dictionary<string, string> Files { get; } = new()
        {
            [Transcript] = MemberHead,
            ["/home/user/.claude/projects/p/lead.jsonl"] = LeadHead,
            [ConfigAt] = TeamConfig,
        };

        public int Reads;
        public long Now = 1_000;
        public Func<string, string?> Locate = path => path;

        public AgentTeamFiles.Reader Reader { get; }

        public Rig()
        {
            string? Read(string path)
            {
                Reads++;
                return Files.GetValueOrDefault(path);
            }

            Reader = new AgentTeamFiles.Reader(Read, Read, path => Locate(path), () => Now);
        }

        public IReadOnlyDictionary<string, AgentTeam.Membership> Ask(params (string Id, string Path)[] sessions) =>
            Reader.ReadMany(sessions);
    }

    [Fact]
    public void AMemberIsFoundFromItsFilesAndAnythingElseIsLeftOut()
    {
        var rig = new Rig();

        var found = rig.Ask(("member", Transcript), ("lead", "/home/user/.claude/projects/p/lead.jsonl"));

        var only = Assert.Single(found);
        Assert.Equal("member", only.Key);
        Assert.Equal(new AgentTeam.Membership(LeadId, "orange", "probe_live"), only.Value);
    }

    [Fact]
    public void AFoundMembershipIsKeptAndNotReadAgain()
    {
        var rig = new Rig();
        rig.Ask(("member", Transcript));
        var readsAfterFirst = rig.Reads;

        rig.Now += 10 * 60_000;
        var again = rig.Ask(("member", Transcript));

        Assert.Equal(readsAfterFirst, rig.Reads);
        Assert.Equal(LeadId, again["member"].Lead);
    }

    [Fact]
    public void ASessionThatIsNotAMemberIsRecheckedOnlyAfterTheInterval()
    {
        // "Not yet" and "never" look the same from outside: a teammate whose
        // config has not landed yet must get its arrow once it has, without the
        // negative answer being cached for good.
        var rig = new Rig();
        rig.Files.Remove(ConfigAt);

        Assert.Empty(rig.Ask(("member", Transcript)));
        var readsAfterFirst = rig.Reads;

        rig.Now += AgentTeamFiles.Reader.NotAMemberRecheckMs - 1;
        Assert.Empty(rig.Ask(("member", Transcript)));
        Assert.Equal(readsAfterFirst, rig.Reads);

        rig.Files[ConfigAt] = TeamConfig;
        rig.Now += 1;
        var later = rig.Ask(("member", Transcript));

        Assert.True(rig.Reads > readsAfterFirst);
        Assert.Equal(LeadId, later["member"].Lead);
    }

    [Theory]
    [InlineData("unlocatable")]     // the path cannot be opened from here
    [InlineData("no-identity")]     // the transcript names no team
    [InlineData("no-config-path")]  // a transcript not under a projects folder
    public void EveryWayTheFilesCanFailToAnswerIsNotATeam(string failure)
    {
        var rig = new Rig();
        var path = Transcript;

        switch (failure)
        {
            case "unlocatable":
                rig.Locate = _ => null;
                break;
            case "no-identity":
                rig.Files[Transcript] = LeadHead;
                break;
            case "no-config-path":
                path = "/home/user/.claude/elsewhere/5e6f7a8b.jsonl";
                rig.Files[path] = MemberHead;
                break;
        }

        Assert.Empty(rig.Ask(("member", path)));
    }

    [Fact]
    public void ASessionWithNoTranscriptPathAndARepeatedIdAreAskedAboutOnce()
    {
        var rig = new Rig();

        var found = rig.Ask(("member", Transcript), ("member", Transcript), ("blank", ""));

        Assert.Single(found);
        Assert.Equal(2, rig.Reads);   // the head, then the config — once, not three times each
    }

    [Fact]
    public void TheCacheDoesNotGrowForever()
    {
        // Found memberships are kept for as long as they are wanted, so only
        // pruning can drop one — which is what makes them the right thing to
        // test it with (a negative would also expire by its own interval).
        // 300 members go past the 256 bound; once they are old enough, the next
        // insertion drops them, and asking for one again reads its files again.
        var rig = new Rig();
        for (var i = 0; i < 300; i++)
        {
            var path = $"/home/user/.claude/projects/p/s{i}.jsonl";
            rig.Files[path] = MemberHead;
            Assert.Single(rig.Ask(($"s{i}", path)));
        }

        var beforeAging = rig.Reads;
        rig.Ask(("s0", "/home/user/.claude/projects/p/s0.jsonl"));
        Assert.Equal(beforeAging, rig.Reads);   // still cached, however many there are

        rig.Now += AgentTeamFiles.Reader.NotAMemberRecheckMs * 5;
        rig.Ask(("fresh", "/home/user/.claude/projects/p/lead.jsonl"));

        var reads = rig.Reads;
        Assert.Single(rig.Ask(("s0", "/home/user/.claude/projects/p/s0.jsonl")));
        Assert.True(rig.Reads > reads, "an old entry should have been pruned and so read again");
    }
}
