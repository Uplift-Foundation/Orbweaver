using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Orbweaver
{
    // Where a slash command typed into a chat panel can actually run, decided
    // once, purely, per channel (CB-306).
    //
    // The decision is a table, and the table is what the gate probes measured on
    // 2026-11-03 against Claude Code 2.1.317 and openclaw 2026.10.10 — see the
    // CB-306 sections of docs/claude-cloud-findings.md,
    // docs/remote-control-findings.md and docs/openclaw-findings.md. The short
    // version, because it is the opposite of what the plan first assumed:
    //
    //  * A `/cmd` posted into a Remote Control bridge or a cloud session as a
    //    user event is a *peer message* to the far CLI — it never reaches the
    //    slash dispatcher, and the model politely declines to run built-ins for
    //    a peer. So there is no PassThrough on those channels, for any command,
    //    custom ones included.
    //  * What does run there is the control-request channel: set_color,
    //    rename_session, set_model, set_permission_mode and
    //    set_max_thinking_tokens all answered success on the bridge; the cloud
    //    worker answered success to all but set_color ("Unsupported control
    //    request subtype").
    //  * OpenClaw's gateway parses a leading slash in chat.send itself and
    //    publishes its catalogue through commands.list, so there everything the
    //    catalogue offers is PassThrough.
    //  * A terminal pane is typed into, as before. A peer socket with no
    //    bridge runs nothing.
    //
    // Pure and windowless on purpose: every arm below has a unit case, and a
    // session only asks the question and acts on the answer.
    public enum SlashRouteKind
    {
        // Keystrokes into a tmux pane (the live view). Today's behaviour.
        Typed,

        // The far side parses the text itself: send `/cmd args` as the message.
        PassThrough,

        // A control_request on the session's events channel, with ControlSubtype.
        Control,

        // A gateway method call (reserved for an OpenClaw command the gateway
        // does not take as text; none is routed this way today).
        Native,

        // Cannot run here. Hidden from autocomplete; typed in full, the send is
        // refused with Reason rather than reaching the model as prose.
        Unavailable,
    }

    // The channel a session's slash command would travel over.
    public enum SlashChannel
    {
        // A local CLI with a pane to type into.
        Terminal,

        // A Remote Control bridge: a Claude Desktop chat, or a headless job
        // whose registry row names a bridge session.
        Bridge,

        // A claude.ai cloud session.
        Cloud,

        // A headless job reached only over its peer socket.
        PeerSocket,

        // An OpenClaw gateway session.
        OpenClaw,
    }

    public sealed record SlashRoute(SlashRouteKind Kind, string? ControlSubtype = null, string? Reason = null)
    {
        public static readonly SlashRoute Typed = new(SlashRouteKind.Typed);
        public static readonly SlashRoute PassThrough = new(SlashRouteKind.PassThrough);

        public static SlashRoute Control(string subtype) => new(SlashRouteKind.Control, subtype);
        public static SlashRoute Unavailable(string reason) => new(SlashRouteKind.Unavailable, null, reason);
    }

    public static class SlashRouting
    {
        // The Claude Code version the control-request table below was measured
        // on (CB-306, 2026-11-03; see the header). Quoted back, beside the
        // session's own version, when the CLI rejects one of these requests,
        // so a rename or a dropped subtype in a later CLI reads as that rather
        // than as Orbweaver being broken. Move it only when the table is
        // re-measured.
        internal const string MeasuredOnClaudeCode = "2.1.317";

        // The reasons, as the sentence the panel's note will carry. One per
        // cause rather than one generic refusal, because the fix differs.
        internal const string PeerMessageReason =
            "a message from here arrives in that session as a teammate's request, and Claude Code runs slash commands only from its own keyboard or claude.ai";

        internal const string CloudNoColorReason =
            "a cloud session has no prompt bar to colour";

        // A job reached only over its peer socket is one whose registry row
        // names no Remote Control bridge (CB-306's product-readiness round).
        // Said as that, with the two ways out, rather than as a fact about
        // sockets the person cannot act on.
        internal const string PeerSocketReason =
            "this job has no Remote Control connection, so slash commands can't reach it. "
            + "Start it with Remote Control on (claude --remote-control), or from a terminal pane, to run them";

        // Measured 2026-11-03: the command's control-request subtype, where one
        // exists. `/permissions` here means the mode form only.
        private static readonly IReadOnlyDictionary<string, string> ControlSubtypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["/color"] = "set_color",
                ["/rename"] = "rename_session",
                ["/model"] = "set_model",
                ["/permissions"] = "set_permission_mode",
                ["/think"] = "set_max_thinking_tokens",
            };

        internal static bool HasControl(string name) => ControlSubtypes.ContainsKey(name);

        // The session permission modes, case-sensitive (camelCase), read from the
        // CLI 2.1.317 binary. "ask" is a per-tool behaviour, not a session mode,
        // and is deliberately not here.
        internal static readonly IReadOnlyList<string> PermissionModes =
            new[] { "default", "acceptEdits", "plan", "bypassPermissions" };

        // Whether a typed line names a command this session actually knows — the
        // gate between "run/refuse this command" and "send this as an ordinary
        // message" (CB-306, the lead's ruling on Nadia's finding). A path
        // (`/tmp/build.log`), a bare "/", or an unknown `/word` is not a command
        // and must be sent unchanged, exactly as before this feature; only a
        // first token that matches a known name — including the Unavailable ones,
        // so a known-but-unrunnable command is refused rather than sent as prose
        // — is routed. The caller passes the session's *full* catalogue.
        public static bool NamesKnownCommand(string line, IEnumerable<string> knownNames)
        {
            if (Split(line) is not { } split) return false;
            foreach (var name in knownNames)
                if (string.Equals(name, split.Name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The alias-aware form: a command is known if the first token matches its
        // Name or any of its Aliases (CB-306, after wren's alias support). This is
        // the one the panel uses, so "/t" is recognised as "/think" and routed
        // rather than sent as a message. Callers that hold only names use the
        // overload above.
        public static bool NamesKnownCommand(string line, IEnumerable<SlashCommand> commands)
        {
            if (Split(line) is not { } split) return false;
            foreach (var command in commands)
                if (command.IsTyped(split.Name)) return true;
            return false;
        }

        // `name` carries its leading slash, the way SlashCommand.Name does.
        public static SlashRoute Resolve(SlashChannel channel, string name, string args)
        {
            switch (channel)
            {
                case SlashChannel.Terminal:
                    return SlashRoute.Typed;

                case SlashChannel.OpenClaw:
                    return SlashRoute.PassThrough;

                case SlashChannel.PeerSocket:
                    return SlashRoute.Unavailable(PeerSocketReason);

                case SlashChannel.Cloud:
                    if (string.Equals(name, "/color", StringComparison.OrdinalIgnoreCase))
                        return SlashRoute.Unavailable(CloudNoColorReason);
                    return ControlSubtypes.TryGetValue(name, out var cloudSubtype)
                        ? SlashRoute.Control(cloudSubtype)
                        : SlashRoute.Unavailable(PeerMessageReason);

                case SlashChannel.Bridge:
                    return ControlSubtypes.TryGetValue(name, out var bridgeSubtype)
                        ? SlashRoute.Control(bridgeSubtype)
                        : SlashRoute.Unavailable(PeerMessageReason);

                default:
                    throw new ArgumentOutOfRangeException(nameof(channel), channel, null);
            }
        }

        // "/cmd the rest" → ("/cmd", "the rest"). The command is the first
        // whitespace-delimited token; a line with no slash has no command.
        public static (string Name, string Args)? Split(string line)
        {
            var trimmed = (line ?? "").Trim();
            if (trimmed.Length < 2 || trimmed[0] != '/') return null;

            var gap = trimmed.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
            return gap < 0
                ? (trimmed, "")
                : (trimmed[..gap], trimmed[(gap + 1)..].Trim());
        }

        // The `request` object for a Control route, or null with a reason when
        // the argument cannot be made into one. Field names are the measured
        // ones (docs/claude-cloud-findings.md, CB-306).
        public static JsonObject? ControlRequest(string subtype, string args, out string? problem)
        {
            problem = null;
            var value = (args ?? "").Trim();

            switch (subtype)
            {
                case "set_color":
                    if (value.Length == 0) { problem = "say which colour, e.g. /color pink, or /color default"; return null; }
                    return new JsonObject { ["subtype"] = subtype, ["color"] = value };

                case "rename_session":
                    if (value.Length == 0) { problem = "give the session a name, e.g. /rename my session"; return null; }
                    return new JsonObject { ["subtype"] = subtype, ["title"] = value };

                case "set_model":
                    // No bare /model: the CLI's own /model with no argument opens
                    // a picker, which this panel has no equivalent for, and a
                    // guess at "reset to default" would silently un-set a model
                    // someone chose. Measured: set_model answers success; a
                    // mapping of empty→default was never measured, so it is not
                    // invented here.
                    if (value.Length == 0) { problem = "name a model, e.g. /model opus"; return null; }
                    return new JsonObject { ["subtype"] = subtype, ["model"] = value };

                case "set_permission_mode":
                    if (value.Length == 0)
                    {
                        problem = "say which mode: " + string.Join(", ", PermissionModes);
                        return null;
                    }
                    // Validated rather than passed through: the mode names are
                    // case-sensitive camelCase, so a typo or a wrong case is a
                    // control request the far side would only reject after the
                    // round trip. Read from the CLI 2.1.317 binary's
                    // permission-mode set; "default" was measured to answer
                    // success (docs/claude-cloud-findings.md), the rest are
                    // binary-read.
                    if (!PermissionModes.Contains(value))
                    {
                        problem = $"\"{value}\" is not a permission mode; use one of: "
                            + string.Join(", ", PermissionModes);
                        return null;
                    }
                    return new JsonObject { ["subtype"] = subtype, ["mode"] = value };

                case "set_max_thinking_tokens":
                    if (value.Length == 0 || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
                        return new JsonObject { ["subtype"] = subtype, ["max_thinking_tokens"] = null };
                    if (!int.TryParse(value, out var budget) || budget < 0)
                    {
                        problem = "give a token budget as a whole number, e.g. /think 8000, or /think default";
                        return null;
                    }
                    return new JsonObject { ["subtype"] = subtype, ["max_thinking_tokens"] = budget };

                default:
                    problem = $"no control request is known for {subtype}";
                    return null;
            }
        }

        // The note a session writes when a typed command cannot run here.
        public static string RefusalNote(string name, string reason) =>
            $"Not sent: {name} can't run from here — {reason}.";
    }
}
