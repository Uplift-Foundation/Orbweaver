using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests;

// CB-241's regression pin, in the shape CB-183's UiDispatcherIsolationTests set:
// it fails the day the next unserialised class arrives, which a kept
// reproduction alone would not.
//
// The defect: since CB-221, ClaudeCliCredentials.SourcesFor reads
// ClaudeConfigRoots, which adds every Claude Code profile directory listed in
// the process-wide settings. Classes that add one (".claude-board") for the
// length of a test were in [Collection("Settings")]; CredentialStoreDisabledTests
// was in no collection, so xUnit ran it beside them, and develop's Windows leg
// saw two accounts where the test asserts one.
//
// The rule pinned here: every test class in this assembly that **writes** the
// profile list (AddClaudeCodeProfileDir / RemoveClaudeCodeProfileDir) or
// **reads** it through SourcesFor, ClaudeConfigRoots or
// TranscriptReader.LatestTranscriptForCwd is in the Settings collection.
//
// Not ConfigDirEnv, decided rather than assumed: that collection serialises
// CLAUDE_CONFIG_DIR, a different input, and is a separate collection, which
// xUnit runs in parallel with Settings — so a ConfigDirEnv class that read the
// profile list would race the Settings writers exactly as this one did. The
// one ConfigDirEnv class here, UsagePollerTests, does not read it: it calls
// UsagePoller.UsageProcess, which builds a start-info from the config dir it is
// handed; the settings read is in the poll loop, which it never runs. Nor does
// BackgroundJobsAccountFilterTests: AccountsToAsk and ClaudeTranscripts are
// pure and are given their directories. So no merge of the two collections.
//
// Collections are per assembly — each suite is its own process — so this
// guards tests/UnitTests only; tests/IntegrationTests has its own copy. A class
// that reaches the profile list several calls down, through an API not named
// above, is out of reach of a text search and is not guarded.
//
// In the Settings collection itself, because the reproduction below holds a
// profile directory.
[Collection("Settings")]
public class SettingsSerialisationPinTests
{
    private const string Settings = "Settings";

    // A call that writes the profile list or reads it the way SourcesFor does.
    private static readonly Regex Touches = new(
        @"\b(AddClaudeCodeProfileDir|RemoveClaudeCodeProfileDir|LatestTranscriptForCwd)\s*\(|\bSourcesFor\s*\(|\bClaudeConfigRoots\.",
        RegexOptions.Compiled);

    // CB-386: a call that writes or reads the skipped-update setting. With no
    // checker, UpdateServices.Skip / ClearSkipped write
    // OrbweaverSettings.SkippedUpdateVersion directly, so they touch it too.
    private static readonly Regex TouchesSkippedUpdate = new(
        @"\bSkippedUpdateVersion\b|\bUpdateServices\.(Skip|ClearSkipped)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orbweaver.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find Orbweaver.csproj above " + AppContext.BaseDirectory);
    }

    // The source with every // comment removed, so a sentence mentioning
    // SourcesFor is not mistaken for a call. Block comments are not used in
    // these tests; a string literal mentioning one of the names would be a
    // false positive, which fails loudly and is fixed by joining the
    // collection — the safe direction.
    private static string WithoutLineComments(string source) =>
        string.Join("\n", source.Split('\n').Select(line =>
        {
            var at = line.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? line : line[..at];
        }));

    // Every class declared in `source` whose own text (up to the next class
    // declaration) touches the profile list. A nested helper class is
    // reported under its own name; the caller attributes it to its outer
    // test class, which is what carries [Collection].
    internal static IReadOnlyList<string> ClassesThatTouchTheProfileList(string source) =>
        ClassesThatTouch(source, Touches);

    internal static IReadOnlyList<string> ClassesThatTouch(string source, Regex touches)
    {
        var code = WithoutLineComments(source);
        var declarations = ClassDeclaration.Matches(code).ToList();
        var found = new List<string>();

        for (var i = 0; i < declarations.Count; i++)
        {
            var start = declarations[i].Index;
            var end = i + 1 < declarations.Count ? declarations[i + 1].Index : code.Length;
            if (touches.IsMatch(code[start..end])) found.Add(declarations[i].Groups[1].Value);
        }

        return found;
    }

    // xUnit reads [Collection] off the class itself, so a nested test class
    // that declares its own is in that collection whatever its outer class is
    // (UpdateCheckerTests+RealSettings); a nested helper with none falls back to
    // the outer test class's.
    private static string? CollectionOf(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            var own = t.GetCustomAttributesData()
                .Where(a => a.AttributeType.Name == "CollectionAttribute")
                .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
                .FirstOrDefault();
            if (own is not null) return own;
        }

        return null;
    }

    [Fact]
    public void TheCredentialStoreTestIsInTheSettingsCollection()
    {
        Assert.Equal(Settings, CollectionOf(typeof(CredentialStoreDisabledTests)));
    }

    // The guard proper. Fails naming every class that touches the profile list
    // from outside the Settings collection.
    [Fact]
    public void EveryClassThatTouchesTheProfileListIsInTheSettingsCollection()
    {
        var root = Path.Combine(FindRepoRoot(), "tests", "UnitTests");
        var types = typeof(SettingsSerialisationPinTests).Assembly.GetTypes();
        var checkedClasses = new List<string>();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            if (Path.GetFileName(file) == nameof(SettingsSerialisationPinTests) + ".cs") continue;

            foreach (var name in ClassesThatTouchTheProfileList(File.ReadAllText(file)))
            {
                var type = types.FirstOrDefault(t => t.Name == name);
                if (type is null) continue; // compiled under another name; not a test class
                checkedClasses.Add(name);
                if (CollectionOf(type) != Settings) offenders.Add($"{name} ({Path.GetFileName(file)})");
            }
        }

        // A guard that matched nothing would pass vacuously. The four known
        // touchers at the time of writing keep it honest.
        Assert.Contains(nameof(CredentialStoreDisabledTests), checkedClasses);
        Assert.Contains(nameof(ClaudeConfigRootsTests), checkedClasses);

        Assert.True(offenders.Count == 0,
            "Outside [Collection(\"Settings\")] but touching the profile list: " + string.Join(", ", offenders));
    }

    // CB-386: the same rule for the skipped-update setting. UpdateUiLogicTests
    // wrote it from outside the collection while UpdateSettingsTests and
    // UpdateCheckerTests (both in it) set it to other values and to null, and
    // its Skip-then-assert could read the other class's value back.
    [Fact]
    public void EveryClassThatTouchesTheSkippedUpdateVersionIsInTheSettingsCollection()
    {
        var root = Path.Combine(FindRepoRoot(), "tests", "UnitTests");
        var types = typeof(SettingsSerialisationPinTests).Assembly.GetTypes();
        var checkedClasses = new List<string>();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            if (Path.GetFileName(file) == nameof(SettingsSerialisationPinTests) + ".cs") continue;

            foreach (var name in ClassesThatTouch(File.ReadAllText(file), TouchesSkippedUpdate))
            {
                var type = types.FirstOrDefault(t => t.Name == name);
                if (type is null) continue;
                // A touch after a nested helper class is attributed to that
                // helper by the text scan; the collection and the report are the
                // outer test class's.
                var outer = type;
                while (outer.DeclaringType is not null) outer = outer.DeclaringType;
                checkedClasses.Add(outer.Name);
                if (CollectionOf(type) != Settings) offenders.Add($"{outer.Name} ({Path.GetFileName(file)})");
            }
        }

        // Not vacuous: the known touchers.
        Assert.Contains(nameof(UpdateUiLogicTests), checkedClasses);
        Assert.Contains(nameof(UpdateSettingsTests), checkedClasses);

        Assert.True(offenders.Count == 0,
            "Outside [Collection(\"Settings\")] but touching the skipped-update version: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheSkippedUpdateDetectorFindsATouchAndIgnoresAComment()
    {
        const string source = """
            public class Writes
            {
                void M() { OrbweaverSettings.SkippedUpdateVersion = null; }
            }

            public class Skips
            {
                void M() { UpdateServices.Skip(release); }
            }

            public class OnlyMentions
            {
                // SkippedUpdateVersion and UpdateServices.Skip( in a comment are not calls.
                void M() { }
            }
            """;

        Assert.Equal(new[] { "Writes", "Skips" }, ClassesThatTouch(source, TouchesSkippedUpdate));
    }

    // The detector, against a class it must flag and one it must not — so a
    // regex that drifted would fail here rather than quietly pass above.
    [Fact]
    public void TheDetectorFindsATouchAndIgnoresAComment()
    {
        const string source = """
            public class Writes
            {
                void M() { OrbweaverSettings.AddClaudeCodeProfileDir(".x"); }
            }

            public class OnlyMentions
            {
                // SourcesFor( and ClaudeConfigRoots. in a comment are not calls.
                void M() { }
            }

            public class Reads
            {
                void M() { ClaudeCliCredentials.SourcesFor(true, "/h"); }
            }
            """;

        Assert.Equal(new[] { "Writes", "Reads" }, ClassesThatTouchTheProfileList(source));
    }

    // The reproduction that found CB-241, kept: a profile directory held in
    // the process-wide settings is a second account to SourcesFor, and letting
    // go of it leaves one. This is the dependency the guard above protects; no
    // sleeps and no timing, because it holds the state rather than racing for it.
    [Fact]
    public void AHeldProfileDirectoryIsASecondAccountToSourcesFor()
    {
        const string home = "/tmp/cb241-pin";

        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        try
        {
            var held = ClaudeCliCredentials.SourcesFor(isMacOS: true, home: home);
            Assert.Equal(2, held.Count);
            Assert.Contains(held, a => a.Label == "board");
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-board");
        }

        Assert.Single(ClaudeCliCredentials.SourcesFor(isMacOS: true, home: home));
    }
}
