using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // Which slash channel a *local* Claude Code session sits on, and what that
    // channel lets the chat panel offer (CB-306). Pure and windowless, so the
    // mirror's far side can ask the same question about a session it hosts
    // without constructing a panel session: LocalCliChatSession is a caller of
    // this, not the owner of the answer.
    //
    //  * a pane to type into            → Terminal (today's keystrokes, the full
    //                                     local catalogue)
    //  * a Remote Control bridge        → Bridge (the five control-request
    //                                     commands, nothing typed)
    //  * a peer socket and nothing else → PeerSocket (nothing runs)
    //  * Desktop-hosted with no bridge  → PeerSocket too: no pane, no socket,
    //                                     so no route exists at all
    public static class LocalSlashChannel
    {
        // The bridge id for a session: Claude Desktop's per-chat record wins
        // (it is where `/rc` writes it), and a headless job's own registry row
        // (`bridgeSessionId`) is the fallback. One answer for both, so no caller
        // has to know which kind of session it holds. Null when neither names a
        // well-formed one.
        internal static string? BridgeIdFor(string? desktopRecordBridge, string? registryBridge) =>
            ClaudeCloudRoster.IsWellFormedId(desktopRecordBridge) ? desktopRecordBridge
            : ClaudeCloudRoster.IsWellFormedId(registryBridge) ? registryBridge
            : null;

        // `canType` is TerminalFocuser.CanSendQuietly's answer; a Desktop-hosted
        // session never types, whatever that says about its stand-in terminal.
        // `canMessage` is TerminalFocuser.CanDeliver's: a session with neither a
        // pane nor a socket keeps the Terminal answer it always had, since there
        // is no better channel to name and the send fails on its own terms.
        public static SlashChannel ChannelFor(bool desktopHosted, bool canType, bool canMessage, string? bridgeId)
        {
            if (!desktopHosted && (canType || (!canMessage && string.IsNullOrEmpty(bridgeId))))
                return SlashChannel.Terminal;
            if (!string.IsNullOrEmpty(bridgeId)) return SlashChannel.Bridge;
            return SlashChannel.PeerSocket;
        }

        // The FULL catalogue a session knows, each command carrying the route it
        // would take on this channel (CB-306): Typed on a pane, Control for the
        // five that run over a bridge, Unavailable for everything else the CLI
        // knows but this channel cannot run. The panel hides Unavailable ones
        // from autocomplete; the same list is the known-name set (a typed line
        // is a command only if its first token is in it) and what the mirror's
        // roster carries. `catalogue` is the local scan.
        public static IReadOnlyList<SlashCommand> CommandsFor(
            SlashChannel channel, Func<IReadOnlyList<SlashCommand>> catalogue)
        {
            var local = catalogue();
            if (channel == SlashChannel.Terminal) return local;

            // Off a pane every command the CLI knows is listed with the route it
            // would take, and the five control commands are always listed — the
            // catalogue may omit one (/think is our name for
            // set_max_thinking_tokens, not a built-in), and an unlisted name
            // would be delivered to the model as prose instead of refused.
            var routed = local
                .Select(c => c with { Route = RouteOf(channel, c.Name) })
                .ToList();
            routed.AddRange(ControlCommands
                .Where(c => !routed.Any(r => string.Equals(r.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => c with { Route = RouteOf(channel, c.Name) }));
            return routed;
        }

        private static SlashRouteKind RouteOf(SlashChannel channel, string name) =>
            channel == SlashChannel.Bridge && HasControl(name)
                ? SlashRouteKind.Control
                : SlashRouteKind.Unavailable;

        internal static bool HasControl(string name) =>
            ControlCommands.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        internal static readonly IReadOnlyList<SlashCommand> ControlCommands = new[]
        {
            new SlashCommand("/color", "Set the prompt bar colour", SlashRouteKind.Control),
            new SlashCommand("/rename", "Rename this session", SlashRouteKind.Control),
            new SlashCommand("/model", "Switch the model", SlashRouteKind.Control),
            new SlashCommand("/permissions", "Switch the permission mode", SlashRouteKind.Control),
            new SlashCommand("/think", "Set the thinking budget", SlashRouteKind.Control),
        };
    }
}
