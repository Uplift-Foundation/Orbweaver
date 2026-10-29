using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 4: what a native session is running on, read off its row.
//
// The rows follow the structure measured on openclaw 2026.10.5 (model ids,
// thinking lists and booleans only, so nothing here is personal): a native row
// reads thinkingLevel null beside thinkingDefault "adaptive", and fastMode null
// beside effectiveFastMode false.
public class OpenClawRunStateTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string NativeRow = """
        {"key":"agent:main:dashboard:u","model":"claude-opus-4-6","modelProvider":"anthropic",
         "thinkingLevel":null,"thinkingDefault":"adaptive",
         "thinkingLevels":[{"id":"off","label":"off"},{"id":"adaptive","label":"adaptive"},{"id":"high","label":"high"}],
         "fastMode":null,"effectiveFastMode":false,"effectiveFastModeSource":"default",
         "runtimeSelectionLocked":false}
        """;

    [Fact]
    public void ANativeRowIsReadFieldByField()
    {
        var state = OpenClawRunState.From(Json(NativeRow))!;

        Assert.Equal("claude-opus-4-6", state.Model);
        Assert.Equal("anthropic", state.ModelProvider);
        Assert.Equal("adaptive", state.ThinkingDefault);
        Assert.Equal(new[] { "off", "adaptive", "high" }, state.ThinkingLevels.Select(o => o.Id));
        Assert.False(state.EffectiveFastMode);
        Assert.False(state.SelectionLocked);
    }

    // Null means "nobody chose", which is not the same fact as choosing the
    // default: it stays null and is never replaced by it.
    [Fact]
    public void ANullThinkingLevelStaysNullBesideItsDefault()
    {
        var state = OpenClawRunState.From(Json(NativeRow))!;

        Assert.Null(state.ThinkingLevel);
        Assert.Equal("adaptive", state.EffectiveThinking);
    }

    [Fact]
    public void ANullFastModeIsNotFalse()
    {
        var unset = OpenClawRunState.From(Json(NativeRow))!;
        var off = OpenClawRunState.From(Json("""{"model":"m","fastMode":false}"""))!;
        var on = OpenClawRunState.From(Json("""{"model":"m","fastMode":true,"effectiveFastMode":true}"""))!;

        Assert.Null(unset.FastMode);
        Assert.False(off.FastMode);
        Assert.True(on.FastMode);
        Assert.True(on.EffectiveFastMode);
    }

    [Fact]
    public void AChosenLevelWinsOverTheDefault()
    {
        var state = OpenClawRunState.From(Json(
            """{"model":"m","thinkingLevel":"high","thinkingDefault":"adaptive","thinkingLevels":[{"id":"high","label":"High effort"}]}"""))!;

        Assert.Equal("high", state.EffectiveThinking);
        Assert.Equal("High effort", state.EffectiveThinkingLabel);
    }

    // A level the model's own list does not carry is shown as itself.
    [Fact]
    public void ALevelMissingFromTheListIsShownAsItself()
    {
        var state = OpenClawRunState.From(Json("""{"model":"m","thinkingLevel":"xhigh"}"""))!;

        Assert.Equal("xhigh", state.EffectiveThinkingLabel);
    }

    [Fact]
    public void NoLevelAtAllHasNoLabel()
    {
        var state = OpenClawRunState.From(Json("""{"model":"m"}"""))!;

        Assert.Null(state.EffectiveThinking);
        Assert.Null(state.EffectiveThinkingLabel);
    }

    [Fact]
    public void NoModelMeansNoRunState()
    {
        Assert.Null(OpenClawRunState.From(Json("""{"key":"k"}""")));
        Assert.Null(OpenClawRunState.From(Json("""{"model":""}""")));
        Assert.Null(OpenClawRunState.From(Json("""{"model":7}""")));
        Assert.Null(OpenClawRunState.From(Json("[]")));
    }

    // The gateway's objects win; the older list of bare strings is the fallback,
    // with the id as its label.
    [Fact]
    public void ObjectsArePreferredAndBareStringsAreTheFallback()
    {
        var both = OpenClawRunState.From(Json(
            """{"model":"m","thinkingLevels":[{"id":"low","label":"on"}],"thinkingOptions":["off","low"]}"""))!;
        var fallback = OpenClawRunState.From(Json(
            """{"model":"m","thinkingOptions":["off","ultra"]}"""))!;

        Assert.Equal(new[] { ("low", "on") }, both.ThinkingLevels.Select(o => (o.Id, o.Label)));
        Assert.Equal(new[] { ("off", "off"), ("ultra", "ultra") }, fallback.ThinkingLevels.Select(o => (o.Id, o.Label)));
    }

    // One odd entry does not take the list with it.
    [Fact]
    public void BadEntriesAreSkippedNotFatal()
    {
        var state = OpenClawRunState.From(Json(
            """{"model":"m","thinkingLevels":[7,{"label":"no id"},"",{"id":"low"},{"id":"high","label":9}]}"""))!;

        Assert.Equal(new[] { ("low", "low"), ("high", "high") }, state.ThinkingLevels.Select(o => (o.Id, o.Label)));
    }

    // A field of the wrong type costs that field and nothing else.
    [Fact]
    public void WrongTypedFieldsCostOnlyThatField()
    {
        var state = OpenClawRunState.From(Json(
            """
            {"model":"m","modelProvider":5,"thinkingLevel":false,"thinkingDefault":[],"thinkingLevels":"x",
             "fastMode":"yes","effectiveFastMode":"yes","runtimeSelectionLocked":"yes"}
            """))!;

        Assert.Equal("m", state.Model);
        Assert.Null(state.ModelProvider);
        Assert.Null(state.ThinkingLevel);
        Assert.Null(state.ThinkingDefault);
        Assert.Empty(state.ThinkingLevels);
        Assert.Null(state.FastMode);
        Assert.False(state.EffectiveFastMode);
        Assert.False(state.SelectionLocked);
    }

    [Fact]
    public void ARuntimeLockedSelectionIsRead()
    {
        var state = OpenClawRunState.From(Json("""{"model":"m","runtimeSelectionLocked":true}"""))!;

        Assert.True(state.SelectionLocked);
    }

    // A level list given as something other than an array is just absent.
    [Fact]
    public void LevelsOnANonObjectOwnerAreEmpty()
    {
        Assert.Empty(OpenClawRunState.LevelsOf(Json("[]"), "thinkingLevels"));
    }
}
