using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270 stage 3b: TextsToDrop, the rule that removes the repeat the stage-3
// coverage rule leaves. A rollup that holds every step plus more is kept whole
// (so nothing is lost) and, under TextsToDrop, its covered steps are dropped
// instead of drawn a second time. Pure functions over JSON; the wiring into
// TurnsFromHistory, PrependHistory and AppendNewTail is a later change.
//
// The expected sets below were produced by an independent Python prototype of
// the rule over the same files and cross-checked against the shapes recorded
// when the pages were captured: 2 rollups and 5 steps dropped across 3 spans,
// the disjoint rollup and its 8 steps kept, nothing touched on the
// openclaw-transcript and api-less pages.
public class OpenClawTextsToDropTests
{
    private static string PagePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "pages", file);

    private static JsonElement Load(string file)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(PagePath(file)));
        return doc.RootElement.TryGetProperty("messages", out var m) ? m.Clone() : doc.RootElement.Clone();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static int[] Sorted(HashSet<int> set) => set.OrderBy(i => i).ToArray();

    [Theory]
    [InlineData("page-channel-disjoint-and-equal-join.json", new[] { 39 })]
    [InlineData("page-direct-plus-more.json", new[] { 37 })]
    [InlineData("page-main-equal-join-and-plus-more.json", new[] { 26, 35 })]
    [InlineData("page-main-plus-more.json", new[] { 0, 3, 7 })]
    [InlineData("page-mixed-runtimes-no-rollup.json", new int[0])]
    [InlineData("page-transcript-runtime-channel.json", new int[0])]
    [InlineData("page-transcript-runtime-lone-rollups.json", new int[0])]
    public void TextsToDropOnTheRealPagesDropsExactlyTheseMessages(string file, int[] expected) =>
        Assert.Equal(expected, Sorted(OpenClawHistoryShape.TextsToDrop(Load(file))));

    [Fact]
    public void TextsToDropOnTheSyntheticPageAddsTheStepsOfTheKeptPlusMoreRollups()
    {
        var drop = OpenClawHistoryShape.TextsToDrop(Load("page-synthetic-shapes.json"));

        // The rollups the coverage rule drops are unchanged (the page's own cases
        // field), plus four covered steps (13, 30, 38, 46) beside rollups it keeps.
        Assert.Equal(new[] { 4, 9, 13, 23, 28, 30, 36, 38, 44, 46, 52, 57 }, Sorted(drop));
    }

    // Rollups TextsToDrop drops are exactly the ones RollupsToDrop drops: it only
    // adds steps.
    [Theory]
    [InlineData("page-channel-disjoint-and-equal-join.json")]
    [InlineData("page-direct-plus-more.json")]
    [InlineData("page-main-equal-join-and-plus-more.json")]
    [InlineData("page-main-plus-more.json")]
    [InlineData("page-synthetic-shapes.json")]
    [InlineData("page-mixed-runtimes-no-rollup.json")]
    public void TextsToDropDropsTheSameRollupsAsRollupsToDropAndOnlyAddsSteps(string file)
    {
        var all = Load(file).EnumerateArray().ToList();
        var texts = OpenClawHistoryShape.TextsToDrop(Load(file));

        Assert.Equal(
            Sorted(HistoryShapeTestHelpers.RollupsToDrop(Load(file))),
            Sorted(texts.Where(i => OpenClawHistoryShape.IsRollup(all[i])).ToHashSet()));
    }

    private static string Collapse(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string TextOf(JsonElement m)
    {
        if (!m.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n\n", c.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out _))
            .Select(b => b.GetProperty("text").GetString()));
    }

    // Never lose text, checked independently of StepsCoveredBy: every dropped step
    // sits in a span closed by a rollup that is not itself dropped, and that
    // rollup's collapsed text contains the step's collapsed text.
    [Theory]
    [InlineData("page-channel-disjoint-and-equal-join.json")]
    [InlineData("page-direct-plus-more.json")]
    [InlineData("page-main-equal-join-and-plus-more.json")]
    [InlineData("page-main-plus-more.json")]
    [InlineData("page-synthetic-shapes.json")]
    public void EveryDroppedStepIsContainedInAKeptRollupOfItsSpan(string file)
    {
        var all = Load(file).EnumerateArray().ToList();
        var drop = OpenClawHistoryShape.TextsToDrop(Load(file));

        foreach (var i in drop.Where(i => !OpenClawHistoryShape.IsRollup(all[i])))
        {
            // The first rollup after the step, with no user turn in between.
            var j = i + 1;
            while (j < all.Count && all[j].GetProperty("role").GetString() != "user" && !OpenClawHistoryShape.IsRollup(all[j])) j++;

            Assert.True(j < all.Count && OpenClawHistoryShape.IsRollup(all[j]), $"{file}: step {i} has no closing rollup");
            Assert.DoesNotContain(j, drop);
            Assert.Contains(Collapse(TextOf(all[i])), Collapse(TextOf(all[j])));
        }
    }

    // After TextsToDrop there is nothing left to repeat on the real pages: no
    // kept rollup contains the text of a step that is still shown.
    [Theory]
    [InlineData("page-channel-disjoint-and-equal-join.json")]
    [InlineData("page-direct-plus-more.json")]
    [InlineData("page-main-equal-join-and-plus-more.json")]
    [InlineData("page-main-plus-more.json")]
    [InlineData("page-mixed-runtimes-no-rollup.json")]
    [InlineData("page-transcript-runtime-channel.json")]
    [InlineData("page-transcript-runtime-lone-rollups.json")]
    public void NoKeptRollupRepeatsAShownStepOnTheRealPages(string file)
    {
        var all = Load(file).EnumerateArray().ToList();
        var drop = OpenClawHistoryShape.TextsToDrop(Load(file));
        var repeats = 0;

        for (var r = 0; r < all.Count; r++)
        {
            if (!OpenClawHistoryShape.IsRollup(all[r]) || drop.Contains(r)) continue;

            var rollup = Collapse(TextOf(all[r]));
            for (var j = r - 1; j >= 0; j--)
            {
                var role = all[j].GetProperty("role").GetString();
                if (role == "user" || OpenClawHistoryShape.IsRollup(all[j])) break;
                var step = Collapse(TextOf(all[j]));
                if (role == "assistant" && step.Length > 0 && !drop.Contains(j) && rollup.Contains(step, StringComparison.Ordinal))
                    repeats++;
            }
        }

        Assert.Equal(0, repeats);
    }

    [Fact]
    public void ThePhaseZeroToolRunDropsItsRollupAndKeepsTheLoneOne()
    {
        var page = Json(OpenClawNativeRunFixtures.ToolRunPage);

        Assert.Equal(new[] { 4 }, Sorted(OpenClawHistoryShape.TextsToDrop(page)));
    }

    // ---- the arms of the rule, on small constructed pages ---------------------

    private const string User = """{"role":"user","content":"go"}""";
    private const string ToolStep = """{"role":"assistant","api":"anthropic-messages","content":[{"type":"toolcall","id":"a","name":"exec","arguments":{}}]}""";

    private static string Step(string text) =>
        "{\"role\":\"assistant\",\"api\":\"anthropic-messages\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}]}";

    private static string Rollup(string text) =>
        "{\"role\":\"assistant\",\"api\":\"cli\",\"idempotencyKey\":\"cli-assistant:r\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}]}";

    private static int[] Drop(params string[] messages) =>
        Sorted(OpenClawHistoryShape.TextsToDrop(Json("[" + string.Join(",", messages) + "]")));

    [Fact]
    public void ALoneRollupWithNoStepsIsKept() => Assert.Empty(Drop(User, Rollup("only")));

    [Fact]
    public void AToolOnlySpanKeepsItsRollup() => Assert.Empty(Drop(User, ToolStep, Rollup("answer")));

    [Fact]
    public void AnEqualJoinDropsTheRollupAndKeepsTheSteps() =>
        Assert.Equal(new[] { 3 }, Drop(User, Step("a b"), Step("c d"), Rollup("a b\n\nc d")));

    [Fact]
    public void ARollupHoldingEveryStepPlusMoreIsKeptAndItsStepsAreDropped() =>
        Assert.Equal(new[] { 1, 2 }, Drop(User, Step("a b"), Step("c d"), Rollup("a b\n\nc d\n\nand the answer")));

    [Fact]
    public void OnlyTheStepsTheRollupContainsAreDropped() =>
        Assert.Equal(new[] { 2 }, Drop(User, Step("unrelated"), Step("c d"), Rollup("c d and the answer")));

    [Fact]
    public void ARollupContainingNoStepKeepsEverything() =>
        Assert.Empty(Drop(User, Step("a b"), Rollup("something else entirely")));

    [Fact]
    public void ADroppedStepsToolCallsAreNotInTheSet()
    {
        // The step message carries its text and a toolcall; the whole message
        // index is in the set, and the caller emits the tool lines of every
        // message, dropped or not (TurnsFromHistory, when it is wired).
        var withTool = "{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"a b\"},{\"type\":\"toolcall\",\"id\":\"x\",\"name\":\"exec\",\"arguments\":{}}]}";

        Assert.Equal(new[] { 1 }, Drop(User, withTool, Rollup("a b and more")));
    }

    [Fact]
    public void StepsAfterTheLastRollupAreARunInFlightAndAreKept() =>
        Assert.Equal(new[] { 1 }, Drop(User, Step("a"), Rollup("a and more"), Step("later")));

    [Fact]
    public void BackToBackRunsAreJudgedApart() =>
        Assert.Equal(new[] { 1, 3 }, Drop(User, Step("a"), Rollup("a more"), Step("b"), Rollup("b more")));

    [Fact]
    public void AUserTurnEndsTheSpan() =>
        Assert.Empty(Drop(User, Step("a"), User, Rollup("a and more")));

    [Fact]
    public void ANonArrayOrEmptyPageDropsNothing()
    {
        Assert.Empty(OpenClawHistoryShape.TextsToDrop(Json("{}")));
        Assert.Empty(OpenClawHistoryShape.TextsToDrop(Json("[]")));
    }

    [Fact]
    public void StepsCoveredByReturnsThePositionsContainedInTheRollup()
    {
        Assert.Equal(new[] { 0, 2 },
            OpenClawHistoryShape.StepsCoveredBy("alpha  beta\ngamma", new[] { "alpha beta", "delta", "gamma" }).ToArray());
        Assert.Empty(OpenClawHistoryShape.StepsCoveredBy("alpha", new[] { "   ", "" }));
        Assert.Empty(OpenClawHistoryShape.StepsCoveredBy("alpha", new string[0]));

        // An occurrence covers one step: the same words twice are covered only if
        // the rollup says them twice.
        Assert.Equal(new[] { 0 }, OpenClawHistoryShape.StepsCoveredBy("same and more", new[] { "same", "same" }).ToArray());
        Assert.Equal(new[] { 0, 1 }, OpenClawHistoryShape.StepsCoveredBy("same same more", new[] { "same", "same" }).ToArray());
    }

    // ---- the page boundary: what the raw messages say ----------------------------

    [Fact]
    public void ACutFirstRunStillDropsTheStepsItsKeptRollupContains()
    {
        // The older page is not visible, but the rollup on this page holds the step's
        // words, so dropping the step needs nothing from it.
        var newer = Json("[" + Step("c d") + "," + Rollup("a b\n\nc d\n\nmore") + "]");

        Assert.Equal(new[] { 0 }, Sorted(OpenClawHistoryShape.TextsToDrop(newer)));
    }

    [Fact]
    public void OpenTailStepsAreTheTextStepsOfAnUnclosedLastRun()
    {
        Assert.Equal(new[] { 1, 3 }, OpenClawHistoryShape.OpenTailSteps(
            Json("[" + User + "," + Step("a") + "," + ToolStep + "," + Step("b") + "]")).ToArray());
        Assert.Empty(OpenClawHistoryShape.OpenTailSteps(Json("[" + User + "," + Step("a") + "," + Rollup("a") + "]")));
        Assert.Empty(OpenClawHistoryShape.OpenTailSteps(Json("[" + Step("a") + "," + User + "]")));
        Assert.Empty(OpenClawHistoryShape.OpenTailSteps(Json("{}")));
    }

    [Fact]
    public void HeadRollupIndexIsTheFirstRollupWithNoUserMessageBeforeIt()
    {
        Assert.Equal(3, OpenClawHistoryShape.HeadRollupIndex(Json("[" + Step("a") + "," + ToolStep + "," + Step("b") + "," + Rollup("a b") + "]")));
        Assert.Equal(-1, OpenClawHistoryShape.HeadRollupIndex(Json("[" + Step("a") + "," + User + "," + Rollup("a") + "]")));
        Assert.Equal(-1, OpenClawHistoryShape.HeadRollupIndex(Json("[" + Step("a") + "," + Step("b") + "]")));
        Assert.Equal(-1, OpenClawHistoryShape.HeadRollupIndex(Json("{}")));
    }

    [Fact]
    public void ResolveSpanHasTheThreeOutcomes()
    {
        Assert.False(OpenClawHistoryShape.ResolveSpan(new string[0], "x").DropRollup);
        Assert.True(OpenClawHistoryShape.ResolveSpan(new[] { "a", "b" }, "a b").DropRollup);

        var plus = OpenClawHistoryShape.ResolveSpan(new[] { "a", "z" }, "a and more");
        Assert.False(plus.DropRollup);
        Assert.Equal(new[] { 0 }, plus.DropSteps.ToArray());
    }
}
