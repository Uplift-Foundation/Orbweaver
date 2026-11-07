using System.Diagnostics;
using Xunit;

namespace Orbweaver.IntegrationTests;

// tools/check-release-sums.sh, run as the real script against scratch artifact
// directories. Each failing case has a passing control beside it, so a script
// that always exits 1 (or always 0) cannot satisfy the suite.
public sealed class ReleaseSumsGuardTests : IDisposable
{
    private const string H = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sums-" + Guid.NewGuid().ToString("N"));

    public ReleaseSumsGuardTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Script()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tools", "check-release-sums.sh");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("tools/check-release-sums.sh");
    }

    private (int Exit, string Output) Run()
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Script());
        psi.ArgumentList.Add(_dir);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, text);
    }

    private void Files(params string[] names) { foreach (var n in names) File.WriteAllText(Path.Combine(_dir, n), "x"); }
    private void Sums(params string[] lines) => File.WriteAllText(Path.Combine(_dir, "SHA256SUMS.txt"), string.Join("\n", lines) + "\n");

    [Fact]
    public void EveryInstallerListedPasses()
    {
        if (OperatingSystem.IsWindows()) return;   // runs in the ubuntu publish job; no bash contract on the Windows leg
        Files("Orbweaver-1.0.4-osx-arm64.dmg", "Orbweaver-1.0.4-win-x64-setup.exe", "OrbweaverSpeech-1.0.4-win-x64.zip");
        Sums($"{H}  Orbweaver-1.0.4-osx-arm64.dmg", $"{H} *Orbweaver-1.0.4-win-x64-setup.exe");
        var (exit, output) = Run();
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void ARenamedInstallerFails()
    {
        if (OperatingSystem.IsWindows()) return;
        Files("Orbweaver-1.0.4-osx-arm64.dmg", "Orbweaver-1.0.4-win-x64-setup.exe");
        Sums($"{H}  Orbweaver-1.0.4-osx-arm64.dmg", $"{H}  Orbweaver-1.0.4-win-x64-installer.exe");
        var (exit, output) = Run();
        Assert.NotEqual(0, exit);
        Assert.Contains("Orbweaver-1.0.4-win-x64-setup.exe", output);
        Assert.DoesNotContain("no line for Orbweaver-1.0.4-osx-arm64.dmg", output);
    }

    [Fact]
    public void ANamePrefixIsNotAMatch()
    {
        if (OperatingSystem.IsWindows()) return;
        Files("a.dmg");
        Sums($"{H}  a.dmg.old");
        Assert.NotEqual(0, Run().Exit);
    }

    [Fact]
    public void NoInstallersAtAllFails()
    {
        if (OperatingSystem.IsWindows()) return;
        Files("OrbweaverSpeech-1.0.4-win-x64.zip");
        Sums($"{H}  OrbweaverSpeech-1.0.4-win-x64.zip");
        var (exit, output) = Run();
        Assert.NotEqual(0, exit);
        Assert.Contains("no *.dmg", output);
    }

    [Fact]
    public void AMissingSumsFileFails()
    {
        if (OperatingSystem.IsWindows()) return;
        Files("a.dmg");
        var (exit, output) = Run();
        Assert.NotEqual(0, exit);
        Assert.Contains("does not exist", output);
    }
}
