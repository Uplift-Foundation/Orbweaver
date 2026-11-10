using System.Diagnostics;
using System.Text;

namespace Orbweaver.ScreenReadProbe;

internal static class Proc
{
    // stdout of a command, UTF-8, throwing with stderr on a non-zero exit.
    internal static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + file);
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} exited {p.ExitCode}: {err.Result.Trim()}");
        return o;
    }
}
