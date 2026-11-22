using System;
using System.IO;
using System.Linq;
using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-404: the parsers behind the run controls, each against rows cut from real
// output (a 2.1.330 Claude Code transcript, a Codex 0.164.0 rollout, a Grok
// 1.0.77 summary.json, the list_models and model/list answers, and a Codex
// 0.154.0 picker captured in a private tmux pane), each with its negatives.
public class RunReadingsTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "run", name));

    // Real 2.1.330 rows, trimmed to the fields the parser reads plus a few it
    // must ignore.
    private const string Assistant1 =
        """{"parentUuid":"a","isSidechain":false,"message":{"model":"claude-opus-5-5","id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"hi"}]},"type":"assistant","timestamp":"2026-11-21T21:21:06.722Z","effort":"medium","perTurnEffort":"medium","version":"2.1.330"}""";
    private const string Assistant2 =
        """{"isSidechain":false,"message":{"model":"claude-sonnet-5-5","role":"assistant","content":[]},"type":"assistant","timestamp":"2026-11-21T21:25:00.000Z","effort":"high","version":"2.1.330"}""";
    private const string SetModelRow =
        """{"type":"user","timestamp":"2026-11-21T21:30:00.000Z","message":{"role":"user","content":"<local-command-stdout>Set model to `Haiku 5.5` and saved as your default for new sessions</local-command-stdout>"}}""";
    private const string SetEffortRow =
        """{"type":"user","timestamp":"2026-11-21T21:31:00.000Z","message":{"role":"user","content":"<local-command-stdout>Set effort level to low (saved as your default for new sessions): Quick, straightforward implementation</local-command-stdout>"}}""";
    private const string RefusedRow =
        """{"type":"user","timestamp":"2026-11-21T21:32:00.000Z","message":{"role":"user","content":"<local-command-stdout>Model 'opus-4.6' not found</local-command-stdout>"}}""";

    // --- Claude transcript ---

    [Fact]
    public void TheLastAssistantRowIsTheReading()
    {
        var r = RunReadings.FromClaudeTranscript(new[] { Assistant1, Assistant2 });

        Assert.Equal(new RunReading("claude-sonnet-5-5", "high", DateTimeOffset.Parse("2026-11-21T21:25:00Z"), RunReadingSource.Transcript), r);
    }

    [Fact]
    public void ALaterSlashConfirmationSupersedesTheAssistantRow()
    {
        var r = RunReadings.FromClaudeTranscript(new[] { Assistant1, SetModelRow, SetEffortRow });

        Assert.Equal("Haiku 5.5", r.Model);
        Assert.Equal("low", r.Effort);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T21:31:00Z"), r.At);
    }

    [Fact]
    public void ARefusalChangesNothing()
    {
        var r = RunReadings.FromClaudeTranscript(new[] { Assistant1, RefusedRow });

        Assert.Equal("claude-opus-5-5", r.Model);
    }

    [Fact]
    public void SidechainSyntheticAndBrokenRowsAreSkipped()
    {
        var side = Assistant2.Replace("\"isSidechain\":false", "\"isSidechain\":true");
        var synthetic = """{"type":"assistant","timestamp":"2026-11-21T21:40:00Z","message":{"model":"<synthetic>","content":[]}}""";
        var listContent = """{"type":"user","timestamp":"2026-11-21T21:41:00Z","message":{"role":"user","content":[{"type":"text","text":"<local-command-stdout>Set model to `X`</local-command-stdout>"}]}}""";
        var noMessage = """{"type":"user","timestamp":"2026-11-21T21:41:00Z"}""";
        var noFields = """{"type":"assistant","message":{"content":[]}}""";

        var r = RunReadings.FromClaudeTranscript(new[] { Assistant1, side, synthetic, listContent, noMessage, noFields, "not json", "", "[1]", "{\"type\":\"system\"}" });

        Assert.Equal(new RunReading("claude-opus-5-5", "medium", DateTimeOffset.Parse("2026-11-21T21:21:06.722Z"), RunReadingSource.Transcript), r);
    }

    [Fact]
    public void ARowWithoutEffortKeepsTheEarlierOne()
    {
        var noEffort = """{"type":"assistant","timestamp":"2026-11-21T22:00:00Z","message":{"model":"claude-opus-5-5"}}""";

        var r = RunReadings.FromClaudeTranscript(new[] { Assistant2, noEffort });

        Assert.Equal(("claude-opus-5-5", "high"), (r.Model, r.Effort));
    }

    [Fact]
    public void NoRowsIsNoReading()
    {
        Assert.Equal(RunReading.None, RunReadings.FromClaudeTranscript(Array.Empty<string>()));
    }

    [Fact]
    public void ACloudEventsSourceIsCarried()
    {
        Assert.Equal(RunReadingSource.CloudEvents, RunReadings.FromClaudeTranscript(new[] { Assistant1 }, RunReadingSource.CloudEvents).Source);
    }

    [Theory]
    [InlineData("<local-command-stdout>Set model to `Opus 5.5 (default)` and saved as your default for new sessions</local-command-stdout>", "Opus 5.5", null)]
    [InlineData("<local-command-stdout>Kept model as `Opus 5.5 (1M context)`</local-command-stdout>", "Opus 5.5", null)]
    [InlineData("<local-command-stdout>Set effort level to medium (saved as your default for new sessions): Balanced approach</local-command-stdout>", null, "medium")]
    public void ConfirmationsAreRead(string content, string? model, string? effort)
    {
        var r = RunReadings.CommandConfirmation(content)!;
        Assert.Equal((model, effort), (r.Model, r.Effort));
    }

    [Theory]
    [InlineData("<local-command-stdout>Model 'x' not found</local-command-stdout>")]
    [InlineData("Set model to `Opus`")]
    public void OtherOutputIsNotAConfirmation(string content)
    {
        Assert.Null(RunReadings.CommandConfirmation(content));
    }

    // --- Claude footer ---

    [Fact]
    public void TheFooterEffortIsRead()
    {
        Assert.Equal("medium", RunReadings.ClaudeFooterEffort(Orbweaver.Tests.PaneFixtures.MacPromptStartup));
    }

    [Fact]
    public void TheLastFooterOnTheScreenWins()
    {
        Assert.Equal("xhigh", RunReadings.ClaudeFooterEffort("◐ low · /effort\n…\n◐ xhigh · /effort"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("> plain prompt\n? for shortcuts")]
    public void NoFooterIsNoEffort(string? pane)
    {
        Assert.Null(RunReadings.ClaudeFooterEffort(pane));
    }

    // --- Codex rollout ---

    private const string SessionMeta = """{"timestamp":"2026-11-03T15:40:01.437Z","type":"session_meta","payload":{"id":"t","cli_version":"0.164.0","cwd":"/x"}}""";
    private const string TurnContext = """{"timestamp":"2026-11-03T15:40:02.451Z","type":"turn_context","payload":{"model":"gpt-5.6-terra","collaboration_mode":{"mode":"default","settings":{"model":"gpt-5.6-terra","reasoning_effort":"medium"}},"effort":"medium"}}""";
    private const string SettingsApplied = """{"timestamp":"2026-11-03T15:45:00.000Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_id":"t","thread_settings":{"model":"gpt-6-astra","model_provider_id":"openai","reasoning_effort":"low"}}}""";

    [Fact]
    public void TheRolloutReadsTurnContextAndVersion()
    {
        var r = RunReadings.FromCodexRollout(new[] { SessionMeta, TurnContext });

        Assert.Equal(new RunReading("gpt-5.6-terra", "medium", DateTimeOffset.Parse("2026-11-03T15:40:02.451Z"), RunReadingSource.Rollout), r.Reading);
        Assert.Equal(new Version(0, 164, 0), r.CliVersion);
    }

    [Fact]
    public void ThreadSettingsAppliedIsAReadingWithNoTurn()
    {
        var r = RunReadings.FromCodexRollout(new[] { SessionMeta, TurnContext, SettingsApplied });

        Assert.Equal(("gpt-6-astra", "low"), (r.Reading.Model, r.Reading.Effort));
    }

    [Fact]
    public void RolloutRowsThatSayNothingAreSkipped()
    {
        var lines = new[]
        {
            "garbage", "", "[]",
            """{"type":"turn_context"}""",
            """{"type":"turn_context","payload":"x"}""",
            """{"type":"turn_context","payload":{"cwd":"/x"}}""",
            """{"type":"event_msg","payload":{"type":"thread_settings_applied"}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","thread_settings":{"model":"nope"}}}""",
            """{"type":"session_meta","payload":{"cli_version":"not a version"}}""",
        };

        var r = RunReadings.FromCodexRollout(lines);

        Assert.Equal(RunReading.None, r.Reading);
        Assert.Null(r.CliVersion);
    }

    // --- Codex footer and banner (captured at 0.154.0) ---

    private const string ReadyPane =
        "╭──────────────────────────────╮\n│ >_ OpenAI Codex (v0.154.0)   │\n│ model:     sonnet   /model to change │\n╰──────────────────────────────╯\n› Ask Codex to do anything\n  sonnet default · ~/.claude-board/jobs/x/tmp/cx\n";

    [Fact]
    public void TheCodexFooterIsTheReadingBeforeTheFirstTurn()
    {
        Assert.Equal(new RunReading("sonnet", null, null, RunReadingSource.PaneFooter), RunReadings.FromCodexFooter(ReadyPane));
        Assert.Equal(new RunReading("GPT-6.2-Sol", "high", null, RunReadingSource.PaneFooter), RunReadings.FromCodexFooter("  GPT-6.2-Sol high · ~/src"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("› Ask Codex to do anything")]
    public void NoCodexFooterIsNoReading(string? pane)
    {
        Assert.Equal(RunReading.None, RunReadings.FromCodexFooter(pane));
    }

    [Fact]
    public void TheBannerNamesTheVersion()
    {
        Assert.Equal(new Version(0, 154, 0), RunReadings.CodexBannerVersion(ReadyPane));
        Assert.Null(RunReadings.CodexBannerVersion("no banner"));
        Assert.Null(RunReadings.CodexBannerVersion(null));
    }

    // --- Codex picker (0.154.0, captured) ---

    [Fact]
    public void TheModelScreenListsIdsByDigit()
    {
        var s = RunReadings.ParseCodexPicker(Fixture("picker-models.txt"))!;

        Assert.Equal(RunReadings.CodexPickerStage.Models, s.Stage);
        Assert.Null(s.ForModel);
        Assert.Equal(new[] { "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" }, s.Rows.Select(r => r.Id));
        Assert.True(s.Rows[0].Highlighted);
        Assert.Equal(3, s.DigitFor("GPT-5.6-TERRA"));
        Assert.Null(s.DigitFor("gpt-7"));
    }

    [Fact]
    public void TheLevelScreenListsLevelIdsAndLeavesOutTheSubList()
    {
        var s = RunReadings.ParseCodexPicker(Fixture("picker-levels.txt"))!;

        Assert.Equal(RunReadings.CodexPickerStage.Levels, s.Stage);
        Assert.Equal("gpt-5.6-terra", s.ForModel);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh" }, s.Rows.Select(r => r.Id));
        Assert.True(s.Rows.Single(r => r.Id == "medium").Highlighted);
        Assert.Equal(4, s.DigitFor("xhigh"));
    }

    [Fact]
    public void OnlyRowsAfterTheLastHeaderCount()
    {
        var screen = Fixture("picker-models.txt") + "\n" + Fixture("picker-levels.txt");

        var s = RunReadings.ParseCodexPicker(screen)!;

        Assert.Equal(RunReadings.CodexPickerStage.Levels, s.Stage);
        Assert.Equal(4, s.Rows.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  Hooks need review\n› 1. Review hooks\n  2. Trust all and continue")]
    public void AnyOtherScreenIsNotThePicker(string? screen)
    {
        Assert.Null(RunReadings.ParseCodexPicker(screen));
    }

    [Theory]
    [InlineData("• Model changed to gpt-5.6-terra high", "gpt-5.6-terra", "high")]
    [InlineData("• Model changed to gpt-6-luna medium for this session only", "gpt-6-luna", "medium")]
    [InlineData("• Model changed to gpt-6-luna for this session only", "gpt-6-luna", null)]
    [InlineData("• Model changed to gpt-6-luna", "gpt-6-luna", null)]
    public void TheChangedLineConfirmsASet(string screen, string model, string? effort)
    {
        var r = RunReadings.FromCodexChanged("old\n" + screen);
        Assert.Equal((model, effort), (r.Model, r.Effort));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothing here")]
    public void NoChangedLineIsNoReading(string? screen)
    {
        Assert.Equal(RunReading.None, RunReadings.FromCodexChanged(screen));
    }

    [Theory]
    [InlineData("■ Model 'no-such-model-xyz' is unavailable. Use /model to see available models.", "Model 'no-such-model-xyz' is unavailable. Use /model to see available models.")]
    [InlineData("■ Reasoning level 'bogus' is unavailable for gpt-6.2-sol. Available: low, medium, high, xhigh, max, ultra.", "Reasoning level 'bogus' is unavailable for gpt-6.2-sol. Available: low, medium, high, xhigh, max, ultra.")]
    [InlineData("■ Unexpected argument 'Terra'", "Unexpected argument 'Terra'")]
    public void TheTypedFormsRefusalsAreRead(string screen, string why)
    {
        Assert.Equal(why, RunReadings.CodexRefusal(screen));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Model changed to x")]
    public void NoRefusalIsNull(string? screen)
    {
        Assert.Null(RunReadings.CodexRefusal(screen));
    }

    // --- Grok summary.json (1.0.77, Windows box) ---

    [Fact]
    public void TheGrokSummaryIsRead()
    {
        const string json = """{"info":{"id":"s"},"updated_at":"2026-11-09T05:39:53.988198100Z","current_model_id":"grok-4.7","reasoning_effort":"high","agent_name":"grok-build-plan"}""";

        var r = RunReadings.FromGrokSummary(json);

        Assert.Equal(("grok-4.7", "high", RunReadingSource.GrokSummary), (r.Model, r.Effort, r.Source));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 5, 39, 53, TimeSpan.Zero), r.At!.Value.AddTicks(-(r.At.Value.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public void ASummaryWithoutUpdatedAtFallsBackToLastActive()
    {
        var r = RunReadings.FromGrokSummary("""{"last_active_at":"2026-11-09T05:00:00Z","current_model_id":"grok-4.7"}""");

        Assert.Equal(DateTimeOffset.Parse("2026-11-09T05:00:00Z"), r.At);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("""{"session_summary":"x"}""")]
    public void AGrokSummaryWithoutTheFieldsIsNoReading(string? json)
    {
        Assert.Equal(RunReading.None, RunReadings.FromGrokSummary(json));
    }

    // --- Catalogues ---

    [Fact]
    public void ClaudesListModelsAnswerIsACatalogue()
    {
        var c = RunReadings.ClaudeModelList(Fixture("claude-list-models.json"));

        Assert.Equal(14, c.Models.Count);
        Assert.Equal(("default", "Default (recommended)", "claude-opus-5-5"), (c.Models[0].Id, c.Models[0].Label, c.Models[0].ResolvedId));
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, c.Models[0].EffortLevels);
        Assert.Null(c.Find("claude-haiku-4-5-20251001")!.EffortLevels);
        Assert.Equal(new[] { "low", "medium", "high", "max" }, c.Find("claude-opus-4-6")!.EffortLevels);
        // The transcript's resolved id reads as the named model, not "Default".
        Assert.Equal("opus", c.Find("claude-opus-5-5")!.Id);
        Assert.Equal("haiku", c.Find("Haiku 5.5")!.Id);
    }

    [Fact]
    public void ABareModelsObjectIsAcceptedToo()
    {
        var c = RunReadings.ClaudeModelList("""{"models":[{"value":"x","supportsEffort":true,"supportedEffortLevels":[]},{"displayName":"no value"},3]}""");

        Assert.Equal(new[] { "x" }, c.Models.Select(m => m.Id));
        Assert.Equal("x", c.Models[0].Label);
        Assert.Null(c.Models[0].EffortLevels);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    [InlineData("[]")]
    [InlineData("""{"type":"control_response","response":{"subtype":"error","error":"x"}}""")]
    [InlineData("""{"models":"x"}""")]
    public void ABadListModelsAnswerIsAnEmptyCatalogue(string? json)
    {
        Assert.Empty(RunReadings.ClaudeModelList(json).Models);
    }

    [Fact]
    public void CodexsModelListIsACatalogue()
    {
        var c = RunReadings.CodexModelList(Fixture("codex-model-list.json"));

        Assert.Equal(new[] { "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" }, c.Models.Select(m => m.Id));
        Assert.Equal("GPT-6-Astra", c.Models[0].Label);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max", "ultra" }, c.Models[0].EffortLevels);
        // Luna lacks ultra, as measured at 0.167.0.
        Assert.DoesNotContain("ultra", c.Find("gpt-5.6-luna")!.EffortLevels!);
    }

    [Fact]
    public void HiddenCodexModelsAndModelsWithoutLevelsAreHandled()
    {
        var c = RunReadings.CodexModelList("""{"data":[{"id":"h","hidden":true},{"id":"plain"},{"id":"odd","supportedReasoningEfforts":[1,{"x":1}]},{"hidden":false},2]}""");

        Assert.Equal(new[] { "plain", "odd" }, c.Models.Select(m => m.Id));
        Assert.All(c.Models, m => Assert.Null(m.EffortLevels));
        Assert.Equal("plain", c.Models[0].Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("x")]
    [InlineData("""{"error":{"code":-1}}""")]
    [InlineData("""{"result":{"data":{}}}""")]
    public void ABadModelListIsAnEmptyCatalogue(string? json)
    {
        Assert.Empty(RunReadings.CodexModelList(json).Models);
    }

    [Fact]
    public void GrokSModelsCacheIsACatalogueLowestLevelFirst()
    {
        var c = RunReadings.GrokModelsCache(Fixture("grok-models-cache.json"));

        Assert.Equal(new[] { "grok-4.6", "grok-4.5" }, c.Models.Select(m => m.Id));
        Assert.Equal("Grok 4.6", c.Models[0].Label);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh" }, c.Models[0].EffortLevels);
    }

    [Fact]
    public void GrokModelsWithoutReasoningOrHiddenAreHandled()
    {
        var c = RunReadings.GrokModelsCache("""{"models":{"a":{"info":{"name":"A","supports_reasoning_effort":false,"reasoning_efforts":[{"id":"low"}]}},"h":{"info":{"hidden":true}},"b":{"supports_reasoning_effort":true,"reasoning_efforts":[{"id":"weird"},{"id":"low"},3,{"x":1}]},"n":5}}""");

        Assert.Equal(new[] { "a", "b" }, c.Models.Select(m => m.Id));
        Assert.Null(c.Models[0].EffortLevels);
        Assert.Equal(new[] { "low", "weird" }, c.Models[1].EffortLevels);
        Assert.Equal("b", c.Models[1].Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"models":[]}""")]
    public void ABadModelsCacheIsAnEmptyCatalogue(string? json)
    {
        Assert.Empty(RunReadings.GrokModelsCache(json).Models);
    }

    [Theory]
    [InlineData("low", 2)]
    [InlineData("ULTRA", 7)]
    [InlineData("other", 8)]
    public void TheLadderRanksLevels(string level, int rank)
    {
        Assert.Equal(rank, RunReadings.Rank(level));
    }
}
