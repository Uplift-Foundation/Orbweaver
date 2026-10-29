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

        // The whole page: the same spans as RollupsToDrop (a boundary is a user
        // message, a rollup, or the page start), with the set holding steps as
        // well as rollups. Tool lines are not text, so a dropped step message still
        // carries its toolcall blocks and the caller emits them: tool lines never
        // drop or move. Steps after the last rollup are a run in flight.
        //
        // startsMidRun: this page does not begin at the start of a run, because an
        // older page exists. Its first span, up to the first user turn or rollup,
        // may then be missing steps that sit on the older page, so (c) cannot be
        // decided from this page alone and is left to the boundary, which sees
        // both halves (ResolveSpan over the combined span). (b) is safe on a cut
        // span: covered by some of the steps means covered by all of them.
        internal static HashSet<int> TextsToDrop(JsonElement messages, bool startsMidRun = false)
        {
            var drop = new HashSet<int>();
            if (messages.ValueKind != JsonValueKind.Array) return drop;

            var steps = new List<(int Index, string Text)>();
            var seenBoundary = false;
            var index = 0;

            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user")
                {
                    steps.Clear();
                    seenBoundary = true;
                }
                else if (IsRollup(message))
                {
                    var decision = ResolveSpan(steps.Select(x => x.Text).ToList(), TextOf(message));
                    var cut = startsMidRun && !seenBoundary;

                    if (decision.DropRollup)
                        drop.Add(index);
                    else if (!cut)
                        foreach (var covered in decision.DropSteps)
                            drop.Add(steps[covered].Index);

                    steps.Clear();
                    seenBoundary = true;
                }
                else if (Str(message, "role") == "assistant" && HasText(message))
                {
                    steps.Add((index, TextOf(message)));
                }

                index++;
            }

            return drop;
        }

        internal readonly record struct SpanStep(int Index, string Text);

        // The text steps of the page's last span when no rollup closes it: the run
        // an older page ends in. Empty when the page ends at a user turn or a
        // rollup.
        internal static List<SpanStep> OpenTailSteps(JsonElement messages)
        {
            var steps = new List<SpanStep>();
            if (messages.ValueKind != JsonValueKind.Array) return steps;

            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user" || IsRollup(message))
                    steps.Clear();
                else if (Str(message, "role") == "assistant" && HasText(message))
                    steps.Add(new SpanStep(index, TextOf(message)));

                index++;
            }

            return steps;
        }

        // The head of a newer page: the text steps before its first rollup, and
        // that rollup's index, provided no user turn comes first (otherwise the
        // span started on this page and there is nothing to join). RollupIndex is
        // -1 when there is no such rollup.
        internal static (List<SpanStep> LeadSteps, int RollupIndex) HeadSpan(JsonElement messages)
        {
            var lead = new List<SpanStep>();
            if (messages.ValueKind != JsonValueKind.Array) return (lead, -1);

            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                if (Str(message, "role") == "user") return (lead, -1);
                if (IsRollup(message)) return (lead, index);
                if (Str(message, "role") == "assistant" && HasText(message))
                    lead.Add(new SpanStep(index, TextOf(message)));

                index++;
            }

            return (lead, -1);
        }

        // Which of the steps a (kept) rollup contains: the positions in
        // `stepTexts` whose whitespace-collapsed, non-empty text is an ordinal
        // substring of the rollup's collapsed text. The one place both TextsToDrop
        // and the page-boundary path decide this, so they cannot disagree.
        internal static List<int> StepsCoveredBy(string rollupText, IReadOnlyList<string> stepTexts)
        {
            var rollup = Collapse(rollupText);
            var covered = new List<int>();

            for (var i = 0; i < stepTexts.Count; i++)
            {
                var text = Collapse(stepTexts[i]);
                if (text.Length > 0 && rollup.Contains(text, System.StringComparison.Ordinal))
                    covered.Add(i);
            }

            return covered;
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
