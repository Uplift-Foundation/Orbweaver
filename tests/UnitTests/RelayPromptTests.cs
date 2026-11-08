using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-315: RelayPrompt.cs against the contract vectors it generated
/// (Orbweaver-Mobile contracts/prompt-, answer- and push-vectors.json, checked
/// there by an independent OpenSSL implementation), plus CB-311's push-v1.json
/// spike vectors, plus a case per branch of the rules themselves.
/// </summary>
public class RelayPromptVectorTests
{
    private static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", name))).RootElement;

    private static readonly JsonElement P = Load("prompt-vectors.json");
    private static readonly JsonElement A = Load("answer-vectors.json");
    private static readonly JsonElement U = Load("push-vectors.json");
    private static readonly JsonElement Spike = Load("push-v1.json");

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static byte[] Bytes(JsonElement e, string name) => Convert.FromHexString(Str(e, name));
    private static KeyPair Keys(JsonElement e, string name) =>
        new(Bytes(e.GetProperty(name), "private"), Bytes(e.GetProperty(name), "public"));

    public static TheoryData<string> Fingerprints()
    {
        var d = new TheoryData<string>();
        foreach (var f in P.GetProperty("fingerprints").EnumerateArray()) d.Add(Str(f, "name"));
        return d;
    }

    [Theory]
    [MemberData(nameof(Fingerprints))]
    public void Fingerprint_matches_the_vector(string name)
    {
        var f = P.GetProperty("fingerprints").EnumerateArray().Single(x => Str(x, "name") == name);
        var fp = PromptFingerprint.Of(Str(f, "screen"));
        if (!f.GetProperty("dialog").GetBoolean())
        {
            Assert.Null(fp);
            return;
        }
        Assert.NotNull(fp);
        Assert.Equal(Str(f, "canonical_text"), fp.Canonical);
        Assert.Equal(Str(f, "screen_hash"), fp.Hash);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(f.GetProperty("options").GetRawText()),
            PromptFrames.OptionsJson(PromptVectorsShim.OptionsOf(fp))), name + " options");
    }

    [Fact]
    public void Controls_hash_equal_and_a_changed_command_does_not()
    {
        string H(string n) => Str(P.GetProperty("fingerprints").EnumerateArray().Single(x => Str(x, "name") == n), "screen_hash");
        Assert.Equal(H("frames-v3-example"), H("cursor-moved"));
        Assert.Equal(H("frames-v3-example"), H("blank-rows-below"));
        Assert.NotEqual(H("frames-v3-example"), H("command-changed"));
    }

    private static readonly byte[] EpochKey0 = Convert.FromHexString(Str(P.GetProperty("inputs"), "session_key_epoch0"));

    private static void AssertSealed(JsonElement box, byte[] key, uint direction, string expectedPlaintext)
    {
        Assert.Equal(expectedPlaintext, Str(box, "plaintext"));
        var aad = Aad(Str(box, "from"), Str(box, "to"), Str(box, "class"));
        Assert.Equal(Str(box, "aad"), Hex(aad));
        Assert.Equal(Str(box, "ct"), Seal(key, Bytes(box, "nonce"), Encoding.UTF8.GetBytes(expectedPlaintext), aad));
        Assert.Equal(expectedPlaintext, Encoding.UTF8.GetString(Open(key, Str(box, "ct"), aad, direction)!));
    }

    [Fact]
    public void Prompt_frame_is_rebuilt_byte_for_byte_and_seals_to_the_vector()
    {
        var fp = PromptFingerprint.Of(PromptVectorsShim.FramesV3Screen)!;
        var p = P.GetProperty("prompt");
        var json = JsonDocument.Parse(Str(p, "plaintext")).RootElement;
        var rebuilt = PromptFrames.Prompt(Str(json, "request_id"), Str(json, "session_id"), "permission", "claude", "Bash",
            string.Join("\n", fp.Preview), PromptVectorsShim.OptionsOf(fp), fp.Hash, json.GetProperty("expires_at").GetInt64());
        AssertSealed(p, EpochKey0, DesktopToPhone, rebuilt);
    }

    [Fact]
    public void Resolved_and_refusal_frames_match()
    {
        var phone = Str(P.GetProperty("inputs"), "phone_id");
        var bys = new[] { "desk", "phone:" + phone, "gone" };
        var r = P.GetProperty("resolved").EnumerateArray().ToArray();
        Assert.Equal(3, r.Length);
        for (var i = 0; i < 3; i++)
            AssertSealed(r[i], EpochKey0, DesktopToPhone, PromptFrames.Resolved("01JC9R0A5N2Q7W8X3E4T6Y9ZBD", "0f6c4f2a", bys[i]));
        var e = P.GetProperty("refusals").EnumerateArray().ToArray();
        Assert.Equal(AnswerGate.Codes.Length, e.Length);
        for (var i = 0; i < e.Length; i++)
            AssertSealed(e[i], EpochKey0, DesktopToPhone, PromptFrames.Refusal("01JC9R0A5N2Q7W8X3E4T6Y9ZBD", "0f6c4f2a", AnswerGate.Codes[i]));
    }

    private static readonly JsonElement AIn = A.GetProperty("inputs");
    private static readonly KeyPair PhId = Keys(AIn, "phone_identity");

    public static TheoryData<string> AnswerPositives()
    {
        var d = new TheoryData<string>();
        foreach (var c in A.GetProperty("positives").EnumerateArray()) d.Add(Str(c, "name"));
        return d;
    }

    [Theory]
    [MemberData(nameof(AnswerPositives))]
    public void Answer_body_message_and_signature_match(string name)
    {
        var c = A.GetProperty("positives").EnumerateArray().Single(x => Str(x, "name") == name);
        var action = Str(c, "action");
        var scope = action == "tell" ? AnswerSigning.TellScope(Str(c, "note")) : Str(c, "scope");
        Assert.Equal(Str(c, "scope"), scope);
        var body = AnswerSigning.Body(Str(AIn, "request_id"), Str(AIn, "session_id"), action, scope, Str(AIn, "nonce"),
            AIn.GetProperty("expires_at").GetInt64());
        Assert.Equal(Str(c, "body"), body);
        Assert.Equal(Str(c, "body_utf8"), Hex(Encoding.UTF8.GetBytes(body)));
        Assert.Equal(Str(c, "message"), Hex(AnswerSigning.Message(body)));
        Assert.True(AnswerSigning.Verify(PhId.Public, body, Str(c, "sig")));
        var frame = c.GetProperty("answer_frame");
        var aad = Aad(Str(frame, "from"), Str(frame, "to"), "action");
        var pt = Open(Convert.FromHexString(Str(AIn, "session_key_epoch0")), Str(frame, "ct"), aad, PhoneToDesktop);
        Assert.Equal(Str(frame, "plaintext"), Encoding.UTF8.GetString(pt!));
    }

    [Fact]
    public void Answer_negatives_fail_verification()
    {
        foreach (var n in A.GetProperty("negatives").EnumerateArray())
        {
            var name = Str(n, "name");
            switch (Str(n, "expect"))
            {
                case "verify_fails":
                    Assert.False(AnswerSigning.Verify(PhId.Public, Str(n, "body"), Str(n, "sig")), name);
                    break;
                case "verify_fails_against_canonical":
                    Assert.True(AnswerSigning.Verify(PhId.Public, Str(n, "body"), Str(n, "sig")), name + " (control: valid over its own bytes)");
                    Assert.False(AnswerSigning.Verify(PhId.Public, Str(n, "canonical_body"), Str(n, "sig")), name);
                    break;
                case "scope_mismatch":
                    Assert.NotEqual(Str(n, "scope"), AnswerSigning.TellScope(Str(n, "note")));
                    break;
                default:
                    Assert.Fail("unknown expect in " + name);
                    break;
            }
        }
    }

    private static readonly KeyPair PhKa = Keys(U.GetProperty("inputs"), "phone_agreement");

    [Fact]
    public void Push_positives_seal_open_and_fit_exactly_as_the_vectors()
    {
        foreach (var c in U.GetProperty("positives").EnumerateArray())
        {
            var name = Str(c, "name");
            var eph = Keys(c, "eph");
            var pt = Str(c, "plaintext");
            Assert.Equal(Str(c, "e"), PushSeal.SealWith(eph, Bytes(c, "nonce"), PhKa.Public, Encoding.UTF8.GetBytes(pt)));
            Assert.Equal(pt, Encoding.UTF8.GetString(PushSeal.Open(PhKa, Str(c, "e"))!));
            Assert.Equal(c.GetProperty("e_length").GetInt32(), PushSeal.SealedLength(Encoding.UTF8.GetByteCount(pt)));
            Assert.True(Str(c, "e").Length <= PushSeal.MaxE, name);

            var fit = Fit(c.GetProperty("input"));
            Assert.NotNull(fit);
            Assert.Equal(pt, fit.Plaintext);
            Assert.Equal(c.GetProperty("detail_cut").GetBoolean(), fit.DetailCut);
            Assert.Equal(c.GetProperty("labels_cut").GetBoolean(), fit.LabelsCut);
        }
    }

    [Fact]
    public void A_prompt_that_cannot_fit_gets_no_e()
    {
        foreach (var c in U.GetProperty("no_e").EnumerateArray())
            Assert.Null(Fit(c.GetProperty("input")));
    }

    [Fact]
    public void Push_negatives_do_not_open()
    {
        foreach (var n in U.GetProperty("negatives").EnumerateArray())
        {
            var key = n.TryGetProperty("phone_agreement", out _) ? Keys(n, "phone_agreement") : PhKa;
            Assert.Null(PushSeal.Open(key, Str(n, "e")));
        }
    }

    [Fact]
    public void The_cb311_spike_vectors_still_seal_and_open()
    {
        foreach (var c in Spike.GetProperty("positives").EnumerateArray())
        {
            var phone = new KeyPair(Bytes(c, "phone_priv"), Bytes(c, "phone_pub"));
            var eph = new KeyPair(Bytes(c, "eph_priv"), Bytes(c, "eph_pub"));
            var pt = Encoding.UTF8.GetBytes(Str(c, "plaintext"));
            Assert.Equal(Str(c, "e"), PushSeal.SealWith(eph, Bytes(c, "nonce"), phone.Public, pt));
            Assert.Equal(Str(c, "key"), Hex(PushSeal.Key(SharedSecret(eph, phone.Public), eph.Public, phone.Public)));
            Assert.Equal(Str(c, "info"), Hex(PushSeal.Info(eph.Public, phone.Public)));
            Assert.Equal(pt, PushSeal.Open(phone, Str(c, "e")));
        }
    }

    private static PromptFrames.Fitted? Fit(JsonElement i)
    {
        var options = i.GetProperty("options").EnumerateArray().Select(o => new PromptOption(Str(o, "key"), Str(o, "label"), Str(o, "kind"),
            o.TryGetProperty("scope", out var s) ? s.GetString() : null)).ToList();
        return PromptFrames.FitPush(Str(i, "request_id"), Str(i, "session_id"), Str(i, "label"), Str(i, "tool"), Str(i, "summary"),
            Str(i, "preview"), i.GetProperty("expires_at").GetInt64(), options, Str(i, "screen_hash"));
    }
}

// The generator's two helpers, restated here rather than referenced: the
// generator is an exe the test project does not reference, and these are the
// exact inputs the vectors were made from.
internal static class PromptVectorsShim
{
    internal const string FramesV3Screen =
        "────────────────────────────────────────────────────────────\n" +
        " Bash command\n" +
        "\n" +
        "   dotnet test tests/UnitTests\n" +
        "   Run the unit tests\n" +
        "\n" +
        " Do you want to proceed?\n" +
        " ❯ 1. Yes\n" +
        "   2. Yes, and don't ask again for commands that start with dotnet test\n" +
        "   3. No, and tell Claude what to do differently\n" +
        "\n" +
        " Esc to cancel · Tab to amend\n";

    internal static List<PromptOption> OptionsOf(PromptFingerprint fp) =>
        fp.Prompt.Options.Select(o =>
        {
            var c = OptionKinds.Classify("permission", o.Label);
            return new PromptOption(o.Key, o.Label, c.Kind, c.Scope);
        }).ToList();
}

public class RelayPromptRuleTests
{
    // ---- canonical text ----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some output\nno dialog here\n")]
    public void No_dialog_is_no_fingerprint(string? screen) => Assert.Null(PromptFingerprint.Of(screen));

    [Fact]
    public void A_frame_edge_bounds_the_span_so_a_live_timer_above_it_is_not_hashed()
    {
        string Screen(int seconds) =>
            "  history line\n" +
            $"  ◇ Running the thing… {seconds}s\n" +
            "┃\n" +
            "┃  ›  Create the probe file\n" +
            "┃  │ touch $HOME/probe\n" +
            "┃\n" +
            "┃  1. Yes, proceed\n" +
            "┃  2. No, reject\n" +
            "┃\n";
        var a = PromptFingerprint.Of(Screen(22))!;
        var b = PromptFingerprint.Of(Screen(23))!;
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal("›  Create the probe file\n│ touch $HOME/probe\n1. Yes, proceed\n2. No, reject", a.Canonical);
    }

    [Fact]
    public void A_conversation_glyph_bounds_the_span_and_blank_lines_do_not()
    {
        string Screen(char spinner) =>
            "› user asked something\n" +
            $"{spinner} Running touch $HOME/probe\n" +
            "\n\n\n" +
            "  Would you like to run the following command?\n" +
            "\n\n" +
            "  $ touch $HOME/probe\n" +
            "\n\n" +
            "› 1. Yes, proceed (y)\n" +
            "  2. No, and tell Codex what to do differently (esc)\n";
        var a = PromptFingerprint.Of(Screen('•'))!;
        Assert.Equal(a.Hash, PromptFingerprint.Of(Screen('◦'))!.Hash);
        Assert.Equal("Would you like to run the following command?\n$ touch $HOME/probe\n1. Yes, proceed (y)\n2. No, and tell Codex what to do differently (esc)", a.Canonical);
    }

    [Fact]
    public void With_no_boundary_the_span_starts_at_the_dialog_window()
    {
        var lines = Enumerable.Range(0, 40).Select(i => $"line {i}").ToList();
        lines.Add("1. Yes");
        lines.Add("2. No");
        var fp = PromptFingerprint.Of(string.Join("\n", lines))!;
        var preview = fp.Canonical.Split('\n')[..^2];
        Assert.Equal(ChatTranscript.DialogWindow - 2, preview.Length);
        Assert.Equal("line 12", preview[0]);
    }

    [Fact]
    public void Hash_is_base64url_sha256_of_the_utf8_text()
    {
        var text = "Do you want to proceed?\n1. Yes\n2. No — ノート";
        Assert.Equal(Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(text))), PromptFingerprint.HashOf(text));
    }

    // ---- option kinds ------------------------------------------------------

    [Theory]
    [InlineData("permission", "Yes", false, "approve", null)]
    [InlineData("permission", "Yes, proceed (y)", false, "approve", null)]
    [InlineData("permission", "Yes, and don't ask again for commands that start with `touch $HOME/x` (p)", false, "always", "touch $HOME/x")]
    [InlineData("permission", "Yes, and don't ask again for commands that start with dotnet test", false, "always", "dotnet test")]
    [InlineData("permission", "Yes, and always allow access to /tmp/x from this project", false, "always", "/tmp/x")]
    [InlineData("permission", "Yes, allow all edits during this session", false, "always", "Yes, allow all edits during this session")]
    [InlineData("permission", "Always allow: Remove-Item C:/x", false, "always", "Remove-Item C:/x")]
    [InlineData("permission", "Never allow: Remove-Item C:/x", false, "never", null)]
    [InlineData("permission", "No", false, "deny", null)]
    [InlineData("permission", "No, reject (type to add feedback)", false, "deny", null)]
    [InlineData("permission", "No, and tell Claude what to do differently", false, "tell", null)]
    [InlineData("permission", "No, and don't ask again", false, "never", null)]
    [InlineData("permission", "No, never for this command", false, "never", null)]
    [InlineData("permission", "Yes, and switch to auto mode · auto mode handles these prompts for you", false, "mode", null)]
    [InlineData("permission", "Yes, and don't ask again for anything (always-approve mode)", false, "mode", null)]
    [InlineData("permission", "Yes", true, "mode", null)]
    [InlineData("permission", "Skip", false, "option", null)]
    [InlineData("question", "Yes", false, "option", null)]
    [InlineData("question", "Blue", true, "mode", null)]
    public void Classify(string kind, string label, bool mode, string expected, string? scope)
    {
        var c = OptionKinds.Classify(kind, label, mode);
        Assert.Equal(expected, c.Kind);
        Assert.Equal(scope, c.Scope);
    }

    // ---- answer escaper ----------------------------------------------------

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a\tb\nc", "a\\u0009b\\u000ac")]
    [InlineData("\u0000\u001f\u0020", "\\u0000\\u001f ")]
    [InlineData("a<b&c'd+e/f\u007f", "a<b&c'd+e/f\u007f")]
    [InlineData("ノート 🙂", "ノート 🙂")]
    public void Escape(string input, string expected) => Assert.Equal(expected, AnswerSigning.Escape(input));

    [Fact]
    public void Body_has_no_whitespace_and_a_bare_integer()
    {
        var b = AnswerSigning.Body("r", "s", "approve", "", "n", 1794113592);
        Assert.Equal("{\"request_id\":\"r\",\"session_id\":\"s\",\"action\":\"approve\",\"scope\":\"\",\"nonce\":\"n\",\"expires_at\":1794113592}", b);
    }

    [Fact]
    public void Verify_refuses_a_missing_or_malformed_signature()
    {
        var k = GenerateKeyPair();
        var body = AnswerSigning.Body("r", "s", "approve", "", "n", 1);
        Assert.False(AnswerSigning.Verify(k.Public, body, null));
        Assert.False(AnswerSigning.Verify(k.Public, body, "not base64url!"));
        Assert.True(AnswerSigning.Verify(k.Public, body, Base64Url(Sign(k, AnswerSigning.Message(body)))));
    }

    // ---- push seal ---------------------------------------------------------

    [Fact]
    public void A_random_seal_opens_and_its_length_is_predicted()
    {
        var phone = GenerateKeyPair();
        var pt = Encoding.UTF8.GetBytes("{\"v\":1}");
        var e = PushSeal.Seal(phone.Public, pt);
        Assert.Equal(PushSeal.SealedLength(pt.Length), e.Length);
        Assert.Equal(pt, PushSeal.Open(phone, e));
        Assert.NotEqual(e, PushSeal.Seal(phone.Public, pt));
    }

    [Fact]
    public void Open_refuses_garbage_short_and_off_curve_frames()
    {
        var phone = GenerateKeyPair();
        Assert.Null(PushSeal.Open(phone, null));
        Assert.Null(PushSeal.Open(phone, "@@@"));
        Assert.Null(PushSeal.Open(phone, Base64Url(new byte[50])));
        var offCurve = new byte[PushSeal.HeaderLength + NonceLength + TagLength + 4];
        offCurve[0] = PushSeal.Version;
        offCurve[1] = 4;
        Assert.Null(PushSeal.Open(phone, Base64Url(offCurve)));
    }

    // ---- frames and fitting ------------------------------------------------

    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 4, "abc…")]
    [InlineData("abcdef", 1, "…")]
    [InlineData("abcdef", 0, "")]
    [InlineData("ab🙂cd", 4, "ab…")]
    public void Cut(string text, int max, string expected) => Assert.Equal(expected, PromptFrames.Cut(text, max));

    [Fact]
    public void Prompt_preview_is_cut_at_4000()
    {
        var json = PromptFrames.Prompt("r", "s", "permission", "claude", "Bash", new string('x', 5000), [], "h", 1);
        var preview = JsonDocument.Parse(json).RootElement.GetProperty("preview").GetString()!;
        Assert.Equal(PromptFrames.MaxPreview, preview.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Fitting_keeps_every_option_kind_and_scope()
    {
        var options = Enumerable.Range(1, 14).Select(i => new PromptOption(i.ToString(), new string('l', 400), i == 2 ? "always" : "option", i == 2 ? "prefix" : null)).ToList();
        var fit = PromptFrames.FitPush("r", "s", "label", "Bash", "Needs an answer", new string('d', 3000), 1, options, "hash")!;
        var j = JsonDocument.Parse(fit.Plaintext).RootElement;
        Assert.True(fit.LabelsCut);
        Assert.Equal(14, j.GetProperty("options").GetArrayLength());
        Assert.Equal("prefix", j.GetProperty("options")[1].GetProperty("scope").GetString());
        Assert.All(j.GetProperty("options").EnumerateArray(), o => Assert.Equal(PromptFrames.MaxLabelAfterCut, o.GetProperty("label").GetString()!.Length));
        Assert.True(PushSeal.SealedLength(Encoding.UTF8.GetByteCount(fit.Plaintext)) <= PushSeal.MaxE);
    }

    [Fact]
    public void Labels_cut_and_detail_whole_again_reports_detail_uncut()
    {
        var options = Enumerable.Range(1, 9).Select(i => new PromptOption(i.ToString(), new string('l', 400), "option", null)).ToList();
        var fit = PromptFrames.FitPush("r", "s", "label", "Bash", "Needs an answer", "short detail", 1, options, "hash")!;
        Assert.True(fit.LabelsCut);
        Assert.False(fit.DetailCut);
        Assert.Equal("short detail", JsonDocument.Parse(fit.Plaintext).RootElement.GetProperty("detail").GetString());
    }
}
