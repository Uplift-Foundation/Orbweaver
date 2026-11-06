using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbweaver
{
    // CB-308: what to tell someone the first time the app starts after an update.
    //
    // On macOS an update swaps the bundle, and macOS ties every privacy grant to
    // the code identity of the thing that holds it. A replaced bundle has a new
    // CDHash, so the grants that identity held do not carry over — Local Network
    // (OpenClaw's gateway and the peer link start failing with EHOSTUNREACH, and
    // nothing prompts loudly enough to notice in a menu-bar app), Automation
    // (click-to-focus goes quiet), Accessibility, and Microphone. The notice says
    // so once, in one sentence, because the alternative is a user finding out by
    // a feature silently not working.
    //
    // The sentence is shown on every Mac update, whether or not the user has
    // configured anything that needs Local Network: a grant that is invisible
    // until it is needed is exactly the one worth naming up front. The button
    // opens the Local Network pane, and appears only when OpenClaw or the peer
    // link is configured, since those are the two things that fail loudly
    // without it. Windows has no equivalent grant to lose, so it gets nothing.
    internal static class UpdatePostInstallNotice
    {
        internal sealed record Decision(bool Show, bool OfferLocalNetwork);

        internal const string Sentence =
            "macOS asks again after an update: because the app was replaced, you may need to "
            + "re-allow Local Network, Automation (click-to-focus), Accessibility and Microphone "
            + "for it in System Settings → Privacy & Security.";

        // Pure, so every arm is a case. lastRunVersion is what the previous run
        // recorded; empty means there was no previous run (a fresh install),
        // which is not an update and gets no notice.
        internal static Decision Decide(
            bool onMac, string? lastRunVersion, string currentVersion, bool openClaw, bool peerLink)
        {
            if (!onMac) return new Decision(false, false);
            if (string.IsNullOrEmpty(lastRunVersion)) return new Decision(false, false);
            if (string.Equals(lastRunVersion, currentVersion, StringComparison.Ordinal))
                return new Decision(false, false);

            return new Decision(true, openClaw || peerLink);
        }

        // Called once from App startup, after the tray exists. Records the version
        // it ran as before deciding to show anything, so a notice that was shown
        // (or a window that threw while opening) is never shown a second time.
        [ExcludeFromCodeCoverage]
        internal static void Run()
        {
            var current = NeuralSpeech.ResolveVersion(
                System.Reflection.Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion);

            RunFor(
                OperatingSystem.IsMacOS(),
                OrbweaverSettings.LastRunVersion,
                current,
                OrbweaverSettings.OpenClawEnabled,
                OrbweaverSettings.PeerLinkEnabled,
                v => OrbweaverSettings.LastRunVersion = v,
                d => UpdateMessageWindow.Toggle($"{Brand.DisplayName} was updated", Sentence, d.OfferLocalNetwork));
        }

        internal static Decision RunFor(
            bool onMac, string? lastRunVersion, string current, bool openClaw, bool peerLink,
            Action<string> record, Action<Decision> show)
        {
            var decision = Decide(onMac, lastRunVersion, current, openClaw, peerLink);

            record(current);
            if (decision.Show) show(decision);

            return decision;
        }
    }

    // The small window for the sentence above, and for the plain answers a manual
    // check and a failed update give (UpdateServices). The same singleton-and-seams shape
    // as UpdateNoticeWindow, and non-modal for the same reason.
    internal sealed class UpdateMessageWindow : Window
    {
        private static UpdateMessageWindow? _open;
        private readonly Action _openLocalNetwork;

        internal TextBlock Message { get; } = new();
        internal Button OpenButton { get; } = new() { Content = "Open Local Network settings" };
        internal Button LogButton { get; } = new() { Content = "Open log" };
        private string? _logPath;
        private readonly Action<string> _openFile;

        internal Button OkButton { get; } = new() { Content = "OK", IsDefault = true, IsCancel = true };

        public static void Toggle(string title, string message, bool offerLocalNetwork, string? logPath = null)
        {
            if (_open is not null)
            {
                _open.Retarget(title, message, offerLocalNetwork, logPath);
                (BringForwardForTests ?? Activate)(_open);
                return;
            }

            _open = new UpdateMessageWindow(title, message, offerLocalNetwork, OpenLocalNetworkPane, logPath, OpenFile);
            _open.Closed += OnClosed;
            (PresentForTests ?? Present)(_open);
        }

        internal static Action<UpdateMessageWindow>? PresentForTests;
        internal static Action<UpdateMessageWindow>? BringForwardForTests;

        internal static UpdateMessageWindow? OpenForTests
        {
            get => _open;
            set => _open = value;
        }

        // The same pane OpenClawGateway.LocalNetworkHint sends people to, because
        // it is the one whose loss fails silently.
        [ExcludeFromCodeCoverage]
        private static void OpenLocalNetworkPane()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork" },
                    UseShellExecute = false
                })?.Dispose();
            }
            catch
            {
            }
        }

        // The platform's default viewer for a file, never the file run as a program:
        // `open <file>` hands a .log to whatever app is associated with it, and
        // ShellExecute does the same on Windows. A path that is not a plain .log
        // never reaches here (UpdateFailureNotice.TrustedLog).
        [ExcludeFromCodeCoverage]
        private static void OpenFile(string path)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                if (OperatingSystem.IsMacOS())
                {
                    psi.FileName = "open";
                    psi.ArgumentList.Add(path);
                    psi.UseShellExecute = false;
                }
                else
                {
                    psi.FileName = path;
                    psi.UseShellExecute = true;
                }

                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch
            {
            }
        }

        [ExcludeFromCodeCoverage]
        private static void Present(UpdateMessageWindow window)
        {
            MacOSActivation.SetRegular();
            window.Show();
            window.Activate();
        }

        [ExcludeFromCodeCoverage]
        private static void Activate(UpdateMessageWindow window)
        {
            MacOSActivation.SetRegular();
            window.Activate();
        }

        [ExcludeFromCodeCoverage]
        private static void OnClosed(object? sender, EventArgs e)
        {
            _open = null;
            MacOSActivation.SetAccessory();
        }

        internal TextBlock Heading { get; } = new() { FontWeight = FontWeight.SemiBold, FontSize = 15 };

        private void Retarget(string title, string message, bool offerLocalNetwork, string? logPath)
        {
            _logPath = logPath;
            LogButton.IsVisible = logPath is not null;
            Title = title;
            Heading.Text = title;
            Message.Text = message;
            OpenButton.IsVisible = offerLocalNetwork;
        }

        internal UpdateMessageWindow(
            string title, string message, bool offerLocalNetwork, Action openLocalNetwork,
            string? logPath = null, Action<string>? openFile = null)
        {
            _logPath = logPath;
            _openFile = openFile ?? (_ => { });
            LogButton.IsVisible = logPath is not null;
            LogButton.Click += (_, _) => { if (_logPath is not null) _openFile(_logPath); };
            _openLocalNetwork = openLocalNetwork;

            Title = title;
            Heading.Text = title;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

            Message.Text = message;
            Message.TextWrapping = TextWrapping.Wrap;

            OpenButton.IsVisible = offerLocalNetwork;
            OpenButton.Click += (_, _) => _openLocalNetwork();
            OkButton.Click += (_, _) => Close();

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    Heading,
                    Message,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { LogButton, OpenButton, OkButton }
                    }
                }
            };
        }
    }
}
