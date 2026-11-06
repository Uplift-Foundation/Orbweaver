using Xunit;

namespace Orbweaver.Tests;

// The comparator the updater trusts to never offer a beta to a stable user (or
// the reverse). Cases are the ordering rules from semver.org §11 plus the two
// real tags this project has shipped.
public class UpdateVersionTests
{
    private static UpdateVersion V(string s)
    {
        Assert.True(UpdateVersion.TryParse(s, out var v), s);
        return v!;
    }

    [Theory]
    [InlineData("1.0.3", "1.0.3-beta")]          // a prerelease sorts below its release
    [InlineData("1.0.3-beta", "0.5.11-beta")]    // the real pair: a stable build outranks old betas
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9")]    // numeric identifiers compare as numbers
    [InlineData("1.0.0-alpha", "1.0.0-1")]       // numeric sorts below alphanumeric
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")] // more identifiers wins when the rest ties
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-99999999999999999999999", "1.0.0-9999999999999999999999")] // wider than long
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.2.0", "1.1.99")]
    [InlineData("1.0.10", "1.0.9")]
    public void TheFirstIsNewerThanTheSecond(string newer, string older)
    {
        Assert.True(UpdateVersion.Compare(V(newer), V(older)) > 0);
        Assert.True(UpdateVersion.Compare(V(older), V(newer)) < 0);
    }

    [Theory]
    [InlineData("1.0.3", "v1.0.3")]
    [InlineData("1.0.3", "1.0.3+abc123")]        // build metadata is ignored
    [InlineData("1.0.3-beta+x", "1.0.3-beta+y")]
    [InlineData("1.0.0-007", "1.0.0-7")]         // leading zeros on numeric identifiers
    [InlineData("1.0.0-beta.2", "1.0.0-beta.2")]
    public void TheseAreEqual(string a, string b) =>
        Assert.Equal(0, UpdateVersion.Compare(V(a), V(b)));

    [Fact]
    public void ParsesEveryField()
    {
        var v = V(" V10.20.30-rc.1+build ");
        Assert.Equal((10L, 20L, 30L), (v.Major, v.Minor, v.Patch));
        Assert.Equal(new[] { "rc", "1" }, v.Prerelease);
        Assert.Empty(V("1.0.3").Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("a.b.c")]
    [InlineData("1.0.x")]
    [InlineData("-1.0.0")]
    [InlineData("1..0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-be..ta")]
    [InlineData("1.0.0-be ta")]
    [InlineData("1.+5.0")]
    [InlineData("99999999999999999999.0.0")]
    public void RejectsWhatIsNotAVersion(string? text)
    {
        Assert.False(UpdateVersion.TryParse(text, out var v));
        Assert.Null(v);
    }
}
