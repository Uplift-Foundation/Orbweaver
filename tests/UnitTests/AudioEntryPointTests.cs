using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests;

// CB-298: "no sound path can bypass the mute" is enforced rather than hoped for.
//
// The mute is checked at the two lines that build a sound process, in
// TextToSpeech and ChimePlayer, and the Kokoro side-car starts from inside
// TextToSpeech. So the claim holds exactly as long as no other file starts a
// sound backend, and nothing at compile time says so. A source scan, for the
// reason SpeechEngineNameConsistencyTests is one: what is being protected is a
// fact about which files mention what, and only reading them says so. When this
// fails, someone has added a way to make noise outside the gate — route it
// through TextToSpeech.Speak or ChimePlayer, do not add the file to the list.
public class AudioEntryPointTests
{
    // Backends: a name that appears on a non-comment line means a process (or
    // an in-process player) that makes sound.
    private static readonly Regex Backend = new(
        @"afplay|SoundPlayer|PlaySync|SpeechSynthesizer|""/usr/bin/say""|NeuralSpeech\.Start\(",
        RegexOptions.Compiled);

    // The two classes that start one, the side-car launcher they delegate to,
    // and AudioVolume, which only builds afplay's argument list.
    private static readonly string[] Gate =
        { "TextToSpeech.cs", "ChimePlayer.cs", "NeuralSpeech.cs", "AudioVolume.cs" };

    // The only production callers of TextToSpeech.Speak: each reaches the gate
    // inside it.
    private static readonly string[] SpeakCallers = { "SpeechRequest.cs", "VoicePreview.cs", "TextToSpeech.cs" };

    // Pure: the lines of one file that a sound backend appears on, outside a
    // comment. Here, not inlined, so the negative control below can hand it a
    // file that is not on disk.
    internal static IReadOnlyList<string> BackendLines(string text) =>
        text.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//"))
            .Where(l => Backend.IsMatch(l))
            .Select(l => l.Trim())
            .ToList();

    private static IEnumerable<string> ProductionSources() =>
        Directory.EnumerateFiles(RepositoryRoot(), "*.cs", SearchOption.TopDirectoryOnly);

    [Fact]
    public void NoFileOutsideTheGateStartsASoundBackend()
    {
        var sources = ProductionSources().ToList();
        Assert.Contains(sources, s => Path.GetFileName(s) == "TextToSpeech.cs");   // we can see what we guard

        var offenders = sources
            .Where(s => !Gate.Contains(Path.GetFileName(s)))
            .Where(s => BackendLines(File.ReadAllText(s)).Count > 0)
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void OnlyTheSpeechRequestAndThePreviewCallTextToSpeechSpeak()
    {
        var offenders = ProductionSources()
            .Where(s => !SpeakCallers.Contains(Path.GetFileName(s)))
            .Where(s => File.ReadAllText(s).Split('\n')
                .Any(l => !l.TrimStart().StartsWith("//") && l.Contains("TextToSpeech.Speak(")))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    // The control: the scan has to fail on exactly what it is there to catch,
    // or a clean result above means nothing.
    [Theory]
    [InlineData("Process.Start(new ProcessStartInfo(\"/usr/bin/afplay\"));")]
    [InlineData("new System.Media.SoundPlayer(path).PlaySync();")]
    [InlineData("var s = new SpeechSynthesizer();")]
    [InlineData("var p = new ProcessStartInfo(\"/usr/bin/say\");")]
    [InlineData("NeuralSpeech.Start(text);")]
    public void TheScanCatchesABackendOnACodeLine(string code)
    {
        Assert.Single(BackendLines("// a comment about afplay\n    " + code + "\n"));
    }

    [Fact]
    public void TheScanIgnoresCommentsAndOrdinaryCode()
    {
        Assert.Empty(BackendLines("    // afplay -v takes a float\nvar x = 1;\n"));
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile);
        while (directory is not null && !File.Exists(Path.Combine(directory, "Orbweaver.csproj")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("no Orbweaver.csproj above " + thisFile);
    }
}
