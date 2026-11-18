using Xunit;

namespace Orbweaver.Tests;

// What the Settings card tells a person after an installer run, and what the
// log says about it (CB-258). Both are pure decisions over a HookInstallResult,
// so no process and no window: the cases are one per outcome, because each
// outcome needs the reader to do something different.
public class HookInstallerStatusTests
{
    private static HookInstallResult Result(
        HookInstallOutcome outcome, int? exit = null, string output = "", string error = "") =>
        new(outcome, "install-macos-hooks.sh", exit, output, error);

    [Fact]
    public void ASuccessfulRunSaysTheProfileWasWired()
    {
        var text = HookInstaller.StatusMessage(Result(HookInstallOutcome.Ok, 0), ".claude-work");

        Assert.Equal("Wired hooks into .claude-work.", text);
    }

    [Fact]
    public void ASuccessfulRunWhoseHooksAreNotThereDoesNotClaimSuccess()
    {
        // Exit 0 is what the installer returns when it could not read the saved
        // list and so wired nothing extra: the failure this ticket could not rule
        // out, and the reason the card checks the file itself.
        var text = HookInstaller.StatusMessage(Result(HookInstallOutcome.Ok, 0), ".claude-work", wired: false);

        Assert.StartsWith("The installer ran, but .claude-work still has no", text);
        Assert.Contains(HookInstallerLog.Path_, text);
    }

    [Fact]
    public void ASuccessfulRunWhoseHooksAreConfirmedSaysWired()
    {
        var text = HookInstaller.StatusMessage(Result(HookInstallOutcome.Ok, 0), ".claude-work", wired: true);

        Assert.Equal("Wired hooks into .claude-work.", text);
    }

    [Fact]
    public void AMissingInstallerNamesTheScript()
    {
        var text = HookInstaller.StatusMessage(
            HookInstallResult.NotFound("install-macos-hooks.sh"), ".claude-work");

        Assert.StartsWith(
            "Couldn't wire hooks into .claude-work: the installer install-macos-hooks.sh was not found.", text);
        Assert.Contains(HookInstallerLog.Path_, text);
    }

    [Fact]
    public void ANonzeroExitGivesTheCodeAndTheLastLineOfStderr()
    {
        var text = HookInstaller.StatusMessage(
            Result(HookInstallOutcome.Failed, 4, error: "first\n\nboom: no such file\n\n"), ".claude-work");

        Assert.Contains("exited with code 4 (boom: no such file)", text);
        Assert.DoesNotContain("first", text);
    }

    [Fact]
    public void ANonzeroExitWithNoStderrGivesJustTheCode()
    {
        var text = HookInstaller.StatusMessage(Result(HookInstallOutcome.Failed, 1), ".claude-work");

        Assert.Contains("exited with code 1.", text);
        Assert.DoesNotContain("(", text);
    }

    [Fact]
    public void AnUnreadableSavedListIsNamedRatherThanShownAsABareExitCode()
    {
        var text = HookInstaller.StatusMessage(
            Result(HookInstallOutcome.Failed, HookInstaller.SavedListUnreadableExit,
                error: "warning: could not read the saved profile list from /x/settings.json (exit 1)"),
            ".claude-work");

        Assert.Contains("the saved profile list could not be read, so no extra profiles were wired", text);
        Assert.Contains("(warning: could not read the saved profile list from /x/settings.json (exit 1))", text);
        Assert.DoesNotContain("exited with code", text);
    }

    [Fact]
    public void AnUnreadableSavedListWithNoStderrStillSaysSo()
    {
        var text = HookInstaller.StatusMessage(
            Result(HookInstallOutcome.Failed, HookInstaller.SavedListUnreadableExit), ".claude-work");

        Assert.Contains("the saved profile list could not be read, so no extra profiles were wired.", text);
    }

    [Fact]
    public void ALongStderrLineIsCutSoTheStatusLineStaysAOneLiner()
    {
        var text = HookInstaller.StatusMessage(
            Result(HookInstallOutcome.Failed, 1, error: new string('x', 400)), ".claude-work");

        Assert.Contains(new string('x', 160) + "…", text);
        Assert.DoesNotContain(new string('x', 161), text);
    }

    [Fact]
    public void ATimeoutSaysHowLongItWaited()
    {
        var text = HookInstaller.StatusMessage(Result(HookInstallOutcome.TimedOut), ".claude-work");

        Assert.Contains("did not finish within 20 seconds", text);
    }

    [Fact]
    public void AnExceptionGivesItsMessage()
    {
        var text = HookInstaller.StatusMessage(
            Result(HookInstallOutcome.Threw, error: "Access to the path is denied"), ".claude-work");

        Assert.StartsWith("Couldn't wire hooks into .claude-work: Access to the path is denied.", text);
    }

    [Fact]
    public void TheLogEntryCarriesOutcomeExitCodeAndBothStreams()
    {
        var when = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

        var text = HookInstallerLog.Format(
            when, "install-macos-hooks.sh",
            Result(HookInstallOutcome.Failed, 4, output: "one\r\ntwo", error: "bad"));

        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("install-macos-hooks.sh  Failed  exit 4", lines[0]);
        Assert.Equal(
            new[] { "    stdout:", "      one", "      two", "    stderr:", "      bad" },
            lines.Skip(1).ToArray());
    }

    [Fact]
    public void ALogEntryWithNoExitCodeOrOutputIsOneLine()
    {
        var text = HookInstallerLog.Format(
            DateTimeOffset.Now, "install-macos-hooks.sh", HookInstallResult.NotFound("install-macos-hooks.sh"));

        Assert.Single(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("ScriptNotFound", text);
        Assert.DoesNotContain("exit", text);
    }

    // CB-377: an entry whose output may be incomplete says so on its header line,
    // and a normal one says nothing, so a reader of the log can tell "Ok" from
    // "Ok, but a descendant still held the pipe".
    [Fact]
    public void AnEntryWhoseOutputMayBeCutShortSaysSoOnItsHeaderOnly()
    {
        var when = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var cut = new HookInstallResult(HookInstallOutcome.Ok, "x.sh", 0, "hello", "", OutputMayBeCutShort: true);
        var whole = new HookInstallResult(HookInstallOutcome.Ok, "x.sh", 0, "hello", "");

        var header = HookInstallerLog.Format(when, "x.sh", cut)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];

        Assert.Contains("Ok  exit 0  (output may be cut short", header);
        Assert.DoesNotContain("cut short", HookInstallerLog.Format(when, "x.sh", whole));
    }
}
