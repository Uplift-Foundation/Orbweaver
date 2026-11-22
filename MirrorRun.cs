using System.Text.Json.Serialization;

namespace Orbweaver
{
    // CB-404 (plan 4.6): the model and effort of a session on a linked desktop.
    //
    // The far Buddy puts its own RunControlsHost's answer in the roster as an
    // optional trailing `run` field on the entry; its presence is the
    // capability, the way `routes` is for slash commands. A near Buddy changes
    // it with an INPUT that carries `run=model|effort` and the value as the
    // payload. Nothing here bumps PeerProtocol.Version: an older far Buddy
    // sends no `run`, so the near panel shows the controls disabled with
    // TooOldReason, and an older near Buddy ignores the unknown roster member.
    //
    // Pending is written only when true and every absent part is omitted
    // (RosterJson is WhenWritingNull), so the bytes move only when what a
    // reader would draw moves — the roster hash (CB-216) is recomputed on
    // every change, and a field that wobbled would resend the roster for
    // nothing.
    public sealed record PeerRunChoice(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label);

    // The same fields as frames-v13's run frame: id confirmed, pending the
    // value being applied, off and failed the desk's sentences.
    public sealed record PeerRunField(
        [property: JsonPropertyName("id")] string? Id = null,
        [property: JsonPropertyName("pending")] string? Pending = null,
        [property: JsonPropertyName("choices")] IReadOnlyList<PeerRunChoice>? Choices = null,
        [property: JsonPropertyName("off")] string? Off = null,
        [property: JsonPropertyName("failed")] string? Failed = null);

    public sealed record PeerRun(
        [property: JsonPropertyName("model")] PeerRunField? Model = null,
        [property: JsonPropertyName("effort")] PeerRunField? Effort = null,
        [property: JsonPropertyName("savesDefault")] bool? SavesDefault = null);

    internal static class MirrorRun
    {
        // The INPUT field that marks the payload as a run change, and its two values.
        internal const string Field = "run";

        // An INPUT reply's `via` when the change went: pending until a reading
        // on the far side confirms it, exactly as it is there.
        internal const string ViaSet = "set";

        // On a refusal (via=refused) the reply also says which kind, in
        // frames-v13's words, so the near side can tell "not a choice" from "the
        // CLI said no" without parsing the sentence.
        internal const string KindField = "kind";

        internal static string TooOldReason(string machine) =>
            $"the Orbweaver on {machine} is too old to change this";

        // Sanitised exactly as a phone's frame is, so the two wires never
        // disagree about what is offered.
        internal static PeerRun From(RunState raw)
        {
            var s = RunFrames.Sanitize(raw);
            return new(FieldOf(s.Model), FieldOf(s.Effort), s.SavesDefault ? true : null);
        }

        private static PeerRunField FieldOf(RunFieldState f) => new(
            f.Id, f.Pending, f.Choices.Select(c => new PeerRunChoice(c.Id, c.Label)).ToList(), f.Off, f.Failed);

        internal static RunState ToState(PeerRun run) => new(StateOf(run.Model), StateOf(run.Effort), run.SavesDefault == true);

        private static RunFieldState StateOf(PeerRunField? f) => new(
            f?.Id, f?.Pending,
            (f?.Choices ?? []).Select(c => new RemoteRunChoice(c.Id, c.Label)).ToList(),
            f?.Off, f?.Failed);

        // What the near panel draws for a far session: the far side's own
        // answer, or, from a far Buddy that predates this, both controls
        // shown and disabled with the reason, never hidden. cli is the roster's
        // word for the far CLI, which picks the saves-a-default sentence.
        internal static RemoteRunOptions Options(PeerRun? run, string machine, string? cli = null, string? offReason = null)
        {
            if (run is null)
            {
                var old = offReason ?? TooOldReason(machine);
                return new RemoteRunOptions(null, [], null, null, [], false, false, false, false, RunText.ModelUnknown)
                {
                    ModelDisabledReason = old,
                    EffortDisabledReason = old,
                    Source = RunReadingSource.None,
                    HasFast = false,
                    HasTimeLimit = false,
                };
            }

            var s = ToState(run);
            var modelId = s.Model.Pending ?? s.Model.Id;
            var effortId = s.Effort.Pending ?? s.Effort.Id;
            var model = s.Model.Choices.FirstOrDefault(c => c.Id == modelId)?.Label ?? RunText.ModelUnknown;
            var effort = s.Effort.Choices.FirstOrDefault(c => c.Id == effortId)?.Label ?? RunText.EffortUnknown;
            var chip = s.Effort.Off == RunText.NoEffortSetting ? model : $"{model} · {effort}";
            return new RemoteRunOptions(modelId, s.Model.Choices, effortId, null, s.Effort.Choices,
                false, false, false, false, chip)
            {
                ModelDisabledReason = s.Model.Off,
                EffortDisabledReason = s.Effort.Off,
                ModelPending = s.Model.Pending is not null,
                EffortPending = s.Effort.Pending is not null,
                ModelFailure = s.Model.Failed,
                EffortFailure = s.Effort.Failed,
                ConfirmedModelId = s.Model.Id,
                ConfirmedEffortId = s.Effort.Id,
                Source = RunReadingSource.None,
                DefaultWarning = !s.SavesDefault ? null
                    : cli == MirrorProtocol.CliFor(SessionSource.Codex) ? RunText.CodexPickerDefault : RunText.ClaudeTypedDefault,
                HasFast = false,
                HasTimeLimit = false,
            };
        }

        // The reply fields for a refusal or a route's answer, on the far side.
        // A set that went carries its note, when it has one (it went by typing
        // after the bridge failed, CB-404), in the same `why` field: via=set says
        // it went, so an older near Buddy reading only `via` still reads success.
        internal static Dictionary<string, string> ReplyFields(string? code, string? why, string? note = null)
        {
            if (code is null)
                return note is null
                    ? new() { ["via"] = ViaSet }
                    : new() { ["via"] = ViaSet, ["why"] = MirrorProtocol.Encode(note) };
            var fields = new Dictionary<string, string> { ["via"] = MirrorProtocol.ViaRefused, [KindField] = code };
            fields["why"] = MirrorProtocol.Encode(why ?? (code == RunFrames.BadValue
                ? "that isn't one of this session's choices" : "it can't be changed there"));
            return fields;
        }

        // A set's result as the near side reads the reply. An ERR (no session,
        // replies off, a bad hash) or a timeout is failed with the code's own
        // words; a refusal keeps its kind; anything else answered OK went.
        internal static RunSetResult ResultOf(RemoteMirrorClient.InputOutcome outcome)
        {
            if (outcome.Error is { } error) return new(RunSetKind.Failed, outcome.Why ?? error);
            if (outcome.Via != MirrorProtocol.ViaRefused)
                return outcome.Why is { } note ? new(RunSetKind.Sent, note) : RunSetResult.Sent;
            var k = outcome.Kind switch
            {
                RunFrames.BadValue => RunSetKind.BadValue,
                RunFrames.Unavailable => RunSetKind.Unavailable,
                _ => RunSetKind.Failed,
            };
            return new(k, outcome.Why);
        }
    }
}
