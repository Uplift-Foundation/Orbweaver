using System.Runtime.InteropServices;

namespace Orbweaver
{
    // Survive a SIGHUP on macOS, and say that it happened (CB-306).
    //
    // Orbweaver leads its own process group in launchd's session, and the
    // kernel sends that group SIGHUP+SIGCONT when the group becomes orphaned
    // while one of its members is stopped. ProcessTreeKill removes the cause
    // we know of; this is the backstop for any other way a stopped child can
    // be orphaned (a tool we spawn that stops its own children, say), because
    // a menu-bar app has no business exiting on a hangup — there is no
    // terminal to hang up.
    //
    // macOS only: on Windows .NET maps SIGHUP to CTRL_CLOSE_EVENT, and
    // cancelling that is not what "the console window was closed" should do.
    internal static class HangupGuard
    {
        // Held in a field because a PosixSignalRegistration that is collected
        // unregisters itself.
        private static PosixSignalRegistration? _registration;

        internal static bool Installed => _registration is not null;

        internal static void Install() => Install(OperatingSystem.IsMacOS());

        internal static void Install(bool macOS)
        {
            if (!macOS || _registration is not null) return;
            _registration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Handle);
        }

        // Swallow the signal and leave a line behind. The signal is still
        // *delivered* — one entry in crash.log per hangup is the evidence that
        // the guard fired, which is the only way to tell "never happened" from
        // "happened and was absorbed".
        internal static void Handle(PosixSignalContext context)
        {
            context.Cancel = true;
            CrashLog.Record("SIGHUP received and ignored (CB-306)", null);
        }
    }
}
