using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // CB-428 (frames-v16 prompt, plan section 13.4): the answerable side of a Paperclip agent. Where
    // PromptPipeline mints a prompt from a dialog on a screen, this mints one from a need Paperclip
    // lists, an approval or a tool action, and carries a phone's answer out through IPaperclipActions
    // instead of pressing a key.
    //
    //   poll → for each agent that is already a row in the roster, its oldest approval or tool action,
    //   one at a time → prompt (push amber, with e) to every phone, once per item however many polls
    //   list it; the next is minted when that one resolves;
    //   the item leaves Paperclip's list → resolved by desk; the agent or its row goes, or the expiry
    //   passes → resolved by gone (and, for an expiry with the item still pending, a fresh prompt with
    //   no push);
    //   answer → AnswerGate rules 1-5, "still listed" as rule 6, approve|deny as rule 7 →
    //   DecideAsync → resolved by phone:<id>, or a refusal to the one that answered.
    //
    // A blocked task mints none (no yes or no). Every outside effect is a seam, so the loop runs in a
    // unit test against a fake.
    internal sealed class PaperclipPrompts
    {
        // Paperclip keeps an item pending for as long as it takes; frames-v3's 1800 is a terminal
        // dialog's lifetime (frames-v15 prompt.frame.expires_at).
        internal const int Lifetime = 86400;

        internal const string ApproveKey = "approve", DeclineKey = "decline";

        private sealed class Live
        {
            internal required PendingPrompt Pending;
            internal required PaperclipNeed Need;
            internal required string Prompt;
            internal required string? Push;
            internal required string Label;
            internal required string Tool;
            internal required string Command;
            internal required long Seq;   // mint order, which a ULID minted in the same millisecond cannot give
            // CB-450: the last answer whose POST was not known to have failed (which phone, which choice). It lives
            // and dies with this prompt, and is dropped when a different phone's or a different choice's answer is accepted.
            internal (string PhoneId, bool Approve)? Unconfirmed;
        }

        private readonly IPromptSender _send;
        private readonly Func<DateTimeOffset> _now;
        private readonly string _machine;
        private readonly Func<IPaperclipActions?> _actions;
        private readonly NonceWindow _nonces;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);   // by "kind:id"
        private readonly object _activityGate = new();
        private long _seq;
        // Items answered through here and not yet gone from Paperclip's list: a poll that started before the
        // answer landed must not mint them again.
        private readonly HashSet<string> _decided = new(StringComparer.Ordinal);

        internal PaperclipPrompts(IPromptSender send, Func<DateTimeOffset> now, string machine, Func<IPaperclipActions?> actions,
            NonceWindow nonces, Action<string> log)
        {
            _send = send; _now = now; _machine = machine; _actions = actions; _nonces = nonces; _log = log;
        }

        internal int PendingCount { get { lock (_activityGate) return _live.Count; } }

        internal static readonly IReadOnlyList<PromptOption> Options =
        [
            new PromptOption(ApproveKey, "Approve", OptionKinds.Approve, null),
            new PromptOption(DeclineKey, "Decline", OptionKinds.Deny, null),
        ];

        // CB-442: a runtime permission prompt is Paperclip's own "Allow once" / "Deny"; the keys stay the same, so
        // AnswerGate and the phone's answer path need no change, only the words on the buttons differ.
        internal static readonly IReadOnlyList<PromptOption> RuntimeOptions =
        [
            new PromptOption(ApproveKey, "Allow once", OptionKinds.Approve, null),
            new PromptOption(DeclineKey, "Deny", OptionKinds.Deny, null),
        ];

        internal static IReadOnlyList<PromptOption> OptionsFor(PaperclipNeed need) =>
            need.Kind == "runtime" ? RuntimeOptions : Options;

        // frames-v16 prompt.stable_id: base64url SHA-256 of "orbweaver-paperclip-v1\n" kind "\n" id.
        internal static string ScreenHash(string kind, string itemId) =>
            RelayCrypto.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes("orbweaver-paperclip-v1\n" + kind + "\n" + itemId)));

        internal static string Key(string kind, string itemId) => kind + ":" + itemId;

        // The amber loop's island line for a session (RelayLink.ActivityPrompts): the oldest pending prompt's.
        internal ActivityPrompt? ActivityFor(string sessionId)
        {
            lock (_activityGate)
            {
                var l = _live.Values.Where(v => v.Pending.SessionId == sessionId && !v.Pending.Answered)
                    .OrderBy(v => v.Seq).FirstOrDefault();
                return l is null ? null : new ActivityPrompt(l.Pending.RequestId, l.Tool, l.Command);
            }
        }

        internal bool HasPending(string sessionId)
        {
            lock (_activityGate) return _live.Values.Any(v => v.Pending.SessionId == sessionId && !v.Pending.Answered);
        }

        // One poll's view: the agents Paperclip lists and the session ids of the rows the phones hold.
        internal async Task OnPolledAsync(IReadOnlyList<PaperclipAgent> agents, ISet<string> roster)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var wanted = new HashSet<string>(StringComparer.Ordinal);
                var rows = new HashSet<string>(StringComparer.Ordinal);
                var byAgent = new List<(string Sid, PaperclipAgent Agent, List<PaperclipNeed> Needs)>();
                foreach (var a in agents)
                {
                    var sid = PaperclipScan.KeyPrefix + a.Id;
                    // "blocked" has no yes or no; the stand-in need for an agent whose status is
                    // pending_approval carries no id to approve.
                    var needs = a.Needs.Where(n => (n.Kind is "approval" or "action" || n.Kind == "runtime" && PaperclipActions.Answerable(n)) && n.Id != "pending" && !string.IsNullOrEmpty(n.Id))
                        .OrderBy(n => n.CreatedAt ?? DateTime.MaxValue).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
                    foreach (var n in needs) wanted.Add(Key(n.Kind, n.Id));
                    if (roster.Contains(sid)) { rows.Add(sid); byAgent.Add((sid, a, needs)); }
                }
                _decided.RemoveWhere(k => !wanted.Contains(k));

                // What left, and what passed its expiry still pending.
                var now = _now().ToUnixTimeSeconds();
                var refresh = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, live) in _live.ToList())
                {
                    var listed = wanted.Contains(key);
                    var expired = listed && now >= live.Pending.ExpiresAt && !live.Pending.Answered;
                    if (listed && !expired && rows.Contains(live.Pending.SessionId)) continue;
                    Remove(key);
                    if (live.Pending.Answered) continue;
                    var by = !listed && rows.Contains(live.Pending.SessionId) ? "desk" : "gone";
                    // CB-450: an answer of ours that timed out may have been applied late. If the item is decided the way that
                    // phone chose, and no other answer was accepted since, it is that phone's; anything else stays by desk.
                    if (by == "desk" && live.Unconfirmed is { } u && _actions() is IPaperclipConfirm confirm
                        && await confirm.DecidedTheWayAsync(live.Need, u.Approve, CancellationToken.None).ConfigureAwait(false))
                        by = "phone:" + u.PhoneId;
                    _send.Resolved(live.Pending.RequestId, live.Pending.SessionId, by,
                        PromptFrames.ResolvedPush(live.Pending.RequestId, live.Pending.SessionId, by, live.Label));
                    _log($"paperclip resolved {live.Pending.RequestId} {by}");
                    // A tool action past its own expiry is over in Paperclip, whatever the list still says for a poll or two:
                    // resolving it is right, minting it again with an expiry already past is not.
                    if (expired && rows.Contains(live.Pending.SessionId) && !PastOwnExpiry(live.Need, _now())) refresh.Add(key);
                }

                // One at a time per agent: its oldest need, when nothing of its is pending.
                foreach (var (sid, agent, needs) in byAgent)
                {
                    lock (_activityGate) if (_live.Values.Any(l => l.Pending.SessionId == sid)) continue;
                    var next = needs.FirstOrDefault(n => !_decided.Contains(Key(n.Kind, n.Id)) && !PastOwnExpiry(n, _now()));
                    if (next is null) continue;
                    var withPush = !refresh.Contains(Key(next.Kind, next.Id));
                    // A need must never arrive without its alert: the relay throttles an agent's alerts to one per 30 s, so the
                    // next prompt that would carry a push waits until 31 s after the last one went out.
                    if (withPush && _lastAmber.TryGetValue(sid, out var last) && _now() - last < AlertGap) continue;
                    Mint(sid, agent, next, withPush);
                    if (withPush) _lastAmber[sid] = _now();
                }
            }
            catch (Exception ex) { _log($"warning paperclip-poll-failed {ex.GetType().Name}"); }
            finally { _gate.Release(); }
        }

        private void Remove(string key) { lock (_activityGate) _live.Remove(key); }

        // frames-v16 prompt.expires_at: mint + 86400, or the item's own expiry when Paperclip gave one sooner
        // (a tool action request lives an hour).
        internal static long ExpiryFor(DateTimeOffset now, PaperclipNeed need)
        {
            var day = now.ToUnixTimeSeconds() + Lifetime;
            return need.ExpiresAt is { } own ? Math.Min(day, new DateTimeOffset(DateTime.SpecifyKind(own, DateTimeKind.Utc)).ToUnixTimeSeconds()) : day;
        }

        // Plan rev 4 (own_wake): a break within 30 s of a Wake pressed on this desktop is the user's own doing.
        internal static readonly TimeSpan OwnWakeWindow = TimeSpan.FromSeconds(30);

        internal static bool OwnWake(string sessionId, DateTimeOffset now, IReadOnlyDictionary<string, DateTime> wakes)
        {
            var agent = sessionId.StartsWith(PaperclipScan.KeyPrefix, StringComparison.Ordinal) ? sessionId[PaperclipScan.KeyPrefix.Length..] : sessionId;
            if (!wakes.TryGetValue(agent, out var at)) return false;
            var age = now - new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
            return age >= TimeSpan.Zero && age <= OwnWakeWindow;
        }

        internal static readonly TimeSpan AlertGap = TimeSpan.FromSeconds(31);
        private readonly Dictionary<string, DateTimeOffset> _lastAmber = new(StringComparer.Ordinal);

        private static bool PastOwnExpiry(PaperclipNeed need, DateTimeOffset now) =>
            need.ExpiresAt is { } own && new DateTimeOffset(DateTime.SpecifyKind(own, DateTimeKind.Utc)) <= now;

        internal static string PreviewOf(PaperclipNeed need, PaperclipAgent agent) =>
            PromptFrames.Cut(need.Title + "\n" + (!string.IsNullOrWhiteSpace(need.Summary) ? need.Summary
                : $"{agent.Name}" + (string.IsNullOrWhiteSpace(agent.Role) ? "" : $" ({agent.Role})")), PromptFrames.MaxPreview);

        private void Mint(string sid, PaperclipAgent agent, PaperclipNeed need, bool withPush)
        {
            var now = _now();
            var requestId = RelayLink.Ulid(now);
            var expires = ExpiryFor(now, need);
            var hash = ScreenHash(need.Kind, need.Id);
            var tool = need.Tool ?? (need.Kind == "action" ? "Tool action" : need.Kind == "runtime" ? "Permission" : "Approval");
            var options = OptionsFor(need);
            var preview = PreviewOf(need, agent);
            var prompt = PromptFrames.Prompt(requestId, sid, "permission", MirrorProtocol.CliPaperclip, tool, preview, options, hash, expires);
            var label = $"{agent.Name} on {_machine}";
            var push = withPush ? PromptFrames.FitPush(requestId, sid, label, tool, need.Title, preview, expires, options, hash) : null;
            var live = new Live
            {
                Pending = new PendingPrompt(requestId, sid, "permission", hash, expires, false, [ApproveKey, DeclineKey]),
                Need = need, Prompt = prompt, Push = push?.Plaintext, Label = label, Tool = tool, Command = need.Title, Seq = ++_seq,
            };
            lock (_activityGate) _live[Key(need.Kind, need.Id)] = live;
            // A re-mint after an expiry goes out with push none: the phone already had the alert.
            if (withPush) _send.Prompt(requestId, sid, prompt, push?.Plaintext);
            else _send.PromptQuiet(requestId, sid, prompt);
            _log($"paperclip prompt {requestId} {hash}{(!withPush ? " re-minted" : push is null ? " no-e" : "")}");
        }

        // Every pending prompt again, to every phone (a desk welcome) or to one (a phone that came
        // online or resynced), as PromptPipeline does.
        internal async Task ResendPendingAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = _now().ToUnixTimeSeconds();
                foreach (var l in _live.Values.ToList())
                    if (!l.Pending.Answered && now < l.Pending.ExpiresAt) _send.Prompt(l.Pending.RequestId, l.Pending.SessionId, l.Prompt, l.Push);
            }
            finally { _gate.Release(); }
        }

        internal async Task ResendPendingToAsync(string phoneId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = _now().ToUnixTimeSeconds();
                foreach (var l in _live.Values.ToList())
                    if (!l.Pending.Answered && now < l.Pending.ExpiresAt) _send.PromptTo(phoneId, l.Pending.RequestId, l.Pending.SessionId, l.Prompt);
            }
            finally { _gate.Release(); }
        }

        // Whether this prompt is one of ours: the router asks before handing an answer over.
        internal bool Owns(string requestId)
        {
            lock (_activityGate) return _live.Values.Any(l => l.Pending.RequestId == requestId);
        }

        internal async Task OnAnswerAsync(string phoneId, byte[] phoneIdPublic, PhoneAnswer answer, byte[]? watchPublic = null)
        {
            _log($"paperclip answer {answer.RequestId} from {phoneId}");
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Live? live;
                lock (_activityGate) live = _live.Values.FirstOrDefault(l => l.Pending.RequestId == answer.RequestId);
                // Rule 6, for a prompt with no screen: Paperclip still lists the item. A prompt not in the
                // table at all is refused by rule 2 (not_pending) through a null pending.
                var d = AnswerGate.DecideExternal(live?.Pending, answer, live is not null, Options, phoneId, phoneIdPublic, _now(), _nonces, watchPublic);
                if (!d.Press)
                {
                    Refuse(phoneId, answer, d.Code!);
                    if (live is null && d.Code == AnswerGate.NotPending)
                        _send.ResolvedQuiet(answer.RequestId, answer.SessionId, "gone");
                    return;
                }

                // Rule 8: marked before the call, so a second answer decided meanwhile cannot repeat it.
                // The gate is held across the POST, so another prompt's answer waits behind this one: at most the
                // POST's 20 s action timeout plus DecideAsync's 5 s confirming read, about 25 s (20 s before CB-450).
                live!.Pending.Answered = true;
                var approving = d.Key == ApproveKey;
                if (live.Unconfirmed is { } was && (was.PhoneId != phoneId || was.Approve != approving)) live.Unconfirmed = null;
                PaperclipOutcome outcome;
                try
                {
                    outcome = _actions() is { } actions
                        ? await actions.DecideAsync(live.Need, d.Key == ApproveKey, CancellationToken.None).ConfigureAwait(false)
                        : new PaperclipOutcome(PaperclipActionResult.Unreachable, "Paperclip is not connected");
                }
                catch (Exception ex)
                {
                    _log($"warning paperclip-action-failed {answer.RequestId} {ex.GetType().Name}");
                    outcome = new PaperclipOutcome(PaperclipActionResult.Unreachable);
                }

                if (outcome.Result == PaperclipActionResult.Done)
                {
                    var key = Key(live.Need.Kind, live.Need.Id);
                    Remove(key);
                    _decided.Add(key);
                    var by = "phone:" + phoneId;
                    _send.Resolved(answer.RequestId, answer.SessionId, by, PromptFrames.ResolvedPush(answer.RequestId, answer.SessionId, by, live.Label));
                    _log($"paperclip resolved {answer.RequestId} {by}");
                    return;
                }

                // Not carried out by this answer, so not answered: unreachable, refused or needing a
                // reconnect leaves the prompt pending and the phone may try again; already decided in
                // Paperclip leaves it for the next poll to resolve by desk, since that poll will no
                // longer list the item.
                live.Pending.Answered = false;
                if (outcome.Result == PaperclipActionResult.Unreachable) live.Unconfirmed = (phoneId, approving);
                Refuse(phoneId, answer, outcome.Result == PaperclipActionResult.AlreadyDecided ? AnswerGate.NotPending : AnswerGate.StalePrompt);
            }
            catch (Exception ex) { _log($"warning paperclip-answer-failed {answer.RequestId} {ex.GetType().Name}"); }
            finally { _gate.Release(); }
        }

        private void Refuse(string phoneId, PhoneAnswer answer, string code)
        {
            _log($"paperclip refuse {answer.RequestId} {code}");
            _send.Refusal(phoneId, answer.RequestId, answer.SessionId, code);
        }
    }

    // The seam Juno's actions implement (CB-427): what the desk can do for a need. PaperclipPrompts
    // codes against this; the real one calls Paperclip's approve / reject / decline endpoints.
}
