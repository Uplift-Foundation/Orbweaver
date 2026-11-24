using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-404: the desk's half of frames-v13 (the model and effort controls), pinned
/// against the committed frames-v13-vectors.json, whose independent check is
/// Orbweaver-Mobile's check_v13_vectors.py. Every serialise case is written by
/// the shipped RunFrames; every shape case is read by the shipped parser; every
/// set case is decided by the shipped check order; and every sealed box is opened
/// under the epoch-0 key and written again by the desk. The phone's tables
/// (decode, offer, accept, display) are the phones' to prove and are not read.
/// </summary>
public class RelayV13VectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v13-vectors.json"))).RootElement;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static string? Opt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static readonly JsonElement In = V.GetProperty("inputs");
    private static readonly byte[] Key = Convert.FromHexString(Str(In, "session_key_epoch0"));
    private static readonly byte[] CkKey = Convert.FromHexString(Str(In, "ck_key"));
    private static readonly string Sid = Str(In, "session_id");

    private static JsonElement Serialise(string name) =>
        V.GetProperty("serialise").GetProperty("cases").EnumerateArray().First(c => Str(c, "name") == name);

    private static RunFieldState Field(JsonElement f) => new(
        Opt(f, "id"), Opt(f, "pending"),
        f.GetProperty("choices").EnumerateArray().Select(c => new RemoteRunChoice(c[0].GetString()!, c[1].GetString()!)).ToList(),
        Opt(f, "off"), Opt(f, "failed"));

    private static RunState State(JsonElement options) => new(
        Field(options.GetProperty("model")), Field(options.GetProperty("effort")),
        options.TryGetProperty("saves_default", out var d) && d.GetBoolean());

    public static TheoryData<string> SerialiseCases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var c in V.GetProperty("serialise").GetProperty("cases").EnumerateArray()) data.Add(Str(c, "name"));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SerialiseCases))]
    public void Every_serialise_case_is_the_run_frame_the_desk_writes(string name)
    {
        var c = Serialise(name);
        var plaintext = RunFrames.State(Sid, State(c.GetProperty("options")));
        Assert.Equal(Str(c, "plaintext"), plaintext);
        Assert.Equal(c.GetProperty("plaintext_bytes").GetInt32(), Encoding.UTF8.GetByteCount(plaintext));
    }

    [Fact]
    public void The_limits_table_is_the_constants_the_desk_uses()
    {
        var l = V.GetProperty("limits");
        Assert.Equal(
            (l.GetProperty("max_choices").GetInt32(), l.GetProperty("max_id_chars").GetInt32(),
             l.GetProperty("max_label_utf16").GetInt32(), l.GetProperty("max_reason_utf16").GetInt32()),
            (RunFrames.MaxChoices, RunFrames.MaxIdChars, RunFrames.MaxLabel, RunFrames.MaxReason));
    }

    [Fact]
    public void Every_shape_case_is_parsed_to_bad_frame_or_a_request()
    {
        var cases = V.GetProperty("set").GetProperty("shape").GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 19);
        foreach (var c in cases)
        {
            var parsed = RelayRequests.Parse(JsonDocument.Parse(Str(c, "plaintext")).RootElement, "ref");
            if (Opt(c, "err") is null) Assert.IsType<SetRunRequest>(parsed);
            else Assert.Equal(Opt(c, "session_id"), Assert.IsType<BadFrameRequest>(parsed).SessionId);
        }
    }

    // A model desk over the shipped decisions: the session's state from the named
    // serialise case, the host's busy flag, and what the route answered.
    private sealed class ModelDesk(RunState? state, bool running, JsonElement route) : IRunControls
    {
        public RunState? Read(SessionStatus status) => state;
        public bool IsRunning(SessionStatus status) => running;

        public Task<RunSetResult> SetAsync(SessionStatus status, string field, string value) =>
            Task.FromResult(route.ValueKind == JsonValueKind.String && route.GetString() != "ok"
                ? new RunSetResult(RunSetKind.Failed, route.GetString())
                : RunSetResult.Sent);
    }

    private sealed class Replies : IRemoteSender
    {
        public readonly List<(string? Session, string Plain)> Sent = [];
        public void Reply(string phoneId, string? sessionId, string plaintext) => Sent.Add((sessionId, plaintext));
        public void Window(string phoneId, string sessionId, string plaintext) { }
        public bool ImageWindow(string phoneId, string hash, string plaintext) => false;
        public void Run(string phoneId, string sessionId, string plaintext) { }
    }

    // The one reply the desk's RelayRemote sends for a set case, and the session it names.
    private static (string? Session, string Plain) Reply(JsonElement c)
    {
        var state = c.GetProperty("state").ValueKind == JsonValueKind.Null ? null : State(Serialise(Str(c, "state")).GetProperty("options"));
        var running = c.TryGetProperty("running", out var r) && r.GetBoolean();
        var sent = new Replies();
        var remote = new RelayRemote(new RelayRemote.Seams(
            () => DateTimeOffset.UnixEpoch, _ => Task.CompletedTask, _ => throw new InvalidOperationException(),
            null!, null!, _ => { })
        {
            RunControls = new ModelDesk(state, running, c.GetProperty("route")),
        }, sent, new NonceWindow());
        if (state is not null)
            remote.OnStatuses([(Sid, new SessionStatus { SessionId = Sid, Source = SessionSource.ClaudeCode, Cwd = "/w" })]);

        var q = c.GetProperty("request");
        remote.HandleAsync("ph", [], new SetRunRequest(Str(c, "envelope_id"), Str(q, "session_id"), Str(q, "field"), Str(q, "value")))
            .GetAwaiter().GetResult();
        return sent.Sent.Single();
    }

    [Fact]
    public void Every_set_case_is_the_one_reply_the_desk_sends()
    {
        var cases = V.GetProperty("set").GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 17);
        foreach (var c in cases)
        {
            var (session, plain) = Reply(c);
            // Structurally here; the sealed boxes below pin the bytes.
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(c.GetProperty("reply").GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(plain)), Str(c, "name") + ": " + plain);
            Assert.Equal(Sid, session);
        }
    }

    [Fact]
    public void The_busy_sentence_is_the_desks()
    {
        Assert.Equal(Str(V.GetProperty("set"), "busy_why"), RunFrames.BusyWhy);
    }

    // ---- the sealed boxes -------------------------------------------------------------------

    private static void AssertBox(JsonElement box, uint direction, string expectedPlaintext, string expectedCk)
    {
        Assert.Equal(expectedPlaintext, Str(box, "plaintext"));
        var aad = Convert.FromHexString(Str(box, "aad"));
        Assert.Equal(Aad(Str(box, "from"), Str(box, "to"), Str(box, "class")), aad);
        Assert.Equal(expectedPlaintext, Encoding.UTF8.GetString(Open(Key, Str(box, "ct"), aad, direction)!));
        Assert.Equal(Str(box, "ct"), Seal(Key, Convert.FromHexString(Str(box, "nonce")), Encoding.UTF8.GetBytes(expectedPlaintext), aad));
        Assert.Equal(expectedCk, Str(box, "ck"));
    }

    [Fact]
    public void Caps_name_run_options_and_a_list_dir_only_desk_is_v11s_bytes()
    {
        Assert.Equal(Str(In, "ck_caps"), Ck(CkKey, BrowseFrames.CapsCk));
        var sealedBoxes = V.GetProperty("sealed");
        // v13's bytes are a list_dir and run_options desk; CB-434's v17 caps add resume (RelayV17 tests).
        AssertBox(sealedBoxes.GetProperty("caps"), DesktopToPhone, BrowseFrames.CapsFrame("list_dir", RunFrames.Cap), Str(In, "ck_caps"));
        AssertBox(sealedBoxes.GetProperty("caps_v11"), DesktopToPhone, BrowseFrames.CapsFrame("list_dir"), Str(In, "ck_caps"));
    }

    [Fact]
    public void Every_sealed_run_frame_is_the_desks_bytes_on_the_run_ck()
    {
        Assert.Equal(Str(In, "ck_run"), Ck(CkKey, RunFrames.CkFor(Sid)));
        var runs = V.GetProperty("sealed").GetProperty("run").EnumerateObject().ToList();
        Assert.True(runs.Count >= 10);
        foreach (var run in runs)
            AssertBox(run.Value, DesktopToPhone, RunFrames.State(Sid, State(Serialise(run.Name).GetProperty("options"))), Str(In, "ck_run"));
    }

    [Fact]
    public void Every_sealed_request_opens_and_every_sealed_reply_is_what_the_desk_writes()
    {
        var setCases = V.GetProperty("set").GetProperty("cases").EnumerateArray().ToList();
        foreach (var (name, box) in V.GetProperty("sealed").EnumerateObject()
                     .Where(p => p.Name is not ("caps" or "caps_v11" or "run")).Select(p => (p.Name, p.Value)))
        {
            var reference = Str(box, "envelope_id");
            string expected;
            string ck;
            if (name.StartsWith("bad_frame"))
            {
                var parsed = Assert.IsType<BadFrameRequest>(RelayRequests.Parse(
                    JsonDocument.Parse(Str(box, "_request_plaintext")).RootElement, reference));
                expected = RelayReplies.Err(reference, RelayReplies.BadFrame, parsed.SessionId);
                ck = parsed.SessionId is null ? "" : Str(In, "ck_session");
            }
            else
            {
                var c = setCases.First(x => Str(x, "name") == name);
                AssertBox(box.GetProperty("request"), PhoneToDesktop, Str(box.GetProperty("request"), "plaintext"), "");
                expected = Reply(c).Plain;
                ck = Str(In, "ck_session");
            }
            AssertBox(box.GetProperty("reply"), DesktopToPhone, expected, ck);
        }
    }
}
