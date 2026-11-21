using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-397 addendum 2: the component walk, against a fake filesystem of "does it
/// exist, and where does it link to". The real call is a one-line seam.
/// </summary>
public class FolderResolverTests
{
    // The fake filesystem is keyed by POSIX paths, and the walk builds paths with
    // Path.Combine, so on Windows every lookup would miss and the input come back
    // unchanged. The resolver exists for /tmp to /private/tmp; the real link case is
    // in the integration suite, so this shows as skipped on windows-latest instead of
    // passing without checking anything.
    private sealed class PosixPathsTheoryAttribute : TheoryAttribute
    {
        public PosixPathsTheoryAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "the fake filesystem is keyed by POSIX paths";
        }
    }

    // A path is present when listed; a link has a target.
    private static Func<string, (bool, string?)> Fs(Dictionary<string, string?> entries) =>
        p => entries.TryGetValue(p, out var t) ? (true, t) : (false, null);

    private static readonly Dictionary<string, string?> Mac = new()
    {
        ["/tmp"] = "private/tmp",                    // relative target in the first component
        ["/private"] = null,
        ["/private/tmp"] = null,
        ["/private/tmp/x"] = null,
        ["/private/tmp/x/y"] = null,
        ["/a"] = null,
        ["/a/mid"] = "/private/tmp/x",               // absolute target in a middle component
        ["/b"] = null,
        ["/b/last"] = "../private/tmp/x/y",          // relative target in the last component
        ["/c"] = null,
        ["/c/one"] = "two",                          // a two-link chain
        ["/c/two"] = "/private/tmp/x",
        ["/loop1"] = "/loop2",
        ["/loop2"] = "/loop1",
        ["/plain"] = null,
    };

    [PosixPathsTheory]
    [InlineData("/tmp/x", "/private/tmp/x")]
    [InlineData("/tmp/x/y", "/private/tmp/x/y")]
    [InlineData("/a/mid", "/private/tmp/x")]
    [InlineData("/a/mid/y", "/private/tmp/x/y")]
    [InlineData("/b/last", "/private/tmp/x/y")]
    [InlineData("/c/one", "/private/tmp/x")]
    [InlineData("/private/tmp/x", "/private/tmp/x")]
    [InlineData("/plain", "/plain")]
    public void A_link_in_any_component_resolves_to_the_physical_path(string asked, string physical)
    {
        Assert.Equal(physical, FolderResolver.Resolve(asked, Fs(Mac)));
    }

    [Theory]
    [InlineData("/tmp/missing")]      // a missing component
    [InlineData("/nope/x")]
    [InlineData("/loop1")]            // a loop runs out of budget
    [InlineData("relative/path")]     // no root at all
    [InlineData("")]
    public void Anything_unexpected_returns_the_path_as_given(string asked)
    {
        Assert.Equal(asked, FolderResolver.Resolve(asked, Fs(Mac)));
    }

    [Fact]
    public void A_filesystem_that_throws_returns_the_path_as_given()
    {
        Assert.Equal("/tmp/x", FolderResolver.Resolve("/tmp/x", _ => throw new UnauthorizedAccessException()));
    }

    // A link whose own target walk fails (the target is missing) leaves the path as given.
    [Fact]
    public void A_link_to_nowhere_returns_the_path_as_given()
    {
        var fs = new Dictionary<string, string?> { ["/l"] = "/gone" };
        Assert.Equal("/l/x", FolderResolver.Resolve("/l/x", Fs(fs)));
    }
}
