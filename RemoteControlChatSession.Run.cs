namespace Orbweaver
{
    // CB-404: the model and effort of a session on another machine, from a panel
    // on this one.
    //
    // The far Buddy answers from its own RunControlsHost and puts the answer in
    // the roster's `run` field (MirrorRun); a change goes back as an INPUT
    // carrying `run=model|effort`, resolved and checked over there. This panel
    // never guesses: what it draws is the far side's last roster, plus, between
    // a set going out and the next roster landing, the value it just sent marked
    // as pending, so the chip does not snap back to the old value for the ten
    // seconds a roster takes.
    //
    // A far Buddy that predates this sends no `run`, and the controls are then
    // drawn disabled with "the Orbweaver on <machine> is too old to change this"
    // — never hidden, never null: a chip that vanishes reads as a feature that
    // is not there, and a disabled one says why.
    internal sealed partial class RemoteControlChatSession : IRemoteChatRunOptions
    {
        private (RunField Field, string Value)? _sentRun;
        private string _runSeen = "";
        private RemoteMirrorClient? _runClient;
        private Action? _runOptionsChanged;

        internal const string RosterNotYet = "not known until the other machine answers";

        private MirrorProtocol.MirrorRosterEntry? RunEntry() =>
            Link() is { } client && client.StateFor(_remoteName) is { Entry: { } entry } ? entry : null;

        public RemoteRunOptions? RunOptions
        {
            get
            {
                if (RunEntry() is not { } entry)
                    return MirrorRun.Options(null, FarMachine, offReason: RosterNotYet);
                var options = MirrorRun.Options(entry.Run, FarMachine, entry.Cli);
                if (entry.Run is null || _sentRun is not { } sent) return options;

                // The value just sent, until the far roster says something new.
                return sent.Field == RunField.Model
                    ? options with { ModelId = sent.Value, ModelPending = true, ChipText = Chip(options, sent.Value, null) }
                    : options with { ThinkingId = sent.Value, EffortPending = true, ChipText = Chip(options, null, sent.Value) };
            }
        }

        private static string Chip(RemoteRunOptions o, string? model, string? effort)
        {
            var m = o.Models.FirstOrDefault(c => c.Id == (model ?? o.ModelId))?.Label ?? model ?? RunText.ModelUnknown;
            if (o.EffortDisabledReason == RunText.NoEffortSetting) return m;
            var e = o.ThinkingChoices.FirstOrDefault(c => c.Id == (effort ?? o.ThinkingId))?.Label ?? effort ?? RunText.EffortUnknown;
            return $"{m} · {e}";
        }

        // Subscribed on first listener, so the roster that changes the answer is
        // heard whichever client carries it (the link's, or a test's seam).
        public event Action? RunOptionsChanged
        {
            add
            {
                _runOptionsChanged += value;
                if (_runClient is null && Link() is { } client)
                {
                    _runClient = client;
                    _runSeen = RunKey();
                    client.RosterUpdated += OnRunRoster;
                }
            }
            remove => _runOptionsChanged -= value;
        }

        // The roster's run, as bytes, so a roster that changed something else
        // (a status, a pane) does not redraw the chip.
        private string RunKey() => RunEntry()?.Run is { } run
            ? System.Text.Json.JsonSerializer.Serialize(run)
            : "";

        private void OnRunRoster() => OnUi(() =>
        {
            if (_disposed) return;
            var key = RunKey();
            if (key == _runSeen) return;
            _runSeen = key;
            _sentRun = null;
            _runOptionsChanged?.Invoke();
        });

        public Task<string?> SetModelAsync(string? id) => SetRunAsync(RunField.Model, id);

        public Task<string?> SetThinkingAsync(string? id) => SetRunAsync(RunField.Effort, id);

        // OpenClaw's; HasFast and HasTimeLimit keep the flyout from offering them.
        public Task<string?> SetFastModeAsync(bool? on) => Task.FromResult<string?>(RunText.NoRoute);

        public int? TimeoutMinutes { get => null; set { } }

        private async Task<string?> SetRunAsync(RunField field, string? value)
        {
            if (value is null) return field == RunField.Model ? "name a model" : "name an effort level";
            if (!OrbweaverSettings.PeerLinkEnabled) return RemoteControlOffNote;
            if (Link() is not { } client || RunEntry() is not { } entry) return RosterNotYet;
            if (entry.Run is null) return MirrorRun.TooOldReason(FarMachine);

            var result = await client.SetRunAsync(_remoteName, field, value).ConfigureAwait(true);
            if (result.Kind != RunSetKind.Sent) return result.Why ?? "it can't be changed there";

            _sentRun = (field, value);
            _runOptionsChanged?.Invoke();
            return null;
        }

        private void DisposeRun()
        {
            if (_runClient is not null) _runClient.RosterUpdated -= OnRunRoster;
            _runClient = null;
        }
    }
}
