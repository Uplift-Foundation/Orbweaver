using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Orbweaver
{
    // CB-308: the window that offers an update, runs it, and says what happened.
    //
    // Non-modal on purpose. This is a menu-bar app with no main window, so a modal
    // would have nothing to be modal over, and an update offer arrives on a timer
    // while someone is mid-conversation: it must be easy to wave away (Later, or
    // Esc) and must never steal a keystroke by being the thing that takes focus
    // for good.
    //
    // The OpenClawRenameWindow pattern, for the same reasons: one at a time,
    // Toggle() to open or bring forward, the presentation halves behind test seams
    // because a headless suite must not Show() a window, and an internal
    // constructor so a test builds one without presenting it. The installer is a
    // seam as well (IUpdateInstaller), so a suite drives every state with a fake
    // and never downloads or swaps anything.
    //
    // Three states, one window:
    //   Offer   — version, release notes, Install & restart / Later / Skip.
    //   Working — a progress bar and a status line; Install and Skip are off.
    //             Later stays on and cancels, so a download can always be walked
    //             away from.
    //   Failed  — the installer's own sentence inline, with Retry and
    //             "Download manually". Nothing has been changed on disk when this
    //             shows (Refused) or the installer has already put things back
    //             (Failed), which is the installer's message to say, not ours.
    internal sealed class UpdateNoticeWindow : Window
    {
        internal enum Phase { Offer, Working, Failed }

        private static UpdateNoticeWindow? _open;

        private readonly IUpdateInstaller? _installer;
        private readonly Action<UpdateRelease> _skip;
        private readonly Action<string> _openUrl;
        private CancellationTokenSource? _cts;

        internal UpdateRelease Release { get; private set; }
        internal Phase State { get; private set; } = Phase.Offer;

        internal TextBlock HeadlineText { get; } = new();
        // Right margin reserves the scrollbar's width: a classic (Windows) scrollbar sits
        // over the content's right edge, and without it the notes run under it.
        internal StackPanel NotesPanel { get; } = new() { Spacing = 4, Margin = new Thickness(0, 0, 14, 0) };
        internal Button InstallButton { get; } = new() { Content = "Install & restart", IsDefault = true };
        internal Button LaterButton { get; } = new() { Content = "Later", IsCancel = true };
        internal Button SkipButton { get; } = new() { Content = "Skip this version" };
        internal Button RetryButton { get; } = new() { Content = "Retry" };
        internal Button ManualButton { get; } = new() { Content = "Download manually" };
        internal ProgressBar Bar { get; } = new() { Minimum = 0, Maximum = 1 };
        internal TextBlock StatusText { get; } = new();

        // Opens the notice on a release, or brings the open one forward pointed at
        // that release. The skip and URL actions default to the real ones, which
        // the seam-built constructor lets a test replace.
        public static void Toggle(UpdateRelease release, IUpdateInstaller? installer)
        {
            if (_open is not null)
            {
                // A different release than the one on screen while an install is
                // running would swap the notes under a download in flight.
                if (_open.State != Phase.Working) _open.Retarget(release);
                (BringForwardForTests ?? Activate)(_open);
                return;
            }

            _open = new UpdateNoticeWindow(release, installer, SkipVersion, OpenUrl);
            _open.Closed += OnClosed;
            (PresentForTests ?? Present)(_open);
        }

        internal static Action<string, string>? ClearFailureMarkerForTests;
        internal static Action<UpdateNoticeWindow>? PresentForTests;
        internal static Action<UpdateNoticeWindow>? BringForwardForTests;

        internal static UpdateNoticeWindow? OpenForTests
        {
            get => _open;
            set => _open = value;
        }

        // Where "Download manually" goes: the release's own page, which carries
        // the same installers the updater would have fetched, so a person whose
        // update failed is one click from doing it by hand.
        internal static string ManualUrl(UpdateRelease release) =>
            $"https://github.com/Uplift-Foundation/Orbweaver/releases/tag/{Uri.EscapeDataString(release.Tag)}";

        [ExcludeFromCodeCoverage]
        private static void SkipVersion(UpdateRelease release) => UpdateServices.Skip(release);

        [ExcludeFromCodeCoverage]
        private static void OpenUrl(string url)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                if (OperatingSystem.IsMacOS())
                {
                    psi.FileName = "open";
                    psi.ArgumentList.Add(url);
                    psi.UseShellExecute = false;
                }
                else
                {
                    psi.FileName = url;
                    psi.UseShellExecute = true;
                }

                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch
            {
                // A browser that will not open is not worth a second failure
                // on top of the one that sent the person here.
            }
        }

        [ExcludeFromCodeCoverage]
        private static void Present(UpdateNoticeWindow window)
        {
            MacOSActivation.SetRegular();
            window.Show();
            window.Activate();
        }

        [ExcludeFromCodeCoverage]
        private static void Activate(UpdateNoticeWindow window)
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

        internal UpdateNoticeWindow(
            UpdateRelease release, IUpdateInstaller? installer, Action<UpdateRelease> skip, Action<string> openUrl)
        {
            Release = release;
            _installer = installer;
            _skip = skip;
            _openUrl = openUrl;

            Title = $"Update {Brand.DisplayName}";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            KeyDown += OnWindowKeyDown;
            Closed += (_, _) => Cancel();

            HeadlineText.FontWeight = FontWeight.SemiBold;
            HeadlineText.FontSize = 15;
            HeadlineText.TextWrapping = TextWrapping.Wrap;

            StatusText.FontSize = 12;
            StatusText.TextWrapping = TextWrapping.Wrap;

            Bar.Height = 6;

            InstallButton.Click += async (_, _) => await InstallAsync();
            RetryButton.Click += async (_, _) => await InstallAsync();
            LaterButton.Click += (_, _) => Later();
            SkipButton.Click += (_, _) => Skip();
            ManualButton.Click += (_, _) => _openUrl(ManualUrl(Release));

            var notes = new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = NotesPanel
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { SkipButton, ManualButton, RetryButton, LaterButton, InstallButton }
            };

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children = { HeadlineText, notes, Bar, StatusText, buttons }
            };

            Render();
        }

        private void Retarget(UpdateRelease release)
        {
            Release = release;
            State = Phase.Offer;
            Render();
        }

        // The one place the window's appearance is decided, so a state is a value
        // rather than a scatter of IsVisible assignments that have to agree.
        private void Render()
        {
            HeadlineText.Text = Headline(Release);

            NotesPanel.Children.Clear();
            foreach (var control in UpdateNotesView.Build(Release.Body)) NotesPanel.Children.Add(control);

            var working = State == Phase.Working;
            var failed = State == Phase.Failed;

            // No installer on this platform or install kind: Install is replaced by
            // Download manually, which is the release page, so the offer is still
            // actionable rather than a button that can only refuse.
            InstallButton.IsVisible = State == Phase.Offer && _installer is not null;
            SkipButton.IsVisible = State == Phase.Offer;
            RetryButton.IsVisible = failed;
            ManualButton.IsVisible = failed || (State == Phase.Offer && _installer is null);
            LaterButton.IsVisible = true;
            Bar.IsVisible = working;
            StatusText.IsVisible = working || failed;

            // Cleared rather than set to null: a local null is "no brush", which
            // draws nothing, where clearing lets the theme's text colour back in.
            if (failed) StatusText.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
            else StatusText.ClearValue(TextBlock.ForegroundProperty);
        }

        internal static string Headline(UpdateRelease release) =>
            $"{Brand.DisplayName} {release.Version} is available";

        internal static string StageText(UpdateProgress progress)
        {
            var stage = progress.Stage switch
            {
                UpdateStage.Downloading => "Downloading",
                UpdateStage.Verifying => "Verifying",
                UpdateStage.Staging => "Preparing",
                UpdateStage.Installing => "Installing",
                _ => "Restarting",
            };

            if (progress.Stage == UpdateStage.Downloading && progress.BytesTotal > 0)
            {
                var percent = (int)Math.Clamp(progress.BytesDone * 100 / progress.BytesTotal, 0, 100);
                stage += $" — {percent}%";
            }

            return progress.Detail is { Length: > 0 } detail ? $"{stage}… {detail}" : stage + "…";
        }

        internal void OnProgress(UpdateProgress progress)
        {
            StatusText.Text = StageText(progress);

            if (progress.Stage == UpdateStage.Downloading && progress.BytesTotal > 0)
            {
                Bar.IsIndeterminate = false;
                Bar.Value = Math.Clamp((double)progress.BytesDone / progress.BytesTotal, 0, 1);
            }
            else
            {
                // Unknown size, or a stage with no byte count: a bar that claims
                // a fraction it does not have is worse than one that just moves.
                Bar.IsIndeterminate = true;
            }
        }

        internal async Task InstallAsync()
        {
            if (State == Phase.Working || _installer is null) return;

            State = Phase.Working;
            _cts = new CancellationTokenSource();
            Render();
            OnProgress(new UpdateProgress(UpdateStage.Downloading, 0, 0));

            InstallOutcome outcome;
            try
            {
                outcome = await _installer.InstallAsync(Release, new InlineProgress(OnProgress), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Later while working: the window is already going.
                return;
            }
            catch (Exception ex)
            {
                outcome = new InstallOutcome(InstallStatus.Failed, ex.Message);
            }

            if (outcome.Status == InstallStatus.HandedOff)
            {
                // The installer owns the process from here; this window stays
                // up, working, until the app exits underneath it.
                OnProgress(new UpdateProgress(UpdateStage.Restarting, 0, 0));
                return;
            }

            State = Phase.Failed;
            Render();
            StatusText.Text = outcome.Message;

            // Shown here, so the next launch must not announce it again.
            (ClearFailureMarkerForTests ?? ((r, v) => UpdateFailureNotice.Clear(r, v)))(UpdateFailureNotice.UpdatesRoot(), Release.Version);
        }

        internal void Later()
        {
            Cancel();
            Close();
        }

        internal void Skip()
        {
            _skip(Release);
            Close();
        }

        private void Cancel()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        internal static bool ShouldClose(Key key) => key == Key.Escape;

        internal void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (ShouldClose(e.Key)) Later();
        }

        // Progress<T> posts to whatever SynchronizationContext it was made on,
        // which under a headless test is none and so lands on a pool thread.
        // This runs inline on the UI thread and posts from anywhere else, which
        // is the behaviour the window needs and the one a test can observe.
        private sealed class InlineProgress : IProgress<UpdateProgress>
        {
            private readonly Action<UpdateProgress> _handler;
            public InlineProgress(Action<UpdateProgress> handler) => _handler = handler;

            public void Report(UpdateProgress value)
            {
                if (Dispatcher.UIThread.CheckAccess()) _handler(value);
                else Dispatcher.UIThread.Post(() => _handler(value));
            }
        }
    }

    // Release notes as controls. GitHub release bodies are Markdown — headings,
    // bold, bullets, the occasional fenced block — which ChatMarkdown already
    // reads, so this only turns its blocks into something to put in a window.
    // Not shared with ChatPanel's own renderer: that one is a nested class bound
    // to chat bubble state (selection, per-agent ink), and what a notice needs is
    // far smaller.
    internal static class UpdateNotesView
    {
        internal static IReadOnlyList<Control> Build(string? body)
        {
            var controls = new List<Control>();

            foreach (var block in ChatMarkdown.Parse(StripTables(body ?? "")))
                controls.Add(Block(block));

            if (controls.Count == 0)
                controls.Add(Line("No release notes were published for this version.", italic: true));

            return controls;
        }

        // Every release body carries a "Which file do I want" section: a downloads
        // table plus a sentence about the optional speech engine. ChatMarkdown
        // (rightly, for chat) turns a table into a monospace block of raw pipes,
        // and in an updater none of it means anything — the updater has already
        // chosen the file. So a section that contains a table is dropped whole,
        // heading and prose with it; a table before any heading just loses its
        // table lines.
        internal static string StripTables(string body)
        {
            var sections = new List<List<string>> { new() };
            foreach (var line in body.Replace("\r", "").Split('\n'))
            {
                if (line.TrimStart().StartsWith('#')) sections.Add(new List<string>());
                sections[^1].Add(line);
            }

            var kept = new List<string>();
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var hasTable = section.Any(l => l.TrimStart().StartsWith('|'));
                if (hasTable && i > 0) continue;
                kept.AddRange(hasTable ? section.Where(l => !l.TrimStart().StartsWith('|')) : section);
            }

            return string.Join("\n", kept).TrimEnd();
        }

        private static Control Block(ChatMarkdown.MdBlock block)
        {
            switch (block.Kind)
            {
                case ChatMarkdown.MdKind.Code:
                    return new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 4),
                        Child = new TextBlock
                        {
                            Text = block.Text,
                            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap
                        }
                    };

                case ChatMarkdown.MdKind.Heading:
                {
                    var heading = Line(block.Text);
                    heading.FontWeight = FontWeight.SemiBold;
                    heading.Margin = new Thickness(0, 4, 0, 0);
                    return heading;
                }

                case ChatMarkdown.MdKind.Quote:
                    return new Border
                    {
                        BorderBrush = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)),
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(6, 0, 0, 0),
                        Child = Line(block.Text)
                    };

                case ChatMarkdown.MdKind.Bullet:
                case ChatMarkdown.MdKind.Ordered:
                {
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Thickness(block.Depth * 10, 0, 0, 0)
                    };

                    // Top-aligned: left to the default, a macOS bullet glyph is
                    // centred against the whole wrapped paragraph instead of
                    // sitting beside its first line.
                    var marker = Line(block.Marker);
                    marker.VerticalAlignment = VerticalAlignment.Top;
                    marker.Margin = new Thickness(0, 0, 5, 0);
                    marker.Opacity = 0.7;

                    var text = Line(block.Text);
                    text.VerticalAlignment = VerticalAlignment.Top;
                    Grid.SetColumn(text, 1);

                    row.Children.Add(marker);
                    row.Children.Add(text);
                    return row;
                }

                default:
                    return Line(block.Text);
            }
        }

        private static TextBlock Line(string text, bool italic = false)
        {
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            if (italic) block.FontStyle = FontStyle.Italic;

            block.Inlines = new Avalonia.Controls.Documents.InlineCollection();
            foreach (var span in ChatMarkdown.Inline(text))
            {
                var run = new Avalonia.Controls.Documents.Run(span.Text);

                switch (span.Style)
                {
                    case ChatMarkdown.MdStyle.Bold:
                        run.FontWeight = FontWeight.SemiBold;
                        break;
                    case ChatMarkdown.MdStyle.Italic:
                        run.FontStyle = FontStyle.Italic;
                        break;
                    case ChatMarkdown.MdStyle.BoldItalic:
                        run.FontWeight = FontWeight.SemiBold;
                        run.FontStyle = FontStyle.Italic;
                        break;
                    case ChatMarkdown.MdStyle.Code:
                        run.FontFamily = new FontFamily("Menlo, Consolas, monospace");
                        break;
                    case ChatMarkdown.MdStyle.Link:
                        run.TextDecorations = TextDecorations.Underline;
                        break;
                }

                block.Inlines!.Add(run);
            }

            return block;
        }
    }
}
