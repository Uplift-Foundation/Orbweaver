using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Orbweaver
{
    // The app's first real window. Everything else is a 56x56 orb or a native
    // menu, and a native menu can't take text input — which is the only reason
    // this exists: naming a profile needs a text field.
    //
    // Built in code rather than XAML because the contents are one row per
    // discovered profile, so there is no static tree to describe.
    //
    // Changes apply immediately. There is no OK/Cancel: this is a preferences
    // window for a menu-bar app, and a settings file that only commits on a
    // button is one more state to get wrong.
    internal sealed class SettingsWindow : Window
    {
        private static SettingsWindow? _open;

        // Excluded from coverage: this is the one entry point the app uses, and
        // it does more than construct a window — MacOSActivation.SetRegular(),
        // Show(), Activate(), StartStatusTicker(). tests/UiTests'
        // SettingsWindowSmokeTest says the same thing from the other side, and
        // reaches the private constructor directly for exactly this reason. The
        // rows the window builds are covered; putting the app in the Dock and
        // taking a window key is not something a headless suite has any business
        // doing.
        [ExcludeFromCodeCoverage]
        public static void Toggle()
        {
            if (_open is not null)
            {
                _open.Activate();
                return;
            }

            _open = new SettingsWindow();
            _open.Closed += (_, _) =>
            {
                // A timer on a closed window would keep the window alive and go
                // on ticking for nothing.
                _open?._openClawStatusTimer?.Stop();
                _open = null;

                // The colour pickers defer their write; closing the window is the
                // last chance to land one that's still pending.
                OrbweaverSettings.FlushPendingSave();

                // Back to a menu-bar-only app: no Dock icon, no Cmd-Tab entry.
                MacOSActivation.SetAccessory();
            };

            // Becoming a regular app for as long as this window is open, so it
            // comes to the front and gets a Dock icon and a Cmd-Tab entry while
            // it is up.
            //
            // The reason recorded here used to be "an accessory app's window
            // can't take keyboard focus". That is not true, and the chat panel
            // demonstrates it: it is a borderless window on the same accessory
            // app, it takes typed input, and it does no policy switching at all.
            // What is different is how each one is opened — this window comes
            // from a status-item click, which does not activate the app, and the
            // panel comes from a click on one of the app's own windows, which
            // does. So the Activate() below is the part that matters here, and
            // SetRegular is what makes it stick for a window with no orb behind
            // it to have been clicked.
            MacOSActivation.SetRegular();
            _open.Show();
            _open.Activate();
            _open.StartStatusTicker();
        }

        private SettingsWindow()
        {
            Title = $"{Brand.DisplayName} Settings";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            MinHeight = 240;
            MaxHeight = 760;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Escape and Cmd-W close it, the way any Mac window does. That's
            // also what lets the Done button go away on macOS, where a
            // preferences window with one would look wrong.
            KeyDown += OnWindowKeyDown;

            // Liquid Glass: the window is a translucent material, not a filled
            // rectangle. AcrylicBlur is what Avalonia maps to NSVisualEffectView
            // on macOS — confirmed granted here, ActualTransparencyLevel reports
            // it back. The fallbacks matter: Windows takes Mica, and anything that
            // can end up with None still reads, because the text all sits on cards
            // that carry their own translucent fill rather than on bare glass.
            TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None
            };

            BorrowFluentToggleSwitch();
            EnsureColorPickerTheme(this);

            Rebuild();

            // Every card colour below is mixed for the current variant, so a
            // system-wide switch to dark while the window is open would otherwise
            // leave white cards on a dark window. Rebuilding is safe because
            // nothing here holds uncommitted state — each control writes its
            // setting as it changes.
            ActualThemeVariantChanged += (_, _) => Rebuild();

            // A voice preview belongs to this window: closing it stops the preview
            // (and only a preview — VoicePreview leaves a read-aloud it did not
            // start alone). Subscribed here rather than in Toggle()'s Closed
            // handler, which is excluded from coverage and which tests never
            // reach, since they construct the window directly.
            Closed += (_, _) => VoicePreview.StopIfLive();
        }

        // Split out from the KeyDown handler above so the decision is testable
        // without ever calling Close() — see CloseFromKeyboardShortcut below for
        // why that matters. Kept as its own method, with its original
        // signature, rather than folded into VerdictFor's callers: the existing
        // ShouldCloseOnKeyDownMatchesEscapeAndCmdW theory asserts this exact
        // five-row table and must keep passing unmodified. It's now a
        // projection of VerdictFor with the query fixed at null — an inactive
        // filter is the one state where "does this key close the window" has
        // never depended on anything else, so the projection reproduces the
        // original table exactly.
        internal static bool ShouldCloseOnKeyDown(Key key, KeyModifiers modifiers) =>
            VerdictFor(key, modifiers, null) == SettingsKeyVerdict.Close;

        // The wider decision ShouldCloseOnKeyDown above is a slice of: what a
        // key press means once there's a filter box that can also be focused
        // or cleared. Query is whatever the filter box currently holds, or
        // null once there's no box to read it from (a window not yet built —
        // never true after the constructor — or ShouldCloseOnKeyDown's own
        // projection).
        internal static SettingsKeyVerdict VerdictFor(Key key, KeyModifiers modifiers, string? query)
        {
            // Cmd-W always closes, whatever the filter holds — no gesture that
            // already closed this window before CB-166 is allowed to stop
            // closing it now.
            if (key == Key.W && modifiers.HasFlag(KeyModifiers.Meta)) return SettingsKeyVerdict.Close;

            if (key == Key.Escape)
            {
                // Escape with a modifier held is a stray chord, not "clear the
                // filter" — it still closes, so no existing close gesture is
                // ever taken away by this ticket.
                if (modifiers != KeyModifiers.None) return SettingsKeyVerdict.Close;

                return SettingsFilter.IsActive(query)
                    ? SettingsKeyVerdict.ClearFilter
                    : SettingsKeyVerdict.Close;
            }

            if (key == Key.F && (modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control)))
            {
                return SettingsKeyVerdict.FocusFilter;
            }

            return SettingsKeyVerdict.Ignore;
        }

        // The safe three-quarters of OnWindowKeyDown below, split out so a test
        // can drive Escape-clears-the-filter and Cmd/Ctrl-F-focuses-it without
        // going anywhere near Close(). Returns the verdict it acted on so the
        // caller decides what to do with the one branch this can't take itself.
        internal SettingsKeyVerdict HandleKeyDown(Key key, KeyModifiers modifiers)
        {
            var verdict = VerdictFor(key, modifiers, _filterBox?.Text);

            switch (verdict)
            {
                case SettingsKeyVerdict.ClearFilter:
                    if (_filterBox is not null) _filterBox.Text = "";
                    break;
                case SettingsKeyVerdict.FocusFilter:
                    _filterBox?.Focus();
                    break;
            }

            return verdict;
        }

        // Excluded from coverage: its only job past HandleKeyDown is to call
        // Close() on the one verdict that means it — HandleKeyDown carries
        // every other branch and is fully tested; this is just the one call
        // this suite may never make.
        [ExcludeFromCodeCoverage]
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (HandleKeyDown(e.Key, e.KeyModifiers) == SettingsKeyVerdict.Close)
            {
                CloseFromKeyboardShortcut();
            }
        }

        // Excluded from coverage: Window.Close() corrupts a process-wide Avalonia
        // FontManager cache when called on a headless window — see
        // SettingsWindowSmokeTest.cs's own comment for the full story of how
        // that took down 25 of 28 UiTests in one CI run. ShouldCloseOnKeyDown
        // above is the actual decision, and it is fully tested; this is just the
        // one call this suite may never make.
        [ExcludeFromCodeCoverage]
        private void CloseFromKeyboardShortcut() => Close();

        // Excluded from coverage: the same Window.Close() hazard as
        // CloseFromKeyboardShortcut above, just reached from the Windows-only
        // Done button (see Body()) instead of Escape/Cmd-W.
        [ExcludeFromCodeCoverage]
        private void CloseFromDoneButton() => Close();

        // The macOS theme's ToggleSwitch template is broken against the stock
        // control: Avalonia's ToggleSwitch demands a Panel named
        // PART_MovingKnobs and its template doesn't satisfy that, so the first
        // switch to be measured throws KeyNotFoundException and takes the app
        // down. Confirmed on Avalonia 11.3.7 *and* 12.0.2, with the theme's
        // newest build for each — so it's the template, not a version mismatch,
        // and upgrading Avalonia doesn't help.
        //
        // Rather than give up switches (checkboxes would be the fallback) or
        // hand-write a template for one control, borrow Fluent's ToggleSwitch
        // ControlTheme into this window's resources. Everything else here stays
        // AppKit-styled by the theme. Remove this once upstream fixes it.
        // Excluded from coverage: reaches into the running application's style
        // resources to borrow one ControlTheme out of another theme's dictionary,
        // and its catch exists for a lookup that only fails under a particular
        // macOS theme. Neither the borrow nor the failure is arrangeable from a
        // test — and the consequence of the catch is covered where it lands:
        // Switch() falls back to a CheckBox, which every switch test in this
        // suite already exercises without knowing which of the two it got.
        [ExcludeFromCodeCoverage]
        private void BorrowFluentToggleSwitch()
        {
            try
            {
                var fluent = new Avalonia.Themes.Fluent.FluentTheme();
                if (fluent.TryGetResource(typeof(ToggleSwitch), ActualThemeVariant, out var found)
                    && found is ControlTheme fluentSwitch)
                {
                    Resources.Add(typeof(ToggleSwitch), fluentSwitch);
                }
            }
            catch
            {
                // Worst case the switches keep the theme's own template, which
                // is the crash this exists to avoid — so if this ever stops
                // working, Switch() below falls back to a CheckBox.
            }
        }

        private bool HasSwitchTheme => Resources.ContainsKey(typeof(ToggleSwitch))
                                       || !OperatingSystem.IsMacOS();

        // The same defensive shape as BorrowFluentToggleSwitch, for two different
        // reasons — and unlike that one, half of this is a confirmed hole rather
        // than a precaution.
        //
        //  - On Windows there is no ColorPicker template at all. The control lives
        //    in its own package and Avalonia.Themes.Fluent contains no reference
        //    to it, so the row would render as an empty gap, which is what an
        //    untemplated TemplatedControl looks like.
        //  - On macOS the Devolutions theme does ship /Controls/ColorPicker.axaml
        //    and its PART names cover everything the control looks up. But its
        //    ToggleSwitch template is already known to be broken against the stock
        //    control, so a themed template here is something to check rather than
        //    assume.
        //
        // Both are answered the same way: merge the ColorPicker package's own
        // Fluent styles into *this window* when the live theme has no ControlTheme
        // for the type. Window-scoped, so nothing else is restyled and no other
        // window pays for it — and window styles beat application ones, so this
        // can also be forced unconditionally if the themed picker turns out to be
        // broken rather than missing.
        //
        // Fluent.xaml's root element is <Styles>, not <ResourceDictionary> — it
        // wraps its sub-dictionaries in Styles.Resources — so this is a
        // StyleInclude.
        // Excluded from coverage: loads a XAML style dictionary from an avares://
        // URI belonging to another assembly, and catches the case where that
        // assembly's layout has changed. Worst case the pickers come out
        // unstyled, which is a gap in one card rather than a crash — but neither
        // the load nor its failure can be arranged headlessly.
        //
        // Shared with GroupStyleWindow (CB-268), whose two pickers came out as
        // exactly that empty gap on Windows until it called this too: any window
        // that holds an ArmedColorPicker calls it from its constructor.
        [ExcludeFromCodeCoverage]
        internal static void EnsureColorPickerTheme(Window window)
        {
            try
            {
                var styles = Application.Current?.Styles;
                if (styles is not null
                    && styles.TryGetResource(typeof(ColorPicker), window.ActualThemeVariant, out _))
                {
                    return;
                }

                window.Styles.Add(new StyleInclude(new Uri($"avares://{Brand.AssemblyName}/"))
                {
                    Source = new Uri(
                        "avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml")
                });
            }
            catch
            {
                // Worst case the pickers come out unstyled, which is a gap in one
                // card rather than a crash — everything that was already in this
                // window still works.
            }
        }

        // The filter box and the empty-state line, both outside what Rebuild()
        // replaces below. Neither is rebuilt on a theme change or a toggle
        // flip — only Body()'s content changes, so a query typed mid-edit
        // survives every one of the many things in this window that call
        // Rebuild(). The query itself is deliberately never read from or
        // written to OrbweaverSettings: it's a transient view of the page,
        // not a preference.
        private TextBox? _filterBox;
        private TextBlock? _emptyState;
        private ScrollViewer? _scrollViewer;

        internal void Rebuild()
        {
            // Held against System Settings side by side, Apple's content pane is
            // *not* very transparent — the glass in Tahoe lives in sidebars,
            // popovers and menus, while a settings pane behind grouped rows stays
            // a near-opaque light surface. A near-clear wash here (the first
            // attempt at fixing the opposite mistake) let the wallpaper through
            // and read as murky rather than glassy. This sits at 85%: the material
            // still lifts the window's edges, the content still reads crisp.
            Background = new SolidColorBrush(IsDark
                ? Color.FromArgb(0xD9, 0x1E, 0x1E, 0x20)
                : Color.FromArgb(0xD9, 0xF2, 0xF2, 0xF5));

            if (_scrollViewer is null)
            {
                // Built once, on the first Rebuild() the constructor makes, and
                // never again — everything after this swaps only the
                // ScrollViewer's Content. A search box that got torn down and
                // rebuilt on every toggle flip would lose focus and drop
                // whatever was typed, on every one of the many handlers in
                // this file that call Rebuild().
                _filterBox = new TextBox
                {
                    Watermark = "Search settings",
                    Margin = new Thickness(20, 18, 20, 0)
                };
                // TextChanged, not LostFocus: this commits nothing and reaches
                // nothing outside the window's own tree, unlike the gateway
                // boxes' commit-on-LostFocus — so there's no reason to wait
                // for focus to leave before a keystroke takes effect.
                _filterBox.TextChanged += (_, _) => ApplyFilter();
                DockPanel.SetDock(_filterBox, Dock.Top);

                _emptyState = new TextBlock
                {
                    Opacity = 0.55,
                    FontSize = 13,
                    Margin = new Thickness(20, 4, 20, 0),
                    IsVisible = false
                };
                DockPanel.SetDock(_emptyState, Dock.Top);

                _scrollViewer = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                };

                Content = new DockPanel
                {
                    Children = { _filterBox, _emptyState, _scrollViewer }
                };
            }

            _scrollViewer.Content = Body();

            // Several toggle handlers, and every gateway commit, call Rebuild()
            // outright rather than mutating the tree in place — so whatever was
            // typed before has to be re-applied to the page Body() just handed
            // back, or a folded-open search result would silently re-fold and
            // an active query would silently stop filtering anything.
            ApplyFilter();
        }

        // Applies the current query to every section and updates the
        // empty-state line. Query is read off the box rather than threaded
        // through as a parameter because every caller already has a reference
        // to this window and none of them has a reason to filter to anything
        // but what's actually typed.
        private void ApplyFilter()
        {
            var query = _filterBox?.Text;

            var anyVisible = false;
            foreach (var section in _sections.Values)
            {
                if (section.ApplyFilter(query)) anyVisible = true;
            }

            if (_emptyState is not null)
            {
                _emptyState.Text = SettingsFilter.EmptyStateText(query);
                _emptyState.IsVisible = SettingsFilter.IsActive(query) && !anyVisible;
            }
        }

        // No control metrics here on purpose. Heights, corner radii, fills and
        // borders for fields and pop-ups come from the macOS theme on macOS and
        // from Fluent on Windows; pinning them by hand is what produced capsule
        // pop-ups and 20pt checkboxes in the first place.

        // Every section the page is built from, keyed by the stable id its
        // Group() call passes. Tests — and, once the rest of CB-166 lands, the
        // filter box — address a section by id rather than by walking the tree
        // for its heading: Body()'s own comments below record that these
        // headings have already been renamed and merged once, so a lookup keyed
        // on prose turns the next copy edit into a silent behaviour change.
        //
        // Rebuilt from scratch every time, because Body() is: the handlers call
        // Rebuild() freely and a dictionary that accumulated stale sections
        // would hand the filter controls that are no longer in any window.
        private readonly Dictionary<string, SettingsSection> _sections = new();

        internal IReadOnlyDictionary<string, SettingsSection> Sections => _sections;

        // A heading, the cards under it, and whether it is unfolded.
        //
        // A StackPanel subclass rather than something wrapping one, so the page
        // stays a flat stack of these and everything that already walks this
        // tree — three screenshot scenarios and the row tests — finds the same
        // controls at the same depth it did before.
        //
        // IsOpen is view state and nothing else. Setting it moves the chevron
        // and the body's visibility; it writes nothing. Persistence belongs to
        // the header's click handler, where the gesture is — persist on the
        // gesture, not on the property.
        //
        // That split is the one contract between the two halves of this ticket,
        // and it is not stylistic: the filter force-expands a section holding a
        // match, so if persistence lived in this setter the first search would
        // quietly rewrite all thirteen sections to open and the user's folds
        // would be gone with no gesture that could have caused it. This file
        // already carries that scar — the ColorRow "arming" comment further
        // down is here because a control once wrote three colours nobody chose
        // into settings.json for want of exactly this distinction.
        internal sealed class SettingsSection : StackPanel
        {
            // The stable handle Group() keyed _sections with — see the comment
            // above Body() for why persistence is keyed on this and not on
            // Title. RestoreOpenState() needs it to read its own fold state
            // back out of settings without Group() having to hand it in twice.
            public required string Id { get; init; }

            public required string Title { get; init; }

            // The cards as Group() was handed them, not the wrapper the params
            // overload builds around several of them, so the filter can hide an
            // emptied card without reaching back through the tree to find it.
            public IReadOnlyList<Control> Cards { get; set; } = Array.Empty<Control>();

            private bool _isOpen = true;
            private Avalonia.Controls.Primitives.ToggleButton? _header;
            private RotateTransform? _chevronRotation;
            private Control? _body;

            // View state and nothing else: setting this moves the chevron and
            // the body's visibility, full stop. It does not touch
            // OrbweaverSettings, on purpose — see the type-level comment
            // above. The header's click handler is the only writer, and it
            // writes before it gets here (see Group()'s Click handler).
            public bool IsOpen
            {
                get => _isOpen;
                set
                {
                    _isOpen = value;
                    if (_header is not null) _header.IsChecked = value;
                    if (_chevronRotation is not null) _chevronRotation.Angle = value ? 90 : 0;
                    if (_body is not null) _body.IsVisible = value;
                }
            }

            // Group() calls this once, right after building the header and its
            // chevron, so IsOpen above has real controls to move instead of
            // just a bool. Kept separate from a constructor because
            // SettingsSection has no Avalonia dependency of its own beyond
            // being a StackPanel — the header lives in Group(), not here.
            internal void WireDisclosure(
                Avalonia.Controls.Primitives.ToggleButton header,
                RotateTransform chevronRotation,
                Control body)
            {
                _header = header;
                _chevronRotation = chevronRotation;
                _body = body;
            }

            // Re-applies whatever this section's id was last persisted as,
            // after Body() has rebuilt the page from nothing. Reads rather
            // than trusts whatever IsOpen already holds, because a Rebuild()
            // can be triggered by something that has nothing to do with
            // folding at all — a gateway TextBox's LostFocus commit, most of
            // all — and the page that comes back has to show the fold the
            // user actually left it in, not whatever this instance happened
            // to default to.
            public void RestoreOpenState()
            {
                IsOpen = !OrbweaverSettings.IsSettingsSectionCollapsed(Id);
            }

            // Applies query to every card, then decides whether the section
            // itself should show. Cards is walked rather than Children because
            // Children also holds the header and the chevron, which have
            // nothing to filter.
            //
            // A section holding a match force-expands so the match is actually
            // visible, and does it through IsOpen rather than
            // OrbweaverSettings — IsOpen's own setter writes nothing (see the
            // type-level comment above), so a search never persists a fold it
            // only changed to show a result. Clearing the filter restores
            // whatever was on disk before the search touched anything.
            //
            // A card that isn't a SettingsCard (there shouldn't be one — Card()
            // is the only thing this window hands to Group()) contributes
            // nothing either way, which is the safe default: better to leave an
            // unrecognised card showing than to guess at whether it matched.
            internal bool ApplyFilter(string? query)
            {
                var titleMatches = SettingsFilter.Matches(query, Title);

                var anyCardMatch = false;
                foreach (var card in Cards)
                {
                    if (card is SettingsCard settingsCard)
                    {
                        anyCardMatch |= settingsCard.ApplyFilter(query, titleMatches);
                    }
                }

                var visible = SettingsFilter.SectionVisible(anyCardMatch, titleMatches);
                IsVisible = visible;

                if (SettingsFilter.IsActive(query))
                {
                    if (visible) IsOpen = true;
                }
                else
                {
                    RestoreOpenState();
                }

                return visible;
            }
        }

        // A row that knows what it's searchable as. Not Tag — Row(ProfileView)
        // already hangs (folder, options) off a ComboBox's Tag for the colour
        // picker's own handler, so a second use of the same property on the
        // row itself would collide with it.
        //
        // A Grid subclass rather than a wrapper, because two tests
        // (SettingsWindowCoverageTests' ColumnLabelsHasFiveColumns and
        // RowForProfileSeedsEachColumnFromStoredSettings) cast a row builder's
        // result straight to Grid, and every row builder here already returns
        // a Grid under the hood.
        internal sealed class SettingsRow : Grid
        {
            // What this row searches against — null for a row this file never
            // gave one, which SettingsFilter.Matches treats as "can't match an
            // active query".
            public string? SearchText { get; set; }

            // A row with no label of its own — a column heading, a status
            // line, the profiles card's small print — that shows only when a
            // real sibling in the same card matched, rather than being judged
            // on SearchText it doesn't have.
            public bool IsCardChrome { get; set; }
        }

        // A card's rows and the hairlines Card() interleaved between them,
        // held onto by reference so ApplyFilter can toggle IsVisible on both
        // without re-parenting anything or walking back through the tree to
        // find them — walking would mean guessing which children are rows and
        // which are separators, which is exactly the ambiguity this avoids.
        internal sealed class SettingsCard : Border
        {
            // As Card() was handed them — one entry per row, not counting the
            // hairlines, which are tracked separately below so a wrong
            // hairline is a unit-test failure (SeparatorsBefore) rather than
            // something only visible on screen.
            public IReadOnlyList<Control> Rows { get; init; } = Array.Empty<Control>();

            // Separators[i] is the hairline immediately before Rows[i + 1].
            // One shorter than Rows, because there's nothing before the first
            // row to draw a line under.
            public IReadOnlyList<Border> Separators { get; init; } = Array.Empty<Border>();

            // Applies query to every row in this card and returns whether any
            // *searchable* row matched directly — chrome rows and rows this
            // file never gave a SearchText don't count, because a chrome row
            // matching only means a sibling matched, and an unsearchable row
            // "matching" would mean this method invented an answer it doesn't
            // have. The section above uses the return value to decide whether
            // the section itself should stay visible.
            internal bool ApplyFilter(string? query, bool titleMatches)
            {
                var n = Rows.Count;
                var texts = new string?[n];
                var chrome = new bool[n];
                var searchable = new bool[n];

                for (var i = 0; i < n; i++)
                {
                    if (Rows[i] is SettingsRow row)
                    {
                        texts[i] = row.SearchText;
                        chrome[i] = row.IsCardChrome;
                        searchable[i] = true;
                    }
                }

                var visible = SettingsFilter.VisibleRows(query, texts, chrome, titleMatches);

                // A row this file can't search — a free-form control handed to
                // Card() directly, like the WSL list or a profile-directory
                // editor — is left showing whenever its card is, rather than
                // being hidden by a filter it has no way to answer.
                for (var i = 0; i < n; i++)
                {
                    if (!searchable[i]) visible[i] = true;
                }

                for (var i = 0; i < n; i++)
                {
                    Rows[i].IsVisible = visible[i];
                }

                var separatorsBefore = SettingsFilter.SeparatorsBefore(visible).ToHashSet();
                for (var i = 0; i < Separators.Count; i++)
                {
                    // Separators[i] sits before Rows[i + 1].
                    Separators[i].IsVisible = separatorsBefore.Contains(i + 1);
                }

                // An empty card is a floating rounded pill with nothing in it,
                // which reads as a rendering bug rather than "nothing here
                // matched" — hide the card itself rather than leave that up.
                IsVisible = n == 0 || visible.Any(v => v);

                var anyMatch = false;
                for (var i = 0; i < n; i++)
                {
                    if (searchable[i] && !chrome[i] && SettingsFilter.Matches(query, texts[i]))
                    {
                        anyMatch = true;
                    }
                }

                return anyMatch;
            }
        }

        private Control Body()
        {
            // First, so a Rebuild() never leaves a section from the previous
            // page reachable by id.
            _sections.Clear();

            var root = new StackPanel { Margin = new Thickness(20, 18), Spacing = 18 };

            root.Children.Add(Group("orbs", "Orbs", Card(OrbsRows())));

            root.Children.Add(Group("orb-click", "Clicking an orb", Card(ClickRows())));

            root.Children.Add(Group("auto-organize", "Auto-organize", Card(AutoOrganizeRows())));

            root.Children.Add(Group("group-outlines", "Group outlines", Card(GroupOutlineRows())));

            root.Children.Add(Group("orb-colours", "Orb colours", Card(OrbColourRows())));

            // Between the orbs and the voice, because that is where the chat
            // panel sits in the app: it is what an orb opens, and the thing
            // the voice types into.
            root.Children.Add(Group("chat-panel", "Chat panel", Card(ChatRows())));

            root.Children.Add(Group("voice", "Voice", Card(VoiceRows())));

            // Right after Voice: a turn sound and a spoken summary are both
            // "how this app tells you something happened without you looking
            // at it", and they share half their vocabulary (Off, the vibe
            // summary, a chosen file) — putting the row that hears you a turn
            // finished directly under the row that reads it to you is the
            // same walk-through order a new user would want either explained
            // in.
            root.Children.Add(Group("sounds", "Sounds", Card(SoundRows())));

            // One section per agent, each starting with whether it is tracked
            // at all and then everything about it — the panel, replying, extra
            // accounts, and on Windows the WSL distros.
            //
            // These used to be six sections scattered between Voice and the
            // Claude Desktop profiles: "Claude Code sessions" here, "Claude Code
            // profiles" four groups later, the same again for Codex, and the
            // Desktop app's own profiles in between. Nothing was wrong with any
            // of them individually; the order was just the order they were
            // added in, which is how a settings window gets that way.
            root.Children.Add(Group("claude-code", "Claude Code", ClaudeCodeSection()));

            root.Children.Add(Group("codex", "Codex", CodexSection()));

            root.Children.Add(Group("grok", "Grok Build", GrokSection()));

            root.Children.Add(Group("openclaw", "OpenClaw agents", Card(OpenClawRows())));

            // Straight after the CLI sections, beside "Other machines" below
            // and for the same reason: it is about the same Claude Code
            // sessions the section above governs, running somewhere that is not
            // this machine. The difference between the two is only whose
            // machine it is, which is why they sit together rather than one of
            // them living beside the gateway.
            root.Children.Add(Group("claude-cloud", "Claude Code in the cloud", Card(ClaudeCloudRows())));

            // Straight after the CLI sections and before the Desktop app,
            // because that is what it is about: the same Claude Code sessions
            // those two sections govern, just not on this machine.
            // One card again. There were briefly two — the direct link and the
            // relay — with the order as the recommendation; the relay is gone
            // and the recommendation went with it.
            root.Children.Add(Group("peer-link", "Other machines", Card(PeerLinkRows())));

            // Not an agent CLI at all — the Electron desktop app — so it sits
            // after them with its own profiles, which is where someone looking
            // for them would go first.
            root.Children.Add(Group("claude-desktop", "Claude Desktop",
                Card(ClaudeDesktopRows(OperatingSystem.IsMacOS())),
                ProfilesCard()));

            // macOS preference windows are dismissed by the window's own close
            // button, not by a Done inside the content. Windows expects the
            // button, so it keeps it.
            if (!OperatingSystem.IsMacOS()) root.Children.Add(DoneButton());

            return root;
        }

        // internal so a test can drive each switch directly, the same reason
        // ClickRows() below is internal — walking the visual tree to find
        // whichever control a row holds depends on which theme template
        // loaded, which is not what these rows are about.
        internal Control[] OrbsRows() => new[]
        {
            Row("Show orbs",
                Switch(SessionManager.Instance?.OrbsVisible ?? OrbweaverSettings.ShowOrbs,
                    value => SessionManager.Instance?.SetOrbsVisible(value))),
            Row("Keep orbs for", LifetimePicker(),
                "How long an orb stays after its session goes quiet. A session that's "
                + "waiting on you is never removed, however long this is — those only go "
                + "away when you answer it or reset it from the orb's menu."),
            Row("Two-letter initials",
                Switch(OrbweaverSettings.TwoLetterGlyphs, value =>
                {
                    OrbweaverSettings.TwoLetterGlyphs = value;
                    SessionManager.Instance?.ReapplyGlyphs();
                }),
                "One letter from each of the first two words of a chat's name, or the "
                + "first two letters of it when there's only one word — instead of just "
                + "the one letter every orb shows today."),
            Row("Size", OrbSizeSlider(),
                "How big every orb is. Right-click an orb to size just that one.")
        };

        // internal for the same reason OrbsRows() above is: a test can drive
        // each row's control directly.
        //
        // CB-153: "Give each session a colour" used to be built twice, back to
        // back, each its own Switch bound to the same
        // OrbweaverSettings.AutoColorSessions and the same OnAutoColorToggled
        // handler, with two help strings hand-edited slightly differently at
        // some point (compare "that has none" / "with none", "so there its
        // orb" / "so a Codex orb"). On screen that read as one switch that
        // happened to repeat its own explanation right below itself — and it
        // was not harmless: the two copies shared the *setting*, not the
        // control, so flipping one wrote the setting and left the other
        // showing the old value until something rebuilt the window. Down to
        // one row, which is what this was always meant to be; the surviving
        // wording is the later, more polished of the two ("Codex orb" over
        // "there its orb").
        internal Control[] OrbColourRows() => new[]
        {
            ColorRow("Idle", "idle"),
            ColorRow("Working", "generating"),
            ColorRow("Needs you", "waiting"),
            Row("Give each session a colour",
                Switch(OrbweaverSettings.AutoColorSessions, OnAutoColorToggled),
                "Off, only a colour you set with /color shows on an orb. On, a session "
                + "with none is given one from its working directory, so a project keeps "
                + "its colour and both CLIs agree on it. For Claude Code that writes the "
                + "same record /color writes, so the colour survives a resume and the "
                + "terminal agrees; /color still overrides it. Codex has nowhere to write "
                + "one and shows none of its own, so a Codex orb takes its Codex section's "
                + "colour if it has one and the derived colour otherwise."),
            Row("Restore the built-in colours", ResetColorsButton(),
                "The orb's fill and its glow. The menu-bar icon follows them too — it "
                + "shows the most urgent state across every session, so very light or "
                + "very dark choices can disappear into the menu bar. A session's own "
                + "/color is separate: that one goes on the orb's ring and letter.")
        };

        // --- Voice input ---
        // --- Auto-organize ---

        // internal so a test can drive the switches directly, and taking the
        // platform rather than asking for it so both shapes of this group are
        // reachable from either machine — the same reason
        // ClaudeDesktopManager.LogCandidates takes one.
        internal Control[] ClaudeDesktopRows(bool macOs)
        {
            var rows = new List<Control> { ClaudeDesktopTintRow() };

            // macOS only: this is about LaunchServices, and the collision it
            // works around is caused by the tinted clones, which are a macOS
            // feature with no Windows analogue.
            if (macOs) rows.Add(ClaudeDesktopUrlRoutingRow());

            return rows.ToArray();
        }

        internal Control ClaudeDesktopTintRow() => Row("Tint the active window",
            Switch(ClaudeDesktopOverlay.Enabled, ClaudeDesktopOverlay.SetEnabled));

        internal Control ClaudeDesktopUrlRoutingRow() => Row(
            "Send Claude links to the right profile",
            Switch(OrbweaverSettings.RouteClaudeUrls, ClaudeDesktopUrlRouter.SetEnabled),
            "Claude Desktop's sign-in callback resolves to a bundle id that every "
            + "profile shares, so without this it opens the Default profile whichever "
            + "profile you were signing in to. Only used when you have more than one.");

        // --- what a click does ---

        private static readonly (string Label, string Value)[] ClickChoices =
        {
            ("Go to the session", "terminal"),
            ("Open the chat panel", "chat"),
            ("Read the latest reply", "speak"),
            ("Nothing", "none")
        };

        // "Go to the session" rather than "Terminal", because that is what it
        // does: for a local CLI it is the terminal, and for a gateway agent
        // there is no terminal anywhere and the panel is the only place it
        // exists. One label covering both beats a label that is wrong for half
        // the orbs on screen.
        // internal, like the toggle handlers: these three return the control they
        // build, so a test can drive the control itself rather than hunting it in
        // a visual tree whose shape depends on which theme templates loaded.
        internal Control ClickPicker(Func<string> get, Action<string> set)
        {
            var current = get();
            var choices = ClickChoices.ToList();

            if (choices.All(c => c.Value != current)) choices.Add((current, current));

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(c => c.Label).ToList(),
                SelectedIndex = choices.FindIndex(c => c.Value == current),
                MinWidth = 168
            };
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index >= 0) set(choices[index].Value);
            };
            return combo;
        }

        // internal so the three rows can be driven as the window builds them,
        // which is the only way the production getters and setters below run — a
        // test supplying its own would be checking ClickPicker rather than the
        // rows.
        internal Control[] ClickRows() => new[]
        {
            Row("Click", ClickPicker(
                () => OrbweaverSettings.ClickAction,
                v => OrbweaverSettings.ClickAction = v)),
            Row("Double click", ClickPicker(
                () => OrbweaverSettings.DoubleClickAction,
                v => OrbweaverSettings.DoubleClickAction = v)),
            Row("Triple click", ClickPicker(
                () => OrbweaverSettings.TripleClickAction,
                v => OrbweaverSettings.TripleClickAction = v),
                "Binding a second or third click makes a single click wait a moment to see "
                + "whether another is coming — there is no way to tell them apart without "
                + "that pause. Leave them on Nothing and a single click acts the instant "
                + "you release, which is what the app has always done.")
        };

        internal static readonly (string Label, string Value)[] ShapeChoices =
        {
            ("Heart", "heart"),
            ("Circle", "circle"),
            ("Diamond", "diamond"),
            ("Star", "star"),
            ("Grid", "grid"),
            ("Horizontal Line", "line"),
            ("Vertical Line", "vline")
        };

        internal Control ShapePicker() => ShapePicker(
            () => OrbweaverSettings.ArrangeShape,
            v => OrbweaverSettings.ArrangeShape = v);

        // Taken over a getter and setter rather than hard-wired to
        // ArrangeShape, because there are now three shapes to pick — the chats'
        // and one for each timer-driven group that has been given its own. One
        // picker for all three keeps the six choices and the unknown-value
        // handling below in one place; three copies is how one of them ends up
        // offering five.
        internal Control ShapePicker(Func<string> get, Action<string> set)
        {
            var current = get();
            var choices = ShapeChoices.ToList();

            // A shape from a newer version, or a hand-edited settings file, is
            // shown as its own entry under its raw name rather than silently
            // swapped for the default — the same courtesy ClickPicker extends,
            // and the difference between "the app forgot my setting" and "the
            // app does not know that word".
            if (choices.All(c => c.Value != current))
                choices.Add((current, current));

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(c => c.Label).ToList(),
                SelectedIndex = choices.FindIndex(c => c.Value == current),
                MinWidth = 132
            };
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index < 0) return;

                set(choices[index].Value);
                SessionManager.Instance?.ReapplyArrangement();
            };
            return combo;
        }

        // The three answers a timer-driven group can be given. See OrbClusters
        // for what each does.
        //
        // "With the chats" and not "Shown": the whole point of the setting is
        // that there are two ways to be shown, and a label that only says
        // whether an orb exists is the label this replaces.
        private static readonly (string Label, ClusterMode Value)[] ClusterModeChoices =
        {
            ("Hidden", ClusterMode.Hidden),
            ("With the chats", ClusterMode.WithChats),
            ("Own shape", ClusterMode.OwnShape)
        };

        // Rebuilds the window rather than only writing the setting, because the
        // shape row beside it appears and disappears with the answer — there is
        // no shape to pick for a group that is hidden or is in somebody else's
        // shape, and a picker for one is worse than no picker at all. The same
        // reason OnOpenClawToggled rebuilds.
        internal Control ClusterModePicker(Func<ClusterMode> get, Action<ClusterMode> set)
        {
            var current = get();
            var choices = ClusterModeChoices.ToList();

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(c => c.Label).ToList(),
                SelectedIndex = choices.FindIndex(c => c.Value == current),
                MinWidth = 132
            };
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index < 0) return;
                if (choices[index].Value == get()) return;

                set(choices[index].Value);
                Rebuild();
            };
            return combo;
        }

        private Control[] AutoOrganizeRows()
        {
            return new Control[]
            {
                Row("Shape", ShapePicker(),
                    "The pattern orbs arrange into when you click the sparkle button on any orb's flyout."),
                Row("Spacing", SpacingSlider(),
                    "How far apart the orbs sit inside the shape. Drag to see them move in real time.")
            };
        }

        // CB-268's global outline style. The same four values a group's own
        // override can replace field by field (GroupStyleWindow). Every setter
        // goes through the S1 properties, which defer their write with SaveSoon
        // for the colour and opacity ones, and then tells the manager so a
        // backdrop already on screen repaints.
        internal Control[] GroupOutlineRows() => new[]
        {
            Row("Outline groups", Switch(OrbweaverSettings.GroupOutlinesEnabled, on =>
                {
                    OrbweaverSettings.GroupOutlinesEnabled = on;
                    SessionManager.Instance?.ReapplyGroupStyles();
                }),
                "Draw each group's shape behind its orbs. Groups can still be grabbed by the "
                + "empty space inside them when this is off."),
            ColorRow("Stroke colour",
                () => GroupStyleControls.ColorOf(OrbweaverSettings.GroupOutlineStroke),
                color => { OrbweaverSettings.GroupOutlineStroke = OrbColors.ToHex(color); SessionManager.Instance?.ReapplyGroupStyles(); },
                "The line around a group. A group can override this from its own right-click menu."),
            Row("Stroke opacity", GroupStyleControls.OpacityControl(
                    OrbweaverSettings.GroupOutlineStrokeOpacity,
                    v => { OrbweaverSettings.GroupOutlineStrokeOpacity = v; SessionManager.Instance?.ReapplyGroupStyles(); }),
                "0% hides the line, 100% draws it solid."),
            ColorRow("Fill colour",
                () => GroupStyleControls.ColorOf(OrbweaverSettings.GroupOutlineFill),
                color => { OrbweaverSettings.GroupOutlineFill = OrbColors.ToHex(color); SessionManager.Instance?.ReapplyGroupStyles(); },
                "The tint inside a group's shape."),
            Row("Fill opacity", GroupStyleControls.OpacityControl(
                    OrbweaverSettings.GroupOutlineFillOpacity,
                    v => { OrbweaverSettings.GroupOutlineFillOpacity = v; SessionManager.Instance?.ReapplyGroupStyles(); }),
                "Keep this low to read the orbs through it."),
            Row("Show group names", Switch(OrbweaverSettings.GroupNamesVisible, on =>
                {
                    OrbweaverSettings.GroupNamesVisible = on;
                    SessionManager.Instance?.ReapplyGroupStyles();
                }),
                "Write each group's name in a small caption above its outline, in the outline's "
                + "colour. Rename a group from its Group menu.")
        };

        // internal for the same reason the rows above are: a test drives the
        // slider directly rather than hunting for it through whichever theme
        // template happened to load.
        internal Control[] ChatRows() => new[]
        {
            Row("Text size", TextSizeSlider(),
                "How big the chat panel draws a conversation. "
                + (OperatingSystem.IsMacOS()
                    ? "Cmd+ and Cmd- do the same thing from the panel itself, and Cmd+0 puts it back."
                    : "Ctrl+ and Ctrl- do the same thing from the panel itself, and Ctrl+0 puts it back."))
        };

        internal Control TextSizeSlider()
        {
            // Over rungs, not over multipliers — ChatZoom.IndexOf explains why
            // the two have to agree.
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = ChatZoom.Steps.Length - 1,
                Value = ChatZoom.IndexOf(OrbweaverSettings.ChatTextScale),
                MinWidth = 160,
                SmallChange = 1,
                LargeChange = 1,
                TickFrequency = 1,
                IsSnapToTickEnabled = true
            };
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;

                OrbweaverSettings.ChatTextScale = ChatZoom.At((int)Math.Round(slider.Value));
                ChatPanel.ReapplyTextScale();
            };
            return slider;
        }

        // CB-198's global orb size. SpacingSlider's shape, with its range read
        // from OrbSizing rather than written here, so the slider, the Size menu
        // and the arrangement sweep can never disagree about which sizes exist
        // — and so Windows' measured 0.7 floor reaches the slider by itself.
        internal Slider OrbSizeSlider()
        {
            var slider = new Slider
            {
                Minimum = OrbSizing.Min,
                Maximum = OrbSizing.Max,
                Value = OrbweaverSettings.OrbSize,
                MinWidth = 160,
                SmallChange = OrbSizing.Step,
                LargeChange = 0.25,
                TickFrequency = OrbSizing.Step,
                IsSnapToTickEnabled = true
            };
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                OrbweaverSettings.OrbSize = slider.Value;
                SessionManager.Instance?.ReapplyOrbSizes();
            };
            return slider;
        }

        internal Control SpacingSlider()
        {
            var slider = new Slider
            {
                Minimum = 0.3,
                Maximum = 2.0,
                Value = OrbweaverSettings.ArrangeSpacing,
                MinWidth = 160,
                SmallChange = 0.05,
                LargeChange = 0.1,
                TickFrequency = 0.05,
                IsSnapToTickEnabled = true
            };
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                OrbweaverSettings.ArrangeSpacing = slider.Value;
                SessionManager.Instance?.ReapplyArrangement();
            };
            return slider;
        }

        // Off by default (see OrbweaverSettings.VoiceInputEnabled) —
        // turning it on is what triggers the one-time Whisper model download,
        // never the first mic click on an orb, so the multi-hundred-MB
        // fetch is always something the user just asked for here.

        // Only meaningful while a download this window kicked off is still
        // running; null the rest of the time, in which case no status row
        // is shown at all rather than a stale one.
        private string? _voiceModelStatus;

        // Only meaningful while a neural-engine download this window kicked off is
        // still running; null otherwise, so no stale row is left behind. Separate
        // from _voiceModelStatus rather than shared: enabling dictation and
        // enabling the neural voice each fetch a few hundred MB, and two
        // downloads reporting into one field would overwrite each other's text.
        private string? _neuralModelStatus;

        // Off by default and, unlike everything else in this window, off for a
        // reason that isn't about taste: while this switch is off the app opens
        // no socket, starts no background task and generates no key. Turning it
        // on is the whole of the consent to talk to a machine on the network.
        // The chat panel on a local orb, and whether it can type back.
        //
        // Unlike every other feature switch in this window the first one is
        // *on* by default, and the difference is real rather than an
        // inconsistency: it opens no socket, starts no engine and asks macOS for
        // no permission. It reads a file the hook already points at, only while
        // a panel is actually up. There is nothing to consent to.
        //
        // The second one is the OpenClaw split, for the OpenClaw reason.
        // Everything about one CLI in one place: whether it is tracked at all,
        // what its orb can do, and which extra accounts to wire.
        //
        // These used to be spread across the window — "Claude Code sessions"
        // near the top, "Claude Code profiles" four groups below it, the same
        // again for Codex, with the Desktop app's profiles in between. Nothing
        // was wrong with any of them alone; the order was the order they were
        // added in, which is how a settings window gets that way.
        internal Control[] ClaudeCodeSection()
        {
            var cards = new List<Control>();

            // CB-267: the one-time card goes first, above everything, because
            // it explains why a message sent from an orb can sit unread — which
            // is the thing someone is most likely to hit on their first try.
            if (OrbweaverSettings.ClaudeCodeEnabled && InboundNoticeVisible())
                cards.Add(Card(InboundNoticeRows()));

            cards.Add(Card(ClaudeCodeChatRows()));

            if (!OrbweaverSettings.ClaudeCodeEnabled) return cards.ToArray();

            cards.Add(Card(ProfileDirsCard(
                blurb: $"Wire {Brand.DisplayName} hooks into additional Claude Code accounts managed "
                       + "via CLAUDE_CONFIG_DIR, alongside the default ~/.claude.",
                watermark: ".claude-work",
                current: () => OrbweaverSettings.ClaudeCodeProfileDirs,
                add: OrbweaverSettings.AddClaudeCodeProfileDir,
                remove: OrbweaverSettings.RemoveClaudeCodeProfileDir,
                reapply: HookInstaller.ReapplyClaudeCode,
                verify: HookInstaller.IsWired)));

            // WSL is genuinely Windows-only, unlike the extra accounts above,
            // and belongs here because Claude Code's sessions are what it
            // reaches.
            if (OperatingSystem.IsWindows()) AddWslCard(cards);

            return cards.ToArray();
        }

        private Control[] CodexSection()
        {
            var cards = new List<Control> { Card(CodexChatRows()) };

            if (!OrbweaverSettings.CodexEnabled) return cards.ToArray();

            cards.Add(Card(ProfileDirsCard(
                blurb: $"Wire {Brand.DisplayName} hooks into additional Codex accounts managed via "
                       + "CODEX_HOME, alongside the default ~/.codex. Codex asks you to trust "
                       + "hooks the first time it sees them, once per account.",
                watermark: ".codex-work",
                current: () => OrbweaverSettings.CodexHomes,
                add: OrbweaverSettings.AddCodexHome,
                remove: OrbweaverSettings.RemoveCodexHome,
                reapply: HookInstaller.ReapplyCodex)));

            return cards.ToArray();
        }

        private Control[] GrokSection()
        {
            var cards = new List<Control> { Card(GrokChatRows()) };

            if (!OrbweaverSettings.GrokEnabled) return cards.ToArray();

            cards.Add(Card(ProfileDirsCard(
                blurb: $"Wire {Brand.DisplayName} hooks into additional Grok Build accounts managed via "
                       + "GROK_HOME, alongside the default ~/.grok.",
                watermark: ".grok-work",
                current: () => OrbweaverSettings.GrokHomes,
                add: OrbweaverSettings.AddGrokHome,
                remove: OrbweaverSettings.RemoveGrokHome,
                reapply: HookInstaller.ReapplyGrok)));

            return cards.ToArray();
        }

        // Switching a CLI off hides the rest of its section rather than greying
        // it out. A column of dead switches is a worse answer to "I only use
        // Claude Code" than a two-line section is: what remains is what still
        // does something.
        internal Control[] ClaudeCodeChatRows()
        {
            var rows = new List<Control>
            {
                Row("Show Claude Code sessions",
                    Switch(OrbweaverSettings.ClaudeCodeEnabled, OnClaudeCodeEnabledToggled),
                    "Off, Claude Code sessions get no orbs and are left out of the menu bar. "
                    + "Its hooks are left alone — they are your own config, and they keep "
                    + "writing where the app will find them again the moment you switch this "
                    + "back on.")
            };

            if (!OrbweaverSettings.ClaudeCodeEnabled) return rows.ToArray();

            rows.Add(Row("Chat panel on the orb",
                Switch(OrbweaverSettings.ClaudeCodeChatEnabled, OnClaudeCodeChatToggled),
                "Adds a keyboard button to the orb's hover menu that opens the session's "
                + "conversation — the same panel OpenClaw agents use. It is the same "
                + "conversation as the terminal's, not a copy: it reads the transcript "
                + "Claude Code already writes. Clicking the orb still goes to the terminal."));

            if (!OrbweaverSettings.ClaudeCodeChatEnabled) return rows.ToArray();

            rows.Add(Row("Allow replying to sessions",
                Switch(OrbweaverSettings.ClaudeCodeReplyEnabled, OnClaudeCodeReplyToggled),
                "Off, the panel shows what a session is doing. On, you can type into it, "
                + "answer its permission prompts and interrupt it — by typing into its tmux "
                + "pane, exactly as if you had typed there yourself, so the terminal shows it "
                + "too. Sessions not running under tmux stay read-only either way, because "
                + "the only way to type into those is to bring their window to the front."));

            // While the first-run card is showing it carries these same rows,
            // so they are not drawn twice on one page.
            if (OrbweaverSettings.ClaudeCodeReplyEnabled && !InboundNoticeVisible())
                rows.AddRange(InboundRows());

            rows.Add(Row("Usage orbs for each account",
                Switch(OrbweaverSettings.AccountUsageEnabled, OnAccountUsageToggled),
                "An orb per Claude Code account wearing three rings — this week, this "
                + "five-hour session, and extra usage. Hover one for the numbers; click it "
                + "to keep the card up. The figures come from Claude Code itself, asked "
                + "once every five minutes per account; nothing here reads your login "
                + "details, and asking costs no tokens."));

            if (OrbweaverSettings.AccountUsageEnabled)
                rows.AddRange(AccountOrbRows(AccountUsageSource.ClaudeCode));

            return rows.ToArray();
        }

        // ---- CB-267: let orbs message sessions that run without permission prompts ----
        //
        // Claude Code holds a message from another session until someone approves
        // it in the receiving session, unless that account's settings.json carries
        // crossSessionInbound: "accept". These rows are the one place the app
        // offers to write that key, and they never do it on the first click: the
        // switch only asks, the inline confirmation says what it costs, and the
        // file is touched when the person presses "Turn it on".
        //
        // Every file access goes through one of the three seams below so the
        // tests never read or write a real ~/.claude.
        internal static Func<IReadOnlyList<string>> InboundRoots { get; set; } =
            () => ClaudeConfigRoots.All().Where(Directory.Exists).ToList();

        // Read and parsed in one seam: what the rows need is the answer, and a
        // test that has to fake the file text *and* the parser is testing its
        // own fakes. The composition itself is covered where those two live.
        internal static Func<string, InboundSetting> InboundState { get; set; } =
            root => PeerInbound.ReadSetting(PeerInboundSettingsFile.ReadText(root));

        internal static Func<string, bool, PeerInboundWrite> InboundApply { get; set; } =
            PeerInboundSettingsFile.Apply;

        internal static Func<string, string> InboundDisplayPath { get; set; } =
            path => PeerInbound.DisplayPath(
                path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // Roots with a "Turn it on?" question open, and the last failure per root.
        // Instance state on purpose: a Rebuild() must not lose either, but a new
        // window starts clean.
        private readonly HashSet<string> _inboundConfirming = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _inboundFailures = new(StringComparer.Ordinal);

        internal static bool InboundNoticeVisible() =>
            !OrbweaverSettings.PeerInboundNoticeSeen
            && InboundRoots().Any(root => InboundState(root) != InboundSetting.Accepting);

        internal Control[] InboundNoticeRows()
        {
            var rows = new List<Control>
            {
                NoteRow(new TextBlock
                {
                    Text = $"Claude Code holds a message from {Brand.DisplayName} until someone "
                           + "approves it in the receiving session, when that session runs without "
                           + "permission prompts. You can let orbs message those sessions directly.",
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                })
            };

            rows.AddRange(InboundRows());

            var gotIt = new Button { Content = "Got it", HorizontalAlignment = HorizontalAlignment.Right };
            gotIt.Click += (_, _) => OnInboundNoticeDismissed();
            rows.Add(NoteRow(gotIt));

            return rows.ToArray();
        }

        // One switch per existing config root. The path is only added to the
        // label when there is more than one root, because "~/.claude" beside a
        // single switch is noise and beside two is the only way to tell them apart.
        internal Control[] InboundRows()
        {
            var roots = InboundRoots();
            var rows = new List<Control>();

            foreach (var root in roots)
            {
                var state = InboundState(root);
                var confirming = _inboundConfirming.Contains(root);
                var path = InboundDisplayPath(PeerInboundSettingsFile.PathFor(root));
                var label = roots.Count > 1
                    ? $"{PeerInbound.ButtonLabel} ({Path.GetFileName(root)})"
                    : PeerInbound.ButtonLabel;

                var toggle = Switch(
                    state == InboundSetting.Accepting || confirming,
                    on => OnInboundToggled(root, on));
                toggle.IsEnabled = state != InboundSetting.Unreadable;

                rows.Add(Row(label, toggle,
                    state == InboundSetting.Unreadable
                        ? $"{path} could not be read, so it was left alone."
                        : $"Writes {PeerInbound.SettingKey}: {PeerInbound.AcceptValue} to {path}, "
                          + $"keeping the old file beside it as settings.json{PeerInboundSettingsFile.BackupSuffix}."));

                if (confirming) rows.Add(InboundConfirmRow(root, path));

                if (_inboundFailures.TryGetValue(root, out var reason))
                    rows.Add(NoteRow(new TextBlock
                    {
                        Text = $"Not changed: {reason} ({path})",
                        FontSize = 11,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap
                    }));
            }

            return rows.ToArray();
        }

        private Control InboundConfirmRow(string root, string path)
        {
            var turnOn = new Button { Content = PeerInbound.ConfirmLabel };
            turnOn.Click += (_, _) => OnInboundConfirmed(root);

            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) => OnInboundCancelled(root);

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = PeerInbound.TradeOff,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = path,
                FontSize = 11,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { turnOn, cancel }
            });

            return NoteRow(panel);
        }

        // Turning it on only asks. Turning it off is the safe direction, so it
        // writes straight away (the writer keeps a backup either way) — except
        // when the switch is being flipped back while a question is open, which
        // is just a cancel and must not touch the file.
        internal void OnInboundToggled(string root, bool on)
        {
            _inboundFailures.Remove(root);

            if (on)
            {
                _inboundConfirming.Add(root);
            }
            else if (!_inboundConfirming.Remove(root))
            {
                RecordInboundOutcome(root, InboundApply(root, false));
            }

            Rebuild();
        }

        internal void OnInboundConfirmed(string root)
        {
            _inboundConfirming.Remove(root);

            var outcome = InboundApply(root, true);
            RecordInboundOutcome(root, outcome);

            if (outcome.Kind is PeerInboundWriteKind.Written or PeerInboundWriteKind.Unchanged)
                OrbweaverSettings.PeerInboundNoticeSeen = true;

            Rebuild();
        }

        // Rebuild() re-reads the file, so the switch lands back where the file
        // says it is — off — rather than where it was dragged to.
        internal void OnInboundCancelled(string root)
        {
            _inboundConfirming.Remove(root);
            Rebuild();
        }

        internal void OnInboundNoticeDismissed()
        {
            OrbweaverSettings.PeerInboundNoticeSeen = true;
            Rebuild();
        }

        private void RecordInboundOutcome(string root, PeerInboundWrite outcome)
        {
            if (outcome.Kind is PeerInboundWriteKind.Refused or PeerInboundWriteKind.Failed)
                _inboundFailures[root] = outcome.Reason ?? "the file could not be written";
            else
                _inboundFailures.Remove(root);
        }

        internal void OnAccountUsageToggled(bool enabled)
        {
            OrbweaverSettings.AccountUsageEnabled = enabled;

            // Whoever changed the setting says so — nothing on the scan path
            // treats a setting change as a session change.
            SessionManager.Instance?.ReapplyAccountOrbs();
        }

        internal void OnClaudeCodeChatToggled(bool enabled)
        {
            OrbweaverSettings.ClaudeCodeChatEnabled = enabled;
            Rebuild();
        }

        internal void OnClaudeCodeReplyToggled(bool enabled)
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = enabled;
        }

        // No re-wiring. The hooks read a marker file beside the status files,
        // which the scan reconciles with this setting within a couple of
        // seconds — see SessionManager.SyncAutoColorMarker. An earlier version
        // baked a flag into the hook command instead, which meant every toggle
        // rewrote Codex's hooks.json and cost the user their hook trust.
        internal void OnAutoColorToggled(bool enabled)
        {
            OrbweaverSettings.AutoColorSessions = enabled;
        }

        // The same two powers for Codex, and its own pair of switches rather
        // than one shared "local CLI" setting: someone can reasonably want to
        // read a Codex session and never type into it while doing the opposite
        // for Claude Code, and the two CLIs are wired independently anyway.
        //
        // The wording differs where the behaviour does, and only there.
        private Control[] CodexChatRows()
        {
            var rows = new List<Control>
            {
                Row("Show Codex sessions",
                    Switch(OrbweaverSettings.CodexEnabled, OnCodexEnabledToggled),
                    "Off, Codex sessions get no orbs and are left out of the menu bar. Its "
                    + "hooks are left alone, so nothing has to be re-approved when you switch "
                    + "this back on.")
            };

            if (!OrbweaverSettings.CodexEnabled) return rows.ToArray();

            rows.AddRange(new Control[]
            {
                Row("Chat panel on the orb",
                    Switch(OrbweaverSettings.CodexChatEnabled, OnCodexChatToggled),
                    "The same panel Claude Code sessions get, reading the rollout transcript "
                    + "Codex already writes. It is the same conversation as the terminal's, not "
                    + "a copy. Clicking the orb still goes to the terminal.")
            });

            if (!OrbweaverSettings.CodexChatEnabled) return rows.ToArray();

            rows.Add(Row("Allow replying to sessions",
                Switch(OrbweaverSettings.CodexReplyEnabled, OnCodexReplyToggled),
                "Off, the panel shows what a session is doing. On, you can type into it, "
                + "answer its approval prompts and interrupt it — by typing into its tmux "
                + "pane, exactly as if you had typed there yourself. Codex's approval prompts "
                + "are numbered the same way Claude Code's are, and a digit answers one "
                + "outright. Sessions not running under tmux stay read-only either way."));

            rows.Add(Row("Usage orbs for each Codex account",
                Switch(OrbweaverSettings.CodexAccountUsageEnabled, OnCodexAccountUsageToggled),
                "An orb per Codex account wearing its weekly credit ring and the five-hour "
                + "window when Codex reports one. Hover for the numbers; click to keep the "
                + "card up. The figures come from the last session's own rollout — Codex "
                + "writes them on every turn — and nothing here reads your login token."));

            if (OrbweaverSettings.CodexAccountUsageEnabled)
                rows.AddRange(AccountOrbRows(AccountUsageSource.Codex));

            return rows.ToArray();
        }

        // CB-287: the accounts a CLI's usage orbs are currently drawn for. A seam
        // rather than a direct SessionManager read so tests can name accounts
        // without a running poller; the default asks the live orbs, which is why
        // an account only appears here once it has answered a poll (listing config
        // directories instead would derive labels from auth files a second way).
        internal static Func<IReadOnlyList<AccountOrbIdentity>> UsageAccounts { get; set; } =
            () => SessionManager.Instance?.AccountOrbList() ?? [];

        // One row per account of this CLI: its derived label over the same editor
        // the orb's "Customize…" opens. The editor panels refresh themselves when
        // an override changes — from here or from that window — so this does not
        // rebuild the whole page on Changed: a rebuild replaces the text box the
        // user is typing in, and a LostFocus commit would then eat the click that
        // moved focus to the next field.
        internal Control[] AccountOrbRows(AccountUsageSource source)
        {
            var rows = new List<Control>();
            foreach (var account in UsageAccounts())
            {
                if (account.Source != source) continue;

                var stack = new StackPanel { Spacing = 8 };
                stack.Children.Add(new TextBlock
                {
                    Text = account.DerivedLabel,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold
                });
                stack.Children.Add(new AccountOrbEditorPanel(account));

                var row = new SettingsRow
                {
                    Margin = new Thickness(14, 10),
                    SearchText = SettingsFilter.TextOf($"{account.DerivedLabel} orb initials name icon", null)
                };
                row.Children.Add(stack);
                rows.Add(row);
            }

            return rows.ToArray();
        }

        private Control[] GrokChatRows()
        {
            var rows = new List<Control>
            {
                Row("Show Grok Build sessions",
                    Switch(OrbweaverSettings.GrokEnabled, OnGrokEnabledToggled),
                    "Off, Grok Build sessions get no orbs and are left out of the menu bar. "
                    + "Its hooks are left alone — they are your own config, and they keep "
                    + "writing where the app will find them again the moment you switch this "
                    + "back on.")
            };

            if (!OrbweaverSettings.GrokEnabled) return rows.ToArray();

            rows.Add(Row("Chat panel on the orb",
                Switch(OrbweaverSettings.GrokChatEnabled, OnGrokChatToggled),
                "The same panel Claude Code sessions get, reading the updates.jsonl "
                + "transcript Grok already writes. It is the same conversation as the "
                + "terminal's, not a copy. Clicking the orb still goes to the terminal."));

            if (!OrbweaverSettings.GrokChatEnabled) return rows.ToArray();

            rows.Add(Row("Allow replying to sessions",
                Switch(OrbweaverSettings.GrokReplyEnabled, OnGrokReplyToggled),
                "Off, the panel shows what a session is doing. On, you can type into it, "
                + "answer its permission prompts and interrupt it — by typing into its tmux "
                + "pane. Sessions not running under tmux stay read-only either way."));

            rows.Add(Row("Usage orbs for each Grok account",
                Switch(OrbweaverSettings.GrokAccountUsageEnabled, OnGrokAccountUsageToggled),
                "An orb per Grok Build account wearing its weekly credit ring, and "
                + "on-demand spend when that is switched on. Grok has no five-hour window, "
                + "so that ring is omitted rather than drawn at zero. Hover one for the "
                + "numbers; click it to keep the card up. Nothing here reads your login "
                + "token."));

            if (!OrbweaverSettings.GrokAccountUsageEnabled) return rows.ToArray();

            rows.AddRange(AccountOrbRows(AccountUsageSource.Grok));

            rows.Add(Row("Keep Grok usage fresh automatically",
                Switch(OrbweaverSettings.GrokAutoRefreshEnabled, OnGrokAutoRefreshToggled),
                "Grok only reports its own usage once, when it starts, and never again for "
                + "the life of that process — there is no lighter way to ask it. On, this "
                + "starts and stops Grok in the background roughly every twenty minutes "
                + "purely to force a fresh number, in a scratch folder rather than one of "
                + "your projects. It runs for about eight seconds each time and shows no "
                + "window: three cycles an hour, some twenty-four seconds of Grok running "
                + "in total, and nothing appears on your screen or in your dock. Off, the "
                + "orb keeps showing whatever the last real Grok session reported, dimming "
                + "and dating it once that is more than fifteen minutes old — which, if you "
                + "have not run Grok today, can be hours.\n\n"
                + "This buys freshness, not movement. If you are not using Grok, a refresh "
                + "will report the same percentage it did before, and that is the right "
                + "answer rather than a failed one: the orb simply stops being dimmed and "
                + "starts claiming the number is current."));

            return rows.ToArray();
        }

        // Rebuild, because switching a CLI off removes the rest of its section.
        internal void OnClaudeCodeEnabledToggled(bool enabled)
        {
            OrbweaverSettings.ClaudeCodeEnabled = enabled;
            Rebuild();
        }

        internal void OnCodexEnabledToggled(bool enabled)
        {
            OrbweaverSettings.CodexEnabled = enabled;
            Rebuild();
        }

        internal void OnCodexChatToggled(bool enabled)
        {
            OrbweaverSettings.CodexChatEnabled = enabled;
            Rebuild();
        }

        internal void OnCodexReplyToggled(bool enabled)
        {
            OrbweaverSettings.CodexReplyEnabled = enabled;
        }

        internal void OnCodexAccountUsageToggled(bool enabled)
        {
            OrbweaverSettings.CodexAccountUsageEnabled = enabled;
            SessionManager.Instance?.ReapplyAccountOrbs();
        }

        internal void OnGrokEnabledToggled(bool enabled)
        {
            OrbweaverSettings.GrokEnabled = enabled;
            Rebuild();
        }

        internal void OnGrokChatToggled(bool enabled)
        {
            OrbweaverSettings.GrokChatEnabled = enabled;
            Rebuild();
        }

        internal void OnGrokReplyToggled(bool enabled)
        {
            OrbweaverSettings.GrokReplyEnabled = enabled;
        }

        internal void OnGrokAccountUsageToggled(bool enabled)
        {
            OrbweaverSettings.GrokAccountUsageEnabled = enabled;

            // Turning this off also turns auto-refresh off, rather than
            // leaving it true underneath a row that just vanished. Auto-refresh
            // starts the user's real Grok app in the background and is
            // deliberately a second, separate opt-in for exactly that reason —
            // leaving it flagged on while hidden would mean a later re-enable of
            // this switch silently resumes it with no fresh consent, which is
            // the one thing that separate-switch design was supposed to
            // prevent. Only on-to-off writes anything here; turning usage orbs
            // back on never flips auto-refresh back on, only makes its own row
            // visible again.
            if (!enabled) OrbweaverSettings.GrokAutoRefreshEnabled = false;

            SessionManager.Instance?.ReapplyAccountOrbs();
            Rebuild();
        }

        internal void OnGrokAutoRefreshToggled(bool enabled)
        {
            OrbweaverSettings.GrokAutoRefreshEnabled = enabled;
        }

        internal Control[] ClaudeCloudRows()
        {
            var rows = new List<Control>
            {
                Row("Show cloud sessions",
                    Switch(OrbweaverSettings.ClaudeCloudEnabled, OnClaudeCloudToggled),

                    // The Keychain prompt is named because it is the one thing
                    // about this feature that looks alarming and is not. It
                    // arrives unannounced, it names an item the user has never
                    // heard of, and a prompt nobody expected is a prompt people
                    // decline — after which the feature never works and nothing
                    // on screen says why.
                    //
                    // **This used to promise that "Always Allow" stops it asking
                    // again, and that promise is withdrawn.** On a machine where
                    // the read had been succeeding, a later secret read blocked
                    // indefinitely, and in between the credential's stamp moved
                    // — the CLI had refreshed its token. So a refresh appears
                    // able to put the prompt back. Appears: what is established
                    // is that the read can block and that the credential was
                    // rewritten, and the causal link between them is inferred
                    // rather than demonstrated.
                    //
                    // The copy therefore promises less rather than explaining a
                    // mechanism nobody has shown. No frequency, no "once", no
                    // "every few hours" — a number here would be the same
                    // mistake in a more precise costume, and the status line
                    // below is what tells the user where the read actually got
                    // to on this machine today.
                    //
                    // CB-199: the same switch now lets the panel *send*, and the
                    // copy says so because a toggle labelled "show" that also
                    // writes into a session would be a surprise. No second
                    // setting, by decision. "As the account that owns it" is the
                    // true half of the multi-account story: a send uses the login
                    // the session was listed under, never whichever is current.
                    "Shows an orb for each Claude Code session running in Anthropic's cloud. "
                    + "Reads the login the Claude Code CLI already stores on this machine, so "
                    + "there is nothing to sign in to — macOS will ask permission to read that "
                    + "item from your Keychain. Choose \u201CAlways Allow\u201D so you are not "
                    + "asked every time; Claude Code refreshing its own login can bring the "
                    + "prompt back. Every Claude Code account directory you have listed is read "
                    + "(one prompt per account), and a prompt you decline is not asked again until that login changes or you press Retry; directories not listed are not seen. If no orbs appear, the line below says how far the read "
                    + "got. Its chat panel can also send into the session, and its right-click "
                    + "menu can archive or delete it, as the account that owns it, using that "
                    + "same login. A Claude Desktop chat that has Remote Control on is sent "
                    + "into with that same login too, and what you send arrives there as a "
                    + "teammate's request rather than as your own typing.")
            };

            // Progressive disclosure, the same as every other section here: off
            // means the app asks the OS for no credential and opens no socket,
            // so there is nothing further to configure and nothing further to
            // show. See ClaudeCloudSessions.Snapshot, which holds that gate
            // itself rather than leaving it to the scan.
            if (!OrbweaverSettings.ClaudeCloudEnabled) return rows.ToArray();

            // Kept as a field and ticked rather than rebuilt, for the reason the
            // gateway's own status line is: this changes while you are looking
            // at it — a Keychain prompt gets answered, a poll comes back, a
            // token turns out to have expired — and a line that was only true
            // when the window opened is worse than no line at all.
            _claudeCloudStatus = new TextBlock
            {
                Text = ClaudeCloudSessions.StatusText,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            };

            rows.Add(NoteRow(_claudeCloudStatus));

            // Because the first failure this hits is almost always the Keychain
            // prompt being dismissed, and the fix for that is to ask again.
            // Without a button the only way back is to toggle the switch off and
            // on, which is a thing users discover rather than a thing the window
            // offers.
            var retry = new Button
            {
                Content = "Retry",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            retry.Click += (_, _) => OnClaudeCloudRetryClicked();
            rows.Add(Row("", retry));

            return rows.ToArray();
        }

        // Immediately, not at the next launch: turning it off should take the
        // orbs off the screen and stop the app reaching for the credential while
        // the user is still looking at the switch. Same as OnOpenClawToggled.
        private void OnClaudeCloudToggled(bool enabled)
        {
            OrbweaverSettings.ClaudeCloudEnabled = enabled;
            ClaudeCloudSessions.Restart();
            Rebuild();
        }

        private void OnClaudeCloudRetryClicked()
        {
            ClaudeCloudSessions.Restart();
            Rebuild();
        }

        internal Control[] OpenClawRows()
        {
            var rows = new List<Control>
            {
                Row("Show OpenClaw agents (experimental)",
                    Switch(OrbweaverSettings.OpenClawEnabled, OnOpenClawToggled),
                    "Shows an orb for each recently active session on an OpenClaw gateway, "
                    + $"alongside your Claude Code ones. Read-only unless \"Allow replying to agents\" "
                    + $"below is on: {Brand.DisplayName} can see what your agents are doing, and "
                    + "can only ask them to do something once you allow it.")
            };

            if (!OrbweaverSettings.OpenClawEnabled) return rows.ToArray();

            rows.Add(Row("Gateway address", GatewayHostBox(),
                "The address of the machine running the gateway — an IP, because the "
                + "certificate it serves carries no hostname. Port "
                + OrbweaverSettings.DefaultOpenClawPort + " unless you changed it."));

            rows.Add(Row("Gateway token", GatewayTokenBox(),
                "From `gateway.auth.token` in the gateway's own openclaw.json. Stored "
                + "outside settings.json, in a file only you can read."));

            rows.Add(Row("Show sessions active within", ActiveWithinPicker(),
                "A gateway remembers every conversation it has ever had, so only recent "
                + "ones get orbs. Anything currently working shows regardless. Note that "
                + "the gateway's own idea of \"recent\" lags badly for Discord chats, so "
                + $"{Brand.DisplayName} also counts anything it has watched happen since it started."));

            rows.Add(Row("Heartbeat sessions", ClusterModePicker(
                    () => OrbweaverSettings.OpenClawHeartbeatMode,
                    OnOpenClawHeartbeatModeChanged),
                "A gateway wakes each agent on a timer to do background work, and it does "
                + "that in the agent's own main session — so those orbs go active together "
                + "every few minutes with nobody on the other end. They wear a beating "
                + "heart. Hidden takes them off the screen; Own shape keeps them, arranged "
                + "beside your conversations instead of among them. The agents keep their "
                + "colours in any channel they are in whichever you pick. Pin one of "
                + "these with Keep on screen and it shows whatever this says. One exception, "
                + "and it changes what you see: with Allow approving agent actions on, a hidden "
                + "session that is waiting for your approval shows its orb until you answer."));

            if (OrbweaverSettings.OpenClawHeartbeatMode == ClusterMode.OwnShape)
            {
                rows.Add(Row("Heartbeat shape", ShapePicker(
                        () => OrbweaverSettings.OpenClawHeartbeatShape,
                        v => OrbweaverSettings.OpenClawHeartbeatShape = v),
                    "The pattern the heartbeat orbs arrange into, drawn beside the one your "
                    + "chats use. Pick something that is not the chats' shape — telling two "
                    + "hearts apart across a screen is the one thing this cannot help with."));
            }

            rows.Add(Row("Cron sessions", ClusterModePicker(
                    () => OrbweaverSettings.OpenClawCronMode,
                    OnOpenClawCronModeChanged),
                "Jobs somebody scheduled on the gateway, which wake on their own timer with "
                + "nobody on the other end either. Same three answers as heartbeats above."));

            if (OrbweaverSettings.OpenClawCronMode == ClusterMode.OwnShape)
            {
                rows.Add(Row("Cron shape", ShapePicker(
                        () => OrbweaverSettings.OpenClawCronShape,
                        v => OrbweaverSettings.OpenClawCronShape = v),
                    "The pattern the cron orbs arrange into. Set both of these to Own shape "
                    + "and the arrange button draws up to four patterns — your chats, the "
                    + "heartbeats, these and your agents' own sessions — side by side rather than one."));
            }

            // CB-270. After the cron rows because it is the same kind of setting;
            // the label and tip live in OpenClawNativeText, which says why it is
            // not called "Direct sessions".
            rows.Add(Row(OpenClawNativeText.SettingLabel, ClusterModePicker(
                    () => OrbweaverSettings.OpenClawNativeMode,
                    OnOpenClawNativeModeChanged),
                OpenClawNativeText.SettingTip));

            if (OrbweaverSettings.OpenClawNativeMode == ClusterMode.OwnShape)
            {
                rows.Add(Row(OpenClawNativeText.ShapeLabel, ShapePicker(
                        () => OrbweaverSettings.OpenClawNativeShape,
                        v => OrbweaverSettings.OpenClawNativeShape = v),
                    OpenClawNativeText.ShapeTip));
            }

            rows.Add(Row("Allow replying to agents",
                Switch(OrbweaverSettings.OpenClawReplyEnabled, OnOpenClawReplyToggled),
                "Off, this shows what your agents are doing. On, you can also reply to "
                + "them from an orb — which asks the gateway for write permission, so you "
                + "have to approve this device again there (`openclaw devices approve --latest`)."));

            // CB-270 Stage 6. A third power, separate from the other two: replying
            // can make an agent act, approving lets you decide when one asks first.
            // The text says what it grants and what it never does, and that turning
            // it on or off needs the device approved again on the gateway.
            rows.Add(Row("Allow approving agent actions",
                Switch(OrbweaverSettings.OpenClawApproveEnabled, OnOpenClawApproveToggled),
                "Off, you can see your agents but not answer when one asks permission. "
                + "On, an agent waiting for your say-so before it runs a command turns "
                + "its orb amber, and its panel offers Allow once or Deny. Nothing is "
                + "ever allowed automatically, and there is no \"always\" here. This asks "
                + "the gateway for approval permission, so you have to approve this "
                + "device again there (`openclaw devices approve --latest`), and again "
                + "if you turn it off. It is independent of replying."));

            // Kept as a field and ticked, rather than rebuilt: the connection
            // changes state while you are looking at it — pairing gets approved,
            // a machine wakes up — and a status line that only tells the truth
            // at the moment the window was built is worse than none. Rebuilding
            // the window instead would be simpler and would also take the focus
            // out of whichever box was being typed into.
            _openClawStatus = new TextBlock
            {
                Text = OpenClawSessions.StatusText,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            };

            rows.Add(NoteRow(_openClawStatus));

            // The first connection from a new install lands in the gateway's
            // pending list and stays there until a human approves it, so the
            // status line above will say so rather than looking broken.
            var reconnect = new Button
            {
                Content = "Reconnect",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            reconnect.Click += (_, _) => OnOpenClawReconnectClicked();
            rows.Add(Row("", reconnect));

            // Only when the pin is the thing standing in the way, because this
            // is the one control here that gives something up.
            //
            // It has to exist. A gateway that regenerates its certificate —
            // reinstalled, upgraded, switched to mkcert — is refused for ever
            // after with a message the user can do nothing about: Reconnect
            // fails the same way every time, and the only way through was to
            // edit settings.json by hand. That is not a security property, it is
            // a dead end that teaches people to distrust the message.
            //
            // Deliberately not automatic, and deliberately not a general "always
            // trust" switch. Accepting a new certificate is exactly what an
            // interception needs you to do, so it stays a separate, explicit act
            // taken while looking at a line that says what changed.
            if (OpenClawSessions.CertificateRejected)
            {
                var trust = new Button
                {
                    Content = "Trust the new certificate",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
                };

                // The pin is cleared rather than set to what was observed: the
                // next successful connection records it anyway (trust on first
                // use), and writing a fingerprint the app has been *refusing* is
                // a longer way round to the same place with more to get wrong.
                //
                // TrustNewCertificate rather than doing it here, so the button
                // is gone by the time Rebuild runs — see its own comment.
                trust.Click += (_, _) => OnTrustNewCertificateClicked();

                rows.Add(Row("", trust,
                    "The gateway's certificate has changed since this install first "
                    + "connected. That is normal after the gateway is reinstalled or "
                    + "upgraded — and is also what someone impersonating it would look "
                    + "like. Only do this if you know why it changed."));
            }

            return rows.ToArray();
        }

        // Excluded from coverage: reconnects to the gateway over a real socket —
        // the same reason OnOpenClawToggled below is excluded. The button that
        // reaches this is covered by the rows around it.
        [ExcludeFromCodeCoverage]
        private void OnOpenClawReconnectClicked()
        {
            OpenClawSessions.Restart();
            Rebuild();
        }

        // Excluded from coverage: clears the pinned certificate and reconnects,
        // which is a real socket the same as OnOpenClawReconnectClicked above.
        // The row that reaches this — including that it only appears when
        // OpenClawSessions.CertificateRejected is set — is covered separately.
        [ExcludeFromCodeCoverage]
        private void OnTrustNewCertificateClicked()
        {
            OpenClawSessions.TrustNewCertificate();
            Rebuild();
        }

        private static readonly (string Label, int Minutes)[] ActiveWithinChoices =
        {
            ("15 minutes", 15),
            ("1 hour", 60),
            ("4 hours", 240),
            ("12 hours", 720),
            ("Everything", OrbweaverSettings.OpenClawActiveWithinAll)
        };

        internal Control ActiveWithinPicker()
        {
            var current = OrbweaverSettings.OpenClawActiveWithinMinutes;
            var choices = ActiveWithinChoices.ToList();

            // Same courtesy LifetimePicker extends: a value typed into
            // settings.json by hand shows as itself rather than being silently
            // rounded to the nearest one on the list.
            if (choices.All(choice => choice.Minutes != current))
            {
                choices.Insert(choices.Count - 1, ($"{current} minutes", current));
            }

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(choice => choice.Label).ToList(),
                SelectedIndex = choices.FindIndex(choice => choice.Minutes == current),
                MinWidth = 132
            };

            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index < 0 || index >= choices.Count) return;

                var minutes = choices[index].Minutes;
                if (minutes == OrbweaverSettings.OpenClawActiveWithinMinutes) return;

                // No reconnect: this only changes which of the sessions we
                // already have gets an orb, and the next poll is a few seconds
                // away.
                OrbweaverSettings.OpenClawActiveWithinMinutes = minutes;
            };

            return combo;
        }

        private TextBlock? _openClawStatus;
        private TextBlock? _claudeCloudStatus;
        private DispatcherTimer? _openClawStatusTimer;

        // Read by the tests that drive OnStatusTick. Internal rather than
        // reflected because there is nothing private about what a label says —
        // the fields are private only so that nothing outside assigns them.
        internal string? OpenClawStatusText => _openClawStatus?.Text;
        internal string? ClaudeCloudStatusText => _claudeCloudStatus?.Text;
        internal string? PeerLinkStatusText => _peerLinkStatus?.Text;

        // Excluded from coverage: starts a real one-second Avalonia timer. The
        // work it schedules is OnStatusTick below, which is reachable directly and
        // is tested that way — this is only the subscription and the Start().
        [ExcludeFromCodeCoverage]
        private void StartStatusTicker()
        {
            _openClawStatusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _openClawStatusTimer.Tick -= OnStatusTick;
            _openClawStatusTimer.Tick += OnStatusTick;
            _openClawStatusTimer.Start();
        }

        // Ticks both status lines, whichever of them the current window actually
        // built. Each is null unless its own section is expanded, so this is one
        // timer serving at most two labels rather than a timer each.
        internal void OnStatusTick(object? sender, EventArgs e)
        {
            if (_openClawStatus is not null)
            {
                var gateway = OpenClawSessions.StatusText;
                if (_openClawStatus.Text != gateway) _openClawStatus.Text = gateway;
            }

            if (_claudeCloudStatus is not null)
            {
                // Changes under you for a reason the others do not have: the
                // first read is gated on a Keychain prompt the user is looking
                // at a different window to answer, so this line goes from
                // "checking" to a real answer at a moment nothing else in this
                // window knows about.
                var cloud = ClaudeCloudSessions.StatusText;
                if (_claudeCloudStatus.Text != cloud) _claudeCloudStatus.Text = cloud;
            }

            if (_peerLinkStatus is not null)
            {
                // Changes under you more than either of the others: a machine
                // waking on the network appears within a discovery interval,
                // and the whole point of the line is to say when that happened.
                var link = PeerSessions.StatusText();
                if (_peerLinkStatus.Text != link) _peerLinkStatus.Text = link;
            }
        }

        internal Control GatewayHostBox()
        {
            var box = new TextBox
            {
                Text = OrbweaverSettings.OpenClawHost,
                Watermark = "198.51.100.10",
                Width = 220
            };

            // On losing focus rather than per keystroke: every edit restarts the
            // connection, and restarting once per character typed would hammer
            // the gateway with half-written addresses.
            box.LostFocus += (_, _) =>
            {
                var value = (box.Text ?? "").Trim();
                if (value == OrbweaverSettings.OpenClawHost) return;

                OnGatewayHostChanged(value);
            };

            return box;
        }

        // Excluded from coverage: writes the new address, clears the pinned
        // certificate — a different gateway is a different certificate, and
        // keeping the old pin would refuse the new one for reasons the user
        // could not possibly guess — and reconnects over a real socket. The
        // no-op path above, losing focus without having actually changed the
        // address, is what a test can safely drive instead.
        [ExcludeFromCodeCoverage]
        internal void OnGatewayHostChanged(string value)
        {
            OrbweaverSettings.OpenClawHost = value;
            OrbweaverSettings.OpenClawFingerprint = "";
            OpenClawSessions.Restart();
            Rebuild();
        }

        // Excluded from coverage: the commit itself. Writing a token calls
        // OnGatewayTokenChanged, which restarts the connection — and while
        // Restart() is inert with the feature off, the point of this handler is
        // the write, so exercising it means exercising that. The guard in front
        // of it is what stops tabbing through the window rewriting a token that
        // has not changed, and OnGatewayTokenChanged itself is covered directly
        // in SettingsGatewayFieldTests, which calls it rather than going through
        // the box.
        [ExcludeFromCodeCoverage]
        private void CommitGatewayToken(TextBox box, string host, string? existing)
        {
            var value = (box.Text ?? "").Trim();
            if (string.IsNullOrEmpty(host) || value == (existing ?? "")) return;

            OnGatewayTokenChanged(host, value);
        }

        // Runs the installer off the UI thread, then says on the card what happened
        // and gives the two controls back (CB-258).
        //
        // This used to be RunThenReEnable, which ignored the task entirely: the
        // controls came back and nothing else did, so an installer that failed —
        // or was never found, or exited 0 having wired nothing — left a card
        // that looked exactly as it does when it worked. The status line is the
        // part a person can see; hook-installer.log has the rest.
        //
        // Returns the task that finishes after the UI update, so a test can await
        // it rather than sleep.
        internal static Task ReapplyThenReport(
            Func<HookInstallResult> reapply, string name, Func<string, bool>? verify,
            TextBlock status, Control a, Control b)
        {
            status.Text = "Wiring hooks…";

            return Task.Run(() =>
            {
                HookInstallResult result;
                try { result = reapply(); }
                catch (Exception ex)
                {
                    // Not the installer's own failure paths, which return a
                    // result; this is whatever escaped them.
                    result = new HookInstallResult(HookInstallOutcome.Threw, "reapply", Error: ex.Message);
                    HookInstallerLog.Record("reapply", result);
                }

                // Checked here, off the UI thread, because it reads a file.
                bool? wired = result.Outcome == HookInstallOutcome.Ok && verify is not null
                    ? verify(name)
                    : null;
                return HookInstaller.StatusMessage(result, name, wired);
            }).ContinueWith(async done =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    status.Text = done.Result;
                    a.IsEnabled = true;
                    b.IsEnabled = true;
                });
            }).Unwrap();
        }

        internal Control GatewayTokenBox()
        {
            var host = OrbweaverSettings.OpenClawHost;
            var existing = string.IsNullOrEmpty(host) ? null : OpenClawIdentity.GatewayTokenFor(host);

            var box = new TextBox
            {
                PasswordChar = '•',
                Text = existing ?? "",
                Watermark = "paste the gateway token",
                Width = 220
            };

            box.LostFocus += (_, _) => CommitGatewayToken(box, host, existing);

            return box;
        }

        // Excluded from coverage: same shape and same reason as
        // OnGatewayHostChanged above — a real reconnect over the network. The
        // no-op path in the LostFocus handler is what a test can safely drive.
        [ExcludeFromCodeCoverage]
        internal void OnGatewayTokenChanged(string host, string value)
        {
            OpenClawIdentity.SetGatewayTokenFor(host, value);
            OpenClawSessions.Restart();
            Rebuild();
        }

        // No Restart() in either of these, unlike the two toggles below them.
        // They change which sessions the *next scan* keeps and which shape their
        // orbs join, not what the connection asked the gateway for, and dropping
        // a working socket to filter a list would take every gateway orb off the
        // screen for as long as the handshake takes. Orbs appear or vanish on the
        // next poll instead.
        //
        // ReapplyArrangement, though, because an orb already on screen changes
        // *band* the moment this is set rather than on the next poll — nothing
        // about it has to be rescanned for the app to know it belongs somewhere
        // else. Waiting for the poll would leave a heartbeat sitting in the
        // chats' heart for as long as a minute after being told to leave it,
        // which reads as the setting not working.
        internal void OnOpenClawHeartbeatModeChanged(ClusterMode mode)
        {
            OrbweaverSettings.OpenClawHeartbeatMode = mode;
            SessionManager.Instance?.ReapplyArrangement();
        }

        internal void OnOpenClawCronModeChanged(ClusterMode mode)
        {
            OrbweaverSettings.OpenClawCronMode = mode;
            SessionManager.Instance?.ReapplyArrangement();
        }

        // CB-270: the same, for an agent's own sessions.
        internal void OnOpenClawNativeModeChanged(ClusterMode mode)
        {
            OrbweaverSettings.OpenClawNativeMode = mode;
            SessionManager.Instance?.ReapplyArrangement();
        }

        // CB-270 Stage 6. Not excluded from coverage, unlike the reply switch it
        // mirrors: with no gateway address set, Restart is inert, which is how the
        // suite drives it without a socket.
        internal void OnOpenClawApproveToggled(bool enabled)
        {
            OrbweaverSettings.OpenClawApproveEnabled = enabled;

            // Reconnects, because the scopes are part of the handshake and the
            // gateway treats a changed scope set as a device to approve afresh.
            OpenClawSessions.Restart();
            Rebuild();
        }

        // Excluded from coverage: reconnects to the gateway, because the scopes
        // are part of the handshake — the setting write either side of that is one
        // line, and the switch it belongs to is covered by the rows around it.
        [ExcludeFromCodeCoverage]
        private void OnOpenClawReplyToggled(bool enabled)
        {
            OrbweaverSettings.OpenClawReplyEnabled = enabled;

            // Reconnects, because the scopes are part of the handshake and the
            // gateway treats a changed scope set as a device to approve afresh.
            // The status row then says it is waiting for that approval.
            OpenClawSessions.Restart();
            Rebuild();
        }

        // Excluded from coverage: opens or closes the gateway socket immediately —
        // the setting write either side of that is one line, and the switch it
        // belongs to is covered by the rows around it.
        [ExcludeFromCodeCoverage]
        private void OnOpenClawToggled(bool enabled)
        {
            OrbweaverSettings.OpenClawEnabled = enabled;

            // Immediately, not at the next launch: turning it off should take
            // the orbs off the screen and the socket off the network while the
            // user is still looking at the switch.
            OpenClawSessions.Restart();
            Rebuild();
        }

        // Both of the builders below used to sit inline inside a platform gate,
        // which made them reachable on exactly one CI leg — and what they build is
        // a decision about what to tell the user, not a platform detail. Pulled
        // out so it can be asserted from either runner, leaving one line of
        // genuine platform behaviour behind at each call site.

        // macOS preference windows are dismissed by the window's own close button,
        // not by a Done inside the content. Windows expects the button, so it
        // keeps it.
        internal Button DoneButton()
        {
            var done = new Button
            {
                Content = "Done",
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 90
            };

            done.Click += (_, _) => CloseFromDoneButton();
            return done;
        }

        private TextBlock? _peerLinkStatus;

        // The direct link, and the pairing that makes it possible.
        //
        // **Deliberately not symmetrical with the relay section above it.** That
        // one is a switch and a warning about cost; this one is a switch and a
        // list of machines, because the thing a person has to *do* here is pair
        // two of them, and a list with a state per row is the only honest way to
        // show a pairing that is halfway done.
        //
        // No platform gate. The relay is macOS-only because it lives in tmux;
        // this is a socket, and the whole reason for choosing SslStream over the
        // gateway's hand-rolled TLS was that it is the same on both.
        internal Control[] PeerLinkRows()
        {
            // Dropped before anything is built, for the reason the relay's is:
            // Rebuild() replaces the content tree, and a label left pointing at
            // the old one is written to once a second forever.
            _peerLinkStatus = null;

            var rows = new List<Control>
            {
                Row("Connect directly to other machines",
                    Switch(OrbweaverSettings.PeerLinkEnabled, OnPeerLinkToggled),

                    // The contrast with the row below is the point, and it is
                    // stated rather than implied: this is the same feature
                    // without the model in the middle.
                    $"Talks straight to {Brand.DisplayName} on your other machines over your local "
                    + "network. Transcripts arrive in a moment rather than in minutes, and "
                    + "nothing here signs into your Claude account or counts against your "
                    + "usage. Both machines need this switched on, and you pair them once.")
            };

            if (!OrbweaverSettings.PeerLinkEnabled) return rows.ToArray();

            _peerLinkStatus = new TextBlock
            {
                Text = PeerSessions.StatusText(),
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            };

            rows.Add(NoteRow(_peerLinkStatus));

            rows.Add(Row("Let another machine pair with this one", PairingCodeButton(),
                "Shows a code. Type it into the other machine's list below, next to this "
                + "machine's name. The code works once."));

            rows.Add(Row("Add a machine by address", AddByAddress(),
                "For a machine this one cannot see on its own — a different subnet, a VPN, or "
                + "a network that does not carry the announcements. Type its address and the "
                + "code it is showing. Its name fills itself in once it answers."));

            var listing = PeerSessions.Listing();

            foreach (var machine in listing) rows.Add(PeerRow(machine));

            if (listing.Count == 0)
            {
                rows.Add(NoteRow(new TextBlock
                {
                    // Says which of the two "nothing here" cases this is. A
                    // machine that is off and a machine that cannot be seen look
                    // identical from here, and the fixes are different.
                    Text = $"No other machines yet. They appear here on their own once {Brand.DisplayName} "
                         + "is running on them with this switched on, and both are on "
                         + "the same network.",
                    FontSize = 11,
                    Opacity = 0.55,
                    TextWrapping = TextWrapping.Wrap
                }));
            }

            return rows.ToArray();
        }

        // One machine, and whatever it is that machine currently needs.
        //
        // A paired one needs a way to be forgotten; an unpaired one needs a box
        // for the code showing on its screen. Both are on the right, where every
        // other control in this window is.
        internal Control PeerRow(PeerSessions.Listed machine)
        {
            var right = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            right.Children.Add(new TextBlock
            {
                Text = PeerSessions.RowStatus(machine),
                FontSize = 11,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (machine.Paired)
            {
                var forget = new Button { Content = "Forget" };
                forget.Click += (_, _) => OnUnpairClicked(machine.Machine);
                right.Children.Add(forget);
            }
            else
            {
                var code = new TextBox
                {
                    Width = 88,
                    MaxLength = 6,
                    Watermark = "code",
                    VerticalAlignment = VerticalAlignment.Center
                };

                var pair = new Button { Content = "Pair" };
                pair.Click += (_, _) => OnPairClicked(machine.Machine, code.Text);

                right.Children.Add(code);
                right.Children.Add(pair);
            }

            return Row(machine.Machine, right);
        }

        // An address and a code, for a machine that never announced itself.
        //
        // No name field, deliberately: the far end says what it is called in its
        // answer to the greeting, and asking a person to guess a value the
        // protocol already carries only creates a way to get it wrong.
        internal Control AddByAddress()
        {
            var where = new TextBox
            {
                Width = 150,
                Watermark = "198.51.100.10",
                VerticalAlignment = VerticalAlignment.Center
            };

            var code = new TextBox
            {
                Width = 88,
                MaxLength = 6,
                Watermark = "code",
                VerticalAlignment = VerticalAlignment.Center
            };

            var add = new Button { Content = "Add" };
            add.Click += (_, _) => OnAddByAddressClicked(where.Text, code.Text);

            var right = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            right.Children.Add(where);
            right.Children.Add(code);
            right.Children.Add(add);

            return right;
        }

        // Excluded from coverage: dials a real address. Both decisions in front
        // of it — whether the code is worth trying and whether the address
        // parses — are pure and tested.
        [ExcludeFromCodeCoverage]
        private void OnAddByAddressClicked(string? typed, string? code)
        {
            if (!PairingWorthTrying(code)) return;

            var address = PeerSessions.Address(typed, PeerLink.DefaultPort);
            if (address is null) return;

            _ = PeerSessions.PairAtAsync(address.Value.Host, address.Value.Port, code!.Trim());
        }

        // The button that opens this machine to one pairing.
        //
        // It becomes the code rather than opening a dialog for it: the code is
        // six digits read aloud or copied once, and a dialog would be a second
        // thing to dismiss for something that fits on the button.
        internal Control PairingCodeButton()
        {
            var button = new Button { Content = "Show a code" };

            button.Click += (_, _) =>
            {
                var code = PeerSessions.OpenForPairing();
                button.Content = code ?? "Not listening";
                button.IsEnabled = false;
            };

            return button;
        }

        // Excluded from coverage: opens and closes a real socket. The rows that
        // reach it are covered, and what the link does with the setting is
        // PeerSessions' own business and tested there.
        [ExcludeFromCodeCoverage]
        internal void OnPeerLinkToggled(bool on)
        {
            OrbweaverSettings.PeerLinkEnabled = on;

            // Restarted rather than left for the next launch, unlike the relay
            // switch above: a socket costs nothing to open, so making someone
            // quit the app to try the thing they just switched on would be a
            // gratuitous round trip.
            PeerSessions.Restart();

            Rebuild();
        }

        [ExcludeFromCodeCoverage]
        private void OnUnpairClicked(string machine)
        {
            PeerSessions.Unpair(machine);
            Rebuild();
        }

        // Excluded from coverage: dials a real machine. Whether the code is
        // worth dialling with is PairingWorthTrying, which is not.
        [ExcludeFromCodeCoverage]
        private void OnPairClicked(string machine, string? code)
        {
            if (!PairingWorthTrying(code)) return;

            var peer = PeerSessions.SeenFor(machine);
            if (peer is null) return;

            _ = PeerSessions.PairAsync(peer, code!.Trim());
        }

        // Whether a typed code is even worth a dial.
        //
        // Six digits, because that is what NewPairingCode makes. Checked here so
        // a half-typed code does not spend a connection and come back refused —
        // which reads as the pairing having failed rather than as not having
        // been attempted.
        internal static bool PairingWorthTrying(string? code) =>
            code is not null
            && code.Trim().Length == 6
            && code.Trim().All(char.IsAsciiDigit);

        // Every config directory a relay could sign into, each with a tick.
        //
        // A list of checkboxes rather than one picker because Remote Control is
        // account-scoped: one relay sees exactly one account's sessions, so
        // seeing two accounts means running two relays. That is a real cost
        // multiplier — each is a live Claude Code session — which is why it is
        // opt-in per account rather than "all of them, automatically".
        //
        // Not free text: a directory name that is not a real profile fails by
        // producing no sessions at all, which is indistinguishable from having
        // none, so the only offer is what actually exists.
        // Excluded from coverage: WslCard runs `wsl.exe -l -q` as a real subprocess
        // to list the installed distributions, and everything it builds depends on
        // that answer.
        [ExcludeFromCodeCoverage]
        private void AddWslCard(List<Control> cards)
        {
            var wsl = WslCard();
            if (wsl is not null) cards.Add(Card(wsl));
        }

        // Excluded from coverage: opens a web page — see
        // OnDownloadVoicesLinkClicked.
        [ExcludeFromCodeCoverage]
        private void OnDownloadVoicesLinkPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            OnDownloadVoicesLinkClicked();
        }

        internal Control[] VoiceRows()
        {
            var rows = new List<Control>();

            rows.Add(Row("High-quality voice (experimental)",
                Switch(OrbweaverSettings.NeuralVoiceEnabled, OnNeuralVoiceToggled),
                "Speaks with a neural voice (Kokoro) that runs entirely on this machine. "
                + "Downloads about 300 MB the first time and takes a few seconds "
                + "before it starts talking."));

            if (OrbweaverSettings.NeuralVoiceEnabled && _neuralModelStatus is not null)
            {
                rows.Add(Row("Speech engine", new TextBlock
                {
                    Text = _neuralModelStatus,
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                }));
            }

            // One list, every engine, each entry saying where it comes from — so
            // choosing a voice is also choosing the engine, and nothing is hidden
            // by a precedence the user can't see.
            rows.Add(Row("Speak voice", SpeakVoicePicker(),
                "Which voice the speaker button on the orb flyout uses to read the latest "
                + "assistant turn aloud. Marked (system) for the ones Windows or macOS "
                + "provides, (Kokoro) for the high-quality engine above, and (custom) for "
                + "anything your own speakCommand lists. Press the play button beside it "
                + "to hear the voice first. It previews this global voice; an orb whose "
                + "persona sets its own voice speaks that one instead."));

            rows.Add(Row("Speech volume", SpeechVolumeControl(),
                "How loud replies are read aloud — the system voices, the high-quality "
                + "voice, and orbs with voices of their own. A custom speak command is "
                + "sent the level and decides what to do with it. How much quieter a "
                + "given step sounds depends on the voice."));

            rows.Add(Row("Speaks", SpeakScopePicker(),
                "What the speaker reads. The full response is everything the assistant "
                + "said, which is what it has always done. A vibe code summary condenses "
                + "the whole response to two or three sentences — useful when a reply "
                + "would take minutes to read aloud. The summary is written by a separate "
                + "throwaway Claude Code call, so it never appears in your own "
                + "conversation, and it adds a few seconds before the speaker starts."));

            // Still shown, because the system voices are always among the choices
            // now rather than being shadowed by whatever else is installed — so a
            // voice added through Windows' own settings is genuinely usable.
            rows.Add(DownloadVoicesRow());

            rows.AddRange(new Control[]
            {
                Row("Enable voice input (experimental)",
                    Switch(OrbweaverSettings.VoiceInputEnabled, OnVoiceInputToggled),
                    "Hover an orb and click the mic that appears to dictate a prompt. Speech is "
                    + "transcribed entirely on this machine (Whisper, no cloud service) and typed "
                    + "into that session's terminal for review — nothing is sent anywhere, and "
                    + "Enter is never pressed for you.")
            });

            if (OrbweaverSettings.VoiceInputEnabled && _voiceModelStatus is not null)
            {
                rows.Add(Row("Voice model", new TextBlock
                {
                    Text = _voiceModelStatus,
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                }));
            }

            return rows.ToArray();
        }

        private Control SpeakVoicePicker()
        {
            // Every voice from every available engine in one list — system, Kokoro
            // and a user command together — with the engine shown against each
            // name. Previously this showed one engine's worth at a time, decided by
            // a precedence the user could not see, so downloading the neural engine
            // silently hid the system voices and configuring a command hid both.
            //
            // Selecting an entry records both the voice and which engine speaks it
            // (TextToSpeech.SelectVoice), so the choice is explicit rather than
            // inferred, and each engine's own key remembers what was picked there.
            //
            // TextToSpeech.AllVoiceOptions() enumerates every engine, and on
            // macOS that means `say -v ?` — a real process, launched synchronously.
            // Building this eagerly meant constructing the settings window at all
            // did that. The saved name alone (no scan) is enough for a placeholder
            // item; the real list, and the ability to change it, arrives the first
            // time the user actually opens the dropdown.
            var placeholder = SavedVoiceNameForPlaceholder() ?? "Loading voices…";

            var combo = new ComboBox
            {
                ItemsSource = new[] { placeholder },
                SelectedIndex = 0,
                MinWidth = 220
            };

            List<TextToSpeech.VoiceOption>? options = null;

            // FillVoiceList replaces the placeholder and sets the selection, which
            // raises SelectionChanged for what is the scan arriving and not a
            // choice. Only a choice should stop a live preview (a preview of the
            // voice that was showing is no longer a preview of the one that is),
            // so the scan is fenced off.
            var filling = false;

            combo.DropDownOpened += (_, _) =>
            {
                filling = true;
                try { options = FillVoiceList(combo, options); }
                finally { filling = false; }
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (!filling) VoicePreview.StopIfLive();
                ChooseVoice(combo, options);

                // Choosing a voice can change the engine, and the engine is
                // what decides what the Speech row's note has to say.
                RefreshSpeechVolumeNote();
            };

            // The button previews whatever the picker shows; null while it still
            // shows the unscanned placeholder, which VoicePreview resolves off the
            // UI thread the way a real read-aloud would.
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    combo,
                    VoicePreviewButton(
                        () => VoicePreview.PreviewTarget(options, combo.SelectedIndex), combo)
                }
            };
        }

        // What the preview button shows for each state: its tooltip, and the fill
        // the flyout's own speaker button uses for the same state (amber while the
        // engine is working towards its first sound, blue while audio plays), or
        // null for the theme's ordinary button. The same colours on purpose, so
        // "preparing" and "playing" mean the same thing wherever they appear.
        internal static (string Tip, IBrush? Fill) VoicePreviewLook(TextToSpeech.SpeakState state) =>
            state switch
            {
                TextToSpeech.SpeakState.Speaking => ("Stop", OrbFlyout.SpeakActiveFill),
                TextToSpeech.SpeakState.Preparing => ("Preparing…", OrbFlyout.SpeakPreparingFill),
                _ => ("Preview voice", null)
            };

        // Drawn geometry, not "▶"/"⏹"/"⏳" — CB-173, see PlayGlyph. White on the
        // coloured states, since the amber and blue are the same on both themes.
        private static Control FilledGlyph(string data, double width, double height) =>
            new Shapes.Path
            {
                Data = Geometry.Parse(data),
                Fill = Brushes.White,
                Width = width,
                Height = height,
                VerticalAlignment = VerticalAlignment.Center
            };

        // internal: a test drives it directly, like PreviewButton. Lives as long as
        // its row, which Rebuild() replaces wholesale, so it listens to
        // VoicePreview only while attached: a subscription made at construction
        // would leak one handler per rebuild and keep every dead button alive.
        internal Button VoicePreviewButton(
            Func<TextToSpeech.VoiceOption?> target, ComboBox picker)
        {
            var button = new Button
            {
                Padding = new Thickness(9, 5),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = picker.IsEnabled
            };

            void Apply()
            {
                var state = VoicePreview.Look;
                var (tip, fill) = VoicePreviewLook(state);

                button.Content = state switch
                {
                    TextToSpeech.SpeakState.Speaking => FilledGlyph("M 0,0 L 7,0 L 7,7 L 0,7 Z", 7, 7),
                    TextToSpeech.SpeakState.Preparing => FilledGlyph("M 0,0 L 8,0 L 4,4.5 Z M 4,4.5 L 8,9 L 0,9 Z", 8, 9),
                    _ => PlayGlyph()
                };

                if (fill is null) button.ClearValue(BackgroundProperty);
                else button.Background = fill;

                ToolTip.SetTip(button, tip);
            }

            Apply();

            button.AttachedToVisualTree += (_, _) =>
            {
                VoicePreview.Changed += Apply;
                Apply();   // it may have moved while this button did not exist
            };
            button.DetachedFromVisualTree += (_, _) => VoicePreview.Changed -= Apply;

            // FillVoiceList disables the picker when there are no voices, and a
            // preview button beside a dead picker would have nothing to say.
            picker.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsEnabledProperty) button.IsEnabled = picker.IsEnabled;
            };

            button.Click += (_, _) => _ = VoicePreview.Toggle(target());
            return button;
        }

        // CB-200's Speech slider and the note under it for the cases where
        // telling an engine the level is not the same as hearing it. Kept as
        // fields so choosing a different voice can change the note without
        // rebuilding the window.
        internal Slider? SpeechVolumeSlider { get; private set; }
        internal TextBlock? SpeechVolumeNote { get; private set; }

        internal TextBlock? SpeechVolumeReadout { get; private set; }

        internal Control SpeechVolumeControl()
        {
            SpeechVolumeReadout = VolumeReadout();
            SpeechVolumeSlider = VolumeSlider(OrbweaverSettings.SpeechVolume,
                level => OrbweaverSettings.SpeechVolume = level, SpeechVolumeReadout);

            // Under the slider rather than beside it, and wrapped: every
            // note is a sentence or two, and beside a 160px slider it
            // would squeeze the row's label into a clip on Windows — the same
            // failure the Sounds rows had.
            SpeechVolumeNote = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.7,
                MaxWidth = 240,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            RefreshSpeechVolumeNote();

            return new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Children = { WithReadout(SpeechVolumeSlider, SpeechVolumeReadout), SpeechVolumeNote }
            };
        }

        // The slider is never disabled: every engine is told the level
        // (AudioVolume.SpeechVolumeNote says what is and is not promised).
        // This decides only the note, from the saved engine and from the
        // engines orbs' own voices resolve to — CB-200's second review found
        // an orb's persona speaking through a custom command while the row,
        // reading the global engine alone, said nothing. No voice is
        // enumerated to find out, for the reason SavedVoiceNameForPlaceholder
        // gives; the orbs' engines come from the voice list only if something
        // has already built it.
        //
        // No null guard: the constructor builds every row, and VoiceRows
        // builds this note before anything that could call here can fire —
        // the picker's SelectionChanged only follows a user's choice.
        internal void RefreshSpeechVolumeNote()
        {
            // The engine that will speak, not merely the one named: a stale
            // "custom" with no command behind it speaks with a system voice.
            var engine = TextToSpeech.EngineThatWillSpeak(
                OrbweaverSettings.SpeakEngine, TextToSpeech.CustomCommandConfigured);
            var note = AudioVolume.SpeechVolumeNote(engine, NeuralSpeech.EngineIgnoresVolume,
                SessionIdentity.OrbEngines(SessionIdentity.PersonaVoiceRequests(), TextToSpeech.CachedVoiceOptions));

            SpeechVolumeNote!.Text = note;
            SpeechVolumeNote.IsVisible = note is not null;
        }

        // CB-200's Alert slider — every chime, whichever engine speaks.
        internal Slider? AlertVolumeSlider { get; private set; }
        internal TextBlock? AlertVolumeReadout { get; private set; }

        internal Control AlertVolumeControl()
        {
            AlertVolumeReadout = VolumeReadout();
            AlertVolumeSlider = VolumeSlider(OrbweaverSettings.AlertVolume,
                level => OrbweaverSettings.AlertVolume = level, AlertVolumeReadout);
            return WithReadout(AlertVolumeSlider, AlertVolumeReadout);
        }

        // The percentage beside each slider (CB-200 QA): a tooltip is a
        // number nobody sees without hovering, and "how loud is it now" is the
        // first thing anyone asks of a volume control. Fixed width, right
        // aligned, so the slider does not shift as 5% becomes 100%.
        private static TextBlock VolumeReadout() => new()
        {
            FontSize = 12,
            Width = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static Control WithReadout(Slider slider, TextBlock readout) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { slider, readout }
        };

        // Both volume sliders are the same control over AudioVolume's range,
        // with the percentage in a readout beside it and as a tooltip. The
        // other sliders in this window read their range from the class that
        // owns the rule; this one does too.
        internal static Slider VolumeSlider(double value, Action<double> write, TextBlock readout)
        {
            var slider = new Slider
            {
                Minimum = AudioVolume.Min,
                Maximum = AudioVolume.Max,
                Value = value,
                MinWidth = 160,
                SmallChange = AudioVolume.Step,
                LargeChange = 0.25,
                TickFrequency = AudioVolume.Step,
                IsSnapToTickEnabled = true
            };
            ToolTip.SetTip(slider, AudioVolume.Percent(value));
            readout.Text = AudioVolume.Percent(value);

            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                write(slider.Value);
                ToolTip.SetTip(slider, AudioVolume.Percent(slider.Value));
                readout.Text = AudioVolume.Percent(slider.Value);
            };
            return slider;
        }

        // Two named modes rather than a switch, because "off" is not what Full
        // is — both are real choices about what gets read, and a toggle labelled
        // "summarise" would put the long-standing behaviour on the unlabelled
        // side of it.
        //
        // Nothing is enumerated or spawned to build this, unlike the voice
        // picker above, so it is safe to construct eagerly and the settings
        // tests can drive it.
        internal static ComboBox SpeakScopePicker()
        {
            var combo = new ComboBox
            {
                ItemsSource = new[] { FullLabel, SummaryLabel },
                SelectedIndex = OrbweaverSettings.SpeakScope == SpeakScope.Summary ? 1 : 0,
                MinWidth = 220
            };

            combo.SelectionChanged += (_, _) =>
                OrbweaverSettings.SpeakScope =
                    combo.SelectedIndex == 1 ? SpeakScope.Summary : SpeakScope.Full;

            return combo;
        }

        internal const string FullLabel = "Full response";

        // The requester's own phrase. It reads well as a label and says what the
        // mode is for in a way "Summary" does not.
        internal const string SummaryLabel = "Vibe code summary";

        // Excluded from coverage: AllVoiceOptions() enumerates the machine's
        // installed voices, which on macOS means running `say -v ?` as a real
        // subprocess and on the neural side means asking an engine binary that may
        // not be downloaded yet. That is exactly the scan the placeholder above
        // exists to defer, and a test that opened this dropdown would perform it.
        //
        // Deferring it is also what makes the rest of the voice section testable:
        // building those rows touches nothing, which SettingsVoiceRowTests relies
        // on and asserts.
        [ExcludeFromCodeCoverage]
        private static List<TextToSpeech.VoiceOption>? FillVoiceList(
            ComboBox combo, List<TextToSpeech.VoiceOption>? options)
        {
            if (options is not null) return options;

            options = TextToSpeech.AllVoiceOptions();
            if (options.Count == 0)
            {
                combo.ItemsSource = new[] { "No voices found" };
                combo.SelectedIndex = 0;
                combo.IsEnabled = false;
                return options;
            }

            var selected = TextToSpeech.SelectedVoice();
            combo.ItemsSource = options.Select(o => o.Label).ToList();
            combo.SelectedIndex = selected is null ? 0 : Math.Max(0, options.IndexOf(selected));
            return options;
        }

        // Excluded from coverage: only reachable once FillVoiceList above has run,
        // since until then `options` is null and this returns immediately — and
        // running it means having enumerated the machine's voices. SelectVoice
        // then writes whichever engine's voice setting the choice belongs to.
        [ExcludeFromCodeCoverage]
        private static void ChooseVoice(ComboBox combo, List<TextToSpeech.VoiceOption>? options)
        {
            if (options is null) return; // still the unscanned placeholder item

            var index = combo.SelectedIndex;
            if (index < 0 || index >= options.Count) return;

            TextToSpeech.SelectVoice(options[index]);
        }

        // The raw saved voice name, read straight from settings rather than via
        // TextToSpeech.SelectedVoice() — that method calls AllVoiceOptions()
        // itself, which is exactly the scan this placeholder exists to avoid.
        internal static string? SavedVoiceNameForPlaceholder() =>
            OrbweaverSettings.SpeakEngine switch
            {
                "custom" => OrbweaverSettings.SpeakCommandVoice,
                "neural" => OrbweaverSettings.NeuralVoice,
                _ => OrbweaverSettings.SpeakVoice
            };

        // A near-copy of OnVoiceInputToggled below, and deliberately so: the
        // download-progress dance (write the setting first, seed the status row,
        // Rebuild, marshal progress onto the UI thread, guard against this window
        // having been closed) is already established there and is worth matching
        // line for line rather than reinventing.
        //
        // The one addition is InvalidateVoiceCache: the voice list is cached for the
        // process lifetime, and this toggle changes what belongs in it — turning the
        // neural engine on adds its voices, turning it off takes them away — so
        // without it the picker would keep showing the previous answer.
        // Whether flicking this switch should start a download. Pulled out so the
        // decision stays measured while the handler that acts on it does not —
        // acting means fetching ~300MB, which no test may do.
        //
        // Both halves matter: enabling with the engine already on disk must NOT
        // re-download it, and disabling must never start one.
        internal static bool ShouldStartNeuralDownload(bool enabled) =>
            enabled && !NeuralSpeech.Installed;

        // Excluded from coverage: the one path through here that a test could not
        // already reach ends in StartNeuralVoiceDownload, which fetches about
        // 300MB over the network. Everything this method decides is
        // ShouldStartNeuralDownload above, which is covered directly; what is left
        // is the setting write, the cache invalidation and the Rebuild.
        [ExcludeFromCodeCoverage]
        internal void OnNeuralVoiceToggled(bool enabled)
        {
            OrbweaverSettings.NeuralVoiceEnabled = enabled;
            TextToSpeech.InvalidateVoiceCache();

            if (!ShouldStartNeuralDownload(enabled))
            {
                Rebuild();
                return;
            }

            StartNeuralVoiceDownload();
        }

        // Excluded from coverage: starts a real ~300MB download over the
        // network (NeuralSpeech.DownloadAsync is itself already excluded for
        // exactly that reason), then chains progress and completion callbacks
        // onto the task it returns. OnNeuralVoiceToggled above — the decision
        // of *whether* to call this, which is everything a test can drive
        // without touching the network — is fully covered on its own.
        [ExcludeFromCodeCoverage]
        private void StartNeuralVoiceDownload()
        {
            _neuralModelStatus = "Downloading the speech engine and its voice model (about 300 MB)…";
            Rebuild();

            var progress = new Progress<string>(message => Dispatcher.UIThread.Post(() =>
            {
                if (_open != this) return;
                _neuralModelStatus = message;
                Rebuild();
            }));

            _ = NeuralSpeech.DownloadAsync(progress).ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_open != this) return;

                    // The voice list only becomes answerable once the engine is on
                    // disk, since it is the engine that enumerates it.
                    TextToSpeech.InvalidateVoiceCache();

                    _neuralModelStatus = t.IsFaulted
                        ? "Couldn't download the speech engine — check your connection and try again."
                        : null;
                    Rebuild();
                });
            });
        }

        internal Control DownloadVoicesRow()
        {
            var link = new TextBlock
            {
                Text = "Download more voices…",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#4A90D9")),
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(14, 2, 14, 8)
            };
            link.PointerPressed += OnDownloadVoicesLinkPressed;
            link.PointerEntered += (_, _) =>
                link.TextDecorations = TextDecorations.Underline;
            link.PointerExited += (_, _) =>
                link.TextDecorations = null;
            return link;
        }

        // Excluded from coverage: launches a real subprocess — macOS's `open`
        // against a System Settings deep link, or Windows' ms-settings: URI —
        // to hand off to the OS's own voice-download UI. Nothing here is
        // decided by data a test could vary; it is OperatingSystem.IsMacOS()
        // and OperatingSystem.IsWindows() picking one fixed command each, on
        // whichever platform is actually running.
        [ExcludeFromCodeCoverage]
        private void OnDownloadVoicesLinkClicked()
        {
            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        ArgumentList = { "x-apple.systempreferences:com.apple.Accessibility-Settings.extension?SpokenContent" },
                        UseShellExecute = false
                    })?.Dispose();
                }
                catch { }

                TextToSpeech.InvalidateVoiceCache();
            }
            else if (OperatingSystem.IsWindows())
            {
                // Windows' own Speech page, which is where "Manage voices"
                // and "Add voices" live. There was no Windows branch here at
                // all, so the link hovered, underlined and did nothing —
                // the whole of the reported bug.
                //
                // UseShellExecute must be true, unlike the macOS call above:
                // ms-settings: is a URI for the shell to resolve, not an
                // executable to launch, and CreateProcess cannot start it.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:speech",
                    UseShellExecute = true
                })?.Dispose();
            }
        }

        // The same decision for dictation's own model, and the same reason for
        // being separate: acting on it fetches ~150MB.
        internal static bool ShouldStartVoiceInputDownload(bool enabled) =>
            enabled && !SpeechTranscriber.ModelDownloaded;

        // Excluded from coverage: ends in StartVoiceInputDownload, which fetches
        // about 150MB over the network. The decision is
        // ShouldStartVoiceInputDownload above and is covered directly.
        [ExcludeFromCodeCoverage]
        internal void OnVoiceInputToggled(bool enabled)
        {
            OrbweaverSettings.VoiceInputEnabled = enabled;

            if (!ShouldStartVoiceInputDownload(enabled))
            {
                Rebuild();
                return;
            }

            StartVoiceInputDownload();
        }

        // Excluded from coverage: starts a real ~150MB download over the
        // network (SpeechTranscriber.DownloadModelAsync is itself already
        // excluded for exactly that reason). Same shape as
        // StartNeuralVoiceDownload above, and for the same reason: the decision
        // of whether to call this is what OnVoiceInputToggled covers, and that
        // is testable without ever reaching the network.
        [ExcludeFromCodeCoverage]
        private void StartVoiceInputDownload()
        {
            _voiceModelStatus = "Downloading voice model (about 150 MB)…";
            Rebuild();

            var progress = new Progress<string>(message => Dispatcher.UIThread.Post(() =>
            {
                // The window this download was kicked off from may already be
                // closed (or replaced by a fresh Toggle()) by the time a
                // progress callback lands — updating a closed window's fields
                // and rebuilding its (torn-down) content would be pointless
                // at best.
                if (_open != this) return;

                _voiceModelStatus = message;
                Rebuild();
            }));

            _ = SpeechTranscriber.DownloadModelAsync(progress).ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_open != this) return;

                    _voiceModelStatus = t.IsFaulted
                        ? "Couldn't download the voice model — check your connection and try again."
                        : null;
                    Rebuild();
                });
            });
        }

        // --- Turn sounds (CB-167) ----------------------------------------------
        // Two triggers, four settings (master switch plus a picker per
        // trigger), one shared picker builder — the same shape SpeakScope's
        // row takes above, widened because a sound setting has more than two
        // named values. SoundChoices is internal (not private) because
        // OrbWindow's context-menu submenu builds the identical list for the
        // per-orb override, and a second copy of "Default / Off / [Vibe
        // summary] / the system sounds / Choose file…" is exactly the kind of
        // drift CB-153's doubled colour switch already cost this project once.

        private const string SoundOffValue = "off";
        private const string SoundSummaryValue = "summary";

        // Never written to a setting — SoundPicker's SelectionChanged
        // intercepts this value and starts the file-open flow instead of
        // ever handing it to `set`. Null-character-prefixed so it can never
        // collide with a real system-sound name or an absolute path, both of
        // which are ordinary text.
        internal const string ChooseFileValue = "\u0000choose-file";
        private const string ChooseFileLabel = "Choose file…";
        private const string OffLabel = "Off";
        internal const string VibeSummaryLabel = "Vibe summary";

        // The seam a headless test satisfies in place of the OS file picker —
        // unlike BrowseForProfileDir above, which is excluded from coverage
        // wholesale because nothing about it can be driven headless, this is
        // a single Func a test can point at a canned "the user picked this
        // path" or "the user cancelled" (null) answer. That is what makes
        // SoundPicker's own branching — write the path, or leave the previous
        // selection alone — provable rather than merely excluded. Set and
        // cleared by the test, never by production code.
        internal static Func<Task<string?>>? ChooseSoundFileForTests;

        internal static ComboBox TurnFinishedSoundPicker() => SoundPicker(
            () => OrbweaverSettings.TurnFinishedSound,
            v => OrbweaverSettings.TurnFinishedSound = v,
            SystemSoundCatalog.DefaultFinishedSoundName,
            includeSummary: true);

        internal static ComboBox NeedsAttentionSoundPicker() => SoundPicker(
            () => OrbweaverSettings.NeedsAttentionSound,
            v => OrbweaverSettings.NeedsAttentionSound = v,
            SystemSoundCatalog.DefaultAttentionSoundName,
            includeSummary: false);

        // The full offered list for one trigger, in display order: the
        // platform default (named, not just "Default" — a user choosing
        // between two unlabelled defaults for two different triggers is the
        // usability bug this parenthetical exists to avoid), Off, the vibe
        // summary (finished only — needsAttentionSound has no such value,
        // see the settings comment on it), every system sound this machine
        // actually has, whatever `current` already is if that is none of the
        // above, and Choose file… last, matching a native picker's own
        // convention of putting "Other…" at the bottom.
        internal static List<(string Label, string? Value)> SoundChoices(
            string? current, string defaultName, bool includeSummary)
        {
            var choices = new List<(string Label, string? Value)>
            {
                ($"Default ({defaultName})", null),
                (OffLabel, SoundOffValue)
            };

            if (includeSummary) choices.Add((VibeSummaryLabel, SoundSummaryValue));

            foreach (var name in SystemSoundCatalog.List(
                SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions))
            {
                choices.Add((name, name));
            }

            // A name or path this list doesn't otherwise offer — a sound
            // removed since it was chosen, a hand-edited settings file, or a
            // file the user picked this session — shown under its own name
            // rather than silently dropped, the same courtesy ClickPicker's
            // unknown-value entry extends above.
            if (current is not null && choices.All(c => c.Value != current))
            {
                var label = Path.IsPathRooted(current) ? Path.GetFileName(current) : current;
                choices.Add((label, current));
            }

            choices.Add((ChooseFileLabel, ChooseFileValue));
            return choices;
        }

        // Static, like SpeakScopePicker: nothing it builds reads `this`, and
        // keeping it that way is what lets a test drive
        // TurnFinishedSoundPicker()/NeedsAttentionSoundPicker() directly
        // without constructing a window.
        //
        // `lastGood` — not the original `current` — is what a cancelled
        // Choose file… reverts to, because a picker driven for a while (a
        // real system sound chosen, then Choose file… opened and cancelled)
        // must land back on the sound just chosen, not jump all the way back
        // to whatever was saved when the row was built.
        //
        // `suppress` guards every place this method assigns
        // combo.SelectedIndex or combo.ItemsSource itself: either raises
        // SelectionChanged again (a plain assignment does, in Avalonia, the
        // same as ColorRow's picker.Color above), and without the guard that
        // re-entrant call would run this whole body a second time — writing
        // the setting twice and, worse, playing the preview twice for one
        // choice.
        private static ComboBox SoundPicker(
            Func<string?> get, Action<string?> set, string defaultName, bool includeSummary)
        {
            var lastGood = get();
            var choices = SoundChoices(lastGood, defaultName, includeSummary);

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(c => c.Label).ToList(),
                SelectedIndex = choices.FindIndex(c => c.Value == lastGood),
                MinWidth = 200
            };

            var suppress = false;

            // async void: an Avalonia routed event has nowhere to return a
            // Task to, the same reason BrowseInto's Click handler above is.
            // Everything this can throw is inside PickSoundFileAsync
            // (ExcludeFromCodeCoverage's whole job) or inside a test's own
            // ChooseSoundFileForTests, which is the test's responsibility.
            combo.SelectionChanged += async (_, _) =>
            {
                if (suppress) return;

                var index = combo.SelectedIndex;
                if (index < 0) return;

                var picked = choices[index];

                if (picked.Value != ChooseFileValue)
                {
                    lastGood = picked.Value;
                    set(picked.Value);
                    PreviewSound(picked.Value, defaultName);
                    return;
                }

                var path = await ChooseSoundFile(combo);

                suppress = true;
                try
                {
                    if (path is null)
                    {
                        // Cancelled: back to whatever was chosen before, not
                        // left sitting on "Choose file…" showing a value
                        // nothing wrote.
                        combo.SelectedIndex = choices.FindIndex(c => c.Value == lastGood);
                        return;
                    }

                    // Rebuilt so the picked file's own name replaces "Choose
                    // file…" as the selected entry, and is still offered
                    // under that name if the picker is reopened without the
                    // window being rebuilt from settings first.
                    lastGood = path;
                    choices = SoundChoices(path, defaultName, includeSummary);
                    combo.ItemsSource = choices.Select(c => c.Label).ToList();
                    combo.SelectedIndex = choices.FindIndex(c => c.Value == path);
                }
                finally
                {
                    suppress = false;
                }

                if (path is not null)
                {
                    set(path);
                    PreviewSound(path, defaultName);
                }
            };

            return combo;
        }

        // internal: OrbWindow's Sound submenu builds the identical
        // Default/Off/[Vibe summary]/system sounds/Choose file… list this
        // picker does (see SoundChoices' own comment), and reuses this
        // rather than opening a second file dialog with its own filter list
        // that could quietly drift from this one's.
        internal static Task<string?> ChooseSoundFile(Control owner)
        {
            var seam = ChooseSoundFileForTests;
            return seam is not null ? seam() : PickSoundFileAsync(owner);
        }

        // Excluded from coverage: opens the OS's own file picker and waits
        // for a human to answer it, the same reason BrowseForProfileDir above
        // is excluded — there is no headless storage provider to satisfy
        // OpenFilePickerAsync. ChooseSoundFile's own branching (which of
        // these two paths runs) and SoundPicker's handling of either answer
        // are both covered through ChooseSoundFileForTests instead, which is
        // the point of that seam existing rather than excluding this whole
        // path the way the folder picker is.
        //
        // Widened past DefaultExtensions on purpose: that list is what this
        // machine's own sound drawer already contains (aiff on macOS, wav on
        // Windows), but a user's own file is not limited to that — the plan's
        // decision is wav/aiff/mp3/m4a on macOS (afplay opens all four) and
        // wav only on Windows (Media.SoundPlayer opens nothing else).
        [ExcludeFromCodeCoverage]
        private static async Task<string?> PickSoundFileAsync(Control owner)
        {
            var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider;
            if (storageProvider is null) return null;

            var patterns = OperatingSystem.IsWindows()
                ? new[] { "*.wav" }
                : new[] { "*.wav", "*.aiff", "*.aif", "*.mp3", "*.m4a" };

            var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a sound",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Sounds") { Patterns = patterns } }
            });

            return result.Count == 0 ? null : result[0].TryGetLocalPath();
        }

        // How long a preview waits for the selection to settle before it
        // actually plays — see PreviewSound's own comment for why, and
        // OrbweaverSettings.SaveDelay a few hundred lines up in that file
        // for the sibling problem (a colour wheel drag) this same shape of
        // fix already solves there.
        private static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(250);

        // A DispatcherTimer rather than Task.Delay, on purpose, after an
        // earlier draft of this fix used Task.Delay(ms, CancellationToken)
        // inside a Task.Run and it produced two genuine test-case-cleanup
        // failures in unrelated suites under `dotnet test -c Release`
        // (AccountOrbsTests, SettingsWindowCoverageTests) — a stray
        // background continuation still winding down its delay after a
        // test's own teardown reached into shared Avalonia state it no
        // longer owned. OrbweaverSettings' own deferred-write timer
        // (_deferred, a few hundred lines up in that file) solves the
        // identical "debounce something that changes fast" problem the
        // same way for exactly that reason, and its own comment there is
        // explicit that no test should ever wait on a real tick — this
        // reuses that same shape, including a Flush...ForTests seam rather
        // than a sleep, instead of reinventing a worse one.
        private static DispatcherTimer? _previewTimer;
        private static string? _pendingPreviewPath;

        // Off and the vibe summary both resolve to nothing playable — Off by
        // the plan's own rule, and the vibe summary because there is no
        // static audio for it, only a per-session sentence SpeechRequest
        // writes at the moment a turn actually finishes — so both are silent
        // no-ops here rather than special-cased by the caller. Everything
        // else resolves exactly the way TurnSounds.Snapshot resolves it for
        // real playback, so a preview genuinely previews what the scan would
        // play, not an approximation of it.
        internal static void PreviewSound(string? setting, string defaultName)
        {
            if (setting is not null && (
                    string.Equals(setting, SoundOffValue, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(setting, SoundSummaryValue, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var path = SystemSoundCatalog.Resolve(
                setting ?? defaultName, SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);

            if (path is null) return;

            // QA round 2 (HIGH): ChimePlayer.Play blocks its caller on
            // WaitForExit for the length of the sound — about 2.4 s for a
            // short system chime, the full 5 s cap for a long user file —
            // and this used to call it straight from SelectionChanged and
            // the ▶ click, both on the Avalonia UI thread. Arrowing through
            // the picker froze the settings window for that long on every
            // step.
            //
            // QA round 3, finding 4: moving that call to a background
            // Task.Run on its own was not enough either — arrow-key steps
            // taken at an ordinary human pace, not just a fast burst, each
            // settle past the 250ms debounce below and each used to start
            // its own independent ChimePlayer.Play call, so several
            // deliberate choices in a row still stacked. That half of the
            // fix now lives in ChimePlayer.PlayPreview itself (a channel
            // that kills whatever preview is already playing before
            // starting the next), not here — this debounce still matters on
            // top of it, since it is what keeps a fast burst from calling
            // PlayPreview at all for the choices nobody stopped on, rather
            // than calling it and immediately killing it again.
            _pendingPreviewPath = path;
            RestartThePreviewDebounce();
        }

        private static void RestartThePreviewDebounce()
        {
            if (_previewTimer is null)
            {
                _previewTimer = new DispatcherTimer { Interval = PreviewDebounce };
                _previewTimer.Tick += OnPreviewTick;
            }

            _previewTimer.Stop();
            _previewTimer.Start();
        }

        // Excluded from coverage: fires only when the debounce interval
        // actually elapses, and no test waits on a real timer — see the
        // class comment on _previewTimer for the exact shape of flake that
        // already cost this file one broken fix. What it does when it fires
        // is PlayPendingPreview, which is covered every other way, and
        // FlushPendingPreviewForTests is the seam a test uses instead.
        [ExcludeFromCodeCoverage]
        private static void OnPreviewTick(object? sender, EventArgs e)
        {
            _previewTimer!.Stop();
            PlayPendingPreview();
        }

        // internal: a test calls this instead of waiting out the real 250ms
        // — the identical role OrbweaverSettings.FlushPendingSave plays
        // for the deferred write, and named the same way on purpose.
        internal static void FlushPendingPreviewForTests()
        {
            if (_previewTimer is null || !_previewTimer.IsEnabled) return;

            _previewTimer.Stop();
            PlayPendingPreview();
        }

        // internal: puts the debounce back in its pre-any-preview state.
        // _previewTimer is a process-wide static that is created once and
        // never reverts to null on its own, so FlushPendingPreviewForTests'
        // `_previewTimer is null` arm is otherwise unreachable from a test
        // the moment any other test anywhere in the process has triggered a
        // preview first — which, given xUnit does not guarantee run order,
        // is not a case a test can otherwise arrange to be first.
        // OrbweaverSettings' identical _deferred field has the same shape
        // and no equivalent reset; this is the local fix for it rather than
        // leaving the arm permanently unreachable.
        internal static void ResetPreviewDebounceForTests()
        {
            _previewTimer?.Stop();
            _previewTimer = null;
            _pendingPreviewPath = null;
        }

        private static void PlayPendingPreview()
        {
            var path = _pendingPreviewPath;
            _pendingPreviewPath = null;

            // PlayPreview, not Play — the whole point of round 3's fix.
            // Cheap enough to call directly rather than from its own
            // Task.Run: it only ever locks, at most kills one process
            // (fast) and either sets a field or starts its own worker the
            // first time in a while, never blocks on WaitForExit itself.
            if (path is not null) ChimePlayer.PlayPreview(path);
        }

        // A drawn triangle rather than the "▶" text glyph it stands in for —
        // CB-173 is an open bug about exactly this rendering as a colour
        // emoji on Windows for the disclosure chevron above, and a preview
        // button is the same kind of tiny, easy-to-miss-in-review icon that
        // bug slipped through as. Filled rather than stroked, since a
        // triangle this small reads better solid than outlined.
        private Control PlayGlyph() => new Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 7,4 L 0,8 Z"),
            Fill = new SolidColorBrush(IsDark ? Colors.White : Colors.Black) { Opacity = 0.75 },
            Width = 7,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 0, 0)
        };

        // internal: a test drives this directly to assert what a click
        // previews, the same reason ResetColorsButton is internal rather than
        // private. Takes the picker's own getter rather than the row's
        // `set`/`get` pair, so the button always previews whatever is
        // currently selected — including a selection not yet committed by a
        // still-running Choose file… flow, which never applies here since the
        // button ignores that entirely and just reads the setting.
        internal Control PreviewButton(Func<string?> get, string defaultName)
        {
            var button = new Button
            {
                Content = PlayGlyph(),
                Padding = new Thickness(9, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(button, "Preview");
            button.Click += (_, _) => PreviewSound(get(), defaultName);
            return button;
        }

        private Control SoundPickerRow(string label, ComboBox picker, Func<string?> get,
            string defaultName, string help) =>
            Row(label, new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { picker, PreviewButton(get, defaultName) }
            }, help);

        // internal, like VoiceRows and ClickRows above: a test drives each
        // row's control directly rather than hunting it through a template
        // whose shape depends on which theme loaded.
        // Named rather than an inline lambda, like OnVoiceInputToggled and
        // OnAutoColorToggled above it — SettingsWindowRowTests drives every
        // switch's handler directly rather than synthesizing a click on the
        // control, since which control a row holds (ToggleSwitch or its
        // CheckBox fallback) is a property of the theme rather than of the
        // setting, and this keeps that row testable the same way.
        internal void OnTurnSoundsToggled(bool enabled) => OrbweaverSettings.TurnSoundsEnabled = enabled;

        internal Control[] SoundRows() => new[]
        {
            // QA round 2 (copy): the old wording named the JSON keys this
            // switch and its two rows write — needsAttentionSound,
            // turnFinishedSound, settings.json — which means nothing to
            // someone reading the settings window rather than the source.
            // Said the same thing in plain terms instead.
            Row("Play a sound", Switch(OrbweaverSettings.TurnSoundsEnabled, OnTurnSoundsToggled),
                "Off silences both rows below, whatever sound each one is set to."),

            // QA round 2 (copy): "it never interrupts speech already
            // playing, and falls back to the chime below it if it would
            // have to" described the mechanism rather than what a user
            // would actually hear. Says the outcome instead — Dmitri's QA
            // round 2 fix is what makes this true rather than aspirational,
            // by only marking the sound "played" once something real was
            // actually about to be heard.
            SoundPickerRow("When a turn finishes", TurnFinishedSoundPicker(),
                () => OrbweaverSettings.TurnFinishedSound, SystemSoundCatalog.DefaultFinishedSoundName,
                $"Plays once a reply is done and {Brand.DisplayName} is waiting on you again. "
                + "Vibe summary speaks two or three sentences on what just happened and "
                + "what's next, in that orb's own voice — if you're already listening to "
                + "something else, it plays the chime below instead of talking over it."),

            SoundPickerRow("When a session needs you", NeedsAttentionSoundPicker(),
                () => OrbweaverSettings.NeedsAttentionSound, SystemSoundCatalog.DefaultAttentionSoundName,
                "Plays when a session is waiting on a permission prompt or a question — "
                + "the state that most needs your attention, so it always wins over a "
                + "turn finishing elsewhere on the same scan. Set an individual orb's "
                + "sound from its right-click menu; that override beats this default for "
                + "that orb alone."),

            // CB-200. Last rather than beside the master switch, so it reads
            // as applying to both sounds above it — and a preview is how a
            // level is judged, so it sits under the buttons that play one.
            Row("Alert volume", AlertVolumeControl(), AudioVolume.AlertVolumeHelp(OperatingSystem.IsWindows()))
        };

        // --- Mac-ish chrome ---------------------------------------------------
        // System Settings' shape: a small dimmed label, then a rounded card whose
        // rows are label-left / control-right and divided by hairlines that stop
        // short of the left edge. Built from brushes derived off the live theme
        // variant rather than hard-coded greys, so the window doesn't invert
        // badly when someone switches to light mode.

        private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

        // Apple's grouped cards are flat, crisp and *unbordered* — the fill
        // against the pane is the whole edge treatment, no rim and no gradient
        // sheen. A gradient plus a bright border, which is what a search for
        // "glass" produces, is visibly not what System Settings does.
        private IBrush CardBackground => new SolidColorBrush(
            IsDark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF));

        private IBrush Hairline => new SolidColorBrush(
            IsDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

        // A heading over several cards, for a section whose parts are separate
        // lists rather than one run of rows — an agent and its extra accounts,
        // say. Same heading treatment as the single-card form; the cards are
        // spaced the way two groups would be, so the break still reads as a
        // break without inventing a second heading level.
        //
        // The id is the section's stable handle — see the _sections comment
        // above Body() for why it is an id and not the heading.
        private SettingsSection Group(string id, string title, params Control[] cards)
        {
            var stack = new StackPanel { Spacing = 10 };
            foreach (var card in cards) stack.Children.Add(card);

            var section = Group(id, title, (Control)stack);

            // Past the wrapper, to the cards themselves. The single-card
            // overload can only see the stack it was handed.
            section.Cards = cards;
            return section;
        }

        private SettingsSection Group(string id, string title, Control card)
        {
            // The disclosure chevron: a drawn Path, not a font glyph. Every
            // screenshot capture runs through AssertTextIsLegible, and a
            // glyph is a font lookup — CB-173 is an open bug about exactly
            // that rendering as a colour emoji on Windows. Points right
            // closed, rotates to point down open; IsOpen above is what turns
            // this transform.
            var chevronRotation = new RotateTransform();
            var chevron = new Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 5,4 L 0,8"),
                Stroke = new SolidColorBrush(IsDark ? Colors.White : Colors.Black) { Opacity = 0.55 },
                StrokeThickness = 1.4,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 5,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = chevronRotation,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            };

            // "Theme" and "Windows" in System Settings are semibold and full
            // strength, not the dimmed 12pt caption this had. They read as
            // headings; a dimmed caption reads as a hint.
            //
            // This has to stay a real TextBlock in the logical tree, carrying
            // the heading verbatim. Three of the screenshot scenarios locate
            // their group by searching the window's descendants for a
            // TextBlock whose Text equals the heading and assert on finding
            // it. Folding the title into the ToggleButton's own Content
            // property (a string), rather than keeping it as a
            // TextBlock child the way it is here, breaks that search without
            // breaking any test.
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.9
            };

            // A ToggleButton templated down to a bare ContentPresenter —
            // App.axaml's "settings-disclosure" style, the same trick it
            // already plays on ToolTip — so this keeps focus, Tab, Space and
            // Enter, IsChecked and the automation peer without drawing the
            // themed chrome a real Button or an Expander header would bring.
            // Not Expander: the macOS theme and Fluent template one
            // differently, which would put two different headers on the two
            // rids a PR reviewer compares side by side.
            var header = new Avalonia.Controls.Primitives.ToggleButton
            {
                Classes = { "settings-disclosure" },
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                // Left inset matches the rows' own 14, because in System
                // Settings the group heading sits directly above the first
                // row's label rather than out to the left of it.
                Margin = new Thickness(14, 0, 0, 7),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { chevron, titleText }
                }
            };

            var section = new SettingsSection
            {
                Id = id,
                Title = title,
                Cards = new[] { card },
                Children = { header, card }
            };

            header.Click += (_, _) =>
            {
                // Computed off section.IsOpen — our own state — rather than
                // read back off header.IsChecked, which Avalonia has already
                // flipped once by the time Click fires. Trusting that timing
                // is exactly the kind of thing worth not trusting; this way
                // the outcome does not depend on it.
                //
                // Written before the tree is touched, on purpose. The
                // gateway TextBoxes commit on LostFocus, and both commits end
                // in Rebuild() (OnGatewayHostChanged, OnGatewayTokenChanged).
                // Folding a section that holds a focused control moves focus
                // off it as soon as the tree changes, which fires that
                // LostFocus before this handler returns — so if the setting
                // were written second, the Rebuild() it triggers would read
                // the old value and come back open while the setting already
                // says closed.
                var opening = !section.IsOpen;
                OrbweaverSettings.SetSettingsSectionCollapsed(id, !opening);
                section.IsOpen = opening;
            };

            section.WireDisclosure(header, chevronRotation, card);
            section.RestoreOpenState();

            _sections[id] = section;
            return section;
        }

        private SettingsCard Card(params Control[] rows)
        {
            var stack = new StackPanel();
            var separators = rows.Length > 1 ? new Border[rows.Length - 1] : Array.Empty<Border>();

            for (var i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    var separator = new Border
                    {
                        Height = 1,
                        Background = Hairline,
                        Margin = new Thickness(14, 0, 0, 0)
                    };
                    separators[i - 1] = separator;
                    stack.Children.Add(separator);
                }

                stack.Children.Add(rows[i]);
            }

            // 12, measured off System Settings' own groups — 18 plus a drop
            // shadow made these read as floating panels, which is a popover's
            // treatment, not a grouped row's.
            return new SettingsCard
            {
                Background = CardBackground,
                CornerRadius = new CornerRadius(12),
                ClipToBounds = true,
                Child = stack,
                Rows = rows,
                Separators = separators
            };
        }

        // A line of text on its own, full width. Card-chrome: it carries no
        // SearchText of its own and shows only when a labelled sibling in the
        // same card matched — a status line without the switch it explains
        // would read as an orphaned error message rather than as a search
        // result.
        //
        // Not Row(): that puts its control in an Auto-width column so it can sit
        // right-aligned beside a label, and a TextBlock in an Auto column is
        // never given a width to wrap inside — it just runs off the edge of the
        // window, which is what the connection status did. The help text under a
        // row wraps because it spans both columns instead, and this is that,
        // without a setting above it.
        private static Control NoteRow(Control content)
        {
            var grid = new SettingsRow { Margin = new Thickness(14, 10), IsCardChrome = true };
            grid.Children.Add(content);
            return grid;
        }

        private static Control Row(string label, Control control, string? help = null)
        {
            var grid = new SettingsRow
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions(help is null ? "Auto" : "Auto,Auto"),
                Margin = new Thickness(14, 10),
                SearchText = SettingsFilter.TextOf(label, help)
            };

            // Wrapped, with a gap before the control: the label sits in the
            // star column, so a wide control takes its width. Unwrapped, a
            // label longer than what was left was cut off mid-word and ran
            // under the control — "When a session need" beside Windows'
            // wider combo box (CB-200 review). Wrapping only happens when the
            // line would not fit anyway, so a row that fit before is unchanged.
            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(text);

            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);

            if (help is not null)
            {
                var hint = HelpText(help);
                Grid.SetRow(hint, 1);
                Grid.SetColumnSpan(hint, 2);
                grid.Children.Add(hint);
            }

            return grid;
        }

        // A row's help, one wrapped TextBlock per paragraph rather than one
        // TextBlock holding a blank line.
        //
        // Not cosmetic. Avalonia 12.1.1's TextFormatter never advances past an
        // empty line in wrapped text laid out with the headless platform's
        // stub font (BareMinimum.ttf): "a\n\nb" under TextWrapping.Wrap
        // produces zero-length lines until memory runs out, while NoWrap, a
        // single "\n", and the same text under real Skia all terminate. Under
        // tests/UiTests that hung any test that showed this window after
        // something had left Grok usage switched on, because the Grok
        // auto-refresh help was the one help text with a paragraph break in
        // it. Installed builds lay out with Skia and never hit it, so this
        // keeps a wrapped TextBlock from ever being handed that input.
        // SettingsHelpTextTests pins both halves.
        //
        // A help text with no paragraph break is the single TextBlock it
        // always was, so every other row's tree is exactly what it was before.
        internal static Control HelpText(string help)
        {
            var paragraphs = help.Split("\n\n");
            var margin = new Thickness(0, 6, 0, 0);
            if (paragraphs.Length == 1) return HintBlock(help, margin);

            var panel = new StackPanel { Spacing = 6, Margin = margin };
            foreach (var paragraph in paragraphs) panel.Children.Add(HintBlock(paragraph, default));
            return panel;
        }

        private static TextBlock HintBlock(string text, Thickness margin) => new()
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin
        };

        // One row per state, seeded from the stored colour and written on change
        // with no commit step — the same read-seed-then-write shape as
        // LifetimePicker below.
        // internal: the arming guard below is the fix for a bug that wrote three
        // colours nobody chose into settings.json, and it is decided entirely in
        // managed code — no OS call, no window needed.
        internal Control ColorRow(string label, string state) =>
            ColorRow(label, () => OrbColors.For(state), color =>
            {
                OrbColors.Set(state, OrbColors.ToHex(color));

                // Nothing observes the settings store, and a colour change isn't a
                // state change, so the orbs and the tray icon have to be told.
                SessionManager.Instance?.ReapplyStateColors();
            });

        // CB-268 generalised the row over a getter and a setter, because the
        // group outline's stroke and fill are colours too and want exactly this
        // control, arming guard and all. `set` is only ever called for a change
        // the user made and that differs from `get()` in RGB; everything below
        // is what ColorRow(label, state) always did, moved rather than rewritten.
        internal Control ColorRow(string label, Func<Color> get, Action<Color> set, string? help = null) =>
            Row(label, ArmedColorPicker(get, set), help);

        internal static ColorPicker ArmedColorPicker(Func<Color> get, Action<Color> set)
        {
            var picker = new ColorPicker
            {
                Color = get(),

                // The orb builds its own alphas — the glow's gradient stops are
                // 150/95/0 over the chosen RGB, and the tray icon's alpha channel
                // is the shape of its ring — so a user-set alpha would either be
                // thrown away silently or make the orb look broken. Hidden *and*
                // disabled, so the control never shows a value we won't honour.
                IsAlphaVisible = false,
                IsAlphaEnabled = false
            };

            // No Width or Height here: the picker's metrics are the theme's
            // business, the same as the combo box's — see the note above Body().

            // ColorChanged is not trustworthy until the user has touched the
            // control, and this is not theoretical: seeding Color and subscribing
            // afterwards is not enough, because the macOS theme's template raises
            // ColorChanged *after* that with a colour of its own — a palette entry,
            // by the look of the values. It wrote #2C273C / #50D140 / #E82323 into
            // settings.json on the first launch that ever opened this window, so
            // three colours nobody chose became the user's colours, the swatches
            // re-seeded from them on the next build, and nothing anywhere looked
            // like an error.
            //
            // Comparing against the stored value can't catch that on its own: a
            // spurious change is a genuine difference. What distinguishes a real
            // edit is that a real one is preceded by a click or a focus — you
            // cannot pick a colour without opening the drop down first. So arm on
            // that, and treat everything before it as the template talking to
            // itself.
            var armed = false;

            // Tunnelling, so it arrives before the template's own button handles
            // it and marks it handled. GotFocus covers tabbing in without a click.
            picker.AddHandler(
                PointerPressedEvent,
                (object? _, PointerPressedEventArgs _) => armed = true,
                RoutingStrategies.Tunnel);
            picker.GotFocus += (_, _) => armed = true;

            picker.ColorChanged += (_, e) =>
            {
                var current = get();
                var same = e.NewColor.R == current.R
                           && e.NewColor.G == current.G
                           && e.NewColor.B == current.B;

                if (!armed)
                {
                    // Put ours back rather than just declining to save it,
                    // otherwise the swatch sits there showing a colour the app is
                    // not using. Self-correcting and terminating: the assignment
                    // raises this again, and that pass is a no-op.
                    if (!same) picker.Color = current;
                    return;
                }

                // A real edit that changes nothing still must not write. Writing
                // the current colour as an explicit hex would freeze today's
                // default into the file and light up the Reset button for a colour
                // nobody chose. Compare RGB only — alpha isn't ours (see above).
                if (same) return;

                set(e.NewColor);
            };

            return picker;
        }

        // One button rather than a reset per row: the rows are narrow already, and
        // "put it back how it shipped" is a single intention.
        //
        // It writes null rather than today's default hex — see
        // OrbweaverSettings.IdleColor for why that distinction matters — and then
        // rebuilds instead of assigning each picker's Color back, because
        // assigning Color raises ColorChanged, which would write the default hex
        // straight into the file that was just cleared. Rebuilding re-seeds every
        // control from the store, which this window already does on a theme
        // change, and there's no uncommitted state to lose. It does reset the
        // scroll position, which for a window this short isn't worth solving.
        // internal so a test can raise its Click without walking the visual
        // tree to find it.
        internal Control ResetColorsButton()
        {
            var reset = new Button
            {
                Content = "Reset",
                IsEnabled = !OrbColors.AllDefault
            };

            reset.Click += (_, _) =>
            {
                OrbColors.Set("idle", null);
                OrbColors.Set("generating", null);
                OrbColors.Set("waiting", null);
                SessionManager.Instance?.ReapplyStateColors();
                Rebuild();
            };

            return reset;
        }

        // A bare switch: Avalonia's default writes "On"/"Off" beside it, which no
        // Mac control does. Falls back to a checkbox if there is no usable switch
        // template — see BorrowFluentToggleSwitch — because a settings row with a
        // working checkbox beats one that crashes the app.
        private Control Switch(bool value, Action<bool> onChange)
        {
            if (!HasSwitchTheme) return Check(value, onChange);

            var toggle = new ToggleSwitch
            {
                IsChecked = value,
                OnContent = null,
                OffContent = null
            };
            toggle.IsCheckedChanged += (_, _) => onChange(toggle.IsChecked ?? false);
            return toggle;
        }

        // Minutes, with 0 for forever. Coarse on purpose: the useful answers are
        // "a few minutes", "the rest of the afternoon" and "never", and a spinner
        // asking for a number would invite precision that doesn't mean anything
        // when the input is a hook that fires every couple of seconds.
        private static readonly (string Label, int Minutes)[] LifetimeChoices =
        {
            ("1 minute", 1),
            ("5 minutes", 5),
            ("15 minutes", 15),
            ("30 minutes", 30),
            ("1 hour", 60),
            ("4 hours", 240),
            ("Forever", OrbweaverSettings.OrbLifetimeForever)
        };

        internal Control LifetimePicker()
        {
            var current = OrbweaverSettings.OrbLifetimeMinutes;
            var choices = LifetimeChoices.ToList();

            // A number hand-written into settings.json shows as itself instead of
            // being silently rounded to whatever is on the list — opening this
            // window shouldn't quietly change a setting.
            if (choices.All(choice => choice.Minutes != current))
            {
                choices.Insert(choices.Count - 1, ($"{current} minutes", current));
            }

            var combo = new ComboBox
            {
                ItemsSource = choices.Select(choice => choice.Label).ToList(),
                SelectedIndex = choices.FindIndex(choice => choice.Minutes == current),
                MinWidth = 132
            };
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index < 0) return;

                OrbweaverSettings.OrbLifetimeMinutes = choices[index].Minutes;
            };
            return combo;
        }

        internal Control ProfilesCard()
        {
            var snapshot = ClaudeDesktopManager.Snapshot;

            if (snapshot.Profiles.Count == 0)
            {
                return Card(new TextBlock
                {
                    Text = "No profiles found. Create one from the menu bar.",
                    FontSize = 13,
                    Opacity = 0.6,
                    Margin = new Thickness(14, 12)
                });
            }

            var rows = new List<Control> { ColumnLabels() };
            rows.AddRange(snapshot.Profiles.Select(Row));

            // Card-chrome, same as the column headings above: it explains the
            // profile rows rather than being one, so it shows only when at
            // least one of them does.
            rows.Add(NoteRow(new TextBlock
            {
                Text = "Colour applies to the menu swatch, the Dock icon and the window tint. "
                       + "Leave a name empty to use the folder name.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.55,
                FontSize = 11
            }));

            return Card(rows.ToArray());
        }

        // Card-chrome: these headings have no text of their own to search and
        // exist only to explain the profile rows beneath them, so they follow
        // whichever of those rows the filter matched.
        internal static Control ColumnLabels()
        {
            var grid = RowGrid();
            grid.IsCardChrome = true;
            Add(grid, 0, Label("Name"));
            Add(grid, 1, Label("Colour"));
            Add(grid, 2, Label("Swatch"));
            Add(grid, 3, Label("Dock"));
            Add(grid, 4, Label("Tint"));
            return grid;

            static TextBlock Label(string text) => new()
            {
                Text = text,
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        internal static SettingsRow RowGrid() => new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,130,64,54,44,84"),
            Margin = new Thickness(14, 8)
        };

        internal static void Add(Grid grid, int column, Control child)
        {
            Grid.SetColumn(child, column);
            grid.Children.Add(child);
        }

        // What a typed profile name becomes on disk. Blank means NULL rather than
        // an empty string, so clearing the box restores the folder-derived name
        // instead of leaving the profile with no name at all.
        internal static string? ChosenProfileName(string? typed)
        {
            var trimmed = typed?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        // Excluded from coverage: KickRefresh rescans every profile directory on a
        // background task, and does so whether or not this attribute is here.
        // ChosenProfileName above is the decision and is covered.
        [ExcludeFromCodeCoverage]
        private static void ApplyProfileName(string folder, string? typed)
        {
            OrbweaverSettings.Update(folder, entry => entry.Name = ChosenProfileName(typed));
            ClaudeDesktopManager.KickRefresh();
        }

        // Excluded from coverage: reaches ApplyProfileColour, which rebuilds a
        // tinted clone of Claude.app.
        //
        // A named handler rather than a lambda, and the picker carries what it
        // needs on its Tag, because a lambda inside an excluded method is hoisted
        // to a method of its own and does not inherit the attribute — so as a
        // lambda this line was counted while everything around it was not.
        [ExcludeFromCodeCoverage]
        private static void OnProfileColourChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo) return;
            if (combo.Tag is not ValueTuple<string, List<string>> tag) return;

            ApplyProfileColour(tag.Item1, tag.Item2, combo.SelectedIndex);
        }

        // Which stored colour a picker index means. Index 0 is "auto", which maps
        // to a NULL stored colour rather than to a colour named "auto" — that is
        // what lets a profile go back to its name-derived colour, and without it a
        // colour is a one-way door, including one set by a stray keystroke.
        //
        // Pure and separate from the handler because the handler cannot run here:
        // see ApplyProfileColour below.
        internal static string? ChosenProfileColour(IReadOnlyList<string> options, int index) =>
            index <= 0 ? null : options[index];

        // Excluded from coverage: RecolourDockIcon rebuilds a tinted clone of
        // Claude.app — scanning live processes first to decide whether it is safe
        // to delete the bundle out from under a running instance — and KickRefresh
        // rescans every profile directory on a background task. Both do that work
        // whether or not this attribute is here, which is precisely why no test may
        // call this method: an exclusion stops a line being counted, not being run.
        //
        // The decision it makes is ChosenProfileColour above, which is covered.
        [ExcludeFromCodeCoverage]
        private static void ApplyProfileColour(
            string folder, IReadOnlyList<string> options, int index)
        {
            if (index < 0) return;

            OrbweaverSettings.SetProfileColor(folder, ChosenProfileColour(options, index));

            // The Dock icon was tinted when its clone was built, so it needs
            // regenerating; the swatch and window tint just re-read the colour.
            ClaudeDesktopManager.RecolourDockIcon(folder);
            ClaudeDesktopManager.KickRefresh();
        }

        // Excluded from coverage: same KickRefresh, same reason.
        [ExcludeFromCodeCoverage]
        private static void ApplyProfileSwatch(string folder, bool value)
        {
            OrbweaverSettings.SetProfileShowSwatch(folder, value);
            ClaudeDesktopManager.KickRefresh();
        }

        internal Control Row(ProfileView profile)
        {
            var folder = Path.GetFileName(profile.Directory);
            var settings = OrbweaverSettings.For(folder);
            var grid = RowGrid();
            grid.SearchText = SettingsFilter.TextOf(profile.DisplayName, folder);

            var name = new TextBox
            {
                Text = settings.Name ?? "",
                Watermark = profile.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            // On every keystroke rather than on commit: there is no OK button to
            // commit at, and the tray picks it up on its next rebuild.
            name.TextChanged += (_, _) => ApplyProfileName(folder, name.Text);
            Add(grid, 0, name);

            // "auto" first, mapping to a null stored colour, so a profile can go
            // back to its name-derived colour. Without it a colour is a one-way
            // door — including one set by a stray keystroke.
            var options = new List<string> { AutoColour };
            options.AddRange(ClaudeDesktopColors.Names);

            var stored = settings.Color;
            var selected = 0;
            if (stored is { Length: > 0 })
            {
                var found = options.FindIndex(o =>
                    string.Equals(o, stored, StringComparison.OrdinalIgnoreCase));
                if (found > 0) selected = found;
            }

            var colour = new ComboBox
            {
                ItemsSource = options
                    .Select(name => name == AutoColour
                        ? SwatchItem(AutoColour, ClaudeDesktopColors.For(folder, profile.IsDefault))
                        : SwatchItem(name, ClaudeDesktopColors.ByName(name)))
                    .ToList(),
                SelectedIndex = selected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            colour.Tag = (folder, options);
            colour.SelectionChanged += OnProfileColourChanged;
            Add(grid, 1, colour);

            Add(grid, 2, Check(settings.ShowSwatch, value => ApplyProfileSwatch(folder, value)));

            Add(grid, 3, Check(settings.TintDockIcon, value =>
                OrbweaverSettings.Update(folder, entry => entry.TintDockIcon = value)));

            Add(grid, 4, Check(settings.TintWindow, value =>
                OrbweaverSettings.Update(folder, entry => entry.TintWindow = value)));

            Add(grid, 5, DeleteProfileButton(profile));

            return grid;
        }

        // Removing a profile, which means removing a Claude Desktop login, its
        // chat history and its local databases. There was no way to do it at
        // all before this — "Reveal profiles folder" and a trip to Finder was
        // the whole story — which is a gap rather than a safeguard, because
        // deleting the folder by hand leaves the cloned Dock icon and the saved
        // name and colour behind.
        //
        // Two clicks, not a modal. The second click is the confirmation, the
        // button says what it is about to do while it waits, and it gives up
        // after a few seconds so a stray click cannot arm it and leave it
        // armed. A dialog would be the heavier answer, and the thing this
        // guards is already recoverable — it goes to the Trash.
        // Excluded from coverage: DeleteProfile moves a real profile directory to
        // the Trash — the user's actual Claude Desktop data — after checking
        // whether anything is running out of it. There is no arrangement of this
        // suite where calling it is acceptable, and an exclusion does not make it
        // so: it stops the line being counted, not being run.
        //
        // What the button says before that point IS covered: the two-step arm
        // ("Delete" then "Trash it?"), the six-second disarm, and the refusal
        // wording. Only the three outcomes of an actual delete are not, and the
        // most interesting of them is worth reading anyway — "Trashed" is set on
        // the button AND a rebuild is kicked, because the rescan the delete
        // triggers is asynchronous, so the row may still be in the snapshot that
        // rebuild reads. A row that stays put with nothing said reads as a click
        // that did nothing; whichever lands first, the user is told.
        [ExcludeFromCodeCoverage]
        private void DeleteAndReport(ProfileView profile, Button button)
        {
            switch (ClaudeDesktopManager.DeleteProfile(profile))
            {
                case ClaudeDesktopManager.DeleteOutcome.Deleted:
                    button.Content = "Trashed";
                    Rebuild();
                    break;

                case ClaudeDesktopManager.DeleteOutcome.RefusedRunning:
                    button.Content = "Quit it first";
                    button.IsEnabled = true;
                    break;

                default:
                    button.Content = "Couldn't";
                    button.IsEnabled = true;
                    break;
            }
        }

        internal Control DeleteProfileButton(ProfileView profile)
        {
            // The default profile is Claude Desktop's own directory rather than
            // one this app made, so there is nothing here to offer.
            if (profile.IsDefault) return new Panel();

            var button = new Button
            {
                Content = "Delete",
                FontSize = 11,
                Padding = new Thickness(8, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var armed = false;
            DispatcherTimer? disarm = null;

            void Disarm()
            {
                disarm?.Stop();
                disarm = null;
                armed = false;
                button.Content = "Delete";
            }

            button.Click += (_, _) =>
            {
                if (!armed)
                {
                    // Say what will happen, in the place the click will happen.
                    armed = true;
                    button.Content = "Trash it?";

                    disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    disarm.Tick += (_, _) => Disarm();
                    disarm.Start();
                    return;
                }

                Disarm();
                button.IsEnabled = false;
                DeleteAndReport(profile, button);
            };

            return button;
        }

        private const string AutoColour = "auto";

        internal static Control SwatchItem(string colourName, Color color)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Shapes.Ellipse
                    {
                        Width = 11,
                        Height = 11,
                        Fill = new SolidColorBrush(color),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock { Text = colourName, VerticalAlignment = VerticalAlignment.Center }
                }
            };
        }

        internal static CheckBox Check(bool value, Action<bool> onChange)
        {
            var box = new CheckBox
            {
                IsChecked = value,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            box.IsCheckedChanged += (_, _) => onChange(box.IsChecked ?? false);
            return box;
        }

        // ---- extra CLI account directories ----------------------------------

        // Distinct from "Profiles" above (Claude Desktop, the Electron app) —
        // these are *CLI* config directory names, for a second (or third...)
        // account: CLAUDE_CONFIG_DIR for Claude Code, CODEX_HOME for Codex,
        // e.g. an alias like `alias kwork="CLAUDE_CONFIG_DIR=~/.claude-work claude"`.
        // Each one is wired in *addition* to the default, never as a
        // replacement, and never auto-discovered: only names added here (or
        // passed explicitly to an installer's --profile-dir) are ever touched.
        //
        // One card, used twice. The two lists are separate settings because
        // they are separate products and someone can easily have extras of one
        // and not the other, but nothing about the *UI* differs between them,
        // and two near-identical copies would have drifted the way the
        // platforms did.
        internal static Control ProfileDirsCard(
            string blurb,
            string watermark,
            Func<IReadOnlyList<string>> current,
            Action<string> add,
            Action<string> remove,
            Func<HookInstallResult> reapply,
            Func<string, bool>? verify = null)
        {
            var content = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };

            content.Children.Add(new TextBlock
            {
                Text = blurb,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.55,
                FontSize = 11
            });

            var itemsPanel = new StackPanel { Spacing = 4 };
            foreach (var dirName in current())
            {
                itemsPanel.Children.Add(ProfileDirRow(dirName, itemsPanel, remove));
            }
            content.Children.Add(itemsPanel);

            var input = new TextBox { Watermark = watermark, Width = 220 };
            var browseButton = new Button { Content = "Browse…" };
            var addButton = new Button { Content = "Add" };
            var status = new TextBlock { FontSize = 11, Opacity = 0.7 };

            // A folder picker is the more discoverable way to do this, but
            // typing stays available too: these variables can point at a
            // directory that doesn't exist yet (the CLI creates it on first use
            // with that alias), which a picker — browsing existing folders only
            // — can't select.
            browseButton.Click += async (_, _) => await BrowseInto(browseButton, status, input);

            addButton.Click += (_, _) =>
            {
                var name = input.Text?.Trim();
                if (string.IsNullOrEmpty(name)) return;

                status.Text = "";
                add(name);
                itemsPanel.Children.Add(ProfileDirRow(name, itemsPanel, remove));
                input.Text = "";

                // Off the UI thread: this shells out to an installer, and on
                // Windows that means a re-run for every already-wired WSL
                // distro. The window must stay responsive the whole time; the
                // installers' own timeouts guarantee it eventually returns
                // either way.
                addButton.IsEnabled = false;
                input.IsEnabled = false;
                _ = ReapplyThenReport(reapply, name, verify, status, addButton, input);
            };

            content.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { input, browseButton, addButton }
            });
            content.Children.Add(status);

            content.Children.Add(new TextBlock
            {
                Text = "Removing a profile stops it from being wired on future changes; it doesn't "
                       + "remove hooks already written to that profile's own config.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.5,
                FontSize = 11
            });

            return content;
        }

        // Returns the picked folder's bare name (e.g. ".claude-work"), or
        // Whether a picked folder is a direct child of one of the home directories
        // a profile may live under.
        //
        // The rule matters more than it looks. A profile directory that is not a
        // direct child of $HOME resolves to the wrong place on native Windows, in
        // WSL and on macOS alike — the same rule the macOS installers apply when
        // they refuse a profile name containing a slash. And a profile can be
        // WSL-only with no Windows-side counterpart at all, so a folder picked
        // from a \\wsl.localhost\<distro>\home\<user>\ path has to validate the
        // same way a native one does rather than being rejected for not sitting
        // under C:\Users\.
        //
        // Pure, and separate from the picker that calls it, because the picker
        // opens an OS dialog and this is the part with a decision in it.
        internal static bool IsDirectChildOfAHome(
            string pickedPath, string home, IEnumerable<string> wslHomes)
        {
            static string Trim(string path) =>
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var validHomes = new[] { home }.Concat(wslHomes).Select(Trim).ToList();
            var parent = Path.GetDirectoryName(Trim(pickedPath));

            return validHomes.Any(h => string.Equals(parent, h, StringComparison.OrdinalIgnoreCase));
        }

        // Excluded from coverage: everything past the first line is the OS folder
        // picker — see BrowseForProfileDir below.
        [ExcludeFromCodeCoverage]
        private static async Task BrowseInto(Control owner, TextBlock status, TextBox input)
        {
            var picked = await BrowseForProfileDir(owner, status);
            if (picked is not null) input.Text = picked;
        }

        // null if the user cancelled or picked something invalid — in which
        // case `status` is set to say why, since a folder outside the home
        // directory would resolve to the wrong place everywhere: native
        // Windows, WSL, and macOS alike (see ProfileDirsCard's doc comment).
        //
        // No longer Windows-only. GetWslHomeUncPaths already returns an empty
        // list off Windows, so the validation below reduces to "a direct child
        // of $HOME", which is exactly the rule the macOS installers enforce
        // when they refuse a profile name containing a slash.
        // Excluded from coverage: opens the OS's own folder picker and waits for a
        // human to answer it. There is no headless storage provider to satisfy
        // OpenFolderPickerAsync, and a test that got one would sit on a modal
        // dialog until CI timed out.
        //
        // The rule it enforces afterwards — a profile directory must be a direct
        // child of $HOME, because anything else resolves to the wrong place on
        // native Windows, WSL and macOS alike — is the part worth having, and it
        // is the same rule the macOS installers apply when they refuse a profile
        // name containing a slash. WslIntegration's half of it is pure and covered
        // in tests/UnitTests/WslIntegrationRulesTests.
        [ExcludeFromCodeCoverage]
        private static async Task<string?> BrowseForProfileDir(Control owner, TextBlock status)
        {
            var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider;
            if (storageProvider is null) return null;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var startLocation = await storageProvider.TryGetFolderFromPathAsync(home);

            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select a config directory",
                SuggestedStartLocation = startLocation,
                AllowMultiple = false
            });

            if (result.Count == 0) return null;

            var pickedPath = result[0].TryGetLocalPath();
            if (pickedPath is null)
            {
                status.Text = "Couldn't resolve that selection to a local folder.";
                return null;
            }

            // Must be a *direct* child of a recognized home, not just nested
            // somewhere under it — the underlying model (-ProfileDir/
            // -WslProfileDir) only ever takes a single path segment relative
            // to home, so picking e.g. ~/work/claude-work would silently
            // keep only "claude-work" and resolve to the wrong, nonexistent
            // ~/claude-work instead. "A recognized home" is deliberately not
            // just the Windows one: the same dir name gets wired under every
            // WSL distro's home too (see the section's own doc comment), and
            // a profile can be WSL-only with no Windows-side counterpart at
            // all — e.g. a second Linux-only account — so a folder picked
            // from \\wsl.localhost\<distro>\home\<user>\ must validate the
            // same way a native one does, not be rejected just because it
            // isn't under C:\Users\....
            if (!IsDirectChildOfAHome(pickedPath, home, WslIntegration.GetWslHomeUncPaths()))
            {
                status.Text = "Must be a folder directly inside your home directory (" + home
                    + ") or a WSL distro's home directory, not a nested subfolder.";
                return null;
            }

            status.Text = "";
            return Path.GetFileName(
                pickedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        [SupportedOSPlatform("windows")]
        internal static Control ProfileDirRow(
            string dirName, StackPanel itemsPanel, Action<string> remove)
        {
            var label = new TextBlock
            {
                Text = dirName,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 220
            };
            var removeButton = new Button { Content = "Remove" };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { label, removeButton }
            };

            removeButton.Click += (_, _) =>
            {
                remove(dirName);
                itemsPanel.Children.Remove(row);
            };

            return row;
        }

        // Null when there's nothing to show (no WSL, or no distros besides
        // Docker Desktop's plumbing ones) — Body() omits the whole group in
        // that case rather than showing an empty card.
        [SupportedOSPlatform("windows")]
        // Excluded from coverage: begins by running `wsl.exe -l -q` as a real
        // subprocess to list the installed distributions, and everything it builds
        // depends on that answer. WslIntegration's own rules — which settings file
        // to write, how to read a home directory out of /etc/passwd — are pure and
        // covered in tests/UnitTests/WslIntegrationRulesTests.
        [ExcludeFromCodeCoverage]
        private static Control? WslCard()
        {
            var distros = WslIntegration.ListDistros();
            if (distros.Count == 0) return null;

            var content = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };
            content.Children.Add(new TextBlock
            {
                Text = $"Wire or unwire {Brand.DisplayName}'s hooks for Claude Code running inside each WSL distro.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.55,
                FontSize = 11
            });

            foreach (var distro in distros) content.Children.Add(WslDistroRow(distro));

            return content;
        }

        // Excluded from coverage: Windows-only, and every line of it runs
        // wsl.exe. IsWired reads a settings file inside the distribution;
        // SetWired writes one by running a script in there, with its own ~10s
        // timeout. The re-entrancy guard and the revert-on-failure are real
        // decisions, but neither can be exercised without that script actually
        // running — and WslIntegration's own rules, which settings file to write
        // and how to find a home directory in /etc/passwd, are pure and covered
        // in tests/UnitTests/WslIntegrationRulesTests.
        [ExcludeFromCodeCoverage]
        [SupportedOSPlatform("windows")]
        private static Control WslDistroRow(string distro)
        {
            var box = new CheckBox { Content = distro, IsChecked = WslIntegration.IsWired(distro) };

            box.IsCheckedChanged += (_, _) =>
            {
                var desired = box.IsChecked ?? false;
                // Prevent a re-entrant click while the script from the last
                // one is still running — SetWired has its own ~10s timeout,
                // so this can't disable the box forever even on failure.
                box.IsEnabled = false;

                Task.Run(() => WslIntegration.SetWired(distro, desired)).ContinueWith(task =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        box.IsEnabled = true;
                        // Revert on failure rather than show a checked state
                        // that doesn't match settings.json's real contents.
                        if (!task.Result) box.IsChecked = !desired;
                    });
                });
            };

            return box;
        }
    }

    // Switching activation policy so a menu-bar-only app can own a focusable
    // window, then switching back.
    //
    // Excluded from coverage as a whole class: every method here is P/Invoke
    // into libobjc, calling into the real Objective-C runtime to change this
    // process's actual NSApplication activation policy — exactly the kind of
    // call a headless CI runner has no business making. It is already only
    // reachable in production via the already-excluded SettingsWindow.Toggle().
    [ExcludeFromCodeCoverage]
    internal static class MacOSActivation
    {
        private const string Objc = "/usr/lib/libobjc.A.dylib";

        private const long Regular = 0;    // NSApplicationActivationPolicyRegular
        private const long Accessory = 1;  // NSApplicationActivationPolicyAccessory

        [DllImport(Objc)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Objc)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern IntPtr msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool msgSend_policy(IntPtr receiver, IntPtr selector, long policy);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern void msgSend_activate(IntPtr receiver, IntPtr selector,
            [MarshalAs(UnmanagedType.U1)] bool ignoringOtherApps);

        public static void SetRegular()
        {
            // Apply() no-ops on non-macOS, but SharedApplication() below goes
            // straight to a P/Invoke against libobjc with no such guard — on
            // Windows that's a DllNotFoundException with nothing upstream to
            // catch it, which took the whole app down the first time Settings
            // was opened on a real Windows box.
            if (!OperatingSystem.IsMacOS()) return;

            Apply(Regular);

            // Regular alone doesn't bring us forward; without this the window
            // opens behind whatever you were using.
            var app = SharedApplication();
            if (app != IntPtr.Zero)
            {
                msgSend_activate(app, sel_registerName("activateIgnoringOtherApps:"), true);
            }
        }

        public static void SetAccessory() => Apply(Accessory);

        private static void Apply(long policy)
        {
            if (!OperatingSystem.IsMacOS()) return;

            try
            {
                var app = SharedApplication();
                if (app == IntPtr.Zero) return;

                msgSend_policy(app, sel_registerName("setActivationPolicy:"), policy);
            }
            catch
            {
                // Worst case the window opens without focus; not fatal.
            }
        }

        private static IntPtr SharedApplication()
        {
            var cls = objc_getClass("NSApplication");
            return cls == IntPtr.Zero ? IntPtr.Zero : msgSend(cls, sel_registerName("sharedApplication"));
        }
    }
}
