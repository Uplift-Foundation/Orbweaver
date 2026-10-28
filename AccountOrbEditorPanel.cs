using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Orbweaver
{
    // CB-287: the one editor for a usage orb's look — initials, display name and
    // icon — shared by the "Customize…" window and the per-account rows in
    // Settings, so the two surfaces cannot drift apart. Built in code, like the
    // rest of SettingsWindow, because it has no static tree worth describing.
    //
    // Every write goes through AccountOrbCustomization, which owns settings.json,
    // the icon files and the Changed event; this control holds no state of its own
    // beyond "what did I last commit", which keeps Enter-then-LostFocus (the usual
    // way a text box is left) from writing the same value twice.
    //
    // Text boxes commit on Enter or on losing focus, never per keystroke: each
    // commit rewrites settings.json and redraws the orb, and a half-typed
    // "A" would otherwise flash onto the orb on the way to "AC".
    internal sealed class AccountOrbEditorPanel : StackPanel
    {
        // The test's answer to the OS file picker, the same seam shape as
        // SettingsWindow.ChooseSoundFileForTests. Null means "use the real picker".
        internal static Func<Task<string?>>? ChooseIconFileForTests;

        // The size an orb's disc is drawn at (AccountOrbWindow.axaml), so the
        // preview shows what the orb will show rather than a bigger flattering one.
        internal const double PreviewSize = 28;

        // A name goes into settings.json, a tooltip and the usage card; nothing
        // there is meant for a paragraph.
        internal const int MaxNameLength = 64;

        private readonly AccountOrbIdentity _account;

        internal TextBox InitialsBox { get; }
        internal TextBox NameBox { get; }
        internal TextBlock PreviewGlyph { get; }
        internal Image PreviewImage { get; }
        internal Button ChooseIconButton { get; }
        internal Button RemoveIconButton { get; }
        internal Button ResetButton { get; }
        internal TextBlock ErrorText { get; }

        private string _committedInitials = "";
        private string _committedName = "";
        private bool _refreshing;

        internal AccountOrbEditorPanel(AccountOrbIdentity account)
        {
            _account = account;
            Spacing = 8;

            InitialsBox = new TextBox { Width = 72, MaxLength = 16, PlaceholderText = "Initials" };
            NameBox = new TextBox { MinWidth = 160, MaxLength = MaxNameLength, PlaceholderText = account.DerivedLabel };

            PreviewGlyph = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#F2FFFFFF")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            PreviewImage = new Image
            {
                Width = PreviewSize,
                Height = PreviewSize,
                Stretch = Stretch.UniformToFill,
                IsVisible = false,
                Clip = new EllipseGeometry(new Rect(0, 0, PreviewSize, PreviewSize))
            };
            var disc = new Grid { Width = PreviewSize, Height = PreviewSize };
            disc.Children.Add(new Ellipse
            {
                Fill = new SolidColorBrush(Color.Parse("#F51C1C1E")),
                Stroke = new SolidColorBrush(Color.Parse("#B8FFFFFF")),
                StrokeThickness = 1.5
            });
            disc.Children.Add(PreviewImage);
            disc.Children.Add(PreviewGlyph);

            ChooseIconButton = new Button { Content = "Choose icon…" };
            RemoveIconButton = new Button { Content = "Remove icon" };
            ResetButton = new Button { Content = "Reset" };
            ErrorText = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#D64545")),
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false
            };

            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            top.Children.Add(disc);
            top.Children.Add(Labelled("Initials", InitialsBox));
            top.Children.Add(Labelled("Name", NameBox));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(ChooseIconButton);
            buttons.Children.Add(RemoveIconButton);
            buttons.Children.Add(ResetButton);

            Children.Add(top);
            Children.Add(buttons);
            Children.Add(ErrorText);
            Children.Add(new TextBlock
            {
                Text = "Square images work best — the icon is cropped to fill the circle. PNG, JPG or SVG.",
                FontSize = 11,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });

            InitialsBox.KeyDown += (_, e) => OnKeyDown(e, InitialsBox);
            InitialsBox.LostFocus += (_, _) => CommitInitials();
            NameBox.KeyDown += (_, e) => OnKeyDown(e, NameBox);
            NameBox.LostFocus += (_, _) => CommitName();
            ChooseIconButton.Click += async (_, _) => await ChooseIconAsync();
            RemoveIconButton.Click += (_, _) => RemoveIcon();
            ResetButton.Click += (_, _) => Reset();

            Refresh();

            // A static event, so only listen while on screen: a panel in a closed
            // window would otherwise be kept alive, and refreshed, forever. This is
            // also what keeps the editor window and the Settings row showing the
            // same account in step with each other.
            AttachedToVisualTree += (_, _) => AccountOrbCustomization.Changed += OnChanged;
            DetachedFromVisualTree += (_, _) => AccountOrbCustomization.Changed -= OnChanged;
        }

        private static StackPanel Labelled(string label, Control control)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.65 });
            panel.Children.Add(control);
            return panel;
        }

        private void OnChanged(string positionKey)
        {
            if (string.Equals(positionKey, _account.PositionKey, StringComparison.OrdinalIgnoreCase))
                Refresh();
        }

        // Called when Escape is pressed in a box that holds no draft. The Customize
        // window sets it to close itself; in a Settings row it stays null, so
        // Escape there only ever reverts and never closes Settings.
        internal Action? EscapeWithNothingToRevert { get; set; }

        private void OnKeyDown(KeyEventArgs e, TextBox box)
        {
            if (e.Key == Key.Enter)
            {
                if (ReferenceEquals(box, InitialsBox)) CommitInitials(); else CommitName();
            }
            else if (e.Key == Key.Escape)
            {
                // Always handled: an unhandled Escape would reach the Settings
                // window's own Escape-closes shortcut.
                e.Handled = true;
                var committed = ReferenceEquals(box, InitialsBox) ? _committedInitials : _committedName;
                if ((box.Text ?? "") != committed)
                    box.Text = committed;
                else
                    EscapeWithNothingToRevert?.Invoke();
            }
        }

        internal void CommitInitials()
        {
            if (_refreshing || (InitialsBox.Text ?? "") == _committedInitials) return;
            _committedInitials = InitialsBox.Text ?? "";
            AccountOrbCustomization.SetInitials(_account.PositionKey, InitialsBox.Text);
            Refresh();
        }

        internal void CommitName()
        {
            if (_refreshing || (NameBox.Text ?? "") == _committedName) return;
            _committedName = NameBox.Text ?? "";
            AccountOrbCustomization.SetName(_account.PositionKey, NameBox.Text);
            Refresh();
        }

        // Async because the real picker is; tests await it directly instead of
        // clicking, so a failure surfaces in the test rather than on a dead task.
        internal async Task ChooseIconAsync()
        {
            var seam = ChooseIconFileForTests;
            var path = seam is not null ? await seam() : await PickIconFileAsync(this);
            if (path is null) return;

            var result = AccountOrbCustomization.SetIcon(_account.PositionKey, path);
            ErrorText.Text = result.Error is { } error ? MessageFor(error) : null;
            ErrorText.IsVisible = result.Error is not null;
            Refresh(keepError: true);
        }

        internal void RemoveIcon()
        {
            AccountOrbCustomization.RemoveIcon(_account.PositionKey);
            Refresh();
        }

        internal void Reset()
        {
            // Reset is the user saying "start over", so drafts go too.
            _committedInitials = InitialsBox.Text ?? "";
            _committedName = NameBox.Text ?? "";
            AccountOrbCustomization.Reset(_account.PositionKey);
            Refresh();
        }

        internal static string MessageFor(IconImportError error) => error switch
        {
            IconImportError.Unreadable => "That file couldn't be read.",
            IconImportError.Empty => "That file is empty.",
            IconImportError.TooLarge => "That image is too large — pick one under 16 MB.",
            IconImportError.NotAnImage => "That file isn't an image.",
            IconImportError.UnsupportedFormat => "Use a PNG, JPG or SVG file.",
            IconImportError.SvgReferencesExternalContent =>
                "This SVG links to files outside itself — export it with embedded images.",
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, "No message for this import error")
        };

        // Re-reads everything from the stored override rather than trusting what
        // the controls say, so the boxes land on what was actually kept (clipped
        // initials, trimmed name) and the preview is the orb's own resolution.
        internal void Refresh(bool keepError = false)
        {
            _refreshing = true;
            try
            {
                var over = AccountOrbCustomization.Current(_account.PositionKey);

                // A box holding text that was never committed is a half-typed
                // draft, and another surface committing a different field (the
                // Customize window and a Settings row can be open together)
                // must not wipe it. Boxes whose text matches what was last
                // committed are clean and take the stored value.
                if ((InitialsBox.Text ?? "") == _committedInitials)
                {
                    _committedInitials = over?.Initials ?? "";
                    InitialsBox.Text = _committedInitials;
                }

                if ((NameBox.Text ?? "") == _committedName)
                {
                    _committedName = over?.Name ?? "";
                    NameBox.Text = _committedName;
                }

                var look = AccountOrbCustomization.Preview(_account);
                PreviewGlyph.Text = look.Glyph;

                var bitmap = LoadPreview(look.IconFile);
                PreviewImage.Source = bitmap;
                PreviewImage.IsVisible = bitmap is not null;
                PreviewGlyph.IsVisible = bitmap is null;

                RemoveIconButton.IsEnabled = over?.IconFile is not null;
                ResetButton.IsEnabled = over is not null;

                if (!keepError)
                {
                    ErrorText.Text = null;
                    ErrorText.IsVisible = false;
                }
            }
            finally
            {
                _refreshing = false;
            }
        }

        // A file that won't load shows letters, never a blank disc — the same
        // fallback the orb itself makes.
        private static Bitmap? LoadPreview(string? iconFile)
        {
            if (iconFile is null) return null;
            var load = AccountOrbIcons.Load(AccountOrbIcons.IconsDirectory, iconFile);
            if (load.Outcome != IconLoadOutcome.Loaded || load.Png is null) return null;
            using var stream = new MemoryStream(load.Png);
            return new Bitmap(stream);
        }

        // Excluded from coverage: opens the OS's own file picker and waits for a
        // human to answer it — there is no headless storage provider. The same
        // justification as SettingsWindow.PickSoundFileAsync; ChooseIconAsync's
        // handling of either answer is covered through ChooseIconFileForTests.
        // The owner is whatever window this panel sits in (the editor or the
        // settings window), never the orb, which is a borderless, unactivated,
        // topmost window a native dialog should not be parented to.
        [ExcludeFromCodeCoverage]
        private static async Task<string?> PickIconFileAsync(Control owner)
        {
            var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider;
            if (storageProvider is null) return null;

            var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose an icon",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.svg" } }
                }
            });

            return result.Count == 0 ? null : result[0].TryGetLocalPath();
        }
    }
}
