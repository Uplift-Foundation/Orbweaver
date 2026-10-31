using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 4: the gateway's model catalogue, and the thinking levels each
// model offers. The fixture is a reduced copy of the structure of the 20-model
// capture from openclaw 2026.10.5 (ids, names and level lists only), keeping the
// cases that matter: an id the refusal text does not list, an id whose label
// differs from it, a model offering only off and ultra, and one not available.
public class OpenClawModelCatalogTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string Models = """
        [
         {"id":"claude-haiku-4-5","name":"Claude Haiku 4.5","provider":"anthropic","thinkingDefault":"medium","available":true,
          "thinkingLevels":[{"id":"off","label":"off"},{"id":"low","label":"low"},{"id":"medium","label":"medium"},{"id":"ultra","label":"ultra"}]},
         {"id":"claude-opus-4-7","name":"Claude Opus 4.7","provider":"anthropic","thinkingDefault":"off","available":true,
          "thinkingLevels":[{"id":"off","label":"off"},{"id":"high","label":"high"},{"id":"xhigh","label":"xhigh"},{"id":"ultra","label":"ultra"}]},
         {"id":"local-model","provider":"ollama","thinkingDefault":"off","available":true,
          "thinkingLevels":[{"id":"off","label":"off"},{"id":"ultra","label":"ultra"}]},
         {"id":"qwen/qwen3.6-plus","name":"Qwen: Qwen3.6 Plus","provider":"openrouter","thinkingDefault":"low","available":true,
          "thinkingLevels":[{"id":"off","label":"off"},{"id":"low","label":"on"},{"id":"ultra","label":"ultra"}]},
         {"id":"gpt-5.6-sol","name":"GPT-5.6-Sol","provider":"openai","available":false},
         {"name":"no id, so skipped"},
         {"id":"no-availability-field","name":"Unvouched"}
        ]
        """;

    private static IReadOnlyList<OpenClawModel> Parsed() => OpenClawModelCatalog.Parse(Json(Models));

    [Fact]
    public void EveryEntryWithAnIdIsParsed() =>
        Assert.Equal(6, Parsed().Count);

    // The unavailable model and the one the gateway never vouched for are not
    // offered; the rest keep the gateway's order.
    [Fact]
    public void OnlyAvailableModelsAreOffered()
    {
        var offered = OpenClawModelCatalog.Offered(Parsed());

        Assert.Equal(new[] { "claude-haiku-4-5", "claude-opus-4-7", "local-model", "qwen/qwen3.6-plus" },
            offered.Select(m => m.Id));
    }

    // The set comes from the model, not from the refusal text, which does not
    // list xhigh.
    [Fact]
    public void OneModelOffersALevelTheRefusalTextDoesNotList() =>
        Assert.Contains(Parsed().Single(m => m.Id == "claude-opus-4-7").ThinkingLevels, o => o.Id == "xhigh");

    // Id and label differ, so the picker has to send one and show the other.
    [Fact]
    public void AnIdCanHaveADifferentLabel()
    {
        var level = Parsed().Single(m => m.Id == "qwen/qwen3.6-plus").ThinkingLevels.Single(o => o.Id == "low");

        Assert.Equal("on", level.Label);
    }

    [Fact]
    public void ALocalModelOffersExactlyOffAndUltra() =>
        Assert.Equal(new[] { "off", "ultra" },
            Parsed().Single(m => m.Id == "local-model").ThinkingLevels.Select(o => o.Id));

    [Fact]
    public void TheProviderIsKept() =>
        Assert.Equal("openrouter", Parsed().Single(m => m.Id == "qwen/qwen3.6-plus").Provider);

    [Fact]
    public void TheNameFallsBackToTheId() =>
        Assert.Equal("local-model", Parsed().Single(m => m.Id == "local-model").Name);

    [Fact]
    public void AModelWithNoLevelsGetsAnEmptyList()
    {
        var sol = Parsed().Single(m => m.Id == "gpt-5.6-sol");

        Assert.Empty(sol.ThinkingLevels);
        Assert.Null(sol.ThinkingDefault);
        Assert.False(sol.Available);
    }

    [Fact]
    public void ABareArrayAndAWrappedOneParseTheSame()
    {
        var wrapped = OpenClawModelCatalog.Parse(Json("{\"models\":" + Models + "}"));

        Assert.Equal(Parsed().Select(m => m.Id), wrapped.Select(m => m.Id));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"models":5}""")]
    [InlineData("null")]
    [InlineData("7")]
    public void ASurprisingShapeGivesAnEmptyCatalogue(string json) =>
        Assert.Empty(OpenClawModelCatalog.Parse(Json(json)));
}
