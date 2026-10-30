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
    // Built from the captured message shapes, not captured itself; carries its
    // own expected drop and keep sets in a "cases" field. The other seven are real.
    private const string SyntheticPage = "page-synthetic-shapes.json";

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
        var drop = HistoryShapeTestHelpers.RollupsToDrop(Load(file));

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
        // On a real page every kept rollup is one turn. On the synthetic page a
        // dropped rollup still yields its picture turns, so there can be more.
        if (file == SyntheticPage) Assert.True(turns.Count(t => t.FromRollup) >= keptRollups.Count);
        else Assert.Equal(keptRollups.Count, turns.Count(t => t.FromRollup));
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

        Assert.Empty(HistoryShapeTestHelpers.RollupsToDrop(Load(file)));

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

            // As the panel loads them: the newer page first, cut at its start
            // because an older one exists; the older page is the conversation's
            // start.
            var session = new OpenClawChatSession("openclaw:k", "k", "main");
            session.SetHistory(OpenClawSessions.TurnsFromHistory(newer, "k", startsMidRun: true));
            session.PrependHistory(OpenClawSessions.TurnsFromHistory(older, "k"));

            if (!Seq(session.History.Select(h => new HistoryTurn(h.Role, h.Text, null, "", h.At, null, null))).SequenceEqual(want))
                differ.Add(split);
        }

        Assert.True(differ.Count == 0, $"{file}: splits differing from the full parse: {string.Join(",", differ)}");
    }

    // ---- what is still repeated -------------------------------------------

    // Stage 3 kept a rollup that holds every step plus more, which drew the steps'
    // narration twice: 5 step texts on the seven real pages. Stage 3b drops the
    // covered steps instead. Measured on the wired parse (not on the rule): for
    // each kept rollup turn, the shown step turns of its run whose text it contains.
    [Fact]
    public void NoRealPageRepeatsAStepsNarrationInsideAKeptRollupAnyMore()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pages"), "*.json")
                     .Select(Path.GetFileName).Where(f => f != SyntheticPage).OrderBy(f => f, StringComparer.Ordinal))
        {
            var turns = OpenClawSessions.TurnsFromHistory(Load(file!), "k");
            var repeated = 0;
            var steps = new List<string>();

            foreach (var t in turns)
            {
                if (t.Role == ChatRole.User) { steps.Clear(); continue; }

                if (t.FromRollup && t.Text.Length > 0)
                {
                    var rollup = Norm(t.Text);
                    repeated += steps.Count(x => x.Length > 0 && rollup.Contains(x, StringComparison.Ordinal));
                    steps.Clear();
                }
                else if (t.Role == ChatRole.Assistant && !t.FromRollup)
                {
                    steps.Add(Norm(t.Text));
                }
            }

            _out.WriteLine($"{file}: repeated step texts = {repeated}");
            Assert.Equal(0, repeated);
        }
    }

    // ---- the synthetic page: one message per shape ------------------------

    // equal join, whitespace-only difference, last step plus more, unrelated
    // text, the same text twice (rollup twice / once), a step once with the
    // rollup twice, a named-path rollup (equal and plus-more), a delivered
    // attachment, an attachment_error rollup plus more, an inline-image rollup,
    // a back-to-back tool run then a lone rollup, and steps still in flight.
    // The page's own cases field names rollups whose *text* is dropped. It
    // predates the rule that a dropped rollup keeps its picture and attachment
    // turns (a loss is worse than a picture shown twice), so the indices are
    // unchanged but index 52, the inline-image rollup, now still yields its
    // picture turn: asserted below.
    [Fact]
    public void ADroppedRollupOnTheSyntheticPageKeepsItsPictureAndLosesOnlyItsText()
    {
        var turns = OpenClawSessions.TurnsFromHistory(Load(SyntheticPage), "k");

        var picture = Assert.Single(turns, t => t.ImageBytes is not null);
        Assert.True(picture.FromRollup);
        Assert.Equal(1, turns.Count(t => t.Text == "S50 look"));
    }

    // The covered named-path rollup (idx 36) and clip rollup (idx 44) keep their
    // picture and attachment with no caption, so the step's words appear once;
    // the covered inline-image rollup (idx 52) does the same.
    [Fact]
    public void CoveredPictureRollupsOnTheSyntheticPageShowTheirPictureOnceAndTheirWordsOnce()
    {
        var turns = OpenClawSessions.TurnsFromHistory(Load(SyntheticPage), "k");

        var captionless = turns.Where(t => t.FromRollup && t.Text == ""
            && (t.ImageUrl is not null || t.ImageBytes is not null || t.Attachment is not null)).ToList();

        Assert.Equal(3, captionless.Count);
        Assert.Contains(captionless, t => t.ImageUrl is not null);
        Assert.Contains(captionless, t => t.Attachment is not null);
        Assert.Contains(captionless, t => t.ImageBytes is not null);
        Assert.Equal(1, turns.Count(t => t.Text.StartsWith("S34", StringComparison.Ordinal)));
        Assert.Equal(1, turns.Count(t => t.Text.StartsWith("S42 clip", StringComparison.Ordinal)));
    }

    // On the seven real pages no dropped rollup carries a picture or an
    // attachment, so dropping only the text is the same as dropping the whole
    // message there, and the counts above did not move.
    [Theory]
    [MemberData(nameof(Pages))]
    public void NoRollupDroppedOnARealPageCarriesAPictureOrAnAttachment(string file)
    {
        if (file == SyntheticPage) return;

        var all = Messages(file);
        foreach (var i in HistoryShapeTestHelpers.RollupsToDrop(Load(file)))
        {
            var types = all[i].GetProperty("content").ValueKind == JsonValueKind.Array
                ? all[i].GetProperty("content").EnumerateArray().Select(b => b.GetProperty("type").GetString()).ToList()
                : new List<string?>();

            Assert.All(types, ty => Assert.Equal("text", ty));
        }
    }

    [Fact]
    public void TheSyntheticPageDropsExactlyTheRollupsItsCasesSay()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "pages", SyntheticPage)));
        var cases = doc.RootElement.GetProperty("cases");
        var wantDrop = cases.GetProperty("drop").EnumerateArray().Select(e => e.GetInt32()).OrderBy(i => i).ToArray();
        var wantKeep = cases.GetProperty("keep").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        var drop = HistoryShapeTestHelpers.RollupsToDrop(Load(SyntheticPage));

        Assert.Equal(wantDrop, drop.OrderBy(i => i).ToArray());
        Assert.DoesNotContain(wantKeep, drop.Contains);
    }
}
