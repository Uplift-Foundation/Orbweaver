using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// PersonaPool: where the shared pool is, and what a team member is looked up as;
// and the front-matter read that lets profile-gen's fixed persona/persona.md be
// offered only to the persona it names.
public class PersonaPoolTests
{
    // Rooted on every platform: "/pool" is not rooted on Windows and "C:\pool"
    // is not on Unix, and which one a test uses decides whether it tests the
    // rule or the machine.
    private static readonly string Home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb-pool-home"));
    private static readonly string Pool = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb-pool-shared"));
    private static readonly string Other = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb-pool-other"));

    // --- SharedRoot ----------------------------------------------------------------

    [Fact]
    public void TheEnvironmentOutranksTheSettingAndBothOutrankTheDefault()
    {
        Assert.Equal(Pool, PersonaPool.SharedRoot(Pool, Other, Home));
        Assert.Equal(Other, PersonaPool.SharedRoot(null, Other, Home));
        Assert.Equal(Other, PersonaPool.SharedRoot("   ", Other, Home));
        Assert.Equal(Path.Combine(Home, ".orbweaver"), PersonaPool.SharedRoot(null, null, Home));
        Assert.Equal(Path.Combine(Home, ".orbweaver"), PersonaPool.SharedRoot("", " ", Home));
    }

    [Fact]
    public void WithNothingChosenAndNoHomeThereIsNoPool()
    {
        Assert.Null(PersonaPool.SharedRoot(null, null, null));
        Assert.Null(PersonaPool.SharedRoot(null, null, "  "));
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/shared", "shared")]
    [InlineData("~\\shared", "shared")]
    public void ATildeIsTheHomeDirectoryBecauseThatIsWhatAPersonMeansByIt(string typed, string below)
    {
        Assert.Equal(Path.Combine(Home, below), PersonaPool.SharedRoot(typed, null, Home));
    }

    [Fact]
    public void ATildeWithNoHomeToExpandItAgainstIsNoPool()
    {
        Assert.Null(PersonaPool.SharedRoot("~/shared", null, null));
    }

    [Theory]
    [InlineData("personas")]
    [InlineData("../personas")]
    [InlineData("~shared")]     // another user's home, not this one's
    public void ARelativePathIsRefusedBecauseItWouldMeanADifferentFolderForEveryCaller(string typed)
    {
        Assert.Null(PersonaPool.SharedRoot(typed, null, Home));
    }

    [Fact]
    public void ThePathIsTrimmedBeforeItIsUsed()
    {
        Assert.Equal(Pool, PersonaPool.SharedRoot("  " + Pool + "  ", null, Home));
    }

    // --- NamesFor ------------------------------------------------------------------

    [Theory]
    [InlineData("atlas", new[] { "atlas" })]
    [InlineData("atlas_2", new[] { "atlas_2", "atlas" })]
    [InlineData("atlas_17", new[] { "atlas_17", "atlas" })]
    [InlineData("a_b_12", new[] { "a_b_12", "a_b" })]
    [InlineData("atlas-2", new[] { "atlas-2" })]            // a hyphen is part of a name, not an instance
    [InlineData("atlas_", new[] { "atlas_" })]
    [InlineData("atlas_x", new[] { "atlas_x" })]
    [InlineData("_2", new[] { "_2" })]                       // nothing before the suffix to be an instance of
    public void ATeamMemberIsLookedUpAsItselfThenAsThePersonaItIsAnInstanceOf(string agent, string[] expected)
    {
        Assert.Equal(expected, PersonaPool.NamesFor(agent));
    }

    // --- SlugIn --------------------------------------------------------------------

    [Theory]
    [InlineData("echo", "---", "name: \"Echo\"", "slug: \"echo\"", "---")]
    [InlineData("echo", "---", "slug: echo", "---")]
    [InlineData("echo", "---", "slug: 'echo'", "---")]
    [InlineData("echo", "---", "  slug:   \"echo\"  ", "---")]
    public void TheSlugIsReadFromTheFrontMatterWhateverTheQuoting(string expected, params string[] lines)
    {
        Assert.Equal(expected, LocalPersona.SlugIn(lines));
    }

    [Fact]
    public void ASlugBelowTheFrontMatterIsProseNotAPersona()
    {
        Assert.Null(LocalPersona.SlugIn(new[] { "---", "name: \"Echo\"", "---", "slug: \"echo\"" }));
    }

    [Theory]
    [InlineData("# Echo", "slug: \"echo\"")]          // no front matter at all
    [InlineData("---", "name: \"Echo\"", "---")]     // front matter with no slug
    [InlineData("---", "slug: \"\"", "---")]         // an empty one
    [InlineData("---", "name: \"Echo\"")]            // never closed
    public void AFileThatDoesNotSayWhoItIsGivesNoSlug(params string[] lines)
    {
        Assert.Null(LocalPersona.SlugIn(lines));
    }

    [Fact]
    public void NoLinesGiveNoSlug()
    {
        Assert.Null(LocalPersona.SlugIn(Array.Empty<string>()));
    }

    // --- the candidate list --------------------------------------------------------

    private static IReadOnlyList<LocalPersona.Candidate> Candidates(
        string agent, IEnumerable<string> configDirs, string? pool, SessionSource source = SessionSource.ClaudeCode) =>
        LocalPersona.CandidatesBelow(
            Path.Combine(Home, "work", "project"), configDirs, source, agent, ceiling: Path.Combine(Home, "work", "project"), pool);

    [Fact]
    public void AnAccountsPoolIsOfferedBeforeThatAccountsClaudeMdAndTheSharedPoolLast()
    {
        var config = Path.Combine(Home, "config");
        var paths = Candidates("atlas", new[] { config }, Pool).Select(c => c.Path).ToList();

        var accountPool = paths.IndexOf(Path.Combine(config, "profiles", "atlas", "atlas.md"));
        var accountClaudeMd = paths.IndexOf(Path.Combine(config, "CLAUDE.md"));
        var sharedPool = paths.IndexOf(Path.Combine(Pool, "profiles", "atlas", "atlas.md"));

        Assert.True(accountPool >= 0 && accountPool < accountClaudeMd, "the account's pool comes before its CLAUDE.md");
        Assert.True(accountClaudeMd < sharedPool, "the shared pool comes after the accounts");
        Assert.Equal(paths.Count - 1, paths.IndexOf(Path.Combine(Pool, "persona", "persona.md")));
    }

    [Fact]
    public void AnInstanceIsOfferedUnderItsOwnNameThenItsPersonasInEachPlace()
    {
        var paths = Candidates("atlas_2", Array.Empty<string>(), Pool).Select(c => c.Path).ToList();

        var own = paths.IndexOf(Path.Combine(Pool, "profiles", "atlas_2", "atlas_2.md"));
        var persona = paths.IndexOf(Path.Combine(Pool, "profiles", "atlas", "atlas.md"));

        Assert.True(own >= 0 && own < persona);
    }

    [Fact]
    public void ThePrivateFixedFileIsOfferedWithTheSlugsItMayNameAndNothingElseHasAFilter()
    {
        var config = Path.Combine(Home, "config");
        var all = Candidates("atlas_2", new[] { config }, Pool);

        var fixedFiles = all.Where(c => c.Path.EndsWith("persona.md", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, fixedFiles.Count);   // the project's .orbweaver, the account, the shared pool
        Assert.All(fixedFiles, c => Assert.Equal("atlas_2|atlas", c.SlugFilter));
        Assert.All(all.Where(c => !c.Path.EndsWith("persona.md", StringComparison.Ordinal)), c => Assert.Null(c.SlugFilter));
    }

    [Fact]
    public void ASessionThatIsNotATeamMemberIsOfferedNoPool()
    {
        var config = Path.Combine(Home, "config");
        var paths = Candidates("", new[] { config }, Pool).Select(c => c.Path).ToList();

        Assert.DoesNotContain(paths, p => p.StartsWith(Pool, StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("profiles", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(config, "CLAUDE.md"), paths);
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.ClaudeCloud)]
    public void OnlyClaudeCodeIsOfferedThePool(SessionSource source)
    {
        var paths = Candidates("atlas", new[] { Path.Combine(Home, "config") }, Pool, source).Select(c => c.Path);

        Assert.DoesNotContain(paths, p => p.StartsWith(Pool, StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoPoolRootOnlyTheAccountsAndProjectAreOffered()
    {
        var paths = Candidates("atlas", Array.Empty<string>(), null).Select(c => c.Path);

        Assert.DoesNotContain(paths, p => p.StartsWith(Pool, StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith(Path.Combine(".orbweaver", "profiles", "atlas", "atlas.md"), StringComparison.Ordinal));
    }

    [Fact]
    public void ABlankConfigDirIsSkippedAsItAlwaysWas()
    {
        // Skipped, not trimmed into a path: the list with blank entries is the
        // list with none.
        var withBlanks = Candidates("atlas", new[] { "  ", "" }, Pool);
        var without = Candidates("atlas", Array.Empty<string>(), Pool);

        Assert.Equal(without, withBlanks);
    }

    [Fact]
    public void EachNameInEachPlaceIsItsOwnPersonaAndOnlyTheTwoLayoutsOfOneNameShareOne()
    {
        // ResolveFrom folds a run of consecutive member candidates as one
        // persona, so Place is what says where one persona ends: the two layouts
        // of a name share it, and everything else — another name, another
        // account, the shared pool, the persona.md file — has its own.
        var config = Path.Combine(Home, "config");
        var all = Candidates("atlas_2", new[] { config }, Pool);

        int PlaceOf(string path) => all.First(c => c.Path == path).Place;
        var accountOwn = PlaceOf(Path.Combine(config, "profiles", "atlas_2", "atlas_2.md"));
        var accountBase = PlaceOf(Path.Combine(config, "profiles", "atlas", "atlas.md"));
        var sharedOwn = PlaceOf(Path.Combine(Pool, "profiles", "atlas_2", "atlas_2.md"));

        Assert.Equal(accountOwn, PlaceOf(Path.Combine(config, ".profiles-assets", "atlas_2", "atlas_2.md")));
        Assert.NotEqual(accountOwn, accountBase);
        Assert.NotEqual(accountOwn, sharedOwn);
        Assert.NotEqual(accountOwn, PlaceOf(Path.Combine(config, "persona", "persona.md")));
        Assert.Equal(all.Count(c => c.IsMemberProfile), all.Count(c => c.IsMemberProfile && c.Place > 0));
    }
}
