using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-428: PaperclipPrompts, the answerable side of a Paperclip agent. A need Paperclip lists becomes
/// one prompt per item however many polls list it; an answer is verified by the existing AnswerGate
/// rules and carried out through IPaperclipActions; every outcome is asserted on the frames that reached
/// the "relay" and the calls that reached the "actions".
/// </summary>
public class PaperclipPromptsTests
{
    private const string PhoneId = "ph_a1b2c3d4e5f60718";
    private const string AgentId = "3f2b8c1e-7a44-4d0e-9b61-5c2d8e0f1a77";
    private const string Sid = "paperclip:" + AgentId;
    private static readonly KeyPair Phone = KeyPairFromPrivate(SHA256.HashData("paperclip-phone"u8.ToArray()));
    private static readonly KeyPair Other = KeyPairFromPrivate(SHA256.HashData("paperclip-other"u8.ToArray()));

    private sealed class Sender : IPromptSender
    {
        public readonly List<(string Rid, string Sid, string Prompt, string? Push)> Prompts = new();
        public readonly List<(string Rid, string Sid, string By, string? Push)> Resolved = new();
        public readonly List<(string Phone, string Rid, string Code)> Refusals = new();
        public readonly List<(string Phone, string Rid)> PromptsTo = new();
        public readonly List<string> Quiet = new();
        bool IPromptSender.Prompt(string r, string s, string p, string? push) { Prompts.Add((r, s, p, push)); return true; }
        public readonly List<string> QuietPrompts = new();
        bool IPromptSender.PromptQuiet(string r, string s, string p) { QuietPrompts.Add(r); Prompts.Add((r, s, p, null)); return true; }
        void IPromptSender.Resolved(string r, string s, string by, string? push) => Resolved.Add((r, s, by, push));
        void IPromptSender.ResolvedQuiet(string r, string s, string by) { Quiet.Add(r); Resolved.Add((r, s, by, null)); }
        void IPromptSender.Refusal(string ph, string r, string s, string code) => Refusals.Add((ph, r, code));
        bool IPromptSender.PromptTo(string ph, string r, string s, string p) { PromptsTo.Add((ph, r)); return true; }
    }

    private sealed class Actions : IPaperclipActions
    {
        public readonly List<string> Calls = new();
        public PaperclipActionResult Result = PaperclipActionResult.Done;
        public Exception? Throw;
        public TaskCompletionSource? Hold;
        public async Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct)
        {
            Calls.Add($"{(approve ? "approve" : "decline")} {need.Kind} {need.Id}");
            if (Hold is not null) await Hold.Task;
            if (Throw is not null) throw Throw;
            return new PaperclipOutcome(Result);
        }
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
    }

    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1794111900);
    private readonly Sender _sent = new();
    private readonly Actions _act = new();
    private IPaperclipActions? _actions;
    private readonly List<string> _log = new();
    private readonly NonceWindow _nonces = new();
    private readonly PaperclipPrompts _p;
    private static readonly ISet<string> InRoster = new HashSet<string> { Sid };

    public PaperclipPromptsTests()
    {
        _actions = _act;
        _p = new PaperclipPrompts(_sent, () => _now, "Test Mac", () => _actions, _nonces, _log.Add);
    }

    private static PaperclipNeed Approval(string id = "a1f0c9d2-0b3e-4f55-8d6a-9e7c1b2a3d44", string title = "Hire a research agent", string tool = "Hire") =>
        new("approval", id, title, "http://pc/approvals/" + id, Tool: tool);
    private static PaperclipNeed Action(string id = "7c4e2b90-5d1a-4a6f-b3c8-0f9e8d7c6b5a") => new("action", id, "Send email", "http://pc/requests", Tool: "Tool action");
    private static PaperclipNeed Blocked() => new("blocked", "issue-1", "Blocked: CB-1 x", "http://pc/issues/1");

    private static PaperclipAgent Agent(string id = AgentId, string name = "Ledger bot", string role = "accountant", params PaperclipNeed[] needs) =>
        new(id, name, role, "", "active", null, "claude_local", needs.Length > 0 ? "waiting" : "idle", PaperclipMark.None, null, needs,
            null, null, null, null, null, DateTime.UtcNow, "http://pc/agents/" + id, false);

    private PhoneAnswer AnswerTo(int prompt = 0, string action = "approve", string scope = "", string note = "", KeyPair? signer = null, string? nonce = null,
        long? expires = null, string? hash = null)
    {
        var p = JsonDocument.Parse(_sent.Prompts[prompt].Prompt).RootElement;
        var rid = p.GetProperty("request_id").GetString()!;
        var sid = p.GetProperty("session_id").GetString()!;
        var exp = expires ?? p.GetProperty("expires_at").GetInt64();
        nonce ??= Base64Url(RandomNumberGenerator.GetBytes(16));
        var sig = Base64Url(Sign(signer ?? Phone, AnswerSigning.Message(AnswerSigning.Body(rid, sid, action, scope, nonce, exp))));
        return new PhoneAnswer(rid, sid, action, scope, note, nonce, exp, hash ?? p.GetProperty("screen_hash").GetString()!, sig);
    }

    private Task Poll(params PaperclipAgent[] agents) => _p.OnPolledAsync(agents, InRoster);
    private Task Answer(PhoneAnswer a) => _p.OnAnswerAsync(PhoneId, Phone.Public, a);

    // ---- minting -------------------------------------------------------------

    [Fact]
    public async Task An_approval_is_one_prompt_in_the_contracts_shape_however_many_polls_list_it()
    {
        await Poll(Agent(needs: Approval()));
        var (rid, sid, prompt, push) = Assert.Single(_sent.Prompts);
        var j = JsonDocument.Parse(prompt).RootElement;
        Assert.Equal(["t", "request_id", "session_id", "kind", "cli", "tool", "preview", "options", "screen_hash", "expires_at"],
            j.EnumerateObject().Select(o => o.Name).ToArray());
        Assert.Equal(("prompt", Sid, "permission", "paperclip", "Hire"),
            (j.GetProperty("t").GetString(), sid, j.GetProperty("kind").GetString(), j.GetProperty("cli").GetString(), j.GetProperty("tool").GetString()));
        Assert.Equal("Hire a research agent\nLedger bot (accountant)", j.GetProperty("preview").GetString());
        Assert.Equal(_now.ToUnixTimeSeconds() + 86400, j.GetProperty("expires_at").GetInt64());
        Assert.Equal(PaperclipPrompts.ScreenHash("approval", "a1f0c9d2-0b3e-4f55-8d6a-9e7c1b2a3d44"), j.GetProperty("screen_hash").GetString());
        var opts = j.GetProperty("options").EnumerateArray().Select(o => (o.GetProperty("key").GetString(), o.GetProperty("label").GetString(), o.GetProperty("kind").GetString())).ToArray();
        Assert.Equal([("approve", "Approve", "approve"), ("decline", "Decline", "deny")], opts);
        var e = JsonDocument.Parse(push!).RootElement;
        Assert.Equal(("Ledger bot on Test Mac", "Hire", rid), (e.GetProperty("label").GetString(), e.GetProperty("tool").GetString(), e.GetProperty("request_id").GetString()));

        _now = _now.AddSeconds(30);
        await Poll(Agent(needs: Approval()));                                   // the same item: nothing new, and the expiry is not re-minted
        Assert.Single(_sent.Prompts);
        Assert.Equal(1, _p.PendingCount);
    }

    [Fact]
    public async Task An_agent_has_one_pending_prompt_at_a_time_its_oldest_need_first_and_the_next_when_that_one_resolves()
    {
        var older = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var action = Action() with { CreatedAt = older };                                          // the tool action is the older one
        var approval = Approval() with { CreatedAt = older.AddHours(1) };
        await Poll(Agent(needs: [approval, action, Blocked(), new PaperclipNeed("approval", "pending", "Waiting for approval", "u"), new PaperclipNeed("approval", "", "x", "u")]));
        Assert.Equal(["Tool action"], _sent.Prompts.Select(p => JsonDocument.Parse(p.Prompt).RootElement.GetProperty("tool").GetString()).ToArray());
        await Poll(Agent(needs: [approval, action]));
        Assert.Single(_sent.Prompts);                                                             // not both, however many polls

        await Answer(AnswerTo());                                                                 // the first is answered ...
        await Poll(Agent(needs: [approval]));                                                     // ... and gone from the list: the next is minted
        Assert.Equal(["Tool action", "Hire"], _sent.Prompts.Select(p => JsonDocument.Parse(p.Prompt).RootElement.GetProperty("tool").GetString()).ToArray());
    }

    [Fact]
    public async Task Needs_with_no_created_time_go_by_id_and_a_blocked_or_pending_need_alone_mints_nothing()
    {
        await Poll(Agent(needs: [Approval(id: "b-2"), Approval(id: "a-1")]));
        Assert.Equal(PaperclipPrompts.ScreenHash("approval", "a-1"), JsonDocument.Parse(Assert.Single(_sent.Prompts).Prompt).RootElement.GetProperty("screen_hash").GetString());

        var other = new PaperclipPromptsTests();
        await other.Poll(Agent(needs: [Blocked(), new PaperclipNeed("approval", "pending", "Waiting for approval", "u")]));
        Assert.Empty(other._sent.Prompts);
    }

    [Fact]
    public async Task A_need_with_no_tool_wording_falls_back_by_kind_and_a_role_less_agent_is_named_alone()
    {
        await Poll(Agent(role: "", needs: [new PaperclipNeed("approval", "x1", "Review", "u")]));
        await Poll(Agent(id: "00000000-0000-4000-8000-0000000000aa", needs: [new PaperclipNeed("action", "x2", "Do it", "u")]));
        var tools = _sent.Prompts.Select(p => JsonDocument.Parse(p.Prompt).RootElement).ToList();
        Assert.Equal(["Approval"], tools.Select(j => j.GetProperty("tool").GetString()).ToArray());     // the second agent is not a roster row here
        Assert.Equal("Review\nLedger bot", tools[0].GetProperty("preview").GetString());
        var sid2 = "paperclip:00000000-0000-4000-8000-0000000000aa";
        await _p.OnPolledAsync([Agent(id: "00000000-0000-4000-8000-0000000000aa", needs: [new PaperclipNeed("action", "x2", "Do it", "u")])], new HashSet<string> { sid2 });
        Assert.Equal("Tool action", JsonDocument.Parse(_sent.Prompts[1].Prompt).RootElement.GetProperty("tool").GetString());
    }

    [Fact]
    public async Task An_agent_that_is_not_yet_a_row_in_the_roster_mints_nothing_until_it_is()
    {
        await _p.OnPolledAsync([Agent(needs: Approval())], new HashSet<string>());
        Assert.Empty(_sent.Prompts);
        Assert.Equal(0, _p.PendingCount);
        await Poll(Agent(needs: Approval()));
        Assert.Single(_sent.Prompts);
    }

    [Fact]
    public async Task The_previews_second_line_is_Paperclips_own_summary_when_it_gave_one_else_the_agent()
    {
        await Poll(Agent(needs: Approval() with { Summary = "Researcher, 40 USD per month" }));
        Assert.Equal("Hire a research agent\nResearcher, 40 USD per month", JsonDocument.Parse(_sent.Prompts[0].Prompt).RootElement.GetProperty("preview").GetString());
        var other = new PaperclipPromptsTests();
        await other.Poll(Agent(needs: Approval() with { Summary = "  " }));
        Assert.Equal("Hire a research agent\nLedger bot (accountant)", JsonDocument.Parse(other._sent.Prompts[0].Prompt).RootElement.GetProperty("preview").GetString());
    }

    [Fact]
    public async Task A_long_preview_is_cut_to_the_contracts_4000()
    {
        await Poll(Agent(needs: Approval(title: new string('x', 5000))));
        var preview = JsonDocument.Parse(_sent.Prompts[0].Prompt).RootElement.GetProperty("preview").GetString()!;
        Assert.Equal(4000, preview.Length);
        Assert.EndsWith("…", preview);
    }

    // ---- resolving -------------------------------------------------------------

    [Fact]
    public async Task When_the_need_leaves_the_list_the_prompt_is_resolved_by_desk_once_and_by_gone_when_the_agent_went()
    {
        await Poll(Agent(needs: Approval()));
        await Poll(Agent());                                                  // decided in Paperclip or at the desk
        var (rid, _, by, push) = Assert.Single(_sent.Resolved);
        Assert.Equal(("desk", _sent.Prompts[0].Rid), (by, rid));
        Assert.Contains("\"by\":\"desk\"", push);
        await Poll(Agent());
        Assert.Single(_sent.Resolved);                                         // not again

        await Poll(Agent(needs: Action()));
        await Poll();                                                           // the agent is gone altogether
        Assert.Equal("gone", _sent.Resolved[1].By);
        Assert.Equal(0, _p.PendingCount);
    }

    // ---- answering -------------------------------------------------------------

    [Theory]
    [InlineData("approve", "approve approval a1f0c9d2-0b3e-4f55-8d6a-9e7c1b2a3d44")]
    [InlineData("deny", "decline approval a1f0c9d2-0b3e-4f55-8d6a-9e7c1b2a3d44")]
    public async Task A_valid_answer_is_carried_out_in_Paperclip_then_resolved_by_that_phone(string action, string call)
    {
        await Poll(Agent(needs: Approval()));
        await Answer(AnswerTo(action: action));
        Assert.Equal([call], _act.Calls);
        var r = Assert.Single(_sent.Resolved);
        Assert.Equal(("phone:" + PhoneId, _sent.Prompts[0].Rid), (r.By, r.Rid));
        Assert.Empty(_sent.Refusals);
        Assert.Equal(0, _p.PendingCount);
        Assert.False(_p.HasPending(Sid));

        await Poll(Agent());                                                    // the item is gone from Paperclip: no second resolve
        Assert.Single(_sent.Resolved);
    }

    [Fact]
    public async Task A_tool_action_is_answered_through_the_action_endpoint()
    {
        await Poll(Agent(needs: Action()));
        await Answer(AnswerTo(action: "deny"));
        Assert.Equal(["decline action 7c4e2b90-5d1a-4a6f-b3c8-0f9e8d7c6b5a"], _act.Calls);
    }

    [Theory]
    [InlineData("always", "forbidden_option")]
    [InlineData("tell", "bad_signature")]            // a note it did not sign for: the gate's rule 1
    [InlineData("option:1", "forbidden_option")]
    [InlineData("other", "bad_signature")]
    public async Task Only_approve_and_deny_are_offered_so_anything_else_is_refused_and_nothing_is_carried_out(string action, string code)
    {
        await Poll(Agent(needs: Approval()));
        await Answer(AnswerTo(action: action, scope: action == "always" ? "x" : ""));
        Assert.Equal([code], _sent.Refusals.Select(r => r.Code).ToArray());
        Assert.Empty(_act.Calls);
        Assert.Equal(1, _p.PendingCount);
    }

    [Fact]
    public async Task The_gates_refusals_each_stop_the_answer_before_Paperclip_hears_of_it()
    {
        await Poll(Agent(needs: Approval()));
        await Answer(AnswerTo(signer: Other));                                                   // rule 1
        await Answer(AnswerTo(hash: "AAAA"));                                                    // rule 5
        var a = AnswerTo(nonce: Base64Url(new byte[16]));
        _now = _now.AddSeconds(86400 + 1);
        await Answer(a);                                                                         // rule 3
        Assert.Equal(["bad_signature", "stale_prompt", "expired"], _sent.Refusals.Select(r => r.Code).ToArray());
        Assert.Empty(_act.Calls);

        // A replay: the same nonce twice at a time the prompt is still live.
        _now = _now.AddSeconds(-86400 - 1);
        var good = AnswerTo(nonce: Base64Url(new byte[16] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }));
        _act.Result = PaperclipActionResult.Unreachable;
        await Answer(good);                                                                      // carried out: unreachable, so refused and still pending
        await Answer(good);
        Assert.Equal("replayed", _sent.Refusals[^1].Code);
        Assert.Single(_act.Calls);
    }

    [Fact]
    public async Task An_unknown_request_is_refused_not_pending_and_cleared_on_every_phone_as_gone()
    {
        await Poll(Agent(needs: Approval()));
        var a = AnswerTo();
        await Poll(Agent());                                                                      // resolved by desk meanwhile
        await Answer(a);
        Assert.Equal("not_pending", _sent.Refusals.Single().Code);
        Assert.Equal(("gone", true), (_sent.Resolved[^1].By, _sent.Quiet.Contains(a.RequestId)));
        Assert.Empty(_act.Calls);
    }

    [Fact]
    public async Task A_refusal_that_is_not_pending_for_a_held_prompt_does_not_clear_it()
    {
        await Poll(Agent(needs: Approval()));
        _act.Result = PaperclipActionResult.AlreadyDecided;                                        // already decided in Paperclip
        await Answer(AnswerTo());
        Assert.Equal("not_pending", Assert.Single(_sent.Refusals).Code);
        Assert.Empty(_sent.Resolved);
        Assert.Equal(1, _p.PendingCount);
        await Poll(Agent());                                                                      // the next poll no longer lists it
        Assert.Equal("desk", Assert.Single(_sent.Resolved).By);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paperclip_unreachable_or_throwing_is_stale_prompt_and_the_prompt_stays_pending_and_answerable_again(bool throws)
    {
        await Poll(Agent(needs: Approval()));
        _act.Result = PaperclipActionResult.Unreachable;
        if (throws) _act.Throw = new HttpRequestException("down");
        await Answer(AnswerTo());
        Assert.Equal("stale_prompt", Assert.Single(_sent.Refusals).Code);
        Assert.Empty(_sent.Resolved);
        Assert.True(_p.HasPending(Sid));
        if (throws) Assert.Contains(_log, l => l.Contains("paperclip-action-failed"));

        _act.Throw = null; _act.Result = PaperclipActionResult.Done;
        await Answer(AnswerTo());                                                                 // retried: now it works
        Assert.Equal("phone:" + PhoneId, Assert.Single(_sent.Resolved).By);
    }

    [Fact]
    public async Task A_second_answer_decided_while_the_first_is_in_flight_waits_behind_it_and_is_refused_not_pending()
    {
        await Poll(Agent(needs: Approval()));
        _act.Hold = new TaskCompletionSource();
        var first = Answer(AnswerTo());
        var second = Answer(AnswerTo());
        await Task.Delay(50);
        Assert.Single(_act.Calls);
        _act.Hold.SetResult();
        await Task.WhenAll(first, second);
        Assert.Single(_act.Calls);
        Assert.Equal("not_pending", Assert.Single(_sent.Refusals).Code);
    }

    [Fact]
    public async Task An_answer_loop_failure_is_logged_and_does_not_stick_the_gate()
    {
        await Poll(Agent(needs: Approval()));
        var a = AnswerTo();
        await _p.OnAnswerAsync(PhoneId, null!, a);                                                // a key the signature check cannot use
        Assert.Contains(_log, l => l.Contains("paperclip-answer-failed") || l.Contains("paperclip refuse"));
        await Answer(AnswerTo());
        Assert.Single(_act.Calls);
    }

    [Fact]
    public async Task A_poll_that_throws_is_logged_and_the_next_one_still_runs()
    {
        await _p.OnPolledAsync([Agent(needs: Approval())], null!);
        Assert.Contains(_log, l => l.Contains("paperclip-poll-failed"));
        await Poll(Agent(needs: Approval()));
        Assert.Single(_sent.Prompts);
    }

    // ---- replays and bookkeeping -----------------------------------------------------

    [Fact]
    public async Task A_welcome_resends_every_live_prompt_with_its_push_and_a_phone_coming_online_gets_them_quietly_and_expired_ones_go_nowhere()
    {
        await Poll(Agent(needs: [Approval()]));
        await _p.ResendPendingAsync();
        Assert.Equal(2, _sent.Prompts.Count);
        Assert.Equal(_sent.Prompts[0].Push, _sent.Prompts[1].Push);
        await _p.ResendPendingToAsync(PhoneId);
        Assert.Equal([(PhoneId, _sent.Prompts[0].Rid)], _sent.PromptsTo);

        _now = _now.AddSeconds(86400);
        await _p.ResendPendingAsync();
        await _p.ResendPendingToAsync(PhoneId);
        Assert.Equal(2, _sent.Prompts.Count);
        Assert.Single(_sent.PromptsTo);
    }

    [Fact]
    public async Task Owns_and_ActivityFor_name_the_oldest_pending_prompt_and_stop_once_it_is_answered()
    {
        Assert.Null(_p.ActivityFor(Sid));
        Assert.False(_p.Owns("nope"));
        await Poll(Agent(needs: [Approval(), Action()]));
        Assert.True(_p.Owns(_sent.Prompts[0].Rid));
        var a = _p.ActivityFor(Sid)!;
        Assert.Equal(("Tool action", "Send email"), (a.Tool, a.Command));       // no created times: by id, and "7c4e" sorts before "a1f0"
        Assert.Null(_p.ActivityFor("paperclip:other"));
        Assert.True(_p.HasPending(Sid));
    }

    [Fact]
    public void The_screen_hash_is_stable_per_item_and_differs_by_kind_and_id()
    {
        Assert.Equal(PaperclipPrompts.ScreenHash("approval", "x"), PaperclipPrompts.ScreenHash("approval", "x"));
        Assert.NotEqual(PaperclipPrompts.ScreenHash("approval", "x"), PaperclipPrompts.ScreenHash("action", "x"));
        Assert.NotEqual(PaperclipPrompts.ScreenHash("approval", "x"), PaperclipPrompts.ScreenHash("approval", "y"));
    }

    // ---- the pin's remaining rules ------------------------------------------------

    [Fact]
    public async Task A_prompt_whose_item_is_still_pending_at_its_expiry_is_resolved_gone_and_minted_afresh_with_push_none()
    {
        await Poll(Agent(needs: Approval()));
        var first = _sent.Prompts[0];
        _now = _now.AddSeconds(86400);
        await Poll(Agent(needs: Approval()));
        Assert.Equal("gone", Assert.Single(_sent.Resolved).By);
        var (rid, _, prompt, push) = _sent.Prompts[1];
        Assert.NotEqual(first.Rid, rid);
        Assert.Null(push);
        Assert.Equal([rid], _sent.QuietPrompts);                                                  // the quiet send: no push class
        Assert.Equal(_now.ToUnixTimeSeconds() + 86400, JsonDocument.Parse(prompt).RootElement.GetProperty("expires_at").GetInt64());
        Assert.False(JsonDocument.Parse(prompt).RootElement.TryGetProperty("quiet", out _));       // not marked as an overlay
        Assert.Equal(1, _p.PendingCount);
    }

    [Fact]
    public async Task When_the_agents_row_leaves_the_roster_or_the_agent_goes_the_prompt_is_resolved_gone()
    {
        await Poll(Agent(needs: Approval()));
        await _p.OnPolledAsync([Agent(needs: Approval())], new HashSet<string>());                 // listed, but no longer a row
        Assert.Equal("gone", Assert.Single(_sent.Resolved).By);
        Assert.Equal(0, _p.PendingCount);
    }

    [Fact]
    public async Task A_poll_that_began_before_an_answer_landed_does_not_mint_the_answered_item_again()
    {
        await Poll(Agent(needs: Approval()));
        await Answer(AnswerTo());
        await Poll(Agent(needs: Approval()));                                                      // a lagging poll still lists it
        Assert.Single(_sent.Prompts);
        await Poll(Agent());                                                                        // gone from the list: forgotten
        await Poll(Agent(needs: Approval()));                                                      // listed again (a new ask): minted again
        Assert.Equal(2, _sent.Prompts.Count);
    }

    [Theory]
    [InlineData("Refused")]
    [InlineData("NeedsReconnect")]
    [InlineData("Unreachable")]
    public async Task Every_outcome_that_did_not_settle_is_stale_prompt_and_leaves_the_prompt_pending(string name)
    {
        var result = Enum.Parse<PaperclipActionResult>(name);
        await Poll(Agent(needs: Approval()));
        _act.Result = result;
        await Answer(AnswerTo());
        Assert.Equal("stale_prompt", Assert.Single(_sent.Refusals).Code);
        Assert.True(_p.HasPending(Sid));
    }

    // An outage must not resolve and re-alert: PaperclipSessions keeps the last needs through a failed poll, so the
    // same needs reach the loop again afterwards and nothing is minted twice.
    [Fact]
    public async Task Needs_then_the_same_needs_through_an_outage_then_again_is_one_prompt_and_one_amber()
    {
        await Poll(Agent(needs: Approval()));
        await Poll(Agent(needs: Approval()));
        await Poll(Agent(needs: Approval()));
        Assert.Single(_sent.Prompts);
        Assert.Empty(_sent.Resolved);
        Assert.NotNull(_sent.Prompts[0].Push);
    }

    [Fact]
    public async Task With_no_actions_available_an_answer_is_stale_prompt_and_nothing_is_lost()
    {
        await Poll(Agent(needs: Approval()));
        _actions = null;                                                                            // Paperclip switched off or no company
        await Answer(AnswerTo());
        Assert.Equal("stale_prompt", Assert.Single(_sent.Refusals).Code);
        Assert.True(_p.HasPending(Sid));
    }
}

// CB-428: the own-wake window and a tool action's own expiry, and the pin between the mark's wording and the why.
public class PaperclipPromptsRulesTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1794111900);

    [Theory]
    [InlineData(5, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(-5, false)]            // a wake from the future is not one
    public void A_break_within_thirty_seconds_of_a_wake_here_is_the_users_own(int secondsAgo, bool own)
    {
        var wakes = new Dictionary<string, DateTime> { ["a1"] = T.UtcDateTime.AddSeconds(-secondsAgo) };
        Assert.Equal(own, PaperclipPrompts.OwnWake("paperclip:a1", T, wakes));
        Assert.Equal(own, PaperclipPrompts.OwnWake("a1", T, wakes));                   // a bare agent id reads the same
        Assert.False(PaperclipPrompts.OwnWake("paperclip:other", T, wakes));           // the control: another agent
    }

    [Fact]
    public void A_tool_actions_own_expiry_is_the_prompts_when_it_is_sooner_and_a_later_or_missing_one_is_not()
    {
        var need = new PaperclipNeed("action", "x", "t", "u");
        Assert.Equal(T.ToUnixTimeSeconds() + 86400, PaperclipPrompts.ExpiryFor(T, need));
        Assert.Equal(T.ToUnixTimeSeconds() + 3600, PaperclipPrompts.ExpiryFor(T, need with { ExpiresAt = T.UtcDateTime.AddHours(1) }));
        Assert.Equal(T.ToUnixTimeSeconds() + 86400, PaperclipPrompts.ExpiryFor(T, need with { ExpiresAt = T.UtcDateTime.AddDays(3) }));
    }

    // The why is read from the reason PaperclipApi.MarkFor words. If someone rewords a reason, this fails
    // here and not as a wrong word on a phone.
    [Fact]
    public void Every_Broke_branch_of_MarkFor_maps_to_its_why()
    {
        PaperclipApi.RawAgent Agent(string status, string? err = null) =>
            new("a", "A", "r", "t", status, null, "x", 0, 0, null, err, null);
        PaperclipApi.RawRun Run(string status, string? code = null) => new("r", "a", status, null, null, code, null, null, null, false);
        string Why(PaperclipApi.RawAgent a, PaperclipApi.RawRun? r) { var (m, reason) = PaperclipApi.MarkFor(a, r); Assert.Equal(PaperclipMark.Broke, m); return RelayRoster.WhyOf(reason); }

        Assert.Equal("run_failed", Why(Agent("idle"), Run("failed")));
        Assert.Equal("run_failed", Why(Agent("idle"), Run("failed", "process_lost")));
        Assert.Equal("timed_out", Why(Agent("idle"), Run("timed_out")));
        Assert.Equal("error", Why(Agent("error"), null));
        Assert.Equal("error", Why(Agent("error", "adapter exploded at /Users/x/secret"), null));
    }
}
