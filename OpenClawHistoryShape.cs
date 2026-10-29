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

        // The indices of rollups that only repeat steps already on the page.
        //
        // A *span* is the messages since the last boundary — a user message, a
        // rollup, or the page start — so a rollup closes the run it summarises
        // and runs with no user turn between them are judged separately. A
        // rollup is dropped iff a text step precedes it in its span AND its text
        // is covered by those steps' texts (see RollupIsCoveredBy). Everything
        // else is kept: a lone rollup (a run with no tools), one whose steps
        // carry only tool blocks, and — the case position alone got wrong — one
        // that carries text no step does.
        //
        // That last arm is a data-loss fix (CB-270 QA, measured on real Discord
        // and main-session pages): in runs where the final reply was never
        // persisted as a step, the rollup *is* the reply, and "a text step
        // precedes it" held anyway, so the user's answer vanished. Of the seven
        // rollups with text steps before them on four real 40-message pages,
        // one equalled the join of its steps (the shape Phase 0 captured), three
        // held the last step plus text found in no step, and three shared none
        // of their text with any step. A repeated line is the cost of the keep
        // arm; a lost reply is the cost of getting it wrong the other way.
        //
        // This replaces the plan's earlier "position only" rule, which argued
        // that comparing texts would let a changed join double the reply. That
        // is still true of a changed join, and is now the cheaper failure.
        internal static HashSet<int> RollupsToDrop(JsonElement messages)
        {
            var drop = new HashSet<int>();
            if (messages.ValueKind != JsonValueKind.Array) return drop;

            var stepTexts = new List<string>();
            var index = 0;

            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user")
                {
                    stepTexts.Clear();
                }
                else if (IsRollup(message))
                {
                    if (stepTexts.Count > 0 && RollupIsCoveredBy(TextOf(message), stepTexts))
                        drop.Add(index);

                    stepTexts.Clear();
                }
                else if (Str(message, "role") == "assistant" && HasText(message))
                {
                    stepTexts.Add(TextOf(message));
                }

                index++;
            }

            return drop;
        }

        private static readonly System.Text.RegularExpressions.Regex Whitespace =
            new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string Collapse(string s) => Whitespace.Replace(s, " ").Trim();

        // Whether every word of the rollup is accounted for by the steps: each
        // step's text (whitespace collapsed) is removed from the rollup once,
        // and what is left must be empty. Equal to the join of the steps, or to
        // any subset of them, is covered; anything extra is not. Steps the
        // rollup does not contain are simply not removed — only text the rollup
        // adds matters, because only that can be lost.
        internal static bool RollupIsCoveredBy(string rollupText, IEnumerable<string> stepTexts)
        {
            var remaining = Collapse(rollupText);

            foreach (var step in stepTexts)
            {
                var text = Collapse(step);
                if (text.Length == 0) continue;

                var at = remaining.IndexOf(text, System.StringComparison.Ordinal);
                if (at >= 0) remaining = remaining.Remove(at, text.Length);
            }

            return Collapse(remaining).Length == 0;
        }

        // The text blocks of a message, joined by a blank line (a string content
        // is its own text).
        private static string TextOf(JsonElement message)
        {
            if (!message.TryGetProperty("content", out var content)) return "";

            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
            if (content.ValueKind != JsonValueKind.Array) return "";

            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") == "text" && Str(block, "text") is { } t && !string.IsNullOrWhiteSpace(t))
                    parts.Add(t);
            }

            return string.Join("\n\n", parts);
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
        // IsError is read from `is_error` or `isError`. Measured on openclaw
        // 2026.10.5 with a deliberately failing tool: a chat.history tool_result
        // carries `is_error: true` (a success has no error key), and the live
        // `session.tool` result carries `isError: true`. Both spellings are
        // read here so either shape marks the call.
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
