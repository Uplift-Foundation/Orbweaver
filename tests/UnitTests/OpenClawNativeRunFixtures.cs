namespace Orbweaver.Tests
{
    // CB-270: the `messages` array of a real chat.history page for a native
    // (dashboard) OpenClaw session, openclaw 2026.10.5, scrubbed from the Phase 0
    // capture: one tool-using run (user, three `anthropic-messages` steps — the
    // second holding the `toolcall` and its `tool_result` — and the `cli` rollup
    // that repeats the steps' text), then one run with no tools (user, lone
    // rollup). Reduced to the fields the history parser reads (role, content,
    // api, idempotencyKey, timestamp); ids are placeholders and the shell
    // command's path is scrubbed. The order and the presence or absence of each
    // field are the capture's own.
    //
    // The `tool_result` block carries no error key at all, which is why the
    // history-side error flag rests on an assumed field name (see
    // OpenClawHistoryShape.ToolCalls).
    internal static class OpenClawNativeRunFixtures
    {
        internal const string ToolRunPage = """
[
{"role":"user","content":"Please run one shell command, ls in your workspace directory, then reply with exactly: probe ok, N files — where N is the count.","idempotencyKey":"run-tools-0001:user","timestamp":1793285895796},
{"role":"assistant","content":[{"type":"text","text":"Hey Boss! I'm Nova — your main agent, here to keep things running, manage the other agents, and generally be your person. Looks like a couple of notes were touched most recently today."}],"api":"anthropic-messages","timestamp":1793285903316},
{"role":"assistant","content":[{"type":"toolcall","id":"toolu_01FIXTURE0000000000000001","name":"mcp__openclaw__exec","caller":{"type":"direct"},"arguments":{"command":"ls /home/op/workspace | wc -l","title":"Count workspace files"}},{"tool_use_id":"toolu_01FIXTURE0000000000000001","type":"tool_result","content":[{"type":"text","text":"18"}],"name":"mcp__openclaw__exec"}],"api":"anthropic-messages","timestamp":1793285904021},
{"role":"assistant","content":[{"type":"text","text":"probe ok, 18 files"}],"api":"anthropic-messages","timestamp":1793285906658},
{"role":"assistant","content":[{"type":"text","text":"Hey Boss! I'm Nova — your main agent, here to keep things running, manage the other agents, and generally be your person. Looks like a couple of notes were touched most recently today.\n\nprobe ok, 18 files"}],"api":"cli","idempotencyKey":"cli-assistant:run-tools-0001","timestamp":1793285907035},
{"role":"user","content":"Reply with exactly: probe ok, deliver true. Do not run any tools.","idempotencyKey":"run-notools-0002:user","timestamp":1793285952025},
{"role":"assistant","content":[{"type":"text","text":"probe ok, deliver true"}],"api":"cli","idempotencyKey":"cli-assistant:run-notools-0002","timestamp":1793285954029}
]
""";
    }
}
