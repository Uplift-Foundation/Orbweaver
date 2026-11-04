using System.Runtime.InteropServices;
using Xunit;

namespace Orbweaver.Tests;

// CB-306's backstop: a SIGHUP is absorbed and leaves exactly one line behind.
// What cannot be reproduced here is the kernel sending that signal to a
// session-1 process group; docs/sighup-findings.md says what was measured on a
// real machine and what was not.
public class HangupGuardTests
{
    [Fact]
    public void HandleCancelsTheSignalAndWritesExactlyOneEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbweaver-hup-" + Guid.NewGuid().ToString("N"));
        using (CrashLog.ScopeForTests(dir))
        {
            var context = new PosixSignalContext(PosixSignal.SIGHUP);

            HangupGuard.Handle(context);

            Assert.True(context.Cancel);
            var log = File.ReadAllText(CrashLog.Path_);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(log, "=== ").Count);
            Assert.Contains("SIGHUP received and ignored (CB-306)", log);
        }
    }

    // One test, because Installed is process-wide state and two tests would
    // race over it. Not-macOS leaves nothing registered; macOS registers once
    // and a second call keeps the one registration.
    [Fact]
    public void InstallRegistersOnlyOnMacOsAndOnlyOnce()
    {
        HangupGuard.Install(macOS: false);
        Assert.False(HangupGuard.Installed);

        HangupGuard.Install(macOS: true);
        Assert.True(HangupGuard.Installed);

        // The parameterless entry Program passes to Startup.Run.
        HangupGuard.Install();
        Assert.True(HangupGuard.Installed);
    }
}
