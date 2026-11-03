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

            // Posts one control request over the session's bridge. Null on
            // success, otherwise the sentence saying why not.
            Func<SessionStatus, string, JsonObject, Task<string?>> PostControl);

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

            var route = SlashRouting.Resolve(ChannelOf(status), cmd.Name, cmd.Args);

            switch (route.Kind)
            {
                case SlashRouteKind.Typed:
                    return await _seams.TypeInto(status, line).ConfigureAwait(false)
                        ? new RemoteMirrorServer.SlashOutcome(MirrorProtocol.ViaTyped, null)
                        : Refused("the terminal refused the text");

                case SlashRouteKind.Control:
                    var request = SlashRouting.ControlRequest(route.ControlSubtype!, cmd.Args, out var problem);
                    if (request is null) return Refused(problem!);

                    var failure = await _seams.PostControl(status, route.ControlSubtype!, request).ConfigureAwait(false);
                    return failure is null
                        ? new RemoteMirrorServer.SlashOutcome(MirrorProtocol.ViaControl, null)
                        : Refused(failure);

                default:
                    // Unavailable, the only kind left for the channels this resolves, always
                    // carries a reason; the server words a missing one itself.
                    return Refused(route.Reason);
            }
        }

        private static RemoteMirrorServer.SlashOutcome Refused(string? reason) =>
            new(MirrorProtocol.ViaRefused, reason);
    }
}
