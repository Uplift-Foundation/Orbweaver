using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-434: resume_session_id through the shared gate — the signed body, the
/// cross-replay negatives both ways for both signers, the gate's own two
/// resume rules, both parsers, the argv each CLI is handed, and the one id
/// rule. Every refusal sits next to the control that passes.
/// </summary>
public class ResumeGateTests
{
    private const string Id = "0f5b1c2e-6a8d-4c11-9d3e-2b7a5f8e1c40";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
    private static readonly RelayCrypto.KeyPair Phone = RelayCrypto.GenerateKeyPair();

    private static LaunchFields F(string resume = "", string account = "", string parent = "", string model = "", string message = "") =>
        new("/w", "claude", "", model, parent, message, RelayCrypto.Base64Url(new byte[16]), Now.ToUnixTimeSeconds() + 120, account, resume);

    private static string PhoneSig(LaunchFields f) =>
        RelayCrypto.Base64Url(RelayCrypto.Sign(Phone, LaunchGate.Message(LaunchGate.Body(f))));

    private static LaunchGate.Environment Env(bool inRoster = false) => new(false, _ => true, _ => true, _ => inRoster);

    private static string? Decide(LaunchFields f, string sig) =>
        LaunchGate.Decide(new LaunchRequest("r", f, sig), "phone", Phone.Public, Now, new NonceWindow(), Env());

    // ---- the signed body -----------------------------------------------------------

    [Fact]
    public void A_launch_that_does_not_resume_signs_exactly_the_bytes_it_always_did()
    {
        var body = LaunchGate.Body(F());

        Assert.DoesNotContain("resume_session_id", body);
        Assert.Equal(LaunchGate.Body(F() with { ResumeSessionId = "" }), body);
    }

    [Fact]
    public void The_resume_id_is_signed_after_parent_session_id_and_after_account()
    {
        var body = LaunchGate.Body(F(resume: Id, account: ".claude-b"));

        var keys = JsonDocument.Parse(body).RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["cwd", "cli", "persona", "model", "account", "parent_session_id", "resume_session_id",
            "first_message_sha256", "nonce", "expires_at"], keys);
        Assert.Contains("\"resume_session_id\":\"" + Id + "\"", body);
    }

    [Fact]
    public void A_signed_resume_passes_the_gate() => Assert.Null(Decide(F(resume: Id), PhoneSig(F(resume: Id))));

    // An older desk rebuilds the body without the field: a resume signature
    // can never start a fresh session there, and the reverse holds too.
    [Fact]
    public void A_resume_signature_does_not_verify_as_a_plain_launch() =>
        Assert.Equal(RelayReplies.BadSignature, Decide(F(), PhoneSig(F(resume: Id))));

    [Fact]
    public void A_plain_launch_signature_does_not_verify_with_a_resume_id_added() =>
        Assert.Equal(RelayReplies.BadSignature, Decide(F(resume: Id), PhoneSig(F())));

    [Fact]
    public void An_altered_resume_id_does_not_verify() =>
        Assert.Equal(RelayReplies.BadSignature, Decide(F(resume: Id.Replace('0', '1')), PhoneSig(F(resume: Id))));

    [Fact]
    public void The_peer_signer_holds_the_same_cross_replay_rule()
    {
        using var key = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=a", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        var own = new string('b', 64);
        var signer = new LaunchGate.PeerSigner(PeerIdentity.PinOf(cert), own, cert.RawData);
        string Sig(LaunchFields f) => RelayCrypto.Base64Url(PeerIdentity.Sign(cert, LaunchGate.PeerMessage(own, LaunchGate.Body(f))).Sig);

        Assert.True(LaunchGate.Verify(signer, F(resume: Id), Sig(F(resume: Id))));
        Assert.False(LaunchGate.Verify(signer, F(), Sig(F(resume: Id))));
        Assert.False(LaunchGate.Verify(signer, F(resume: Id), Sig(F())));
    }

    // ---- the gate's own resume rules -----------------------------------------------

    [Fact]
    public void A_resume_may_carry_a_first_message_at_the_gate() =>
        Assert.Null(LaunchGate.Rules(F(resume: Id, message: "carry on"), "p", Now, new NonceWindow(), Env()));

    [Fact]
    public void A_resume_naming_a_model_is_bad_input() =>
        Assert.Equal(RelayReplies.BadInput, LaunchGate.Rules(F(resume: Id, model: "opus"), "p", Now, new NonceWindow(), Env()));

    [Fact]
    public void A_resume_naming_a_parent_is_bad_input()
    {
        Assert.Equal(RelayReplies.BadInput, LaunchGate.Rules(F(resume: Id, parent: "lead"), "p", Now, new NonceWindow(), Env(inRoster: true)));
        // Control: the same parent on a plain launch passes.
        Assert.Null(LaunchGate.Rules(F(parent: "lead"), "p", Now, new NonceWindow(), Env(inRoster: true)));
    }

    [Fact]
    public void A_badly_shaped_resume_id_that_reached_the_gate_is_bad_frame() =>
        Assert.Equal(RelayReplies.BadFrame, LaunchGate.Rules(F(resume: "a b"), "p", Now, new NonceWindow(), Env()));

    // ---- the id rule (amendment 2, section 5) --------------------------------------

    [Fact]
    public void One_id_rule_caps_at_128()
    {
        Assert.True(PastSessions.IsSafeId(new string('a', 128)));
        Assert.False(PastSessions.IsSafeId(new string('a', 129)));
        Assert.Null(NewChatCommand.ResumeArgs(NewChatCli.ClaudeCode, new string('a', 129)));
    }

    // ---- the phone's parser --------------------------------------------------------

    private static RelayRequest? ParsePhone(string extra) =>
        RelayRequests.Parse(JsonDocument.Parse(
            "{\"t\":\"launch\",\"cwd\":\"/w\",\"cli\":\"claude\",\"persona\":\"\",\"model\":\"\",\"first_message\":\"\"," +
            "\"nonce\":\"n\",\"expires_at\":1,\"sig\":\"s\"" + extra + "}").RootElement, "r");

    [Fact]
    public void The_phone_parser_reads_a_resume_id() =>
        Assert.Equal(Id, ((LaunchRequest)ParsePhone(",\"resume_session_id\":\"" + Id + "\"")!).Fields.ResumeSessionId);

    [Fact]
    public void The_phone_parser_reads_absent_and_empty_as_a_new_chat()
    {
        Assert.Equal("", ((LaunchRequest)ParsePhone("")!).Fields.ResumeSessionId);
        Assert.Equal("", ((LaunchRequest)ParsePhone(",\"resume_session_id\":\"\"")!).Fields.ResumeSessionId);
    }

    [Theory]
    [InlineData(",\"resume_session_id\":7")]
    [InlineData(",\"resume_session_id\":null")]
    [InlineData(",\"resume_session_id\":\"a;b\"")]
    public void The_phone_parser_refuses_a_resume_id_of_the_wrong_shape(string extra) =>
        Assert.IsType<BadFrameRequest>(ParsePhone(extra));

    // ---- the peer's codec ----------------------------------------------------------

    private static PeerLaunchWire.LaunchRequest Peer(string? resume) =>
        new("/w", "claude", "", "", "default", "", "", "n", 1, "s", "c", ResumeSessionId: resume);

    [Fact]
    public void The_peer_codec_omits_the_field_for_a_new_chat_and_carries_it_for_a_resume()
    {
        Assert.DoesNotContain("resume_session_id", PeerLaunchWire.EncodeLaunch(Peer(null)).GetRawText());
        Assert.Equal(Id, PeerLaunchWire.ParseLaunch(PeerLaunchWire.EncodeLaunch(Peer(Id)))!.ResumeSessionId);
        Assert.Equal("", PeerLaunchWire.ParseLaunch(PeerLaunchWire.EncodeLaunch(Peer("")))!.ResumeSessionId);
    }

    [Fact]
    public void The_peer_codec_refuses_a_resume_id_of_the_wrong_shape() =>
        Assert.Null(PeerLaunchWire.ParseLaunch(PeerLaunchWire.EncodeLaunch(Peer("$(rm)"))));

    // ---- argv (amendment 2, section 3) ---------------------------------------------

    [Theory]
    [InlineData("ClaudeCode", new[] { "--resume", Id })]
    [InlineData("Codex", new[] { "resume", Id, "--", "-it's on" })]
    [InlineData("Grok", new[] { "--resume", Id, "--", "-it's on" })]
    public void A_first_message_rides_argv_only_where_the_cli_takes_its_prompt_there(string cli, string[] expected) =>
        Assert.Equal(expected, NewChatCommand.ResumeArgs(Enum.Parse<NewChatCli>(cli), Id, "-it's on"));
}
