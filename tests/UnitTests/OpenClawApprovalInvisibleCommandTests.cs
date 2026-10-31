using System.IO;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// "No command to show" means no VISIBLE character, not just no character. A command
// made only of characters that draw as nothing (the Hangul fillers, the blank braille
// cell, a lone combining mark, a variation selector, no-break spaces) read as an empty
// "wants to run:" line and still offered Allow once. One helper decides, and the
// record, the prompt and the answer all ask it.
[Collection("Settings")]
public class OpenClawApprovalInvisibleCommandTests : IDisposable
{
    private const string Key = "agent:main:dashboard:00000000-0000-4000-8000-000000000004";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public OpenClawApprovalInvisibleCommandTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
    }

    public void Dispose()
    {
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
    }

    // QA's seven shapes: each draws as nothing.
    private static readonly string[] InvisibleShapes =
    {
        "\u3164",                 // Hangul filler
        "\u115F",                 // Hangul choseong filler
        "\uFFA0",                 // halfwidth Hangul filler
        "\u2800",                 // blank braille cell
        "\u0301",                 // a lone combining acute accent
        "\uFE0F",                 // a lone variation selector
        "\u00A0\u3164\u2800",     // no-break space + filler + blank braille
        "\u1160",                 // Hangul jungseong filler
        " \u3164 \u2800 \uFE0F ", // a mix with ordinary spaces

        // Code points that draw nothing because nothing is assigned to them.
        "\uE000",                 // private use (Co)
        "\uF8FF",                 // private use, the last of the BMP block
        "\uDB80\uDC00",           // U+F0000, plane 15 private use
        "\u0378",                 // unassigned (Cn)
        "\uFFFF",                 // noncharacter
        "\uFFFE",                 // noncharacter
        "\uFDD0",                 // noncharacter
        "\uFDEF",                 // noncharacter
        "\uD83F\uDFFE",           // U+1FFFE, a noncharacter in a supplementary plane
        "\uDBFF\uDFFF",           // U+10FFFF, the last code point
        "\uFFFC",                 // object replacement character
        "\uE000\uFFFF\u0378\uFFFC", // a mix of them
    };

    // Readable commands that contain the same kinds of characters beside a real one.
    private static readonly string[] ReadableShapes =
    {
        "true",
        "cafe\u0301",            // e + combining acute
        "echo \u2764\uFE0F",          // a heart with a variation selector
        "\u3164ls\u3164",             // fillers around a real command
        "\u2800x",                    // blank braille then a letter
        "\uD83D\uDC4D\uD83C\uDFFD",   // an emoji with a skin-tone modifier (surrogate pairs)
        "ls \uE000 -la",              // a private-use glyph beside real text
        "\uE000ls",                   // a private-use glyph first
        "echo \uFFFC done",           // an object replacement character beside real text
        "run\uFFFF",                  // a noncharacter beside real text
        "\uFF21\uFF22",                  // fullwidth letters, in the same block as the filler and the noncharacters
    };

    private static string Request(string command) =>
        "{\"id\":\"a1\",\"approvalKind\":\"exec\",\"request\":{\"command\":" + JsonSerializer.Serialize(command) +
        ",\"sessionKey\":\"" + Key + "\"},\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(10)) + "}";

    private static string Snapshot(string command) =>
        "{\"id\":\"a1\",\"status\":\"pending\",\"presentation\":{\"kind\":\"exec\",\"commandText\":" + JsonSerializer.Serialize(command) +
        ",\"allowedDecisions\":[\"allow-once\",\"deny\"]},\"urlPath\":\"/approve/a1\",\"createdAtMs\":" + Ms(Now) +
        ",\"expiresAtMs\":" + Ms(Now.AddMinutes(10)) + "}";

    // Each of the four feeds, as a function that brings the record into the store.
    private static readonly string[] FeedNames = { "legacy event", "list row", "session pending", "replay" };

    public static TheoryData<string> Invisible()
    {
        var data = new TheoryData<string>();
        foreach (var s in InvisibleShapes) data.Add(s);
        return data;
    }

    public static TheoryData<string> Readable()
    {
        var data = new TheoryData<string>();
        foreach (var s in ReadableShapes) data.Add(s);
        return data;
    }

    private static void Feed(string feed, string command)
    {
        var at = new DateTimeOffset(Now);
        switch (feed)
        {
            case "legacy event":
                OpenClawSessions.OnEvent("exec.approval.requested", Json(Request(command)), Now);
                break;
            case "list row":
                OpenClawSessions.ReplacePending("exec", OpenClawApprovals.ParseList(Json("[" + Request(command) + "]"), "exec", at), at);
                break;
            case "session pending":
                OpenClawSessions.OnEvent("session.approval", Json(
                    "{\"sessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(Now) + ",\"phase\":\"pending\",\"approval\":" + Snapshot(command) + "}"), Now);
                break;
            case "replay":
                var replay = OpenClawApprovals.ParseReplay(Json(
                    "{\"approvalReplay\":{\"sessionKey\":\"" + Key + "\",\"approvals\":[" + Snapshot(command) + "],\"truncated\":false}}"), Key, at)!;
                OpenClawSessions.ApplyReplay(Key, replay, at);
                break;
        }
    }

    private static async Task<FakeGatewaySocket> ConnectedAsync()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read", "operator.approvals" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : request.Method == "approval.resolve"
                    ? FakeGatewaySocket.Ok(request.Id, Json("{\"applied\":true}"))
                    : FakeGatewaySocket.Ok(request.Id, new { });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        Assert.Equal(OpenClawGateway.Outcome.Connected, (await gateway.ConnectAsync(null, CancellationToken.None)).Outcome);
        OpenClawSessions.SetGatewayForTests(gateway);
        return socket;
    }

    [Theory]
    [MemberData(nameof(Invisible))]
    public void TheHelperSaysAnInvisibleStringHasNoVisibleText(string text) =>
        Assert.False(OpenClawApprovals.HasVisibleText(text));

    [Theory]
    [MemberData(nameof(Readable))]
    public void TheHelperSaysAReadableStringHasVisibleText(string text) =>
        Assert.True(OpenClawApprovals.HasVisibleText(text));

    [Fact]
    public void TheHelperHandlesNullAndEmpty()
    {
        Assert.False(OpenClawApprovals.HasVisibleText(null));
        Assert.False(OpenClawApprovals.HasVisibleText(""));
        Assert.False(OpenClawApprovals.HasVisibleText(" \t\r\n"));
    }

    // Every shape through every feed: held, flagged, prompted Deny-only, and an allow
    // refused with nothing sent while a deny still goes out.
    [Fact]
    public async Task AnInvisibleCommandIsDenyOnlyOnEveryFeedInEveryShape()
    {
        var socket = await ConnectedAsync();
        var chat = new OpenClawChatSession("openclaw:" + Key, Key, "Lilibeth");
        var cells = 0;

        foreach (var command in InvisibleShapes)
            foreach (var feed in FeedNames)
            {
                OpenClawSessions.ForgetApprovalsForTests();
                Feed(feed, command);

                var held = OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now)).SingleOrDefault();
                var cell = feed + " / U+" + string.Join("+", command.Select(c => ((int)c).ToString("X4")));
                Assert.True(held is not null, "not held: " + cell);
                Assert.True(held!.NoCommandToShow, "not flagged: " + cell);

                var prompt = chat.Prompt!;
                Assert.True(prompt.Options.Count == 1 && prompt.Options[0].Key == "deny", "Allow offered: " + cell);
                Assert.DoesNotContain("wants to run", prompt.Title);

                var before = socket.Requests.Count(r => r.Method == "approval.resolve");
                var refused = await OpenClawSessions.ResolveApprovalAsync(chat, "a1", "allow-once");
                Assert.True(refused.Result == OpenClawApprovalResult.Failed, "allow not refused: " + cell);
                Assert.True(socket.Requests.Count(r => r.Method == "approval.resolve") == before, "a frame was sent: " + cell);
                cells++;
            }

        Assert.Equal(InvisibleShapes.Length * FeedNames.Length, cells);

        var denied = await OpenClawSessions.ResolveApprovalAsync(chat, "a1", "deny");
        Assert.Equal(OpenClawApprovalResult.Answered, denied.Result);
    }

    // The controls: readable commands, including ones with combining marks, fillers
    // beside real text and emoji with selectors, stay allowed on every feed.
    [Fact]
    public async Task AReadableCommandStaysAllowedOnEveryFeed()
    {
        var socket = await ConnectedAsync();
        var chat = new OpenClawChatSession("openclaw:" + Key, Key, "Lilibeth");

        foreach (var command in ReadableShapes)
            foreach (var feed in FeedNames)
            {
                OpenClawSessions.ForgetApprovalsForTests();
                Feed(feed, command);

                var held = Assert.Single(OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now)));
                Assert.False(held.NoCommandToShow, feed + " / " + command);

                var prompt = chat.Prompt!;
                Assert.Equal(new[] { "deny", "allow-once" }, prompt.Options.Select(o => o.Key));

                var answered = await OpenClawSessions.ResolveApprovalAsync(chat, "a1", "allow-once");
                Assert.Equal(OpenClawApprovalResult.Answered, answered.Result);
            }

        Assert.True(socket.Requests.Count(r => r.Method == "approval.resolve") > 0);
    }
}
