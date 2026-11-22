using System;
using System.Collections.Generic;
using Xunit;

namespace Orbweaver.Tests;

// CB-413: a session absent from a successfully read `claude agents --json`
// listing is not necessarily over. The daemon re-points a job to a newer
// conversation that shares its worker, so the listing names the old session id
// and not the live one beside it — and the live one lost its orb.
//
// The fixture is the real row shape off the mini: job b1375106, pid 85260,
// listed under session d7b7db77, while b1375106-f6f2-... runs in the same
// process and is not named anywhere.
public class AbsentLiveSessionTests
{
    private const string Listing = """
    [
      {
        "pid": 85260,
        "id": "b1375106",
        "kind": "background",
        "sessionId": "d7b7db77-0000-4000-8000-000000000000",
        "state": "working",
        "status": "busy"
      },
      {
        "pid": 85301,
        "id": "aaaa1111",
        "kind": "background",
        "sessionId": "e0e0e0e0-0000-4000-8000-000000000000",
        "state": "done",
        "status": "idle"
      },
      {
        "pid": 85302,
        "id": "bbbb2222",
        "kind": "background",
        "sessionId": "f1f1f1f1-0000-4000-8000-000000000000",
        "state": "blocked",
        "status": "idle"
      }
    ]
    """;

    private const string Unlisted = "b1375106-f6f2-4a11-9c3d-5e5e5e5e5e5e";
    private const string Listed = "d7b7db77-0000-4000-8000-000000000000";
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static Dictionary<string, string>? Parsed => BackgroundJobs.Parse(Listing);

    private static JobPhase Ask(string id, int pid, TimeSpan age, bool running, Dictionary<string, string>? states = null)
        => BackgroundJobs.Phase(states ?? Parsed, id, pid, Now - age, Now, _ => running);

    [Fact]
    public void AbsentButAliveAndFresh_IsUnknown_NotAJob()
        => Assert.Equal(JobPhase.Unknown, Ask(Unlisted, 85260, TimeSpan.FromSeconds(30), true));

    [Fact]
    public void AbsentAndProcessDead_StaysNotAJob()
        => Assert.Equal(JobPhase.NotAJob, Ask(Unlisted, 85260, TimeSpan.FromSeconds(30), false));

    [Fact]
    public void AbsentAliveButStale_StaysNotAJob()
        => Assert.Equal(JobPhase.NotAJob, Ask(Unlisted, 85260, BackgroundJobs.AbsentGrace + TimeSpan.FromSeconds(1), true));

    [Fact]
    public void AbsentWithNoRecordedPid_KeepsTheListingsWord()
        => Assert.Equal(JobPhase.NotAJob, Ask(Unlisted, 0, TimeSpan.FromSeconds(30), true));

    [Fact]
    public void DoneStaysDone_EvenWhenAliveAndFresh()
        => Assert.Equal(JobPhase.Done, Ask("e0e0e0e0-0000-4000-8000-000000000000", 85301, TimeSpan.Zero, true));

    [Fact]
    public void ListedRowsAreUnchanged()
    {
        Assert.Equal(JobPhase.Working, Ask(Listed, 85260, TimeSpan.Zero, true));
        Assert.Equal(JobPhase.Parked, Ask("f1f1f1f1-0000-4000-8000-000000000000", 85302, TimeSpan.Zero, true));
    }

    [Fact]
    public void UnreadableListing_IsStillUnknown()
        => Assert.Equal(JobPhase.Unknown, BackgroundJobs.Phase(null, Unlisted, 85260, Now, Now, _ => false));

    [Fact]
    public void LivenessIsOnlyAskedAboutASessionTheListingDidNotName()
    {
        var asked = false;
        BackgroundJobs.Phase(Parsed, Listed, 85260, Now, Now, _ => { asked = true; return true; });
        Assert.False(asked);
    }

    // The scan-level shape: two live sessions, one process, one job row. The
    // unlisted one must not be dropped for having no terminal.
    [Fact]
    public void TwoSessionsOneJob_UnlistedLiveOneIsNotRuledOut()
    {
        var phase = Ask(Unlisted, 85260, TimeSpan.FromSeconds(5), true);
        Assert.False(SessionPresence.RuledOutAsAJob(phase));
        Assert.True(SessionPresence.RuledOutAsAJob(Ask(Unlisted, 85260, TimeSpan.FromSeconds(5), false)));
    }
}
