using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests;

// CB-410: RemoteControlSessions.ResetForTests, UseMirrorClientForTests and PeerSessions.UseClientForTests
// swap the process-wide mirror link. A swap that lands between two steps of another test makes
// RemoteControlChatSession.Files see a far side that does not serve files and answer "update" instead of
// the refusal the test is waiting for (CI run 44961762478). MirrorGate.Shared keeps the two mirror
// suites from overlapping; this scan fails when a third file starts swapping the link without being one
// of them, or when one of them stops taking the gate.
public class MirrorLinkSwapGuardTests
{
    private static readonly Regex Swap = new(
        @"\b(RemoteControlSessions\s*\.\s*(ResetForTests|UseMirrorClientForTests)|PeerSessions\s*\.\s*UseClientForTests)\b",
        RegexOptions.Compiled);

    private static readonly Regex Comment = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    // The classes that swap the link, each holding MirrorGate.Shared for the whole of every test.
    private static bool IsGated(string fileName) =>
        fileName == "MirrorEdgeCaseTests.cs" || fileName.StartsWith("MirrorRoundTripTests", StringComparison.Ordinal);

    // The files in `sources` (name, text) that swap the link and are not allowed to, plus the gated
    // files that swap it with no MirrorGate.Shared taken anywhere in the class.
    internal static List<string> Offenders(IEnumerable<(string Name, string Text)> sources)
    {
        var swapsByGatedClass = new Dictionary<string, (bool Swaps, bool Holds)>();
        var offenders = new List<string>();

        foreach (var (name, raw) in sources)
        {
            var text = Comment.Replace(raw, "");
            var swaps = Swap.IsMatch(text);
            var holds = text.Contains("MirrorGate.Shared.Hold(", StringComparison.Ordinal);

            if (!IsGated(name))
            {
                if (swaps) offenders.Add(name + " swaps the mirror link without being under MirrorGate.Shared");
                continue;
            }

            var cls = name.Split('.')[0];
            var seen = swapsByGatedClass.GetValueOrDefault(cls);
            swapsByGatedClass[cls] = (seen.Swaps || swaps, seen.Holds || holds);
        }

        foreach (var (cls, seen) in swapsByGatedClass)
            if (seen.Swaps && !seen.Holds) offenders.Add(cls + " swaps the mirror link but never takes MirrorGate.Shared");

        return offenders;
    }

    private static string IntegrationTestsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "IntegrationTests");
            if (File.Exists(Path.Combine(dir.FullName, "Orbweaver.csproj")) && Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find tests/IntegrationTests above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Only_the_gated_mirror_suites_swap_the_link_and_they_hold_the_gate()
    {
        var sources = Directory.EnumerateFiles(IntegrationTestsDir(), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(p => Path.GetFileName(p) != nameof(MirrorLinkSwapGuardTests) + ".cs")
            .Select(p => (Path.GetFileName(p), File.ReadAllText(p)))
            .ToList();

        // The scan has to be looking at the suites it guards, or an empty result proves nothing.
        Assert.Contains(sources, s => s.Item1 == "MirrorEdgeCaseTests.cs");
        Assert.Contains(sources, s => s.Item1 == "MirrorRoundTripTests.SessionFiles.cs");

        Assert.Empty(Offenders(sources));
    }

    [Theory]
    [InlineData("RemoteControlSessions.ResetForTests();")]
    [InlineData("RemoteControlSessions.UseMirrorClientForTests(\"a\", null);")]
    [InlineData("PeerSessions.UseClientForTests(null);")]
    public void An_ungated_class_that_swaps_the_link_is_an_offender(string call)
    {
        var offenders = Offenders(new[] { ("SomeOtherTests.cs", "class C { void M() { " + call + " } }") });

        Assert.Equal("SomeOtherTests.cs swaps the mirror link without being under MirrorGate.Shared", Assert.Single(offenders));
    }

    [Fact]
    public void A_gated_class_that_swaps_without_taking_the_gate_is_an_offender()
    {
        var offenders = Offenders(new[]
        {
            ("MirrorRoundTripTests.cs", "class C { C() { } }"),
            ("MirrorRoundTripTests.SessionFiles.cs", "class C { void M() { RemoteControlSessions.ResetForTests(); } }"),
        });

        Assert.Equal("MirrorRoundTripTests swaps the mirror link but never takes MirrorGate.Shared", Assert.Single(offenders));
    }

    [Fact]
    public void A_gated_class_taking_the_gate_in_another_part_and_a_mention_in_a_comment_are_fine()
    {
        var offenders = Offenders(new[]
        {
            ("MirrorRoundTripTests.cs", "class C { C() { MirrorGate.Shared.Hold(() => { }); } }"),
            ("MirrorRoundTripTests.SessionFiles.cs", "class C { void M() { RemoteControlSessions.ResetForTests(); } }"),
            ("OtherTests.cs", "// RemoteControlSessions.ResetForTests();\n/* PeerSessions.UseClientForTests(null); */ class D { }"),
        });

        Assert.Empty(offenders);
    }
}
