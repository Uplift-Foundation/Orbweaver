using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Orbweaver
{
    // The Paperclip group of Settings (CB-421, plan 2.2 to 2.4). Its own file so
    // the one-way dependency is plain: this reads PaperclipConnector.Current and
    // PaperclipSessions, and the rest of SettingsWindow knows only that it exists.
    //
    // No control here ever holds the key. Connecting is one approval in the
    // browser; the only thing typed is an address.
    internal sealed partial class SettingsWindow
    {
        private TextBlock? _paperclipStatus;

        internal string? PaperclipStatusText => _paperclipStatus?.Text;

        // Changes under the user's hands: an approval lands, discovery finishes,
        // a key is revoked in Paperclip. A joined string, so the tick can tell
        // when the rows are stale and rebuild them.
        private string _paperclipKey = PaperclipKey();

        private static string PaperclipKey()
        {
            var c = PaperclipConnector.Current;
            return string.Join("|", c.Phase, c.Message, c.Found.Count, c.Companies.Count,
                PaperclipSessions.Link, OrbweaverSettings.PaperclipEnabled, OrbweaverSettings.PaperclipCompanyId);
        }

        // Called from OnStatusTick.
        private void TickPaperclip()
        {
            if (PaperclipKey() != _paperclipKey) { Rebuild(); return; }
            if (_paperclipStatus is not null)
            {
                var text = PaperclipSessions.StatusText;
                if (_paperclipStatus.Text != text) _paperclipStatus.Text = text;
            }
        }

        // The address the user would be connecting to: what was found on this
        // machine, else whatever was connected before, else the default.
        internal static string PaperclipSuggestedAddress(IReadOnlyList<PaperclipDiscovery.Found> found, string stored) =>
            found.Count > 0 ? found[0].BaseUrl : stored.Length > 0 ? stored : "";

        // "The key is sent unencrypted on this network" - when the scheme is
        // http and the host is not this machine (plan 2.3, step 7).
        internal static bool SendsKeyUnencrypted(string? address)
        {
            if (!Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp) return false;
            return !(u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        }

        internal Control[] PaperclipRows()
        {
            _paperclipKey = PaperclipKey();
            _paperclipStatus = null;
            var conn = PaperclipConnector.Current;
            var rows = new List<Control>();

            if (conn.Phase == PaperclipPhase.Approving)
            {
                rows.Add(NoteRow(Note(conn.Message)));
                var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Left };
                cancel.Click += (_, _) => conn.CancelConnect();
                rows.Add(Row("", cancel));
                return rows.ToArray();
            }

            if (!OrbweaverSettings.PaperclipEnabled) return DisconnectedRows(conn, rows);

            _paperclipStatus = Note(PaperclipSessions.StatusText);
            rows.Add(NoteRow(_paperclipStatus));

            if (conn.Companies.Count > 1)
                rows.Add(Row("Company", CompanyPicker(conn)));

            rows.Add(Row("When agents are asleep", SleepingPicker(),
                "Up to eight agents always show. Beyond that, sleeping agents fold into their lead as a count, "
                + "and unfold while working or needing you."));

            if (SendsKeyUnencrypted(OrbweaverSettings.PaperclipUrl))
                rows.Add(NoteRow(Note("The key is sent unencrypted on this network.")));

            if (PaperclipSessions.Link == PaperclipLink.NeedsReconnect)
            {
                var reconnect = new Button { Content = "Reconnect", HorizontalAlignment = HorizontalAlignment.Left };
                reconnect.Click += (_, _) => _ = OnPaperclipConnectAsync(OrbweaverSettings.PaperclipUrl);
                rows.Add(Row("", reconnect));
            }

            var disconnect = new Button { Content = "Disconnect", HorizontalAlignment = HorizontalAlignment.Left };
            disconnect.Click += (_, _) => _ = OnPaperclipDisconnectAsync();
            rows.Add(Row("", disconnect));

            if (conn.Message.Length > 0) rows.Add(NoteRow(Note(conn.Message)));
            return rows.ToArray();
        }

        private Control[] DisconnectedRows(IPaperclipConnector conn, List<Control> rows)
        {
            var suggested = PaperclipSuggestedAddress(conn.Found, OrbweaverSettings.PaperclipUrl);

            rows.Add(NoteRow(Note(conn.Found.Count > 0
                ? "Paperclip found on this machine. Show your company?"
                : "Shows the agents of a Paperclip company as orbs: asleep, working, waiting on you, or broke. "
                  + "Connecting is one approval in your browser. Nothing is installed in your Paperclip.")));

            var box = new TextBox { Text = suggested, Watermark = "http://127.0.0.1:3100", Width = 240 };
            var unencrypted = Note("The key is sent unencrypted on this network.");
            var unencryptedRow = NoteRow(unencrypted);
            unencrypted.IsVisible = unencryptedRow.IsVisible = SendsKeyUnencrypted(suggested);
            // The property, not TextChanged: the event is raised by the text input
            // pipeline, and the note should follow the value however it changed.
            // The row is hidden with it, or an empty padded bar is left behind.
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                    unencrypted.IsVisible = unencryptedRow.IsVisible = SendsKeyUnencrypted(box.Text);
            };
            rows.Add(Row("Address", box));
            rows.Add(unencryptedRow);

            var connect = new Button { Content = "Connect", HorizontalAlignment = HorizontalAlignment.Left };
            connect.Click += (_, _) => _ = OnPaperclipConnectAsync(string.IsNullOrWhiteSpace(box.Text)
                ? PaperclipDiscovery.DefaultAddress : box.Text!);
            rows.Add(Row("", connect));

            if (conn.Phase == PaperclipPhase.Failed && conn.Message.Length > 0)
                rows.Add(NoteRow(Note(conn.Message)));
            return rows.ToArray();
        }

        private static TextBlock Note(string text) => new()
        {
            Text = text, FontSize = 12, Opacity = 0.75, TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        private Control CompanyPicker(IPaperclipConnector conn)
        {
            var companies = conn.Companies.ToList();
            var combo = new ComboBox
            {
                ItemsSource = companies.Select(c => c.Name).ToList(),
                SelectedIndex = companies.FindIndex(c => c.Id == OrbweaverSettings.PaperclipCompanyId),
                MinWidth = 168
            };
            combo.SelectionChanged += (_, _) =>
            {
                var i = combo.SelectedIndex;
                if (i >= 0 && companies[i].Id != OrbweaverSettings.PaperclipCompanyId) conn.ChooseCompany(companies[i].Id);
            };
            return combo;
        }

        internal static readonly (string Value, string Label)[] SleepingChoices =
        {
            ("fold", "Fold into the lead"), ("show", "Always show"), ("hide", "Hide"),
        };

        internal Control SleepingPicker()
        {
            var current = OrbweaverSettings.PaperclipSleeping;
            var combo = new ComboBox
            {
                ItemsSource = SleepingChoices.Select(c => c.Label).ToList(),
                SelectedIndex = Array.FindIndex(SleepingChoices, c => c.Value == current),
                MinWidth = 168
            };
            combo.SelectionChanged += (_, _) =>
            {
                var i = combo.SelectedIndex;
                if (i >= 0) OrbweaverSettings.PaperclipSleeping = SleepingChoices[i].Value;
            };
            return combo;
        }

        // Awaitable so a test can drive the whole path and read the result; the
        // click handlers drop the task.
        internal async Task OnPaperclipConnectAsync(string address)
        {
            var conn = PaperclipConnector.Current;
            var running = conn.ConnectAsync(address, CancellationToken.None);
            Rebuild();                       // shows "Approve in your browser" + Cancel at once
            await running;
            Rebuild();
        }

        internal async Task OnPaperclipDisconnectAsync()
        {
            await PaperclipConnector.Current.DisconnectAsync(CancellationToken.None);
            Rebuild();
        }

        // Discovery runs when the window opens: a folder read and one loopback
        // GET, never on a timer (plan 2.2).
        internal async Task DiscoverPaperclipAsync()
        {
            var conn = PaperclipConnector.Current;
            await conn.DiscoverAsync(CancellationToken.None);
            if (OrbweaverSettings.PaperclipEnabled) await conn.LoadCompaniesAsync(CancellationToken.None);
            Rebuild();
        }
    }
}
