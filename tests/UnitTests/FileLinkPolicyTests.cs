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

    // One row per allowlisted family (documents, text and data, images,
    // audio, video, office, mail), upper-cased too: the allowlist is the only
    // way to Open.
    [Theory]
    [InlineData(".pdf")] [InlineData(".txt")] [InlineData(".md")] [InlineData(".markdown")] [InlineData(".rtf")]
    [InlineData(".csv")] [InlineData(".tsv")] [InlineData(".json")] [InlineData(".log")] [InlineData(".xml")]
    [InlineData(".yaml")] [InlineData(".yml")]
    [InlineData(".png")] [InlineData(".jpg")] [InlineData(".jpeg")] [InlineData(".gif")] [InlineData(".webp")]
    [InlineData(".heic")] [InlineData(".bmp")] [InlineData(".tiff")] [InlineData(".tif")]
    [InlineData(".mp3")] [InlineData(".m4a")] [InlineData(".wav")] [InlineData(".mp4")] [InlineData(".mov")] [InlineData(".m4v")]
    [InlineData(".docx")] [InlineData(".xlsx")] [InlineData(".pptx")] [InlineData(".odt")] [InlineData(".ods")]
    [InlineData(".odp")] [InlineData(".pages")] [InlineData(".numbers")] [InlineData(".key")] [InlineData(".eml")]
    public void EveryAllowlistedExtensionOpens(string extension)
    {
        Assert.Equal(FileLinkAction.Open, Mac("/a/x" + extension));
        Assert.Equal(FileLinkAction.Open, Mac("/a/x" + extension.ToUpperInvariant()));
        Assert.Equal(FileLinkAction.Open, Win("C:\\a\\x" + extension));
    }

    // The controls, one per class: script in a browser, a script host, a Windows
    // script/shell/console type, an installer, a disk image, a macro Office
    // file, a bundle, and names with no usable extension. Each is Reveal, and
    // each sits beside the .pdf that opens.
    [Theory]
    [InlineData("/a/x.html")] [InlineData("/a/x.htm")] [InlineData("/a/x.xhtml")] [InlineData("/a/x.svg")]
    [InlineData("/a/x.webarchive")]
    [InlineData("/a/x.py")] [InlineData("/a/x.pyw")] [InlineData("/a/x.rb")] [InlineData("/a/x.php")]
    [InlineData("/a/x.vbe")] [InlineData("/a/x.jse")] [InlineData("/a/x.msc")] [InlineData("/a/x.chm")]
    [InlineData("/a/x.jnlp")] [InlineData("/a/x.msix")] [InlineData("/a/x.xll")] [InlineData("/a/x.iqy")]
    [InlineData("/a/x.iso")] [InlineData("/a/x.img")] [InlineData("/a/x.vhd")] [InlineData("/a/x.dmg")]
    [InlineData("/a/x.docm")] [InlineData("/a/x.xlsm")] [InlineData("/a/x.pptm")] [InlineData("/a/x.dotm")]
    [InlineData("/a/x.exe")] [InlineData("/a/x.sh")] [InlineData("/a/x.command")] [InlineData("/a/x.app")]
    [InlineData("/a/x.ps1")] [InlineData("/a/x.bat")] [InlineData("/a/x.lnk")] [InlineData("/a/x.url")]
    [InlineData("/a/x.webloc")] [InlineData("/a/x.jar")] [InlineData("/a/x.js")]
    [InlineData("/a/noextension")]
    [InlineData("/a/.hidden")]
    [InlineData("/a.pdf/noextension")]   // the dot belongs to the folder
    [InlineData("/a/x.pdfx")]            // near-miss of .pdf
    [InlineData("/a/x.")]
    public void AnythingNotOnTheAllowlistIsRevealedNotOpened(string path)
    {
        Assert.Equal(FileLinkAction.Reveal, Mac(path));
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.pdf"));
    }

    [Fact]
    public void AnUppercaseDeniedSpellingIsStillRevealed()
    {
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x.PDF.EXE"));
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x.Exe"));
        Assert.Equal(FileLinkAction.Reveal, Win("C:\\a\\x.HTML"));
    }

    [Fact]
    public void ADoubleExtensionIsJudgedByTheLastOne()
    {
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.pdf"));
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x.pdf.exe"));
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.exe.pdf"));
    }

    // A directory with an allowlisted name is still a directory.
    [Fact]
    public void ADirectoryNamedLikeAPdfIsRevealed()
    {
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x.pdf", new FileFacts(true, true, false)));
        Assert.Equal(FileLinkAction.Open, Mac("/a/x.pdf"));
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

    // On a Mac a trailing dot is not refused (that is a Windows rule), but the
    // extension is then "." and off the allowlist, so it is shown, not opened.
    [Fact]
    public void ATrailingDotIsOnlyRefusedOnWindows()
    {
        Assert.Equal(FileLinkAction.Reveal, Mac("/a/x.pdf."));
        Assert.Equal(FileLinkAction.Refuse, Win("C:\\a\\x.pdf."));
    }

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
        Assert.NotEmpty(FileLinkPolicy.Decide(new FileLink("/a/x.html", false), Plain, false).Reason);
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

    // The extension rule reads both names: a link named x.pdf is only as safe
    // as what it points at.
    [Fact]
    public void ASymlinkIsJudgedByItsTargetAsWellAsItsName()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Temp();
        try
        {
            var d = dir;
            var yPdf = Path.Combine(d, "y.pdf");
            var yCommand = Path.Combine(d, "y.command");
            var yHtml = Path.Combine(d, "y.html");
            var bundle = Path.Combine(d, "dir.app");
            foreach (var f in new[] { yPdf, yCommand, yHtml }) File.WriteAllText(f, "x");
            Directory.CreateDirectory(bundle);

            string Link(string name, string target)
            {
                var l = Path.Combine(d, name);
                File.CreateSymbolicLink(l, target);
                return l;
            }

            FileLinkAction Act(string path)
            {
                var facts = FileFacts.Probe(path);
                return FileLinkPolicy.Decide(new FileLink(path, false), facts, false).Action;
            }

            // Control: a link to a pdf opens, and carries its target.
            var good = Link("x.pdf", yPdf);
            Assert.Equal(FileLinkAction.Open, Act(good));
            Assert.Equal(Path.GetFullPath(yPdf), FileFacts.Probe(good).Target);
            Assert.Null(FileFacts.Probe(yPdf).Target);

            Assert.Equal(FileLinkAction.Reveal, Act(Link("c.pdf", yCommand)));
            Assert.Equal(FileLinkAction.Reveal, Act(Link("h.pdf", yHtml)));
            Assert.Equal(FileLinkAction.Reveal, Act(Link("b.pdf", bundle)));
            Assert.Equal(FileLinkAction.Reveal, Act(Link("l.pdf", "/bin/ls")));

            // A link to a link: the final target counts.
            Assert.Equal(FileLinkAction.Reveal, Act(Link("chain.pdf", Link("hop.pdf", yCommand))));

            // Dangling and looping links name nothing: refused.
            Assert.Equal(FileLinkAction.Refuse, Act(Link("dangling.pdf", Path.Combine(d, "nope.pdf"))));
            var loopA = Path.Combine(d, "la.pdf");
            var loopB = Path.Combine(d, "lb.pdf");
            File.CreateSymbolicLink(loopA, loopB);
            File.CreateSymbolicLink(loopB, loopA);
            Assert.Equal(FileLinkAction.Refuse, Act(loopA));
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

            // A link named like a pdf that points at a bundle directory is
            // judged by the target: a directory, so Reveal.
            var bundle = Path.Combine(dir, "Thing.app");
            var bundleLink = Path.Combine(dir, "report.pdf.link");
            Directory.CreateDirectory(bundle);
            File.CreateSymbolicLink(bundleLink, bundle);
            Assert.True(FileFacts.Probe(bundleLink).IsDirectory);
        }
        finally { Directory.Delete(dir, true); }
    }
}
