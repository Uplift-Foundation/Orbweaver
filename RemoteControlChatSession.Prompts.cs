using System;
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
        }

        internal sealed record PromptReply(PromptReplyKind Kind, ChatPrompt? Prompt = null, string? Fingerprint = null);

        // What an answer came back with. Changed carries the form as it is now,
        // for when the screen moved on between asking and answering: the key was
        // not pressed, and the user is shown the new question instead.
        internal enum AnswerKind { Done, Changed, Unsupported, Failed }

        internal sealed record AnswerReply(AnswerKind Kind, ChatPrompt? Prompt = null, string? Fingerprint = null);

        // How often an unchanged waiting form is re-read.
        internal static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

        // The seams. Null means "not wired", which reads as unsupported, so a
        // session built before the transport exists simply never asks.
        internal Func<CancellationToken, Task<PromptReply>>? Ask { get; set; }
        internal Func<string, string?, string?, Task<AnswerReply>>? Answer { get; set; }
        internal Func<bool>? WaitingNow { get; set; }
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

        public ChatPrompt? Prompt { get; private set; }

        public event Action? PromptChanged;

        private string? _promptFingerprint;
        private bool _promptsUnsupported;
        private bool _polling;
        private CancellationTokenSource? _pollCts;
        private TaskCompletionSource? _wake;

        // For a test: agree a live view without a client behind it, so the rules
        // above can be driven with the seams alone.
        internal void AssumeMirroringForTests() => _mirroring = true;

        private bool Waiting() => (WaitingNow ?? RosterSaysWaiting)();

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
            if (_disposed || !_panelOpen || !_mirroring || _promptsUnsupported) return;

            if (!Waiting())
            {
                // Left waiting: whatever was on show is not being asked any more.
                ClearPrompt();
                return;
            }

            if (_polling) return;

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

                    var reply = Ask is null
                        ? new PromptReply(PromptReplyKind.Unsupported)
                        : await SafeAsk(ct).ConfigureAwait(true);

                    if (ct.IsCancellationRequested) return;
                    ApplyReply(reply);
                    if (_promptsUnsupported) return;

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
            try { return await Ask!(ct).ConfigureAwait(true); }
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

                // Transient, or a Prompt reply with no prompt: leave what is on
                // show alone and ask again.
            }
        }

        private void SetPrompt(ChatPrompt prompt, string? fingerprint)
        {
            // The same form again is not a change, so a 3-second re-read does not
            // restart the panel's arming clock or close a half-typed text box.
            if (_promptFingerprint is not null && _promptFingerprint == fingerprint && Prompt is not null) return;

            // The terminal is on another machine, so the "answer elsewhere" link
            // must not dismiss the panel: its answer is a note saying where.
            _promptFingerprint = fingerprint;
            Prompt = prompt.ElsewhereLabel is null
                ? prompt with { ElsewhereLabel = $"Answer on {_remoteName}" }
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
            if (Prompt is null || Answer is null) return;

            // The fingerprint the user saw, sent so the far side can refuse an
            // answer to a form that has since moved on. Cleared first, as the
            // local session does: the buttons stop being live the instant one is
            // clicked rather than staying open for a second answer.
            var fingerprint = _promptFingerprint;
            ClearPrompt();

            AnswerReply reply;
            try { reply = await Answer(option.Key, fingerprint, text).ConfigureAwait(true); }
            catch { reply = new AnswerReply(AnswerKind.Failed); }

            switch (reply.Kind)
            {
                case AnswerKind.Done:
                    // The next tab appears on the far screen; look now rather
                    // than at the next tick.
                    _wake?.TrySetResult();
                    EnsurePromptPolling();
                    break;

                case AnswerKind.Changed when reply.Prompt is not null:
                    // The screen moved on before this answer landed, and the far
                    // side pressed nothing. Show what is there now.
                    SetPrompt(reply.Prompt, reply.Fingerprint);
                    Note($"{_remoteName}'s question had changed, so nothing was answered. Here is the current one.");
                    break;

                case AnswerKind.Unsupported:
                    _promptsUnsupported = true;
                    Note($"{_remoteName} can't take an answer from here. Answer it in its terminal.");
                    break;

                default:
                    Note($"Couldn't answer that on {_remoteName}.");
                    _wake?.TrySetResult();
                    EnsurePromptPolling();
                    break;
            }
        }

        // The terminal is on another machine, so there is nothing to bring
        // forward here: say where it is.
        public void AnswerElsewhere() =>
            Note($"This question is on {_remoteName}'s terminal. Answer it there.");
    }
}
