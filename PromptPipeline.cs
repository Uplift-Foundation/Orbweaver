using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // What the pipeline hands to the relay. RelayClient implements it by
    // dispatching into RelayLink; a test implements it with a list.
    internal interface IPromptSender
    {
        // True when a frame was produced for at least one phone; false while
        // the relay is down (RelayLink sends nothing unwelcomed), which is
        // what ResendUndeliveredAsync later makes good.
        bool Prompt(string requestId, string sessionId, string prompt, string? pushPlaintext);
        void Resolved(string requestId, string sessionId, string by);
        void Refusal(string phoneId, string requestId, string sessionId, string code);
    }

    // CB-315 surface B, the desktop half of the amber loop:
    //   scan → a session waiting on a dialog → capture → PromptFingerprint →
    //   prompt (push amber, with e) to every phone;
    //   answer → AnswerGate's eight rules → mark answered, press once,
    //   recapture → resolved to every phone, or a sealed refusal to the one
    //   that answered;
    //   a dialog answered at the desk, replaced, or whose session went away →
    //   resolved by desk / gone.
    // Every outside effect is a seam (capture, press, type, clock, delay, log,
    // sender), so the whole loop runs in a unit test against a fake pane, and
    // desktop-only against a real tmux pane QA owns.
    //
    // One answer at a time, through _gate, for the whole pipeline: the gate's
    // rule 2 relies on Answered being set before a second answer is decided,
    // and an answer that awaited its capture while another was being decided
    // could otherwise press the same dialog twice.
    internal sealed class PromptPipeline
    {
        internal sealed record Seams(
            Func<SessionStatus, Task<string?>> Capture,
            Func<SessionStatus, string, Task<bool>> PressKey,
            Func<SessionStatus, string, Task<bool>> TypeLiteral,
            Func<DateTimeOffset> Now,
            Func<TimeSpan, Task> Delay,
            Action<string> Log,
            // Whether PressKey can reach this session. A screen the app can
            // read but not press goes out "Open on desk" rather than as a prompt
            // whose every answer would end in press-no-effect. Null: always.
            Func<SessionStatus, bool>? CanPress = null);

        // How long after a press the recapture waits, and how long a tell waits
        // for the dialog to clear before typing (plan §4: ≤ 4 s).
        internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);
        internal static readonly TimeSpan TellClearLimit = TimeSpan.FromSeconds(4);
        internal static readonly TimeSpan TellPoll = TimeSpan.FromMilliseconds(250);

        private sealed class Live
        {
            internal required PendingPrompt Pending;
            internal required SessionStatus Status;
            internal required string Prompt;
            internal required string? Push;
            internal bool Delivered;
        }

        private readonly Seams _seams;
        private readonly IPromptSender _send;
        private readonly string _machine;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);   // by session id
        private readonly NonceWindow _nonces = new();

        internal PromptPipeline(Seams seams, IPromptSender send, string machine)
        {
            _seams = seams;
            _send = send;
            _machine = machine;
        }

        internal int PendingCount => _live.Count;

        internal PendingPrompt? PendingFor(string sessionId) => _live.TryGetValue(sessionId, out var l) ? l.Pending : null;

        // ---- the watcher ------------------------------------------------------

        // A scan that arrives while the previous one is still capturing is
        // dropped rather than queued: the next scan carries the same news, and
        // a queue of captures behind a slow pane would only grow.
        private int _scanning;

        internal async Task OnScannedAsync(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var present = snapshot.ToDictionary(s => s.SessionId, s => s.Status, StringComparer.Ordinal);
                foreach (var (sid, live) in _live.ToList())
                {
                    if (!present.TryGetValue(sid, out var status)) Settle(sid, live, "gone");
                    else if (status.State != "waiting") Settle(sid, live, "desk");
                }

                foreach (var (sid, status) in snapshot)
                {
                    if (status.State != "waiting" || !status.IsLocalCli) continue;
                    var screen = await _seams.Capture(status).ConfigureAwait(false);
                    var fp = PromptFingerprint.Of(screen);
                    _live.TryGetValue(sid, out var live);
                    if (live is not null)
                    {
                        live.Status = status;
                        if (fp?.Hash == live.Pending.ScreenHash) continue;
                        // Answered at the desk and the next dialog is up, or
                        // the dialog is gone while the hook still says waiting.
                        Settle(sid, live, "desk");
                    }
                    if (fp is null || screen is null) continue;
                    // AskUserQuestion forms are answered through FormAnswer's
                    // own procedure on the desk; CB-315 sends no prompt for one.
                    if (ChatTranscript.ParseAskForm(screen) is not null) continue;
                    Mint(sid, status, fp, screen);
                }
            }
            finally
            {
                _gate.Release();
                Volatile.Write(ref _scanning, 0);
            }
        }

        // A pending prompt is over: tell every phone, unless the phone path
        // already did (Answered — its own resolved or refusal went out).
        private void Settle(string sid, Live live, string by)
        {
            _live.Remove(sid);
            if (live.Pending.Answered) return;
            _send.Resolved(live.Pending.RequestId, sid, by);
            _seams.Log($"resolved {live.Pending.RequestId} {by}");
        }

        private void Mint(string sid, SessionStatus status, PromptFingerprint fp, string screen)
        {
            var now = _seams.Now();
            var requestId = RelayLink.Ulid(now);
            var expires = now.ToUnixTimeSeconds() + PromptFrames.Lifetime;
            var cli = MirrorProtocol.CliFor(status.Source);
            const string kind = "permission";
            // A trust, update or hooks-review screen numbers its choices like a
            // permission dialog, so the fingerprint alone cannot tell them apart.
            // It goes to the phone as "Open on desk": the same prompt with no
            // options, and the gate refuses anything an answer names.
            var deskOnly = FirstRunDialogs.Is(screen) || !(_seams.CanPress?.Invoke(status) ?? true);
            var options = deskOnly ? [] : fp.Prompt.Options.Select(o =>
            {
                var c = OptionKinds.Classify(kind, o.Label, o.Mode);
                return new PromptOption(o.Key, o.Label, c.Kind, c.Scope);
            }).ToList();
            var preview = string.Join("\n", fp.Preview);
            var tool = ToolOf(fp.Preview);
            var prompt = PromptFrames.Prompt(requestId, sid, kind, cli, tool, preview, options, fp.Hash, expires);
            var label = string.IsNullOrWhiteSpace(status.Title) ? _machine : $"{status.Title} on {_machine}";
            var push = PromptFrames.FitPush(requestId, sid, label, tool, SummaryOf(tool), preview, expires, options, fp.Hash);

            var live = new Live { Pending = new PendingPrompt(requestId, sid, kind, fp.Hash, expires, deskOnly), Status = status, Prompt = prompt, Push = push?.Plaintext };
            _live[sid] = live;
            live.Delivered = _send.Prompt(requestId, sid, prompt, push?.Plaintext);
            _seams.Log($"prompt {requestId} {fp.Hash}{(deskOnly ? " desk-only" : "")}{(push is null ? " no-e" : "")}");
        }

        // "Bash command" → Bash (Claude Code); Codex's "Would you like to run
        // the following command?" → Bash; anything else names no tool.
        private static readonly Regex ToolLine = new(@"^(?<t>[A-Z][A-Za-z]*) (?:command|file)$", RegexOptions.CultureInvariant);

        internal static string ToolOf(IReadOnlyList<string> preview)
        {
            foreach (var line in preview)
            {
                var m = ToolLine.Match(line);
                if (m.Success) return m.Groups["t"].Value;
                if (line.Contains("run the following command", StringComparison.OrdinalIgnoreCase)) return "Bash";
            }
            return "";
        }

        // frames-v3 push_plaintext_e.summary: one of four fixed strings.
        internal static string SummaryOf(string tool) => tool switch
        {
            "Bash" => "Wants to run a command",
            "Edit" or "Write" or "Update" or "Create" => "Wants to edit a file",
            _ => "Needs an answer",
        };

        // A prompt minted while the relay was down produced no frame, and the
        // phone would never hear of it: on every welcome, each pending prompt
        // that never went out is sent now — same request_id, same bytes —
        // unless it was answered (it cannot have been, by a phone) or has
        // expired, which no phone could answer anyway. One that did go out is
        // not repeated: the relay queues action frames for an offline phone.
        internal async Task ResendUndeliveredAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = _seams.Now().ToUnixTimeSeconds();
                foreach (var live in _live.Values)
                {
                    if (live.Delivered || live.Pending.Answered || now >= live.Pending.ExpiresAt) continue;
                    live.Delivered = _send.Prompt(live.Pending.RequestId, live.Pending.SessionId, live.Prompt, live.Push);
                    _seams.Log($"prompt-resent {live.Pending.RequestId}{(live.Delivered ? "" : " (still not sent)")}");
                }
            }
            finally { _gate.Release(); }
        }

        // ---- answers ----------------------------------------------------------

        internal async Task OnAnswerAsync(string phoneId, byte[] phoneIdPublic, PhoneAnswer answer)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var live = _live.Values.FirstOrDefault(l => l.Pending.RequestId == answer.RequestId);
                var fresh = live is null ? null : await _seams.Capture(live.Status).ConfigureAwait(false);
                var d = AnswerGate.Decide(live?.Pending, answer, fresh, phoneId, phoneIdPublic, _seams.Now(), _nonces);
                if (!d.Press)
                {
                    Refuse(phoneId, answer, d.Code!);
                    return;
                }

                // Rule 8: marked before the press, so nothing decided after this
                // line can press this dialog again.
                var pending = live!.Pending;
                pending.Answered = true;
                _seams.Log($"press {answer.RequestId} {d.Key}");
                var tookEffect = answer.Action == "tell"
                    ? await PressTellAsync(live.Status, pending, d.Key!, answer.Note).ConfigureAwait(false)
                    : await PressOnceAsync(live.Status, pending, d.Key!).ConfigureAwait(false);

                if (!tookEffect)
                {
                    _seams.Log($"press-no-effect {answer.RequestId}");
                    _send.Refusal(phoneId, answer.RequestId, answer.SessionId, AnswerGate.StalePrompt);
                    // Left in place, answered: the dialog is still up and is the
                    // desk's to answer now. Not minted again while its hash
                    // stands, and settled silently when the session moves on.
                    return;
                }

                _live.Remove(answer.SessionId);
                _send.Resolved(answer.RequestId, answer.SessionId, "phone:" + phoneId);
                _seams.Log($"resolved {answer.RequestId} phone:{phoneId}");
            }
            finally { _gate.Release(); }
        }

        private void Refuse(string phoneId, PhoneAnswer answer, string code)
        {
            _seams.Log($"refuse {answer.RequestId} {code}");
            _send.Refusal(phoneId, answer.RequestId, answer.SessionId, code);
        }

        private async Task<bool> PressOnceAsync(SessionStatus status, PendingPrompt pending, string key)
        {
            if (!await _seams.PressKey(status, key).ConfigureAwait(false)) return false;
            await _seams.Delay(SettleDelay).ConfigureAwait(false);
            return AnswerGate.PressTookEffect(pending, await _seams.Capture(status).ConfigureAwait(false));
        }

        // tell: press the option, wait for the dialog to clear (the CLI then
        // shows its own input), and only then type the note and Enter. If the
        // dialog never clears the note is not typed anywhere.
        private async Task<bool> PressTellAsync(SessionStatus status, PendingPrompt pending, string key, string note)
        {
            if (!await _seams.PressKey(status, key).ConfigureAwait(false)) return false;
            var waited = TimeSpan.Zero;
            while (true)
            {
                await _seams.Delay(TellPoll).ConfigureAwait(false);
                waited += TellPoll;
                if (AnswerGate.PressTookEffect(pending, await _seams.Capture(status).ConfigureAwait(false))) break;
                if (waited >= TellClearLimit) return false;
            }
            if (note.Length > 0)
            {
                await _seams.TypeLiteral(status, note).ConfigureAwait(false);
                await _seams.PressKey(status, "Enter").ConfigureAwait(false);
            }
            return true;
        }
    }
}
