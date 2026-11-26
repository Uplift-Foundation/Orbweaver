using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

// CB-474: a named-account launch on Windows reaches the CLI with the account's
// folder exactly, spaces and all. The start info is run for real through cmd,
// which is where CB-432's construction went wrong, but without conhost: conhost
// always opens a console window, and a CI runner must not. conhost's own part
// (it takes the quotes off the line it hands cmd) was measured on the Windows PC
// and is why the account no longer travels in that line at all; the control
// below reproduces what that stripping did, so the mechanism stays pinned.
//
// Windows only: the cases return early elsewhere, where there is no cmd.exe.
public class NamedAccountConsoleTests
{
    private const string Folder = @"C:\Users\a b\.claude-sundaysky";

    private static string PowerShell =>
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    // Prints the variable's length, then the variable, so a trailing space shows.
    private static readonly string[] Report =
        ["-NoProfile", "-Command", "$env:CLAUDE_CONFIG_DIR.Length; $env:CLAUDE_CONFIG_DIR"];

    // What conhost does with the start info, minus its window: run cmd with the
    // same arguments and the same environment, /c so it exits.
    private static string[] RunWithoutConhost(ProcessStartInfo start)
    {
        Assert.Equal("conhost.exe", start.FileName);
        var cmd = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = start.WorkingDirectory,
        };
        var args = start.ArgumentList.ToList();
        Assert.Equal(new[] { "cmd.exe", "/k" }, args.Take(2));
        cmd.ArgumentList.Add("/c");
        foreach (var a in args.Skip(2)) cmd.ArgumentList.Add(a);
        foreach (var (k, v) in start.Environment) cmd.Environment[k] = v;
        return Run(cmd);
    }

    private static string[] Run(ProcessStartInfo start)
    {
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30_000));
        return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void The_cli_gets_the_account_folder_exactly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = NewChatCommand.ConsoleStartInfoForAccount(
            NewChatCli.ClaudeCode, PowerShell, Path.GetTempPath(), Report, Folder)!;
        var lines = RunWithoutConhost(start);
        Assert.Equal(new[] { Folder.Length.ToString(), Folder }, lines);
    }

    [Fact]
    public void Control_the_line_conhost_handed_cmd_before_the_fix_adds_a_space()
    {
        if (!OperatingSystem.IsWindows()) return;
        // CB-432's line as cmd received it on the PC: quotes gone, so `set`
        // keeps the space before && in the value.
        var cmd = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            Arguments = $"/c set CLAUDE_CONFIG_DIR={Folder} && {PowerShell} {string.Join(" ", Report.Take(2))} \"{Report[2]}\"",
        };
        cmd.Environment.Remove("CLAUDE_CONFIG_DIR");
        var lines = Run(cmd);
        Assert.Equal(new[] { (Folder.Length + 1).ToString(), Folder }, lines.Select(l => l.TrimEnd()));
    }
}
