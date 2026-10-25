using Xunit;

namespace Orbweaver.Tests;

// CB-261: tools/Orbweaver.iss adds no firewall rule. The installer is a per-user
// install (PrivilegesRequired=lowest) with no override, so an entry gated on
// IsAdminInstallMode can never run, and the netsh entries that were gated that
// way were dead code that claimed behaviour that could not happen. Windows
// Firewall's own first-listen prompt is the mechanism instead.
//
// This is a static check of the script text: compiling it needs Inno Setup and
// installing it needs Windows, which CI's verify-windows-installer workflow does
// for real. Each assertion has a control that feeds the same predicate a script
// that DOES have the thing, so a clean pass cannot be a predicate matching nothing.
public class InstallerScriptTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Iss = File.ReadAllText(Path.Combine(RepoRoot, "tools", "Orbweaver.iss"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "Orbweaver.iss")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find tools/Orbweaver.iss by walking up from " + AppContext.BaseDirectory);
    }

    // Inno comments are lines whose first non-space character is ';'.
    internal static IEnumerable<string> CodeLines(string iss) =>
        iss.Split('\n').Select(l => l.TrimEnd('\r'))
           .Where(l => !l.TrimStart().StartsWith(';'));

    internal static IEnumerable<string> LinesMentioning(string iss, params string[] needles) =>
        CodeLines(iss).Where(l => needles.Any(n => l.Contains(n, StringComparison.OrdinalIgnoreCase)));

    internal static bool DeclaresDirective(string iss, string name) =>
        CodeLines(iss).Any(l => l.TrimStart().StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));

    private const string Offender =
        "Filename: \"{sys}\\netsh.exe\"; Parameters: \"advfirewall firewall add rule name=x\"; Check: IsAdminInstallMode";

    [Fact]
    public void Setup_is_per_user_with_no_admin_override()
    {
        Assert.True(DeclaresDirective(Iss, "PrivilegesRequired"));
        Assert.Contains(CodeLines(Iss), l => l.Trim().Equals("PrivilegesRequired=lowest", StringComparison.OrdinalIgnoreCase));
        // If this fails, an admin install became possible: revisit CB-261.
        Assert.False(DeclaresDirective(Iss, "PrivilegesRequiredOverridesAllowed"));
    }

    [Fact]
    public void Control_override_directive_is_detected()
    {
        var withOverride = "[Setup]\nPrivilegesRequired=lowest\nPrivilegesRequiredOverridesAllowed=dialog\n";
        Assert.True(DeclaresDirective(withOverride, "PrivilegesRequiredOverridesAllowed"));
        Assert.False(DeclaresDirective("[Setup]\n; PrivilegesRequiredOverridesAllowed=dialog\n", "PrivilegesRequiredOverridesAllowed"));
        Assert.False(DeclaresDirective("[Setup]\nPrivilegesRequired=lowest\n", "PrivilegesRequiredOverridesAllowed"));
    }

    [Fact]
    public void Script_has_no_netsh_or_firewall_step()
    {
        Assert.Empty(LinesMentioning(Iss, "netsh", "advfirewall"));
    }

    [Fact]
    public void Control_netsh_step_is_detected_unless_commented_out()
    {
        Assert.Single(LinesMentioning(Offender, "netsh", "advfirewall"));
        Assert.Empty(LinesMentioning("; " + Offender, "netsh", "advfirewall"));
        Assert.Empty(LinesMentioning("   ;" + Offender, "netsh", "advfirewall"));
    }

    [Fact]
    public void Script_gates_nothing_on_admin_install_mode()
    {
        Assert.Empty(LinesMentioning(Iss, "IsAdminInstallMode"));
    }

    [Fact]
    public void Control_admin_gate_is_detected_unless_commented_out()
    {
        Assert.Single(LinesMentioning(Offender, "IsAdminInstallMode"));
        Assert.Empty(LinesMentioning("; " + Offender, "IsAdminInstallMode"));
    }
}
