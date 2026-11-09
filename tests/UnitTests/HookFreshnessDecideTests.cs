using System.IO;
using Xunit;

namespace Orbweaver.Tests;

// The pure half of CB-265's hook refresh: whether to overwrite, and which paths
// are the installed copies on each platform. Both platforms are asked here so
// neither CI leg only ever checks its own.
public class HookFreshnessDecideTests
{
    private static readonly byte[] Old = { 1, 2, 3 };
    private static readonly byte[] New = { 1, 2, 4 };

    [Fact]
    public void ADifferingInstalledCopyIsReapplied() =>
        Assert.Equal(HookFreshness.Verdict.Reapply, HookFreshness.Decide(true, Old, New));

    [Fact]
    public void AnIdenticalInstalledCopyIsLeftAlone() =>
        Assert.Equal(HookFreshness.Verdict.Skip, HookFreshness.Decide(true, New, (byte[])New.Clone()));

    [Fact]
    public void AMissingInstalledCopyIsNeverCreated() =>
        Assert.Equal(HookFreshness.Verdict.Skip, HookFreshness.Decide(false, null, New));

    [Fact]
    public void AMissingInstalledCopyIsNeverCreatedEvenWhenBytesAreSupplied() =>
        Assert.Equal(HookFreshness.Verdict.Skip, HookFreshness.Decide(false, Old, New));

    [Fact]
    public void AnUnreadableInstalledCopyIsLeftAlone() =>
        Assert.Equal(HookFreshness.Verdict.Skip, HookFreshness.Decide(true, null, New));

    [Fact]
    public void ANullBundledScriptNeverReplacesAWorkingCopy() =>
        Assert.Equal(HookFreshness.Verdict.Skip, HookFreshness.Decide(true, Old, null));

    [Fact]
    public void AnEmptyBundledScriptStillCountsAsADifference() =>
        // Empty is a real, different byte string; the guard against it is the
        // caller's null for "could not read", not a length check here.
        Assert.Equal(HookFreshness.Verdict.Reapply, HookFreshness.Decide(true, Old, new byte[0]));

    [Fact]
    public void MacOsCandidatesAreTheOrbweaverCopyThenTheLegacyOne()
    {
        var home = Path.Combine("h", "me");
        Assert.Equal(
            new[]
            {
                Path.Combine(home, ".claude", "orbweaver", "OrbweaverHook.sh"),
                Path.Combine(home, ".claude", "claude-buddy", "ClaudeBuddyHook.sh"),
                Path.Combine(home, ".codex", "orbweaver", "OrbweaverHook.sh"),
                Path.Combine(home, ".codex", "claude-buddy", "ClaudeBuddyHook.sh"),
            },
            HookFreshness.InstalledCandidates(false, home, Path.Combine("l", "local")));
    }

    [Fact]
    public void WindowsCandidatesLiveUnderLocalAppDataNotUnderDotClaude()
    {
        var local = Path.Combine("l", "local");
        Assert.Equal(
            new[]
            {
                Path.Combine(local, "Orbweaver", "OrbweaverHook.ps1"),
                Path.Combine(local, "ClaudeBuddy", "ClaudeBuddyHook.ps1"),
                Path.Combine("h", "me", ".codex", "orbweaver", "OrbweaverHook.ps1"),
                Path.Combine("h", "me", ".codex", "claude-buddy", "ClaudeBuddyHook.ps1"),
            },
            HookFreshness.InstalledCandidates(true, Path.Combine("h", "me"), local));
    }

    // CB-316: a CODEX_HOME moves Codex's copies with it.
    [Fact]
    public void A_codex_home_moves_the_codex_copies()
    {
        var candidates = HookFreshness.InstalledCandidates(false, Path.Combine("h", "me"), "l", codexHome: Path.Combine("c", "x"));
        Assert.Contains(Path.Combine("c", "x", "orbweaver", "OrbweaverHook.sh"), candidates);
        Assert.DoesNotContain(Path.Combine("h", "me", ".codex", "orbweaver", "OrbweaverHook.sh"), candidates);
    }
}
