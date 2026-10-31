using System.Text.Json;

namespace Orbweaver
{
    // CB-170: what an OpenClaw orb's two gateway rows — "Interrupt the current
    // run" and "End the conversation" — need to know, and what to make of the
    // answers. Everything here is pure: no socket, no window, no settings. The
    // requests themselves are OpenClawSessions.InterruptAsync and
    // EndConversationAsync; the rule for when a row is offered is in
    // SessionPresence beside CanEndSession; the wording is OpenClawActionText.
    //
    // Measured against a real gateway (OpenClaw 2026.9.2) before any of this was
    // written, and recorded on CB-170: both verbs need operator.write and
    // nothing more; chat.abort on a session with nothing running answers
    // aborted:false rather than failing; sessions.patch refuses to archive
    // without expectedSessionId; the gateway refuses to archive an agent's main
    // session. The rest of this file is shaped by those four facts.

    // What the menu is allowed to assume about one orb's conversation at the
    // moment it is drawn. Built by OpenClawSessions.CapabilitiesFor from the
    // live connection and the last session list; a test builds one by hand.
    internal sealed record OpenClawActionContext(
        bool Connected,

        // hello-ok's features.methods. Not filtered by scope — the gateway
        // lists sessions.reset to a device that cannot call it — which is why
        // every rule reads Scopes as well.
        IReadOnlySet<string> Methods,

        // What the gateway actually granted this device on connect.
        IReadOnlyList<string> Scopes,

        bool IsMain,

        // The id sessions.patch demands as expectedSessionId. Null when the
        // session is not in the last list, which is also when End cannot work.
        string? SessionId,

        // The gateway's key for the conversation, without the app's
        // "openclaw:" prefix.
        string? Key,

        // CB-270: the gateway's pin and the conversation's current name, for
        // the Keep on screen checkbox and the Rename window. Defaulted so every
        // construction that predates them is unchanged.
        bool GatewayPinned = false,
        string? Label = null)
    {
        public static readonly OpenClawActionContext None = new(
            false, new HashSet<string>(), Array.Empty<string>(), false, null, null);
    }

    public enum OpenClawAction
    {
        Interrupt,
        End,

        // CB-270. Pin and unpin are two actions rather than one with a flag
        // because the row's wording differs for each outcome.
        KeepOnScreen,
        StopKeeping,
        Rename,

        // CB-270 Stage 4: a native conversation's run controls. Each sends one
        // sessions.patch field, and each can also be sent back to its default.
        Model,
        Thinking,
        Fast,
    }

    public enum OpenClawActionOutcome
    {
        Done,

        // chat.abort answered aborted:false. Not a failure: the gateway was
        // asked to stop a run and there was none.
        NothingRunning,

        // chat.abort refused "unauthorized". A device without admin may abort
        // only runs it started itself or runs with no owner, so a run started
        // from somewhere else — the Control UI in a browser, another Buddy — is
        // out of reach. Declared by the gateway's source, not yet seen live.
        OtherDevice,

        // Any other answer from the gateway, carried verbatim as the detail.
        Refused,

        NotConnected,
    }

    internal static class OpenClawOrbActions
    {
        public const string AbortMethod = "chat.abort";
        public const string PatchMethod = "sessions.patch";
        public const string WriteScope = "operator.write";

        // The gateway's code for a refusal worth asking again. An archive sent
        // while the run it follows is still settling is refused this way, with
        // "retry the archive" in the message.
        public const string RetryableCode = "UNAVAILABLE";

        // chat.abort's answer. Only an explicit true is "interrupted", and only
        // an explicit false is "nothing was running"; a reply that says neither
        // is the gateway not telling us, and claiming either would be a guess
        // presented as a result.
        internal static (OpenClawActionOutcome Outcome, string? Detail) ParseAbortResult(JsonElement res)
        {
            if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("aborted", out var aborted))
            {
                if (aborted.ValueKind == JsonValueKind.True) return (OpenClawActionOutcome.Done, null);
                if (aborted.ValueKind == JsonValueKind.False) return (OpenClawActionOutcome.NothingRunning, null);
            }

            return (OpenClawActionOutcome.Refused, "the gateway didn't say whether anything stopped");
        }

        // sessions.patch's answer. The gateway sends ok:true with the updated
        // row; anything else is not a confirmed archive, whatever it is.
        //
        // `unconfirmed` is what the row says when it was not confirmed: the
        // default is the archive's, so End's wording is unchanged, and pin and
        // rename pass "the gateway didn't confirm the change".
        internal static (OpenClawActionOutcome Outcome, string? Detail) ParsePatchResult(
            JsonElement res, string unconfirmed = "the gateway didn't confirm the archive") =>
            res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("ok", out var ok)
            && ok.ValueKind == JsonValueKind.True
                ? (OpenClawActionOutcome.Done, null)
                : (OpenClawActionOutcome.Refused, unconfirmed);

        // A request that threw. "unauthorized" is matched exactly, on the
        // message, because that is the whole of what the gateway sends for it —
        // an INVALID_REQUEST whose message is the one word, with no detail code.
        // Everything else is shown as the gateway worded it: "missing scope:
        // operator.write" is more useful on the row than any paraphrase.
        internal static (OpenClawActionOutcome Outcome, string? Detail) ClassifyFailure(Exception ex) =>
            ex is OpenClawRequestException { Message: "unauthorized" }
                ? (OpenClawActionOutcome.OtherDevice, null)
                : (OpenClawActionOutcome.Refused, ex.Message);

        internal static bool IsRetryable(Exception ex) =>
            ex is OpenClawRequestException { Code: RetryableCode };
    }

    // Every word the two rows say, in one table so the menu and its tests read
    // the same sentences.
    internal static class OpenClawActionText
    {
        // Says it can be undone, because it can: an archive is reversed from
        // OpenClaw with archived:false, history intact (measured). Worded as
        // permanent it would be a warning about something that isn't true.
        public const string Armed = "End it? Click again (can be restored on the gateway)";

        public static string Header(OpenClawAction action) => action switch
        {
            OpenClawAction.Interrupt => "Interrupt the current run",
            OpenClawAction.End => "End the conversation",
            OpenClawAction.Rename => "Rename conversation…",
            OpenClawAction.Model => "Model",
            OpenClawAction.Thinking => "Thinking",
            OpenClawAction.Fast => "Fast mode",
            _ => "Keep on screen",
        };

        public static string Tip(OpenClawAction action) => action switch
        {
            OpenClawAction.Interrupt => "Stops the agent generating. The conversation stays.",
            OpenClawAction.End => "Archives this conversation on the gateway, so every client stops showing it. It can be restored from OpenClaw.",
            OpenClawAction.Rename => "Changes this conversation's name on the gateway, so every client shows the new one.",
            OpenClawAction.Model => "Changes the model this conversation runs on, in every client.",
            OpenClawAction.Thinking => "Changes how much this conversation's agent thinks before it answers, in every client.",
            OpenClawAction.Fast => "Turns fast mode on or off for this conversation, in every client.",
            _ => "Pins this conversation on the gateway, so it stays on screen however long it has been quiet — in every client.",
        };

        public static string Working(OpenClawAction action) => action switch
        {
            OpenClawAction.Interrupt => "Interrupting…",
            OpenClawAction.End => "Ending…",
            OpenClawAction.KeepOnScreen => "Keeping…",
            OpenClawAction.StopKeeping => "Releasing…",
            OpenClawAction.Model or OpenClawAction.Thinking or OpenClawAction.Fast => "Changing…",
            _ => "Renaming…",
        };

        public static string For(OpenClawActionOutcome outcome, string? detail, OpenClawAction action)
        {
            var verb = action switch
            {
                OpenClawAction.Interrupt => "interrupt",
                OpenClawAction.End => "end",
                OpenClawAction.KeepOnScreen => "keep it on screen",
                OpenClawAction.StopKeeping => "release it",
                OpenClawAction.Model => "change the model",
                OpenClawAction.Thinking => "change thinking",
                OpenClawAction.Fast => "change fast mode",
                _ => "rename",
            };

            return outcome switch
            {
                OpenClawActionOutcome.Done => action switch
                {
                    OpenClawAction.Interrupt => "Interrupted",
                    OpenClawAction.End => "Ended",
                    OpenClawAction.KeepOnScreen => "Kept on screen",
                    OpenClawAction.StopKeeping => "No longer kept on screen",
                    OpenClawAction.Model or OpenClawAction.Thinking or OpenClawAction.Fast => "Changed",
                    _ => "Renamed",
                },
                OpenClawActionOutcome.NothingRunning => "Nothing was running",
                OpenClawActionOutcome.OtherDevice => action switch
                {
                    OpenClawAction.Interrupt => "Started from another device",
                    OpenClawAction.End => "Couldn't end: a run started from another device is still going",
                    _ => $"Couldn't {verb}: the gateway said unauthorized",
                },
                OpenClawActionOutcome.NotConnected => $"Couldn't {verb}: not connected to the gateway",
                _ => $"Couldn't {verb}: {detail}",
            };
        }
    }
}
