using Orbweaver;
using Xunit;

namespace Orbweaver.IntegrationTests;

// The liveness rule against a real file, read through the real tail window.
//
// The unit tests cover the rule; this covers the seam, which fails
// differently. What the tail read decides is *which lines the rule ever
// sees* — and the failure this guards is specific and was the actual risk
// when the rule was written: a session parked at a prompt keeps accruing
// untimestamped bookkeeping rows, and if enough of them pile up after the
// last turn, a window sized for rendering a conversation would push that turn
// out of view. The rule would then be handed nothing, fail open, and the
// abandoned orb would come back — with every test still green.
public class AbandonedSessionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cb-abandoned-" + Guid.NewGuid().ToString("N")[..8]);

    public AbandonedSessionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Turn(string role, string at) =>
        $$"""{"type":"{{role}}","timestamp":"{{at}}","text":"hello"}""";

    // What Remote Control's bridge appends to a session it is attached to.
    private static string Bridge(int n) =>
        $$"""{"type":"bridge-session","sessionId":"dc6b769b","seq":{{n}}}""";

    private static DateTime? LastTurnOf(string path) =>
        SessionLiveness.LastTurnAt(
            TranscriptReader.TailLines(path, LastTurnCache.TailBytes));

    [Fact]
    public void TheLastTurnIsFoundUnderAPileOfBridgeHousekeeping()
    {
        // The abandoned session on the mini had 174 bridge rows. A thousand is
        // comfortably past that and still well inside the window.
        var lines = new List<string>
        {
            Turn("user", "2026-08-29T04:24:05.027Z"),
            Turn("assistant", "2026-08-29T04:24:08.729Z"),
        };
        for (var i = 0; i < 1000; i++) lines.Add(Bridge(i));

        var path = Write("parked.jsonl", lines);

        Assert.Equal(
            new DateTime(2026, 8, 29, 4, 24, 8, 729, DateTimeKind.Utc),
            LastTurnOf(path));
    }

    [Fact]
    public void AndTheAnswerIsThatItShouldNotBeShown()
    {
        var lines = new List<string> { Turn("assistant", "2026-08-29T04:24:08.729Z") };
        for (var i = 0; i < 1000; i++) lines.Add(Bridge(i));

        var path = Write("parked-verdict.jsonl", lines);

        Assert.False(!SessionLiveness.IsParked(
            "idle", LastTurnOf(path), new DateTime(2026, 8, 31, 3, 7, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ALiveConversationIsKeptEvenThoughItReportsItselfIdle()
    {
        // The other half of the real pair, and the case CB-74's heartbeat
        // filter got wrong: mid-conversation, and `idle` at this instant
        // because the model had just finished answering.
        var now = DateTime.UtcNow;
        var path = Write("live.jsonl", new[]
        {
            Turn("user", now.AddSeconds(-16).ToString("o")),
            Turn("assistant", now.AddSeconds(-8).ToString("o")),
            Bridge(0),
        });

        Assert.True(!SessionLiveness.IsParked("idle", LastTurnOf(path), now));
    }

    [Fact]
    public void ATranscriptTooBigForTheWindowStillAnswersFromWhatItCanSee()
    {
        // Past the tail window the read starts mid-file and drops its first
        // torn line. The turn here sits near the end, which is where a live
        // session's turn always is — the point is that a multi-megabyte file
        // does not become unreadable.
        var lines = new List<string>();
        var filler = new string('x', 900);
        for (var i = 0; i < 2000; i++)
            lines.Add($$"""{"type":"attachment","seq":{{i}},"pad":"{{filler}}"}""");
        lines.Add(Turn("assistant", "2026-08-31T03:07:07.499Z"));

        var path = Write("big.jsonl", lines);

        Assert.True(new FileInfo(path).Length > LastTurnCache.TailBytes);
        Assert.Equal(
            new DateTime(2026, 8, 31, 3, 7, 7, 499, DateTimeKind.Utc),
            LastTurnOf(path));
    }

    [Fact]
    public void AFileThatIsNotThereIsShownRatherThanHidden()
    {
        // TailLines swallows the IO and returns nothing, and nothing means
        // "cannot tell" — which must not read as "abandoned".
        var missing = Path.Combine(_dir, "no-such-file.jsonl");

        Assert.Null(LastTurnOf(missing));
        Assert.True(!SessionLiveness.IsParked("idle", LastTurnOf(missing), DateTime.UtcNow));
    }

    [Fact]
    public void AnEmptyTranscriptIsShownRatherThanHidden()
    {
        // A session that has just started has a file and no turns in it yet.
        // Hiding it would mean an orb that appears only after the first reply.
        var path = Write("empty.jsonl", Array.Empty<string>());

        Assert.Null(LastTurnOf(path));
        Assert.True(!SessionLiveness.IsParked("idle", LastTurnOf(path), DateTime.UtcNow));
    }
}
