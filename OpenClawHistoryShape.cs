using System.Collections.Generic;
using System.Text.Json;

namespace Orbweaver
{
    // CB-270: the two facts about an OpenClaw history page that decide what a
    // transcript draws, kept pure (System.Text.Json and nothing else) so
    // tests/TranscriptTests can compile it in and run it against real captures.
    //
    // A tool-using run is stored twice. The gateway writes one `anthropic-messages`
    // message per *step* — step text, then a message holding the `toolcall` and its
    // `tool_result`, then the closing step text — and then a single `cli` rollup
    // whose text is the steps' texts joined by a blank line. Drawn naively the
    // reply appears twice (measured on develop at 550cee45: "probe ok, 18 files"
    // rendered as step 3 and again inside the rollup). A run with no tools is
    // stored once, as a lone rollup, and that one must be kept: it is the reply.
    internal static class OpenClawHistoryShape
    {
        // An assistant message the gateway synthesised from a whole run. Both
        // markers were measured on the same captures and either is enough: the
        // `api` of "cli", and an idempotencyKey of "cli-assistant:<runId>".
        internal static bool IsRollup(JsonElement message)
        {
            if (message.ValueKind != JsonValueKind.Object) return false;
            if (Str(message, "role") != "assistant") return false;

            return Str(message, "api") == "cli"
                || (Str(message, "idempotencyKey")?.StartsWith("cli-assistant:", System.StringComparison.Ordinal) ?? false);
        }

        // The indices of rollups that repeat steps already on the page.
        //
        // The pairing is by position, and by position only. No id links a
        // rollup to its steps (measured), and comparing texts would let a gateway
        // that changed its join silently start doubling the reply. A *span* is
        // the messages since the last boundary, and a boundary is a user message,
        // a rollup, or the page's start: a rollup closes the run it summarises, so
        // two runs with no user turn between them (a heartbeat or cron job
        // injecting into a main session, back-to-back agent-initiated runs) are
        // judged separately. A rollup is dropped iff the span before it holds at
        // least one step — an assistant message that is not a rollup — with a
        // non-empty text block. A rollup with no text step before it (a run with
        // no tools, which is every message of a WhatsApp-shaped page; or steps
        // that carry only tool blocks, so there is no step text to double) is
        // kept, as is a page with no rollups at all. Steps after the last rollup
        // belong to a run still in flight and are left alone.
        internal static HashSet<int> RollupsToDrop(JsonElement messages)
        {
            var drop = new HashSet<int>();
            if (messages.ValueKind != JsonValueKind.Array) return drop;

            var spanHasStepText = false;
            var index = 0;

            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user")
                {
                    spanHasStepText = false;
                }
                else if (IsRollup(message))
                {
                    if (spanHasStepText) drop.Add(index);
                    spanHasStepText = false;
                }
                else if (Str(message, "role") == "assistant" && HasText(message))
                {
                    spanHasStepText = true;
                }

                index++;
            }

            return drop;
        }

        // Whether the page ends in a run that has text steps and no rollup after
        // them: the last span (since the last user turn or rollup) holds a text
        // step. For an *older* page that means the run's rollup is on the
        // newer page, split from its steps by the page boundary, and the
        // caller has to remove it when it prepends this page (CB-270).
        internal static bool EndsInOpenTextStep(JsonElement messages)
        {
            if (messages.ValueKind != JsonValueKind.Array) return false;

            var spanHasStepText = false;

            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user" || IsRollup(message))
                    spanHasStepText = false;
                else if (Str(message, "role") == "assistant" && HasText(message))
                    spanHasStepText = true;
            }

            return spanHasStepText;
        }

        // Every `toolcall` block in an assistant content array, with whether a
        // `tool_result` in the same array answers it with an error.
        //
        // IsError is read from `is_error` or `isError` — **assumed names**. The
        // one captured result carries neither key (tool_use_id, content, name,
        // type only), so a failed tool read back from history has never been
        // observed; the live `session.tool` result does carry `isError`, which is
        // where the second spelling comes from.
        internal static IEnumerable<(string? Id, string? Name, JsonElement Args, bool IsError)> ToolCalls(
            JsonElement content)
        {
            if (content.ValueKind != JsonValueKind.Array) yield break;

            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") != "toolcall") continue;

                var id = Str(block, "id");
                var args = block.TryGetProperty("arguments", out var a) ? a : default;

                yield return (id, Str(block, "name"), args, id is not null && ResultIsError(content, id));
            }
        }

        private static bool ResultIsError(JsonElement content, string id)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") != "tool_result") continue;
                if (Str(block, "tool_use_id") != id) continue;

                if (Flag(block, "is_error") || Flag(block, "isError")) return true;
            }

            return false;
        }

        private static bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static bool HasText(JsonElement message)
        {
            if (!message.TryGetProperty("content", out var content)) return false;

            if (content.ValueKind == JsonValueKind.String)
                return !string.IsNullOrWhiteSpace(content.GetString());

            if (content.ValueKind != JsonValueKind.Array) return false;

            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") == "text" && !string.IsNullOrWhiteSpace(Str(block, "text")))
                    return true;
            }

            return false;
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
