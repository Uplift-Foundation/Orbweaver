using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Orbweaver
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // App.axaml declares the macOS theme, which is right for a Mac and
            // wrong for Windows. Fluent is Microsoft's own design language, so
            // it's the correct answer there — and restyling AppKit's controls by
            // hand was the alternative, which kept landing close-but-wrong
            // because their metrics and states aren't published anywhere to copy.
            if (!OperatingSystem.IsMacOS()) UseFluentTheme();
        }

        // Excluded from coverage: the non-macOS arm of a platform choice, and
        // coverage is gathered from one platform's run — so on the macOS leg this
        // cannot execute, and on the Windows leg the macOS arm cannot. Neither is
        // untested so much as untestable *together*.
        //
        // What it does is swap the whole style set for Fluent, which is
        // Microsoft's own design language and therefore the right answer on
        // Windows; the alternative was restyling AppKit's controls by hand, which
        // kept landing close-but-wrong because their metrics and states are not
        // published anywhere to copy.
        [ExcludeFromCodeCoverage]
        private void UseFluentTheme()
        {
            Styles.Clear();
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        }

        // Excluded from coverage: every line below the guard is unreachable
        // under test, and by design rather than by omission. Avalonia's headless
        // lifetime is not an IClassicDesktopStyleApplicationLifetime — it is
        // null outright — so the guard never opens, which is precisely what lets
        // tests/UiTests host the *real* App class instead of a stand-in (see
        // that suite's TestAppBuilder, whose own comment records the same
        // finding from a spike). Nothing here could be made to run without
        // giving the test host a desktop lifetime, and then it would start a
        // real SessionManager polling the temp directory and put a tray icon in
        // the menu bar of the machine running the suite.
        //
        // The single-instance mutex used to be claimed here, first, with the
        // loser calling desktop.Shutdown() and returning — which ran inside
        // Avalonia's own startup, before its main loop existed, and
        // Shutdown() tearing the dispatcher down there was what turned a
        // routine "someone else is already running" into an uncaught
        // InvalidOperationException and a SIGABRT (CB-178). That decision now
        // happens in Program.Main, through Startup.Run, entirely before
        // BuildAvaloniaApp().StartWithClassicDesktopLifetime is ever called —
        // a duplicate instance now returns from Main normally, having never
        // reached this method at all. See SingleInstance.cs for the claim
        // itself and Startup.cs for where it's sequenced.
        [ExcludeFromCodeCoverage]
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Orb windows come and go with sessions; the app itself only
                // exits via the context menu.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // QA round 3 (CB-167), finding 5: TrayController.Shutdown
                // (the tray menu's own Quit) used to be the only place that
                // called ChimePlayer.StopAll, so the orb menu's own "Exit
                // Orbweaver" (OrbWindow.Exit_Click) and the OS's Cmd-Q
                // both skipped it — either one could leave a chime still
                // mid-playback after the app itself was gone. Every one of
                // those paths ends the same way, by calling this lifetime's
                // own Shutdown(), which is what fires Exit — so wiring the
                // stop here once covers all of them by construction, rather
                // than by remembering to add the call at every call site
                // that can end the app.
                //
                // ShutdownRequested is wired too, round 4: Cmd-Q on macOS
                // reaches the app through the OS's own Quit handling before
                // Exit does, and Exit alone was not confirmed to fire on
                // that specific path without a real Mac to drive it through.
                // It calls a different method, though (round 4, item 4) —
                // ShutdownRequested fires for a quit that can still be
                // cancelled (its own Cancel property, or the OS refusing for
                // a reason of its own), and StopAll's _stopped flag is
                // sticky for the rest of the process's life by design. Wiring
                // StopAll itself here would leave a *cancelled* quit
                // permanently deaf — silencing every chime for the rest of
                // the run over a quit that never actually happened.
                // KillCurrentlyPlayingForCancellableShutdown kills whatever
                // is currently playing (the user did ask to quit, and a
                // chime shouldn't survive that choice even if the app does)
                // without setting the flag; only Exit, firing once shutdown
                // is genuinely proceeding, may do that.
                //
                // This whole method only ever runs under a real desktop
                // lifetime (see its own header comment on why it is excluded
                // from coverage entirely), so both lines are excluded along
                // with it rather than tested directly; ChimePlayer.StopAll
                // and KillCurrentlyPlayingForCancellableShutdown each have
                // their own real-process test proving what they actually do.
                desktop.Exit += (_, _) => ChimePlayer.StopAll();
                desktop.ShutdownRequested += (_, _) => ChimePlayer.KillCurrentlyPlayingForCancellableShutdown();

                new SessionManager().Start();

                // The updater (CB-308). After SessionManager so the tray exists for
                // PendingChanged to refresh. The post-update notice records this
                // run's version before it decides to show anything, and the
                // failure notice reads what a finisher left behind.
                UpdateServices.Start();
                UpdateFailureNotice.RunWindowsAtLaunch(UpdateServices.Checker?.CurrentVersion ?? "");
                UpdatePostInstallNotice.Run();
                UpdateFailureNotice.Run();

                // Hide/show orbs (and room for more — see HotkeyRegistry) from
                // a system-wide key combo, so the toggle already on the tray
                // menu doesn't require finding the menu bar icon first. Started
                // after SessionManager so TrayController.ToggleOrbsVisible has
                // something real to toggle the moment a key could fire.
                GlobalHotkeys.Start();

                // Claude Desktop's URL schemes resolve to a bundle *id*, and
                // every tinted clone shares Claude Desktop's — so a sign-in
                // callback cannot say which profile it belongs to and always
                // lands in Default. Orbweaver claims the schemes and
                // forwards each link to the right instance instead; see
                // ClaudeDesktopUrlRouting. A no-op off macOS, and a no-op with
                // fewer than two profiles, where there is nothing to route.
                ClaudeDesktopUrlRouter.Start();

                // The other half of that: the links themselves. Avalonia
                // surfaces macOS protocol activation here, so no Apple Event
                // plumbing of our own is needed.
                if (ApplicationLifetime is IActivatableLifetime activatable)
                {
                    activatable.Activated += (_, args) =>
                    {
                        if (args is ProtocolActivatedEventArgs protocol)
                        {
                            ClaudeDesktopUrlRouter.Handle(protocol.Uri.ToString());
                        }
                    };
                }

                // An upgrade leaves the previous release's speech engine on disk
                // and this build looking for its own. Speaking still works from
                // the old one, so nothing here is urgent — but the matching
                // engine has to be fetched by *something*, or the app runs a
                // steadily older engine until the user happens to toggle the
                // setting. This is that something, and it is a no-op unless the
                // feature is already enabled and already installed.
                //
                // The voice list is cached for the process lifetime and is built
                // by asking the engine, so it has to be dropped once a different
                // engine is in place — otherwise the picker keeps showing the old
                // engine's answer until restart.
                _ = NeuralSpeech.EnsureCurrentAsync()
                    .ContinueWith(_ => TextToSpeech.InvalidateVoiceCache(), TaskScheduler.Default);

                // The installed-hook refresh (CB-265) used to run here, and
                // here is behind the screen-lock wait: on a Mac whose screen
                // never unlocks the UI never starts and the hook was never
                // refreshed (CB-306). It runs from Startup.Run's refreshHooks
                // step now, before the wait, with the other display-free work.

                // Development entry point: `Orbweaver --settings` opens the
                // settings window at launch. It is otherwise only reachable by
                // clicking the status-bar menu, which is awkward when the thing
                // being changed *is* that window.
                if (desktop.Args?.Contains("--settings") == true)
                {
                    SettingsWindow.Toggle();
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
