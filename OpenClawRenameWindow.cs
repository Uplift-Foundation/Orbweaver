using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbweaver
{
    // CB-270: the dialog behind an OpenClaw orb's "Rename conversation…" row.
    //
    // The GroupStyleWindow pattern, for the same reasons: one window at a time,
    // Toggle() to open or bring forward, and the presentation halves behind test
    // seams because a headless suite must not Show() a window while the decisions
    // around it are worth testing. The constructor is internal so a test builds
    // the window without ever presenting it.
    //
    // The request is a seam as well (RenameFunc), so the suite answers it
    // without a gateway. What Save does:
    //   * a name that trims to nothing closes the window and sends nothing;
    //     there is no "save an empty name" request to make;
    //   * anything else disables the controls and awaits the request. Done
    //     closes the window. Anything else writes the same sentence the orb's
    //     rows use into the status line, re-enables the controls and leaves the
    //     window open, so the person can fix the name or give up.
    // The new title reaches the orb on the next poll, not from here.
    internal sealed class OpenClawRenameWindow : Window
    {
        internal delegate Task<(OpenClawActionOutcome Outcome, string? Detail)> RenameFunc(
            string orbSessionId, string label, CancellationToken ct);

        private static OpenClawRenameWindow? _open;

        private RenameFunc _rename;

        internal string OrbSessionId { get; private set; }

        internal TextBox NameBox { get; } = new();
        internal Button SaveButton { get; } = new() { Content = "Save", IsDefault = true };
        internal Button CancelButton { get; } = new() { Content = "Cancel", IsCancel = true };
        internal TextBlock StatusText { get; } = new();

        // Opens the dialog on a conversation, or brings the open one forward
        // pointed at that conversation.
        public static void Toggle(string orbSessionId, string? currentLabel, RenameFunc rename)
        {
            if (_open is not null)
            {
                _open.Retarget(orbSessionId, currentLabel, rename);
                (BringForwardForTests ?? Activate)(_open);
                return;
            }

            _open = new OpenClawRenameWindow(orbSessionId, currentLabel, rename);
            _open.Closed += OnClosed;
            (PresentForTests ?? Present)(_open);
        }

        internal static Action<OpenClawRenameWindow>? PresentForTests;
        internal static Action<OpenClawRenameWindow>? BringForwardForTests;

        // The singleton, for a test to inspect and clear between cases.
        internal static OpenClawRenameWindow? OpenForTests
        {
            get => _open;
            set => _open = value;
        }

        [ExcludeFromCodeCoverage]
        private static void Present(OpenClawRenameWindow window)
        {
            MacOSActivation.SetRegular();
            window.Show();
            window.Activate();
            window.NameBox.Focus();
            window.NameBox.SelectAll();
        }

        [ExcludeFromCodeCoverage]
        private static void Activate(OpenClawRenameWindow window)
        {
            MacOSActivation.SetRegular();
            window.Activate();
            window.NameBox.Focus();
            window.NameBox.SelectAll();
        }

        [ExcludeFromCodeCoverage]
        private static void OnClosed(object? sender, EventArgs e)
        {
            _open = null;
            MacOSActivation.SetAccessory();
        }

        internal OpenClawRenameWindow(string orbSessionId, string? currentLabel, RenameFunc rename)
        {
            OrbSessionId = orbSessionId;
            _rename = rename;

            Title = "Rename conversation";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            KeyDown += OnWindowKeyDown;

            NameBox.Text = currentLabel ?? "";
            NameBox.Watermark = "Conversation name";

            StatusText.FontSize = 12;
            StatusText.TextWrapping = TextWrapping.Wrap;
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
            StatusText.IsVisible = false;

            SaveButton.Click += async (_, _) => await SaveAsync();
            CancelButton.Click += (_, _) => CloseForReal();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { CancelButton, SaveButton }
            };

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Name", FontWeight = FontWeight.SemiBold },
                    NameBox,
                    StatusText,
                    buttons
                }
            };
        }

        // Pointed at another conversation while open: the same window, with the
        // new name in the box and any old error cleared.
        private void Retarget(string orbSessionId, string? currentLabel, RenameFunc rename)
        {
            OrbSessionId = orbSessionId;
            _rename = rename;
            NameBox.Text = currentLabel ?? "";
            StatusText.IsVisible = false;
            SetBusy(false);
        }

        internal static bool ShouldClose(Key key) => key == Key.Escape;

        internal void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (ShouldClose(e.Key)) CloseForReal();
        }

        private void CloseForReal() => Close();

        private void SetBusy(bool busy)
        {
            NameBox.IsEnabled = !busy;
            SaveButton.IsEnabled = !busy;
            CancelButton.IsEnabled = !busy;
        }

        internal async Task SaveAsync()
        {
            var name = (NameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                CloseForReal();
                return;
            }

            StatusText.IsVisible = false;
            SetBusy(true);

            var (outcome, detail) = await _rename(OrbSessionId, name, CancellationToken.None);

            if (outcome == OpenClawActionOutcome.Done)
            {
                CloseForReal();
                return;
            }

            StatusText.Text = OpenClawActionText.For(outcome, detail, OpenClawAction.Rename);
            StatusText.IsVisible = true;
            SetBusy(false);
        }
    }
}
