using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-315 surface B: the whole desktop loop — watcher, gate, press, recapture,
/// resolved — against a fake pane, so every outcome is asserted on the keys that
/// reached the "terminal" and the frames that reached the "relay".
/// </summary>
public class PromptPipelineTests
{
    private const string PhoneId = "ph_a1b2c3d4e5f60718";
    private static readonly KeyPair Phone = KeyPairFromPrivate(SHA256.HashData("pipeline-phone"u8.ToArray()));
    private static readonly KeyPair Other = KeyPairFromPrivate(SHA256.HashData("pipeline-other"u8.ToArray()));
    private static readonly string Dialog = PromptVectorsShim.FramesV3Screen;
    private const string Cleared = "⏺ Running…\n\n> \n";

    private sealed class Pane
    {
        public string? Screen = Dialog;
        public readonly List<string> Keys = new();
        public readonly List<string> Typed = new();
        public bool Stuck;                 // a press changes nothing
        public bool PressFails;            // the press itself fails
        public int ClearAfterPolls;        // tell: the dialog clears on this capture after the press
        public int CapturesSincePress = -1;
        public TaskCompletionSource? Hold; // a capture that waits

        public async Task<string?> Capture(SessionStatus _)
        {
            if (Hold is not null) await Hold.Task;
            if (CapturesSincePress >= 0 && ++CapturesSincePress >= ClearAfterPolls && !Stuck) Screen = Cleared;
            return Screen;
        }

        public Task<bool> Press(SessionStatus _, string key)
        {
            Keys.Add(key);
            if (PressFails) return Task.FromResult(false);
            if (key != "Enter" && ClearAfterPolls == 0 && !Stuck) Screen = Cleared;
            if (key != "Enter" && ClearAfterPolls > 0) CapturesSincePress = 0;
            return Task.FromResult(true);
        }

        public Task<bool> Type(SessionStatus _, string text) { Typed.Add(text); return Task.FromResult(true); }
    }

    private sealed class Sender : IPromptSender
    {
        public readonly List<(string Rid, string Sid, string Prompt, string? Push)> Prompts = new();
        public readonly List<(string Rid, string Sid, string By)> Resolved = new();
        public readonly List<(string Phone, string Rid, string Code)> Refusals = new();
        public bool Online = true;   // false: the relay is down and no frame is produced
        bool IPromptSender.Prompt(string r, string s, string p, string? push) { Prompts.Add((r, s, p, push)); return Online; }
        public readonly List<string?> ResolvedPushes = new();
        void IPromptSender.Resolved(string r, string s, string by, string? push) { Resolved.Add((r, s, by)); ResolvedPushes.Add(push); }
        void IPromptSender.Refusal(string ph, string r, string s, string code) => Refusals.Add((ph, r, code));
    }

    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
    private readonly Pane _pane = new();
    private readonly Sender _sent = new();
    private readonly List<string> _log = new();
    private readonly PromptPipeline _p;

    public PromptPipelineTests()
    {
        _p = new PromptPipeline(new PromptPipeline.Seams(_pane.Capture, _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add),
            _sent, "Test Mac");
    }

    private static SessionStatus Status(string state = "waiting", SessionSource source = SessionSource.ClaudeCode, string title = "orbweaver") =>
        new() { State = state, Source = source, Title = title };

    private static (string, SessionStatus)[] Snap(string state = "waiting", string sid = "s1") => [(sid, Status(state))];

    private PhoneAnswer AnswerTo(int prompt = 0, string action = "approve", string scope = "", string note = "", KeyPair? signer = null, string? nonce = null)
    {
        var p = JsonDocument.Parse(_sent.Prompts[prompt].Prompt).RootElement;
        var rid = p.GetProperty("request_id").GetString()!;
        var sid = p.GetProperty("session_id").GetString()!;
        var exp = p.GetProperty("expires_at").GetInt64();
        var hash = p.GetProperty("screen_hash").GetString()!;
        nonce ??= Base64Url(RandomNumberGenerator.GetBytes(16));
        var sig = Base64Url(Sign(signer ?? Phone, AnswerSigning.Message(AnswerSigning.Body(rid, sid, action, scope, nonce, exp))));
        return new PhoneAnswer(rid, sid, action, scope, note, nonce, exp, hash, sig);
    }

    // ---- the watcher ---------------------------------------------------------

    [Fact]
    public async Task A_waiting_session_on_a_dialog_sends_one_prompt_with_its_push()
    {
        await _p.OnScannedAsync(Snap());
        var (rid, sid, prompt, push) = Assert.Single(_sent.Prompts);
        var j = JsonDocument.Parse(prompt).RootElement;
        Assert.Equal(("prompt", "s1", "claude", "Bash", PromptFingerprint.Of(Dialog)!.Hash),
            (j.GetProperty("t").GetString(), sid, j.GetProperty("cli").GetString(), j.GetProperty("tool").GetString(), j.GetProperty("screen_hash").GetString()));
        Assert.Equal(_now.ToUnixTimeSeconds() + 1800, j.GetProperty("expires_at").GetInt64());
        var e = JsonDocument.Parse(push!).RootElement;
        Assert.Equal(("orbweaver on Test Mac", "Wants to run a command"), (e.GetProperty("label").GetString(), e.GetProperty("summary").GetString()));
        Assert.Equal(rid, j.GetProperty("request_id").GetString());
        Assert.Contains($"prompt {rid} {PromptFingerprint.Of(Dialog)!.Hash}", _log);

        await _p.OnScannedAsync(Snap());   // the same dialog: nothing new
        Assert.Single(_sent.Prompts);
    }

    [Theory]
    [InlineData("generating", SessionSource.ClaudeCode, "dialog")]
    [InlineData("waiting", SessionSource.RemoteControl, "dialog")]
    [InlineData("waiting", SessionSource.ClaudeCode, "none")]
    [InlineData("waiting", SessionSource.ClaudeCode, "null")]
    [InlineData("waiting", SessionSource.ClaudeCode, "form")]
    public async Task No_prompt_without_a_waiting_local_session_showing_a_dialog(string state, SessionSource source, string screen)
    {
        _pane.Screen = screen switch
        {
            "dialog" => Dialog,
            "none" => Cleared,
            "form" => PaneFixtures.MiniAskForm,
            _ => null,
        };
        await _p.OnScannedAsync([("s1", Status(state, source))]);
        Assert.Empty(_sent.Prompts);
    }

    [Theory]
    [InlineData("idle", null, "desk")]
    [InlineData(null, null, "gone")]
    [InlineData("waiting", Cleared, "desk")]
    public async Task A_pending_prompt_settled_elsewhere_resolves_on_every_phone(string? state, string? screen, string by)
    {
        await _p.OnScannedAsync(Snap());
        if (screen is not null) _pane.Screen = screen;
        await _p.OnScannedAsync(state is null ? [] : Snap(state));
        var r = Assert.Single(_sent.Resolved);
        Assert.Equal((_sent.Prompts[0].Rid, "s1", by), r);
        Assert.Equal(0, _p.PendingCount);
        Assert.Null(_p.PendingFor("s1"));
    }

    [Fact]
    public async Task A_new_dialog_while_still_waiting_resolves_the_old_as_gone_and_prompts_the_new()
    {
        await _p.OnScannedAsync(Snap());
        _pane.Screen = Dialog.Replace("tests/UnitTests", "tests/IntegrationTests");
        _now = _now.AddSeconds(2);
        await _p.OnScannedAsync(Snap());
        Assert.Equal(2, _sent.Prompts.Count);
        Assert.Equal((_sent.Prompts[0].Rid, "gone"), (_sent.Resolved.Single().Rid, _sent.Resolved.Single().By));
        Assert.NotEqual(_sent.Prompts[0].Rid, _sent.Prompts[1].Rid);
    }

    [Fact]
    public async Task A_scan_arriving_while_one_is_capturing_is_dropped()
    {
        _pane.Hold = new TaskCompletionSource();
        var first = _p.OnScannedAsync(Snap());
        await _p.OnScannedAsync(Snap());   // returns at once
        _pane.Hold.SetResult();
        await first;
        Assert.Single(_sent.Prompts);
    }

    [Fact]
    public async Task A_prompt_whose_push_cannot_fit_still_goes_without_e()
    {
        _pane.Screen = "────────────────────────────────────────\n Do you want to proceed?\n ❯ 1. Yes\n   2. Yes, and don't ask again for commands that start with "
                       + new string('s', 3000) + "\n   3. No\n";
        await _p.OnScannedAsync(Snap());
        Assert.Null(Assert.Single(_sent.Prompts).Push);
        Assert.Contains(_log, l => l.EndsWith(" no-e"));
    }

    // ---- answers -------------------------------------------------------------

    [Fact]
    public async Task A_valid_approve_presses_once_and_resolves_on_every_phone()
    {
        await _p.OnScannedAsync(Snap());
        var a = AnswerTo();
        await _p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Equal(["1"], _pane.Keys);
        Assert.Equal((a.RequestId, "s1", "phone:" + PhoneId), Assert.Single(_sent.Resolved));
        Assert.Empty(_sent.Refusals);
        Assert.Contains($"press {a.RequestId} 1", _log);
        Assert.Equal(0, _p.PendingCount);
    }

    [Fact]
    public async Task A_refused_answer_presses_nothing_and_tells_only_that_phone()
    {
        await _p.OnScannedAsync(Snap());
        var a = AnswerTo(signer: Other);
        await _p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Empty(_pane.Keys);
        Assert.Equal((PhoneId, a.RequestId, AnswerGate.BadSignature), Assert.Single(_sent.Refusals));
        Assert.Contains($"refuse {a.RequestId} bad_signature", _log);
        Assert.Empty(_sent.Resolved);
    }

    [Fact]
    public async Task An_answer_to_nothing_pending_is_not_pending()
    {
        await _p.OnScannedAsync(Snap());
        var a = AnswerTo() with { RequestId = "01JC9R0A5N2Q7W8X3E4T6Y9ZZZ" };
        var sig = Base64Url(Sign(Phone, AnswerSigning.Message(AnswerSigning.Body(a.RequestId, a.SessionId, a.Action, a.Scope, a.Nonce, a.ExpiresAt))));
        await _p.OnAnswerAsync(PhoneId, Phone.Public, a with { Sig = sig });
        Assert.Empty(_pane.Keys);
        Assert.Equal(AnswerGate.NotPending, Assert.Single(_sent.Refusals).Code);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_press_that_changes_nothing_is_stale_with_no_retry(bool stuck, bool pressFails)
    {
        await _p.OnScannedAsync(Snap());
        _pane.Stuck = stuck;
        _pane.PressFails = pressFails;
        var a = AnswerTo();
        await _p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Equal(["1"], _pane.Keys);
        Assert.Equal(AnswerGate.StalePrompt, Assert.Single(_sent.Refusals).Code);
        Assert.Contains($"press-no-effect {a.RequestId}", _log);
        Assert.True(_p.PendingFor("s1")!.Answered);

        // The same dialog on the next scan is not prompted again, and when the
        // desk answers it nothing more is sent: the phone already heard.
        await _p.OnScannedAsync(Snap());
        Assert.Single(_sent.Prompts);
        await _p.OnScannedAsync(Snap("generating"));
        Assert.Empty(_sent.Resolved);
        Assert.Equal(0, _p.PendingCount);
    }

    [Fact]
    public async Task Two_answers_racing_press_exactly_once()
    {
        await _p.OnScannedAsync(Snap());
        _pane.Stuck = true;   // even with the dialog still up, the second must not press
        var one = _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());
        var two = _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());
        await Task.WhenAll(one, two);
        Assert.Equal(["1"], _pane.Keys);
        Assert.Contains(_sent.Refusals, r => r.Code == AnswerGate.NotPending);
    }

    [Fact]
    public async Task Tell_presses_waits_for_the_dialog_to_clear_then_types_the_note()
    {
        await _p.OnScannedAsync(Snap());
        _pane.ClearAfterPolls = 3;
        const string note = "Use the integration tests";
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo(action: "tell", scope: AnswerSigning.TellScope(note), note: note));
        Assert.Equal(["3", "Enter"], _pane.Keys);
        Assert.Equal([note], _pane.Typed);
        Assert.Equal("phone:" + PhoneId, Assert.Single(_sent.Resolved).By);
    }

    [Fact]
    public async Task Tell_with_an_empty_note_types_nothing()
    {
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo(action: "tell", scope: AnswerSigning.TellScope(""), note: ""));
        Assert.Equal(["3"], _pane.Keys);
        Assert.Empty(_pane.Typed);
    }

    [Fact]
    public async Task Tell_whose_dialog_never_clears_types_nothing_and_is_stale()
    {
        await _p.OnScannedAsync(Snap());
        _pane.Stuck = true;
        const string note = "x";
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo(action: "tell", scope: AnswerSigning.TellScope(note), note: note));
        Assert.Equal(["3"], _pane.Keys);
        Assert.Empty(_pane.Typed);
        Assert.Equal(AnswerGate.StalePrompt, Assert.Single(_sent.Refusals).Code);
    }

    [Fact]
    public async Task Tell_whose_press_fails_types_nothing()
    {
        await _p.OnScannedAsync(Snap());
        _pane.PressFails = true;
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo(action: "tell", scope: AnswerSigning.TellScope(""), note: ""));
        Assert.Empty(_pane.Typed);
        Assert.Equal(AnswerGate.StalePrompt, Assert.Single(_sent.Refusals).Code);
    }

    // ---- tool, summary, label ------------------------------------------------

    [Theory]
    [InlineData("Bash command", "Bash")]
    [InlineData("Edit file", "Edit")]
    [InlineData("Would you like to run the following command?", "Bash")]
    [InlineData("Do you want to proceed?", "")]
    public void ToolOf(string line, string tool) => Assert.Equal(tool, PromptPipeline.ToolOf([line]));

    [Theory]
    [InlineData("Bash", "Wants to run a command")]
    [InlineData("Edit", "Wants to edit a file")]
    [InlineData("Write", "Wants to edit a file")]
    [InlineData("", "Needs an answer")]
    public void SummaryOf(string tool, string summary) => Assert.Equal(summary, PromptPipeline.SummaryOf(tool));

    [Fact]
    public async Task A_session_with_no_title_is_labelled_by_the_machine_alone()
    {
        await _p.OnScannedAsync([("s1", Status(title: ""))]);
        Assert.Equal("Test Mac", JsonDocument.Parse(_sent.Prompts[0].Push!).RootElement.GetProperty("label").GetString());
    }

    // ---- reconnect -----------------------------------------------------------

    [Fact]
    public async Task A_prompt_minted_while_the_relay_was_down_is_sent_on_the_welcome()
    {
        _sent.Online = false;
        await _p.OnScannedAsync(Snap());
        var minted = Assert.Single(_sent.Prompts);

        await _p.ResendPendingAsync();            // still down
        Assert.Equal(2, _sent.Prompts.Count);
        Assert.Contains($"prompt-resent {minted.Rid} (still not sent)", _log);

        _sent.Online = true;
        await _p.ResendPendingAsync();            // the welcome
        Assert.Equal(3, _sent.Prompts.Count);
        Assert.Equal(minted, _sent.Prompts[2]);   // same request id, same bytes
        Assert.Contains($"prompt-resent {minted.Rid}", _log);
    }

    // Lead's decision: a frame lost as the socket dropped looks delivered, so
    // every pending prompt goes out again on every welcome.
    [Fact]
    public async Task A_prompt_that_went_out_is_sent_again_on_every_welcome()
    {
        await _p.OnScannedAsync(Snap());
        await _p.ResendPendingAsync();
        await _p.ResendPendingAsync();
        Assert.Equal(3, _sent.Prompts.Count);
        Assert.Single(_sent.Prompts.Distinct());   // the same frame each time
    }

    [Fact]
    public async Task An_expired_or_answered_prompt_is_not_resent()
    {
        _sent.Online = false;
        await _p.OnScannedAsync(Snap());
        _now = _now.AddSeconds(1800);
        _sent.Online = true;
        await _p.ResendPendingAsync();
        Assert.Single(_sent.Prompts);

        _now = _now.AddSeconds(-1800);
        _pane.Stuck = true;
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());   // answered (no effect), still pending
        Assert.True(_p.PendingFor("s1")!.Answered);
        await _p.ResendPendingAsync();
        Assert.Single(_sent.Prompts);
    }

    // ---- first-run screens -----------------------------------------------------

    [Theory]
    [InlineData("conhost-codex-updatedialog.txt")]
    [InlineData("wt-codex-hooksdialog.txt")]
    [InlineData("wt-codex-trustdialog.txt")]
    public async Task A_first_run_screen_goes_out_open_on_desk_and_nothing_presses_it(string fixture)
    {
        _pane.Screen = AnswerGateTests.Screenshot(fixture);
        await _p.OnScannedAsync([("s1", Status(source: SessionSource.Codex))]);
        var sent = Assert.Single(_sent.Prompts);
        Assert.Equal(0, JsonDocument.Parse(sent.Prompt).RootElement.GetProperty("options").GetArrayLength());
        Assert.Equal(0, JsonDocument.Parse(sent.Push!).RootElement.GetProperty("options").GetArrayLength());
        Assert.Contains(_log, l => l.StartsWith("prompt ") && l.Contains(" desk-only"));

        foreach (var action in new[] { "approve", "option:1", "deny" })
            await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo(action: action));
        Assert.Empty(_pane.Keys);
        Assert.All(_sent.Refusals, r => Assert.Equal(AnswerGate.ForbiddenOption, r.Code));
        Assert.Equal(3, _sent.Refusals.Count);
    }

    [Fact]
    public async Task A_screen_that_can_be_read_but_not_pressed_goes_out_open_on_desk()
    {
        var p = new PromptPipeline(new PromptPipeline.Seams(_pane.Capture, _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add,
            CanPress: _ => false), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        Assert.Equal(0, JsonDocument.Parse(Assert.Single(_sent.Prompts).Prompt).RootElement.GetProperty("options").GetArrayLength());
        await p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());
        Assert.Empty(_pane.Keys);
        Assert.Equal(AnswerGate.ForbiddenOption, Assert.Single(_sent.Refusals).Code);
    }

    // Lead's rule: a route that cannot press without focus — Terminal.app first
    // of all — sends its prompts Open on desk, with no pressable option.
    [Fact]
    public async Task A_Terminal_app_prompt_carries_no_pressable_option()
    {
        var p = new PromptPipeline(new PromptPipeline.Seams(_pane.Capture, _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add,
            CanPress: s => TerminalPress.Silent(TerminalTyping.RouteFor(s, onMacOS: true, onWindows: false, TerminalTyping.Tools.None))), _sent, "Test Mac");
        var terminal = new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, TermProgram = TerminalTyping.TerminalAppProgram, Tty = "/dev/ttys004" };
        await p.OnScannedAsync([("s1", terminal)]);
        var sent = JsonDocument.Parse(Assert.Single(_sent.Prompts).Prompt).RootElement;
        Assert.Equal(0, sent.GetProperty("options").GetArrayLength());
        await p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());
        Assert.Empty(_pane.Keys);
    }

    // ---- rule 6 interleavings (Nadia's acceptance-4 try 1) ---------------------

    private static readonly string DialogB = Dialog.Replace("tests/UnitTests", "tests/IntegrationTests");

    // The screen is redrawn to another dialog AFTER the gate's fresh capture
    // (rule 6 passed against A) and BEFORE the key. On a route without a
    // single capture-and-press command, the last look before the key sees B,
    // so no key is sent at all: stale_prompt, and the watcher settles A as
    // gone for every phone on its next look.
    [Fact]
    public async Task A_redraw_between_the_gate_and_the_key_sends_no_key()
    {
        var captures = 0;
        var keys = new List<string>();
        // 1: the watcher's look; 2: the gate's fresh capture (rule 6 passes
        // against A); 3: the last look before the key, after the redraw.
        var p = new PromptPipeline(new PromptPipeline.Seams(
            _ => Task.FromResult<string?>(++captures <= 2 ? Dialog : DialogB),
            (_, k) => { keys.Add(k); return Task.FromResult(true); }, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        var a = AnswerTo();
        await p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Equal(3, captures);
        Assert.Empty(keys);
        Assert.Equal((a.RequestId, AnswerGate.StalePrompt), (Assert.Single(_sent.Refusals).Rid, _sent.Refusals[0].Code));
        Assert.False(p.PendingFor("s1")!.Answered);
        Assert.Empty(_sent.Resolved);

        await p.OnScannedAsync(Snap());          // the watcher sees B: A is gone for every phone
        Assert.Equal((a.RequestId, "gone"), (Assert.Single(_sent.Resolved).Rid, _sent.Resolved[0].By));
    }

    // On tmux the capture and the key are one invocation, and tmux cannot make
    // the key conditional, so a redraw that lands after the gate but before
    // that invocation IS pressed. The in-invocation capture says so, and the
    // answer is then never reported as the phone's: resolved by unknown, with
    // a warning.
    [Fact]
    public async Task A_key_that_landed_on_another_dialog_is_never_reported_as_the_phones()
    {
        var keys = new List<string>();
        var p = new PromptPipeline(new PromptPipeline.Seams(_pane.Capture, _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add,
            CaptureAndPress: (_, k, _) => { keys.Add(k); return Task.FromResult<(bool, string?)>((true, DialogB)); }), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        var a = AnswerTo();
        await p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Equal(["1"], keys);
        Assert.Equal((a.RequestId, "s1", "unknown"), Assert.Single(_sent.Resolved));
        Assert.Contains($"warning press-landed-elsewhere {a.RequestId} 1", _log);
        Assert.DoesNotContain(_log, l => l.Contains("phone:"));
        Assert.Equal(0, p.PendingCount);
    }

    // The same seam when the in-invocation capture IS the approved dialog: a
    // normal press, resolved by the phone.
    [Fact]
    public async Task A_key_that_landed_on_the_approved_dialog_is_the_phones()
    {
        var p = new PromptPipeline(new PromptPipeline.Seams(_pane.Capture, _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add,
            CaptureAndPress: async (s, k, approved) => { var before = await _pane.Capture(s); return approved(before) ? (await _pane.Press(s, k), before) : (false, before); }),
            _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        await p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());
        Assert.Equal(["1"], _pane.Keys);
        Assert.Equal("phone:" + PhoneId, Assert.Single(_sent.Resolved).By);
    }

    [Fact]
    public async Task A_failed_last_look_presses_nothing_and_is_no_effect()
    {
        var captures = 0;
        var p = new PromptPipeline(new PromptPipeline.Seams(
            _ => Task.FromResult<string?>(++captures <= 2 ? Dialog : null), _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());                     // capture 1
        await p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());   // capture 2: the gate; 3: the last look fails
        Assert.Empty(_pane.Keys);
        Assert.Contains(_log, l => l.StartsWith("press-no-effect "));
    }

    [Theory]
    [InlineData("desk")]
    [InlineData("gone")]
    public async Task Every_resolve_carries_its_sealed_plaintext_with_the_ambers_label(string by)
    {
        await _p.OnScannedAsync(Snap());
        if (by == "gone") _pane.Screen = DialogB;
        await _p.OnScannedAsync(by == "gone" ? Snap() : Snap("idle"));
        var push = JsonDocument.Parse(Assert.Single(_sent.ResolvedPushes)!).RootElement;
        Assert.Equal((1, "resolved", _sent.Prompts[0].Rid, "s1", by, "orbweaver on Test Mac"),
            (push.GetProperty("v").GetInt32(), push.GetProperty("t").GetString(), push.GetProperty("request_id").GetString(),
             push.GetProperty("session_id").GetString(), push.GetProperty("by").GetString(), push.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task A_phone_answer_resolve_and_an_unknown_one_carry_their_by_in_the_plaintext()
    {
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());
        Assert.Equal("phone:" + PhoneId, JsonDocument.Parse(_sent.ResolvedPushes.Single()!).RootElement.GetProperty("by").GetString());

        var p = new PromptPipeline(new PromptPipeline.Seams(_pane.Capture, _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add,
            CaptureAndPress: (_, _, _) => Task.FromResult<(bool, string?)>((true, DialogB))), _sent, "Test Mac");
        _pane.Screen = Dialog;
        await p.OnScannedAsync(Snap());
        await p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo(prompt: 1));
        Assert.Equal("unknown", JsonDocument.Parse(_sent.ResolvedPushes[^1]!).RootElement.GetProperty("by").GetString());
    }

    [Fact]
    public async Task A_cut_always_scope_goes_out_greyed_and_the_rest_stays_pressable()
    {
        _pane.Screen = AnswerGateTests.Screenshot("wt-grok-prompt.txt");
        await _p.OnScannedAsync([("s1", Status(source: SessionSource.Grok))]);
        var (_, _, prompt, push) = Assert.Single(_sent.Prompts);
        foreach (var json in new[] { prompt, push! })
        {
            var opts = JsonDocument.Parse(json).RootElement.GetProperty("options").EnumerateArray().ToList();
            var always = opts.Single(o => o.GetProperty("kind").GetString() == "always");
            Assert.True(always.GetProperty("scope_truncated").GetBoolean());
            Assert.All(opts.Where(o => o.GetProperty("kind").GetString() != "always"), o => Assert.False(o.TryGetProperty("scope_truncated", out _)));
        }
    }

    // ---- the settle check (lead's fix for QA's blank-gap rule-6 press) --------

    // The order that matters: the gate's capture, 150 ms, the second capture,
    // then the key with nothing in between.
    [Fact]
    public async Task The_key_follows_two_captures_150_ms_apart_with_nothing_between_the_second_and_it()
    {
        var events = new List<string>();
        var p = new PromptPipeline(new PromptPipeline.Seams(
            s => { events.Add("capture"); return _pane.Capture(s); },
            (s, k) => { events.Add("press " + k); return _pane.Press(s, k); },
            _pane.Type, () => _now, d => { events.Add($"delay {d.TotalMilliseconds}"); return Task.CompletedTask; }, _log.Add), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        events.Clear();
        await p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());
        Assert.Equal(["capture", "delay 150", "capture", "press 1", "delay 75", "capture"], events);
    }

    // Press to resolved used to cost a flat 500 ms. The recapture now looks every
    // 75 ms and resolves on the first look that shows the dialog gone.
    private PromptPipeline PipelineRecordingDelays(List<double> delays) =>
        new(new PromptPipeline.Seams(_pane.Capture, _pane.Press, _pane.Type, () => _now, d => { delays.Add(d.TotalMilliseconds); return Task.CompletedTask; }, _log.Add), _sent, "Test Mac");

    [Theory]
    [InlineData(0, 1)]   // gone at the first look
    [InlineData(1, 1)]
    [InlineData(3, 3)]   // gone at the third look: three polls, no more
    [InlineData(7, 7)]   // gone at the last look inside the patience: still took
    public async Task A_press_is_resolved_on_the_first_look_that_shows_the_dialog_gone(int clearAfterPolls, int polls)
    {
        var delays = new List<double>();
        var p = PipelineRecordingDelays(delays);
        await p.OnScannedAsync(Snap());
        _pane.ClearAfterPolls = clearAfterPolls;
        var a = AnswerTo();
        await p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Equal(Enumerable.Repeat(75d, polls).Prepend(150d), delays);
        Assert.Empty(_sent.Refusals);
        Assert.DoesNotContain(_log, l => l.StartsWith("press-no-effect"));
        Assert.Equal((a.RequestId, "s1", "phone:" + PhoneId), Assert.Single(_sent.Resolved));
    }

    [Fact]
    public async Task A_dialog_still_up_after_the_patience_is_no_effect_after_exactly_seven_looks()
    {
        var delays = new List<double>();
        var p = PipelineRecordingDelays(delays);
        await p.OnScannedAsync(Snap());
        _pane.ClearAfterPolls = 8;   // one look past the patience
        var a = AnswerTo();
        await p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Equal(Enumerable.Repeat(75d, 7).Prepend(150d), delays);
        Assert.Equal(AnswerGate.StalePrompt, Assert.Single(_sent.Refusals).Code);
        Assert.Contains($"press-no-effect {a.RequestId}", _log);
        Assert.Empty(_sent.Resolved);
    }

    // QA's blank-gap case: the gate saw A, and 150 ms later the pane is being
    // respawned (blank) or already shows B. No key, stale_prompt.
    [Theory]
    [InlineData("blank")]
    [InlineData("other")]
    public async Task A_screen_that_did_not_settle_on_the_approved_dialog_gets_no_key(string second)
    {
        var captures = 0;
        var keys = new List<string>();
        var p = new PromptPipeline(new PromptPipeline.Seams(
            _ => Task.FromResult<string?>(++captures <= 2 ? Dialog : second == "blank" ? "" : DialogB),
            (_, k) => { keys.Add(k); return Task.FromResult(true); }, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        var a = AnswerTo();
        await p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Empty(keys);
        Assert.Equal((a.RequestId, AnswerGate.StalePrompt), (Assert.Single(_sent.Refusals).Rid, _sent.Refusals[0].Code));
        Assert.DoesNotContain(_sent.Resolved, r => r.By.StartsWith("phone:"));
    }

    [Fact]
    public async Task An_answer_that_throws_is_logged_and_does_not_block_the_next()
    {
        var fail = true;
        var p = new PromptPipeline(new PromptPipeline.Seams(
            s => fail ? throw new InvalidOperationException("boom") : _pane.Capture(s), _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add), _sent, "Test Mac");
        fail = false;
        await p.OnScannedAsync(Snap());
        fail = true;
        var a = AnswerTo();
        await p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Contains($"warning answer-failed {a.RequestId} InvalidOperationException", _log);
        fail = false;
        await p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());   // the gate was released
        Assert.Equal(["1"], _pane.Keys);
    }

    [Fact]
    public async Task A_scan_that_throws_is_logged_and_the_next_scan_runs()
    {
        var fail = true;
        var p = new PromptPipeline(new PromptPipeline.Seams(
            s => fail ? throw new InvalidOperationException("boom") : _pane.Capture(s), _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        Assert.Contains("warning scan-failed InvalidOperationException", _log);
        fail = false;
        await p.OnScannedAsync(Snap());
        Assert.Single(_sent.Prompts);
    }

    // A "press" line means a key went in. A settle refusal logs
    // settle-refused and no press line; a real press logs press after the key.
    [Fact]
    public async Task A_settle_refusal_logs_settle_refused_and_no_press_line()
    {
        var captures = 0;
        var p = new PromptPipeline(new PromptPipeline.Seams(
            _ => Task.FromResult<string?>(++captures <= 2 ? Dialog : DialogB), _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        var a = AnswerTo();
        await p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Contains($"settle-refused {a.RequestId}", _log);
        Assert.DoesNotContain(_log, l => l.StartsWith("press "));
        Assert.Empty(_pane.Keys);
    }

    [Fact]
    public async Task The_press_line_comes_after_the_key()
    {
        var events = new List<string>();
        var p = new PromptPipeline(new PromptPipeline.Seams(_pane.Capture, (s, k) => { events.Add("key " + k); return _pane.Press(s, k); },
            _pane.Type, () => _now, _ => Task.CompletedTask, l => { if (l.StartsWith("press ")) events.Add("log press"); }), _sent, "Test Mac");
        await p.OnScannedAsync(Snap());
        await p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo());
        Assert.Equal(["key 1", "log press"], events);
    }

    [Fact]
    public async Task An_answer_is_logged_the_moment_it_reaches_the_desk()
    {
        await _p.OnScannedAsync(Snap());
        var a = AnswerTo();
        await _p.OnAnswerAsync(PhoneId, Phone.Public, a);
        var at = _log.IndexOf($"answer {a.RequestId} from {PhoneId}");
        Assert.True(at >= 0);
        Assert.True(at < _log.FindIndex(l => l.StartsWith("press ")));
    }

    // Two sessions waiting on the same dialog text are two prompts: a device
    // run rotates sessions to stay under the relay's per-session throttle.
    [Fact]
    public async Task Two_sessions_on_identical_dialogs_mint_two_prompts()
    {
        await _p.OnScannedAsync([("s1", Status()), ("s2", Status())]);
        Assert.Equal(2, _sent.Prompts.Count);
        Assert.Equal(["s1", "s2"], _sent.Prompts.Select(p => p.Sid));
        Assert.NotEqual(_sent.Prompts[0].Rid, _sent.Prompts[1].Rid);
    }
}
