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

    public sealed record PeerRunField(
        [property: JsonPropertyName("id")] string? Id = null,
        [property: JsonPropertyName("label")] string? Label = null,
        [property: JsonPropertyName("choices")] IReadOnlyList<PeerRunChoice>? Choices = null,
        [property: JsonPropertyName("off")] string? Off = null,
        [property: JsonPropertyName("pending")] bool? Pending = null);

    public sealed record PeerRun(
        [property: JsonPropertyName("model")] PeerRunField? Model = null,
        [property: JsonPropertyName("effort")] PeerRunField? Effort = null);

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

        internal static PeerRun From(RunState s) => new(FieldOf(s.Model), FieldOf(s.Effort));

        private static PeerRunField FieldOf(RunFieldState f) => new(
            f.Id, f.Label,
            RunFrames.Capped(f.Choices, f.Id).Select(c => new PeerRunChoice(c.Id, c.Label)).ToList(),
            f.Off, f.Pending ? true : null);

        internal static RunState ToState(PeerRun run) => new(StateOf(run.Model), StateOf(run.Effort));

        private static RunFieldState StateOf(PeerRunField? f) => new(
            f?.Id, f?.Label,
            (f?.Choices ?? []).Select(c => new RemoteRunChoice(c.Id, c.Label)).ToList(),
            f?.Off, f?.Pending == true);

        // What the near panel draws for a far session: the far side's own
        // answer, or — from a far Buddy that predates this — both controls
        // shown and disabled with the reason, never hidden.
        internal static RemoteRunOptions Options(PeerRun? run, string machine)
        {
            if (run is null)
            {
                var old = TooOldReason(machine);
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
            var model = s.Model.Label ?? RunText.ModelUnknown;
            var chip = s.Effort.Off == RunText.NoEffortSetting ? model : $"{model} · {s.Effort.Label ?? RunText.EffortUnknown}";
            return new RemoteRunOptions(s.Model.Id, s.Model.Choices, s.Effort.Id, null, s.Effort.Choices,
                false, false, false, false, chip)
            {
                ModelDisabledReason = s.Model.Off,
                EffortDisabledReason = s.Effort.Off,
                ModelPending = s.Model.Pending,
                EffortPending = s.Effort.Pending,
                Source = RunReadingSource.None,
                HasFast = false,
                HasTimeLimit = false,
            };
        }

        // The reply fields for a set's result, on the far side.
        internal static Dictionary<string, string> ReplyFields(RunSetResult result)
        {
            if (result.Kind == RunSetKind.Sent) return new() { ["via"] = ViaSet };
            return new()
            {
                ["via"] = MirrorProtocol.ViaRefused,
                ["why"] = MirrorProtocol.Encode(result.Why ?? "it can't be changed there"),
                [KindField] = KindWord(result.Kind),
            };
        }

        internal static string KindWord(RunSetKind kind) => kind switch
        {
            RunSetKind.BadValue => RunFrames.BadValue,
            RunSetKind.Unavailable => RunFrames.Unavailable,
            _ => RunFrames.Failed,
        };

        // A set's result as the near side reads the reply. An ERR (no session,
        // replies off, a bad hash) or a timeout is failed with the code's
        // own words; a refusal keeps its kind; anything else that was answered
        // OK went.
        internal static RunSetResult ResultOf(RemoteMirrorClient.InputOutcome outcome)
        {
            if (outcome.Error is { } error) return new(RunSetKind.Failed, outcome.Why ?? error);
            if (outcome.Via != MirrorProtocol.ViaRefused) return RunSetResult.Sent;
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
