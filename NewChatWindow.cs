using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Orbweaver
{
    // CB-168's "start a new chat" dialog: pick a CLI, pick a folder, Start.
    //
    // Follows SettingsWindow's singleton-window pattern almost exactly —
    // Toggle()/Activate(), MacOSActivation.SetRegular() on open and
    // SetAccessory() on close — see that file's own header for why the
    // activation dance exists at all. The one difference is the optional
    // prefill: an orb's "New chat here" context item opens this already
    // pointed at that orb's cwd and CLI, which SettingsWindow never needs.
    internal sealed class NewChatWindow : Window
    {
        private static NewChatWindow? _open;

        // Opens the dialog, or brings the one already open forward — never a
        // second one. Despite the name it never closes anything: the tray
        // item, an orb's "New chat here" and the global hotkey all mean "take
        // me to New chat", and a second press of the hotkey closing the window
        // it had just opened would be the opposite of that.
        //
        // The decision is covered; the two OS-facing halves are not. They are
        // real OS work (MacOSActivation, Show, Activate) with no decision in
        // them, and Show() is the one thing a headless suite must not do here
        // (see NewChatWindowTests' own header on why Close() specifically is
        // dangerous, and HotkeyActionsTests on Show). PresentForTests and
        // BringForwardForTests stand in for them, which is what lets the
        // open-versus-focus rule be tested at all — it was excluded as a
        // whole until the hotkey made it worth having a test.
        public static void Toggle(NewChatCli? prefillCli = null, string? prefillCwd = null, string? prefillAgentId = null)
        {
            if (_open is not null)
            {
                BringForward(_open);
                return;
            }

            _open = new NewChatWindow(prefillCli, prefillCwd, prefillAgentId);
            _open.Closed += OnClosed;
            (PresentForTests ?? Present)(_open);
        }

        // A hotkey pressed while the dialog is minimised has to un-minimise
        // it; Activate() alone raises a minimised window on neither platform.
        // Done here rather than in the excluded half because it is a decision
        // a test can observe on a window that was never shown.
        private static void BringForward(NewChatWindow window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            (BringForwardForTests ?? Activate)(window);
        }

        internal static Action<NewChatWindow>? PresentForTests;
        internal static Action<NewChatWindow>? BringForwardForTests;

        // The singleton itself, for a test to inspect and to clear between
        // cases: tests never Close() a window, so without this the first
        // test's window would be "already open" for every test after it.
        internal static NewChatWindow? OpenForTests
        {
            get => _open;
            set => _open = value;
        }

        [ExcludeFromCodeCoverage]
        private static void Present(NewChatWindow window)
        {
            MacOSActivation.SetRegular();
            window.Show();
            window.Activate();
        }

        // SetRegular again as well as Activate, for two reasons. It is what
        // calls activateIgnoringOtherApps: — without which Activate() only
        // reorders the window within this app, and a hotkey pressed from
        // another app would leave that app in front. And the policy may have
        // been put back to Accessory by SettingsWindow closing while this one
        // stayed open, since SetAccessory doesn't ask what else is showing.
        // Toggle's old open-window arm called Activate() alone, so the tray
        // item gets the same fix.
        [ExcludeFromCodeCoverage]
        private static void Activate(NewChatWindow window)
        {
            MacOSActivation.SetRegular();
            window.Activate();
        }

        [ExcludeFromCodeCoverage]
        private static void OnClosed(object? sender, EventArgs e)
        {
            Forget();
            MacOSActivation.SetAccessory();
        }

        // What closing means to Toggle: stop the orb watch and let the next
        // call build a fresh window. Split out of OnClosed so the part a test
        // can observe runs in one — OnClosed itself only fires on a real
        // Close(), which this suite must not call.
        internal static void Forget()
        {
            _open?._watchTimer?.Stop();
            _open?._reopenLoadTimer?.Stop();
            _open = null;
        }

        // The seam a UI test satisfies in place of the real session scan —
        // the same shape NewChatAvailability.CurrentForTests and
        // NewChatLauncher.LaunchForTests already use. Without it, both the
        // folder combo (RecentFolders.Merge wants live cwds) and the
        // orb-appeared watch (NewChatOrbWatch wants the ids that existed
        // before a launch) would depend on SessionManager.Instance, which is
        // null outside the running app.
        internal static Func<IReadOnlyDictionary<string, SessionStatus>>? CurrentStatusesForTests;

        // The seam for Browse…, in the ChooseSoundFile shape SettingsWindow
        // already uses: a Func a test can point at a canned answer instead
        // of the real OS folder picker.
        internal static Func<Task<string?>>? ChooseFolderForTests;

        // The OpenClaw slot's four seams, one per real call it makes —
        // availability, the agent list, starting the conversation, and
        // finding the orb the scan eventually creates for it. Same shape as
        // every other seam here: a test points these at a canned answer
        // instead of OrbweaverSettings/OpenClawSessions/SessionManager.
        internal static Func<OpenClawNewChatAvailability>? OpenClawAvailabilityForTests;
        internal static Func<IReadOnlyList<(string Id, string Name)>>? KnownAgentsForTests;

        internal static Func<string, CancellationToken, Task<(IRemoteChatSession? Session, string? Failure)>>?
            StartOpenClawConversationForTests;

        internal static Func<string, OrbWindow?>? OrbForTests;

        // CB-329's three seams. ReopenRowsForTests replaces the whole
        // ListedSnapshot + Build call (null means "no poll has completed yet",
        // the Loading state, exactly as ListedSnapshot's own null does);
        // ReopenOpenClawForTests replaces OpenClawSessions.Reopen, which
        // marks the row active and builds the chat session; NowForTests pins
        // the clock the "2 h ago" text is read against.
        internal static Func<IReadOnlyList<OpenClawReopenRow>?>? ReopenRowsForTests;
        internal static Func<OpenClawReopenRow, IRemoteChatSession?>? ReopenOpenClawForTests;
        internal static Func<DateTime>? NowForTests;

        // The last connect failure the gateway reported, for the detail line
        // under "Can't reach the OpenClaw gateway".
        internal static Func<string?>? LastConnectFailureForTests;

        // How long the picker waits for a first poll before it says the
        // gateway is unreachable. It keeps polling afterwards.
        internal static readonly TimeSpan ReopenLoadBudget = TimeSpan.FromSeconds(20);

        private static IReadOnlyDictionary<string, SessionStatus> CurrentStatuses() =>
            CurrentStatusesForTests?.Invoke()
            ?? SessionManager.Instance?.AllStatuses
            ?? new Dictionary<string, SessionStatus>();

        private static OrbWindow? OrbForSession(string sessionId) =>
            OrbForTests?.Invoke(sessionId) ?? SessionManager.Instance?.OrbFor(sessionId);

        // One agent row in the OpenClaw picker. ToString() rather than a
        // DisplayMemberBinding, the same reason the folder combo's plain
        // strings need neither — ComboBox falls back to ToString() with no
        // binding configured, and a ValueTuple's field names (Item1/Item2)
        // aren't reachable by a binding path anyway.
        internal sealed record AgentItem(string Id, string Name)
        {
            public override string ToString() => Name;
        }

        // The sentinel a RadioButton.Tag holds for the OpenClaw row, so the
        // same IsCheckedChanged handler that reads a boxed NewChatCli for
        // the three local rows can tell the fourth one apart by reference
        // rather than by a value that could collide with a real enum member.
        // internal rather than private: NewChatWindowTests needs it to find
        // the OpenClaw row's own reason text via ReasonTextFor, the same way
        // it finds a local row's by NewChatCli.
        internal static readonly object OpenClawTag = new();

        // CB-357: the peer launcher the Machine row asks. The real one is
        // PeerSessions.Launcher; a test hands in a fake. PeerLinkOnForTests
        // stands in for the setting, which a headless suite would otherwise
        // have to write into a settings file to flip.
        internal static IPeerLauncher? PeerLauncherForTests;
        internal static bool? PeerLinkOnForTests;

        private static IPeerLauncher? PeerLauncher => PeerLauncherForTests ?? PeerSessions.Launcher;

        // Two lines reserved under the Machine row, always, for the same
        // reason the status line reserves three: every note it shows (asking,
        // no answer, switch off) arrives after the window is on screen, and a
        // note that could add a line would resize it (CB-207).
        private const int MachineNoteLines = 2;
        private const double MachineNoteLineHeight = 15;
        private readonly ComboBox _machineCombo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _machineNote = new()
        {
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = MachineNoteLines,
            LineHeight = MachineNoteLineHeight,
            Height = MachineNoteLines * MachineNoteLineHeight
        };
        private StackPanel? _machineSection;
        private IPeerLauncher? _peers;
        // Null while this machine is chosen. Everything remote-specific hangs
        // off it, so the local path reads exactly as it did before CB-357.
        private NewChatMachines.Entry? _machine;
        private NewChatMachines.Scope? _scope;
        private int _machineGeneration;
        private CancellationTokenSource? _machineCts;
        private bool _restoringMachine;
        private int _machineIndex;
        // What each CLI row and the OpenClaw row were enabled for locally, so
        // going back to this machine can put them back.
        private readonly Dictionary<RadioButton, bool> _localEnabled = new();
        private readonly Dictionary<RadioButton, string?> _localTip = new();
        private List<string>? _localFolders;
        private string? _localFolder;
        private NewChatCli? _localCli;
        private bool _localOpenClaw;

        private readonly StackPanel _cliList = new() { Spacing = 6 };
        private readonly StackPanel _accountSection = new() { Spacing = 6, IsVisible = false };
        private readonly StackPanel _folderSection = new() { Spacing = 6 };
        private readonly StackPanel _agentSection = new() { Spacing = 6, IsVisible = false };
        private readonly ComboBox _accountCombo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _folderCombo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _agentCombo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        // Three lines reserved up front and trimmed past that, rather than
        // left to grow: every message this line shows arrives after the window
        // is on screen, and a launch message carries the whole folder path, so
        // an unbounded line would resize the window after show — exactly what
        // CB-207 removes (see ReservedSlot). The reserve is a deliberate cost
        // to every New chat window, 38pt taller than before CB-207, sized to
        // the longest fixed message rather than to the rare long path: two
        // lines was tried and "Terminal opened; no orb yet…" measured three at
        // this width, so it would have been trimmed on screen
        // (EveryFixedStatusMessageFitsTheReservedLines pins it). A path that
        // needs more is trimmed, with the full text in the ToolTip.
        private const int StatusLines = 3;
        private const double StatusLineHeight = 18;

        private readonly TextBlock _statusLine = new()
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = StatusLines,
            LineHeight = StatusLineHeight,
            MinHeight = StatusLines * StatusLineHeight,
            Opacity = 0.8
        };
        private readonly Button _startButton = new() { Content = "Start" };
        private readonly Button _browseButton = new() { Content = "Browse…" };
        // CB-207's space-holders, one per slot whose occupant changes after
        // show. See ReservedSlot for why they exist and Ghost for what they
        // are; which ones are visible is decided once, in BuildCliList.
        private readonly StackPanel _accountGhost = Ghost("Account", withBrowse: false);

        // CB-203's missing-directory warning under the Account picker, and its
        // placeholder in the account ghost. The line can appear and disappear
        // after show, so — the CB-207 rule — it never moves the window: its
        // height is fixed at three lines (the longest warning wraps to three
        // in the headless font the height tests measure with, the same reason
        // the status line reserves three), and the ghost reserves the same
        // lines whenever any account the dialog could select has a missing
        // directory (decided once, in BuildCliList). When none does, nothing
        // is reserved and the dialog reads exactly as it did before.
        private const int AccountWarningLines = 3;
        private const double AccountWarningLineHeight = 15;
        private readonly TextBlock _accountWarning = AccountWarningLine(opacity: 0.7);
        private readonly TextBlock _accountWarningGhost = AccountWarningLine(opacity: 0);
        private readonly Control _folderGhost = Ghost("Folder", withBrowse: true);

        // CB-329: the OpenClaw section is now a mode row over two panes, "New
        // conversation" (the old Agent picker) and "Open existing" (filter,
        // search, list). Each pane has its own ghost, built in the
        // constructor because the reason-line ghosts they carry are instance
        // state. See ReservedSlot.
        private readonly Control _agentGhost;
        private readonly Control _existingGhost;
        private readonly List<TextBlock> _modeReasonGhosts = new();

        // Fixed, never measured from content: the list is the one part of the
        // dialog whose contents arrive after show (the first poll) and change
        // with every filter keystroke, so it is a box of a set height, the
        // same way the status line is a set number of lines. Sized so the
        // dialog with everything available still fits a 720 px work area
        // (NewChatWindowHeightTests pins the bound).
        internal const double ExistingListHeight = 124;
        internal const double ExistingRowHeight = 40;
        private const double ModeReasonHeight = 16;

        private readonly RadioButton _newModeRadio = new()
        {
            GroupName = "openclaw-mode",
            Content = "New conversation"
        };
        private readonly RadioButton _existingModeRadio = new()
        {
            GroupName = "openclaw-mode",
            Content = "Open existing"
        };
        private readonly TextBlock _modeReason = ModeReasonLine(opacity: 0.7);
        private readonly StackPanel _newPane = new() { Spacing = 6 };
        private readonly StackPanel _existingPane = new() { Spacing = 6, IsVisible = false };
        private readonly ComboBox _existingAgentFilter = new() { Width = 150 };
        private readonly TextBox _existingSearch = new() { Watermark = "Search" };
        private readonly ListBox _existingList = new();
        private readonly TextBlock _existingEmpty = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.7
        };
        // Under the unreachable headline: the known connect failure, or where
        // to look. Three lines at most, full text in the ToolTip, inside the
        // list's fixed-height cell so the window height does not move.
        private readonly TextBlock _existingEmptyDetail = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 3,
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Avalonia.Thickness(16, 6, 16, 0),
            IsVisible = false
        };
        private readonly StackPanel _existingEmptyBox = new() { VerticalAlignment = VerticalAlignment.Center };
        private bool _reopenTimedOut;
        private DateTime _reopenLoadStartedUtc;
        private bool _existingMode;
        private bool _canStartNew;
        private IReadOnlyList<OpenClawReopenRow>? _reopenRows;
        private IReadOnlyDictionary<string, string> _agentNames = new Dictionary<string, string>();
        private DispatcherTimer? _reopenLoadTimer;
        private Grid? _accountSlot;
        private readonly NewChatCli? _prefillCli;
        private readonly string? _prefillCwd;
        private readonly string? _prefillAgentId;

        private NewChatCli? _selectedCli;
        private bool _openClawSelected;
        private HashSet<string>? _watchPriorIds;
        private NewChatCli? _watchCli;
        private string? _watchFolder;
        private string? _watchOpenClawSessionId;
        private IRemoteChatSession? _watchOpenClawSession;
        private DateTime _watchDeadlineUtc;
        private DispatcherTimer? _watchTimer;

        // Parameterless-looking default args rather than an overload, so the
        // reflection seam every smoke test in this app uses
        // (GetConstructor(..., types: new[] { typeof(NewChatCli?),
        // typeof(string), typeof(string) })) reaches exactly one constructor
        // — the same reasoning SettingsWindowSmokeTest's own header gives
        // for reaching this privately rather than through Toggle().
        private NewChatWindow(NewChatCli? prefillCli = null, string? prefillCwd = null, string? prefillAgentId = null)
        {
            _prefillCli = prefillCli;
            _prefillCwd = prefillCwd;
            _prefillAgentId = prefillAgentId;

            _agentGhost = ModeGhost(Ghost("Agent", withBrowse: false));
            _existingGhost = ExistingPaneGhost();
            BuildExistingPane();
            _newModeRadio.IsCheckedChanged += (_, _) =>
            {
                if (_newModeRadio.IsChecked != true) return;
                _existingMode = false;
                ApplyMode();
            };
            _existingModeRadio.IsCheckedChanged += (_, _) =>
            {
                if (_existingModeRadio.IsChecked != true) return;
                _existingMode = true;
                ApplyMode();
            };

            Title = "New chat";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            MinHeight = 200;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            KeyDown += OnWindowKeyDown;

            Content = Body();

            // Once more now the account slot exists: BuildCliList ran inside
            // Body(), before it did, so the slot's own visibility was never set.
            UpdateTargetSectionVisibility();

            // The full text of a status message the three-line trim cut short.
            _statusLine.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty) ToolTip.SetTip(_statusLine, _statusLine.Text);
            };
        }

        // Escape and Cmd-W close it, the same as SettingsWindow — a small
        // dialog with no unsaved state to protect. Pure, in
        // SettingsWindow.ShouldCloseOnKeyDown's own shape, so the decision
        // is testable without ever calling the real Close() — closing a
        // headless window here risks the same process-wide font-cache
        // corruption SettingsWindowSmokeTest's own comment documents.
        internal static bool ShouldClose(Key key, KeyModifiers modifiers) =>
            key == Key.Escape || (key == Key.W && modifiers.HasFlag(KeyModifiers.Meta));

        // internal, not private: a test drives this directly with a
        // non-closing key to prove the no-op half runs without ever routing
        // through KeyDown (and without ever supplying a closing key, which
        // would reach the real Close() below — see CloseForReal's own
        // comment on why that's excluded rather than tested).
        internal void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (ShouldClose(e.Key, e.KeyModifiers)) CloseForReal();
        }

        // The one line that actually needs the process's real windowing:
        // excluded so ShouldClose above stays the thing a test can drive.
        [ExcludeFromCodeCoverage]
        private void CloseForReal() => Close();

        private Control Body()
        {
            var root = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };

            BuildMachineRow(root);

            root.Children.Add(new TextBlock { Text = "CLI", FontWeight = FontWeight.SemiBold });
            BuildCliList();
            root.Children.Add(_cliList);

            // Between the CLI list and the Folder section, the same reading
            // order the plan's own mockup gives: which CLI, then which
            // account it runs under, then where. Hidden by default and only
            // ever shown by UpdateTargetSectionVisibility — CB-201's "empty
            // list means no picker" decision, and never for anything but
            // Claude Code (see that method's own comment).
            _accountSection.Children.Add(new TextBlock { Text = "Account", FontWeight = FontWeight.SemiBold });
            _accountSection.Children.Add(_accountCombo);
            _accountSection.Children.Add(_accountWarning);
            _accountGhost.Children.Add(_accountWarningGhost);
            _accountCombo.SelectionChanged += (_, _) => UpdateAccountWarning();
            _accountSlot = ReservedSlot(_accountGhost, _accountSection);
            root.Children.Add(_accountSlot);

            // Folder (local CLIs) and Agent (OpenClaw) occupy the same slot —
            // "OpenClaw selected: an agent picker replaces the folder field"
            // is the plan's own wording — so both sections are built once
            // and visibility toggles between them rather than one replacing
            // the other's controls in the tree.
            _folderSection.Children.Add(new TextBlock { Text = "Folder", FontWeight = FontWeight.SemiBold });

            var folderRow = new DockPanel();
            DockPanel.SetDock(_browseButton, Dock.Right);
            _browseButton.Margin = new Avalonia.Thickness(8, 0, 0, 0);
            _browseButton.Click += async (_, _) => await BrowseForFolder();
            folderRow.Children.Add(_browseButton);
            BuildFolderCombo();
            folderRow.Children.Add(_folderCombo);
            _folderSection.Children.Add(folderRow);

            // CB-329: the mode row, then whichever pane the mode shows. The
            // reason line is the "New conversation" disabled-because text for
            // ReplyDisabled; it has a fixed height and is reserved in both
            // ghosts only then (BuildCliList decides once).
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            modeRow.Children.Add(_newModeRadio);
            modeRow.Children.Add(_existingModeRadio);
            _agentSection.Children.Add(modeRow);
            _agentSection.Children.Add(_modeReason);

            _newPane.Children.Add(new TextBlock { Text = "Agent", FontWeight = FontWeight.SemiBold });
            _newPane.Children.Add(_agentCombo);
            _agentSection.Children.Add(_newPane);

            _agentSection.Children.Add(_existingPane);

            root.Children.Add(ReservedSlot(_folderGhost, _agentGhost, _existingGhost, _folderSection, _agentSection));

            _startButton.Click += async (_, _) => await StartClicked();
            root.Children.Add(_startButton);

            root.Children.Add(_statusLine);

            return root;
        }

        // CB-207: a window that resizes itself while it is on screen can be
        // left drawing a stale surface. Measured on a real Mac (Avalonia
        // 12.1.1): change the window's height while it is not presenting —
        // minimised was the reliable way to get there — and once it presents
        // again the old-sized surface is stretched to the new frame, until the
        // next resize. Growing gives exactly what was reported: everything
        // scaled up, Start cut off at the bottom, and a layout that no longer
        // lines up with what is drawn. Minimising only makes it certain: the
        // same stale-surface stretch is a race on any resize, which is why the
        // report says "sometimes" — AvaloniaUI/Avalonia#22215, open and
        // unmerged at 12.1.1, describes a Metal frame rendered into a surface
        // of the old size and stretched by Core Animation after a resize.
        // Avalonia.Native also drops a resize that arrives while another is
        // in progress (WindowBaseImpl::Resize's _inResize guard), and marks
        // any frame change during a live resize as a User resize, which
        // Window.HandleResized answers by switching SizeToContent off for good
        // even with CanResize false.
        //
        // So nothing that changes after show is allowed to change the height.
        // Each slot whose occupant changes (Account shown or hidden, Folder
        // swapped for Agent) is a single Grid cell holding its real sections
        // plus a ghost of every shape that could appear there, so the cell,
        // and so the window, is always as tall as the tallest of them. The
        // window keeps SizeToContent, which now resolves once, before the
        // first show, and never moves after it.
        //
        // The real sections keep toggling IsVisible exactly as before, so a
        // hidden picker is still gone from the tab order and from
        // accessibility. Only the ghost takes up the space, and it takes
        // nothing else.
        private static Grid ReservedSlot(params Control[] layers)
        {
            var slot = new Grid();
            foreach (var layer in layers)
            {
                layer.VerticalAlignment = VerticalAlignment.Top;
                slot.Children.Add(layer);
            }

            return slot;
        }

        // A section's shape with nothing in it that can be seen, clicked,
        // focused or announced: the same label, the same ComboBox (holding one
        // item, since an empty ComboBox measures shorter than one showing its
        // selection), and the Browse button when the section has one. Each
        // control is disabled and unfocusable itself, not only through the
        // panel, so Tab can never land on one. Built
        // from the same controls as the real section rather than a hard-coded
        // height, so it measures the same in both themes and at any font size.
        // The tests pin that the height really does hold across every switch,
        // which catches this shape drifting away from the real one.
        private static StackPanel Ghost(string label, bool withBrowse)
        {
            var field = new ComboBox
            {
                MinWidth = 260,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[] { label },
                SelectedIndex = 0,
                IsEnabled = false,
                Focusable = false
            };

            Control row = field;
            if (withBrowse)
            {
                var browse = new Button
                {
                    Content = "Browse…",
                    Margin = new Avalonia.Thickness(8, 0, 0, 0),
                    IsEnabled = false,
                    Focusable = false
                };
                var dock = new DockPanel();
                DockPanel.SetDock(browse, Dock.Right);
                dock.Children.Add(browse);
                dock.Children.Add(field);
                row = dock;
            }

            var ghost = new StackPanel
            {
                Spacing = 6,
                Opacity = 0,
                IsHitTestVisible = false,
                IsEnabled = false,
                IsVisible = false
            };
            ghost.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
            ghost.Children.Add(row);
            AutomationProperties.SetAccessibilityView(ghost, AccessibilityView.Raw);
            return ghost;
        }

        // internal for NewChatWindowSmokeTests, the same visibility
        // TrayMenuTests etc. use to reach a window's own children without a
        // synthesized click on each one.
        internal Control AccountGhost => _accountGhost;
        internal Control FolderGhost => _folderGhost;
        internal Control AgentGhost => _agentGhost;
        internal Control ExistingGhost => _existingGhost;
        internal RadioButton NewConversationRadio => _newModeRadio;
        internal RadioButton OpenExistingRadio => _existingModeRadio;
        internal TextBlock ModeReason => _modeReason;
        internal StackPanel NewPane => _newPane;
        internal StackPanel ExistingPane => _existingPane;
        internal ListBox ExistingList => _existingList;
        internal ComboBox ExistingAgentFilter => _existingAgentFilter;
        internal TextBox ExistingSearch => _existingSearch;
        internal TextBlock ExistingEmpty => _existingEmpty;
        internal TextBlock ExistingEmptyDetail => _existingEmptyDetail;
        internal bool OpenExistingSelected => _existingMode;
        internal bool ReopenLoadTimerRunning => _reopenLoadTimer?.IsEnabled == true;
        internal StackPanel CliList => _cliList;
        internal StackPanel AccountSection => _accountSection;
        internal ComboBox AccountCombo => _accountCombo;
        internal TextBlock AccountWarning => _accountWarning;
        internal TextBlock AccountWarningGhost => _accountWarningGhost;
        internal ComboBox FolderCombo => _folderCombo;
        internal StackPanel FolderSection => _folderSection;
        internal ComboBox AgentCombo => _agentCombo;
        internal StackPanel AgentSection => _agentSection;
        internal TextBlock StatusLine => _statusLine;
        internal Button StartButton => _startButton;
        internal Button BrowseButton => _browseButton;
        internal NewChatCli? SelectedCli => _selectedCli;
        internal bool OpenClawSelected => _openClawSelected;

        private void BuildCliList()
        {
            _cliList.Children.Clear();

            var options = NewChatAvailability.CurrentForTests?.Invoke() ?? NewChatAvailability.Current();
            NewChatCli? firstEnabled = null;

            var lastCli = OrbweaverSettings.NewChatLastCli is { Length: > 0 } saved
                && Enum.TryParse<NewChatCli>(saved, out var parsed)
                ? parsed
                : (NewChatCli?)null;

            foreach (var option in options)
            {
                var radio = new RadioButton
                {
                    GroupName = "NewChatCli",
                    Content = NewChatLauncher.DisplayName(option.Cli),
                    IsEnabled = option.Enabled,
                    Tag = option.Cli
                };

                if (option.Enabled && firstEnabled is null) firstEnabled = option.Cli;

                // Reason is set exactly when disabled, Warning exactly when
                // enabled-but-imperfect — NewChatOption's own comment states
                // this, so at most one of the two is ever shown. Stated on
                // screen, not only as a tooltip: a reason nobody can see
                // without hovering isn't a stated reason, and a screenshot
                // can't prove a tooltip exists.
                var statedText = !option.Enabled ? option.Reason : option.Warning;
                if (statedText is { Length: > 0 } tip) ToolTip.SetTip(radio, tip);

                var capturedCli = option.Cli;
                radio.IsCheckedChanged += (_, _) =>
                {
                    if (radio.IsChecked != true) return;
                    _selectedCli = capturedCli;
                    _openClawSelected = false;
                    BuildAccountCombo();
                    UpdateTargetSectionVisibility();
                };

                _cliList.Children.Add(radio);
                _localEnabled[radio] = option.Enabled;
                _localTip[radio] = ToolTip.GetTip(radio) as string;

                if (statedText is { Length: > 0 } shown)
                {
                    _cliList.Children.Add(ReasonText(shown, option.Cli));
                }
            }

            var openClawAvailability = OpenClawAvailabilityForTests?.Invoke() ?? OpenClawNewChat.AvailabilityFor(
                OrbweaverSettings.OpenClawEnabled, OrbweaverSettings.OpenClawHost,
                OrbweaverSettings.OpenClawReplyEnabled);
            // CB-329: "ready" is two questions now. The radio is live when a
            // conversation can be reopened (needs only a gateway); starting a
            // new one still needs the reply switch.
            var openClawUsable = OpenClawNewChat.CanOpenExisting(openClawAvailability);
            _canStartNew = OpenClawNewChat.CanStartNew(openClawAvailability);

            // Which shapes each slot can ever hold for this window's lifetime
            // (CB-207, see ReservedSlot) — decided once here from the same
            // inputs the sections themselves are shown from, so a slot is
            // never reserved for something that could not appear in it. A
            // user with no extra accounts gets no gap where the picker would
            // be; the window reads exactly as it did before CB-201.
            //
            // Any usable local CLI with a real extra account reserves it
            // (CB-203): the picker can appear for Codex or Grok as well as
            // Claude Code, so the slot is held when any of the three could
            // show it, not only Claude Code.
            _accountGhost.IsVisible = options.Any(o => o.Enabled && AccountChoices(o.Cli).Count > 1);
            _accountWarningGhost.IsVisible = _accountGhost.IsVisible && options.Any(o =>
                o.Enabled && AccountChoices(o.Cli).Any(c => AccountWarningFor(o.Cli, c) is not null));
            _folderGhost.IsVisible = options.Any(o => o.Enabled);
            // CB-357: a remote machine can bring accounts and a folder of its
            // own, after show, so the Machine row reserves both slots whether
            // or not this machine would have (CB-207).
            if (_machineSection is not null)
            {
                _accountGhost.IsVisible = true;
                _folderGhost.IsVisible = true;
            }
            _agentGhost.IsVisible = openClawUsable;
            _existingGhost.IsVisible = openClawUsable;
            var replyDisabled = openClawAvailability == OpenClawNewChatAvailability.ReplyDisabled;
            foreach (var g in _modeReasonGhosts) g.IsVisible = replyDisabled;

            _newModeRadio.IsEnabled = _canStartNew;
            _modeReason.IsVisible = replyDisabled;
            _modeReason.Text = replyDisabled ? OpenClawNewChat.ReasonFor(openClawAvailability) : null;
            ToolTip.SetTip(_modeReason, _modeReason.Text);
            ToolTip.SetTip(_newModeRadio, replyDisabled ? _modeReason.Text : null);

            var openClawRadio = new RadioButton
            {
                GroupName = "NewChatCli",
                Content = "OpenClaw",
                IsEnabled = openClawUsable,
                Tag = OpenClawTag
            };

            // Only NoGateway explains itself under the row. ReplyDisabled
            // leaves the row live and says its piece under the disabled
            // "New conversation" radio instead.
            var openClawReasonText = openClawUsable ? null : OpenClawNewChat.ReasonFor(openClawAvailability);
            if (openClawReasonText is { Length: > 0 } openClawTip) ToolTip.SetTip(openClawRadio, openClawTip);

            openClawRadio.IsCheckedChanged += (_, _) =>
            {
                if (openClawRadio.IsChecked != true) return;
                _selectedCli = null;
                _openClawSelected = true;
                BuildAgentCombo();
                BuildAccountCombo();
                ChooseDefaultMode();
                UpdateTargetSectionVisibility();
            };

            _cliList.Children.Add(openClawRadio);
            _localEnabled[openClawRadio] = openClawUsable;
            _localTip[openClawRadio] = ToolTip.GetTip(openClawRadio) as string;

            if (openClawReasonText is { Length: > 0 } shownOpenClawReason)
            {
                _cliList.Children.Add(ReasonText(shownOpenClawReason, OpenClawTag));
            }

            // Preference order: an explicit OpenClaw agent prefill (an
            // OpenClaw orb's own "New chat here") wins outright when the
            // slot is actually usable — it names one specific agent, which
            // is more specific than any local prefill or last-used choice
            // could be. Failing that: a local prefill (an orb's own CLI),
            // then whatever was chosen last time, then the first enabled
            // local row, then OpenClaw if it's the only usable option — so
            // the dialog never opens with nothing selected when at least one
            // entry is available.
            if (_prefillAgentId is { Length: > 0 } wantedAgent && _canStartNew)
            {
                _openClawSelected = true;
                openClawRadio.IsChecked = true;
                BuildAgentCombo(wantedAgent);
                ChooseDefaultMode();
                BuildAccountCombo();
                UpdateTargetSectionVisibility();
                return;
            }

            var preferred =
                (_prefillCli is { } wanted && options.Any(o => o.Cli == wanted && o.Enabled)) ? wanted
                : (lastCli is { } last && options.Any(o => o.Cli == last && o.Enabled)) ? last
                : firstEnabled;

            if (preferred is { } chosen)
            {
                _selectedCli = chosen;
                foreach (var child in _cliList.Children)
                {
                    if (child is RadioButton { Tag: NewChatCli cli } rb && cli == chosen) rb.IsChecked = true;
                }
            }
            else if (openClawUsable)
            {
                _openClawSelected = true;
                openClawRadio.IsChecked = true;
                BuildAgentCombo();
                ChooseDefaultMode();
            }

            UpdateTargetSectionVisibility();
        }

        private void UpdateTargetSectionVisibility()
        {
            _folderSection.IsVisible = !_openClawSelected;
            _agentSection.IsVisible = _openClawSelected;
            UpdateModeVisibility();

            // Any of the three local CLIs (CB-203 extended CB-201's Claude
            // Code-only picker to Codex and Grok), never alongside OpenClaw,
            // and never shown for a one-entry list (Default alone) — CB-201's
            // "empty list means no picker" decision. BuildAccountCombo has
            // already filled the combo from the selected CLI's own list, or
            // cleared it for OpenClaw. Read off the combo's own item count
            // rather than a separate field, so this can never drift out of
            // sync with what BuildAccountCombo actually populated.
            var accountChoiceCount = (_accountCombo.ItemsSource as IEnumerable<NewChatAccounts.Choice>)?.Count() ?? 0;
            //
            // And never beyond the space BuildCliList reserved when the window
            // was built (CB-207). The count is read fresh on every switch, but
            // Settings is not modal, so accounts can be added while this
            // dialog is open; showing a picker that no ghost made room for
            // would grow the window on screen, which is the very resize
            // CB-207 removes. A new account appears the next time the dialog
            // opens; one removed mid-session just hides the picker, leaving
            // the ghost to hold the height.
            _accountSection.IsVisible = _accountGhost.IsVisible
                && !_openClawSelected && _selectedCli is not null && accountChoiceCount > 1;

            // An empty slot would still collect the root StackPanel's spacing,
            // leaving a 12px gap where the picker never appears.
            if (_accountSlot is not null) _accountSlot.IsVisible = _accountGhost.IsVisible;
        }

        // Populates the Account combo for whichever CLI is now selected —
        // every real choice from that CLI's own list (CB-203: Codex reads
        // CodexHomes and Grok GrokHomes, never Claude Code's
        // ClaudeCodeProfileDirs), cleared for OpenClaw so a stale list from a
        // previous selection can never leak into
        // UpdateTargetSectionVisibility's item count. Restores the account
        // last chosen *for this CLI*, falling back to Default when that entry
        // has since been removed from settings (CB-201's own restore rule).
        private void BuildAccountCombo()
        {
            if (_openClawSelected || _selectedCli is not { } cli)
            {
                _accountCombo.ItemsSource = null;
                return;
            }

            if (_scope is { } remote)
            {
                // The far machine's own accounts for this CLI. "default" is
                // the CLI's own directory there, which Choice spells as null.
                var theirs = remote.Accounts(RelayAccounts.CliName(cli))
                    .Select(a => new NewChatAccounts.Choice(a.Label, a.Id == RelayAccounts.Default ? null : a.Id))
                    .ToList();
                _accountCombo.ItemsSource = theirs;
                _accountCombo.SelectedIndex = theirs.Count > 0 ? 0 : -1;
                UpdateAccountWarning();
                return;
            }

            var choices = AccountChoices(cli);
            _accountCombo.ItemsSource = choices;

            var saved = OrbweaverSettings.NewChatLastProfileFor(cli);
            var preferredIndex = saved is { Length: > 0 }
                ? choices.FindIndex(c => c.ProfileDir == saved)
                : -1;

            _accountCombo.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
            UpdateAccountWarning();
        }

        // The selected account's missing-directory warning, shown only in
        // space BuildCliList reserved for it — see _accountWarning.
        private void UpdateAccountWarning()
        {
            // A far machine's account directory is not ours to test.
            var text = _scope is null && _selectedCli is { } cli && _accountCombo.SelectedItem is NewChatAccounts.Choice choice
                ? AccountWarningFor(cli, choice)
                : null;

            _accountWarning.Text = text;
            ToolTip.SetTip(_accountWarning, text);
            _accountWarning.IsVisible = _accountWarningGhost.IsVisible && text is not null;
        }

        // The dialog's own seam for the filesystem half of
        // NewChatAccountWarning, the same shape as ChooseFolderForTests: a
        // test or screenshot decides which account directories "exist"
        // rather than depending on what is in the real home directory.
        internal static Func<string, bool>? AccountDirectoryExistsForTests;

        private static string? AccountWarningFor(NewChatCli cli, NewChatAccounts.Choice choice) =>
            NewChatAccountWarning.For(
                cli,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                choice.ProfileDir,
                AccountDirectoryExistsForTests ?? Directory.Exists);

        private static TextBlock AccountWarningLine(double opacity) => new()
        {
            FontSize = 11,
            Opacity = opacity,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = AccountWarningLines,
            LineHeight = AccountWarningLineHeight,
            Height = AccountWarningLines * AccountWarningLineHeight,
            IsVisible = false
        };

        private static List<NewChatAccounts.Choice> AccountChoices(NewChatCli cli) =>
            NewChatAccounts.Choices(
                cli,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                cli switch
                {
                    NewChatCli.Codex => OrbweaverSettings.CodexHomes,
                    NewChatCli.Grok => OrbweaverSettings.GrokHomes,
                    _ => OrbweaverSettings.ClaudeCodeProfileDirs
                }).ToList();

        // The stated reason/warning line under a CLI row — small, secondary
        // text, indented to read as belonging to the row above it. Tag
        // carries the same value the row's own RadioButton.Tag does (a
        // NewChatCli, or OpenClawTag for the fourth row), so a test can find
        // "the reason text for this row" the same way it finds the row
        // itself, without depending on sibling order in _cliList.Children.
        private const double ReasonOpacity = 0.7;

        private static TextBlock ReasonText(string text, object tag) => new()
        {
            Text = text,
            Tag = tag,
            FontSize = 11,
            Opacity = ReasonOpacity,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(24, -2, 0, 4)
        };

        // internal for NewChatWindowTests: the reason/warning line for a
        // given row's tag, or null when that row has none (an enabled row
        // with no warning, most local CLIs most of the time).
        internal TextBlock? ReasonTextFor(object tag) =>
            _cliList.Children.OfType<TextBlock>().SingleOrDefault(t => Equals(t.Tag, tag));

        private void BuildAgentCombo(string? preferredAgentId = null)
        {
            var agents = KnownAgentsForTests?.Invoke() ?? OpenClawSessions.KnownAgents();
            var items = agents.Select(a => new AgentItem(a.Id, a.Name)).ToList();

            _agentCombo.ItemsSource = items;

            var preferredIndex = preferredAgentId is { Length: > 0 } wanted
                ? items.FindIndex(a => a.Id == wanted)
                : -1;

            _agentCombo.SelectedIndex = preferredIndex >= 0 ? preferredIndex : (items.Count > 0 ? 0 : -1);
        }

        // --- CB-329: Open existing ---------------------------------------

        // New conversation or Open existing, decided each time OpenClaw is
        // selected: whatever the user last chose, and the first time New when
        // it is possible (the long-standing default) or Existing when it is
        // the only choice left (ReplyDisabled).
        private void ChooseDefaultMode()
        {
            _existingMode = !_canStartNew || _existingMode;
            (_existingMode ? _existingModeRadio : _newModeRadio).IsChecked = true;
            ApplyMode();
        }

        private void ApplyMode()
        {
            UpdateModeVisibility();
            if (_openClawSelected && _existingMode) LoadReopenRows();
            else _reopenLoadTimer?.Stop();
        }

        private void UpdateModeVisibility()
        {
            // Follows the CLI too: with a local CLI chosen neither pane is on
            // screen, whatever mode OpenClaw was last left in.
            _newPane.IsVisible = _openClawSelected && !_existingMode;
            _existingPane.IsVisible = _openClawSelected && _existingMode;
            _startButton.Content = _openClawSelected && _existingMode ? "Open" : "Start";
            if (!_openClawSelected) _reopenLoadTimer?.Stop();
        }

        private static IReadOnlyList<OpenClawReopenRow>? CurrentReopenRows() =>
            ReopenRowsForTests is { } seam
                ? seam()
                : OpenClawSessions.ListedSnapshot() is { } listed
                    ? OpenClawReopenList.Build(
                        listed,
                        OrbweaverSettings.OpenClawHeartbeatMode,
                        OrbweaverSettings.OpenClawCronMode,
                        OrbweaverSettings.OpenClawNativeMode,
                        OrbweaverSettings.OpenClawApproveEnabled)
                    : null;

        // Built when Open existing is selected and not again while the dialog
        // sits open, as the plan has it: a list rearranging under the pointer
        // is worse than a stale one. Filter and search re-run over these.
        private void LoadReopenRows()
        {
            _reopenLoadTimer?.Stop();
            _reopenRows = CurrentReopenRows();
            _reopenTimedOut = false;
            _reopenLoadStartedUtc = ReopenNow();
            BuildExistingFilter();
            RefreshExistingList();

            if (_reopenRows is not null) return;

            // No poll has completed on this connection yet. Ask again once a
            // second until one has, rather than showing "No conversations" for
            // a gateway that has simply not answered.
            _reopenLoadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _reopenLoadTimer.Tick += OnReopenLoadTick;
            _reopenLoadTimer.Start();
        }

        // Named and internal so a test drives it without waiting a second,
        // the OnWatchTick pattern.
        internal void OnReopenLoadTick(object? sender, EventArgs e)
        {
            var rows = CurrentReopenRows();
            if (rows is null)
            {
                // Give up *saying* Loading after the budget, not polling: the
                // gateway may come up later and the list then replaces this.
                if (!_reopenTimedOut && ReopenNow() - _reopenLoadStartedUtc >= ReopenLoadBudget)
                {
                    _reopenTimedOut = true;
                    RefreshExistingList();
                }

                return;
            }

            _reopenLoadTimer?.Stop();
            _reopenTimedOut = false;
            _reopenRows = rows;
            BuildExistingFilter();
            RefreshExistingList();
        }

        private void BuildExistingPane()
        {
            var filterRow = new DockPanel();
            DockPanel.SetDock(_existingAgentFilter, Dock.Left);
            _existingAgentFilter.Margin = new Avalonia.Thickness(0, 0, 8, 0);
            filterRow.Children.Add(_existingAgentFilter);
            filterRow.Children.Add(_existingSearch);
            _existingPane.Children.Add(filterRow);

            // Every realized item is exactly ExistingRowHeight on both themes.
            // Fluent's ListBoxItem brings its own padding and minimum height
            // (about 54 dip on Windows), which ignores a height set on the row
            // template, so the container itself is styled: no padding, a
            // fixed height. The row template then fills it.
            _existingList.Padding = new Avalonia.Thickness(0);
            _existingList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(Layoutable.MinHeightProperty, ExistingRowHeight),
                    new Setter(Layoutable.HeightProperty, ExistingRowHeight),
                    new Setter(TemplatedControl.PaddingProperty, new Avalonia.Thickness(8, 0))
                }
            });
            // The null arm is not defensive padding. When the list's
            // VirtualizingStackPanel recycles a row container, Avalonia clears
            // its content and the ContentPresenter builds this template again
            // with a null item — FuncDataTemplate<T> casts it through rather
            // than refusing it. Handing that to ExistingRowView threw inside a
            // layout pass and took the whole app down (1.0.6, the hotfix after
            // CB-329). An empty Panel is what a recycled container shows anyway.
            _existingList.ItemTemplate = new FuncDataTemplate<OpenClawReopenRow?>(
                (row, _) => row is null ? new Panel() : ExistingRowView(row));
            var box = new Grid { Height = ExistingListHeight };
            _existingEmptyBox.Children.Add(_existingEmpty);
            _existingEmptyBox.Children.Add(_existingEmptyDetail);
            box.Children.Add(_existingList);
            box.Children.Add(_existingEmptyBox);
            _existingPane.Children.Add(box);

            _existingAgentFilter.SelectionChanged += (_, _) => RefreshExistingList();
            _existingSearch.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty) RefreshExistingList();
            };
            _existingList.DoubleTapped += (_, _) => OpenExistingClicked();
            _existingList.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                OpenExistingClicked();
            };
        }

        // "All agents", then every agent the gateway knows. Rebuilt with the
        // rows, so an agent added since the dialog opened shows the next time
        // Open existing is selected.
        private void BuildExistingFilter()
        {
            var agents = KnownAgentsForTests?.Invoke() ?? OpenClawSessions.KnownAgents();
            _agentNames = agents.ToDictionary(a => a.Id, a => a.Name, StringComparer.OrdinalIgnoreCase);

            var items = new List<AgentItem> { new("", "All agents") };
            items.AddRange(agents.Select(a => new AgentItem(a.Id, a.Name)));
            _existingAgentFilter.ItemsSource = items;
            _existingAgentFilter.SelectedIndex = 0;
        }

        private string AgentName(string id) => _agentNames.TryGetValue(id, out var name) ? name : id;

        private IReadOnlyList<OpenClawReopenRow> FilteredRows()
        {
            if (_reopenRows is null) return Array.Empty<OpenClawReopenRow>();

            var agentId = _existingAgentFilter.SelectedItem is AgentItem { Id: { Length: > 0 } id } ? id : null;
            return OpenClawReopenList.Filter(_reopenRows, agentId, _existingSearch.Text, AgentName);
        }

        private void RefreshExistingList()
        {
            var shown = FilteredRows();
            var loaded = _reopenRows is not null;
            var text = OpenClawReopenList.EmptyText(
                loaded, _reopenTimedOut, _reopenRows?.Count ?? 0, shown.Count);
            var unreachable = !loaded && _reopenTimedOut;

            _existingList.ItemsSource = shown;
            _existingList.IsVisible = text is null;
            _existingEmpty.Text = text;
            _existingEmpty.IsVisible = text is not null;
            _existingEmptyBox.IsVisible = text is not null;

            var detail = unreachable
                ? OpenClawReopenList.UnreachableDetail(
                    LastConnectFailureForTests is { } seam ? seam() : OpenClawSessions.LastConnectFailure())
                : null;
            _existingEmptyDetail.Text = detail;
            _existingEmptyDetail.IsVisible = detail is not null;
            ToolTip.SetTip(_existingEmptyDetail, detail);
        }

        private static DateTime ReopenNow() => (NowForTests ?? (() => DateTime.UtcNow))();

        private Control ExistingRowView(OpenClawReopenRow row)
        {
            var badge = OpenClawReopenList.Badge(row);
            var ago = OpenClawReopenList.Ago(row.LastActivity, (NowForTests ?? (() => DateTime.UtcNow))());
            var agents = string.Join(", ", row.AgentIds.Select(AgentName));
            var detail = badge.Length > 0 ? agents + " · " + badge : agents;

            Control avatar;
            if (OpenClawSessions.AvatarForSession(row.OrbSessionId)?.Frames is { Count: > 0 } frames)
            {
                avatar = new Image { Source = frames[0], Width = 24, Height = 24 };
            }
            else
            {
                avatar = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new Avalonia.CornerRadius(12),
                    Background = Brushes.Gray,
                    Child = new TextBlock
                    {
                        Text = OrbGlyph.For(row.Title, OrbweaverSettings.TwoLetterGlyphs),
                        FontSize = 10,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }

            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock
            {
                Text = row.Title,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            titles.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var when = new TextBlock
            {
                Text = ago,
                FontSize = 11,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(8, 0, 0, 0)
            };

            var dock = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(avatar, Dock.Left);
            avatar.Margin = new Avalonia.Thickness(0, 0, 8, 0);
            avatar.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(when, Dock.Right);
            dock.Children.Add(avatar);
            dock.Children.Add(when);
            dock.Children.Add(titles);

            AutomationProperties.SetName(dock, row.Title + ", " + badge + ", " + ago);
            return dock;
        }

        private void OpenExistingClicked()
        {
            if (_existingList.SelectedItem is not OpenClawReopenRow row)
            {
                _statusLine.Text = "Choose a conversation first.";
                return;
            }

            var session = ReopenOpenClawForTests is { } seam ? seam(row) : OpenClawSessions.Reopen(row);
            if (session is null)
            {
                _statusLine.Text = "Couldn't open that conversation.";
                return;
            }

            _statusLine.Text = "Opening " + row.Title + ". Waiting for its orb…";
            StartOpenClawWatch(session);
        }

        private static TextBlock ModeReasonLine(double opacity) => new()
        {
            FontSize = 11,
            Opacity = opacity,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Height = ModeReasonHeight,
            Margin = new Avalonia.Thickness(24, -2, 0, 0),
            IsVisible = false
        };

        // The mode row's ghost: the same two radios (and the reason line when
        // ReplyDisabled reserves it) at the head of a pane's ghost.
        private StackPanel ModeGhost(StackPanel paneGhost)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            foreach (var text in new[] { "New conversation", "Open existing" })
            {
                row.Children.Add(new RadioButton { Content = text, IsEnabled = false, Focusable = false });
            }

            var reason = ModeReasonLine(opacity: 0);
            _modeReasonGhosts.Add(reason);
            paneGhost.Children.Insert(0, reason);
            paneGhost.Children.Insert(0, row);
            return paneGhost;
        }

        // The whole Existing pane as an invisible shape: mode row, filter row
        // and a box of the list's fixed height. Empty text lives inside that
        // box, so it needs nothing of its own.
        private StackPanel ExistingPaneGhost()
        {
            var filter = new DockPanel();
            var combo = new ComboBox
            {
                Width = 150,
                ItemsSource = new[] { "All agents" },
                SelectedIndex = 0,
                IsEnabled = false,
                Focusable = false,
                Margin = new Avalonia.Thickness(0, 0, 8, 0)
            };
            DockPanel.SetDock(combo, Dock.Left);
            filter.Children.Add(combo);
            filter.Children.Add(new TextBox { Watermark = "Search", IsEnabled = false, Focusable = false });

            var ghost = new StackPanel
            {
                Spacing = 6,
                Opacity = 0,
                IsHitTestVisible = false,
                IsEnabled = false,
                IsVisible = false
            };
            ghost.Children.Add(filter);
            ghost.Children.Add(new Border { Height = ExistingListHeight });
            AutomationProperties.SetAccessibilityView(ghost, AccessibilityView.Raw);
            return ModeGhost(ghost);
        }

        private void BuildFolderCombo()
        {
            var live = CurrentStatuses().Values;
            var saved = OrbweaverSettings.NewChatRecentFolders;
            var folders = RecentFolders.Merge(live, saved).ToList();

            if (_prefillCwd is { Length: > 0 } pre && !folders.Contains(pre))
            {
                folders.Insert(0, pre);
            }

            if (folders.Count == 0) folders.Add(Environment.CurrentDirectory);

            _folderCombo.ItemsSource = folders;

            var selectIndex = _prefillCwd is { Length: > 0 } wanted ? folders.IndexOf(wanted) : 0;
            _folderCombo.SelectedIndex = selectIndex >= 0 ? selectIndex : 0;
        }

        private async Task BrowseForFolder()
        {
            var seam = ChooseFolderForTests;
            var picked = seam is not null ? await seam() : await PickFolderAsync();

            if (picked is not { Length: > 0 }) return;

            var items = (_folderCombo.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
            if (!items.Contains(picked)) items.Insert(0, picked);
            _folderCombo.ItemsSource = items;
            _folderCombo.SelectedItem = picked;
        }

        // Excluded from coverage: opens the OS's own folder picker, the same
        // reason SettingsWindow's PickSoundFileAsync/BrowseForProfileDir are
        // — there is no headless storage provider to satisfy
        // OpenFolderPickerAsync. BrowseForFolder's own branching (which of
        // the two paths runs) is covered through ChooseFolderForTests
        // instead.
        [ExcludeFromCodeCoverage]
        private async Task<string?> PickFolderAsync()
        {
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null) return null;

            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder",
                AllowMultiple = false
            });

            return result.Count == 0 ? null : result[0].TryGetLocalPath();
        }

        private async Task StartClicked()
        {
            if (_machine is not null)
            {
                await StartRemoteClicked();
                return;
            }

            if (_openClawSelected)
            {
                if (_existingMode) OpenExistingClicked();
                else await StartOpenClawClicked();
                return;
            }

            if (_selectedCli is not { } cli)
            {
                _statusLine.Text = "Choose a CLI first.";
                return;
            }

            var folder = (_folderCombo.SelectedItem as string) is { Length: > 0 } chosen
                ? chosen
                : Environment.CurrentDirectory;

            // The account combo's real selection when that section is what's
            // on screen, otherwise Default. Gated on the section rather than
            // the CLI since CB-203 (all three local CLIs can show it): the
            // combo is filled for the selected CLI even when the section stays
            // hidden — an account added in Settings while this dialog was
            // open, with no slot reserved for it (CB-207) — and a launch must
            // never run under an account the user could not see chosen.
            var profileDir = _accountSection.IsVisible && _accountCombo.SelectedItem is NewChatAccounts.Choice choice
                ? choice.ProfileDir
                : null;

            _startButton.IsEnabled = false;
            _statusLine.Text = "Starting…";

            var priorIds = new HashSet<string>(CurrentStatuses().Keys, StringComparer.Ordinal);

            var launch = NewChatLauncher.LaunchForTests?.Invoke(cli, folder, profileDir)
                ?? NewChatLauncher.Launch(cli, folder, profileDir);
            _statusLine.Text = launch.Message;
            _startButton.IsEnabled = true;

            if (launch.Outcome != LaunchOutcome.Launched) return;

            OrbweaverSettings.SetNewChatLastCli(cli.ToString());
            OrbweaverSettings.SetNewChatLastProfile(cli, profileDir);

            var updatedFolders = RecentFolders.Merge(
                CurrentStatuses().Values,
                new[] { folder }.Concat(OrbweaverSettings.NewChatRecentFolders).ToList());
            OrbweaverSettings.SetNewChatRecentFolders(updatedFolders);

            StartWatch(priorIds, cli, folder);
        }

        // ---- CB-357: the Machine row ------------------------------------

        // The machine list is read once, when the window is built: a peer that
        // comes online afterwards stays "offline" until the window is reopened.
        // That is deliberate (a list that reshuffled under the cursor would be
        // worse), not a missed refresh.
        private void BuildMachineRow(StackPanel root)
        {
            _peers = PeerLauncher;
            if (_peers is null) return;

            var entries = NewChatMachines.Build(
                PeerLinkOnForTests ?? OrbweaverSettings.PeerLinkEnabled, _peers.Machines(), OperatingSystem.IsWindows());
            if (entries.Count == 0) return;

            foreach (var e in entries)
            {
                _machineCombo.Items.Add(new ComboBoxItem
                {
                    Content = e.Label,
                    Tag = e,
                    // An offline peer is listed so it can be seen to be paired,
                    // and cannot be chosen.
                    IsEnabled = e.Selectable,
                });
            }
            _machineCombo.SelectedIndex = 0;
            _machineCombo.SelectionChanged += OnMachineChanged;

            _machineSection = new StackPanel { Spacing = 6 };
            _machineSection.Children.Add(new TextBlock { Text = "Machine", FontWeight = FontWeight.SemiBold });
            _machineSection.Children.Add(_machineCombo);
            _machineSection.Children.Add(_machineNote);
            root.Children.Add(_machineSection);
        }

        internal ComboBox MachineCombo => _machineCombo;
        internal TextBlock MachineNote => _machineNote;
        internal StackPanel? MachineSection => _machineSection;

        // A test drives a switch by setting the combo's selection, which comes
        // here, and an offline pick must bounce back.
        private async void OnMachineChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_restoringMachine) return;
            if (_machineCombo.SelectedItem is not ComboBoxItem { Tag: NewChatMachines.Entry entry })
                return;

            if (!entry.Selectable)
            {
                _restoringMachine = true;
                _machineCombo.SelectedIndex = _machineIndex;
                _restoringMachine = false;
                return;
            }

            _machineIndex = _machineCombo.SelectedIndex;
            await SwitchMachineAsync(entry);
        }

        private async Task SwitchMachineAsync(NewChatMachines.Entry entry)
        {
            var generation = ++_machineGeneration;
            _machineCts?.Cancel();
            _machineCts = new CancellationTokenSource();
            var ct = _machineCts.Token;

            if (entry.IsSelf)
            {
                RestoreLocal();
                return;
            }

            // Snapshot the local form once, the first time it is left.
            if (_machine is null)
            {
                _localFolders = ((IEnumerable<string>)_folderCombo.ItemsSource!).ToList();
                _localFolder = _folderCombo.SelectedItem as string;
                _localCli = _selectedCli;
                _localOpenClaw = _openClawSelected;
            }

            _machine = entry;
            ApplyScope(NewChatMachines.Loading(entry.Name!));

            PeerLaunchInfo? info;
            try { info = await _peers!.InfoAsync(entry.Name!, ct); }
            catch (OperationCanceledException) { return; }
            // Anything else the launcher throws (a signer still loading its
            // certificate, say) is no answer: the form says so and stays usable,
            // rather than an async void taking the app down.
            catch (Exception) { info = null; }

            // A newer pick (or a return to this machine) owns the form now.
            if (generation != _machineGeneration) return;
            ApplyScope(NewChatMachines.From(entry.Name!, info));
        }

        // Paints one scope onto the form. Every control keeps its place and
        // its size — only text, enablement and items change (CB-207).
        private void ApplyScope(NewChatMachines.Scope scope)
        {
            _scope = scope;

            // This machine's own "not found" / "hook not installed" lines under
            // its rows describe this machine, not the one chosen. Opacity, never
            // IsVisible: the lines keep their height (CB-207).
            foreach (var line in _cliList.Children.OfType<TextBlock>()) line.Opacity = 0;

            // The reserved note line: the machine's own problem if it has one,
            // otherwise which CLIs it lacks and that OpenClaw stays here. The
            // OpenClaw sentence is last so the two-line trim cuts it first; the
            // tooltip has all of it.
            var missing = _cliList.Children.OfType<RadioButton>()
                .Select(r => r.Tag is NewChatCli c ? scope.CliReason(RelayAccounts.CliName(c)) : null)
                .Where(r => r is not null);
            var note = scope.Note ?? string.Join(" ", missing.Append(PeerLaunchCopy.OpenClawLocalOnly));
            _machineNote.Text = note;
            ToolTip.SetTip(_machineNote, note);

            foreach (var radio in _cliList.Children.OfType<RadioButton>())
            {
                radio.IsEnabled = radio.Tag is NewChatCli cli && scope.CliEnabled(RelayAccounts.CliName(cli));
                ToolTip.SetTip(radio, radio.Tag is NewChatCli rc
                    ? scope.CliReason(RelayAccounts.CliName(rc))
                    : PeerLaunchCopy.OpenClawLocalOnly);
            }

            _openClawSelected = false;

            // While the answer is still on its way nothing is installed as far as
            // the scope knows, so the chosen CLI is left alone: dropping it here
            // would mean it could never survive the trip to a machine that has it.
            if (scope.State != NewChatMachines.InfoState.Loading)
            {
                // Keep the chosen CLI when the far machine has it; otherwise the
                // first one it does have; otherwise none, and Start stays off.
                var keep = _selectedCli is { } sel && scope.CliEnabled(RelayAccounts.CliName(sel)) ? sel : (NewChatCli?)null;
                var pick = keep ?? _cliList.Children.OfType<RadioButton>()
                    .Where(r => r.IsEnabled && r.Tag is NewChatCli).Select(r => (NewChatCli)r.Tag!).Cast<NewChatCli?>().FirstOrDefault();

                _selectedCli = pick;
                foreach (var radio in _cliList.Children.OfType<RadioButton>())
                    radio.IsChecked = pick is { } p && radio.Tag is NewChatCli c && c == p;
            }

            var folders = scope.Folders().ToList();
            _folderCombo.ItemsSource = folders;
            _folderCombo.SelectedIndex = folders.Count > 0 ? 0 : -1;
            _browseButton.IsEnabled = false;

            BuildAccountCombo();
            UpdateTargetSectionVisibility();
            _startButton.IsEnabled = scope.StartEnabled;
            _statusLine.Text = null;
        }

        private void RestoreLocal()
        {
            _machine = null;
            _scope = null;
            foreach (var line in _cliList.Children.OfType<TextBlock>()) line.Opacity = ReasonOpacity;
            _machineNote.Text = null;
            ToolTip.SetTip(_machineNote, null);

            foreach (var (radio, enabled) in _localEnabled)
            {
                radio.IsEnabled = enabled;
                ToolTip.SetTip(radio, _localTip[radio]);
            }

            // Snapshotted the first time a remote machine was chosen, and a
            // restore only ever follows one, so both are set.
            _folderCombo.ItemsSource = _localFolders!;
            _folderCombo.SelectedItem = _localFolder;
            _browseButton.IsEnabled = true;

            // Put back what was chosen before the first remote pass, falling
            // back to the first CLI this machine can start.
            var radios = _cliList.Children.OfType<RadioButton>().ToList();
            var back = (_localOpenClaw
                    ? radios.FirstOrDefault(r => ReferenceEquals(r.Tag, OpenClawTag))
                    : radios.FirstOrDefault(r => r.Tag is NewChatCli c && c == _localCli))
                is { IsEnabled: true } was ? was
                : radios.FirstOrDefault(r => r.IsEnabled && r.Tag is NewChatCli);
            if (back is not null) back.IsChecked = true;

            BuildAccountCombo();
            UpdateTargetSectionVisibility();
            _startButton.IsEnabled = true;
            _statusLine.Text = null;
        }

        private async Task StartRemoteClicked()
        {
            // Start is disabled in each of these states; a stray click still says
            // why instead of launching.
            var scope = _scope!;   // set with _machine, always
            if (!scope.StartEnabled || _selectedCli is not { } cli
                || _folderCombo.SelectedItem is not string folder)
            {
                _statusLine.Text = scope.Note ?? "Choose a CLI first.";
                return;
            }

            var machine = _machine!.Name!;

            var account = _accountSection.IsVisible && _accountCombo.SelectedItem is NewChatAccounts.Choice choice
                ? choice.ProfileDir ?? RelayAccounts.Default
                : RelayAccounts.Default;

            var wire = RelayAccounts.CliName(cli);
            _startButton.IsEnabled = false;
            // Once the request is on the wire the far desk will start it whatever
            // happens here, so the form cannot be allowed to move to another
            // machine and lose the answer: the Machine row is locked until the
            // result is shown, and the launch is never cancelled.
            _machineCombo.IsEnabled = false;
            _statusLine.Text = PeerLaunchCopy.Starting(machine);

            PeerLaunchResult result;
            try
            {
                result = await _peers!.LaunchAsync(machine, new PeerLaunchSpec(wire, folder, account), CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new PeerLaunchResult(false, RelayReplies.LaunchFailed, null, ex.GetType().Name);
            }

            _machineCombo.IsEnabled = true;
            _startButton.IsEnabled = true;

            // Nothing local is written: the last CLI, the last account and the
            // recent folders are this machine's, and no orb watch starts here —
            // the new session's orb comes from the far machine's own roster.
            _statusLine.Text = result.Ok
                ? PeerLaunchCopy.Launched(wire, machine)
                : PeerLaunchCopy.Explain(result.Code, result.Why, wire, machine);
        }

        // OpenClaw's Start: no launch-and-wait-for-a-hook the way a local
        // CLI needs — sessions.create either hands back a real session right
        // away or it doesn't, so there's no "launched but no orb yet" middle
        // state the way a terminal spawn has. What the watch still has to
        // wait for is the scan discovering the new session and building its
        // orb — SessionManager.OrbFor is the only place that orb is ever
        // created, so this never builds its own (see OrbFor's own comment on
        // why that would be a genuine duplicate).
        private async Task StartOpenClawClicked()
        {
            if (_agentCombo.SelectedItem is not AgentItem agent)
            {
                _statusLine.Text = "Choose an agent first.";
                return;
            }

            _startButton.IsEnabled = false;
            _statusLine.Text = "Starting…";

            var (session, failure) = StartOpenClawConversationForTests is { } seam
                ? await seam(agent.Id, CancellationToken.None)
                : await OpenClawSessions.StartConversationAsync(agent.Id, CancellationToken.None);

            _startButton.IsEnabled = true;

            if (session is null)
            {
                _statusLine.Text = failure ?? "Couldn't start the conversation.";
                return;
            }

            _statusLine.Text = "Conversation started with " + agent.Name + ". Waiting for its orb…";
            StartOpenClawWatch(session);
        }

        private void StartWatch(HashSet<string> priorIds, NewChatCli cli, string folder)
        {
            _watchOpenClawSessionId = null;
            _watchOpenClawSession = null;
            _watchPriorIds = priorIds;
            _watchCli = cli;
            _watchFolder = folder;
            _watchDeadlineUtc = DateTime.UtcNow.AddSeconds(20);

            ArmTimer();
        }

        private void StartOpenClawWatch(IRemoteChatSession session)
        {
            _watchCli = null;
            _watchFolder = null;
            _watchPriorIds = null;
            _watchOpenClawSessionId = session.SessionId;
            _watchOpenClawSession = session;
            _watchDeadlineUtc = DateTime.UtcNow.AddSeconds(20);

            ArmTimer();
        }

        private void ArmTimer()
        {
            _watchTimer?.Stop();
            _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watchTimer.Tick += OnWatchTick;
            _watchTimer.Start();
        }

        // Named rather than an inline lambda so a test can invoke it
        // directly with the (sender, EventArgs) shape a real DispatcherTimer
        // tick would use, the same pattern LocalCliChatSessionTests' own
        // comment describes for its debounce timer — no real ~20s wall-clock
        // wait needed to cover any branch below. Local-CLI and OpenClaw
        // watches are mutually exclusive (Start*Watch above clears the
        // other's fields), so exactly one of the two blocks below ever has
        // anything to check.
        internal void OnWatchTick(object? sender, EventArgs e)
        {
            if (_watchOpenClawSessionId is { } sessionId)
            {
                OnOpenClawWatchTick(sessionId);
                return;
            }

            if (_watchCli is not { } cli || _watchFolder is not { } folder || _watchPriorIds is not { } prior)
            {
                _watchTimer?.Stop();
                return;
            }

            var match = NewChatOrbWatch.FindMatch(CurrentStatuses(), prior, cli, folder);
            if (match is not null)
            {
                _statusLine.Text = NewChatLauncher.DisplayName(cli) + " is running — its orb should be on screen.";
                _watchTimer?.Stop();
                return;
            }

            if (DateTime.UtcNow >= _watchDeadlineUtc)
            {
                _statusLine.Text =
                    "Terminal opened; no orb yet — the CLI's hook may not be installed / trusted.";
                _watchTimer?.Stop();
            }
        }

        private void OnOpenClawWatchTick(string sessionId)
        {
            if (OrbForSession(sessionId) is { } orb)
            {
                ChatPanel.OpenFor(orb, _watchOpenClawSession!);
                _statusLine.Text = "Its orb should be on screen.";
                _watchTimer?.Stop();
                return;
            }

            if (DateTime.UtcNow >= _watchDeadlineUtc)
            {
                _statusLine.Text = "Conversation created; no orb yet — check your gateway connection.";
                _watchTimer?.Stop();
            }
        }

        // Test seam: sets the watch's own fields without going through
        // StartClicked's full launch flow, so OnWatchTick's branches can be
        // driven directly.
        internal void ArmWatchForTests(HashSet<string> priorIds, NewChatCli cli, string folder, DateTime deadlineUtc)
        {
            _watchOpenClawSessionId = null;
            _watchOpenClawSession = null;
            _watchPriorIds = priorIds;
            _watchCli = cli;
            _watchFolder = folder;
            _watchDeadlineUtc = deadlineUtc;
        }

        internal void ArmOpenClawWatchForTests(IRemoteChatSession session, DateTime deadlineUtc)
        {
            _watchCli = null;
            _watchFolder = null;
            _watchPriorIds = null;
            _watchOpenClawSessionId = session.SessionId;
            _watchOpenClawSession = session;
            _watchDeadlineUtc = deadlineUtc;
        }
    }
}
