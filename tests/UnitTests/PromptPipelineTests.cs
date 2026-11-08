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
        void IPromptSender.Resolved(string r, string s, string by) => Resolved.Add((r, s, by));
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
}
