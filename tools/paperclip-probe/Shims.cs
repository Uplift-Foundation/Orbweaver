using System;

namespace Orbweaver
{
    // PaperclipHttp words a connect failure through OpenClawGateway, whose file
    // drags in the whole gateway stack. The probe prints the plain message
    // instead; the macOS Local Network hint is the app's to append.
    internal static class OpenClawGateway
    {
        internal static string ExplainConnectFailure(Exception ex, bool onMacOS) => ex.Message;

        // What PaperclipHttp.Explain asks of it; the probe never appends the macOS hint.
        internal const string LocalNetworkHint = "";
        internal static bool IsHostUnreachable(Exception ex) => false;
    }

    // PaperclipHttp.DefaultLog writes through the app's diagnostics.log, which drags in
    // CrashLog. The probe is a console program: its failure lines go to its stderr.
    internal static class DiagnosticsLog
    {
        internal static void Write(string line) => Console.Error.WriteLine(line);
    }
}
