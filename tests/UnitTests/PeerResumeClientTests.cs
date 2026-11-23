using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

/// <summary>
/// CB-434: PeerLaunchClient as the window's IRemotePastSessions — the page it
/// asks for, how a far answer (or none) becomes the window's outcome, and what
/// a resume launch carries on the wire. Plus launch-info's `resume`.
/// </summary>
public class PeerResumeClientTests
{
    private const string Id = "0f5b1c2e-6a8d-4c11-9d3e-2b7a5f8e1c40";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public readonly List<(string Machine, PeerProtocol.PeerMessage Message)> Sent = new();
        public bool SendResult = true;
        public Action<PeerProtocol.PeerMessage>? OnSend;
        public PeerLaunchWire.LaunchRequest? Signed;
        public readonly PeerLaunchClient Client;

        public Rig(TimeSpan? wait = null)
        {
            Client = new PeerLaunchClient(new PeerLaunchClient.Seams(
                (m, msg) => { Sent.Add((m, msg)); OnSend?.Invoke(msg); return Task.FromResult(SendResult); },
                () => [new PeerMachine("mini", true)],
                (_, unsigned) => { Signed = unsigned; return Task.FromResult<(string, string)?>(("SIG", "CERT")); },
                () => Now)
            {
                ListWait = wait ?? TimeSpan.FromSeconds(10),
                LaunchWait = wait ?? TimeSpan.FromSeconds(10),
                NewNonce = () => "NONCE",
            });
        }

        public void Answers(string type, JsonElement body, string from = "mini") =>
            OnSend = msg => Client.OnReply(from, PeerProtocol.Message(type, msg.Id, body: body));
    }

    private static JsonElement Page(params PeerPastSession[] rows) =>
        PastSessionsWire.EncodePage(new PeerPastSessionsResult(true, "ok", rows.Length + 5, 0, rows, ["/w"]), "claude");

    // ---- the list ------------------------------------------------------------------

    [Fact]
    public async Task The_window_asks_for_the_newest_2000_and_gets_the_far_rows_as_its_own()
    {
        var rig = new Rig();
        rig.Answers(PeerProtocol.PastSessions, Page(
            new PeerPastSession(Id, "/w", "Fix the feed", 1794000000, 12, false, true),
            new PeerPastSession("b2", null, "No folder", 1793000000, null, true)));

        var list = await rig.Client.ListAsync("mini", NewChatCli.Codex, ".codex-b", true, CancellationToken.None);

        var query = PastSessionsWire.ParseQuery(rig.Sent.Single().Message.Body!.Value)!;
        Assert.Equal(new PastSessionQuery("codex", ".codex-b", true, "", "", 0, PeerLaunchClient.WindowLimit), query);
        Assert.Equal(PeerProtocol.PastSessionsGet, rig.Sent.Single().Message.Type);

        Assert.Equal(RemoteListOutcome.Ok, list.Outcome);
        Assert.Equal(7, list.Total);
        var first = list.Rows[0];
        Assert.Equal(new PastSession(NewChatCli.Codex, Id, "/w", "Fix the feed", DateTimeOffset.FromUnixTimeSeconds(1794000000), 12, "", false), first.Session);
        Assert.True(first.IsLive);
        Assert.True(list.Rows[1].Session.FolderMissing);
        Assert.False(list.Rows[1].IsLive);
    }

    [Fact]
    public async Task A_far_refusal_is_unreachable_with_a_short_reason()
    {
        var rig = new Rig();
        rig.Answers(PeerProtocol.PastSessions, PastSessionsWire.EncodePage(PeerPastSessionsResult.Fail(PeerLaunchCodes.PeerLaunchOff), ""));

        var list = await rig.Client.ListAsync("mini", NewChatCli.ClaudeCode, "default", false, CancellationToken.None);

        Assert.Equal(new RemoteListResult(RemoteListOutcome.Unreachable, [], "it doesn't allow other desktops to start sessions"),
            list with { Rows = [] });
    }

    [Fact]
    public async Task No_answer_a_lost_link_and_an_unreadable_reply_are_each_a_failed_page()
    {
        var silent = new Rig(TimeSpan.FromMilliseconds(50));
        Assert.Equal(PeerLaunchCodes.NoAnswer, (await silent.Client.PastSessionsAsync("mini", new("claude", "default", false, "", "", 0), CancellationToken.None)).Code);

        var unsent = new Rig { SendResult = false };
        Assert.Equal(PeerLaunchCodes.LinkLost, (await unsent.Client.PastSessionsAsync("mini", new("claude", "default", false, "", "", 0), CancellationToken.None)).Code);

        var dropped = new Rig();
        dropped.OnSend = _ => dropped.Client.OnDisconnected("mini");
        Assert.Equal(PeerLaunchCodes.LinkLost, (await dropped.Client.PastSessionsAsync("mini", new("claude", "default", false, "", "", 0), CancellationToken.None)).Code);

        var garbled = new Rig();
        garbled.Answers(PeerProtocol.PastSessions, JsonDocument.Parse("{\"ok\":true}").RootElement);
        Assert.Equal(PeerLaunchCodes.NoAnswer, (await garbled.Client.PastSessionsAsync("mini", new("claude", "default", false, "", "", 0), CancellationToken.None)).Code);
    }

    [Theory]
    [InlineData(PeerLaunchCodes.NoAnswer, null)]
    [InlineData(PeerLaunchCodes.LinkLost, "the link dropped")]
    [InlineData(RelayReplies.Unreadable, RelayReplies.Unreadable)]
    public void List_reasons_are_short_enough_for_the_windows_sentence(string code, string? why) =>
        Assert.Equal(why, PeerLaunchClient.ListWhy(code));

    // ---- resume --------------------------------------------------------------------

    [Fact]
    public async Task A_resume_is_a_signed_launch_carrying_the_id_and_the_rows_folder_that_cannot_ask_the_trust_question()
    {
        var rig = new Rig();
        rig.Answers(PeerProtocol.LaunchResult, PeerLaunchWire.EncodeResult(new PeerLaunchResult(true, PeerLaunchCodes.Launched, Id, null)));

        var result = await rig.Client.ResumeAsync("mini", NewChatCli.Grok, Id, "/w", "default", CancellationToken.None);

        Assert.Equal(new RemoteResumeResult(RemoteResumeOutcome.Launched, null, false, Id), result);
        Assert.Equal((Id, "/w", "grok", "default", false), (rig.Signed!.ResumeSessionId, rig.Signed.Cwd, rig.Signed.Cli, rig.Signed.Account, rig.Signed.CanTrust));
        var sent = PeerLaunchWire.ParseLaunch(rig.Sent.Single().Message.Body!.Value)!;
        Assert.Equal((Id, "SIG"), (sent.ResumeSessionId, sent.Sig));
    }

    [Fact]
    public async Task A_new_chat_still_carries_no_resume_id_and_can_ask_the_trust_question()
    {
        var rig = new Rig();
        rig.Answers(PeerProtocol.LaunchResult, PeerLaunchWire.EncodeResult(new PeerLaunchResult(true, PeerLaunchCodes.Launched, "s", null)));

        await rig.Client.LaunchAsync("mini", new PeerLaunchSpec("claude", "/w", "default"), CancellationToken.None);

        Assert.Null(rig.Signed!.ResumeSessionId);
        Assert.True(rig.Signed.CanTrust);
        Assert.DoesNotContain("resume_session_id", rig.Sent.Single().Message.Body!.Value.GetRawText());
    }

    private static readonly (string Code, bool Ok, string? Why, RemoteResumeResult Expected)[] Answers =
    [
        (PeerLaunchCodes.Launched, true, null, new(RemoteResumeOutcome.Launched, null, false, Id)),
        (PeerLaunchCodes.ResumeStarted, true, null, new(RemoteResumeOutcome.Launched, null, true, Id)),
        (RelayReplies.AlreadyRunning, true, null, new(RemoteResumeOutcome.AlreadyRunning, null, false, Id)),
        (RelayReplies.ResumeUnsupported, false, "Too old.", new(RemoteResumeOutcome.Unsupported, "Too old.", false)),
        (RelayReplies.UnknownResume, false, null, new(RemoteResumeOutcome.Failed, "mini no longer has that session in that folder.", false)),
        (PeerLaunchCodes.NoAnswer, false, null, new(RemoteResumeOutcome.Unreachable, null, false)),
        (PeerLaunchCodes.LinkLost, false, null, new(RemoteResumeOutcome.Unreachable, "the link dropped", false)),
        (RelayReplies.UnknownCwd, false, null, new(RemoteResumeOutcome.Failed, PeerLaunchCopy.Explain(RelayReplies.UnknownCwd, null, "claude", "mini"), false)),
        // An ok flag the code does not go with is not believed.
        (PeerLaunchCodes.Launched, false, null, new(RemoteResumeOutcome.Failed, PeerLaunchCopy.Explain(PeerLaunchCodes.Launched, null, "claude", "mini"), false)),
    ];

    [Fact]
    public void Every_far_answer_maps_to_the_windows_outcome()
    {
        foreach (var (code, ok, why, expected) in Answers)
            Assert.Equal(expected, PeerLaunchClient.Resumed(new PeerLaunchResult(ok, code, ok ? Id : null, why), NewChatCli.ClaudeCode, "mini"));
    }

    // ---- launch-info ---------------------------------------------------------------

    [Fact]
    public void Launch_info_says_resume_only_when_the_desk_allows_launching_and_the_wire_keeps_it()
    {
        PeerLaunchInfoSource Source(bool allowed) => new(new PeerLaunchInfoSource.Seams(
            () => allowed, () => "macos", () => "/Users/dev", () => true, _ => true, () => [], () => []));

        var on = Source(true).Build();
        Assert.Equal(PastSessionsRemote.ResumeVersion, on.Resume);
        Assert.Equal(PastSessionsRemote.ResumeVersion, PeerLaunchWire.ParseInfo(PeerLaunchWire.EncodeInfo(on))!.Resume);

        var off = Source(false).Build();
        Assert.Equal(0, off.Resume);
        Assert.DoesNotContain("resume", PeerLaunchWire.EncodeInfo(off).GetRawText());
        // An older desk's info has no field at all: 0, so the window says to update it.
        Assert.Equal(0, PeerLaunchWire.ParseInfo(JsonDocument.Parse("{\"allowed\":true,\"os\":\"macos\"}").RootElement)!.Resume);
    }
}
