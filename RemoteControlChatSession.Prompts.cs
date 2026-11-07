using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // CB-307 addendum item 3: answering a question form on another machine.
    //
    // The far session's pane is on the far machine, so the form is read there
    // (PROMPT) and answered there (ANSWER); this side only draws what came back
    // and says which option was chosen. Nothing here types into a terminal or
    // reads a screen, and nothing ever presses Escape: on some of these screens
    // Escape does something other than cancel.
    //
    // **Asked only while it can matter.** The panel is open, a live view is
    // agreed, and the far roster says the session is `waiting` — checked on open,
    // on every roster change, and then every PollEvery until the state leaves
    // `waiting`. A closed panel or a session that is not waiting costs nothing.
    //
    // The transport is two seams, so this class is decided by rules a test can
    // hand answers to, and wired to the client by Ask/Answer's defaults.
    internal sealed partial class RemoteControlChatSession : IRemoteChatPrompts, IRemoteChatFreeText
    {
        // What one ask came back with, reduced to what this side decides on.
        internal enum PromptReplyKind
        {
            // A form (or ordinary dialog) is on the far screen.
            Prompt,
            // Nothing readable is waiting right now: the session may not be on
            // a dialog yet, or the screen is not one. Asked again next tick.
            NoPrompt,
            // This far Buddy cannot answer, or its pane cannot be read, or its
            // owner switched replying off. Stop asking: today's "answer it in
            // the terminal" view is the honest one.
            Unsupported,
            // A one-off failure. Asked again next tick.
            Transient,
            // The far machine has not said yet that it answers PROMPT, which is
            // not the same as saying it does not: the answer rides on the roster
            // reply, and a panel can be asking a moment before it lands. Asked
            // again next tick, and never settled as unsupported.
            NotYet,
        }

        internal sealed record PromptReply(PromptReplyKind Kind, ChatPrompt? Prompt = null, string? Fingerprint = null);

        // What an answer came back with. Changed carries the form as it is now,
        // for when the screen moved on between asking and answering: the key was
        // not pressed, and the user is shown the new question instead.
        //
        // Refused is the far side declining the request as asked, with nothing
        // pressed (a key not on the screen, text where none is taken, a commit on
        // a question with nothing to commit): a different fact from the screen
        // having moved on, and worded differently.
        //
        // AlreadyAnswered is the far side saying nothing is waiting any more
        // (ErrNoPrompt): the form is gone, which is what answering it does, so a
        // late second click on it is not a failure and is never reported as one.
        internal enum AnswerKind { Done, Changed, AlreadyAnswered, Refused, Unsupported, Failed }

        internal sealed record AnswerReply(AnswerKind Kind, ChatPrompt? Prompt = null, string? Fingerprint = null);

        // How often an unchanged waiting form is re-read.
        internal static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

        // The seams, defaulting to the link's client. A test replaces them to
        // hand over answers without a socket.
        internal Func<CancellationToken, Task<PromptReply>>? Ask { get; set; }
        internal Func<string, string?, string?, Task<AnswerReply>>? Answer { get; set; }

        // Whether the transport can carry typed text with an answer. It can: the
        // far side moves the cursor onto the row, types, reads it back and only
        // then presses Enter. Kept as a switch because a form's "Type
        // something." option is withheld whenever it is false, rather than
        // offered and left half-done.
        internal bool CarriesText { get; set; } = true;
        internal Func<bool>? WaitingNow { get; set; }
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

        public ChatPrompt? Prompt { get; private set; }

        public event Action? PromptChanged;

        // How long the form just answered is held back if it is read again
        // unchanged. The far screen takes a moment to advance after a key, and
        // an immediate re-ask reads the question that was just answered, which
        // would put live buttons back on it — and a second click on them answers
        // the NEXT question blind (or the same one twice).
        internal static readonly TimeSpan SettleAfterAnswer = TimeSpan.FromSeconds(4);

        internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        private string? _answeredFingerprint;
        private DateTime _answeredAt;

        // Which read of the far screen is current. Bumped when an answer is sent
        // and again when it comes back, so a read that began before either — a
        // poll already in flight, whose reply is the screen as it was before the
        // key landed — is dropped when it arrives instead of being drawn as
        // current. That is how the final review screen showed twice: the old
        // form returned after the answer had gone out and was put back up with
        // live buttons.
        private int _generation;
        private bool _answering;

        private string? _promptFingerprint;
        private bool _promptsUnsupported;
        private bool _polling;
        private CancellationTokenSource? _pollCts;
        private TaskCompletionSource? _wake;

        // For a test: agree a live view without a client behind it, so the rules
        // above can be driven with the seams alone.
        internal void AssumeMirroringForTests() => _mirroring = true;

        private bool Waiting() => (WaitingNow ?? RosterSaysWaiting)();

        // What the far roster says this session is doing, for the log line that
        // explains why a form was not asked for. Null when it says nothing.
        private string? RosterStatus() => RemoteControlSessions.MirrorFor(_account, _remoteName)?.Status;

        // Why a form was not asked for, said once per reason per session and
        // changed reason: a panel polls every few seconds, and a silent skip is
        // how "the form never appeared" went unexplained. Never any screen text.
        private string? _lastSkip;

        internal void SaySkip(string reason, string detail)
        {
            var line = $"{reason} {detail}".TrimEnd();
            if (_lastSkip == line) return;

            _lastSkip = line;
            PromptLog("prompt-skip", $"n={_remoteName} {line}");
        }

        // Its own sink, apart from the session's Log, so what this says never
        // mixes into the open/reopen lines that sink is asserted on. MirrorLog in
        // the app; a capture in a test.
        internal Action<string, string> PromptLog { get; set; } = (what, detail) => MirrorLog.Say(what, detail);

        // The far roster's own word. Hana's promotion on the far host turns a
        // dialog-blocked session's `idle` into `waiting` before it is served.
        private bool RosterSaysWaiting() =>
            string.Equals(
                RemoteControlSessions.MirrorFor(_account, _remoteName)?.Status,
                "waiting", StringComparison.OrdinalIgnoreCase);

        // Called when something that decides whether to ask may have changed:
        // the panel opened, the roster landed, an answer went in.
        internal void EnsurePromptPolling()
        {
            if (_disposed || !_panelOpen || !_mirroring || _promptsUnsupported)
            {
                SaySkip("not-eligible",
                    $"disposed={_disposed} panel-open={_panelOpen} mirroring={_mirroring} unsupported={_promptsUnsupported}");
                return;
            }

            if (!Waiting())
            {
                // Left waiting: whatever was on show is not being asked any more.
                SaySkip("not-waiting", $"roster-status={RosterStatus() ?? "none"} account={_account}");
                ClearPrompt();
                return;
            }

            if (_polling)
            {
                SaySkip("already-polling", "");
                return;
            }

            _polling = true;
            _pollCts = new CancellationTokenSource();
            _ = PollAsync(_pollCts.Token);
        }

        private async Task PollAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && !_disposed && _panelOpen && !_promptsUnsupported)
                {
                    if (!Waiting())
                    {
                        ClearPrompt();
                        return;
                    }

                    // Not while an answer is in flight, and never apply a read that
                    // began before the latest answer: see _generation.
                    if (!_answering)
                    {
                        var generation = _generation;
                        var reply = await SafeAsk(ct).ConfigureAwait(true);

                        if (ct.IsCancellationRequested) return;

                        if (generation == _generation && !_answering) ApplyReply(reply);
                        else SaySkip("stale-read-dropped", "a read that began before the latest answer");

                        if (_promptsUnsupported) return;
                    }

                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await Task.WhenAny(Delay(PollEvery, ct), _wake.Task).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                // The panel closed or the session was disposed mid-wait.
            }
            finally
            {
                _polling = false;
            }
        }

        private async Task<PromptReply> SafeAsk(CancellationToken ct)
        {
            try { return await (Ask ?? AskTheFarSide)(ct).ConfigureAwait(true); }
            catch (OperationCanceledException) { throw; }
            catch { return new PromptReply(PromptReplyKind.Transient); }
        }

        internal void ApplyReply(PromptReply reply)
        {
            switch (reply.Kind)
            {
                case PromptReplyKind.Prompt when reply.Prompt is not null:
                    SetPrompt(reply.Prompt, reply.Fingerprint);
                    break;

                case PromptReplyKind.Unsupported:
                    _promptsUnsupported = true;
                    ClearPrompt();
                    break;

                case PromptReplyKind.NoPrompt:
                    ClearPrompt();
                    break;

                case PromptReplyKind.NotYet:
                    SaySkip("far-side-not-known-to-answer-prompts", "asked again next tick");
                    break;

                // Transient, or a Prompt reply with no prompt: leave what is on
                // show alone and ask again.
            }
        }

        private void SetPrompt(ChatPrompt prompt, string? fingerprint)
        {
            // The form that was just answered, read again before the far screen
            // has moved on: held back until it changes, or until it has stayed
            // long enough that the answer plainly did not take.
            if (_answeredFingerprint is not null)
            {
                if (fingerprint == _answeredFingerprint && Clock() - _answeredAt < SettleAfterAnswer) return;

                _answeredFingerprint = null;
            }

            if (!CarriesText && prompt.Options.Any(o => o.FreeText))
                prompt = prompt with { Options = prompt.Options.Where(o => !o.FreeText).ToList() };

            // The same form again is not a change, so a 3-second re-read does not
            // restart the panel's arming clock or close a half-typed text box.
            if (_promptFingerprint is not null && _promptFingerprint == fingerprint && Prompt is not null) return;

            // The terminal is on another machine, so the "answer elsewhere" link
            // must not dismiss the panel: its answer is a note saying where.
            _promptFingerprint = fingerprint;
            Prompt = prompt.ElsewhereLabel is null
                ? prompt with { ElsewhereLabel = $"Answer on {FarMachine}" }
                : prompt;
            OnUi(() => PromptChanged?.Invoke());
        }

        private void ClearPrompt()
        {
            if (Prompt is null && _promptFingerprint is null) return;

            Prompt = null;
            _promptFingerprint = null;
            OnUi(() => PromptChanged?.Invoke());
        }

        // The panel is gone: stop asking, and drop the form so reopening asks
        // afresh rather than showing one that may have been answered meanwhile.
        private void StopPromptPolling()
        {
            _pollCts?.Cancel();
            _pollCts = null;
            _polling = false;
            ClearPrompt();
        }

        public Task AnswerAsync(ChatPromptOption option) => AnswerWith(option, null);

        public Task AnswerFreeTextAsync(ChatPromptOption option, string text) => AnswerWith(option, text);

        private async Task AnswerWith(ChatPromptOption option, string? text)
        {
            if (Prompt is null) return;

            // The free-text option is never answered by its key: on the far
            // screen that digit would select it empty, which declines the whole
            // form. It is answered with text, from the box the panel opens.
            if (option.FreeText && text is null)
            {
                Note($"Type your answer for {FarName} in the box, then send it.");
                return;
            }

            // The fingerprint the user saw, sent so the far side can refuse an
            // answer to a form that has since moved on. Cleared first, as the
            // local session does: the buttons stop being live the instant one is
            // clicked rather than staying open for a second answer.
            var fingerprint = _promptFingerprint;
            _answeredFingerprint = fingerprint;
            _answeredAt = Clock();
            _generation++;
            _answering = true;
            ClearPrompt();

            AnswerReply reply;
            try { reply = await (Answer ?? AnswerOnTheFarSide)(option.Key, fingerprint, text).ConfigureAwait(true); }
            catch { reply = new AnswerReply(AnswerKind.Failed); }
            finally
            {
                // Bumped again on the way out: a read that began while the answer
                // was in flight saw the screen before or during the key, and is
                // as stale as one that began before it.
                _generation++;
                _answering = false;
            }

            switch (reply.Kind)
            {
                case AnswerKind.Done:
                    // The next tab appears on the far screen; look now rather
                    // than at the next tick.
                    _wake?.TrySetResult();
                    EnsurePromptPolling();
                    break;

                case AnswerKind.Changed when reply.Prompt is not null:
                    _answeredFingerprint = null;

                    // The screen moved on before this answer landed, and the far
                    // side pressed nothing. Show what is there now.
                    SetPrompt(reply.Prompt, reply.Fingerprint);
                    Note($"{FarTitle}'s question had changed, so nothing was answered. Here is the current one.");
                    break;

                case AnswerKind.AlreadyAnswered:
                    // Nothing is waiting any more, which is what answering does:
                    // the form is gone, and said so quietly rather than as a
                    // failure. A later question, if there is one, arrives from
                    // the next read.
                    _answeredFingerprint = null;
                    Note("That question was already answered.");
                    _wake?.TrySetResult();
                    EnsurePromptPolling();
                    break;

                case AnswerKind.Refused:
                    _answeredFingerprint = null;

                    // Not the screen moving on: the request itself could not be
                    // honoured as asked, and nothing was pressed. Look again so
                    // the buttons come back.
                    Note($"That can't be answered that way on {FarName}. Nothing was pressed.");
                    _wake?.TrySetResult();
                    EnsurePromptPolling();
                    break;

                case AnswerKind.Unsupported:
                    _promptsUnsupported = true;
                    Note($"{FarName} can't take an answer from here. Answer it in its terminal.");
                    break;

                default:
                    _answeredFingerprint = null;
                    Note($"Couldn't answer that on {FarName}.");
                    _wake?.TrySetResult();
                    EnsurePromptPolling();
                    break;
            }
        }

        // --- the transport: the link's client, read through each time ----------

        private async Task<PromptReply> AskTheFarSide(CancellationToken ct)
        {
            if (RemoteControlSessions.MirrorClientFor(_account) is not { } client)
                return new PromptReply(PromptReplyKind.Transient);

            // Not yet known to answer PROMPT and ANSWER: that is said on the roster
            // reply, which may not have landed, and a far Buddy upgraded mid-session
            // is noticed on its next poll. So not "unsupported", which is final.
            if (!client.SpeaksPrompts(_remoteName)) return new PromptReply(PromptReplyKind.NotYet);

            return MapFetch(await client.FetchPromptAsync(_remoteName).ConfigureAwait(true));
        }

        private async Task<AnswerReply> AnswerOnTheFarSide(string key, string? fingerprint, string? text)
        {
            if (RemoteControlSessions.MirrorClientFor(_account) is not { } client || fingerprint is null)
                return new AnswerReply(AnswerKind.Failed);

            // Three different acts, each its own call: the explicit Submit button
            // moves a multi-select on with its ticks kept, typed text goes into
            // the free-text row, and anything else is a digit read off the screen.
            var code = key == AskFormView.CommitKey
                ? await client.CommitPromptAsync(_remoteName, fingerprint).ConfigureAwait(true)
                : text is not null
                    ? await client.AnswerFreeTextAsync(_remoteName, key, fingerprint, text).ConfigureAwait(true)
                    : await client.AnswerPromptAsync(_remoteName, key, fingerprint).ConfigureAwait(true);
            var kind = MapAnswerCode(code);
            if (kind != AnswerKind.Changed) return new AnswerReply(kind);

            // The screen moved on before the key was pressed, so nothing was.
            // Read what is there now, so the user answers that.
            return MapFetch(await client.FetchPromptAsync(_remoteName).ConfigureAwait(true)) is
                { Kind: PromptReplyKind.Prompt, Prompt: { } now } fresh
                    ? new AnswerReply(AnswerKind.Changed, now, fresh.Fingerprint)
                    : new AnswerReply(AnswerKind.Failed);
        }

        internal static PromptReply MapFetch(RemoteMirrorClient.PromptOutcome outcome)
        {
            if (outcome.Prompt is { } wire && outcome.Error is null)
                return new PromptReply(PromptReplyKind.Prompt, AskFormView.FromWire(wire), wire.Fingerprint);

            return outcome.Error switch
            {
                MirrorProtocol.ErrNoPrompt => new PromptReply(PromptReplyKind.NoPrompt),

                // This far Buddy cannot answer, cannot read its pane, or has had
                // replying switched off: asking again will not change that.
                MirrorProtocol.ErrUnsupported or MirrorProtocol.ErrNoPane or MirrorProtocol.ErrReplyOff =>
                    new PromptReply(PromptReplyKind.Unsupported),

                _ => new PromptReply(PromptReplyKind.Transient),
            };
        }

        internal static AnswerKind MapAnswerCode(string? code) => code switch
        {
            null => AnswerKind.Done,

            // The screen no longer shows what the answer was chosen from, or the
            // key is not on it any more: the far side pressed nothing.
            MirrorProtocol.ErrPromptChanged => AnswerKind.Changed,

            // Nothing is waiting any more: the form was answered (by this panel
            // a moment ago, or in the terminal), which is not an error.
            MirrorProtocol.ErrNoPrompt => AnswerKind.AlreadyAnswered,

            // The far side refused the request as asked and pressed nothing.
            MirrorProtocol.ErrBadKey => AnswerKind.Refused,

            MirrorProtocol.ErrUnsupported or MirrorProtocol.ErrNoPane or MirrorProtocol.ErrReplyOff =>
                AnswerKind.Unsupported,

            _ => AnswerKind.Failed,
        };

        // The terminal is on another machine, so there is nothing to bring
        // forward here: say where it is.
        public void AnswerElsewhere() =>
            Note($"This question is in the terminal of {FarName}. Answer it there.");
    }
}
