using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Orbweaver.UnitTests;

// CB-270: the pairing rule against seven scrubbed real 40-message chat.history
// pages (five Discord sessions, alexis:main and main:main), taken from a read-only
// capture on openclaw 2026.10.5. Text is replaced by tokens with equality and
// containment between messages preserved, so these pages test the rule's shape
// decisions and nothing about the words. They include `openclaw-transcript`
// runtime pages, which have no rollups at all, and api-less claude-cli rows.
//
// The assertions are about *this* rule (text coverage), not the later
// step-dropping refinement: no reply text is lost, pages without rollups are
// untouched, and a page split at any index reads the same as the whole.
public class OpenClawRealPageTests
{
    private readonly ITestOutputHelper _out;

    public OpenClawRealPageTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Pages() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pages"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileName(f) });

    private static JsonElement Load(string file)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pages", file)));
        return doc.RootElement.TryGetProperty("messages", out var m) ? m.Clone() : doc.RootElement.Clone();
    }

    private static List<JsonElement> Messages(string file) => Load(file).EnumerateArray().ToList();

    private static JsonElement Array(IEnumerable<JsonElement> items) =>
        JsonDocument.Parse("[" + string.Join(",", items.Select(i => i.GetRawText())) + "]").RootElement;

    private static (ChatRole Role, string Text)[] Seq(IEnumerable<HistoryTurn> turns) =>
        turns.Select(t => (t.Role, t.Text)).ToArray();

    private static string TextOf(JsonElement m)
    {
        if (!m.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        return string.Join(" ", c.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var ty) && ty.GetString() == "text" && b.TryGetProperty("text", out _))
            .Select(b => b.GetProperty("text").GetString()));
    }

    private static string Role(JsonElement m) => m.GetProperty("role").GetString() ?? "";

    private static IEnumerable<string> Tokens(string t) =>
        t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // The steps in a rollup's span, found independently of the rule: walk back
    // from the rollup to the previous user turn or rollup.
    private static List<string> SpanSteps(List<JsonElement> all, int rollup)
    {
        var steps = new List<string>();
        for (var j = rollup - 1; j >= 0; j--)
        {
            if (Role(all[j]) == "user" || OpenClawHistoryShape.IsRollup(all[j])) break;
            if (Role(all[j]) == "assistant" && TextOf(all[j]).Trim().Length > 0) steps.Add(TextOf(all[j]));
        }
        return steps;
    }

    private static string Norm(string s) => string.Join(" ", Tokens(s));

    // ---- no reply text is lost ------------------------------------------

    [Theory]
    [MemberData(nameof(Pages))]
    public void NoDroppedRollupHasTextOutsideItsStepsAndEveryOtherRollupSurvives(string file)
    {
        var all = Messages(file);
        var drop = OpenClawHistoryShape.RollupsToDrop(Load(file));

        // Independent of RollupIsCoveredBy: a token-multiset check.
        foreach (var i in drop)
        {
            Assert.True(OpenClawHistoryShape.IsRollup(all[i]));

            var pool = SpanSteps(all, i).SelectMany(Tokens).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            foreach (var token in Tokens(TextOf(all[i])))
            {
                Assert.True(pool.TryGetValue(token, out var n) && n > 0, $"{file} rollup@{i} has text its steps lack");
                pool[token]--;
            }
        }

        // Every rollup that is not dropped still produces a turn (text-bearing).
        var turns = OpenClawSessions.TurnsFromHistory(Load(file), "k");
        var keptRollups = Enumerable.Range(0, all.Count)
            .Where(i => OpenClawHistoryShape.IsRollup(all[i]) && !drop.Contains(i) && TextOf(all[i]).Trim().Length > 0)
            .ToList();
        Assert.Equal(keptRollups.Count, turns.Count(t => t.FromRollup));
    }

    // ---- pages without rollups are today's parse ------------------------

    // Strip toolcall and tool_result blocks and the parse is what it was before
    // tool lines existed; the full parse must be that plus the tool lines and
    // nothing else, and no rollup may be dropped or marked.
    [Theory]
    [MemberData(nameof(Pages))]
    public void ToolLinesAreTheOnlyAdditionOnAPageWithNoRollups(string file)
    {
        var all = Messages(file);
        if (all.Any(OpenClawHistoryShape.IsRollup)) return;

        Assert.Empty(OpenClawHistoryShape.RollupsToDrop(Load(file)));

        var full = OpenClawSessions.TurnsFromHistory(Load(file), "k");
        Assert.DoesNotContain(full, t => t.FromRollup);

        var stripped = JsonNode.Parse(Load(file).GetRawText())!.AsArray();
        foreach (var msg in stripped)
        {
            if (msg?["content"] is not JsonArray blocks) continue;
            for (var b = blocks.Count - 1; b >= 0; b--)
            {
                var type = blocks[b]?["type"]?.GetValue<string>();
                if (type is "toolcall" or "tool_result") blocks.RemoveAt(b);
            }
        }

        var before = OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(stripped.ToJsonString()).RootElement, "k");
        var withoutToolLines = full.Where(t => !(t.Role == ChatRole.System && t.Text.StartsWith("· ", StringComparison.Ordinal)));

        Assert.True(Seq(before).SequenceEqual(Seq(withoutToolLines)), $"{file}: non-tool turns changed");
    }

    // ---- a split page reads like the whole ------------------------------

    [Theory]
    [MemberData(nameof(Pages))]
    public void SplitAtEveryIndexEqualsTheFullPageParse(string file)
    {
        var all = Messages(file);
        var want = Seq(OpenClawSessions.TurnsFromHistory(Load(file), "k"));
        var differ = new List<int>();

        for (var split = 1; split < all.Count; split++)
        {
            var older = Array(all.Take(split));
            var newer = Array(all.Skip(split));

            var session = new OpenClawChatSession("openclaw:k", "k", "main");
            session.SetHistory(OpenClawSessions.TurnsFromHistory(newer, "k"));
            session.PrependHistory(OpenClawSessions.TurnsFromHistory(older, "k"),
                OpenClawHistoryShape.EndsInOpenTextStep(older));

            if (!Seq(session.History.Select(h => new HistoryTurn(h.Role, h.Text, null, "", h.At, null, null))).SequenceEqual(want))
                differ.Add(split);
        }

        Assert.True(differ.Count == 0, $"{file}: splits differing from the full parse: {string.Join(",", differ)}");
    }

    // ---- what this rule still repeats -----------------------------------

    // A kept rollup that holds text from steps before it draws that text twice:
    // once as the steps, once inside the rollup. This is the cost of never
    // dropping a rollup that carries more than its steps, and the same repeat
    // develop shows today. Counted here so the number is on record; the
    // step-dropping refinement removes it.
    [Fact]
    public void TheRepeatedNarrationOnTheRealPagesIsCounted()
    {
        var total = 0;
        var perPage = new Dictionary<string, int>();

        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pages"), "*.json")
                     .Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal))
        {
            var all = Messages(file!);
            var drop = OpenClawHistoryShape.RollupsToDrop(Load(file!));
            var repeated = 0;

            for (var i = 0; i < all.Count; i++)
            {
                if (!OpenClawHistoryShape.IsRollup(all[i]) || drop.Contains(i)) continue;

                var rollup = Norm(TextOf(all[i]));
                repeated += SpanSteps(all, i).Count(s => Norm(s).Length > 0 && rollup.Contains(Norm(s), StringComparison.Ordinal));
            }

            _out.WriteLine($"{file}: repeated step texts = {repeated}");
            perPage[file!] = repeated;
            total += repeated;
        }

        _out.WriteLine($"total repeated step texts = {total}");

        // Measured: 3 on main-plus-more, 1 on direct-plus-more and 1 on
        // main-equal-join-and-plus-more, none elsewhere. A change here means
        // the rule (or the fixtures) moved and the PR's stated cost with it.
        Assert.Equal(5, total);
        Assert.Equal(3, perPage["page-main-plus-more.json"]);
        Assert.Equal(1, perPage["page-direct-plus-more.json"]);
        Assert.Equal(1, perPage["page-main-equal-join-and-plus-more.json"]);
    }
}
