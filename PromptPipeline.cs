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
        // which the pipeline only logs: ResendPendingAsync sends every pending
        // prompt again on each welcome regardless.
        bool Prompt(string requestId, string sessionId, string prompt, string? pushPlaintext);
        // pushPlaintext: the sealed resolve's plaintext (PromptFrames.ResolvedPush),
        // which the phone's notification extension uses to replace the amber
        // with a quiet "Answered at the desk" alert.
        void Resolved(string requestId, string sessionId, string by, string? pushPlaintext);
        void Refusal(string phoneId, string requestId, string sessionId, string code);
        // CB-321 addendum 2: the persona this session's amber speaks as (its
        // roster row's), or null. A sender that knows no roster names none.
        PushPersona? PersonaFor(string sessionId) => null;
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
            Func<SessionStatus, bool>? CanPress = null,
            // The press together with the screen it landed on. Null: capture,
            // then press, as two steps (tests mostly; production passes
            // TerminalFocuser.CaptureAndPress, a single invocation on tmux).
            Func<SessionStatus, string, Func<string?, bool>, Task<(bool Pressed, string? Before)>>? CaptureAndPress = null);

        // What one press did. Took: the key went into the approved dialog and
        // the dialog went away. NoEffect: the key failed or the dialog is still
        // up. LandedElsewhere: the screen the key went into was NOT the
        // approved dialog — the residual check-to-press window was hit — so
        // nobody may claim the phone answered it.
        // Changed: the last look before the key showed another screen, so the
        // key was never sent — rule 6 again, one step later.
        internal enum PressOutcome { Took, NoEffect, LandedElsewhere, Changed }

        // How long after a press the recapture keeps looking for the dialog to
        // go away before calling the press NoEffect, and how long a tell waits
        // for the dialog to clear before typing (plan §4: ≤ 4 s).
        internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);

        // How often that recapture looks. It used to wait the whole SettleDelay
        // and look once, so every answer cost ~500 ms between the press and the
        // resolve even when the dialog was gone in a frame (gate (b): press to
        // resolved measured 548 ms on a Mac, 516 ms on Windows). Now it looks
        // every SettlePoll and resolves on the first look that shows the dialog
        // gone; SettleDelay is only the patience before NoEffect.
        internal static readonly TimeSpan SettlePoll = TimeSpan.FromMilliseconds(75);

        // The settle check (lead's fix for QA's blank-gap rule-6 press): the
        // gate's fresh capture, then this long, then a second capture, and the
        // key only if both still are the approved dialog. A dialog being torn
        // down, respawned or redrawn rarely reads as itself twice 150 ms apart.
        internal static readonly TimeSpan SettleCheck = TimeSpan.FromMilliseconds(150);
        internal static readonly TimeSpan TellClearLimit = TimeSpan.FromSeconds(4);
        internal static readonly TimeSpan TellPoll = TimeSpan.FromMilliseconds(250);

        private sealed class Live
        {
            internal required PendingPrompt Pending;
            internal required SessionStatus Status;
            internal required string Prompt;
            internal required string? Push;
            internal required string Label;
        }

        private readonly Seams _seams;
        private readonly IPromptSender _send;
        private readonly string _machine;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);   // by session id
        private readonly NonceWindow _nonces = new();

        // CB-316: one nonce table per phone across answers and launches
        // (frames-v4 launch.fields), so RelayRemote checks launches against it.
        internal NonceWindow Nonces => _nonces;

        internal PromptPipeline(Seams seams, IPromptSender send, string machine)
        {
            _seams = seams;
            _send = send;
            _machine = machine;
        }

        internal int PendingCount => _live.Count;

        internal PendingPrompt? PendingFor(string sessionId) => _live.TryGetValue(sessionId, out var l) ? l.Pending : null;

        // CB-323: what the activity frame's oldest amber says about a session's
        // minted prompt — request id, tool and command line. Read by RelayLink
        // under the client's lock on another thread than the one minting, so it
        // is a copy kept under its own lock rather than a look into _live.
        private readonly object _activityGate = new();
        private readonly Dictionary<string, ActivityPrompt> _activity = new(StringComparer.Ordinal);

        internal ActivityPrompt? ActivityFor(string sessionId)
        {
            lock (_activityGate) return _activity.GetValueOrDefault(sessionId);
        }

        private void Drop(string sid)
        {
            _live.Remove(sid);
            lock (_activityGate) _activity.Remove(sid);
        }

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
                        // Another dialog in its place: the old request is gone
                        // (plan's wording) — nobody here can say who answered
                        // it, or whether anyone did. No dialog while the hook
                        // still says waiting: answered at the desk.
                        Settle(sid, live, fp is null ? "desk" : "gone");
                    }
                    if (fp is null || screen is null) continue;
                    // An AskUserQuestion form goes to the phone as a question
                    // (CB-323): CB-315 sent none, so the phone saw the session
                    // waiting with nothing to answer, and froze on it.
                    Mint(sid, status, fp, screen, ChatTranscript.ParseAskForm(screen));
                }
            }
            // Fired and forgotten by the caller, like an answer: said, not lost,
            // and the next scan runs.
            catch (Exception ex)
            {
                _seams.Log($"warning scan-failed {ex.GetType().Name}");
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
            Drop(sid);
            if (live.Pending.Answered) return;
            _send.Resolved(live.Pending.RequestId, sid, by, PromptFrames.ResolvedPush(live.Pending.RequestId, sid, by, live.Label));
            _seams.Log($"resolved {live.Pending.RequestId} {by}");
        }

        private void Mint(string sid, SessionStatus status, PromptFingerprint fp, string screen, AskForm? form = null)
        {
            var now = _seams.Now();
            var requestId = RelayLink.Ulid(now);
            var expires = now.ToUnixTimeSeconds() + PromptFrames.Lifetime;
            var cli = MirrorProtocol.CliFor(status.Source);
            var kind = form is null ? "permission" : "question";
            // A trust, update or hooks-review screen numbers its choices like a
            // permission dialog, so the fingerprint alone cannot tell them apart.
            // It goes to the phone as "Open on desk": the same prompt with no
            // options, and the gate refuses anything an answer names.
            var questionOptions = form is null ? null : QuestionOptions(form);
            var deskOnly = FirstRunDialogs.Is(screen) || !(_seams.CanPress?.Invoke(status) ?? true) || questionOptions is { Count: 0 };
            var options = deskOnly ? [] : questionOptions ?? fp.Prompt.Options.Select(o =>
            {
                var c = OptionKinds.Classify(kind, o.Label, o.Mode);
                return new PromptOption(o.Key, o.Label, c.Kind, c.Scope, o.ScopeTruncated);
            }).ToList();
            var preview = form is null ? string.Join("\n", fp.Preview) : QuestionPreview(form);
            var tool = form is null ? ToolOf(fp.Preview) : "";
            var prompt = PromptFrames.Prompt(requestId, sid, kind, cli, tool, preview, options, fp.Hash, expires);
            var label = string.IsNullOrWhiteSpace(status.Title) ? _machine : $"{status.Title} on {_machine}";
            var persona = _send.PersonaFor(sid);
            var push = PromptFrames.FitPush(requestId, sid, label, tool, form is null ? SummaryOf(tool) : AsksAQuestion, preview, expires, options, fp.Hash, persona);

            // A question presses only what it offered (the gate's rule 7).
            var keys = form is null ? null : options.Select(o => o.Key).ToList();
            var live = new Live { Pending = new PendingPrompt(requestId, sid, kind, fp.Hash, expires, deskOnly, keys), Status = status, Prompt = prompt, Push = push?.Plaintext, Label = label };
            _live[sid] = live;
            // Before the send, so the activity frame that goes ahead of this
            // prompt's push already names it.
            // A question's line on the island is the question itself; its
            // preview's first line is the form's tab bar ("☐ Colour").
            lock (_activityGate) _activity[sid] = new ActivityPrompt(requestId, tool, form is null ? RelayActivity.CommandOf(fp.Preview) : form.Prompt.Title);
            _send.Prompt(requestId, sid, prompt, push?.Plaintext);
            _seams.Log($"prompt {requestId} {fp.Hash}{(deskOnly ? " desk-only" : "")}{(push is null ? " no-e" : "")} "
                + PushPersona.LogTagFor(persona, push is { PersonaDropped: false } && persona is not null));
        }

        // frames-v3 push_plaintext_e.summary for a question.
        internal const string AsksAQuestion = "Asks a question";

        // What of an AskUserQuestion form a phone may answer (CB-323): its own
        // choices on a single-select question, each pressed by its digit, which
        // selects it and moves on — to the next question, which goes out as a
        // prompt of its own, or the review. On the review, "Submit answers"
        // alone, so a phone can finish what it started; never "Cancel", which
        // throws every answer away. Never "Type something." (its digit on an
        // empty field declines the form) and never "Chat about this". A
        // multi-select question (ticks, then an unnumbered Submit row) offers
        // nothing: it goes as "Open on desk", which the phone shows instead of
        // a session that is waiting on nothing it was told about.
        internal static IReadOnlyList<PromptOption> QuestionOptions(AskForm form)
        {
            if (form.IsReview)
                return form.Prompt.Options.Where(o => o.Key == form.SubmitAnswersKey)
                    .Select(o => new PromptOption(o.Key, o.Label, OptionKinds.Option, null)).ToList();
            if (form.MultiSelect) return [];
            return form.Prompt.Options
                .Where(o => o.Key != form.FreeTextKey && o.Key != form.ChatAboutKey && !o.Mode)
                .Select(o => new PromptOption(o.Key, o.Label, OptionKinds.Option, null))
                .ToList();
        }

        // The question as the phone shows it: "Question 2 of 3" when the form
        // says so, then the question (and, on the review, what is about to be sent).
        internal static string QuestionPreview(AskForm form) =>
            AskFormView.ProgressOf(form) is { } progress ? progress + "\n" + AskFormView.TitleOf(form) : AskFormView.TitleOf(form);

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

        // On every welcome, every pending prompt goes out again — same
        // request_id, same bytes — unless it was answered or has expired, which
        // no phone could act on. Every one, not only those minted while the
        // relay was down: a frame written in the instant a socket drops is lost
        // with it and looks delivered, and a session blocked on someone who was
        // never told is worse than a repeated alert. The repeat is absorbed
        // three times over: the relay throttles alerts to one per (desktop,
        // session, phone) per 30 s (answering push_throttled), APNs's
        // collapse-id replaces rather than stacks, and the phone's PromptBook
        // dedupes on request_id. (Lead's decision on CB-315.)
        internal async Task ResendPendingAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = _seams.Now().ToUnixTimeSeconds();
                foreach (var live in _live.Values)
                {
                    if (live.Pending.Answered || now >= live.Pending.ExpiresAt) continue;
                    var sent = _send.Prompt(live.Pending.RequestId, live.Pending.SessionId, live.Prompt, live.Push);
                    _seams.Log($"prompt-resent {live.Pending.RequestId}{(sent ? "" : " (still not sent)")}");
                }
            }
            finally { _gate.Release(); }
        }

        // ---- answers ----------------------------------------------------------

        // watchPublic: that phone's accepted watch key, or null (CB-323).
        internal async Task OnAnswerAsync(string phoneId, byte[] phoneIdPublic, PhoneAnswer answer, byte[]? watchPublic = null)
        {
            // Gate (a)'s breakdown starts here: when the answer reached the desk.
            _seams.Log($"answer {answer.RequestId} from {phoneId}");
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var live = _live.Values.FirstOrDefault(l => l.Pending.RequestId == answer.RequestId);
                var fresh = live is null ? null : await _seams.Capture(live.Status).ConfigureAwait(false);
                var d = AnswerGate.Decide(live?.Pending, answer, fresh, phoneId, phoneIdPublic, _seams.Now(), _nonces, watchPublic);
                if (!d.Press)
                {
                    Refuse(phoneId, answer, d.Code!);
                    return;
                }

                // Rule 8: marked before the press, so nothing decided after this
                // line can press this dialog again.
                var pending = live!.Pending;
                pending.Answered = true;
                var outcome = answer.Action == "tell"
                    ? await PressTellAsync(live.Status, pending, answer.RequestId, d.Key!, answer.Note).ConfigureAwait(false)
                    : await PressOnceAsync(live.Status, pending, answer.RequestId, d.Key!).ConfigureAwait(false);

                if (outcome == PressOutcome.LandedElsewhere)
                {
                    // Never "phone": the transition the gate approved is not
                    // the one that happened. The dialog it approved is gone
                    // (another was on screen when the key landed), so it is
                    // settled for everyone, by an actor nobody can name.
                    _seams.Log($"warning press-landed-elsewhere {answer.RequestId} {d.Key}");
                    Drop(answer.SessionId);
                    _send.Resolved(answer.RequestId, answer.SessionId, "unknown", PromptFrames.ResolvedPush(answer.RequestId, answer.SessionId, "unknown", live.Label));
                    _seams.Log($"resolved {answer.RequestId} unknown");
                    return;
                }

                if (outcome == PressOutcome.Changed)
                {
                    // Nothing was pressed, so the prompt is not answered: the
                    // watcher settles it as gone for every phone on its next
                    // look, and this phone hears why now.
                    pending.Answered = false;
                    _seams.Log($"settle-refused {answer.RequestId}");
                    Refuse(phoneId, answer, AnswerGate.StalePrompt);
                    return;
                }

                if (outcome == PressOutcome.NoEffect)
                {
                    _seams.Log($"press-no-effect {answer.RequestId}");
                    _send.Refusal(phoneId, answer.RequestId, answer.SessionId, AnswerGate.StalePrompt);
                    // Left in place, answered: the dialog is still up and is the
                    // desk's to answer now. Not minted again while its hash
                    // stands, and settled silently when the session moves on.
                    return;
                }

                Drop(answer.SessionId);
                _send.Resolved(answer.RequestId, answer.SessionId, "phone:" + phoneId, PromptFrames.ResolvedPush(answer.RequestId, answer.SessionId, "phone:" + phoneId, live.Label));
                _seams.Log($"resolved {answer.RequestId} phone:{phoneId}");
            }
            // The caller fires this and forgets it, so an exception here would
            // vanish with the answer. Said instead; the gate is released below
            // either way, so the next answer is not stuck behind it.
            catch (Exception ex)
            {
                _seams.Log($"warning answer-failed {answer.RequestId} {ex.GetType().Name}");
            }
            finally { _gate.Release(); }
        }

        private void Refuse(string phoneId, PhoneAnswer answer, string code)
        {
            _seams.Log($"refuse {answer.RequestId} {code}");
            _send.Refusal(phoneId, answer.RequestId, answer.SessionId, code);
        }

        // The key and the screen it landed on, then: elsewhere, no effect, or
        // took (after the settle, the dialog is gone).
        // The gate's capture (rule 6) was the first look. After SettleCheck the
        // second look and the key follow with nothing of ours between them:
        // CaptureAndPress reads, asks Approved, and presses at once (on tmux the
        // key and a capture of the screen it landed on are one invocation).
        // "Approved" is the canonical hash, not the raw text: a live timer or
        // spinner outside the dialog (Grok's "22s", Codex's ◦/•) changes the raw
        // screen every read, and would never settle.
        private async Task<PressOutcome?> PressAsync(SessionStatus status, PendingPrompt pending, string requestId, string key)
        {
            bool Approved(string? screen) => PromptFingerprint.Of(screen)?.Hash == pending.ScreenHash;
            await _seams.Delay(SettleCheck).ConfigureAwait(false);
            var (pressed, before) = _seams.CaptureAndPress is { } both
                ? await both(status, key, Approved).ConfigureAwait(false)
                : await CaptureThenPress(status, key, Approved).ConfigureAwait(false);
            if (!pressed) return Approved(before) || before is null ? PressOutcome.NoEffect : PressOutcome.Changed;
            // Only now, with the key sent: a "press" line is a key that went
            // in, never one the settle check stopped (QA read 13 refused tries
            // as presses when this was logged before the second capture).
            _seams.Log($"press {requestId} {key}");
            if (!Approved(before)) return PressOutcome.LandedElsewhere;
            return null;   // went into the approved dialog; the caller checks it went away
        }

        private async Task<(bool, string?)> CaptureThenPress(SessionStatus status, string key, Func<string?, bool> approved)
        {
            var before = await _seams.Capture(status).ConfigureAwait(false);
            if (!approved(before)) return (false, before);
            return (await _seams.PressKey(status, key).ConfigureAwait(false), before);
        }

        private async Task<PressOutcome> PressOnceAsync(SessionStatus status, PendingPrompt pending, string requestId, string key)
        {
            if (await PressAsync(status, pending, requestId, key).ConfigureAwait(false) is { } early) return early;
            // The same test as before (the approved dialog's hash is gone), asked
            // sooner and again: a dialog that is still up after SettleDelay of
            // looks is as stale as it was after one look at SettleDelay. A single
            // look can catch a blank or half-drawn frame that is not the dialog
            // going away, so Took needs the hash absent on two looks in a row;
            // a streak begun inside the patience gets its confirming look.
            var gone = 0;
            for (var waited = TimeSpan.Zero; waited < SettleDelay || gone > 0; waited += SettlePoll)
            {
                await _seams.Delay(SettlePoll).ConfigureAwait(false);
                gone = AnswerGate.PressTookEffect(pending, await _seams.Capture(status).ConfigureAwait(false)) ? gone + 1 : 0;
                if (gone >= 2) return PressOutcome.Took;
            }
            return PressOutcome.NoEffect;
        }

        // tell: press the option, wait for the dialog to clear (the CLI then
        // shows its own input), and only then type the note and Enter. If the
        // dialog never clears the note is not typed anywhere.
        private async Task<PressOutcome> PressTellAsync(SessionStatus status, PendingPrompt pending, string requestId, string key, string note)
        {
            if (await PressAsync(status, pending, requestId, key).ConfigureAwait(false) is { } early) return early;
            var waited = TimeSpan.Zero;
            while (true)
            {
                await _seams.Delay(TellPoll).ConfigureAwait(false);
                waited += TellPoll;
                if (AnswerGate.PressTookEffect(pending, await _seams.Capture(status).ConfigureAwait(false))) break;
                if (waited >= TellClearLimit) return PressOutcome.NoEffect;
            }
            if (note.Length > 0)
            {
                await _seams.TypeLiteral(status, note).ConfigureAwait(false);
                await _seams.PressKey(status, "Enter").ConfigureAwait(false);
            }
            return PressOutcome.Took;
        }
    }
}
