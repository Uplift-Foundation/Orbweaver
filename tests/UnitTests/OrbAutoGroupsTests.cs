using Xunit;

namespace Orbweaver.Tests;

// CB-281: the pure classifier. One case per outcome of each attribute, because the
// mistake it can make is an orb in a group it does not belong to, or in none.
public class OrbAutoGroupsTests
{
    private const string Home = "/Users/ann";

    private static AutoGroupFacts Facts(
        SessionSource source = SessionSource.ClaudeCode, string remoteCli = "", string cwd = "",
        string? configRoot = null, string? accountKey = null, string? accountLabel = null, string? repoRoot = null) =>
        new(source, remoteCli, cwd, configRoot, accountKey, accountLabel, repoRoot);

    // ---- Off ------------------------------------------------------------------

    [Fact]
    public void OffClassifiesNothing() =>
        Assert.Null(OrbAutoGroups.Classify(AutoGroupBy.Off, Facts(cwd: "/a"), Home));

    // ---- CLI ------------------------------------------------------------------

    [Theory]
    [InlineData(SessionSource.ClaudeCode, "", "claude", "Claude")]
    [InlineData(SessionSource.ClaudeCloud, "", "claude", "Claude")]
    [InlineData(SessionSource.Codex, "", "codex", "Codex")]
    [InlineData(SessionSource.Grok, "", "grok", "Grok")]
    [InlineData(SessionSource.OpenClaw, "", "openclaw", "OpenClaw")]
    [InlineData(SessionSource.RemoteControl, "codex", "codex", "Codex")]
    [InlineData(SessionSource.RemoteControl, " GROK ", "grok", "Grok")]
    [InlineData(SessionSource.RemoteControl, "claude", "claude", "Claude")]
    [InlineData(SessionSource.RemoteControl, "something-new", "claude", "Claude")]
    [InlineData(SessionSource.RemoteControl, null, "claude", "Claude")]
    public void CliNamesTheCliAndAnUnknownRemoteOneIsClaude(SessionSource source, string? remote, string key, string name)
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Cli, Facts(source, remote!), Home);
        Assert.Equal(new AutoGroupValue(key, name), value);
    }

    // ---- Directory ------------------------------------------------------------

    [Fact]
    public void DirectoryPrefersTheRepositoryRootOverTheCwd()
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Directory,
            Facts(cwd: "/Users/ann/Orbweaver/tests", repoRoot: "/Users/ann/Orbweaver"), Home);
        Assert.Equal(new AutoGroupValue("/users/ann/orbweaver", "Orbweaver"), value);
    }

    [Fact]
    public void DirectoryFallsBackToTheCwdOutsideARepositoryKeepingTheFolderCaseInTheName()
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Directory, Facts(cwd: "/Users/ann/Scratch/"), Home);
        Assert.Equal(new AutoGroupValue("/users/ann/scratch", "Scratch"), value);
    }

    [Fact]
    public void DirectoryReadsAWindowsPathTheSameAsItsForwardSlashTwin()
    {
        var windows = OrbAutoGroups.Classify(AutoGroupBy.Directory, Facts(cwd: @"C:\Src\Repo\"), Home);
        var unix = OrbAutoGroups.Classify(AutoGroupBy.Directory, Facts(cwd: "c:/src/repo"), Home);
        Assert.Equal(unix!.Value.Key, windows!.Value.Key);
        Assert.Equal("Repo", windows.Value.Name);
    }

    [Fact]
    public void ARootFolderIsNamedByItself()
    {
        Assert.Equal("/", OrbAutoGroups.Classify(AutoGroupBy.Directory, Facts(cwd: "/"), Home)!.Value.Name);
        Assert.Equal("C:", OrbAutoGroups.Classify(AutoGroupBy.Directory, Facts(cwd: "C:"), Home)!.Value.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DirectoryHasNoValueForAnEmptyCwdAndNoRoot(string cwd) =>
        Assert.Null(OrbAutoGroups.Classify(AutoGroupBy.Directory, Facts(SessionSource.OpenClaw, cwd: cwd), Home));

    // ---- Profile --------------------------------------------------------------

    [Fact]
    public void ProfileNamesTheDefaultRootDefault()
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Profile, Facts(configRoot: "/Users/ann/.claude"), Home);
        Assert.Equal(new AutoGroupValue("/users/ann/.claude", "default"), value);
    }

    [Fact]
    public void ProfileNamesAnotherRootByItsLabel()
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Profile, Facts(configRoot: "/Users/ann/.claude-board"), Home);
        Assert.Equal(new AutoGroupValue("/users/ann/.claude-board", "board"), value);
    }

    [Fact]
    public void ProfileAppliesToACloudSessionToo()
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Profile,
            Facts(SessionSource.ClaudeCloud, configRoot: "/Users/ann/.claude-work"), Home);
        Assert.Equal("work", value!.Value.Name);
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void ProfileIsClaudeOnly(SessionSource source) =>
        Assert.Null(OrbAutoGroups.Classify(AutoGroupBy.Profile, Facts(source, configRoot: "/Users/ann/.claude"), Home));

    [Fact]
    public void ProfileHasNoValueWithoutARoot() =>
        Assert.Null(OrbAutoGroups.Classify(AutoGroupBy.Profile, Facts(configRoot: null), Home));

    // ---- Account --------------------------------------------------------------

    [Fact]
    public void AccountUsesTheGivenIdentityAndLabel()
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Account,
            Facts(accountKey: "claude\nann@example.org", accountLabel: "ann"), Home);
        Assert.Equal(new AutoGroupValue("claude\nann@example.org", "ann"), value);
    }

    [Fact]
    public void AccountWithoutALabelIsStillGroupedUnderAGenericName()
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Account, Facts(accountKey: "k", accountLabel: " "), Home);
        Assert.Equal("account", value!.Value.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void AccountHasNoValueWhenTheIdentityIsUnknown(string? key) =>
        Assert.Null(OrbAutoGroups.Classify(AutoGroupBy.Account, Facts(accountKey: key), Home));

    // ---- ids ------------------------------------------------------------------

    [Fact]
    public void AnIdIsAStableFunctionOfTheAttributeAndTheKey()
    {
        var id = OrbAutoGroups.IdFor(AutoGroupBy.Directory, "/users/ann/orbweaver");
        Assert.Equal(id, OrbAutoGroups.IdFor(AutoGroupBy.Directory, "/users/ann/orbweaver"));
        Assert.Matches("^a-dir-[0-9a-f]{12}$", id);
    }

    [Fact]
    public void IdsDifferByKeyAndByAttribute()
    {
        var a = OrbAutoGroups.IdFor(AutoGroupBy.Cli, "claude");
        Assert.NotEqual(a, OrbAutoGroups.IdFor(AutoGroupBy.Cli, "codex"));
        Assert.NotEqual(a, OrbAutoGroups.IdFor(AutoGroupBy.Profile, "claude"));
        Assert.StartsWith("a-cli-", a);
        Assert.StartsWith("a-profile-", OrbAutoGroups.IdFor(AutoGroupBy.Profile, "x"));
        Assert.StartsWith("a-account-", OrbAutoGroups.IdFor(AutoGroupBy.Account, "x"));
    }

    [Theory]
    [InlineData("a-dir-1a2b3c4d5e6f", true)]
    [InlineData("g-0a1b2c3d", false)]
    [InlineData("main", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAutoRecognisesTheAutoPrefixOnly(string? id, bool expected) =>
        Assert.Equal(expected, OrbAutoGroups.IsAuto(id));

    [Theory]
    [InlineData("a-dir-1a2b3c4d5e6f", "Directory")]
    [InlineData("a-cli-1a2b3c4d5e6f", "Cli")]
    [InlineData("a-profile-1a2b3c4d5e6f", "Profile")]
    [InlineData("a-account-1a2b3c4d5e6f", "Account")]
    public void AttributeOfReadsTheMiddleSegment(string id, string expected) =>
        Assert.Equal(Enum.Parse<AutoGroupBy>(expected), OrbAutoGroups.AttributeOf(id));

    [Theory]
    [InlineData("g-0a1b2c3d")]
    [InlineData(null)]
    [InlineData("a-zzz-1a2b3c4d5e6f")]
    [InlineData("a-dir")]
    [InlineData("a-dir-1-2")]
    public void AttributeOfIsNullForAnythingElse(string? id) => Assert.Null(OrbAutoGroups.AttributeOf(id));

    [Theory]
    [InlineData("Off", "off")]
    [InlineData("Directory", "directory")]
    [InlineData("Cli", "cli")]
    [InlineData("Profile", "profile")]
    [InlineData("Account", "account")]
    public void NameAndParseRoundTrip(string byName, string text)
    {
        var by = Enum.Parse<AutoGroupBy>(byName);
        Assert.Equal(text, OrbAutoGroups.Name(by));
        Assert.Equal(by, OrbAutoGroups.Parse(text));
        Assert.Equal(by, OrbAutoGroups.Parse(" " + text.ToUpperInvariant() + " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    public void JunkParsesAsOff(string? text) => Assert.Equal(AutoGroupBy.Off, OrbAutoGroups.Parse(text));
}
