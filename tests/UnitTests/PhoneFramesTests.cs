using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>CB-316: PhoneFrames and the diff rule against frames-v4-vectors.json (window section), the bytes the iPhone and Android clients also pin.</summary>
public class PhoneFramesTests
{
    private static readonly JsonElement W = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v4-vectors.json"))).RootElement.GetProperty("window");

    private static PhoneFrames.Line Row(JsonElement e)
    {
        var (text, cut) = PhoneFrames.CutText(e.GetProperty("text").GetString()!);
        return new PhoneFrames.Line(e.GetProperty("role").GetString()!, text, e.GetProperty("at").GetInt64(),
            e.TryGetProperty("error", out var er) && er.GetBoolean(), cut || (e.TryGetProperty("cut", out var c) && c.GetBoolean()));
    }

    private static List<PhoneFrames.Line> Input() => W.GetProperty("lines_input").EnumerateArray().Select(Row).ToList();

    private static string[] Plain(string key) => W.GetProperty(key).EnumerateArray().Select(f => f.GetProperty("plaintext").GetString()!).ToArray();

    [Fact]
    public void BackfillIsTheLastTwoHundredLinesChunkedUnder48000Bytes()
    {
        var lines = Input();
        var windows = PhoneFrames.Windows("0f6c4f2a", 30, 230, live: false, reset: false, lines.Skip(30).ToList());
        Assert.Equal(Plain("backfill"), windows);
        Assert.All(windows, w => Assert.True(Encoding.UTF8.GetByteCount(w) <= PhoneFrames.MaxWindowBytes));
    }

    [Fact]
    public void ThePageBefore30IsOneWindowFromLineZero() =>
        Assert.Equal(Plain("page_before_30"), PhoneFrames.Windows("0f6c4f2a", 0, 230, false, false, Input().Take(30).ToList()));

    [Fact]
    public void ATailWindowIsLive() =>
        Assert.Equal(Plain("tail"), PhoneFrames.Windows("0f6c4f2a", 230, 233, true, false,
            W.GetProperty("tail_appended_input").EnumerateArray().Select(Row).ToList()));

    [Fact]
    public void AResetIsMarkedAndStartsAtZero() =>
        Assert.Equal(Plain("reset_to_5"), PhoneFrames.Windows("0f6c4f2a", 0, 5, false, true, Input().Take(5).ToList()));

    [Fact]
    public void ALineOverTheCapIsCutOnAScalarBoundary()
    {
        foreach (var i in W.GetProperty("cut_lines").EnumerateArray().Select(x => x.GetInt32()))
        {
            var (text, cut) = PhoneFrames.CutText(W.GetProperty("lines_input")[i].GetProperty("text").GetString()!);
            Assert.True(cut);
            Assert.InRange(Encoding.UTF8.GetByteCount(text), 7997, 8000);
            Assert.EndsWith("…", text);
        }
        Assert.False(PhoneFrames.CutText("short").Cut);
    }

    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        var (text, cut) = PhoneFrames.CutText(new string('a', 7996) + "\U0001F642" + "tail");
        Assert.True(cut);
        Assert.Equal(new string('a', 7996) + "…", text);
    }

    public static TheoryData<string> DiffCases()
    {
        var d = new TheoryData<string>();
        foreach (var c in W.GetProperty("diff_rule").GetProperty("cases").EnumerateArray()) d.Add(c.GetProperty("name").GetString()!);
        return d;
    }

    [Theory, MemberData(nameof(DiffCases))]
    public void TheDiffRuleMatchesTheVectors(string name)
    {
        var c = W.GetProperty("diff_rule").GetProperty("cases").EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        var prev = c.GetProperty("prev").EnumerateArray().Select(Row).ToList();
        var next = c.GetProperty("new").EnumerateArray().Select(Row).ToList();
        var got = PhoneService.Diff(prev, next);
        var expect = c.GetProperty("expect").GetString();
        Assert.Equal(expect, got.Kind.ToString().ToLowerInvariant());
        if (expect == "tail")
        {
            Assert.Equal(c.GetProperty("from_line").GetInt32(), got.Start);
            Assert.Equal(c.GetProperty("line_count").GetInt32(), next.Count - got.Start);
        }
    }

    [Fact]
    public void OkAndErrKeepTheSpecsKeyOrder()
    {
        Assert.Equal("{\"t\":\"ok\",\"ref\":\"r\",\"code\":\"watching\",\"session_id\":\"s\",\"total\":4}", PhoneFrames.Ok("r", "watching", "s", 4));
        Assert.Equal("{\"t\":\"ok\",\"ref\":\"r\",\"code\":\"typed\",\"session_id\":\"s\"}", PhoneFrames.Ok("r", "typed", "s"));
        Assert.Equal("{\"t\":\"err\",\"ref\":\"r\",\"code\":\"not_at_prompt\",\"session_id\":\"s\",\"why\":\"w\\\"x\"}", PhoneFrames.Err("r", "not_at_prompt", "s", "w\"x"));
        Assert.Equal("{\"t\":\"err\",\"ref\":\"r\",\"code\":\"bad_frame\"}", PhoneFrames.Err("r", "bad_frame"));
    }

    [Fact]
    public void ATurnBecomesAWireLine()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1794124800);
        var line = PhoneFrames.FromTurn(new ChatTurn { Role = ChatRole.System, Text = "Bash failed", At = at, IsError = true });
        Assert.Equal(new PhoneFrames.Line("system", "Bash failed", 1794124800, true, false), line);
        Assert.Equal("assistant", PhoneFrames.FromTurn(new ChatTurn { Role = ChatRole.Assistant, Text = "x", At = at }).Role);
        Assert.Equal("user", PhoneFrames.FromTurn(new ChatTurn { Role = ChatRole.User, Text = "x", At = at }).Role);
    }
}
