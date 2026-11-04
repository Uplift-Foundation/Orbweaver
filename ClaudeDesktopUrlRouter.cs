using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Orbweaver as the handler for Claude Desktop's URL schemes, forwarding
    // each link to the profile it belongs to.
    //
    // ClaudeDesktopUrlRouting has the why and the decision; this file is the
    // machinery around it: claiming the schemes from LaunchServices, keeping
    // track of which instance the user was last in, and doing the delivery.
    //
    // Delivery is `/usr/bin/open -a <bundle path> [--env ...] <url>`, the same
    // command the launcher already uses, and deliberately *without* -n. -n is
    // right when launching a profile, because the caller has just proved
    // nothing is running on that directory; here the opposite is wanted — an
    // already-running instance should receive the link, and starting a second
    // Chromium on a live userData directory is the leveldb corruption the whole
    // profile feature exists to avoid. Verified on a real machine: `open -a`
    // against a clone path delivered to the running instance and started
    // nothing new.
    internal static class ClaudeDesktopUrlRouter
    {
        private const string ClaudeDesktopBundleId = "com.anthropic.claudefordesktop";

        // In a holder rather than fields on this class, for the reason
        // BackgroundJobs.Cache gives: a static field initializer does not run
        // until some static field is touched, and the only thing that touches
        // these is Start, which is excluded. Left here they read as uncovered
        // lines in a class whose measured half never reaches them.
        [ExcludeFromCodeCoverage]
        private static class Once
        {
            internal static readonly object Gate = new();
            internal static bool Started;
        }

        private static int _lastActivePid;

        // What the last claim attempt did, for the settings window to show. A
        // silent failure here means links keep going to the wrong profile with
        // nothing to explain why, which is the state this bug was found in.
        public static string? Status { get; private set; }

        // Excluded from coverage: its whole body is a task that claims URL
        // schemes on the machine running it. The decision it eventually reaches
        // is WhatToDoAboutClaiming, which is measured.
        [ExcludeFromCodeCoverage]
        public static void Start()
        {
            if (!OperatingSystem.IsMacOS()) return;

            lock (Once.Gate)
            {
                if (Once.Started) return;
                Once.Started = true;
            }

            // Off the UI thread: this scans processes and shells out to plutil.
            Task.Run(() =>
            {
                try { ClaimIfWorthwhile(); }
                catch { /* never worth taking the app down for */ }
            });
        }

        // Claiming a system-wide URL scheme is not something to do speculatively.
        // With one profile there is nothing to route — a link can only be meant
        // for that profile — so the schemes are left exactly as they were, and
        // an install that never creates a second profile never notices this
        // feature exists.
        // Excluded from coverage: each arm either writes the machine's
        // URL-handler database or scans its processes to count profiles. The
        // rule itself is WhatToDoAboutClaiming below.
        [ExcludeFromCodeCoverage]
        private static void ClaimIfWorthwhile()
        {
            switch (WhatToDoAboutClaiming(
                OrbweaverSettings.RouteClaudeUrls,
                ClaudeDesktopManager.RouteCandidates().Count))
            {
                case ClaimAction.Restore:
                    Restore();
                    return;

                case ClaimAction.NotNeeded:
                    Status = NotNeededStatus;
                    return;

                default:
                    Claim();
                    return;
            }
        }

        internal enum ClaimAction { Restore, NotNeeded, Claim }

        internal const string NotNeededStatus = "not needed (one profile)";

        // Claiming a system-wide URL scheme is not something to do
        // speculatively. With one profile there is nothing to route — a link can
        // only be meant for that profile — so the schemes are left exactly as
        // they were, and an install that never creates a second profile never
        // notices this feature exists.
        //
        // Taking the count rather than asking for it is what makes that rule
        // assertable: asking means scanning the machine's real processes, and
        // then the rule is only ever exercised with whatever profiles the person
        // running the tests happens to have.
        internal static ClaimAction WhatToDoAboutClaiming(bool routeEnabled, int candidateCount)
        {
            if (!routeEnabled) return ClaimAction.Restore;

            return candidateCount < 2 ? ClaimAction.NotNeeded : ClaimAction.Claim;
        }

        // Excluded from coverage: sets the machine's default handler for
        // `claude:`. Its two decisions are measured — ClaimStatusFor for what the
        // settings window is told, and the guard on remembering a previous
        // handler, which is asserted through ShouldRememberPreviousHandler.
        [ExcludeFromCodeCoverage]
        internal static bool Claim()
        {
            if (!OperatingSystem.IsMacOS()) return false;

            var ownId = MacOSUrlScheme.OwnBundleId();
            if (ownId is null)
            {
                // A loose `dotnet run` binary has no bundle, so there would be
                // nothing for macOS to launch when a link arrived later —
                // claiming from here would break links until the next bundled
                // run put it right.
                Status = "not claimed (running unbundled)";
                return false;
            }

            var claimed = 0;

            foreach (var scheme in ClaudeDesktopUrlRouting.Schemes)
            {
                var current = MacOSUrlScheme.CurrentHandler(scheme);
                if (string.Equals(current, ownId, StringComparison.OrdinalIgnoreCase))
                {
                    claimed++;
                    continue;
                }

                // Remember what to put back, once, and only something that
                // isn't us — re-claiming must not overwrite the real previous
                // handler with our own id.
                if (ShouldRememberPreviousHandler(
                        scheme, current, OrbweaverSettings.PreviousClaudeUrlHandler))
                {
                    OrbweaverSettings.PreviousClaudeUrlHandler = current!;
                }

                if (MacOSUrlScheme.SetHandler(scheme, ownId)) claimed++;
            }

            var total = ClaudeDesktopUrlRouting.Schemes.Length;
            Status = ClaimStatusFor(claimed, total);
            return claimed == total;
        }

        // Remember what to put back, once, and only something that isn't us —
        // re-claiming must not overwrite the real previous handler with our own
        // id, which would make the setting a one-way door the second time it was
        // switched on.
        //
        // Only for "claude": it is the scheme a person recognises and the one
        // whose old owner is worth restoring, and remembering a different
        // handler per scheme would mean a setting that restores three different
        // things and can only record one.
        internal static bool ShouldRememberPreviousHandler(
            string scheme, string? current, string alreadyRemembered) =>
            scheme == "claude"
            && current is { Length: > 0 }
            && alreadyRemembered.Length == 0;

        // Said in full rather than as "failed", because a partial claim is a
        // real state with a real consequence — the schemes that did land route
        // correctly and the ones that did not still go to Default — and "failed"
        // would describe neither.
        internal static string ClaimStatusFor(int claimed, int total) =>
            claimed == total ? "routing links" : $"claimed {claimed} of {total} schemes";

        // Hands the schemes back to whoever had them — Claude Desktop, in every
        // case we have seen. Called when the user turns routing off, so the
        // setting is genuinely reversible rather than a one-way door.
        // Excluded from coverage: hands the machine's URL schemes back. Which
        // id it hands them to is HandlerToRestore, which is measured.
        [ExcludeFromCodeCoverage]
        internal static void Restore()
        {
            if (!OperatingSystem.IsMacOS()) return;

            var previous = HandlerToRestore(OrbweaverSettings.PreviousClaudeUrlHandler);

            var ownId = MacOSUrlScheme.OwnBundleId();

            foreach (var scheme in ClaudeDesktopUrlRouting.Schemes)
            {
                // Only undo our own claim. If something else owns the scheme
                // now, the user or another app changed it deliberately and it
                // is not ours to overwrite.
                var current = MacOSUrlScheme.CurrentHandler(scheme);
                if (ownId is null || !string.Equals(current, ownId, StringComparison.OrdinalIgnoreCase)) continue;

                MacOSUrlScheme.SetHandler(scheme, previous);
            }

            OrbweaverSettings.PreviousClaudeUrlHandler = "";
            Status = "not routing links";
        }

        // Claude Desktop when nothing was remembered, because that is who owned
        // the scheme in every case seen — and because turning the setting off
        // has to leave links working, not leave them owned by nobody.
        //
        // Reached with an empty string whenever routing is switched off without
        // ever having been on, which is an ordinary thing for someone to do
        // while looking at the switch.
        internal static string HandlerToRestore(string remembered) =>
            remembered.Length == 0 ? ClaudeDesktopBundleId : remembered;

        // Excluded from coverage: both arms write the machine's URL-handler
        // database, off a task, and then poke the tray.
        [ExcludeFromCodeCoverage]
        public static void SetEnabled(bool enabled)
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (OrbweaverSettings.RouteClaudeUrls == enabled) return;

            OrbweaverSettings.RouteClaudeUrls = enabled;

            Task.Run(() =>
            {
                try
                {
                    if (enabled) ClaimIfWorthwhile();
                    else Restore();
                }
                catch { }

                TrayController.Instance?.Refresh();
            });
        }

        // Which Claude Desktop instance the user was last in. Called from the
        // tray's existing 2s tick on the UI thread — NSWorkspace wants the main
        // thread's autorelease pool, and a sign-in's browser round trip is far
        // longer than one tick, so nothing finer is needed.
        //
        // Only ever *raised* by a Claude Desktop instance: once the browser
        // takes focus it must not overwrite the answer, because the answer is
        // precisely "which Claude window sent the user to the browser".
        // Excluded from coverage: asks the window server which application is
        // frontmost, which a headless runner has no answer for and which would
        // be whatever the person running the tests was looking at.
        [ExcludeFromCodeCoverage]
        public static void NoteFrontmost()
        {
            if (!OperatingSystem.IsMacOS()) return;

            try
            {
                var frontmost = MacOSWindowList.FrontmostPid();
                if (frontmost <= 0) return;

                // Snapshot rather than a fresh scan: this runs every two
                // seconds, and the snapshot is refreshed on the same tick.
                var match = ClaudeDesktopManager.Snapshot.Profiles
                    .Any(p => p.IsRunning && p.Pid == frontmost);

                if (match) Volatile.Write(ref _lastActivePid, frontmost);
            }
            catch
            {
                // Losing the hint costs routing accuracy, never correctness —
                // Choose has an answer for a pid of 0.
            }
        }

        internal static int LastActivePid => Volatile.Read(ref _lastActivePid);

        // The entry point Avalonia's protocol activation calls into.
        // Excluded from coverage: hands off to Deliver on a task, which runs
        // /usr/bin/open. Whether a url is ours at all is
        // ClaudeDesktopUrlRouting.Handles, which is pure and covered.
        [ExcludeFromCodeCoverage]
        public static void Handle(string url)
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (!ClaudeDesktopUrlRouting.Handles(url)) return;

            Task.Run(() =>
            {
                try { Deliver(url); }
                catch { /* a link that fails to route must not take the app down */ }
            });
        }

        // Excluded from coverage: scans processes and then opens a link for
        // real. The command it builds is ArgumentsFor, which is measured.
        [ExcludeFromCodeCoverage]
        private static void Deliver(string url)
        {
            var candidates = ClaudeDesktopManager.RouteCandidates();
            var route = ClaudeDesktopUrlRouting.Choose(candidates, LastActivePid);

            Run("/usr/bin/open", ArgumentsFor(route, url));
        }

        // With no profile to route to, still address a bundle by path rather
        // than calling plain `open <url>` — that would resolve the scheme
        // straight back to us and loop, which is the one failure mode of this
        // feature that a user cannot get out of without turning it off.
        internal static string[] ArgumentsFor(UrlRoute? route, string url) =>
            route is null
                ? new[] { "-a", "/Applications/Claude.app", url }
                : Arguments(route, url);

        // Pure, so the shape of the delivery command is testable without
        // opening anything. No -n, for the reason in this file's header.
        internal static string[] Arguments(UrlRoute route, string url)
        {
            var arguments = new List<string> { "-a", route.BundlePath };

            // Only meaningful when the instance is not already running — `open`
            // applies these at launch — but harmless when it is, and passing
            // them unconditionally means a link that has to start the profile
            // starts it on the right userData directory.
            //
            // Both selectors, for the reason in
            // ClaudeDesktopManager.LaunchArguments: Claude Desktop no longer
            // honours the environment variable, so a callback that started the
            // profile used to start it on Default — which is precisely the
            // failure this whole routing feature was built to stop, arriving
            // again by a different route.
            if (route.UserDataDir is { Length: > 0 } directory)
            {
                arguments.Add("--env");
                arguments.Add("CLAUDE_USER_DATA_DIR=" + directory);

                // The URL is an operand and has to precede --args, after which
                // open(1) hands everything to the application untouched.
                arguments.Add(url);
                arguments.Add("--args");
                arguments.Add("--user-data-dir=" + directory);
                return arguments.ToArray();
            }

            arguments.Add(url);
            return arguments.ToArray();
        }

        // Excluded from coverage: starts a process.
        [ExcludeFromCodeCoverage]
        private static bool Run(string executable, string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

                using var process = Process.Start(startInfo);
                if (process is null) return false;

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(10_000))
                {
                    try { ProcessTreeKill.Kill(process); } catch { }
                    return false;
                }

                stdout.GetAwaiter().GetResult();
                stderr.GetAwaiter().GetResult();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
