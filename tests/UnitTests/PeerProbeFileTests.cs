using Xunit;

namespace Orbweaver.Tests;

// The arguments and the printed answer of `peer-probe ... read_file`
// (CB-363). The E2E leans on both: the positive run compares a checksum, the
// negative control needs a refusal code and a non-zero exit.
public class PeerProbeFileTests
{
    [Fact]
    public void ASessionAndAPathParse()
    {
        var (request, error) = PeerProbeFile.Parse(["evidence-5d", "/Users/x/a.pdf"]);
        Assert.Null(error);
        Assert.Equal(new PeerProbeFile.Request("evidence-5d", "/Users/x/a.pdf", null, null), request);
    }

    [Fact]
    public void OutAndHaveCanComeInAnyPosition()
    {
        var (request, error) = PeerProbeFile.Parse(["--out", "/tmp/o", "s", "--have", "5-9", "/a/b.pdf"]);
        Assert.Null(error);
        Assert.Equal(new PeerProbeFile.Request("s", "/a/b.pdf", "/tmp/o", "5-9"), request);
    }

    [Theory]
    [InlineData]
    [InlineData("only-a-session")]
    [InlineData("s", "/a", "extra")]
    public void TheWrongNumberOfPositionalsIsAUsageError(params string[] args)
    {
        var (request, error) = PeerProbeFile.Parse(args);
        Assert.Null(request);
        Assert.Contains("session name and a path", error);
    }

    [Theory]
    [InlineData("--out")]
    [InlineData("--have")]
    public void AFlagWithNoValueIsAUsageError(string flag)
    {
        var (request, error) = PeerProbeFile.Parse(["s", "/a", flag]);
        Assert.Null(request);
        Assert.Equal($"{flag} needs a value", error);
    }

    [Fact]
    public void BytesPrintOutcomeSizeStampAndChecksum()
    {
        var data = "hello"u8.ToArray();
        var lines = PeerProbeFile.Describe(new PeerFileResult.Bytes(data, 1234, 5, Exec: true));

        Assert.Equal(
            ["outcome=ok", "size=5", "mtime=1234", "exec=1", "stamp=1234-5",
             "sha256=2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"],
            lines);
        Assert.Equal("exec=0", PeerProbeFile.Describe(new PeerFileResult.Bytes(data, 1, 5, false))[3]);
    }

    [Fact]
    public void SameAndErrorsPrintTheirOwnLines()
    {
        Assert.Equal(["outcome=same"], PeerProbeFile.Describe(new PeerFileResult.Same()));
        Assert.Equal(["outcome=err", "code=forbidden_path"],
            PeerProbeFile.Describe(new PeerFileResult.Error(MirrorProtocol.ErrForbiddenPath, null)));
        Assert.Equal(["outcome=err", "code=too_large", "reason=big"],
            PeerProbeFile.Describe(new PeerFileResult.Error("too_large", "big")));
    }

    // The path never appears in what is printed: the output is pasted into
    // tickets and a file name is somebody's content.
    [Fact]
    public void NoLineEchoesThePath()
    {
        var lines = PeerProbeFile.Describe(new PeerFileResult.Error("not_found", null));
        Assert.DoesNotContain(lines, l => l.Contains('/'));
    }

    [Fact]
    public void ARefusalExitsNonZeroAndEverythingElseZero()
    {
        Assert.Equal(1, PeerProbeFile.ExitCodeOf(new PeerFileResult.Error("x", null)));
        Assert.Equal(0, PeerProbeFile.ExitCodeOf(new PeerFileResult.Same()));
        Assert.Equal(0, PeerProbeFile.ExitCodeOf(new PeerFileResult.Bytes([], 0, 0, false)));
    }

    [Fact]
    public void TheDefaultOutPathIsNamedByTheChecksum() =>
        Assert.Equal(Path.Combine("/tmp", "peer-probe-2cf24dba5fb0"),
            PeerProbeFile.DefaultOutPath("/tmp", "hello"u8.ToArray()));
}
