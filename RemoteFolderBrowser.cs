using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Orbweaver
{
    // CB-357 (reopened): the folder picker for a remote machine in New Chat.
    // It walks the far desktop's own browse listing (list-dir over the peer
    // link): starts at its top level, goes into a folder and back up, shows
    // folders only, and hands back the path of the one chosen. What may be seen
    // is decided on the far machine by its CB-339 rules; this window only asks.
    //
    // A fixed size and not resizable, like the New Chat window it opens from
    // (CB-207): the list scrolls instead of the window growing.
    internal sealed class RemoteFolderBrowser : Window
    {
        internal const string UpLabel = "↑ Up";
        internal const string ChooseLabel = "Choose this folder";

        private readonly IPeerLauncher _peers;
        private readonly string _machine;
        private readonly TextBlock _where = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        private readonly ListBox _list = new() { Height = 240 };
        private readonly TextBlock _status = new()
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 36,
        };
        private readonly Button _choose = new() { Content = ChooseLabel, IsEnabled = false };
        private readonly Button _cancel = new() { Content = "Cancel" };

        private PeerDirListing? _listing;
        private int _generation;
        private bool _hasUp;

        internal RemoteFolderBrowser(IPeerLauncher peers, string machine)
        {
            _peers = peers;
            _machine = machine;

            Title = "Choose a folder on " + machine;
            Width = 420;
            Height = 420;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _list.DoubleTapped += async (_, _) => await OpenSelectedAsync();
            _list.KeyDown += async (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) await OpenSelectedAsync();
            };
            _choose.Click += (_, _) => Close(Chosen);
            _cancel.Click += (_, _) => Close(null);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttons.Children.Add(_cancel);
            buttons.Children.Add(_choose);

            var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
            root.Children.Add(_where);
            root.Children.Add(_list);
            root.Children.Add(_status);
            root.Children.Add(buttons);
            Content = root;

        }

        // Starts at the far machine's top level. An override rather than a
        // handler so a headless test can raise it without showing the window,
        // which hangs that suite.
        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            await NavigateAsync("");
        }

        // The folder "Choose this folder" returns: the listing's own path. Null at
        // the top level, which is a list of roots and not itself a folder.
        internal string? Chosen => _listing is { Path.Length: > 0 } l ? l.Path : null;

        internal ListBox List => _list;
        internal Button ChooseButton => _choose;
        internal Button CancelButton => _cancel;
        internal TextBlock Where => _where;
        internal string? StatusText => _status.Text;
        internal PeerDirListing? Listing => _listing;

        // The rows as shown: an Up row first when there is somewhere to go up to,
        // then one per folder.
        internal IReadOnlyList<string> Rows => _list.Items.OfType<string>().ToList();

        // The path of a child folder. Top-level items carry their own full path
        // (a root); below it a child is the listing's path plus its name, joined
        // with whichever separator the far path already uses.
        internal static string ChildPath(PeerDirListing listing, PeerDirItem item)
        {
            if (item.Path is { Length: > 0 } full) return full;

            var sep = listing.Path.Contains('\\') ? '\\' : '/';
            return listing.Path.TrimEnd('\\', '/') + sep + item.Name;
        }

        internal Task OpenSelectedAsync() => _list.SelectedIndex is { } i and >= 0 ? OpenAsync(i) : Task.CompletedTask;

        internal async Task OpenAsync(int row)
        {
            if (_listing is not { } l) return;

            if (_hasUp && row == 0)
            {
                await NavigateAsync(l.Parent);
                return;
            }

            var index = row - (_hasUp ? 1 : 0);
            if (index >= 0 && index < l.Items.Count) await NavigateAsync(ChildPath(l, l.Items[index]));
        }

        internal async Task NavigateAsync(string path)
        {
            var generation = ++_generation;
            _status.Text = PeerLaunchCopy.Asking(_machine);

            PeerDirResult result;
            try { result = await _peers.ListDirAsync(_machine, path, CancellationToken.None); }
            catch (Exception) { result = PeerDirResult.Fail(PeerLaunchCodes.NoAnswer); }

            if (generation != _generation) return;

            if (result is not { Ok: true, Listing: { } listing })
            {
                // Stay where we were: the list and the choice are untouched, and
                // the refusal says why in the machine's name.
                _status.Text = PeerLaunchCopy.ExplainBrowse(result.Code, _machine);
                return;
            }

            _listing = listing;
            _hasUp = listing.Path.Length > 0;

            var rows = new List<string>();
            if (_hasUp) rows.Add(UpLabel);
            rows.AddRange(listing.Items.Select(i => i.Drive is { Length: > 0 } d && i.Home is false ? i.Name + " (" + d + ")" : i.Name));

            _list.ItemsSource = rows;
            _where.Text = listing.Path.Length > 0 ? listing.Path : _machine;
            _choose.IsEnabled = Chosen is not null;
            _status.Text = listing.Truncated ? "Only the first folders are shown." : listing.Items.Count == 0 ? "No folders here." : null;
        }
    }
}
