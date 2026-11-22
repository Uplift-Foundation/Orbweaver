using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-404: driving the /model picker of a Codex older than 0.164, against a
// scripted pane whose screens are the real 0.154.0 captures. Every key must
// follow a read of the screen it is meant for, and every wrong screen is left
// with Esc.
public class CodexPickerTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "run", name));

    private static readonly string ModelsScreen = Fixture("picker-models.txt");
    private static readonly string TerraLevels = Fixture("picker-levels.txt");
    // An idle prompt the typing gate recognises (its footer names shortcuts).
    private const string Prompt = "› Ask Codex to do anything\n  sonnet default · ~/x\n  ? for shortcuts\n";

    // A pane that behaves like Codex 0.154.0 as measured: "/model" opens the
    // models screen, a digit opens that model's levels (only terra's capture
    // exists, so other models show terra's levels — which is what the
    // wrong-screen case needs), a level digit closes with "Model changed to",
    // and Esc steps back one screen.
    private sealed class Pane
    {
        public enum Where { Prompt, Models, Levels }

        public Where At = Where.Prompt;
        public string? Override;
        public bool OpensPicker = true;
        public bool Unreadable;
        public bool Confirms = true;
        public bool TypeWorks = true;
        public HashSet<string> RefusedKeys = new();
        public string? LastChanged;
        public readonly List<string> Log = new();
        public int ReadsSinceKey;

        public CodexPicker.Seams Seams => new(
            line =>
            {
                Log.Add("type " + line);
                if (!TypeWorks) return Task.FromResult(false);
                if (line == "/model" && OpensPicker) At = Where.Models;
                ReadsSinceKey = 0;
                return Task.FromResult(true);
            },
            key =>
            {
                // The rule under test: a key only after the screen was read.
                Assert.True(ReadsSinceKey > 0 || key == "Escape", $"pressed {key} without reading the screen");
                Log.Add("press " + key);
                ReadsSinceKey = 0;
                if (RefusedKeys.Contains(key)) return Task.FromResult(false);
                if (key == "Escape") { At = At == Where.Levels ? Where.Models : Where.Prompt; return Task.FromResult(true); }
                var d = int.Parse(key);
                if (At == Where.Models) { At = Where.Levels; return Task.FromResult(true); }
                if (At == Where.Levels)
                {
                    var level = new[] { "low", "medium", "high", "xhigh" }[d - 1];
                    LastChanged = Confirms ? $"• Model changed to gpt-5.6-terra {level}" : null;
                    At = Where.Prompt;
                }
                return Task.FromResult(true);
            },
            () =>
            {
                ReadsSinceKey++;
                if (Unreadable) return Task.FromResult<string?>(null);
                if (Override is not null) return Task.FromResult<string?>(Override);
                return Task.FromResult<string?>(At switch
                {
                    Where.Models => ModelsScreen,
                    Where.Levels => TerraLevels,
                    _ => (LastChanged is null ? "" : LastChanged + "\n") + Prompt,
                });
            },
            _ => Task.CompletedTask);
    }

    [Fact]
    public async Task AModelAndLevelAreChosenByTheirDigitsAndConfirmed()
    {
        var pane = new Pane();

        Assert.Null(await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "xhigh", effortRequired: true));

        Assert.Equal(new[] { "type /model", "press 3", "press 4" }, pane.Log);
    }

    [Fact]
    public async Task AnEffortChangeKeepsTheHighlightedModel()
    {
        var pane = new Pane();
        // The highlighted model is row 1 in the capture; terra's levels stand in.
        Assert.Equal(CodexPicker.WrongScreen("gpt-6-astra"), await CodexPicker.SetAsync(pane.Seams, null, "high", effortRequired: true));
        Assert.Equal(new[] { "type /model", "press 1", "press Escape", "press Escape" }, pane.Log);
        Assert.Equal(Pane.Where.Prompt, pane.At);
    }

    [Fact]
    public async Task AnEffortChangeForAModelNotListedFallsBackToTheHighlightedRow()
    {
        var pane = new Pane();
        Assert.Equal(CodexPicker.WrongScreen("gpt-6-astra"), await CodexPicker.SetAsync(pane.Seams, "sonnet", "high", effortRequired: true));
        Assert.Equal("press 1", pane.Log[1]);
    }

    [Fact]
    public async Task AnEffortChangeChoosesTheSessionsModelByName()
    {
        var pane = new Pane();
        Assert.Null(await CodexPicker.SetAsync(pane.Seams, "GPT-5.6-Terra", "high", effortRequired: true));
        Assert.Equal(new[] { "type /model", "press 3", "press 3" }, pane.Log);
    }

    [Fact]
    public async Task AModelChangeKeepsThePreferredLevelWhenItIsListed()
    {
        var pane = new Pane();

        Assert.Null(await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "low", effortRequired: false));

        Assert.Equal("press 1", pane.Log[^1]);
    }

    [Fact]
    public async Task AModelChangeWithoutThePreferredLevelTakesTheHighlightedOne()
    {
        var pane = new Pane();

        Assert.Null(await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "ultra", effortRequired: false));

        // "Medium (default)" is highlighted in the capture: row 2.
        Assert.Equal("press 2", pane.Log[^1]);
    }

    [Fact]
    public async Task AModelChangeWithNoKnownLevelTakesTheHighlightedOne()
    {
        var pane = new Pane();

        Assert.Null(await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", null, effortRequired: false));

        Assert.Equal("press 2", pane.Log[^1]);
    }

    [Fact]
    public async Task ARequiredLevelNotListedBacksOutOfBothScreens()
    {
        var pane = new Pane();

        Assert.Equal(CodexPicker.NotListed("max"), await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "max", effortRequired: true));

        Assert.Equal(new[] { "type /model", "press 3", "press Escape", "press Escape" }, pane.Log);
        Assert.Equal(Pane.Where.Prompt, pane.At);
    }

    [Fact]
    public async Task AModelNotOnTheFirstScreenBacksOutOnce()
    {
        var pane = new Pane();

        Assert.Equal(CodexPicker.NotListed("gpt-7"), await CodexPicker.SetAsync(pane.Seams, "gpt-7", "low", effortRequired: false));

        Assert.Equal(new[] { "type /model", "press Escape" }, pane.Log);
        Assert.Equal(Pane.Where.Prompt, pane.At);
    }

    [Fact]
    public async Task NoHighlightedModelIsNotGuessed()
    {
        var pane = new Pane { Override = ModelsScreen.Replace("›", " ") };

        Assert.Equal(CodexPicker.NotListed("the current model"), await CodexPicker.SetAsync(pane.Seams, null, "low", effortRequired: true));
    }

    [Fact]
    public async Task NoHighlightedLevelIsNotGuessed()
    {
        var pane = new Pane();
        var seams = pane.Seams;
        var levelsNoHighlight = TerraLevels.Replace("›", " ");
        var wrapped = seams with { ReadScreen = async () => pane.At == Pane.Where.Levels ? levelsNoHighlight : await seams.ReadScreen() };

        Assert.Equal(CodexPicker.NotListed("the current level"), await CodexPicker.SetAsync(wrapped, "gpt-5.6-terra", null, effortRequired: false));
    }

    [Fact]
    public async Task ADialogInsteadOfThePickerIsLeftWithEscape()
    {
        var pane = new Pane { Override = "  Hooks need review\n› 1. Review hooks\n  2. Trust all and continue" };

        Assert.Equal(CodexPicker.NoPicker, await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "low", effortRequired: true));

        Assert.Equal(new[] { "type /model", "press Escape" }, pane.Log);
    }

    [Fact]
    public async Task TheLevelsScreenWhereTheModelsShouldBeIsAFailure()
    {
        var pane = new Pane { Override = TerraLevels };

        Assert.Equal(CodexPicker.NoPicker, await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "low", effortRequired: true));
    }

    [Fact]
    public async Task ThePickerNeverOpeningTimesOut()
    {
        var pane = new Pane { OpensPicker = false };

        Assert.Equal(CodexPicker.NoPicker, await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "low", effortRequired: true));

        Assert.Equal(new[] { "type /model", "press Escape" }, pane.Log);
    }

    [Fact]
    public async Task ThePickerStuckOnModelsAfterADigitBacksOutTwice()
    {
        var pane = new Pane();
        var seams = pane.Seams;
        // The digit is accepted but the screen never moves on.
        var stuck = seams with { Press = async key => { var ok = await seams.Press(key); if (key != "Escape") pane.At = Pane.Where.Models; return ok; } };

        Assert.Equal(CodexPicker.NoPicker, await CodexPicker.SetAsync(stuck, "gpt-5.6-terra", "low", effortRequired: true));
        Assert.Equal(new[] { "press Escape", "press Escape" }, pane.Log.TakeLast(2));
    }

    [Fact]
    public async Task AnUnreadablePaneGetsNoKeysAtAll()
    {
        var pane = new Pane { Unreadable = true };

        Assert.Equal(CodexPicker.Unreadable, await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "low", effortRequired: true));

        Assert.Equal(new[] { "type /model" }, pane.Log);
    }

    [Fact]
    public async Task APaneThatBecomesUnreadableOnTheSecondScreenGetsNoMoreKeys()
    {
        var pane = new Pane();
        var seams = pane.Seams;
        var flaky = seams with { ReadScreen = async () => pane.At == Pane.Where.Levels ? null : await seams.ReadScreen() };

        Assert.Equal(CodexPicker.Unreadable, await CodexPicker.SetAsync(flaky, "gpt-5.6-terra", "low", effortRequired: true));

        Assert.Equal(new[] { "type /model", "press 3" }, pane.Log);
    }

    [Theory]
    [InlineData("3", 1)]
    [InlineData("1", 2)]
    public async Task ARefusedKeyBacksOut(string refused, int escapes)
    {
        var pane = new Pane();
        pane.RefusedKeys.Add(refused);

        Assert.Equal(CodexPicker.KeyRefused, await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "low", effortRequired: true));

        Assert.Equal(escapes, pane.Log.Count(l => l == "press Escape"));
    }

    // The picker closed on the level's digit but no "Model changed to" line
    // was seen in time: not a failure, and no confirmed reading — it stays a
    // pending change for the rollout to settle.
    [Fact]
    public async Task NoConfirmationLineLeavesItPendingNotFailed()
    {
        var pane = new Pane { Confirms = false };

        var outcome = await CodexPicker.RunAsync(pane.Seams, "gpt-5.6-terra", "low", effortRequired: true);

        Assert.Null(outcome.Failure);
        Assert.Null(outcome.Confirmed);
    }

    [Fact]
    public async Task TheConfirmationLineIsReturnedAsAReading()
    {
        var pane = new Pane();

        var outcome = await CodexPicker.RunAsync(pane.Seams, "gpt-5.6-terra", "xhigh", effortRequired: true);

        Assert.Null(outcome.Failure);
        Assert.Equal(new RunReading("gpt-5.6-terra", "xhigh", null, RunReadingSource.PaneFooter), outcome.Confirmed);
    }

    [Fact]
    public async Task AConfirmationForAnotherLevelIsNotAConfirmation()
    {
        var pane = new Pane();
        var seams = pane.Seams;
        var lying = seams with { ReadScreen = async () => pane.At == Pane.Where.Prompt && pane.LastChanged is not null ? "• Model changed to gpt-5.6-terra high\n" : await seams.ReadScreen() };

        var outcome = await CodexPicker.RunAsync(lying, "gpt-5.6-terra", "low", effortRequired: true);
        Assert.Null(outcome.Failure);
        Assert.Null(outcome.Confirmed);
    }

    [Fact]
    public async Task AConfirmationWithoutALevelIsEnough()
    {
        var pane = new Pane();
        var seams = pane.Seams;
        var bare = seams with { ReadScreen = async () => pane.At == Pane.Where.Prompt && pane.LastChanged is not null ? "• Model changed to gpt-5.6-terra\n" : await seams.ReadScreen() };

        Assert.Null(await CodexPicker.SetAsync(bare, "gpt-5.6-terra", "low", effortRequired: true));
    }

    // Through the host: a Codex before 0.164 with a pane that can be read and
    // pressed goes through the picker and is then pending like any other set.
    [Fact]
    public async Task TheHostDrivesThePickerForAnOldCodex()
    {
        var pane = new Pane();
        var seams = pane.Seams;
        var typed = new List<string>();
        var host = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, _ => null, () => true, _ => new RunReading("gpt-5.6-terra", "medium", null, RunReadingSource.PaneFooter), _ => null,
            _ => new Version(0, 154, 0),
            async (_, text) => { typed.Add(text); return await seams.TypeLine(text); },
            (_, _, _) => Task.FromResult(MirrorSlashHost.ControlVerdict.Done),
            () => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            Log: (_, _) => { },
            PressKey: (_, key) => seams.Press(key),
            Delay: _ => Task.CompletedTask,
            CapturePane: _ => seams.ReadScreen()));
        var codex = new SessionStatus { SessionId = "c1", Source = SessionSource.Codex, Cwd = "/w" };

        Assert.Equal(RunSetResult.Sent, await host.SetAsync(codex, RunField.Effort, "high"));
        Assert.Equal(new[] { "/model" }, typed);
        // Codex confirmed it on screen ("Model changed to gpt-5.6-terra high"):
        // that is a reading, so nothing is left pending and the dial shows it.
        Assert.False(host.Options(codex)!.EffortPending);
        Assert.Equal("high", host.Options(codex)!.ThinkingId);
        Assert.True(host.Options(codex)!.SavesDefault);
        Assert.Equal(RunText.CodexPickerDefault, host.Options(codex)!.DefaultWarning);

        pane.Override = "  Hooks need review\n› 1. Review hooks";
        // A dialog up instead of the prompt: the gate does not type "/model",
        // and its own sentence is what comes back.
        var refused = await host.SetAsync(codex, RunField.Model, "gpt-5.6-terra");
        Assert.Equal(RunSetKind.Failed, refused.Kind);
        Assert.NotEqual(CodexPicker.TypeRefused, refused.Why);
        Assert.Equal(new[] { "/model" }, typed);

        // With nothing to press with, the route is unavailable as before.
        var noKeys = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, _ => null, () => true, _ => null, _ => null, _ => new Version(0, 154, 0),
            (_, _) => Task.FromResult(true), (_, _, _) => Task.FromResult(MirrorSlashHost.ControlVerdict.Done),
            () => DateTimeOffset.UnixEpoch, Log: (_, _) => { }));
        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, RunControlsHost.OldCodexReason), await noKeys.SetAsync(codex, RunField.Model, "x"));
    }

    // The paired case: the same set where the screen never confirms stays
    // pending under the host until a reading settles it.
    [Fact]
    public async Task TheHostKeepsAnUnconfirmedPickerSetPending()
    {
        var pane = new Pane { Confirms = false };
        var seams = pane.Seams;
        var host = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, _ => null, () => true, _ => new RunReading("gpt-5.6-terra", "medium", null, RunReadingSource.PaneFooter), _ => null,
            _ => new Version(0, 154, 0),
            (_, text) => seams.TypeLine(text),
            (_, _, _) => Task.FromResult(MirrorSlashHost.ControlVerdict.Done),
            () => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            Log: (_, _) => { },
            PressKey: (_, key) => seams.Press(key),
            Delay: _ => Task.CompletedTask,
            CapturePane: _ => seams.ReadScreen()));
        var codex = new SessionStatus { SessionId = "c2", Source = SessionSource.Codex, Cwd = "/w" };

        Assert.Equal(RunSetResult.Sent, await host.SetAsync(codex, RunField.Effort, "high"));

        var o = host.Options(codex)!;
        Assert.True(o.EffortPending);
        Assert.Equal("medium", o.ConfirmedEffortId);
    }

    // A rollout row newer than the screen's confirmation wins over it.
    [Fact]
    public async Task ANewerRolloutRowWinsOverThePickersConfirmation()
    {
        var pane = new Pane();
        var seams = pane.Seams;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        RunReading files = new("gpt-5.6-terra", "medium", now.AddSeconds(-60), RunReadingSource.Rollout);
        var host = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, _ => null, () => true, _ => files, _ => null,
            _ => new Version(0, 154, 0),
            (_, text) => seams.TypeLine(text),
            (_, _, _) => Task.FromResult(MirrorSlashHost.ControlVerdict.Done),
            () => now,
            Log: (_, _) => { },
            PressKey: (_, key) => seams.Press(key),
            Delay: _ => Task.CompletedTask,
            CapturePane: _ => seams.ReadScreen()));
        var codex = new SessionStatus { SessionId = "c3", Source = SessionSource.Codex, Cwd = "/w" };

        await host.SetAsync(codex, RunField.Effort, "low");
        Assert.Equal("low", host.Options(codex)!.ThinkingId);

        files = new RunReading("gpt-5.6-terra", "xhigh", now.AddSeconds(30), RunReadingSource.Rollout);
        Assert.Equal("xhigh", host.Options(codex)!.ThinkingId);
    }

    [Fact]
    public async Task TypingRefusedPressesNothing()
    {
        var pane = new Pane { TypeWorks = false };

        Assert.Equal(CodexPicker.TypeRefused, await CodexPicker.SetAsync(pane.Seams, "gpt-5.6-terra", "low", effortRequired: true));

        Assert.Equal(new[] { "type /model" }, pane.Log);
    }
}
