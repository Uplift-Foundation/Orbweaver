using Xunit;

namespace Orbweaver.Tests;

// What a click on a file link may do (CB-363): open it, show it, or refuse.
//
// The deny list is only meaningful beside its controls, so every extension row
// is paired with a harmless sibling, and the one place the spelling is the
// attack (trailing dot, double extension) is tested in both directions.
public class FileLinkPolicyTests
{
    private static readonly FileFacts Plain = new(Exists: true, IsDirectory: false, Executable: false);
    private static readonly FileFacts Missing = new(false, false, false);

    private static FileLinkAction Mac(string path, FileFacts? facts = null) =>
        FileLinkPolicy.Decide(new FileLink(path, false), facts ?? Plain, isWindows: false).Action;

    private static FileLinkAction Win(string path, FileFacts? facts = null) =>
        FileLinkPolicy.Decide(new FileLink(path, true), facts ?? Plain, isWindows: true).Action;

    [Theory]
    [InlineData("/a/x.pdf")]
    [InlineData("/a/x.PDF")]
    [InlineData("/a/README.md")]
    [InlineData("/a/x.exe.pdf")]       // reads as a pdf
    [InlineData("/a/noextension")]
    [InlineData("/a/.hidden")]         // ".hidden" is an extension-only name; not on the list
    [InlineData("/a.b/noextension")]   // the dot belongs to the folder
    [InlineData("/a/x.shx")]           // near-miss of .sh
    [InlineData("/a/x.jsx")]           // near-miss of .js
    public void OrdinaryFilesOpen(string path) =>
        Assert.Equal(FileLinkAction.Open, Mac(path));

    [Theory]
    [InlineData(".app")] [InlineData(".command")] [InlineData(".sh")] [InlineData(".exe")] [InlineData(".bat")]
    [InlineData(".cmd")] [InlineData(".ps1")] [InlineData(".lnk")] [InlineData(".scpt")] [InlineData(".pkg")]
    [InlineData(".msi")] [InlineData(".jar")] [InlineData(".js")] [InlineData(".vbs")] [InlineData(".wsf")]
    [InlineData(".hta")] [InlineData(".scr")] [InlineData(".com")] [InlineData(".pif")] [InlineData(".reg")]
    [InlineData(".cpl")] [InlineData(".dll")] [InlineData(".url")] [InlineData(".terminal")] [InlineData(".tool")]
    [InlineData(".workflow")] [InlineData(".action")] [InlineData(".inetloc")] [InlineData(".fileloc")]
    [InlineData(".webloc")] [InlineData(".dmg")] [InlineData(".desktop")]
    public void EveryDeniedExtensionIsRevealedNotOpened(string extension)
    {
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x" + extension));
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x" + extension.ToUpperInvariant()));
        Assert.Equal(FileLinkAction.Reveal, Win("C:\\a\\x" + extension));
        // The control: the same stem with an ordinary extension opens.
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.pdf"));
    }

    [Fact]
    public void ADoubleExtensionIsJudgedByTheLastOne()
    {
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.pdf"));
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x.pdf.exe"));
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.exe.pdf"));
    }

    [Fact]
    public void ADirectoryIsRevealedEvenWithAnOrdinaryName()
    {
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/Folder", new FileFacts(true, true, false)));
        Assert.Equal(FileLinkAction.Open, Mac("/a/Folder"));
    }

    [Fact]
    public void TheExecuteBitRevealsAnOtherwiseOrdinaryFile()
    {
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x.pdf", new FileFacts(true, false, true)));
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.pdf", new FileFacts(true, false, false)));
    }

    [Fact]
    public void AMissingFileIsRefusedWhateverItsName()
    {
        Assert.Equal(FileLinkAction.Refuse, Mac("/a/x.pdf", Missing));
        Assert.Equal(FileLinkAction.Refuse, Mac("/a/x.exe", Missing));
        Assert.Equal(FileLinkAction.Refuse, Win("C:\\a\\x.pdf", Missing));
    }

    // Windows strips a trailing dot or space when it opens a name, so these
    // would run as x.exe; the pair is the same file without the dot.
    [Theory]
    [InlineData("C:\\a\\x.exe.")]
    [InlineData("C:\\a\\x.exe ")]
    [InlineData("C:\\a\\x.pdf.")]
    [InlineData("C:\\a.\\x.pdf")]
    [InlineData("C:\\a \\x.pdf")]
    public void WindowsTrailingDotsAndSpacesAreRefused(string path)
    {
        Assert.Equal(FileLinkAction.Refuse, Win(path));
        Assert.Equal(FileLinkAction.Open, Win("C:\\a\\x.pdf"));
    }

    // On a Mac a trailing dot is just a name.
    [Fact]
    public void ATrailingDotIsOnlyAWindowsProblem() =>
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.pdf."));

    [Fact]
    public void ASeparatorRunInsideThePathDoesNotTripTheTrailingRule() =>
        Assert.Equal(FileLinkAction.Open, Win("C:\\a\\\\x.pdf"));

    // A path for the other platform names nothing on this one.
    [Fact]
    public void APathOfTheOtherPlatformIsRefused()
    {
        Assert.Equal(FileLinkAction.Refuse, FileLinkPolicy.Decide(new FileLink("C:\\a\\x.pdf", true), Plain, isWindows: false).Action);
        Assert.Equal(FileLinkAction.Refuse, FileLinkPolicy.Decide(new FileLink("/a/x.pdf", false), Plain, isWindows: true).Action);
    }

    // A link built by hand (not through TryParse) with a network path or a
    // shape FromDecodedPath refuses is refused too.
    [Theory]
    [InlineData("//server/share/x.pdf", false)]
    [InlineData("\\\\server\\share\\x.pdf", true)]
    [InlineData("relative.pdf", false)]
    [InlineData("/a/x\u202E.pdf", false)]
    public void AHandBuiltNetworkOrMalformedLinkIsRefused(string path, bool windows)
    {
        var verdict = FileLinkPolicy.Decide(new FileLink(path, windows), Plain, windows);
        Assert.Equal(FileLinkAction.Refuse, verdict.Action);
        Assert.NotEmpty(verdict.Reason);
    }

    [Fact]
    public void EveryNonOpenVerdictSaysWhy()
    {
        Assert.NotEmpty(FileLinkPolicy.Decide(new FileLink("/a/x.exe", false), Plain, false).Reason);
        Assert.NotEmpty(FileLinkPolicy.Decide(new FileLink("/a/x.pdf", false), Missing, false).Reason);
        Assert.NotEmpty(FileLinkPolicy.Decide(new FileLink("/a/d", false), new FileFacts(true, true, false), false).Reason);
        Assert.NotEmpty(FileLinkPolicy.Decide(new FileLink("/a/x", false), new FileFacts(true, false, true), false).Reason);
        Assert.Empty(FileLinkPolicy.Decide(new FileLink("/a/x.pdf", false), Plain, false).Reason);
    }

    // --- Probe, against a real temp tree ---

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orb-filelink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ProbeSeesAFileADirectoryAndNothing()
    {
        var dir = Temp();
        try
        {
            var file = Path.Combine(dir, "a.pdf");
            File.WriteAllText(file, "x");

            Assert.Equal(new FileFacts(true, false, false), FileFacts.Probe(file));
            Assert.Equal(new FileFacts(true, true, false), FileFacts.Probe(dir));
            Assert.Equal(new FileFacts(false, false, false), FileFacts.Probe(Path.Combine(dir, "missing.pdf")));
            Assert.Equal(new FileFacts(false, false, false), FileFacts.Probe(Path.Combine(dir, "bad\0name")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ProbeReadsTheExecuteBitThroughASymlinkOnPosix()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Temp();
        try
        {
            var plain = Path.Combine(dir, "plain.pdf");
            var exec = Path.Combine(dir, "run.pdf");
            var link = Path.Combine(dir, "link.pdf");
            File.WriteAllText(plain, "x");
            File.WriteAllText(exec, "x");
            File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(exec, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupExecute);
            File.CreateSymbolicLink(link, exec);

            Assert.False(FileFacts.Probe(plain).Executable);
            Assert.True(FileFacts.Probe(exec).Executable);
            Assert.True(FileFacts.Probe(link).Executable);
        }
        finally { Directory.Delete(dir, true); }
    }
}
