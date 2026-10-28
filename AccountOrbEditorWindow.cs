using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Orbweaver
{
    // CB-287: the small window "Customize…" opens on a usage orb. One per account:
    // choosing Customize… again while it is up brings it forward instead of
    // stacking a second editor over the same override.
    internal sealed class AccountOrbEditorWindow : Window
    {
        private static readonly Dictionary<string, AccountOrbEditorWindow> Windows =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly AccountOrbIdentity _account;
        private readonly Action _onChanged;

        internal AccountOrbEditorPanel Panel { get; }

        // onChanged is called after every write to this account's look, for the
        // caller to redraw whatever it owns.
        internal static void Open(AccountOrbIdentity account, Action onChanged)
        {
            if (Windows.TryGetValue(account.PositionKey, out var existing))
            {
                existing.Activate();
                return;
            }

            var window = new AccountOrbEditorWindow(account, onChanged);
            Windows[account.PositionKey] = window;
            window.Show();

            // Typing needs the app active. This opens from a click on an orb (one
            // of our own windows), which activates it, but Activate() is what makes
            // the keystrokes land and costs nothing when it already has.
            window.Activate();
        }

        internal static AccountOrbEditorWindow? Find(string positionKey) =>
            Windows.TryGetValue(positionKey, out var w) ? w : null;

        // For tests, which must not leave a window registered for the next one.
        internal static int OpenCount => Windows.Count;

        private AccountOrbEditorWindow(AccountOrbIdentity account, Action onChanged)
        {
            _account = account;
            _onChanged = onChanged;

            Title = $"Customize {account.DerivedLabel}";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Panel = new AccountOrbEditorPanel(account) { Margin = new Thickness(16) };
            Panel.EscapeWithNothingToRevert = Close;
            Content = Panel;

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };

            // Subscribed for as long as the window lives; the panel has its own
            // subscription for refreshing itself, this one is for the caller.
            AccountOrbCustomization.Changed += OnChanged;
            Closed += (_, _) =>
            {
                AccountOrbCustomization.Changed -= OnChanged;
                if (Windows.TryGetValue(_account.PositionKey, out var w) && ReferenceEquals(w, this))
                    Windows.Remove(_account.PositionKey);
            };
        }

        private void OnChanged(string positionKey)
        {
            if (string.Equals(positionKey, _account.PositionKey, StringComparison.OrdinalIgnoreCase))
                _onChanged();
        }
    }
}
