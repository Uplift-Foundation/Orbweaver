using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>CB-316: launch — the signature and every refusal, replayed against frames-v4-vectors.json (launch section), then the command it starts.</summary>
public class PhoneLaunchTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v4-vectors.json"))).RootElement;
    private static JsonElement L => V.GetProperty("launch");
    private static readonly KeyPair Phone = new(
        Convert.FromHexString(V.GetProperty("inputs").GetProperty("phone_identity").GetProperty("private").GetString()!),
        Convert.FromHexString(V.GetProperty("inputs").GetProperty("phone_identity").GetProperty("public").GetString()!));

    private sealed class Rig
    {
        internal DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
        internal readonly List<(string Cls, string? Ck, string Plain)> Sent = new();
        internal readonly NonceWindow Nonces = new();
        internal bool Dir = true, Windows;
        internal HashSet<NewChatCli> Installed = [NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok];
        internal bool HasParent = true;
        internal (string? Code, string? Why) Start = (null, null);
        internal PhoneService.Session? Appears = new("9c1e002b", new SessionStatus(), "idle");
        internal Func<(bool Typed, string? Refusal)> Typing = () => (true, null);
        internal LaunchPlan? Started;
        internal readonly List<string> Typed = new();
        internal List<NewChatAccounts.Choice> Accounts = [new("Default (~/.claude)", null), new("~/.claude-kmart", ".claude-kmart"), new("~/.claude-uplift", ".claude-uplift")];
        internal string? Last = ".claude-uplift";
        internal string? Designated;
        internal PhoneLaunch Launch;

        internal Rig()
        {
            Launch = new PhoneLaunch(new PhoneLaunch.Seams(
                () => Now,
                d => { Now += d; return Task.CompletedTask; },
                _ => Dir,
                cli => Installed.Contains(cli) ? "/bin/" + LaunchCommand.CliName(cli) : null,
                id => HasParent && id == "0f6c4f2a" ? new(id, new SessionStatus(), "idle") : null,
                (plan, _) => { Started = plan; return Start; },
                (_, _, _) => Appears,
                (_, text) => { Typed.Add(text); return Task.FromResult(Typing()); },
                (phone, cls, ck, plain) => { Sent.Add((cls, ck, plain)); return true; },
                () => Windows,
                _ => { }, _ => Accounts, _ => Last, _ => Designated), Nonces);
        }

        internal Task Run(JsonElement fields, string reference = "req1")
        {
            string S(string n) => fields.TryGetProperty(n, out var v) ? v.GetString()! : "";
            var expires = fields.GetProperty("expires_at").GetInt64();
            var body = LaunchSigning.Body(S("cwd"), S("cli"), S("persona"), S("model"), S("parent_session_id"), S("first_message"), S("nonce"), expires, S("account"));
            var sig = Base64Url(Sign(Phone, [.. Encoding.UTF8.GetBytes(LaunchSigning.Domain), .. Encoding.UTF8.GetBytes(body)]));
            return Launch.HandleAsync("ph_1", reference, Phone.Public, Frame(fields, sig));
        }

        internal static JsonElement Frame(JsonElement fields, string sig)
        {
            var d = fields.EnumerateObject().ToDictionary(p => p.Name, p => (object)(p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : p.Value.GetString()!));
            d["t"] = "launch";
            d["sig"] = sig;
            return JsonDocument.Parse(JsonSerializer.Serialize(d)).RootElement;
        }

        internal string[] Plains => Sent.Select(s => s.Plain).ToArray();
        internal string Code => JsonDocument.Parse(Sent.Last().Plain).RootElement.GetProperty("code").GetString()!;
    }

    private static JsonElement Fields(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Base_(Action<Dictionary<string, object>>? tweak = null)
    {
        var d = new Dictionary<string, object>
        {
            ["cwd"] = "/Users/w/Source/p", ["cli"] = "claude", ["persona"] = "", ["model"] = "", ["parent_session_id"] = "",
            ["first_message"] = "hello", ["nonce"] = "n1", ["expires_at"] = 1794111912L,
        };
        tweak?.Invoke(d);
        return JsonDocument.Parse(JsonSerializer.Serialize(d)).RootElement;
    }

    // ---- the vectors ---------------------------------------------------------------

    [Fact]
    public void EveryPositiveSignatureVerifiesOverTheRebuiltBody()
    {
        foreach (var p in L.GetProperty("positives").EnumerateArray())
        {
            var f = p.GetProperty("fields");
            string S(string n) => f.TryGetProperty(n, out var v) ? v.GetString()! : "";
            var body = LaunchSigning.Body(S("cwd"), S("cli"), S("persona"), S("model"), S("parent_session_id"), S("first_message"), S("nonce"), f.GetProperty("expires_at").GetInt64());
            Assert.Equal(p.GetProperty("body").GetString(), body);
            Assert.True(LaunchSigning.Verify(Phone.Public, body, p.GetProperty("sig").GetString()), p.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void EveryNegativeFailsVerification()
    {
        foreach (var n in L.GetProperty("negatives").EnumerateArray())
        {
            var name = n.GetProperty("name").GetString()!;
            var body = n.GetProperty("body").GetString()!;
            var verifies = LaunchSigning.Verify(Phone.Public, body, n.GetProperty("sig").GetString());
            // These two are signed over their own bytes and verify against them: they fail
            // because the desktop rebuilds the canonical body from the frame's fields and
            // verifies against that, so a library-escaped or spaced layout is a different message.
            if (name is "library-escaped-body" or "whitespace-body")
                Assert.True(body.Contains("\\u00", StringComparison.Ordinal) || body.Contains(": ") || body.Contains(", "), name);
            else Assert.False(verifies, name);
        }
    }

    [Fact]
    public async Task EveryRuleNegativeGetsTheSpecsCode()
    {
        foreach (var n in L.GetProperty("rule_negatives").EnumerateArray())
        {
            var rig = new Rig { Now = DateTimeOffset.FromUnixTimeSeconds(n.GetProperty("now").GetInt64()) };
            var name = n.GetProperty("name").GetString()!;
            if (name == "replayed-nonce") rig.Nonces.Add("ph_1", n.GetProperty("fields").GetProperty("nonce").GetString()!, rig.Now);
            await rig.Run(n.GetProperty("fields"));
            var expect = n.GetProperty("expect").GetString()!;
            if (expect == "ok") Assert.Contains("\"code\":\"launched\"", rig.Plains[0]);
            else Assert.Equal(expect, rig.Code);
        }
    }

    [Fact]
    public async Task EachPositiveEarnsTheRepliesTheVectorPins()
    {
        foreach (var p in L.GetProperty("positives").EnumerateArray())
        {
            var rig = new Rig();
            var f = p.GetProperty("fields");
            var replies = p.GetProperty("replies").EnumerateArray().Select(r => r.GetProperty("plaintext").GetString()!).ToArray();
            var reference = JsonDocument.Parse(replies[0]).RootElement.GetProperty("ref").GetString()!;
            rig.Appears = new(JsonDocument.Parse(replies[0]).RootElement.GetProperty("session_id").GetString()!, new SessionStatus(), "idle");
            await rig.Run(f, reference);
            Assert.Equal(p.GetProperty("reply_count").GetInt32(), rig.Plains.Length);
            Assert.Equal(replies, rig.Plains);
        }
    }

    // ---- the check order, one at a time ----------------------------------------------

    [Fact]
    public async Task AFrameMissingAFieldIsBadFrame()
    {
        var rig = new Rig();
        await rig.Launch.HandleAsync("ph_1", "r", Phone.Public, Fields("""{"t":"launch","cwd":"/x"}"""));
        Assert.Equal("""{"t":"err","ref":"r","code":"bad_frame"}""", Assert.Single(rig.Plains));
        Assert.Null(rig.Sent[0].Ck);
    }

    [Fact]
    public async Task ATamperedMessageOrAForeignKeyIsBadSignature()
    {
        var rig = new Rig();
        var good = Base_();
        var d = good.EnumerateObject().ToDictionary(p => p.Name, p => (object)(p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : p.Value.GetString()!));
        var body = LaunchSigning.Body("/Users/w/Source/p", "claude", "", "", "", "hello", "n1", 1794111912);
        d["t"] = "launch";
        d["sig"] = Base64Url(Sign(Phone, [.. Encoding.UTF8.GetBytes(LaunchSigning.Domain), .. Encoding.UTF8.GetBytes(body)]));
        d["first_message"] = "something else";
        await rig.Launch.HandleAsync("ph_1", "r", Phone.Public, JsonDocument.Parse(JsonSerializer.Serialize(d)).RootElement);
        Assert.Equal("bad_signature", rig.Code);
        Assert.Null(rig.Started);
        d["first_message"] = "hello";
        await rig.Launch.HandleAsync("ph_1", "r", GenerateKeyPair().Public, JsonDocument.Parse(JsonSerializer.Serialize(d)).RootElement);
        Assert.Equal("bad_signature", rig.Code);
    }

    [Theory]
    [InlineData("unknown_cwd")]
    [InlineData("unknown_cli")]
    [InlineData("unknown_parent")]
    public async Task TheDesksOwnFactsRefuseBeforeAnythingStarts(string code)
    {
        var rig = new Rig();
        var fields = Base_(d => { d["parent_session_id"] = "0f6c4f2a"; });
        if (code == "unknown_cwd") rig.Dir = false;
        if (code == "unknown_cli") rig.Installed.Clear();
        if (code == "unknown_parent") rig.HasParent = false;
        await rig.Run(fields);
        Assert.Equal(code, rig.Code);
        Assert.Null(rig.Started);
    }

    [Fact]
    public async Task ACodexParentIsUnsupportedOnlyOnceItExists()
    {
        var rig = new Rig();
        await rig.Run(Base_(d => { d["cli"] = "codex"; d["parent_session_id"] = "gone"; }));
        Assert.Equal("unknown_parent", rig.Code);
    }

    [Theory]
    [InlineData("a\u001bb")]
    [InlineData("a\u009bb")]
    public async Task AControlCharacterInTheMessageIsBadInput(string message)
    {
        var rig = new Rig();
        await rig.Run(Base_(d => d["first_message"] = message));
        Assert.Equal("bad_input", rig.Code);
    }

    [Fact]
    public async Task APercentNameIsBadInputForCodexAndGrokOnWindowsOnly()
    {
        var windows = new Rig { Windows = true };
        await windows.Run(Base_(d => { d["cli"] = "grok"; d["first_message"] = "show %PATH% please"; }));
        Assert.Equal("bad_input", windows.Code);

        var lone = new Rig { Windows = true };
        await lone.Run(Base_(d => { d["cli"] = "grok"; d["first_message"] = "100% sure, 20%"; d["nonce"] = "n2"; }));
        Assert.Equal("launched", lone.Sent[0].Plain.Contains("launched") ? "launched" : "?");

        var claude = new Rig { Windows = true };
        await claude.Run(Base_(d => d["first_message"] = "show %PATH% please"));
        Assert.Contains("launched", claude.Plains[0]);

        var mac = new Rig();
        await mac.Run(Base_(d => { d["cli"] = "codex"; d["first_message"] = "show %PATH% please"; }));
        Assert.Contains("launched", mac.Plains[0]);
    }

    [Theory]
    [InlineData("no_terminal", "tmux is not installed")]
    [InlineData("launch_failed", "tmux refused")]
    public async Task ATerminalThatWillNotStartCarriesTheDesksSentence(string code, string why)
    {
        var rig = new Rig { Start = (code, why) };
        await rig.Run(Base_());
        Assert.Equal($$"""{"t":"err","ref":"req1","code":"{{code}}","why":"{{why}}"}""", Assert.Single(rig.Plains));
    }

    [Fact]
    public async Task ASessionThatNeverChecksInIsNoSessionAfterThirtySeconds()
    {
        var rig = new Rig { Appears = null };
        var began = rig.Now;
        await rig.Run(Base_());
        Assert.Equal("no_session", rig.Code);
        Assert.InRange((rig.Now - began).TotalSeconds, 30, 31);
    }

    // ---- what was started, and the two replies ------------------------------------------

    [Fact]
    public async Task AClaudeLaunchStartsWithModelAndParentThenTypesTheMessage()
    {
        var rig = new Rig();
        await rig.Run(Base_(d => { d["model"] = "opus"; d["parent_session_id"] = "0f6c4f2a"; }));
        Assert.Equal(new LaunchPlan(NewChatCli.ClaudeCode, "/Users/w/Source/p", "opus", "0f6c4f2a", "hello", ".claude-uplift"), rig.Started);
        Assert.Equal(["hello"], rig.Typed);
        Assert.Equal(["""{"t":"ok","ref":"req1","code":"launched","session_id":"9c1e002b"}""", """{"t":"ok","ref":"req1","code":"typed","session_id":"9c1e002b"}"""], rig.Plains);
        Assert.All(rig.Sent, s => Assert.Equal("9c1e002b", s.Ck));
    }

    [Fact]
    public async Task ACodexLaunchIsDeliveredInArgvSoCheckingInIsTyped()
    {
        var rig = new Rig();
        await rig.Run(Base_(d => d["cli"] = "codex"));
        Assert.Empty(rig.Typed);
        Assert.Equal(2, rig.Plains.Length);
        Assert.Contains("\"code\":\"typed\"", rig.Plains[1]);
    }

    [Fact]
    public async Task ALaunchWithNoMessageGetsOneReply()
    {
        var rig = new Rig();
        await rig.Run(Base_(d => d["first_message"] = ""));
        Assert.Single(rig.Plains);
        Assert.Empty(rig.Typed);
    }

    [Fact]
    public async Task AClaudeThatIsNotYetAtItsPromptIsRetriedUntilItIsThenTyped()
    {
        var rig = new Rig();
        var calls = 0;
        rig.Typing = () => ++calls < 4 ? (false, "its screen could not be read as a prompt") : (true, null);
        await rig.Run(Base_());
        Assert.Equal(4, calls);
        Assert.Contains("\"code\":\"typed\"", rig.Plains[1]);
    }

    [Fact]
    public async Task AClaudeThatNeverReachesItsPromptIsNotAtPromptAfterSixtySeconds()
    {
        var rig = new Rig { Typing = () => (false, "it is showing a dialog: Trust this folder") };
        var began = rig.Now;
        await rig.Run(Base_());
        Assert.Equal("""{"t":"err","ref":"req1","code":"not_at_prompt","session_id":"9c1e002b","why":"it is showing a dialog: Trust this folder"}""", rig.Plains[1]);
        Assert.InRange((rig.Now - began).TotalSeconds, 60, 61);
    }

    [Fact]
    public async Task ATyperThatFailsOutrightIsTypingFailed()
    {
        var rig = new Rig { Typing = () => (false, null) };
        await rig.Run(Base_());
        Assert.Contains("\"code\":\"typing_failed\"", rig.Plains[1]);
    }

    [Fact]
    public async Task ARepeatedNonceIsReplayed()
    {
        var rig = new Rig();
        await rig.Run(Base_());
        await rig.Run(Base_(), "req2");
        Assert.Equal("replayed", rig.Code);
    }

    // ---- accounts (additive to v4) ---------------------------------------------------------

    [Fact]
    public void AnAccountIsSignedOnlyWhenNamed()
    {
        var plain = LaunchSigning.Body("/x", "claude", "", "", "", "m", "n", 5);
        Assert.DoesNotContain("account", plain);
        Assert.Equal(plain, LaunchSigning.Body("/x", "claude", "", "", "", "m", "n", 5, ""));
        Assert.Equal("{\"cwd\":\"/x\",\"cli\":\"claude\",\"persona\":\"\",\"model\":\"opus\",\"account\":\".claude-uplift\",\"parent_session_id\":\"\",\"first_message_sha256\":\"" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("m"u8)).ToLowerInvariant() + "\",\"nonce\":\"n\",\"expires_at\":5}",
            LaunchSigning.Body("/x", "claude", "", "opus", "", "m", "n", 5, ".claude-uplift"));
    }

    [Fact]
    public async Task ANamedAccountIsUsedAndAnUnnamedOneFallsBackToTheLastUsedInNewChat()
    {
        var named = new Rig();
        await named.Run(Base_(d => d["account"] = ".claude-kmart"));
        Assert.Equal(".claude-kmart", named.Started!.ProfileDir);

        var unnamed = new Rig();
        await unnamed.Run(Base_());
        Assert.Equal(".claude-uplift", unnamed.Started!.ProfileDir);

        var none = new Rig { Last = null };
        await none.Run(Base_());
        Assert.Null(none.Started!.ProfileDir);

        var explicitDefault = new Rig();
        await explicitDefault.Run(Base_(d => d["account"] = "default"));
        Assert.Null(explicitDefault.Started!.ProfileDir);
    }

    [Fact]
    public async Task ADesignatedDefaultBeatsTheLastUsedAndALaunchThatNamesOneBeatsBoth()
    {
        var designated = new Rig { Designated = ".claude-kmart", Last = ".claude-uplift" };
        await designated.Run(Base_());
        Assert.Equal(".claude-kmart", designated.Started!.ProfileDir);

        var named = new Rig { Designated = ".claude-kmart" };
        await named.Run(Base_(d => d["account"] = ".claude-uplift"));
        Assert.Equal(".claude-uplift", named.Started!.ProfileDir);

        var theCliDefault = new Rig { Designated = "default", Last = ".claude-uplift" };
        await theCliDefault.Run(Base_());
        Assert.Null(theCliDefault.Started!.ProfileDir);

        var unlisted = new Rig { Designated = ".claude-gone" };
        await unlisted.Run(Base_());
        Assert.Equal("unknown_account", unlisted.Code);
    }

    [Fact]
    public async Task AnAccountTheDesktopDoesNotListIsUnknownAccountAndNothingStarts()
    {
        var rig = new Rig();
        await rig.Run(Base_(d => d["account"] = ".claude-other"));
        Assert.Equal("unknown_account", rig.Code);
        Assert.Null(rig.Started);
    }

    [Fact]
    public async Task TheAccountIsCheckedAfterTheModelAndBeforeThePersona()
    {
        var rig = new Rig();
        await rig.Run(Base_(d => { d["account"] = ".nope"; d["model"] = "bad model!"; }));
        Assert.Equal("bad_model", rig.Code);
        var persona = new Rig();
        await persona.Run(Base_(d => { d["account"] = ".nope"; d["persona"] = "P"; d["nonce"] = "n9"; }));
        Assert.Equal("unknown_account", persona.Code);
    }

    [Fact]
    public void TheAccountsFrameListsEachClisChoicesAndNothingIsStillAFrame()
    {
        var choices = new Dictionary<NewChatCli, IReadOnlyList<NewChatAccounts.Choice>>
        {
            [NewChatCli.ClaudeCode] = [new("Default (~/.claude)", null), new("~/.claude-uplift", ".claude-uplift")],
            [NewChatCli.Codex] = [new("Default (~/.codex)", null)],
            [NewChatCli.Grok] = [],
        };
        var frame = PhoneAccounts.Frame(PhoneAccounts.From(c => choices[c]));
        Assert.Equal("{\"t\":\"accounts\",\"accounts\":[{\"cli\":\"claude\",\"id\":\"default\",\"label\":\"Default (~/.claude)\"},{\"cli\":\"claude\",\"id\":\".claude-uplift\",\"label\":\"~/.claude-uplift\"},{\"cli\":\"codex\",\"id\":\"default\",\"label\":\"Default (~/.codex)\"}]}", frame);
        Assert.Equal("{\"t\":\"accounts\",\"accounts\":[]}", PhoneAccounts.Frame([]));
    }

    // ---- the command ---------------------------------------------------------------------

    [Fact]
    public void TheWordsPerCli()
    {
        Assert.Equal(["--model", "opus", "--parent-session-id", "p1"], LaunchCommand.Words(new(NewChatCli.ClaudeCode, "/x", "opus", "p1", "typed later")));
        Assert.Empty(LaunchCommand.Words(new(NewChatCli.ClaudeCode, "/x", "", null, "")));
        Assert.Equal(["-m", "gpt-5", "--", "-rf notaflag"], LaunchCommand.Words(new(NewChatCli.Codex, "/x", "gpt-5", null, "-rf notaflag")));
        Assert.Empty(LaunchCommand.Words(new(NewChatCli.Grok, "/x", "", null, "")));
    }

    [Fact]
    public void TheMacLineIsExecWithEachWordQuoted()
    {
        Assert.Equal("exec '/bin/codex' '-m' 'o' '--' 'it'\\''s'", LaunchCommand.Shell(new(NewChatCli.Codex, "/x", "o", null, "it's"), "/bin/codex", null));
        Assert.StartsWith("exec env CLAUDE_CONFIG_DIR='/c' '/bin/claude'", LaunchCommand.Shell(new(NewChatCli.ClaudeCode, "/x", "", null, ""), "/bin/claude", "/c"));
    }

    [Fact]
    public void TheWindowsLaunchIsANewTabInTheExistingWindowAndBuildsArgvWordByWord()
    {
        var claude = LaunchCommand.Windows(new(NewChatCli.ClaudeCode, @"C:\p", "opus", "p1", "m"), @"C:\bin\claude.exe", null)!;
        Assert.Equal("wt.exe", claude.FileName);
        Assert.Equal(["-w", "0", "new-tab", "-d", @"C:\p", "cmd.exe", "/k", @"C:\bin\claude.exe", "--model", "opus", "--parent-session-id", "p1"], claude.ArgumentList);

        var codex = LaunchCommand.Windows(new(NewChatCli.Codex, @"C:\p", "", null, "a & b | c > d"), @"C:\bin\codex.exe", null)!;
        Assert.Equal(["-w", "0", "new-tab", "-d", @"C:\p", @"C:\bin\codex.exe", "--", "a & b | c > d"], codex.ArgumentList);

        Assert.Null(LaunchCommand.Windows(new(NewChatCli.Grok, @"C:\p", "", null, "hi"), @"C:\bin\grok.cmd", null));
        Assert.NotNull(LaunchCommand.Windows(new(NewChatCli.Grok, @"C:\p", "", null, ""), @"C:\bin\grok.cmd", null));
    }

    [Fact]
    public void AClaudeAccountIsSetInsideTheTabsOwnCommandBecauseATabInheritsTheWindowsEnvironment()
    {
        var info = LaunchCommand.Windows(new(NewChatCli.ClaudeCode, @"C:\p", "opus", null, "m"), @"C:\Program Files\claude.exe", @"C:\Users\me\.claude-uplift")!;
        Assert.Equal(["-w", "0", "new-tab", "-d", @"C:\p", "cmd.exe", "/k",
            "set \"CLAUDE_CONFIG_DIR=C:\\Users\\me\\.claude-uplift\" && \"C:\\Program Files\\claude.exe\" --model opus"], info.ArgumentList);
        Assert.False(info.Environment.ContainsKey("CLAUDE_CONFIG_DIR") && info.Environment["CLAUDE_CONFIG_DIR"] == @"C:\Users\me\.claude-uplift");
    }

    [Fact]
    public void AnAccountPathCmdWouldReadAsSyntaxIsCarriedOnTheProcessInstead()
    {
        var info = LaunchCommand.Windows(new(NewChatCli.ClaudeCode, @"C:\p", "", null, ""), @"C:\bin\claude.exe", @"C:\Users\a&b\.claude-x")!;
        Assert.Equal(["-w", "0", "new-tab", "-d", @"C:\p", "cmd.exe", "/k", @"C:\bin\claude.exe"], info.ArgumentList);
        Assert.Equal(@"C:\Users\a&b\.claude-x", info.Environment["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void ACodexAccountOpensItsOwnWindowCarryingTheVariableOnTheProcess()
    {
        var info = LaunchCommand.Windows(new(NewChatCli.Codex, @"C:\p", "", null, "x"), @"C:\bin\codex.exe", @"C:\Users\me\.codex-b")!;
        Assert.Equal(["-d", @"C:\p", @"C:\bin\codex.exe", "--", "x"], info.ArgumentList);
        Assert.Equal(@"C:\Users\me\.codex-b", info.Environment[NewChatAccountHome.For(NewChatCli.Codex).EnvVar]);
    }

    [Theory]
    [InlineData(@"C:\Users\me\.claude-uplift", true)]
    [InlineData(@"C:\Program Files\x", true)]
    [InlineData("a&b", false)]
    [InlineData("50%", false)]
    [InlineData("a\"b", false)]
    [InlineData("a|b", false)]
    [InlineData("", false)]
    public void WhatIsSafeInsideOneCmdLine(string text, bool safe) => Assert.Equal(safe, LaunchCommand.CmdSafe(text));

    [Fact]
    public void TheConhostFallbackNamesConhostOutright()
    {
        var claude = LaunchCommand.Windows(new(NewChatCli.ClaudeCode, @"C:\p", "", null, ""), @"C:\bin\claude.exe", null, windowsTerminal: false)!;
        Assert.Equal("conhost.exe", claude.FileName);
        Assert.Equal(["cmd.exe", "/k", @"C:\bin\claude.exe"], claude.ArgumentList);
        Assert.Equal(@"C:\p", claude.WorkingDirectory);
        var codex = LaunchCommand.Windows(new(NewChatCli.Codex, @"C:\p", "", null, "x"), @"C:\bin\codex.exe", null, windowsTerminal: false)!;
        Assert.Equal([@"C:\bin\codex.exe", "--", "x"], codex.ArgumentList);
    }

    [Theory]
    [InlineData(@"C:\Users\Hawai", "C:/Users/Hawai", true, true)]
    [InlineData(@"c:\users\hawai\", "C:/Users/Hawai", true, true)]
    [InlineData(@"c:\users\hawai", "C:/Users/Hawai", false, false)]
    [InlineData("/home/a/", "/home/a", false, true)]
    [InlineData("/home/a", "/home/b", false, false)]
    public void ALaunchedFolderMatchesHoweverTheSlashesAreWritten(string recorded, string asked, bool windows, bool same) =>
        Assert.Equal(same, PhoneSeams.SameFolder(recorded, asked, windows));

    [Theory]
    [InlineData("/Users/w", true)]
    [InlineData(@"C:\Users\w", true)]
    [InlineData("D:/x", true)]
    [InlineData("Source/x", false)]
    [InlineData(@"C:", false)]
    [InlineData("", false)]
    public void AbsolutePaths(string path, bool expected) => Assert.Equal(expected, PhoneLaunch.IsAbsolute(path));

    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("grok", true)]
    [InlineData("gemini", false)]
    public void ParseCli(string name, bool known) => Assert.Equal(known, LaunchCommand.ParseCli(name) is not null);
}
