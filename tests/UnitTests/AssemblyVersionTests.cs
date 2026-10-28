using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-205: the shipped exe reported FileVersion 0.4.1.0 next to a ProductVersion of
/// 0.5.x-beta, because the csproj hard-coded AssemblyVersion/FileVersion and nobody
/// bumped them. They are now derived from &lt;Version&gt;; this pins that, so a future
/// hard-code fails on both rids instead of surviving five releases.
/// </summary>
public class AssemblyVersionTests
{
    private static readonly Assembly App = typeof(SessionManager).Assembly;

    private static string Informational() =>
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    // "0.5.9-beta+abc123" -> "0.5.9": the numeric part of the informational version.
    private static string NumericPart() => Informational().Split('+')[0].Split('-')[0];

    [Fact]
    public void FileVersion_is_the_numeric_part_of_Version()
    {
        var file = App.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
        Assert.Equal(NumericPart() + ".0", file);
    }

    [Fact]
    public void AssemblyVersion_is_the_numeric_part_of_Version()
    {
        Assert.Equal(NumericPart() + ".0", App.GetName().Version!.ToString());
    }

    // The literal <Version> element, which release.yml and the packaging scripts
    // parse the same way.
    private static string CsprojVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orbweaver.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "Orbweaver.csproj"));
        return Regex.Match(text, @"<Version>([^<]+)</Version>").Groups[1].Value;
    }

    [Fact]
    public void Informational_version_keeps_the_label_Version_carries()
    {
        // Stripping the suffix for the numeric fields must not strip it from the
        // label users see. Compared against the csproj rather than demanding a
        // hyphen, so a stable release (1.0.0, no label) passes and a prerelease
        // (0.5.11-beta) still has to keep its label.
        var shown = Informational().Split('+')[0];
        Assert.Equal(CsprojVersion(), shown);
        Assert.StartsWith(NumericPart(), shown);
    }
}
