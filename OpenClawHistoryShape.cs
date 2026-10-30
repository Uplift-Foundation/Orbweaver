using System.Collections.Generic;
using System.Linq;
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

        // CB-270 stage 3b: the rule for one span, in one place. Given the text
        // steps S1..Sk of a span (in order) and the rollup R that closes it:
        //
        //   (a) k == 0: nothing to decide, keep R.
        //   (b) R is covered by the steps (RollupIsCoveredBy): drop R, keep every
        //       step. Exactly the stage-3 coverage rule.
        //   (c) otherwise keep R and drop every step whose collapsed text occurs
        //       in R's collapsed text (StepsCoveredBy); uncovered steps stay.
        //
        // (c) is what removes the repeat (b) alone leaves: a rollup that holds
        // every step plus more is kept whole, so nothing is lost, and its covered
        // steps would otherwise be drawn a second time. Dropping a step is safe
        // by construction because its text is inside the rollup that stays.
        internal readonly record struct SpanDecision(bool DropRollup, IReadOnlyList<int> DropSteps);

        internal static SpanDecision ResolveSpan(IReadOnlyList<string> stepTexts, string rollupText)
        {
            if (stepTexts.Count == 0) return new SpanDecision(false, System.Array.Empty<int>());

            if (RollupIsCoveredBy(rollupText, stepTexts))
                return new SpanDecision(true, System.Array.Empty<int>());

            return new SpanDecision(false, StepsCoveredBy(rollupText, stepTexts));
        }

        // The whole page: spans are bounded by a user message, a rollup, or the
        // page start, and the set it returns holds steps as well as rollups. Tool
        // lines are not text, so a dropped step message still carries its toolcall
        // blocks and the caller emits them: tool lines never drop or move. Steps
        // after the last rollup are a run in flight.
        //
        // Rule (c) is applied on every span, including a page's first, whose
        // earlier steps may sit on an older page: a kept rollup on this page holds
        // the words of any step it contains, so dropping that step needs nothing
        // from the older page. What the older page can still change is only which
        // of *its* steps the same rollup covers, and the page boundary does that
        // (PrependHistory, using OpenTailSteps and HeadRollupIndex). Where the whole
        // run is an exact join across the seam, the page's outcome stands: the
        // rollup kept and the steps dropped, the same words as the whole-page
        // parse with the narration at the end instead of interleaved.
        internal static HashSet<int> TextsToDrop(JsonElement messages)
        {
            var drop = new HashSet<int>();
            if (messages.ValueKind != JsonValueKind.Array) return drop;

            var steps = new List<(int Index, string Text)>();
            var index = 0;

            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user")
                {
                    steps.Clear();
                }
                else if (IsRollup(message))
                {
                    var decision = ResolveSpan(steps.Select(x => x.Text).ToList(), TextOf(message));

                    if (decision.DropRollup)
                        drop.Add(index);
                    else
                        foreach (var covered in decision.DropSteps)
                            drop.Add(steps[covered].Index);

                    steps.Clear();
                }
                else if (Str(message, "role") == "assistant" && HasText(message))
                {
                    steps.Add((index, TextOf(message)));
                }

                index++;
            }

            return drop;
        }

        // The message indices of the text steps in the page's last span when no
        // rollup closes it: the run an older page ends in. Boundaries are the raw
        // messages' (a user message, a rollup), not the turns', because a user
        // message can render to no turn at all (scaffolding) and a boundary read off
        // turns would vanish with it. Empty when the page ends at a user message or
        // a rollup.
        internal static List<int> OpenTailSteps(JsonElement messages)
        {
            var steps = new List<int>();
            if (messages.ValueKind != JsonValueKind.Array) return steps;

            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user" || IsRollup(message))
                    steps.Clear();
                else if (Str(message, "role") == "assistant" && HasText(message))
                    steps.Add(index);

                index++;
            }

            return steps;
        }

        // The index of the first rollup on the page with no user message before it:
        // the rollup that closes the run an older page's trailing steps belong to,
        // if there is an older page. -1 when a user message comes first or there is
        // none.
        internal static int HeadRollupIndex(JsonElement messages)
        {
            if (messages.ValueKind != JsonValueKind.Array) return -1;

            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user") return -1;
                if (IsRollup(message)) return index;
                index++;
            }

            return -1;
        }

        private static readonly System.Text.RegularExpressions.Regex Whitespace =
            new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string Collapse(string s) => Whitespace.Replace(s, " ").Trim();

        // Which of the steps a (kept) rollup contains: the positions in `stepTexts`
        // whose whitespace-collapsed, non-empty text occurs in the rollup's collapsed
        // text, each occurrence accounting for one step. Steps are taken in order and
        // an occurrence is used up once it covers a step, so two steps with the same
        // words are both covered only if the rollup says them twice; otherwise the
        // second is a statement the rollup does not repeat and stays. The one place
        // TextsToDrop and the page boundary decide this, so they cannot disagree.
        internal static List<int> StepsCoveredBy(string rollupText, IReadOnlyList<string> stepTexts)
        {
            var remaining = Collapse(rollupText);
            var covered = new List<int>();

            for (var i = 0; i < stepTexts.Count; i++)
            {
                var text = Collapse(stepTexts[i]);
                if (text.Length == 0) continue;

                var at = remaining.IndexOf(text, System.StringComparison.Ordinal);
                if (at < 0) continue;

                remaining = remaining.Remove(at, text.Length);
                covered.Add(i);
            }

            return covered;
        }

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
