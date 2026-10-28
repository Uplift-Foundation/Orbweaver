using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests
{
    // BrandEnv: every override variable is ORBWEAVER_<name>, the pre-rename
    // CLAUDE_BUDDY_<name> still works, and the new one wins (CB-256).
    //
    // Every arm of the precedence for every variable, with the environment
    // injected — nothing here touches this process's own variables, which
    // TestBootstrap has already pointed at scratch directories. Then a source
    // scan, so a seventeenth variable cannot be added that the list (and so
    // this theory) does not know about.
    public class BrandEnvTests
    {
        // The spellings themselves, written out once rather than derived, so
        // the constants cannot drift and still pass the tests built on them.
        [Fact]
        public void ThePrefixesAreTheNewNameAndThePreRenameOne()
        {
            Assert.Equal("ORBWEAVER_", BrandEnv.Prefix);
            Assert.Equal("CLAUDE_BUDDY_", BrandEnv.LegacyPrefix);
            Assert.Equal("ORBWEAVER_SETTINGS_DIR", BrandEnv.Name(BrandEnv.SettingsDir));
        }

        // Fifteen: the sixteen of phase 2 less the two nothing read
        // (NO_RELAY, RC_BRIDGE_TAG), which were deleted rather than renamed,
        // plus LIVE_DAEMON_TESTS, the opt-in gate on DaemonStopLiveTests (CB-264).
        [Fact]
        public void TheListHoldsNineteenDistinctVariables()
        {
            Assert.Equal(19, BrandEnv.All.Count);
            Assert.Equal(BrandEnv.All.Count, BrandEnv.All.Distinct().Count());
        }

        public enum Arm { NewOnly, LegacyOnly, BothNewWins, BothEmpty, NewEmptyLegacySet }

        public static TheoryData<string, Arm> EveryVariableByEveryArm()
        {
            var data = new TheoryData<string, Arm>();
            foreach (var suffix in BrandEnv.All)
                foreach (var arm in Enum.GetValues<Arm>())
                    data.Add(suffix, arm);
            return data;
        }

        [Theory]
        [MemberData(nameof(EveryVariableByEveryArm))]
        public void TheNewSpellingWinsAndTheOldOneIsTheFallback(string suffix, Arm arm)
        {
            var newName = "ORBWEAVER_" + suffix;
            var legacyName = "CLAUDE_BUDDY_" + suffix;

            var env = arm switch
            {
                Arm.NewOnly => new Dictionary<string, string> { [newName] = "new" },
                Arm.LegacyOnly => new Dictionary<string, string> { [legacyName] = "old" },
                Arm.BothNewWins => new Dictionary<string, string> { [newName] = "new", [legacyName] = "old" },
                Arm.BothEmpty => new Dictionary<string, string> { [newName] = "", [legacyName] = "" },
                _ => new Dictionary<string, string> { [newName] = "", [legacyName] = "old" },
            };

            var expected = arm switch
            {
                Arm.NewOnly or Arm.BothNewWins => "new",
                Arm.LegacyOnly or Arm.NewEmptyLegacySet => "old",
                _ => null,
            };

            Assert.Equal(expected, BrandEnv.Get(suffix, name => env.GetValueOrDefault(name)));
        }

        // The one-argument overload reads the real process environment. Driven
        // through a variable no code reads, set in both spellings, so the test
        // cannot disturb (or be disturbed by) a seam another test relies on.
        [Fact]
        public void TheProcessOverloadReadsTheRealEnvironmentWithTheSamePrecedence()
        {
            const string suffix = "BRANDENV_TESTS_PROBE";
            try
            {
                Assert.Null(BrandEnv.Get(suffix));

                Environment.SetEnvironmentVariable(BrandEnv.LegacyPrefix + suffix, "old");
                Assert.Equal("old", BrandEnv.Get(suffix));

                Environment.SetEnvironmentVariable(BrandEnv.Prefix + suffix, "new");
                Assert.Equal("new", BrandEnv.Get(suffix));
            }
            finally
            {
                Environment.SetEnvironmentVariable(BrandEnv.Prefix + suffix, null);
                Environment.SetEnvironmentVariable(BrandEnv.LegacyPrefix + suffix, null);
            }
        }

        // --- the source scan ----------------------------------------------------

        // A variable spelled out with either prefix (not preceded by a word
        // character, so the installer's __ORBWEAVER_SPLIT__ marker is not one),
        // and a shell script's brand_env call, which names only the suffix.
        private static readonly Regex Spelled =
            new(@"(?<![A-Za-z0-9_])(?:ORBWEAVER_|CLAUDE_BUDDY_)([A-Z][A-Z_]*[A-Z])", RegexOptions.Compiled);
        private static readonly Regex BrandEnvCall =
            new(@"\bbrand_env\s+([A-Z][A-Z_]*[A-Z])\b", RegexOptions.Compiled);
        private static readonly Regex ConstantUse =
            new(@"\bBrandEnv\.([A-Z][A-Za-z]+)\b", RegexOptions.Compiled);

        private static readonly string[] Extensions = [".cs", ".sh", ".ps1", ".iss", ".py", ".yml"];

        [Fact]
        public void TheListIsExactlyTheVariablesTheSourcesUse()
        {
            var root = RepositoryRoot();
            var constants = typeof(BrandEnv)
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.IsLiteral && f.Name is not (nameof(BrandEnv.Prefix) or nameof(BrandEnv.LegacyPrefix)))
                .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

            // Every constant is in the list and every list entry is a constant.
            Assert.Equal(constants.Values.OrderBy(v => v), BrandEnv.All.OrderBy(v => v));

            var spelled = new Dictionary<string, string>();
            var used = new HashSet<string>();

            foreach (var path in SourceFiles(root))
            {
                var text = File.ReadAllText(path);
                var relative = Path.GetRelativePath(root, path);
                var name = Path.GetFileName(path);

                foreach (Match m in Spelled.Matches(text).Concat(BrandEnvCall.Matches(text)))
                    spelled.TryAdd(m.Groups[1].Value, relative);

                // Uses, for the other direction. BrandEnv.cs defines the
                // constants and this file enumerates them; neither is a use.
                if (name is "BrandEnv.cs" or "BrandEnvTests.cs") continue;
                foreach (Match m in Spelled.Matches(text).Concat(BrandEnvCall.Matches(text)))
                    used.Add(m.Groups[1].Value);
                foreach (Match m in ConstantUse.Matches(text))
                    if (constants.TryGetValue(m.Groups[1].Value, out var suffix)) used.Add(suffix);
            }

            var unknown = spelled.Keys.Except(BrandEnv.All).OrderBy(k => k)
                .Select(k => $"  {k} (first in {spelled[k]})").ToList();
            Assert.True(unknown.Count == 0,
                "an override variable is spelled outside BrandEnv's list — add it to BrandEnv.All "
                + "(and read it through BrandEnv.Get / brand_env), or rename it:"
                + Environment.NewLine + string.Join(Environment.NewLine, unknown));

            var unused = BrandEnv.All.Except(used).OrderBy(k => k).ToList();
            Assert.True(unused.Count == 0,
                "BrandEnv.All lists variables nothing reads or sets any more — delete them: "
                + string.Join(", ", unused));
        }

        // The repository's sources: every text file of the kinds above, outside
        // build output, git's own directory, the .claude directory (agent
        // worktrees hold whole second copies of the tree) and docs, which are
        // prose about the variables rather than uses of them.
        private static IEnumerable<string> SourceFiles(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(p => Extensions.Contains(Path.GetExtension(p)))
                .Where(p =>
                {
                    var parts = Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar);
                    return !parts.Any(part => part is "bin" or "obj" or ".git" or ".claude" or "docs");
                });

        // Same resolution as LogDirSingleReadSiteTests: from this file's own
        // compile-time path, and loud rather than skipped when it fails.
        private static string RepositoryRoot([CallerFilePath] string thisFile = "")
        {
            var directory = Path.GetDirectoryName(thisFile);
            while (directory is not null && !File.Exists(Path.Combine(directory, "Orbweaver.csproj")))
                directory = Path.GetDirectoryName(directory);

            Assert.True(directory is not null,
                $"could not find the repository root above {thisFile} — this guard cannot run, "
                + "and must fail rather than pass silently.");
            return directory!;
        }
    }
}
