using Xunit;

namespace Orbweaver.Tests;

// SessionPark.SaysParked over a session record — the rule that decides whether
// a window has handed its conversation to a background job and so should not be
// wearing an orb.
//
// The fixture is a real record, captured from the machine the duplicate "Mc"
// orbs were photographed on (CLI 2.1.251), with the user's paths and the
// session's title scrubbed and nothing else changed. It is the record of the
// *parent* — an interactive session, idle in a tmux pane, whose conversation
// had just been forked into a background job by opening agents mode. Nothing
// about it looks wrong: `kind` is still "interactive", `status` is still
// "idle", and the transcript it points at ends with a normally completed turn.
// The single field that says what happened is `parkedJobId`, which is the whole
// reason this rule reads the record rather than the transcript.
public class SessionParkTests
{
    // Verbatim but for cwd and name. Note `bridgeSessionId: null` beside a
    // populated `parkedJobId`: parking flushes the Remote Control bridge at the
    // same instant it writes this field, so a record in this state genuinely
    // carries both, and neither one may be read as cancelling the other.
    private const string Parked =
        @"{""pid"":70580,""sessionId"":""746496c9-d663-42fc-96bd-92a67b843f48"",""cwd"":""/Users/w/project"",""startedAt"":1787939084339,""procStart"":""Fri Aug 28 17:44:44 2026"",""version"":""2.1.251"",""peerProtocol"":1,""peerFeatures"":[""notify_idle"",""reply_across_default_dirs"",""artifact_yield""],""kind"":""interactive"",""entrypoint"":""cli"",""pidDomain"":""darwin"",""tmux"":""1:@16.%41"",""messagingSocketPath"":""/tmp/cc-socks/70580.sock"",""name"":""unmerged-branches"",""nameSource"":""user"",""nameSince"":1787942557221,""status"":""idle"",""updatedAt"":1787972787545,""statusUpdatedAt"":1787972543188,""bridgeSessionId"":null,""formerNames"":[],""parkedJobId"":""e4f5c5e4""}";

    // The same record as it reads before the fork, and again after the user
    // comes back out of the agents view — Claude Code clears the field rather
    // than setting it to anything, so "not parked" is a field that isn't there.
    private const string NotParked =
        @"{""pid"":70580,""sessionId"":""746496c9-d663-42fc-96bd-92a67b843f48"",""cwd"":""/Users/w/project"",""kind"":""interactive"",""name"":""unmerged-branches"",""status"":""idle"",""bridgeSessionId"":null}";

    private const string Owner = "746496c9-d663-42fc-96bd-92a67b843f48";

    [Fact]
    public void TheRealParkedRecordReadsAsParked()
    {
        Assert.True(SessionPark.SaysParked(Parked, Owner));
    }

    [Fact]
    public void TheSameRecordWithoutTheFieldReadsAsLive()
    {
        // The orb must come back the moment the user returns to the window,
        // which is the only thing that clears the field.
        Assert.False(SessionPark.SaysParked(NotParked, Owner));
    }

    // The reason the rule is two conditions rather than one. Records are keyed
    // by pid and pids are reused: a parked session that exits leaves this file
    // behind, and until something else is given that number and overwrites it,
    // the record names one session while a different one asks about it. A rule
    // that trusted `parkedJobId` alone would take the orb off the innocent
    // session — the one failure direction this whole file is written to avoid.
    [Fact]
    public void ARecordNamingADifferentSessionParksNothing()
    {
        Assert.False(SessionPark.SaysParked(Parked, "00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void ARecordWithNoSessionIdParksNothing()
    {
        const string anonymous =
            @"{""pid"":70580,""kind"":""interactive"",""parkedJobId"":""e4f5c5e4""}";

        Assert.False(SessionPark.SaysParked(anonymous, Owner));
    }

    // An empty string is not a job id, and a rule that accepted one would hide
    // an orb on the strength of a field somebody cleared badly.
    [Fact]
    public void AnEmptyJobIdParksNothing()
    {
        const string blank =
            @"{""sessionId"":""" + Owner + @""",""parkedJobId"":""""}";

        Assert.False(SessionPark.SaysParked(blank, Owner));
    }

    // JSON null is how a field can be present and mean nothing, and it is what
    // `bridgeSessionId` is in the real fixture above — so a record really does
    // arrive with a null where a string might have been.
    [Theory]
    [InlineData(@"{""sessionId"":""746496c9-d663-42fc-96bd-92a67b843f48"",""parkedJobId"":null}")]
    [InlineData(@"{""sessionId"":""746496c9-d663-42fc-96bd-92a67b843f48"",""parkedJobId"":42}")]
    [InlineData(@"{""sessionId"":""746496c9-d663-42fc-96bd-92a67b843f48"",""parkedJobId"":true}")]
    [InlineData(@"{""sessionId"":""746496c9-d663-42fc-96bd-92a67b843f48""}")]
    public void AFieldThatIsNotAJobIdParksNothing(string json)
    {
        Assert.False(SessionPark.SaysParked(json, Owner));
    }

    // A record caught mid-write is the ordinary case, not an exotic one: these
    // files are rewritten by a live session while the scan is reading them.
    // Every unreadable shape has to answer "leave the orb alone".
    [Theory]
    [InlineData(@"{""sessionId"":""746")]
    [InlineData(@"[{""parkedJobId"":""e4f5c5e4""}]")]
    [InlineData(@"""just a string""")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingReadableParksNothing(string? json)
    {
        Assert.False(SessionPark.SaysParked(json, Owner));
    }

    [Fact]
    public void AnUnnamedSessionParksNothing()
    {
        // Asking "is *nobody* parked" has no useful true answer, and the guard
        // keeps the sessionId comparison below from matching an empty field
        // against an empty argument.
        Assert.False(SessionPark.SaysParked(Parked, ""));
    }

    // The I/O half's cheap rejections, which never touch the disk. A pid of
    // zero is what a status file written before the hook learned to record one
    // still carries, and it must not send the walk looking for "0.json".
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APidThatIsNotAProcessIsNeverParked(int pid)
    {
        Assert.False(SessionPark.IsParked(pid, Owner));
    }

    [Fact]
    public void AnUnnamedSessionIsNeverParkedOnDisk()
    {
        Assert.False(SessionPark.IsParked(70580, ""));
    }

    // --- SaysStatus (CB-307 addendum) -----------------------------------------

    // The record Claude Code writes while its TUI sits on a modal dialog: the
    // shape read off a real session on 2026-11-06, trimmed to the fields that
    // matter. `status` is the only word acted on, and only when it is `waiting`.
    private static string WithStatus(string sessionId, string status) =>
        "{\"pid\":32273,\"sessionId\":\"" + sessionId + "\",\"kind\":\"interactive\",\"status\":\"" + status + "\"}";

    [Theory]
    [InlineData("waiting")]
    [InlineData("busy")]
    [InlineData("idle")]
    public void TheRecordsStatusIsReadBackWhenTheRecordIsThisSessions(string status)
    {
        Assert.Equal(status, SessionPark.SaysStatus(WithStatus(Owner, status), Owner));
    }

    // Pids are reused. A recycled record must be able to fail to assert, never
    // to assert about the wrong session — the same guard SaysParked has.
    [Fact]
    public void ARecordBelongingToAnotherSessionSaysNothing()
    {
        Assert.Null(SessionPark.SaysStatus(WithStatus("someone-else", "waiting"), Owner));
    }

    [Fact]
    public void ARecordWithNoSessionIdSaysNothing()
    {
        Assert.Null(SessionPark.SaysStatus(@"{""status"":""waiting""}", Owner));
        Assert.Null(SessionPark.SaysStatus(@"{""sessionId"":7,""status"":""waiting""}", Owner));
    }

    [Fact]
    public void ARecordWithNoStatusOrANonStringOneSaysNothing()
    {
        Assert.Null(SessionPark.SaysStatus($@"{{""sessionId"":""{Owner}""}}", Owner));
        Assert.Null(SessionPark.SaysStatus($@"{{""sessionId"":""{Owner}"",""status"":3}}", Owner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"{""sessionId"":""746")]
    [InlineData("[1,2]")]
    public void NothingReadableSaysNothing(string? json)
    {
        Assert.Null(SessionPark.SaysStatus(json, Owner));
    }

    [Fact]
    public void AnUnnamedAskerHearsNothing()
    {
        Assert.Null(SessionPark.SaysStatus(WithStatus(Owner, "waiting"), ""));
    }

    // SaysParked and SaysStatus read the same record independently: a parked
    // record's status is still reported, and a waiting one is not parked.
    [Fact]
    public void ParkedAndStatusAreIndependentQuestionsOfOneRecord()
    {
        Assert.True(SessionPark.SaysParked(Parked, Owner));
        Assert.Equal("idle", SessionPark.SaysStatus(Parked, Owner));
        Assert.False(SessionPark.SaysParked(WithStatus(Owner, "waiting"), Owner));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordStatusOfANonProcessIsNull(int pid)
    {
        Assert.Null(SessionPark.RecordStatus(pid, Owner));
    }

    [Fact]
    public void RecordStatusOfAnUnnamedSessionIsNull()
    {
        Assert.Null(SessionPark.RecordStatus(70580, ""));
    }
}
