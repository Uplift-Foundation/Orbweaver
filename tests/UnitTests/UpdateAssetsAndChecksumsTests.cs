using System.Runtime.InteropServices;
using Xunit;

namespace Orbweaver.Tests;

// Asset selection and SHA256SUMS parsing, against the real v1.0.3 release
// metadata and the real SHA256SUMS.txt captured from it.
public class UpdateAssetsAndChecksumsTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "update", name));

    private static UpdateRelease Real() => UpdateFeed.ParseLatest(Fixture("latest.json"))!;

    [Theory]
    [InlineData(true, Architecture.Arm64, "osx-arm64")]
    [InlineData(true, Architecture.X64, "osx-x64")]
    [InlineData(false, Architecture.X64, "win-x64")]
    [InlineData(false, Architecture.Arm64, "win-x64")]
    public void RidFollowsTheProcessNotTheHardware(bool mac, Architecture arch, string rid) =>
        Assert.Equal(rid, UpdateAssets.RidFor(mac, arch));

    [Fact]
    public void CurrentRidIsOneTheReleaseShips() =>
        Assert.Contains(UpdateAssets.CurrentRid(), new[] { "osx-arm64", "osx-x64", "win-x64" });

    [Theory]
    [InlineData("osx-arm64", "Orbweaver-1.0.3-osx-arm64.dmg")]
    [InlineData("osx-x64", "Orbweaver-1.0.3-osx-x64.dmg")]
    [InlineData("win-x64", "Orbweaver-1.0.3-win-x64-setup.exe")]
    public void ExpectedNamesMatchWhatReleaseYmlPublished(string rid, string name)
    {
        Assert.Equal(name, UpdateAssets.ExpectedAssetName("1.0.3", rid));
        // And the claim is checked against the real release, not just restated:
        Assert.Contains(Real().Assets, a => a.Name == name);
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    [InlineData("win-x64")]
    public void SelectFindsInstallerAndSumsInTheRealRelease(string rid)
    {
        var (installer, sums) = UpdateAssets.Select(Real(), rid);
        Assert.Equal(UpdateAssets.ExpectedAssetName("1.0.3", rid), installer.Name);
        Assert.Equal("SHA256SUMS.txt", sums.Name);
    }

    [Fact]
    public void SelectNeverPicksTheSpeechEngineZipAsTheInstaller()
    {
        var (installer, _) = UpdateAssets.Select(Real(), "win-x64");
        Assert.DoesNotContain("Speech", installer.Name);
    }

    [Fact]
    public void AVersionThatWouldEscapeTheUpdatesFolderIsRefusedBeforeAnythingIsWritten()
    {
        // Constructed directly: UpdateFeed normalises the version so a real feed
        // cannot produce this, but Select must hold on its own. Control beside it.
        var hostile = new UpdateRelease("v1.0.4", "1.0.4+a/../../x", false, "",
            [new UpdateAsset("Orbweaver-1.0.4+a/../../x-osx-arm64.dmg", "http://x/y", 1), new UpdateAsset("SHA256SUMS.txt", "http://x/s", 1)]);
        var ex = Assert.Throws<UpdateException>(() => UpdateAssets.Select(hostile, "osx-arm64"));
        Assert.Equal(UpdateFailure.UnsafeName, ex.Kind);

        var normal = new UpdateRelease("v1.0.4", "1.0.4", false, "",
            [new UpdateAsset("Orbweaver-1.0.4-osx-arm64.dmg", "http://x/y", 1), new UpdateAsset("SHA256SUMS.txt", "http://x/s", 1)]);
        Assert.Equal("Orbweaver-1.0.4-osx-arm64.dmg", UpdateAssets.Select(normal, "osx-arm64").Installer.Name);
    }

    [Theory]
    [InlineData("Orbweaver-1.0.4-osx-arm64.dmg", true)]
    [InlineData("a.b-c_d", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("../x", false)]
    [InlineData("a\\b", false)]
    [InlineData("a\0b", false)]
    public void PlainFileNameRule(string name, bool plain) =>
        Assert.Equal(plain, UpdateAssets.IsPlainFileName(name));

    [Fact]
    public void AMissingInstallerIsATypedFailure()
    {
        var r = Real() with { Assets = Real().Assets.Where(a => !a.Name.EndsWith(".dmg")).ToList() };
        var ex = Assert.Throws<UpdateException>(() => UpdateAssets.Select(r, "osx-arm64"));
        Assert.Equal(UpdateFailure.NoInstaller, ex.Kind);
    }

    [Fact]
    public void AMissingSumsFileIsATypedFailureEvenWithTheInstallerPresent()
    {
        var r = Real() with { Assets = Real().Assets.Where(a => a.Name != "SHA256SUMS.txt").ToList() };
        var ex = Assert.Throws<UpdateException>(() => UpdateAssets.Select(r, "osx-arm64"));
        Assert.Equal(UpdateFailure.NoChecksums, ex.Kind);
    }

    // ---- checksums -----------------------------------------------------------

    [Fact]
    public void ParsesTheRealSumsFile()
    {
        var map = UpdateChecksums.Parse(Fixture("SHA256SUMS.txt"));
        Assert.Equal(6, map.Count);
        Assert.Equal("e61c8c2a3f5dedc67c2e67fb01359f90b1a9fb1d198019108239ec385152be52", map["Orbweaver-1.0.3-osx-arm64.dmg"]);
        Assert.Equal(map["Orbweaver-1.0.3-win-x64-setup.exe"], UpdateChecksums.Expect(Fixture("SHA256SUMS.txt"), "Orbweaver-1.0.3-win-x64-setup.exe"));
    }

    private const string H1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string H2 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void ToleratesCrlfBinaryMarkerBlankLinesAndUppercaseHashes()
    {
        var map = UpdateChecksums.Parse($"{H1}  a.dmg\r\n\r\n{H2} *b.exe\r\n");
        Assert.Equal(H1, map["a.dmg"]);
        Assert.Equal(H2.ToLowerInvariant(), map["b.exe"]);
    }

    [Fact]
    public void NamesAreCaseSensitive() =>
        Assert.False(UpdateChecksums.Parse($"{H1}  A.dmg").ContainsKey("a.dmg"));

    [Fact]
    public void TheSameNameTwiceWithTheSameHashIsHarmless() =>
        Assert.Single(UpdateChecksums.Parse($"{H1}  a.dmg\n{H1}  a.dmg\n"));

    [Theory]
    [InlineData("deadbeef  a.dmg")]                    // too short
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz  a.dmg")]
    [InlineData("<html>404</html>")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // no name
    public void AMalformedLineRejectsTheWholeFile(string text)
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateChecksums.Parse(text));
        Assert.Equal(UpdateFailure.ChecksumUnavailable, ex.Kind);
    }

    [Fact]
    public void TheSameNameWithTwoHashesIsAmbiguousAndRejected()
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateChecksums.Parse($"{H1}  a.dmg\n{H2}  a.dmg\n"));
        Assert.Equal(UpdateFailure.ChecksumUnavailable, ex.Kind);
    }

    [Fact]
    public void AMissingEntryIsAFailureNotAPass()
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateChecksums.Expect($"{H1}  a.dmg", "b.dmg"));
        Assert.Equal(UpdateFailure.ChecksumUnavailable, ex.Kind);
    }

    [Fact]
    public async Task VerifyAcceptsTheRightHashInEitherCaseAndRejectsAWrongOne()
    {
        var path = Path.Combine(Path.GetTempPath(), "upd-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "hello");
            const string hello = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

            await UpdateChecksums.VerifyAsync(path, hello, default);
            await UpdateChecksums.VerifyAsync(path, hello.ToUpperInvariant(), default);

            var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateChecksums.VerifyAsync(path, H1, default));
            Assert.Equal(UpdateFailure.ChecksumMismatch, ex.Kind);
        }
        finally { File.Delete(path); }
    }
}
