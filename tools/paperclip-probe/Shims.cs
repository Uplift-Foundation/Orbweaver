using System;

namespace Orbweaver
{
    // PaperclipHttp words a connect failure through OpenClawGateway, whose file
    // drags in the whole gateway stack. The probe prints the plain message
    // instead; the macOS Local Network hint is the app's to append.
    internal static class OpenClawGateway
    {
        internal static string ExplainConnectFailure(Exception ex, bool onMacOS) => ex.Message;
    }
}
