namespace Orbweaver
{
    // The only way this app builds a `sessions.patch` request (CB-270).
    //
    // A patch is one of the gateway's most powerful verbs: its write-scope field
    // set (measured against 2026.10.5, docs/openclaw-findings.md, CB-270) is
    //   label, autoLabel, icon, color, category, boardFace, boardPresentation,
    //   pinned, archived, snoozedUntil, unread, model, agentRuntime,
    //   thinkingLevel, fastMode, permissionMode
    // plus an envelope (key, agentId, expectedSessionId, ...), and it demands
    // admin for `permissionMode: "full"` or any `sandboxMode`.
    //
    // This app uses six of those fields: archived, pinned and label, and, for a
    // native session's run controls (Stage 4), model, thinkingLevel and fastMode.
    // `permissionMode` is *in* the write-scope set and is deliberately left out
    // here: "full" needs admin, and nothing in this app has a reason to send any
    // value of it. `sandboxMode` is out of the write-scope set altogether. The
    // constructor is private and each factory builds exactly one shape, so
    // sending either of them means editing this file, where the next reader will
    // meet this paragraph, rather than adding a key to a dictionary somewhere.
    //
    // expectedSessionId rides on every one: it guards against changing a
    // conversation that has since been replaced under the same key. Measured
    // beside `archived`; beside `pinned` and `label` it is declared by the
    // envelope rather than measured, and QA confirms it live.
    internal sealed class OpenClawSessionPatch
    {
        // Every field a factory here may send. Public to tests, which assert the
        // union of what the factories build stays inside it, and that it never
        // grows a sandbox or permission field.
        internal static readonly IReadOnlySet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
        {
            "key", "expectedSessionId", "archived", "pinned", "label",
            "model", "thinkingLevel", "fastMode"
        };

        private OpenClawSessionPatch(IReadOnlyDictionary<string, object?> parameters) =>
            Params = parameters;

        // Insertion order is the order on the wire, which is what the existing
        // archive request has always sent: key, the change, then expectedSessionId.
        internal IReadOnlyDictionary<string, object?> Params { get; }

        internal static OpenClawSessionPatch Archive(string key, string sessionId) =>
            new(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["archived"] = true,
                ["expectedSessionId"] = sessionId
            });

        internal static OpenClawSessionPatch Pin(string key, string sessionId, bool pinned) =>
            new(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["pinned"] = pinned,
                ["expectedSessionId"] = sessionId
            });

        internal static OpenClawSessionPatch Rename(string key, string sessionId, string label) =>
            new(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["label"] = label,
                ["expectedSessionId"] = sessionId
            });

        // Run controls for a native session (Stage 4). Each sends one field and the
        // value the picker chose by id. A blank value is refused here, not at the
        // gateway: a blank model or level is never what anyone meant, and an empty
        // string on the wire is a different request from "reset to default",
        // which is not how a field is cleared (see ClearModel, ClearThinking and ClearFast).
        internal static OpenClawSessionPatch Model(string key, string sessionId, string model) =>
            new(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["model"] = NotBlank(model, nameof(model)),
                ["expectedSessionId"] = sessionId
            });

        internal static OpenClawSessionPatch Thinking(string key, string sessionId, string level) =>
            new(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["thinkingLevel"] = NotBlank(level, nameof(level)),
                ["expectedSessionId"] = sessionId
            });

        internal static OpenClawSessionPatch Fast(string key, string sessionId, bool on) =>
            new(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["fastMode"] = on,
                ["expectedSessionId"] = sessionId
            });

        // Back to the default, by sending JSON null: measured on 2026.10.5 to clear
        // each of these three, after which the row reads back as it did before
        // anything was chosen. An override and a chosen value that happens to equal
        // the default are different facts, and this is the only way to get back to
        // the first. Null on the wire is the point, so Params carries a real null
        // here and not a missing key.
        internal static OpenClawSessionPatch ClearModel(string key, string sessionId) => Clear(key, sessionId, "model");

        internal static OpenClawSessionPatch ClearThinking(string key, string sessionId) =>
            Clear(key, sessionId, "thinkingLevel");

        internal static OpenClawSessionPatch ClearFast(string key, string sessionId) =>
            Clear(key, sessionId, "fastMode");

        private static OpenClawSessionPatch Clear(string key, string sessionId, string field) =>
            new(new Dictionary<string, object?>
            {
                ["key"] = key,
                [field] = null,
                ["expectedSessionId"] = sessionId
            });

        private static string NotBlank(string value, string name) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("a value is required", name)
                : value;
    }
}
