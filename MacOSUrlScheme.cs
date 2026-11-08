using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbweaver
{
    // Reading and setting which app macOS hands a URL scheme to.
    //
    // Declaring CFBundleURLTypes in Info.plist only makes an app *eligible* to
    // handle a scheme. Claude Desktop declares `claude:` too and got there
    // first, so eligibility alone changes nothing — the default has to be
    // claimed explicitly, which is what this file is for. See
    // ClaudeDesktopUrlRouting for why claiming it is the fix rather than an
    // intrusion: LaunchServices resolves a scheme to a bundle *id*, and every
    // tinted clone shares Claude Desktop's, so the id cannot identify a
    // profile and the link always lands in Default.
    //
    // LSSetDefaultHandlerForURLScheme is deprecated in favour of
    // -[NSWorkspace setDefaultApplicationAtURL:toOpenURLsWithScheme:completionHandler:],
    // which takes a completion *block*. Constructing an Objective-C block from
    // .NET means hand-building a block literal and its descriptor, which is a
    // lot of unsafe surface for a call whose deprecated form is two CFStrings
    // and still works on every macOS this app supports (LSMinimumSystemVersion
    // is 11.0). If a future macOS removes it, the failure is visible and
    // recoverable — the setting below goes back to reporting "not claimed",
    // and links behave exactly as they did before this router existed.
    // Excluded from coverage in full, the same way every other P/Invoke wrapper
    // in this repo is (MacOSAppActivation, MacOSWindowList, WindowsAppLookup and
    // the rest). Every member either calls into CoreFoundation and
    // LaunchServices or launches plutil, and the two that matter *write the
    // machine's URL-handler database* — a test that reached SetHandler would
    // take `claude:` off Claude Desktop on the machine running it and leave it
    // that way.
    //
    // What can be decided without any of that is decided in
    // ClaudeDesktopUrlRouter and ClaudeDesktopUrlRouting, which are measured:
    // whether claiming is worth doing, what to put back afterwards, what the
    // settings window is told, and which profile a link belongs to.
    [ExcludeFromCodeCoverage]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    internal static class MacOSUrlScheme
    {
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const string CoreServices =
            "/System/Library/Frameworks/CoreServices.framework/CoreServices";

        private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(
            IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr reference);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool CFStringGetCString(
            IntPtr reference, byte[] buffer, nint size, uint encoding);

        [DllImport(CoreServices)]
        private static extern int LSSetDefaultHandlerForURLScheme(IntPtr scheme, IntPtr bundleId);

        [DllImport(CoreServices)]
        private static extern IntPtr LSCopyDefaultHandlerForURLScheme(IntPtr scheme);

        // Which bundle id currently owns a scheme, or null if nothing does.
        // Used both to decide whether claiming is needed and to record what to
        // put back afterwards.
        public static string? CurrentHandler(string scheme)
        {
            if (!OperatingSystem.IsMacOS()) return null;

            var schemeRef = IntPtr.Zero;
            var handlerRef = IntPtr.Zero;

            try
            {
                schemeRef = CFStringCreateWithCString(IntPtr.Zero, scheme, Utf8);
                if (schemeRef == IntPtr.Zero) return null;

                handlerRef = LSCopyDefaultHandlerForURLScheme(schemeRef);
                if (handlerRef == IntPtr.Zero) return null;

                // A bundle id is short; 512 bytes is generous rather than tight,
                // and CFStringGetCString fails rather than truncating.
                var buffer = new byte[512];
                if (!CFStringGetCString(handlerRef, buffer, buffer.Length, Utf8)) return null;

                var end = Array.IndexOf(buffer, (byte)0);
                if (end < 0) end = buffer.Length;
                var value = Encoding.UTF8.GetString(buffer, 0, end);

                return value.Length == 0 ? null : value;
            }
            catch
            {
                return null;
            }
            finally
            {
                // LSCopy* follows the Core Foundation copy rule, so the result is
                // ours to release; the scheme string we created is too.
                if (handlerRef != IntPtr.Zero) CFRelease(handlerRef);
                if (schemeRef != IntPtr.Zero) CFRelease(schemeRef);
            }
        }

        // True when the scheme now resolves to the given bundle id. Returns
        // false rather than throwing on refusal — nothing here is worth taking
        // the app down for, and the caller reports it instead.
        public static bool SetHandler(string scheme, string bundleId)
        {
            if (!OperatingSystem.IsMacOS()) return false;

            var schemeRef = IntPtr.Zero;
            var bundleRef = IntPtr.Zero;

            try
            {
                schemeRef = CFStringCreateWithCString(IntPtr.Zero, scheme, Utf8);
                bundleRef = CFStringCreateWithCString(IntPtr.Zero, bundleId, Utf8);
                if (schemeRef == IntPtr.Zero || bundleRef == IntPtr.Zero) return false;

                return LSSetDefaultHandlerForURLScheme(schemeRef, bundleRef) == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (bundleRef != IntPtr.Zero) CFRelease(bundleRef);
                if (schemeRef != IntPtr.Zero) CFRelease(schemeRef);
            }
        }

        // This process's own bundle id, read from the running bundle rather than
        // hard-coded, so a rename in build-macos-app.sh can't silently leave the
        // router claiming schemes for an id that no longer exists. Null for a
        // loose `dotnet run` binary, which has no bundle — and which therefore
        // must not claim anything, since there would be nothing for macOS to
        // launch when a link arrived later.
        public static string? OwnBundleId()
        {
            if (!OperatingSystem.IsMacOS()) return null;

            try
            {
                // .../Orbweaver.app/Contents/MacOS/Orbweaver
                var executable = Environment.ProcessPath;
                if (executable is null) return null;

                var macOs = Path.GetDirectoryName(executable);
                var contents = macOs is null ? null : Path.GetDirectoryName(macOs);
                var bundle = contents is null ? null : Path.GetDirectoryName(contents);

                if (contents is null || bundle is null) return null;
                if (!bundle.EndsWith(".app", StringComparison.Ordinal)) return null;

                var plist = Path.Combine(contents, "Info.plist");
                if (!File.Exists(plist)) return null;

                return PlistValue(plist, "CFBundleIdentifier");
            }
            catch
            {
                return null;
            }
        }

        private static string? PlistValue(string plist, string key)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/plutil")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[] { "-extract", key, "raw", "-o", "-", plist })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process is null) return null;

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(5_000))
                {
                    try { ProcessTreeKill.Kill(process); } catch { }
                    return null;
                }

                stderr.GetAwaiter().GetResult();
                if (process.ExitCode != 0) return null;

                var value = stdout.GetAwaiter().GetResult().Trim();
                return value.Length == 0 ? null : value;
            }
            catch
            {
                return null;
            }
        }
    }
}
