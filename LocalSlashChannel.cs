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

        // What the autocomplete offers on a channel. `terminalCatalogue` is the
        // local scan, consulted only for Terminal — it is a disk read the other
        // channels never need.
        public static IReadOnlyList<SlashCommand> CommandsFor(
            SlashChannel channel, Func<IReadOnlyList<SlashCommand>> terminalCatalogue) =>
            channel switch
            {
                SlashChannel.Terminal => terminalCatalogue(),
                SlashChannel.Bridge => ControlCommands,
                _ => Array.Empty<SlashCommand>(),
            };

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
