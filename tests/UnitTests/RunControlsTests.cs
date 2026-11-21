using System;
using System.Collections.Generic;
using System.Linq;
using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-404: the pure core behind the model dropdown and effort dial. One case per
// outcome the panel can draw, and per route the plan names (docs/plans/
// model-effort-controls-plan.md, 4.1).
public class RunControlsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Five = { "low", "medium", "high", "xhigh", "max" };

    private static readonly RunCatalogue Claude = new(new[]
    {
        new RunModel("opus", "Opus 5.5", "claude-opus-5-5", Five),
        new RunModel("sonnet", "Sonnet 5.5", "claude-sonnet-5-5", Five),
        new RunModel("haiku", "Haiku 4.5", "claude-haiku-4-5", null),
    });

    private static readonly RunRouteDecision Control = new(RunRoute.Control, null, false);
    private static readonly RunPending[] NonePending = Array.Empty<RunPending>();

    // --- RunReading.Merge ---

    [Fact]
    public void ANewerReadingWinsPartByPart()
    {
        var older = new RunReading("opus", "high", T0, RunReadingSource.Transcript);
        var newer = new RunReading("sonnet", null, T0.AddSeconds(5), RunReadingSource.PaneFooter);

        var merged = RunReading.Merge(older, newer);

        Assert.Equal(new RunReading("sonnet", "high", T0.AddSeconds(5), RunReadingSource.PaneFooter), merged);
    }

    [Fact]
    public void ArgumentOrderDoesNotDecideWhichIsNewer()
    {
        var older = new RunReading("opus", "high", T0, RunReadingSource.Transcript);
        var newer = new RunReading("sonnet", "low", T0.AddSeconds(5), RunReadingSource.Transcript);

        Assert.Equal(newer, RunReading.Merge(newer, older));
    }

    [Fact]
    public void AnUndatedReadingLosesToADatedOne()
    {
        var dated = new RunReading("opus", "high", T0, RunReadingSource.Transcript);
        var undated = new RunReading("sonnet", "low", null, RunReadingSource.PaneFooter);

        Assert.Equal(dated, RunReading.Merge(dated, undated));
    }

    [Fact]
    public void BetweenTwoUndatedReadingsTheSecondWins()
    {
        var a = new RunReading("opus", null, null, RunReadingSource.PaneFooter);
        var b = new RunReading(null, "low", null, RunReadingSource.PaneFooter);

        Assert.Equal(new RunReading("opus", "low", null, RunReadingSource.PaneFooter), RunReading.Merge(a, b));
    }

    [Fact]
    public void AnEmptyNewerReadingKeepsTheOlderSource()
    {
        var older = new RunReading("opus", "high", T0, RunReadingSource.Transcript);
        var empty = new RunReading(null, null, T0.AddSeconds(1), RunReadingSource.PaneFooter);

        Assert.Equal(RunReadingSource.Transcript, RunReading.Merge(older, empty).Source);
    }

    // --- RunCatalogue.Find ---

    [Theory]
    [InlineData("opus")]
    [InlineData("OPUS")]
    [InlineData("claude-opus-5-5")]
    [InlineData("Opus 5.5")]
    public void AModelIsFoundByAliasResolvedIdOrLabel(string name)
    {
        Assert.Equal("opus", Claude.Find(name)!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("gpt-6-luna")]
    public void NothingIsFoundForNoNameOrAStranger(string? name)
    {
        Assert.Null(Claude.Find(name));
    }

    // --- RunRoutes.For ---

    [Fact]
    public void ClaudeWithABridgeAndTheSettingOnUsesControl()
    {
        Assert.Equal(new RunRouteDecision(RunRoute.Control, null, false),
            RunRoutes.For(RunCli.Claude, hasPane: true, bridgeId: "b1", bridgeSlashOn: true));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("b1", false)]
    public void ClaudeWithAPaneButNoUsableBridgeTypesAndWarns(string? bridge, bool settingOn)
    {
        var route = RunRoutes.For(RunCli.Claude, hasPane: true, bridgeId: bridge, bridgeSlashOn: settingOn);

        Assert.Equal(new RunRouteDecision(RunRoute.Typed, null, true), route);
        Assert.Equal(RunText.ClaudeTypedDefault, RunRoutes.DefaultWarning(RunCli.Claude, route));
    }

    [Fact]
    public void ClaudeWithNoPaneAndNoBridgeIsDisabledWithTheGivenSentence()
    {
        var route = RunRoutes.For(RunCli.Claude, false, null, true, noBridgeReason: SlashRouting.DesktopNoBridgeReason);

        Assert.Equal(RunRouteDecision.Unavailable(SlashRouting.DesktopNoBridgeReason), route);
    }

    [Fact]
    public void ClaudeWithNoPaneAndNoReasonFallsBackToThePeerSocketSentence()
    {
        Assert.Equal(SlashRouting.PeerSocketReason, RunRoutes.For(RunCli.Claude, false, null, true).DisabledReason);
    }

    [Fact]
    public void ACloudSessionUsesControl()
    {
        Assert.Equal(RunRoute.Control, RunRoutes.For(RunCli.ClaudeCloud, false, null, false).Route);
    }

    [Theory]
    [InlineData("0.167.0", false)]
    [InlineData("0.164.0", false)]
    [InlineData("0.163.0", true)]
    [InlineData("0.154.0", true)]
    [InlineData(null, false)]
    public void CodexTypesAndWarnsOnlyBeforeTheArgumentForm(string? version, bool warns)
    {
        var route = RunRoutes.For(RunCli.Codex, true, null, false, version is null ? null : Version.Parse(version));

        Assert.Equal(new RunRouteDecision(RunRoute.Typed, null, warns), route);
        Assert.Equal(warns ? RunText.CodexPickerDefault : null, RunRoutes.DefaultWarning(RunCli.Codex, route));
    }

    [Theory]
    [InlineData(RunCli.Codex)]
    [InlineData(RunCli.Grok)]
    public void CodexAndGrokWithNoPaneHaveNoRoute(RunCli cli)
    {
        Assert.Equal(RunRouteDecision.Unavailable(RunText.NoRoute), RunRoutes.For(cli, false, null, false));
    }

    [Fact]
    public void GrokWithAPaneTypesWithoutWarning()
    {
        Assert.Equal(new RunRouteDecision(RunRoute.Typed, null, false), RunRoutes.For(RunCli.Grok, true, null, false));
    }

    [Fact]
    public void OpenClawPatchesTheGateway()
    {
        Assert.Equal(RunRoute.GatewayPatch, RunRoutes.For(RunCli.OpenClaw, false, null, false).Route);
    }

    // --- RunPendingRules ---

    [Theory]
    [InlineData(RunCli.Codex, 15)]
    [InlineData(RunCli.OpenClaw, 15)]
    [InlineData(RunCli.Claude, 600)]
    [InlineData(RunCli.Grok, 600)]
    [InlineData(RunCli.ClaudeCloud, 600)]
    public void TheTimeoutFollowsTheCliCadence(RunCli cli, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RunPendingRules.TimeoutFor(cli));
    }

    [Fact]
    public void ANewerAgreeingReadingClearsAPendingSet()
    {
        var pending = new[] { new RunPending(RunField.Effort, "high", T0) };
        var reading = new RunReading("opus", "high", T0.AddSeconds(2), RunReadingSource.Transcript);

        var (still, failures) = RunPendingRules.Reconcile(pending, reading, T0.AddSeconds(2), TimeSpan.FromMinutes(1));

        Assert.Empty(still);
        Assert.Empty(failures);
    }

    [Fact]
    public void AnAliasAgreesWithItsResolvedId()
    {
        var pending = new[] { new RunPending(RunField.Model, "opus", T0) };
        var reading = new RunReading("claude-opus-5-5", null, T0.AddSeconds(2), RunReadingSource.Transcript);

        var (still, failures) = RunPendingRules.Reconcile(pending, reading, T0, TimeSpan.FromMinutes(1), Claude);

        Assert.Empty(still);
        Assert.Empty(failures);
    }

    [Fact]
    public void WithoutACatalogueAnAliasIsADifferentModel()
    {
        var pending = new[] { new RunPending(RunField.Model, "opus", T0) };
        var reading = new RunReading("claude-opus-5-5", null, T0.AddSeconds(2), RunReadingSource.Transcript);

        var (_, failures) = RunPendingRules.Reconcile(pending, reading, T0, TimeSpan.FromMinutes(1));

        Assert.Equal(new[] { RunText.Contradicted(RunField.Model, "opus", "claude-opus-5-5") }, failures);
    }

    [Fact]
    public void ANewerDisagreeingReadingIsReportedAsAFailure()
    {
        var pending = new[] { new RunPending(RunField.Effort, "high", T0) };
        var reading = new RunReading("opus", "medium", T0.AddSeconds(2), RunReadingSource.Transcript);

        var (still, failures) = RunPendingRules.Reconcile(pending, reading, T0.AddSeconds(2), TimeSpan.FromMinutes(1));

        Assert.Empty(still);
        Assert.Equal(new[] { "The effort level didn't change to high; the session is on medium." }, failures);
    }

    [Fact]
    public void AnOlderReadingNeitherClearsNorContradicts()
    {
        var pending = new[] { new RunPending(RunField.Model, "sonnet", T0) };
        var reading = new RunReading("opus", "medium", T0.AddSeconds(-5), RunReadingSource.Transcript);

        var (still, failures) = RunPendingRules.Reconcile(pending, reading, T0.AddSeconds(1), TimeSpan.FromMinutes(1));

        Assert.Equal(pending, still);
        Assert.Empty(failures);
    }

    [Fact]
    public void AReadingWithoutThatPartLeavesItPending()
    {
        var pending = new[] { new RunPending(RunField.Effort, "high", T0) };
        var reading = new RunReading("opus", null, T0.AddSeconds(5), RunReadingSource.Rollout);

        var (still, _) = RunPendingRules.Reconcile(pending, reading, T0.AddSeconds(5), TimeSpan.FromMinutes(1));

        Assert.Equal(pending, still);
    }

    [Fact]
    public void AnUndatedReadingSaysNothingAboutAPendingSet()
    {
        var pending = new[] { new RunPending(RunField.Effort, "high", T0) };
        var reading = new RunReading("opus", "low", null, RunReadingSource.PaneFooter);

        var (still, failures) = RunPendingRules.Reconcile(pending, reading, T0.AddSeconds(5), TimeSpan.FromMinutes(1));

        Assert.Equal(pending, still);
        Assert.Empty(failures);
    }

    [Fact]
    public void APendingSetPastItsTimeoutIsReported()
    {
        var pending = new[] { new RunPending(RunField.Model, "sonnet", T0) };

        var (still, failures) = RunPendingRules.Reconcile(pending, RunReading.None, T0.AddSeconds(16), TimeSpan.FromSeconds(15));

        Assert.Empty(still);
        Assert.Equal(new[] { RunText.TimedOut("sonnet") }, failures);
    }

    [Fact]
    public void ASecondSetForAFieldReplacesTheFirst()
    {
        IReadOnlyList<RunPending> pending = new[] { new RunPending(RunField.Model, "opus", T0), new RunPending(RunField.Effort, "low", T0) };

        var after = RunPendingRules.With(pending, new RunPending(RunField.Model, "sonnet", T0.AddSeconds(1)));

        Assert.Equal(new[] { new RunPending(RunField.Effort, "low", T0), new RunPending(RunField.Model, "sonnet", T0.AddSeconds(1)) }, after);
    }

    // --- RunControlsResolver.Resolve ---

    [Fact]
    public void AnEnabledSessionShowsItsReading()
    {
        var reading = new RunReading("claude-opus-5-5", "high", T0, RunReadingSource.Transcript);

        var o = RunControlsResolver.Resolve(RunCli.Claude, Control, reading, Claude, NonePending);

        Assert.Equal("opus", o.ModelId);
        Assert.Equal("high", o.ThinkingId);
        Assert.Equal("Opus 5.5 · high", o.ChipText);
        Assert.Equal(new[] { "opus", "sonnet", "haiku" }, o.Models.Select(m => m.Id));
        Assert.Equal(Five, o.ThinkingChoices.Select(c => c.Id));
        Assert.Null(o.ModelDisabledReason);
        Assert.Null(o.EffortDisabledReason);
        Assert.False(o.ModelPending);
        Assert.False(o.EffortPending);
        Assert.False(o.Locked);
        Assert.False(o.HasFast);
        Assert.False(o.HasTimeLimit);
        Assert.Null(o.DefaultWarning);
        Assert.Equal(RunReadingSource.Transcript, o.Source);
        Assert.Equal("opus", o.ConfirmedModelId);
        Assert.Equal("high", o.ConfirmedEffortId);
    }

    [Fact]
    public void NoRouteDisablesBothWithItsReason()
    {
        var o = RunControlsResolver.Resolve(RunCli.Claude, RunRouteDecision.Unavailable("no bridge"), RunReading.None, Claude, NonePending);

        Assert.Equal("no bridge", o.ModelDisabledReason);
        Assert.Equal("no bridge", o.EffortDisabledReason);
    }

    [Fact]
    public void ANoRouteDecisionWithoutAReasonStillGivesOne()
    {
        var o = RunControlsResolver.Resolve(RunCli.Grok, new RunRouteDecision(RunRoute.None, null, false), RunReading.None, Claude, NonePending);

        Assert.Equal(RunText.NoRoute, o.ModelDisabledReason);
    }

    [Fact]
    public void ALockedModelIsDisabledButEffortStillFollowsIt()
    {
        var reading = new RunReading("opus", "low", T0, RunReadingSource.Gateway);

        var o = RunControlsResolver.Resolve(RunCli.OpenClaw, new RunRouteDecision(RunRoute.GatewayPatch, null, false),
            reading, Claude, NonePending, modelLockedReason: "locked");

        Assert.True(o.Locked);
        Assert.Equal("locked", o.ModelDisabledReason);
        Assert.Null(o.EffortDisabledReason);
    }

    [Fact]
    public void AModelWithNoEffortSettingDisablesTheDialAndDropsItFromTheChip()
    {
        var reading = new RunReading("claude-haiku-4-5", null, T0, RunReadingSource.Transcript);

        var o = RunControlsResolver.Resolve(RunCli.Claude, Control, reading, Claude, NonePending);

        Assert.Equal(RunText.NoEffortSetting, o.EffortDisabledReason);
        Assert.Empty(o.ThinkingChoices);
        Assert.Equal("Haiku 4.5", o.ChipText);
    }

    [Fact]
    public void ACliThatCannotSetEffortSaysWhy()
    {
        var reading = new RunReading("opus", "high", T0, RunReadingSource.CloudEvents);

        var o = RunControlsResolver.Resolve(RunCli.ClaudeCloud, Control, reading, Claude, NonePending,
            effortUnavailableReason: "the worker refused it");

        Assert.Equal("the worker refused it", o.EffortDisabledReason);
        Assert.Null(o.ModelDisabledReason);
    }

    [Fact]
    public void NothingKnownSaysSo()
    {
        var o = RunControlsResolver.Resolve(RunCli.Codex, new RunRouteDecision(RunRoute.Typed, null, false), RunReading.None, RunCatalogue.Empty, NonePending);

        Assert.Null(o.ModelId);
        Assert.Null(o.ThinkingId);
        Assert.Equal($"{RunText.ModelUnknown} · {RunText.EffortUnknown}", o.ChipText);
        Assert.Empty(o.Models);
        Assert.Null(o.ConfirmedModelId);
    }

    [Fact]
    public void ACloudSessionWithNoEffortReadingSaysItIsNotReported()
    {
        var reading = new RunReading("opus", null, T0, RunReadingSource.CloudRoster);

        var o = RunControlsResolver.Resolve(RunCli.ClaudeCloud, Control, reading, Claude, NonePending);

        Assert.Equal($"Opus 5.5 · {RunText.CloudEffortUnreported}", o.ChipText);
        Assert.DoesNotContain(o.ThinkingChoices, c => c.Id == "max");
    }

    [Fact]
    public void ARunningModelMissingFromTheCatalogueIsStillListed()
    {
        var reading = new RunReading("claude-fable-5-2", "high", T0, RunReadingSource.Transcript);

        var o = RunControlsResolver.Resolve(RunCli.Claude, Control, reading, Claude, NonePending);

        Assert.Equal(new RemoteRunChoice("claude-fable-5-2", "claude-fable-5-2"), o.Models[0]);
        Assert.Equal("claude-fable-5-2", o.ModelId);
        // No model entry, so the dial offers every level the catalogue knows.
        Assert.Equal(Five, o.ThinkingChoices.Select(c => c.Id));
        Assert.Equal("claude-fable-5-2", o.ConfirmedModelId);
    }

    [Fact]
    public void ARunningEffortMissingFromTheLevelsIsStillListed()
    {
        var reading = new RunReading("opus", "ultra", T0, RunReadingSource.Transcript);

        var o = RunControlsResolver.Resolve(RunCli.Claude, Control, reading, Claude, NonePending);

        Assert.Equal("ultra", o.ThinkingChoices[0].Id);
    }

    [Fact]
    public void APendingSetIsDrawnAsTheChosenValue()
    {
        var reading = new RunReading("opus", "medium", T0, RunReadingSource.Transcript);
        var pending = new[] { new RunPending(RunField.Model, "sonnet", T0), new RunPending(RunField.Effort, "high", T0) };

        var o = RunControlsResolver.Resolve(RunCli.Claude, Control, reading, Claude, pending);

        Assert.Equal("sonnet", o.ModelId);
        Assert.Equal("high", o.ThinkingId);
        Assert.True(o.ModelPending);
        Assert.True(o.EffortPending);
        Assert.Equal("opus", o.ConfirmedModelId);
        Assert.Equal("medium", o.ConfirmedEffortId);
    }

    [Fact]
    public void TheTypedRouteCarriesItsWarning()
    {
        var o = RunControlsResolver.Resolve(RunCli.Claude, new RunRouteDecision(RunRoute.Typed, null, true), RunReading.None, Claude, NonePending);

        Assert.Equal(RunText.ClaudeTypedDefault, o.DefaultWarning);
    }

    [Fact]
    public void ACatalogueWhoseModelsHaveNoLevelsLeavesTheUnknownDialEmpty()
    {
        var cat = new RunCatalogue(new[] { new RunModel("a", "A", null, null) });

        var o = RunControlsResolver.Resolve(RunCli.Grok, new RunRouteDecision(RunRoute.Typed, null, false), RunReading.None, cat, NonePending);

        Assert.Empty(o.ThinkingChoices);
        Assert.Null(o.EffortDisabledReason);
    }

    // --- RunControlsResolver.Validate ---

    [Theory]
    [InlineData(RunField.Model, "", "name a model")]
    [InlineData(RunField.Effort, " ", "name an effort level")]
    public void AnEmptyValueIsRefused(RunField field, string value, string expected)
    {
        Assert.Equal(expected, RunControlsResolver.Validate(RunCli.Claude, field, value, RunReading.None, Claude));
    }

    [Theory]
    [InlineData("sonnet")]
    [InlineData("claude-sonnet-5-5")]
    public void AKnownModelMayGo(string value)
    {
        Assert.Null(RunControlsResolver.Validate(RunCli.Claude, RunField.Model, value, RunReading.None, Claude));
    }

    [Fact]
    public void AnUnknownModelIsRefused()
    {
        Assert.Equal(RunText.UnknownModel("gpt-6"), RunControlsResolver.Validate(RunCli.Claude, RunField.Model, "gpt-6", RunReading.None, Claude));
    }

    [Fact]
    public void WithNoCatalogueTheCliIsLeftToRefuseAModel()
    {
        Assert.Null(RunControlsResolver.Validate(RunCli.Claude, RunField.Model, "anything", RunReading.None, RunCatalogue.Empty));
    }

    [Fact]
    public void ALevelOfTheModelMayGo()
    {
        var reading = new RunReading("opus", null, T0, RunReadingSource.Transcript);
        Assert.Null(RunControlsResolver.Validate(RunCli.Claude, RunField.Effort, "XHIGH", reading, Claude));
    }

    [Fact]
    public void ALevelNotOfTheModelIsRefused()
    {
        var reading = new RunReading("opus", null, T0, RunReadingSource.Transcript);
        Assert.Equal(RunText.UnknownEffort("bogus", Five),
            RunControlsResolver.Validate(RunCli.Claude, RunField.Effort, "bogus", reading, Claude));
    }

    [Fact]
    public void MaxIsRefusedOnACloudSession()
    {
        var reading = new RunReading("opus", null, T0, RunReadingSource.CloudEvents);
        Assert.NotNull(RunControlsResolver.Validate(RunCli.ClaudeCloud, RunField.Effort, "max", reading, Claude));
    }

    [Fact]
    public void AnyEffortIsRefusedForAModelWithoutTheSetting()
    {
        var reading = new RunReading("haiku", null, T0, RunReadingSource.Transcript);
        Assert.Equal(RunText.NoEffortSetting, RunControlsResolver.Validate(RunCli.Claude, RunField.Effort, "low", reading, Claude));
    }

    [Fact]
    public void WithNoLevelsKnownTheCliIsLeftToRefuseAnEffort()
    {
        Assert.Null(RunControlsResolver.Validate(RunCli.Grok, RunField.Effort, "high", RunReading.None, RunCatalogue.Empty));
    }

    // --- SlashRouting: the effort arm ---

    [Theory]
    [InlineData("high", "high")]
    [InlineData("XHIGH", "xhigh")]
    public void EffortBecomesApplyFlagSettings(string args, string level)
    {
        var request = SlashRouting.ControlRequest("apply_flag_settings", args, out var problem);

        Assert.Null(problem);
        Assert.Equal("{\"subtype\":\"apply_flag_settings\",\"settings\":{\"effortLevel\":\"" + level + "\"}}", request!.ToJsonString());
    }

    [Fact]
    public void DefaultEffortIsAJsonNull()
    {
        var request = SlashRouting.ControlRequest("apply_flag_settings", "default", out _);

        Assert.Equal("""{"subtype":"apply_flag_settings","settings":{"effortLevel":null}}""", request!.ToJsonString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    public void ABadEffortIsRefusedBeforeSending(string args)
    {
        Assert.Null(SlashRouting.ControlRequest("apply_flag_settings", args, out var problem));
        Assert.Contains("low, medium, high, xhigh, max, or default", problem);
    }

    [Fact]
    public void EffortIsAControlCommand()
    {
        Assert.True(SlashRouting.HasControl("/effort"));
        Assert.Equal(SlashRoute.Control("apply_flag_settings"), SlashRouting.Resolve(SlashChannel.Bridge, "/effort", "high"));
    }
}
