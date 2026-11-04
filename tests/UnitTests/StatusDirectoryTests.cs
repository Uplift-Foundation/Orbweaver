using Xunit;

namespace Orbweaver.UnitTests;

// Which temp root holds the session status files.
//
// **The hooks and the app have to agree on this, and on a launchd-started Mac
// they did not.** Hooks run from the user's shell, where `TMPDIR` is the
// per-user temp directory macOS gives every login session. The app asked .NET
// for `Path.GetTempPath()`, which reads `TMPDIR` and falls back to `/tmp` when
// it is unset — and a launchd agent's environment carries no `TMPDIR` at all.
//
// Measured on the mini: `launchctl print` listed no TMPDIR for the job, and
// `/tmp/claude_buddy` did not exist. So Buddy read an empty directory, drew no
// orbs, and told every other machine it had no sessions, while two live Claude
// Code sessions sat in the real directory the whole time. Nothing errored — the
// two halves were looking in different places.
//
// That is precisely the deployment the direct link is for: a headless Mac,
// always on, started by launchd, serving the machine somebody is sitting at.
//
// **And then it happened again from the other side (CB-306).** The fix above
// let TMPDIR win when set, because that is where the hooks wrote. OpenClaw
// runs its gateway with TMPDIR=~/.openclaw/tmp, and every Claude Code session
// it spawns inherits that — so did a tmux server started from one, and every
// session inside it. Those hooks wrote to ~/.openclaw/tmp/orbweaver; the app,
// with the login TMPDIR, read /var/folders/.../T/orbweaver; the mini served no
// sessions to any peer with five alive. An environment variable any parent
// can set is not something two processes with different parents can agree
// through. The per-user directory, asked of the C library, is — confstr reads
// no environment — so it now outranks TMPDIR on both halves, and the sandbox
// for a second instance is ORBWEAVER_STATUS_ROOT, which both honour first.
public class StatusDirectoryTests
{
    [Fact]
    public void ThePerUserDirectoryOutranksAnExplicitTmpdir()
    {
        // The CB-306 shape: TMPDIR set by a parent the user never chose, and
        // a platform that can say where this user's temp directory really is.
        // Before, this returned the caller's value and the hook agreed with
        // it; now neither does.
        Assert.Equal(
            "/per/user",
            StatusDirectory.Root(null, "/set/by/the/caller", () => "/per/user", "/tmp"));
    }

    [Fact]
    public void TmpdirIsUsedOnlyWhenThePlatformCannotAnswer()
    {
        // TMPDIR is not gone — it is the next thing asked. A platform with no
        // per-user directory (Windows, Linux, a failed P/Invoke) leaves it in
        // charge exactly as before.
        Assert.Equal(
            "/set/by/the/caller",
            StatusDirectory.Root(null, "/set/by/the/caller", () => null, "/tmp"));
    }

    [Fact]
    public void WithNoTmpdirThePlatformIsAsked()
    {
        // The launchd case, which is the one that shipped broken.
        Assert.Equal("/per/user", StatusDirectory.Root(null, null, () => "/per/user", "/tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTmpdirCountsAsNoneRatherThanAsARoot(string tmpdir)
    {
        // An empty variable is set-but-useless, and joining a folder onto it
        // would produce a relative path that resolves against whatever the
        // working directory happens to be. The platform answers nothing here
        // so that the TMPDIR arm is the one under test.
        Assert.Equal("/tmp", StatusDirectory.Root(null, tmpdir, () => null, "/tmp"));
    }

    [Fact]
    public void WithNothingToGoOnTheOldBehaviourRemains()
    {
        // On a platform that cannot answer — or when the call fails — this
        // falls back to what .NET said, which is what it did before. Being no
        // worse than the previous behaviour is the floor.
        Assert.Equal("/tmp", StatusDirectory.Root(null, null, () => null, "/tmp"));
    }

    [Fact]
    public void AnEmptyPlatformAnswerIsNotUsedEither()
    {
        Assert.Equal("/tmp", StatusDirectory.Root(null, null, () => "", "/tmp"));
    }

    [Fact]
    public void ThePlatformIsAskedEvenWhenTmpdirIsSet()
    {
        // The inverse of the rule this file used to pin. Skipping the P/Invoke
        // whenever TMPDIR was set was the cheap thing, and it is exactly the
        // CB-306 bug: the answer in the environment was somebody else's. It is
        // one confstr(3) into a caller-supplied buffer, and it runs every time.
        var asked = 0;

        var root = StatusDirectory.Root(null, "/mine", () => { asked++; return "/per/user"; }, "/tmp");

        Assert.Equal(1, asked);
        Assert.Equal("/per/user", root);
    }

    [Fact]
    public void TheStatusRootOverrideBeatsAnExplicitTmpdir()
    {
        // CB-172. This arm exists so a test suite can have its own status
        // directory without moving TMPDIR out from under everything else in the
        // process — the coverage collector's IPC socket lives under TMPDIR, and
        // moving it mid-process left `tools/coverage.sh` unable to produce a
        // report from either Microsoft-Testing-Platform suite at all.
        //
        // It has to outrank TMPDIR rather than merely fill in for it: the four
        // TestBootstrap.cs files run inside a process that already has a
        // perfectly good TMPDIR, and it is precisely that one they must not
        // disturb. Since CB-306 it outranks the platform's per-user directory
        // too, which is what makes it a sandbox at all — the platform's answer
        // is the live directory a running Orbweaver is watching.
        Assert.Equal(
            "/sandbox",
            StatusDirectory.Root("/sandbox", "/set/by/the/caller", () => "/per/user", "/tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyStatusRootFallsThroughToTheRestOfTheOrder(string statusRoot)
    {
        // Same reasoning as the empty-TMPDIR case above: set-but-useless is not
        // a root, and joining a folder onto it would produce a relative path
        // resolved against whatever the working directory happens to be. The
        // platform is next in line, and TMPDIR after it.
        Assert.Equal(
            "/per/user",
            StatusDirectory.Root(statusRoot, "/set/by/the/caller", () => "/per/user", "/tmp"));
        Assert.Equal(
            "/set/by/the/caller",
            StatusDirectory.Root(statusRoot, "/set/by/the/caller", () => null, "/tmp"));
    }

    [Fact]
    public void TheStatusRootOverrideAloneIsEnough()
    {
        // The launchd shape — no TMPDIR at all — with the override set. The
        // platform must not be consulted: the override exists to keep a test
        // out of the live directory, and the platform's answer is the live
        // directory.
        var asked = 0;

        var root = StatusDirectory.Root("/sandbox", null, () => { asked++; return "/per/user"; }, "/tmp");

        Assert.Equal("/sandbox", root);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheFolderNameIsTheOneTheHooksWrite()
    {
        // Both halves of this agreement live in different languages — the hooks
        // are shell and PowerShell — so the name is worth pinning on this side
        // at least. tests/IntegrationTests drives the real hooks against a
        // scratch ORBWEAVER_STATUS_ROOT and would catch a change on the other
        // side.
        Assert.Equal("orbweaver", StatusDirectory.FolderName);
    }

    // The folder the pre-Orbweaver hooks write (CB-255 §1), watched beside the
    // new one until the cleanup after phase 3. Pinned to the old string: a
    // session alive across the upgrade is still running the old script, and
    // that script's folder name is history rather than a choice.
    [Fact]
    public void TheLegacyFolderNameIsTheOneTheOldHooksWrite()
    {
        Assert.Equal("claude_buddy", StatusDirectory.LegacyFolderName);
    }

    // **Same root, different last segment, always.** The old hook and the new
    // one resolve the root the same way, so a legacy folder resolved under any
    // other root would be watched and never written.
    [Theory]
    [InlineData("/var/folders/ab/T")]
    [InlineData("/tmp")]
    [InlineData("/sandbox/with space")]
    public void BothFoldersSitUnderTheSameRoot(string root)
    {
        var (current, legacy) = StatusDirectory.Under(root);

        Assert.Equal(Path.Combine(root, "orbweaver"), current);
        Assert.Equal(Path.Combine(root, "claude_buddy"), legacy);
        Assert.Equal(Path.GetDirectoryName(current), Path.GetDirectoryName(legacy));
    }

    // And the real pair agrees: whatever Root decides for this process —
    // here, the suite's own ORBWEAVER_STATUS_ROOT — Path() and LegacyPath()
    // are siblings in it. A negative control is built in: the two differ.
    [Fact]
    public void TheRealLegacyPathIsASiblingOfTheRealPath()
    {
        var current = StatusDirectory.Path();
        var legacy = StatusDirectory.LegacyPath();

        Assert.NotEqual(current, legacy);
        Assert.Equal(Path.GetDirectoryName(current), Path.GetDirectoryName(legacy));
        Assert.Equal("claude_buddy", Path.GetFileName(legacy));
    }
}
