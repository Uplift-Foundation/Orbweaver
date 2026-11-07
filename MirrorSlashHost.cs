using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Orbweaver
{
    // The far Buddy's answer to "what can this session run, and run this" (CB-306).
    //
    // The mirror link asks these questions about sessions this machine hosts, and
    // the answer is the one a local panel on this same machine would have got:
    // the channel comes from LocalSlashChannel and every decision from
    // SlashRouting, so a command offered to a far panel runs by the same route a
    // near one would. Everything that touches the machine — the pane, the
    // registry, the cloud client — comes in through Seams, so the whole thing is
    // driven in tests with the real resolver and fakes at the edges.
    internal sealed class MirrorSlashHost
    {
        internal sealed record Seams(
            Func<SessionStatus, bool> CanType,
            Func<SessionStatus, bool> CanMessage,
            Func<SessionStatus, string?> BridgeIdFor,
            Func<SessionStatus, IReadOnlyList<SlashCommand>> TerminalCatalogue,
            Func<SessionStatus, string, Task<bool>> TypeInto,

            // Posts one control request over the session's bridge and says
            // what came of it: refused (with the sentence), done, or delivered
            // with no answer yet.
            Func<SessionStatus, string, JsonObject, Task<ControlVerdict>> PostControl,

            // CB-307 addendum: the typing gate's reads, as on the server's own
            // seams. Null on both means ungated, which every harness built
            // before this relies on.
            Func<SessionStatus, string?>? RecordStatus = null,
            Func<SessionStatus, Task<string?>>? CapturePane = null,
            Func<SessionStatus, bool>? CanReadPane = null,

            // CB-307: shadow mode and where its verdicts are logged, as on the
            // server's own seams. Null shadow is off.
            Func<bool>? Shadow = null,
            Action<string, string>? Log = null);

        // CB-306's product-readiness round: the far side's answer to a control
        // request, carried back over the link so the near panel says what the
        // far CLI said rather than assuming success.
        internal sealed record ControlVerdict(string? Refusal, bool Confirmed)
        {
            internal static readonly ControlVerdict Done = new(null, true);
            internal static readonly ControlVerdict Unconfirmed = new(null, false);
            internal static ControlVerdict Refused(string reason) => new(reason, false);
        }

        private readonly Seams _seams;

        public MirrorSlashHost(Seams seams) => _seams = seams;

        internal SlashChannel ChannelOf(SessionStatus status)
        {
            var canType = _seams.CanType(status);
            if (!status.IsDesktopHosted && canType) return SlashChannel.Terminal;
            return LocalSlashChannel.ChannelFor(
                status.IsDesktopHosted, canType,
                !status.IsDesktopHosted && _seams.CanMessage(status),
                _seams.BridgeIdFor(status));
        }

        // The whole catalogue the session's CLI knows, each with the route it
        // would take here — Unavailable ones included, because the near side
        // decides what is a command by name, and a name left out would be sent
        // to the far model as prose. LocalSlashChannel owns the list, so a
        // change to it (a control command added, a channel's rule) reaches the
        // roster without this file changing.
        public IReadOnlyList<SlashCommand> CommandsFor(SessionStatus status) =>
            LocalSlashChannel.CommandsFor(ChannelOf(status), () => _seams.TerminalCatalogue(status));

        public async Task<RemoteMirrorServer.SlashOutcome> RunAsync(SessionStatus status, string line)
        {
            if (SlashRouting.Split(line) is not { } cmd)
                return Refused("that is not a command");

            var channel = ChannelOf(status);
            var route = SlashRouting.Resolve(channel, cmd.Name, cmd.Args);

            switch (route.Kind)
            {
                case SlashRouteKind.Typed:
                    var (typed, notAtPrompt, _) = await TypingGate.GuardedTypeAsync(status, line, _seams.TypeInto,
                        _seams.RecordStatus, _seams.CapturePane, _seams.CanReadPane,
                        _seams.Shadow, _seams.Log ?? ((what, detail) => MirrorLog.Say(what, detail)), "slash-host")
                        .ConfigureAwait(false);
                    if (notAtPrompt is not null) return Refused(TypingGate.NotAtPromptReason(notAtPrompt));

                    return typed
                        ? new RemoteMirrorServer.SlashOutcome(MirrorProtocol.ViaTyped, null)
                        : Refused("the terminal refused the text");

                case SlashRouteKind.Control:
                    var request = SlashRouting.ControlRequest(route.ControlSubtype!, cmd.Args, out var problem);
                    if (request is null) return Refused(problem!);

                    var verdict = await _seams.PostControl(status, route.ControlSubtype!, request).ConfigureAwait(false);
                    return verdict.Refusal is { } refusal
                        ? Refused(refusal)
                        : new RemoteMirrorServer.SlashOutcome(MirrorProtocol.ViaControl, null,
                            verdict.Confirmed ? MirrorProtocol.VerdictDone : MirrorProtocol.VerdictUnconfirmed);

                default:
                    // Unavailable, the only kind left for the channels this resolves, always
                    // carries a reason; the server words a missing one itself. A
                    // Desktop chat with no bridge lands on the peer-socket channel
                    // too, and is told about itself rather than about a job.
                    return Refused(status.IsDesktopHosted && channel == SlashChannel.PeerSocket
                        ? SlashRouting.DesktopNoBridgeReason
                        : route.Reason);
            }
        }

        // The PostControl seam the live server wires, with its gates in front of
        // the post: the bridge-commands setting on *this* machine (CB-306's
        // product-readiness round — the far side posts with its own login, so
        // its own toggle is the one that decides), then a bridge to post to.
        // Lifted out of RemoteMirrorServer.LiveSeams so the round-trip harness
        // drives the real gate with only the post itself faked.
        //
        // A rejection from the CLI itself comes back worded with the session's
        // Claude Code version beside the one the requests were measured on;
        // versionFor reads it off this machine's registry row.
        internal static Func<SessionStatus, string, JsonObject, Task<ControlVerdict>> GatedPost(
            Func<bool> commandsEnabled,
            Func<SessionStatus, string?> bridgeIdFor,
            Func<SessionStatus, string?> versionFor,
            Func<SessionStatus, string, JsonObject, Task<CloudTurnSender.ControlAttempt>> post) =>
            async (status, _, request) =>
            {
                if (!commandsEnabled()) return ControlVerdict.Refused(LocalCliChatSession.BridgeCommandsOffHint);
                if (bridgeIdFor(status) is not { } id) return ControlVerdict.Refused(NoBridgeReason);

                var attempt = await post(status, id, request).ConfigureAwait(false);
                if (attempt.Failure is { } failure) return ControlVerdict.Refused(failure);
                if (attempt.CliError is { } refused)
                    return ControlVerdict.Refused(CloudTurnSender.RejectionClause(refused, versionFor(status)));
                return attempt.Confirmed ? ControlVerdict.Done : ControlVerdict.Unconfirmed;
            };

        internal const string NoBridgeReason = "this session has no Remote Control bridge";

        private static RemoteMirrorServer.SlashOutcome Refused(string? reason) =>
            new(MirrorProtocol.ViaRefused, reason);
    }
}
