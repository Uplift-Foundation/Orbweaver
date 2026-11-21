using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-397: the far desk's trust question and the signed answer to it, in the
/// order PeerLaunchServer documents. The pane is a fake: no terminal starts.
/// </summary>
public class PeerLaunchServerTrustTests
{
    private const string Folder = TrustScreens.Folder;
    private static readonly X509Certificate2 Sender = Cert("mbp");
    private static readonly X509Certificate2 Other = Cert("other");
    private static readonly string OwnPin = new('b', 64);

    private static X509Certificate2 Cert(string name)
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static readonly string OnNo = TrustScreens.OnNo;
    private static readonly string OnYes = TrustScreens.OnYes;
    private const string Idle = "$ ";

    private sealed class World
    {
        public readonly LaunchRunnerTests.Desk Desk = new() { TrustOn = true };
        public string Screen = OnNo;
        public bool Enabled = true;
        public readonly List<string> Log = [];
        public readonly Dictionary<string, PeerIdentity.Peer> Paired = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mbp"] = new(PeerIdentity.PinOf(Sender), "mbp"),
            ["other"] = new(PeerIdentity.PinOf(Other), "other"),
        };
        public TaskCompletionSource Expiry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly PeerLaunchServer Server;
        private int _nonce;

        public World()
        {
            Desk.Screen = _ => Screen;
            Desk.OnPress = key => { if (key == "Down") Screen = OnYes; };
            Server = new PeerLaunchServer(new PeerLaunchServer.Seams(
                () => Enabled,
                m => Paired.GetValueOrDefault(m),
                () => OwnPin,
                () => Desk.Sessions,
                Desk.Launch(),
                Desk.Accounts(),
                () => Desk.Now,
                Desk.Delay,
                Log.Add)
            { TrustDelay = _ => Expiry.Task });
        }

        public string NextNonce() => RelayCrypto.Base64Url(SHA256.HashData(BitConverter.GetBytes(++_nonce)).AsSpan(0, 16).ToArray());

        public JsonElement Launch(bool canTrust = true, string? nonce = null, X509Certificate2? by = null)
        {
            var f = new LaunchFields(Folder, "claude", "", "", "", "", nonce ?? NextNonce(), Desk.Now.ToUnixTimeSeconds() + 120, "default");
            var signed = PeerIdentity.Sign(by ?? Sender, LaunchGate.PeerMessage(OwnPin, LaunchGate.Body(f)));
            return PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest(
                f.Cwd, f.Cli, f.Persona, f.Model, f.Account, f.ParentSessionId, f.FirstMessage, f.Nonce, f.ExpiresAt,
                RelayCrypto.Base64Url(signed.Sig), Convert.ToBase64String(signed.CertificateDer), canTrust));
        }

        public async Task<(PeerLaunchResult Result, string LaunchNonce)> Ask(string machine = "mbp")
        {
            var nonce = NextNonce();
            return (await Server.HandleAsync(machine, Launch(nonce: nonce)), nonce);
        }

        public JsonElement Answer(PeerLaunchTrust trust, string launchNonce, string answer = "trust", X509Certificate2? by = null,
            string? sentAnswer = null, long? expires = null, string? nonce = null, string? cert = null, string? token = null)
        {
            nonce ??= NextNonce();
            var exp = expires ?? Desk.Now.ToUnixTimeSeconds() + 120;
            var fields = new PeerTrustAnswer.Fields(trust.Token, answer, Folder, "claude", "default", launchNonce, nonce, exp);
            var signed = PeerIdentity.Sign(by ?? Sender, PeerTrustAnswer.Message(OwnPin, PeerTrustAnswer.Body(fields)));
            return PeerLaunchWire.EncodeTrust(new PeerLaunchWire.TrustRequest(
                token ?? trust.Token, sentAnswer ?? answer, nonce, exp,
                RelayCrypto.Base64Url(signed.Sig), cert ?? Convert.ToBase64String(signed.CertificateDer)));
        }
    }

    private static void Refused(PeerLaunchResult r, string code)
    {
        Assert.False(r.Ok);
        Assert.Equal(code, r.Code);
        Assert.Null(r.SessionId);
    }

    [Fact]
    public async Task A_launch_that_can_show_the_question_gets_trust_needed_with_a_token_and_holds_one_record()
    {
        var w = new World();
        var (r, _) = await w.Ask();

        Refused(r, PeerLaunchCodes.TrustNeeded);
        Assert.Equal("claude", r.Trust!.Cli);
        Assert.Equal(Folder, r.Trust.Cwd);
        Assert.Equal(w.Desk.Now.ToUnixTimeSeconds() + 120, r.Trust.ExpiresAt);
        Assert.Equal(22, r.Trust.Token.Length);
        Assert.False(r.Trust.Hooks);
        Assert.Equal(1, w.Server.PendingCount);
        Assert.Contains($"peer-launch mbp trust_needed pane=%1", w.Log);
        Assert.DoesNotContain(w.Log, l => l.Contains("proj"));
        Assert.Empty(w.Desk.Presses);
    }

    [Fact]
    public async Task A_launch_that_cannot_show_it_is_told_to_update_and_the_pane_is_closed_at_once()
    {
        var w = new World();
        var r = await w.Server.HandleAsync("mbp", w.Launch(canTrust: false));

        Refused(r, RelayReplies.LaunchFailed);
        Assert.Equal("Claude Code is asking whether to trust that folder. Update this machine to answer from here.", r.Why);
        Assert.Equal(["%1"], w.Desk.Closed);
        Assert.Equal(0, w.Server.PendingCount);
    }

    [Fact]
    public async Task Trust_presses_down_then_enter_and_names_the_session_that_checks_in()
    {
        var w = new World();
        var (asked, nonce) = await w.Ask();
        var polls = 0;
        w.Desk.OnDelay = () => { if (++polls == 1) w.Desk.Sessions = [("new", LaunchRunnerTests.Status(cwd: Folder))]; };

        var r = await w.Server.HandleTrustAsync("mbp", w.Answer(asked.Trust!, nonce));

        Assert.Equal(new PeerLaunchResult(true, PeerLaunchCodes.Launched, "new", null), r);
        Assert.Equal(["Down", "Enter"], w.Desk.Presses);
        Assert.Equal(0, w.Server.PendingCount);
        Assert.Empty(w.Desk.Closed);
    }

    [Fact]
    public async Task Cancel_closes_the_pane_only_while_it_still_shows_the_prompt()
    {
        var w = new World();
        var (asked, nonce) = await w.Ask();
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(asked.Trust!, nonce, "cancel")), PeerLaunchCodes.TrustCancelled);
        Assert.Equal(["%1"], w.Desk.Closed);
        Assert.Empty(w.Desk.Presses);

        // Somebody answered it at that desk: the screen is theirs now.
        var (again, nonce2) = await w.Ask();
        w.Screen = Idle;
        w.Desk.Closed.Clear();
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(again.Trust!, nonce2, "cancel")), PeerLaunchCodes.TrustCancelled);
        Assert.Empty(w.Desk.Closed);
    }

    [Fact]
    public async Task A_trust_answer_that_cannot_be_applied_arrives_as_launch_failed_with_why()
    {
        var w = new World();
        var (asked, nonce) = await w.Ask();
        w.Screen = Idle;
        var r = await w.Server.HandleTrustAsync("mbp", w.Answer(asked.Trust!, nonce));
        Refused(r, RelayReplies.LaunchFailed);
        Assert.Equal("The trust question was no longer on screen.", r.Why);
        Assert.Empty(w.Desk.Presses);
    }

    [Fact]
    public async Task Every_check_has_its_own_code_in_order()
    {
        var w = new World();
        var (asked, nonce) = await w.Ask();
        var trust = asked.Trust!;

        // Whole frame.
        Refused(await w.Server.HandleTrustAsync("mbp", JsonDocument.Parse("42").RootElement), RelayReplies.BadFrame);
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce, sentAnswer: "maybe")), RelayReplies.BadFrame);
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce, cert: "not base64!")), RelayReplies.BadFrame);

        // A token this machine does not own: a second paired machine with a valid signature of its own.
        Refused(await w.Server.HandleTrustAsync("other", w.Answer(trust, nonce, by: Other)), PeerLaunchCodes.TrustGone);
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce, token: "nope")), PeerLaunchCodes.TrustGone);

        // The signature binds the answer: flipping it, signing as somebody else, or a stranger's certificate.
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce, "cancel", sentAnswer: "trust")), RelayReplies.BadSignature);
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce, by: Other)), RelayReplies.BadSignature);
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, "wrong-launch-nonce")), RelayReplies.BadSignature);
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce, nonce: "short")), RelayReplies.BadSignature);

        // Expiry, with the gate's own rule.
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce, expires: w.Desk.Now.ToUnixTimeSeconds() - 1)), RelayReplies.Expired);
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce, expires: w.Desk.Now.ToUnixTimeSeconds() + 100000)), RelayReplies.Expired);

        // None of those took the record.
        Assert.Equal(1, w.Server.PendingCount);
        Assert.Empty(w.Desk.Presses);

        w.Enabled = false;
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(trust, nonce)), PeerLaunchCodes.PeerLaunchOff);
    }

    [Fact]
    public async Task A_spent_nonce_is_replayed_and_a_taken_record_is_gone()
    {
        var w = new World();
        var (first, n1) = await w.Ask();
        var spent = w.NextNonce();
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(first.Trust!, n1, "cancel", nonce: spent)), PeerLaunchCodes.TrustCancelled);

        // Single use: the same answer again finds no question.
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(first.Trust!, n1, "cancel", nonce: spent)), PeerLaunchCodes.TrustGone);

        // A new question, answered with the nonce that was already spent.
        var (second, n2) = await w.Ask();
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(second.Trust!, n2, "cancel", nonce: spent)), RelayReplies.Replayed);
        Assert.Equal(1, w.Server.PendingCount);
    }

    [Fact]
    public async Task A_second_launch_from_the_same_machine_closes_the_older_question()
    {
        var w = new World();
        var (first, _) = await w.Ask();
        var (second, _) = await w.Ask();

        Assert.NotEqual(first.Trust!.Token, second.Trust!.Token);
        Assert.Equal(1, w.Server.PendingCount);
        Assert.Equal(["%1"], w.Desk.Closed);
        Assert.Contains("peer-launch mbp trust_replaced pane=%1", w.Log);
    }

    [Fact]
    public async Task Expiry_closes_the_pane_under_the_guard_and_the_answer_is_then_gone()
    {
        var w = new World();
        var (asked, nonce) = await w.Ask();

        w.Expiry.SetResult();
        await Task.Yield();
        SpinWait.SpinUntil(() => w.Server.PendingCount == 0, TimeSpan.FromSeconds(5));

        Assert.Equal(0, w.Server.PendingCount);
        Assert.Equal(["%1"], w.Desk.Closed);
        Assert.Contains("peer-launch mbp trust_expired pane=%1", w.Log);
        Refused(await w.Server.HandleTrustAsync("mbp", w.Answer(asked.Trust!, nonce)), PeerLaunchCodes.TrustGone);
    }

    // The timer firing after an answer took the record leaves the pane alone.
    [Fact]
    public async Task An_expiry_after_an_answer_does_nothing()
    {
        var w = new World();
        var (asked, nonce) = await w.Ask();
        await w.Server.HandleTrustAsync("mbp", w.Answer(asked.Trust!, nonce, "cancel"));
        w.Desk.Closed.Clear();

        w.Expiry.SetResult();
        await Task.Delay(50);
        Assert.Empty(w.Desk.Closed);
    }

    [Fact]
    public async Task A_dropped_link_closes_that_machines_question_and_nobody_elses()
    {
        var w = new World();
        await w.Ask();

        await w.Server.OnDisconnectedAsync("other");
        Assert.Equal(1, w.Server.PendingCount);
        Assert.Empty(w.Desk.Closed);

        await w.Server.OnDisconnectedAsync("mbp");
        Assert.Equal(0, w.Server.PendingCount);
        Assert.Equal(["%1"], w.Desk.Closed);
        Assert.Contains("peer-launch mbp trust_dropped pane=%1", w.Log);
    }

    [Fact]
    public async Task Without_a_trust_delay_the_runners_own_delay_times_the_question()
    {
        var w = new World();
        var server = new PeerLaunchServer(new PeerLaunchServer.Seams(
            () => true, m => w.Paired.GetValueOrDefault(m), () => OwnPin, () => w.Desk.Sessions,
            w.Desk.Launch(), w.Desk.Accounts(), () => w.Desk.Now, w.Desk.Delay, w.Log.Add));
        var r = await server.HandleAsync("mbp", w.Launch());
        Assert.Equal(PeerLaunchCodes.TrustNeeded, r.Code);
        // The default delay completes at once, so the question was expired and closed straight away.
        SpinWait.SpinUntil(() => server.PendingCount == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.PendingCount);
    }

    // Display only: the hooks warning on the far screen rides in the question and is not signed.
    [Fact]
    public async Task The_hooks_warning_rides_in_the_question_and_is_not_part_of_the_signature()
    {
        var w = new World { Screen = TrustScreens.HooksOnNo };
        var f = new LaunchFields(TrustScreens.HooksFolder, "claude", "", "", "", "", w.NextNonce(), w.Desk.Now.ToUnixTimeSeconds() + 120, "default");
        var signed = PeerIdentity.Sign(Sender, LaunchGate.PeerMessage(OwnPin, LaunchGate.Body(f)));
        var body = PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest(f.Cwd, f.Cli, "", "", f.Account, "", "", f.Nonce, f.ExpiresAt,
            RelayCrypto.Base64Url(signed.Sig), Convert.ToBase64String(signed.CertificateDer), true));

        var r = await w.Server.HandleAsync("mbp", body);

        Assert.Equal(PeerLaunchCodes.TrustNeeded, r.Code);
        Assert.True(r.Trust!.Hooks);
        Assert.True(PeerLaunchWire.ParseResult(PeerLaunchWire.EncodeResult(r))!.Trust!.Hooks);
        Assert.DoesNotContain("hooks", PeerTrustAnswer.Body(new PeerTrustAnswer.Fields("t", "trust", "/w", "claude", "default", "n", "m", 1)));
    }
}
