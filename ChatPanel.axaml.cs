using System;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Orbweaver
{
    // A small conversation anchored to an orb: the last few turns, a line to
    // type in, and a mic. Opened by clicking an orb that represents a session
    // with no terminal to jump to — for those, this is where the click goes.
    //
    // One *transient* panel, reused. The rule used to be one panel full stop,
    // and the argument for it was that two panels would be two windows
    // competing to be the key window, each one's dismiss-on-deactivate closing
    // the other. Read again, that argument is about dismiss-on-deactivate and
    // not about windows: what it forbids is a second panel that hides the
    // instant it loses focus. It says nothing about one that has been told to
    // stay. So a pinned panel keeps its own window and opts out of Deactivated
    // entirely, and everything not pinned still shares the single transient
    // instance — "opening B closes A" remains correct by construction for
    // exactly the panels it was ever true of.
    //
    // Two invariants hold the registry together, and every static entry point
    // below is one of them being enforced somewhere. At most one unpinned
    // panel: that is the paragraph above. At most one panel per session id:
    // that is why OpenFor activates an existing pinned panel rather than
    // binding the transient to a conversation already on screen, which would
    // put one transcript in two windows that then disagree about it.
    //
    // What is worth keeping per session is the draft and the scroll position,
    // and those live in a dictionary — a window is not a storage mechanism, and
    // now that there can be several of them, one store keyed by session is what
    // keeps a draft with its conversation rather than with whichever window
    // happened to be typing it. The draft store isn't a nicety either: an
    // unpinned panel hides whenever you switch apps, so without it every
    // alt-tab would eat a half-typed sentence.
    public partial class ChatPanel : Window
    {
        // Every live panel, pinned or not, in the order they were built.
        //
        // A list rather than the old single field because "which panel" is now
        // a question with several answers — the transient one, the one showing
        // a given session, the one a given orb owns — and each of those is a
        // query over this rather than another field to keep in step with it.
        // Panels add themselves at construction and drop out when their window
        // closes; hiding the transient deliberately does not remove it, since
        // the whole point of the transient is that it is reused.
        private static readonly List<ChatPanel> Panels = new();

        // Whether this panel has been told to stay. Almost the entire feature
        // is this flag and the handful of places that read it: Deactivated,
        // HideFor, RepositionFor, the header's drag, and what the close button
        // means.
        private bool _pinned;

        // Bumped by every call to ScrollToEndAfterLayout, and read back by
        // whichever settle loop that call started. This panel is reused
        // across sessions (the class comment says why), so a settle loop
        // still watching layout from the *previous* bind must not go on
        // fighting the offset once a new one has started its own — the
        // generation check is what lets an in-flight loop notice it is stale
        // and unsubscribe rather than assuming it is still the only one.
        private int _scrollSettleGeneration;

        // The panel that dismiss-on-deactivate still applies to, if there is
        // one. Every caller that used to say "the panel" and mean the singleton
        // means this: an orb about to move under it, an arrangement animation,
        // a hide.
        internal static ChatPanel? Transient => Panels.FirstOrDefault(p => !p._pinned);

        // The panel showing a conversation, wherever it is. Distinct from
        // Transient because a pinned panel is still the one place that session
        // is on screen, and a caller asking "where is this conversation" must
        // not be answered with a different one.
        internal static ChatPanel? PanelFor(string sessionId) =>
            Panels.FirstOrDefault(p => p._session?.SessionId == sessionId);

        // For the calls that are genuinely about all of them — speech state and
        // the text-scale slider, both of which are one global number that every
        // open panel draws.
        internal static IReadOnlyList<ChatPanel> All => Panels;

        internal bool IsPinned => _pinned;

        // CB-198: an orb changing size moves its centre-to-edge gap, so the one
        // panel that is placed relative to its orb follows. A pinned panel is
        // left exactly where it was put, for the reason SetPinned gives.
        internal static void FollowOrbResize()
        {
            if (Transient is { IsVisible: true } panel) panel.Reposition();
        }

        private static readonly Dictionary<string, string> Drafts = new(StringComparer.Ordinal);

        private IRemoteChatSession? _session;

        private string? AskingKey() => (_session as IRemoteChatGatewayKey)?.GatewayKey;
        private OrbWindow? _owner;

        // Whether the header is currently wearing the orb's identity rather
        // than an agent's own. Only that case can be refreshed from the orb.
        private bool _borrowedIdentity;

        // Who is talking in this session when the transcript does not say.
        //
        // A box shared by reference with every TurnView, not a value copied
        // into each. The name is not always known when the panel binds — a
        // terminal session's title arrives with a later hook write, and the
        // panel opens on whatever the orb had at the time, which is often
        // nothing. Copied, that nothing was baked into every row already built
        // and the chips never appeared; boxed, filling it in later fills in the
        // rows that were waiting for it. Same one-shot-read mistake the header
        // made two commits ago, in a second place.
        //
        // IsRoom (CB-36) is what tells TurnView.SpeakerName whether Name is
        // safe to fall back to. ChatSpeaker.Resolve's own comment already
        // concedes that Name is the panel's title for a room — the channel,
        // not a speaker — because a room is not an agent and has no identity
        // to prefer instead. That answer is exactly right for what
        // ChatSpeaker was written for (the header, and a terminal session's
        // one-agent fallback) and exactly wrong for an unattributed turn in a
        // room, which OpenClawRoomChatSession.Rebuild deliberately built with
        // no Speaker to mean "we do not know who said this" — not "the
        // channel said this". TurnView reads this flag rather than asking the
        // session's type itself, so the one place a turn decides whether it
        // may borrow the sole speaker's name is a plain field next to the
        // name it would borrow.
        private sealed class Speaker { public string? Name; public bool IsRoom; }

        private readonly Speaker _soleSpeaker = new();

        // How much bigger or smaller than shipped this panel draws chat text.
        //
        // Boxed and shared with every TurnView for the same reason Speaker
        // above is: a bubble built before the last Cmd+ has to end up the same
        // size as one built after it. Copied into each row, only the rows built
        // since the keystroke would be right, and a transcript would fan out
        // into as many sizes as the user had pressed the key.
        private sealed class TextScale { public double Value = ChatZoom.Default; }

        private readonly TextScale _textScale = new();

        // The colour a reply is drawn in when the turn itself doesn't name one.
        //
        // A room's turns carry their own, because several agents are talking. In
        // every other panel exactly one agent is, and repeating its name on
        // every bubble would be noise — but its colour is not, so it comes from
        // the orb rather than from each message.
        private Color? _defaultBubble;

        private readonly ObservableCollection<TurnView> _turns = new();

        // Pictures pasted since the last send, waiting to go out with
        // whatever gets typed next. Not part of a session's own draft store
        // below — a paste is only ever meant for the message being composed
        // right now, so switching sessions clears it rather than saving it.
        private readonly ObservableCollection<PendingImage> _pendingImages = new();

        // What the bound session's CLI understands, so "/" in the box can
        // offer the same autocomplete the terminal itself would. Empty for a
        // session with no answer for IRemoteChatSlashCommands, which quietly
        // turns the whole feature off for it rather than needing a check at
        // every call site.
        //
        // Asked of the session on every keystroke rather than read once when
        // the panel binds. A local session knows its commands before the panel
        // opens, so caching looked free; a session on another machine has to be
        // *asked* what it can run, and the answer lands half a minute later. A
        // list captured at bind time was therefore permanently empty for the
        // one kind of session that most needed it — the panel had to be closed
        // and reopened to see anything, which nobody would guess.
        private IReadOnlyList<SlashCommand> SlashCommands =>
            (_session as IRemoteChatSlashCommands)?.SlashCommands ?? Array.Empty<SlashCommand>();

        // The suggestions currently shown, and which one Up/Down has landed
        // on. Kept as a plain list rather than something observable: the
        // popup is small and rebuilt wholesale on every keystroke or arrow
        // press anyway, so there is nothing an incremental update would save.
        private List<SlashCommand> _slashMatches = new();
        private int _slashSelected;

        // Distance from the orb's centre to the panel's near edge is
        // OrbSizing.ChatPanelGap(orb size) — it was a flat 34 that cleared the
        // 56pt orb with a small gap, and a CB-198 orb at 2x would sit on top of
        // a panel only 34 from its centre.

        // The size the XAML ships, captured before anything has been restored
        // over it. An agent with no saved size has to go *back* to this, not
        // keep whatever the previously bound agent was dragged to — one window
        // serves every session, so without this the first resize would silently
        // become the size of every panel opened after it.
        private readonly double _defaultWidth;
        private readonly double _defaultHeight;

        public ChatPanel()
        {
            InitializeComponent();

            // Drawn rather than typed — see SymbolMarks. The same gear the
            // background-job badge wears, which is the whole point of this
            // button's mark.
            AttachMark.Data = StreamGeometry.Parse(SymbolMarks.Gear);
            HeartChipMark.Data = StreamGeometry.Parse(SymbolMarks.Heart);
            RunOptionsChip.PointerPressed += RunOptionsChip_PointerPressed;

            _defaultWidth = Width;
            _defaultHeight = Height;

            _bubbleMenu = BuildBubbleMenu();

            Turns.ItemsSource = _turns;
            Attachments.ItemsSource = _pendingImages;

            // Wired once here rather than per bind: the handler reads
            // _readOnlyUrl, which ApplyComposerAffordances sets, so the
            // subscription never needs to change. Hover underline the same way
            // SettingsWindow's links do it — one link look for the app.
            ReadOnlyLink.PointerPressed += OnReadOnlyLinkPressed;
            ReadOnlyLink.PointerEntered += (_, _) =>
                ReadOnlyLink.TextDecorations = TextDecorations.Underline;
            ReadOnlyLink.PointerExited += (_, _) =>
                ReadOnlyLink.TextDecorations = null;

            // SessionLink is the live-session twin of ReadOnlyLink and opens the
            // same address, so it shares the handler and the one link look.
            SessionLink.PointerPressed += OnReadOnlyLinkPressed;
            SessionLink.PointerEntered += (_, _) =>
                SessionLink.TextDecorations = TextDecorations.Underline;
            SessionLink.PointerExited += (_, _) =>
                SessionLink.TextDecorations = null;

            // Bubbles size themselves off Scroll's actual width (see
            // TurnView.MaxBubbleWidth) rather than a fixed pixel cap, since
            // the panel is user-resizable now. Two hooks cover the two ways a
            // turn's width can go stale: the collection hook catches a turn
            // that didn't exist yet at the last resize, and SizeChanged
            // catches turns that were already on screen when the resize
            // happened.
            _turns.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is null) return;

                var width = Scroll.Bounds.Width;
                if (width <= 0) return;

                foreach (TurnView turn in e.NewItems) turn.AvailableWidth = width;
            };
            Scroll.SizeChanged += (_, _) =>
            {
                var width = Scroll.Bounds.Width;
                if (width <= 0) return;

                foreach (var turn in _turns) turn.AvailableWidth = width;
            };

            // Dismiss rather than HideNow: what the button means depends on
            // whether this panel was told to stay. See Dismiss.
            CloseButton.PointerPressed += (_, e) => { e.Handled = true; Dismiss(); };

            PinButton.PointerPressed += (_, e) => { e.Handled = true; TogglePin(); };

            // CB-267's box. Labels set once: they are PeerInbound's copy and do
            // not change per session.
            InboundAskLabel.Text = PeerInbound.ButtonLabel;
            InboundConfirmLabel.Text = PeerInbound.ConfirmLabel;
            InboundCancelLabel.Text = "Cancel";
            InboundResendLabel.Text = ResendLabel;
            InboundAskButton.PointerPressed += (_, e) => { e.Handled = true; StepInbound(InboundInput.Ask); };
            InboundCancelButton.PointerPressed += (_, e) => { e.Handled = true; StepInbound(InboundInput.Cancel); };
            InboundConfirmButton.PointerPressed += (_, e) => { e.Handled = true; _ = ConfirmInboundAsync(); };
            InboundResendButton.PointerPressed += (_, e) => { e.Handled = true; _ = ResendInboundAsync(); };

            // A pinned panel is a window the user placed, so it has to be
            // movable, and with WindowDecorations="None" there is no title bar
            // to drag it by — the header is the title bar.
            //
            // Only when pinned. An unpinned panel is recentred on its orb by
            // RepositionFor every time the arrangement moves, so a drag would
            // be silently undone somewhere between the next frame and the next
            // scan; offering a gesture that gets reverted is worse than not
            // offering it. Presses the header's own controls claimed are
            // already marked handled and never arrive here.
            HeaderRow.PointerPressed += (_, e) =>
            {
                if (!_pinned) return;

                BeginMoveDrag(e);
            };

            // The portrait opens at four times the size, centred on itself.
            // Handled so the click doesn't also travel on to anything behind it
            // — and handled before the null check now, not after. The portrait
            // is a control whether or not a picture has arrived in it yet, and
            // the header behind it starts a window drag: leaving an empty
            // portrait's press unhandled would make one gesture mean "enlarge
            // this" or "move the window" depending on whether a download had
            // finished, which is the worse of the two inconsistencies.
            AvatarBox.PointerPressed += (_, e) =>
            {
                e.Handled = true;

                if (_avatar is null) return;

                var centre = AvatarBox.Bounds.Center;
                AvatarPopup.Show(_avatar, this.PointToScreen(new Point(
                    AvatarBox.Bounds.X + centre.X,
                    AvatarBox.Bounds.Y + centre.Y)));
            };
            SendButton.PointerPressed += (_, e) => { e.Handled = true; Send(); };

            // The click asks the same question the button's visibility does
            // (StopOffered), so a press that races a state change — or one that
            // arrives with nothing bound — reaches no Cancel. What an interrupt
            // means, and CanInterrupt going false on the click itself, stay the
            // session's job.
            StopButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (StopOffered(_session)) _session!.Cancel();
            };

            // Fire and forget, like the click on the orb it shares its
            // implementation with. Nothing is awaited and nothing about the panel
            // changes: what the attach produces arrives through the ordinary
            // scan, which is also what eventually gives this session a pane and
            // turns the composer into an ordinary one.
            AttachButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                (_session as IRemoteChatElsewhere)?.OpenElsewhere();
            };
            MicButton.PointerPressed += (_, e) => { e.Handled = true; _owner?.ToggleRecording(); };
            SpeakButton.PointerPressed += (_, e) => { e.Handled = true; SpeakLatest(); };
            MuteButton.PointerPressed += (_, e) => { e.Handled = true; AudioMute.Toggle(); };

            // Tunnel, not bubble. TextBox handles Return in a class handler that
            // runs before any instance handler on the same element, so a plain
            // KeyDown += would fire after the newline had already been inserted.
            // Getting there first is the whole point.
            Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
            Input.TextChanged += (_, _) => UpdateSlashSuggestions();

            // On the window rather than on Input, and ahead of it: a selection
            // in a bubble has to be copyable no matter which control the
            // keystroke was aimed at, and the composer would otherwise eat the
            // gesture whether or not it had anything to copy. See OnCopyKeyDown.
            AddHandler(KeyDownEvent, OnCopyKeyDown, RoutingStrategies.Tunnel);

            KeyDown += OnPanelKeyDown;

            // Tunnel, and on the window rather than on the composer. The
            // composer holds focus almost the whole time this panel is open, so
            // a bubbling handler would be asking the TextBox's own key handling
            // for permission to run — and the one gesture that has to work
            // whatever is focused is the one that makes the text readable.
            AddHandler(KeyDownEvent, OnZoomKeyDown, RoutingStrategies.Tunnel);

            // The size is a setting, so a panel opens at whatever the last one
            // was left at. Done here rather than in Bind because it is about
            // the window, not about which session is in it.
            ApplyTextScale();

            Opened += (_, _) =>
            {
                // Orbs follow you across Spaces; a panel that didn't would be
                // stranded behind you mid-sentence.
                this.ShowOnAllSpaces();

                // A no-op in practice — the shared class patch is installed by
                // whichever orb you clicked to get here — but kept for symmetry
                // with OrbFlyout, and correct if that ever stops being true.
                this.AcceptFirstClick();
            };

            // Deferred and re-checked: clicking our own mic or close button can
            // deactivate the window for an instant, and a recording in progress
            // must not be orphaned by a click elsewhere — the same rule
            // ScheduleFlyoutHide already follows for the arc.
            Deactivated += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (IsActive) return;
                if (_owner?.IsRecording == true) return;

                // The enlarged portrait is this panel's own window: opening it
                // deactivates the panel, and closing the panel out from under it
                // would be a strange answer to "show me that picture".
                if (AvatarPopup.IsOpen) return;

                // Same argument, one control further out: a context menu opened
                // on a message bubble is its own window too, and closing the
                // panel behind it would take the menu with it.
                if (ContextMenuIsOpen) return;

                // The fourth carve-out, and the only one that is permanent
                // rather than about this instant. The three above are "not
                // yet"; this one is "not this panel". A pinned panel was told
                // to stay, and dismiss-on-deactivate is the whole of what
                // pinning turns off — see the class comment on why that is the
                // rule the old singleton was really enforcing.
                if (_pinned) return;

                HideNow();
            }, DispatcherPriority.Background);

            // The window's Width/Height are the resize target now (see the
            // XAML comment), so they're known synchronously and Reposition()
            // is called explicitly wherever the size or the orb changes —
            // Bind and RepositionFor — rather than off SizeChanged. Doing it
            // off SizeChanged too would refire Reposition() on every pixel of
            // a user's own resize drag and recentre the window on the orb out
            // from under their cursor.
            ResizeN.PointerPressed += (_, e) => BeginResize(WindowEdge.North, e);
            ResizeS.PointerPressed += (_, e) => BeginResize(WindowEdge.South, e);
            ResizeE.PointerPressed += (_, e) => BeginResize(WindowEdge.East, e);
            ResizeW.PointerPressed += (_, e) => BeginResize(WindowEdge.West, e);
            ResizeNE.PointerPressed += (_, e) => BeginResize(WindowEdge.NorthEast, e);
            ResizeNW.PointerPressed += (_, e) => BeginResize(WindowEdge.NorthWest, e);
            ResizeSE.PointerPressed += (_, e) => BeginResize(WindowEdge.SouthEast, e);
            ResizeSW.PointerPressed += (_, e) => BeginResize(WindowEdge.SouthWest, e);
            PointerMoved += OnResizePointerMoved;
            PointerReleased += OnResizePointerReleased;

            // CB-111: a pinned panel's place is worth remembering across a
            // restart, and BeginMoveDrag (the header's drag gesture — see
            // HeaderRow.PointerPressed above) hands the whole gesture to the
            // platform rather than raising anything this class could hook at
            // the end of a drag the way OnResizePointerReleased does for a
            // resize. PositionChanged is what the platform gives back instead,
            // and it fires for every kind of move — Reposition() included — so
            // the guard is what keeps this from writing a save for a panel
            // that only followed its orb. OrbweaverSettings.
            // SetPinnedChatPanelPosition already no-ops when the position
            // hasn't actually changed, so a native drag's stream of identical
            // in-between events costs nothing once it settles.
            PositionChanged += (_, _) =>
            {
                if (!_pinned || _owner is not { PositionKey.Length: > 0 } owner) return;

                OrbweaverSettings.SetPinnedChatPanelPosition(owner.PositionKey, Position.X, Position.Y);
            };

            // See NwSeCursor/NeSwCursor: no StandardCursorType member draws
            // as an actual diagonal on this platform, so these two corner
            // pairs get a cursor bitmap built by hand instead.
            ResizeNW.Cursor = NwSeCursor;
            ResizeSE.Cursor = NwSeCursor;
            ResizeNE.Cursor = NeSwCursor;
            ResizeSW.Cursor = NeSwCursor;

            // Reaching the top asks for the page before. A threshold rather than
            // exactly zero, because a trackpad flick lands a few pixels short of
            // the top as often as it lands on it.
            Scroll.ScrollChanged += (_, _) =>
            {
                // Only when there is something to scroll. ScrollChanged fires on
                // extent and viewport changes too, and a transcript shorter than
                // the panel sits at offset zero forever — so this asked for the
                // page before, which grew the extent, which fired it again, and
                // walked the entire backlog the instant the orb was clicked.
                if (Scroll.Extent.Height <= Scroll.Viewport.Height + 8) return;
                if (Scroll.Offset.Y > 24) return;

                _ = LoadOlderAsync();
            };

            // Whatever closes this window, the registry stops holding it. On
            // the event rather than beside the one Close() call, because a
            // stale entry is not a visible bug — it is Transient or PanelFor
            // handing out a panel whose window is gone, which shows up later
            // and somewhere else.
            Closed += (_, _) => { Panels.Remove(this); AudioMute.Changed -= ApplyMuted; };

            // Every open panel mirrors the one global flag, so the tray, the
            // hotkey and a click on any panel's button all leave every header
            // saying the same thing. Unsubscribed beside Panels.Remove: a
            // closed panel left on a static event is a leak that also keeps
            // repainting a window that is gone.
            AudioMute.Changed += ApplyMuted;

            ApplyPinAffordance();

            // Last, so a panel is never in the registry before it is built.
            Panels.Add(this);
        }

        // Any visible panel showing this conversation, not just the transient
        // one. The callers are asking "is this session already on screen" —
        // dictation looking for somewhere to land, a backlog test — and a
        // pinned panel is as much on screen as the transient is.
        public static bool IsOpenFor(string sessionId) =>
            Panels.Any(p => p.IsVisible && p._session?.SessionId == sessionId);

        public static void OpenFor(OrbWindow orb, IRemoteChatSession session)
        {
            // Already pinned somewhere: raise that window rather than binding
            // this conversation into the transient as well. Two windows on one
            // transcript is the second invariant in the class comment, and it
            // is not a tidiness rule — both would subscribe to the session, and
            // the one you were not looking at would answer a permission prompt
            // out from under the one you were.
            if (PanelFor(session.SessionId) is { _pinned: true } pinned)
            {
                // The orb's arc stays available. Pinning gave it back (see
                // Pin), and clicking the orb again must not take it away for a
                // panel that is not sitting in that space.
                orb.SetChatOpen(false);
                pinned.Activate();
                return;
            }

            var panel = Transient ?? new ChatPanel();
            panel.Bind(orb, session);
        }

        // Used when the orb is about to move under the panel — an arrangement
        // animation. The transient only, deliberately: a pinned panel is
        // somewhere the user put it, and an orb sliding across the screen has
        // no business taking it along. An orb that is actually going away is a
        // different question and asks CloseFor instead.
        public static void HideFor(string sessionId)
        {
            if (Transient is not { } panel) return;
            if (panel._session?.SessionId != sessionId) return;

            panel.HideNow();
        }

        // The orb this conversation belongs to has gone. Unlike HideFor this
        // reaches a pinned panel too — a chat with no session behind it any
        // more is not something pinning should be able to keep on screen.
        public static void CloseFor(string sessionId)
        {
            if (PanelFor(sessionId) is not { } panel) return;

            panel.Dismiss();
        }

        public static void RepositionFor(OrbWindow orb)
        {
            if (Transient is not { IsVisible: true } panel) return;
            if (!ReferenceEquals(panel._owner, orb)) return;

            panel.Reposition();
        }

        // CB-111: whether some panel is already the pinned one for this
        // PositionKey. SessionManager's own RestoreOrbPosition guards its
        // sibling case the same way — two orbs sharing one key, which only
        // happens for two titled sessions in the same directory with the same
        // title, since PositionKeyFor gives every untitled session its own —
        // and this is the same guard for a pinned panel: whichever orb gets
        // there first in a scan wins the one saved spot, rather than both
        // popping a copy of it.
        internal static bool IsPinnedFor(string positionKey) =>
            Panels.Any(p => p._pinned && p._owner?.PositionKey == positionKey);

        // CB-111's startup path: bring back a panel that was pinned before the
        // app last quit, at the place SessionManager read out of settings.
        // Builds and binds a fresh window exactly the way a click on the orb
        // would (OpenFor's transient-reuse rule does not apply here — a
        // restored panel is pinned from the moment it exists, never the
        // shared unpinned one), but without taking focus: see Bind's activate
        // parameter for why a restart must not steal it.
        //
        // Position is set, then Pin() records it, rather than the other order
        // — Bind() already ran its own Reposition() to put the window near
        // the orb, and pinning before moving would save that transient spot
        // instead of the one actually being restored.
        internal static void RestorePinned(OrbWindow orb, IRemoteChatSession session, PixelPoint position)
        {
            if (PanelFor(session.SessionId) is not null) return;

            var panel = new ChatPanel();
            panel.Bind(orb, session, activate: false);

            // Clamped to this panel's own size via ChatPanelPlacement, which
            // already owns this maths for a freshly placed panel — see
            // ClampSavedPosition's own comment on why a panel needs its own
            // width and height here rather than an orb's fixed footprint.
            if (panel.Screens.ScreenFromPoint(position) is { } screen)
            {
                var scale = screen.Scaling;
                var size = new PixelSize(
                    (int)(panel.Width * scale), (int)(panel.Height * scale));

                position = ChatPanelPlacement.ClampSavedPosition(position, size, screen.WorkingArea);
            }

            panel.Position = position;
            panel.Pin();
        }

        // Speech is global rather than per-orb, so the panel is told about it
        // the same way the flyout is, from one place — and now every panel is,
        // because the button says whether the one voice is talking and they
        // would otherwise disagree about it.
        public static void SetSpeakState(TextToSpeech.SpeakState state)
        {
            foreach (var panel in Panels) panel.ApplySpeakState(state);
        }

        public static void SetRecording(OrbWindow orb, bool recording)
        {
            if (OwnedBy(orb) is not { } panel) return;

            panel.MicFill.Fill = recording ? RecordingFill : IdleFill;
        }

        // Dictation lands here rather than being sent. Same rule
        // TerminalFocuser.SendText has always followed and explains at its own
        // definition: transcription is a typing aid, and it does not get to
        // decide that you meant it.
        //
        // Takes the orb now. It used to mean "the one panel", which was
        // unambiguous while there was one; with several open, words spoken at
        // one orb landing in whichever panel happened to be transient is the
        // worst kind of wrong — silent, and in someone else's message box.
        public static void AppendToInput(OrbWindow orb, string text)
        {
            if (OwnedBy(orb) is not { } panel) return;

            var existing = panel.Input.Text ?? "";
            panel.Input.Text = existing.Length == 0 ? text : existing.TrimEnd() + " " + text;
            panel.Input.CaretIndex = panel.Input.Text.Length;
            panel.Input.Focus();
        }

        // The visible panel a given orb opened, pinned or not. One orb shows
        // one session and one session has one panel, so this is a lookup rather
        // than a choice — but it is written once here because three callers
        // used to spell it out and all three had to be kept in step.
        private static ChatPanel? OwnedBy(OrbWindow orb) =>
            Panels.FirstOrDefault(p => p.IsVisible && ReferenceEquals(p._owner, orb));

        private static readonly IBrush IdleFill = new SolidColorBrush(Color.Parse("#E0202024"));
        private static readonly IBrush RecordingFill = new SolidColorBrush(Color.Parse("#E0D93B3B"));
        private static readonly IBrush SpeakActiveFill = new SolidColorBrush(Color.Parse("#E04A90D9"));
        private static readonly IBrush SpeakPreparingFill = new SolidColorBrush(Color.Parse("#E0B8860B"));

        // Connected is green rather than the speak button's blue. The two dots
        // sit inches apart and meant different things in the same colour: blue
        // on the button is "this is playing right now", blue on the portrait was
        // "this conversation is reachable". Green is what a presence dot is
        // everywhere else, so it needs no explanation, and it leaves blue to
        // mean one thing again.
        //
        // #00AF5F rather than a green picked by eye — it is the app's green
        // already, the value Claude Code's own /color green resolves to (see
        // OrbWindow's palette and ClaudeDesktopColors), so a connected dot and
        // a green orb are the same green.
        private static readonly IBrush ConnectedFill = new SolidColorBrush(Color.Parse("#E000AF5F"));

        private void Unbind()
        {
            if (_session is null) return;

            Drafts[_session.SessionId] = Input.Text ?? "";

            // Not carried to Drafts with the text: a picture pasted for one
            // conversation attached to whichever session happens to be open
            // next would be a silent misdirection rather than a convenience.
            _pendingImages.Clear();
            Attachments.IsVisible = false;

            _session.TurnAdded -= OnTurnAdded;
            _session.TurnUpdated -= OnTurnUpdated;
            _session.StateChanged -= OnStateChanged;
            if (_session is OpenClawChatSession gatewayChat) gatewayChat.TurnsRemoved -= OnTurnsRemoved;

            if (_session is IRemoteChatBacklog previous)
            {
                previous.HistoryReplaced -= OnHistoryReplaced;
                previous.HistoryPrepended -= OnHistoryPrepended;
            }

            if (_session is IRemoteChatPrompts prompts) prompts.PromptChanged -= OnPromptChanged;

            if (_session is IRemoteChatFetchWait waited) waited.WaitChanged -= OnWaitChanged;
            HideWait();

            // CB-199. The panel is reused across orbs, so a session left
            // subscribed would go on redrawing the composer of whichever
            // conversation is open next — a refusal from one cloud session
            // taking the box away from an unrelated one.
            if (_session is IRemoteChatReadOnly wasReadOnly) wasReadOnly.ReadOnlyChanged -= OnReadOnlyChanged;
            if (_session is IRemoteChatInterrupt wasInterruptible) wasInterruptible.InterruptChanged -= OnInterruptChanged;
            if (_session is IRemoteChatRunOptions wasRunnable) wasRunnable.RunOptionsChanged -= OnRunOptionsChanged;

            // The flyout belongs to the conversation it was built for.
            _runFlyout?.Hide();
            _runFlyout = null;
            if (_session is IRemoteChatPeerInbound wasInbound) wasInbound.InboundChanged -= OnInboundChanged;

            // Nobody is looking any more, so its live loop stops.
            // It does not stop a turn: closing a window should not cancel work
            // somebody asked for. Concrete type, following the RemoteControl
            // precedent below — one caller does not earn an interface.
            if (_session is ClaudeCloudChatSession previousCloud) previousCloud.PanelClosed();

            if (_session is IRemoteChatMachine wasNamed) wasNamed.MachineChanged -= OnMachineChanged;

            if (_session is RemoteControlChatSession previousRemote)
            {
                // A live view is the one thing here that costs something while
                // nobody is looking: it holds a subscription on the other
                // machine's Buddy, which keeps it watching that session's
                // transcript. So the panel closing says so, rather than leaving
                // it to lapse.
                //
                // Told on the panel rather than in Dispose because these
                // sessions are deliberately never disposed: a remote
                // conversation outlives its orb, since there is no file on this
                // machine to rebuild it from. See _remoteChats in SessionManager.
                previousRemote.PanelClosed();
            }

            // The last good name is per session, not per panel. The transient
            // panel outlives the sessions bound into it and so does this box,
            // so leaving it set meant the *next* conversation inherited it —
            // and because "we already knew a name" beats "we do not know one
            // yet", a session whose title had not arrived would wear the
            // previous session's initials on every bubble rather than none.
            // Wrong is worse than absent here: the chip is there to say who is
            // talking.
            _soleSpeaker.Name = null;
            _soleSpeaker.IsRoom = false;

            // An OpenClaw conversation is now allowed to be let go of — CB-92.
            // Nothing here disposes anything: this says that no window is
            // showing this transcript any more, which is what starts the clock
            // on giving its decoded pictures, and eventually the transcript
            // itself, back. Last, so everything above still runs against a
            // session this panel is provably finished with.
            OpenClawSessions.PanelClosed(_session);
        }

        // activate is false only for CB-111's startup restore: a panel that
        // was pinned before the app last quit is shown again where it was
        // left, but taking keyboard focus for it would mean every restart
        // stealing focus into an old conversation, one steal per restored
        // panel, ending on whichever happened to bind last. An ordinary open
        // — a click on an orb — always wants the focus, which is what every
        // other caller still gets by leaving this at its default.
        private void Bind(OrbWindow orb, IRemoteChatSession session, bool activate = true)
        {
            Unbind();

            // Tell the orb that is losing the panel. Only HideNow used to clear
            // this, and rebinding skips it — so clicking a second orb left the
            // first believing its panel was still open, and its hover flyout
            // never appeared again for the life of the process.
            if (_owner is not null && !ReferenceEquals(_owner, orb)) _owner.SetChatOpen(false);

            _owner = orb;
            _session = session;

            // Somebody is looking at this conversation, so it is not a
            // candidate for release while the window is up — and binding is
            // also the moment the set of open conversations changed, which is
            // when the sweep for the others is worth running (CB-92).
            OpenClawSessions.PanelOpened(session);

            // Set before RefreshSoleSpeaker/turn construction below, so every
            // TurnView built for this session — including the ones the
            // History loop is about to build — sees the right answer from its
            // first read rather than a stale one from whatever was bound
            // before it. See the Speaker.IsRoom comment for what this gates.
            //
            // Read off the optional interface rather than a concrete type
            // check against OpenClawRoomChatSession, the same reason every
            // other optional capability here (IRemoteChatMachine,
            // IRemoteChatFetchWait, …) is asked for rather than switched on:
            // a test's FakeChatSession can then stand in for a room by
            // implementing IRemoteChatRoom itself, which a sealed concrete
            // type it is not could never satisfy.
            _soleSpeaker.IsRoom = (session as IRemoteChatRoom)?.IsRoom == true;

            // Whatever this agent's panel was last dragged to. Before the
            // transcript is built and before Reposition(), because the height
            // decides whether the panel goes above or below the orb.
            ApplySavedSize(orb);

            session.TurnAdded += OnTurnAdded;
            session.TurnUpdated += OnTurnUpdated;
            if (session is OpenClawChatSession gateway) gateway.TurnsRemoved += OnTurnsRemoved;
            session.StateChanged += OnStateChanged;

            if (session is RemoteControlChatSession remote)
            {
                // Re-opens the live view if this panel closed it earlier. Cheap
                // when it is already open and nothing at all in messaging mode.
                remote.PanelOpened();
            }

            // The backlog usually lands a moment after the panel opens, so the
            // transcript has to be able to be replaced under it rather than only
            // appended to. Optional on purpose — a session that only ever
            // appends never raises it.
            if (session is IRemoteChatBacklog loader)
            {
                loader.HistoryReplaced += OnHistoryReplaced;
                loader.HistoryPrepended += OnHistoryPrepended;
            }

            if (session is IRemoteChatPrompts prompts) prompts.PromptChanged += OnPromptChanged;

            // Only the mirror has a wait worth drawing; everything else answers
            // fast enough that a spinner would flicker. Optional for exactly
            // that reason — see IRemoteChatFetchWait.
            if (session is IRemoteChatFetchWait waiting)
            {
                waiting.WaitChanged += OnWaitChanged;
                ShowWait(waiting);
            }
            else
            {
                HideWait();
            }

            // The roster usually answers after the panel is already open, so the
            // machine's name arrives late and the chip has to be redrawn when it
            // does.
            if (session is IRemoteChatMachine named) named.MachineChanged += OnMachineChanged;

            // CB-199: whether the box is there, and whether Stop is, can both
            // change while the panel is open — a cloud session turns read-only
            // the moment the server refuses it, and a reply starts and ends on
            // the roster's schedule rather than the panel's. Both are re-read by
            // ApplyComposerAffordances below for the initial state.
            if (session is IRemoteChatReadOnly readOnlyNow) readOnlyNow.ReadOnlyChanged += OnReadOnlyChanged;
            if (session is IRemoteChatInterrupt interruptible) interruptible.InterruptChanged += OnInterruptChanged;
            if (session is IRemoteChatRunOptions runnable) runnable.RunOptionsChanged += OnRunOptionsChanged;
            ApplyRunOptions(session);

            // CB-267: the offer comes and goes under an open panel — the
            // transcript's mode row lands after bind, settings.json is edited
            // by hand — and a half-finished confirmation belongs to the session
            // it was started for, so a rebind starts the box over.
            _inboundStage = InboundStage.Offer;
            if (session is IRemoteChatPeerInbound inboundNow) inboundNow.InboundChanged += OnInboundChanged;

            // Somebody is looking, which is what licenses the one transcript read
            // the session makes when a turn finishes (busy to idle on the roster).
            if (session is ClaudeCloudChatSession cloud) cloud.PanelOpened();

            // "Nova — wtvamp" is built as name plus place, so it splits back
            // into the two lines the header now has. A name with no place (an
            // agent's own main session) simply leaves the second line empty.
            ApplyTitle();
            RefreshSoleSpeaker();

            // Read off the orb rather than from the session, so the panel and
            // the badge on the thing that was clicked cannot disagree — the
            // same reason the header takes its colour and letter from there.
            // The kind, and — for a session that is quiet in a way worth naming —
            // what it is waiting for. Read off the orb rather than re-derived, so
            // the chip and the thing that was clicked cannot disagree; the same
            // reason KindLabel is read from there rather than from the status.
            //
            // "needs input" is the daemon's own phrase, so the chip, the orb's
            // tooltip and `claude agents` all say the same words about the same
            // session.
            var kind = orb.KindLabel;
            var presence = orb.PresenceLabel;

            // Gated on the kind, not on either: a presence word without a kind
            // cannot happen — both marks belong to background sessions, and every
            // one of those is badged — and a chip that made room for the
            // impossible case would be a claim about this app that is not true.
            KindChip.IsVisible = kind is not null;
            ApplyKindChip(orb.KindGlyphText, orb.KindMarkData, kind, presence,
                (_session as IRemoteChatMachine)?.MachineName);

            ApplyHeartbeat(orb.IsHeartbeat);

            _defaultBubble = orb.AccentColor;

            ApplyAvatar(session.SessionId);
            OnStateChanged(session.State);

            _turns.Clear();
            foreach (var turn in session.History) _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker, Adopt, _textScale, AskingKey()));

            HideSlashSuggestions();

            Input.Text = Drafts.GetValueOrDefault(session.SessionId, "");
            MicButton.IsVisible = OrbweaverSettings.VoiceInputEnabled;
            ApplySpeakState(TextToSpeech.State);
            ApplyMuted(AudioMute.IsMuted);

            // The box stays enabled even when sending won't work, and says why
            // on itself instead. A disabled box can't be pasted into or drafted
            // in, and SendAsync explains itself in the transcript anyway.
            ApplyComposerAffordances(session);
            ApplyPrompt();

            Reposition();

            if (!IsVisible) Show();

            // Show() then Activate(): an accessory app can be activated
            // programmatically or by a click on one of its windows, and this one
            // is opened by exactly such a click. Focus is taken on Activated
            // rather than by WaitForOwnActivation, which sleeps the UI thread up
            // to 600ms — fine at the tail of a TerminalFocuser call, not between
            // a click and a window appearing.
            if (activate)
            {
                Activate();
                Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Input);
            }

            // Unconditionally, not the pinned-only rule — this used to be
            // ScrollToEndIfPinned and that is why a panel sometimes opened
            // halfway up a conversation.
            //
            // The transient panel is reused across orbs (the class comment
            // says why: two panels that both hide on deactivate would fight
            // over being the key window), so the scroll position this instance
            // is carrying belongs to whichever session you had open last.
            // Asking whether *that* offset is at the bottom is asking a
            // question about a transcript that is no longer on screen: scroll
            // up in one chat, click a different orb, and the answer is "no",
            // so the new chat opens at the old offset with the newest message
            // somewhere below the fold.
            //
            // Same reasoning as OnHistoryReplaced: a transcript that was just
            // loaded wholesale has no read position worth preserving, and the
            // newest turn is the one you clicked the orb to read.
            ScrollToEndAfterLayout();
        }

        // What the box says, and whether there is a button beside it — one method
        // because they are two halves of one answer, and reading them from two
        // places is how they came to disagree in the first place.
        //
        // Both are re-read wherever the other was: at bind, and when a panel's
        // history is replaced wholesale (which is what a remote session
        // upgrading to a live view looks like). A local session's own transition
        // — parked, then attached, then typeable — arrives through the scan
        // rather than through the panel, and shows up here the next time the
        // panel is opened. Worth stating rather than papering over with a timer:
        // the attach opens a terminal the user is looking at, so the panel is not
        // where they are waiting for the answer.
        private void ApplyComposerAffordances(IRemoteChatSession? session)
        {
            Input.Watermark = (session as IRemoteChatComposer)?.ComposerHint ?? "Message…";
            AttachButton.IsVisible = (session as IRemoteChatElsewhere)?.CanOpenElsewhere ?? false;

            // A session that can be read and not written to loses the box
            // entirely, and gets a sentence where it was.
            //
            // **Hidden, not disabled** — the opposite of what the watermark
            // above does for a session that merely cannot be typed into *yet*.
            // A cloud session is written to through
            // POST /v1/code/sessions/{id}/events (measured 2026-09-28), so a
            // live one keeps its box; it turns read-only only once it has ended,
            // been deleted, or refused this login, and none of those will take
            // a reply later. A box that accepts a paragraph and only then admits
            // there is nowhere for it to go has already lost the paragraph,
            // which is CB-59's rule at its sharpest. That can now happen *while
            // the panel is open*, which is why this also runs on
            // ReadOnlyChanged rather than only at bind.
            //
            // CB-266: a Claude Desktop session is the second thing that flips
            // while the panel is up — read-only until its chat is put on Remote
            // Control (or "Show cloud sessions" is turned on), sendable after, and
            // read-only again if the bridge ends or the API refuses it. Nothing
            // here is Desktop-specific: LocalCliChatSession raises
            // ReadOnlyChanged and this redraws, so the panel is never rebound.
            //
            // The hint is kept rather than dropped with the box, because it says
            // where the session *can* be replied to. Hiding the box and
            // explaining nothing would leave a panel that looks truncated.
            var readOnly = session is IRemoteChatReadOnly { IsReadOnly: true };

            ComposerRow.IsVisible = !readOnly;
            ReadOnlyBox.IsVisible = readOnly;
            ReadOnlyNote.Text = readOnly
                ? (session as IRemoteChatComposer)?.ComposerHint ?? ""
                : "";

            // ...and the way to where it can be replied to. Null means there is
            // nowhere, and then the sentence stands on its own rather than a
            // link to nothing sitting beside it.
            _readOnlyUrl = readOnly ? (session as IRemoteChatReadOnly)?.ReplyUrl : null;
            ReadOnlyLink.IsVisible = _readOnlyUrl is not null;
            ReadOnlyLink.Text = _readOnlyUrl is null ? "" : "Open in your browser";

            // A live session has the same address and needs the same way to it
            // (CB-199 hid the link along with the box). One link at a time: this
            // one while the composer is up, ReadOnlyLink's while it is not.
            _sessionUrl = SessionLinkUrl(session);
            SessionLink.IsVisible = _sessionUrl is not null;
            SessionLink.Text = _sessionUrl is null ? "" : "Open in your browser";

            ApplyStop(session);
            ApplyInbound(session);
        }

        // Stop is shown only while the session says pressing it would stop
        // something, and never over a read-only one. Hidden rather than
        // disabled, for CB-59's reason: a greyed Stop over an idle session reads
        // as "stuck". The read-only half is already true of the row it sits in;
        // it is stated here too so the button's own visibility says the true
        // thing rather than relying on its parent to hide a lie.
        private void ApplyStop(IRemoteChatSession? session) => StopButton.IsVisible = StopOffered(session);

        // The one rule for both the button and its click. Static and pure, so
        // each arm is a unit case rather than a panel to build — including the
        // ones no real transport produces today, like an interruptible session
        // that is not read-only-capable at all.
        internal static bool StopOffered(IRemoteChatSession? session) =>
            session is IRemoteChatInterrupt { CanInterrupt: true }
            && session is not IRemoteChatReadOnly { IsReadOnly: true };

        // Raised on the UI thread by contract (see IRemoteChatReadOnly), and only
        // by the session currently bound: Unbind takes both subscriptions off.
        private void OnReadOnlyChanged() => ApplyComposerAffordances(_session);

        private void OnInterruptChanged() => ApplyStop(_session);

        // --- CB-270 Stage 4: run controls --------------------------------------

        private void OnRunOptionsChanged() => ApplyRunOptions(_session);

        // The chip says what the conversation is running on; a click opens the
        // flyout. Hidden whenever the session offers nothing, which is also what
        // closes a flyout left open when the options go away under it.
        private void ApplyRunOptions(IRemoteChatSession? session)
        {
            var options = (session as IRemoteChatRunOptions)?.RunOptions;

            RunOptionsChip.IsVisible = options is not null;

            if (options is null)
            {
                _runFlyout?.Hide();
                return;
            }

            RunOptionsChipText.Text = options.ChipText;
            // The whole text in the tip as well, since a long model name can be cut
            // by the column's width.
            ToolTip.SetTip(RunOptionsChip, options.ChipText + "\n" + OpenClawRunText.ChipTip);

            if (_runFlyout is not null) _runFlyout.Content = BuildRunFlyout((IRemoteChatRunOptions)session!, options);
        }

        private Flyout? _runFlyout;

        // The controls of the flyout, kept so a test can reach what a real person
        // would click without a pointer. Rebuilt from the options every time they
        // change; _runApplying holds the handlers off while it is being set.
        internal ComboBox? RunModelBox { get; private set; }
        internal ComboBox? RunThinkingBox { get; private set; }
        internal ToggleSwitch? RunFastSwitch { get; private set; }
        internal Button? RunFastDefault { get; private set; }
        internal ComboBox? RunLimitBox { get; private set; }
        internal TextBlock? RunStatus { get; private set; }
        private bool _runApplying;

        private void RunOptionsChip_PointerPressed(object? sender, PointerPressedEventArgs e) => OpenRunOptions();

        internal void OpenRunOptions()
        {
            if (_session is not IRemoteChatRunOptions run || run.RunOptions is not { } options) return;

            _runFlyout ??= new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            _runFlyout.Content = BuildRunFlyout(run, options);
            _runFlyout.ShowAt(RunOptionsChip);
        }

        internal Control BuildRunFlyout(IRemoteChatRunOptions run, RemoteRunOptions options)
        {
            _runApplying = true;
            try
            {
                var panel = new StackPanel { Spacing = 8, MinWidth = 240 };

                // Model. "Default" first when it can be reset, so it is the same
                // gesture as choosing any other.
                RunModelBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                if (options.CanResetToDefault) RunModelBox.Items.Add(new ComboBoxItem { Content = OpenClawRunText.DefaultLabel, Tag = null });
                foreach (var m in options.Models) RunModelBox.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id });
                RunModelBox.IsEnabled = !options.Locked;
                if (options.Locked) ToolTip.SetTip(RunModelBox, OpenClawRunText.LockedModelTip);
                // The handler before the selection, so setting the selection from
                // the options is held off by _runApplying like any other.
                RunModelBox.SelectionChanged += async (_, _) =>
                {
                    if (_runApplying || RunModelBox.SelectedItem is not ComboBoxItem item) return;
                    await ChangeAsync(RunModelBox, () => run.SetModelAsync((string?)item.Tag));
                };
                RunModelBox.SelectedIndex = IndexOfTag(RunModelBox, options.ModelId);
                panel.Children.Add(Labelled(OpenClawRunText.ModelLabel, RunModelBox));

                // Thinking: the model's own levels, id sent and label shown.
                RunThinkingBox = null;
                if (options.ThinkingChoices.Count > 0)
                {
                    RunThinkingBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                    if (options.CanResetToDefault)
                    {
                        var label = options.ThinkingDefaultLabel is { } d
                            ? $"{OpenClawRunText.DefaultLabel} ({d})"
                            : OpenClawRunText.DefaultLabel;
                        RunThinkingBox.Items.Add(new ComboBoxItem { Content = label, Tag = null });
                    }

                    foreach (var t in options.ThinkingChoices) RunThinkingBox.Items.Add(new ComboBoxItem { Content = t.Label, Tag = t.Id });
                    RunThinkingBox.SelectionChanged += async (_, _) =>
                    {
                        if (_runApplying || RunThinkingBox.SelectedItem is not ComboBoxItem item) return;
                        await ChangeAsync(RunThinkingBox, () => run.SetThinkingAsync((string?)item.Tag));
                    };
                    RunThinkingBox.SelectedIndex = IndexOfTag(RunThinkingBox, options.ThinkingId);
                    panel.Children.Add(Labelled(OpenClawRunText.ThinkingLabel, RunThinkingBox));
                }

                // Fast mode, with its own way back to the default.
                RunFastSwitch = new ToggleSwitch { IsChecked = options.Fast, OnContent = "", OffContent = "" };
                RunFastSwitch.IsCheckedChanged += async (_, _) =>
                {
                    if (_runApplying) return;
                    var on = RunFastSwitch.IsChecked == true;
                    await ChangeAsync(RunFastSwitch, () => run.SetFastModeAsync(on));
                };
                RunFastDefault = new Button
                {
                    Content = OpenClawRunText.DefaultLabel,
                    IsVisible = options.CanResetToDefault && options.FastIsSet,
                    Padding = new Thickness(6, 1)
                };
                RunFastDefault.Click += async (_, _) => await ChangeAsync(RunFastDefault, () => run.SetFastModeAsync(null));
                var fastRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                fastRow.Children.Add(RunFastSwitch);
                fastRow.Children.Add(RunFastDefault);
                panel.Children.Add(Labelled(OpenClawRunText.FastLabel, fastRow));

                // Time limit for the next sends: held on the session, never zero.
                RunLimitBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                RunLimitBox.Items.Add(new ComboBoxItem { Content = OpenClawRunText.TimeLimitText(null), Tag = null });
                foreach (var minutes in OpenClawRunText.TimeLimitMinutes)
                {
                    RunLimitBox.Items.Add(new ComboBoxItem { Content = OpenClawRunText.TimeLimitText(minutes), Tag = minutes });
                }

                RunLimitBox.SelectionChanged += (_, _) =>
                {
                    if (_runApplying || RunLimitBox.SelectedItem is not ComboBoxItem item) return;
                    run.TimeoutMinutes = (int?)item.Tag;
                };
                RunLimitBox.SelectedIndex = IndexOfTag(RunLimitBox, run.TimeoutMinutes);
                panel.Children.Add(Labelled(OpenClawRunText.TimeLimitLabel, RunLimitBox));

                RunStatus = new TextBlock
                {
                    FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false,
                    Foreground = RecordingFill
                };
                panel.Children.Add(RunStatus);

                return panel;
            }
            finally
            {
                _runApplying = false;
            }
        }

        private static int IndexOfTag(ComboBox box, object? tag)
        {
            // Every item is one this panel added, so each is a ComboBoxItem.
            for (var i = 0; i < box.Items.Count; i++)
            {
                if (Equals(((ComboBoxItem)box.Items[i]!).Tag, tag)) return i;
            }

            return -1;
        }

        private static Control Labelled(string label, Control control)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Opacity = 0.7 });
            row.Children.Add(control);
            return row;
        }

        // One change: the control is held off while the request is out, and the
        // sentence the session returns, if any, goes in the status line. Success
        // returns null, and the session's own RunOptionsChanged then redraws the
        // chip and rebuilds the flyout from what really happened.
        private async Task ChangeAsync(Control control, Func<Task<string?>> change)
        {
            // Always built by the time a control can be changed: the handlers that
            // call this belong to the flyout that builds it.
            var status = RunStatus!;

            control.IsEnabled = false;
            status.IsVisible = false;

            var failure = await change();

            control.IsEnabled = true;

            if (failure is null) return;

            status.Text = failure;
            status.IsVisible = true;

            // A refusal changed nothing, so put the controls back to what is true.
            // The panel may have been rebound, or the options gone, while the
            // request was out, and then there is nothing to put back.
            if (_session is IRemoteChatRunOptions run && run.RunOptions is { } options && _runFlyout is not null)
            {
                _runFlyout.Content = BuildRunFlyout(run, options);
                RunStatus!.Text = failure;
                RunStatus.IsVisible = true;
            }
        }

        // --- CB-267: a message the session would hold ---

        internal enum InboundStage { Offer, Confirming, Working, Done, Failed }

        internal enum InboundInput { Ask, Cancel, Confirm, Succeeded, Failed }

        internal const string ResendLabel = "Send it again";

        internal const string InboundFailedText =
            "Couldn't change that file, so nothing was turned on — the chat says why.";

        // Where the box is. Per panel rather than per session, and reset at
        // every bind: see Bind.
        private InboundStage _inboundStage = InboundStage.Offer;

        // The confirmation's whole state machine, pure so every arrow is a case.
        // Anything not listed leaves the stage alone — a stray Succeeded after
        // the user cancelled, or a second Confirm while the first is writing.
        internal static InboundStage InboundStep(InboundStage stage, InboundInput input) => (stage, input) switch
        {
            (InboundStage.Offer, InboundInput.Ask) => InboundStage.Confirming,
            (InboundStage.Failed, InboundInput.Ask) => InboundStage.Confirming,
            (InboundStage.Confirming, InboundInput.Cancel) => InboundStage.Offer,
            (InboundStage.Confirming, InboundInput.Confirm) => InboundStage.Working,
            (InboundStage.Working, InboundInput.Succeeded) => InboundStage.Done,
            (InboundStage.Working, InboundInput.Failed) => InboundStage.Failed,
            _ => stage,
        };

        internal readonly record struct InboundView(
            string Text, string? Path, bool Ask, bool Confirm, bool Cancel, bool Resend);

        // What the box shows, or null for no box. Done outlives the offer on
        // purpose — the offer is gone *because* it worked — and so does a write
        // in flight, since enabling raises InboundChanged before it returns.
        // An offer that cannot be edited (settings.json unreadable) is said with
        // no button, whatever stage the panel was in.
        internal static InboundView? InboundViewFor(
            InboundStage stage, PeerInboundOffer? offer, bool held, string home)
        {
            var path = offer is null ? null : PeerInbound.DisplayPath(offer.SettingsPath, home);

            return stage switch
            {
                InboundStage.Done => new(PeerInbound.DoneText, null, false, false, false, Resend: held),
                InboundStage.Working => new(PeerInbound.TradeOff, path, false, false, false, false),
                InboundStage.Failed => new(InboundFailedText, path, Ask: offer?.CanEdit == true, false, false, false),
                _ when offer is null => null,
                InboundStage.Confirming when offer.CanEdit => new(PeerInbound.TradeOff, path, false, true, true, false),
                _ => new(offer.Explanation, path, Ask: offer.CanEdit, false, false, false),
            };
        }

        private void ApplyInbound(IRemoteChatSession? session)
        {
            var inbound = session as IRemoteChatPeerInbound;
            var view = inbound is null
                ? null
                : InboundViewFor(_inboundStage, inbound.InboundOffer, inbound.HasHeldMessage,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            InboundBox.IsVisible = view is not null;
            if (view is not { } v) return;

            InboundText.Text = v.Text;
            InboundPath.Text = v.Path ?? "";
            InboundPath.IsVisible = v.Path is not null;
            InboundAskButton.IsVisible = v.Ask;
            InboundConfirmButton.IsVisible = v.Confirm;
            InboundCancelButton.IsVisible = v.Cancel;
            InboundResendButton.IsVisible = v.Resend;
            InboundButtons.IsVisible = v.Ask || v.Confirm || v.Cancel || v.Resend;
        }

        // Raised on the UI thread by contract, and only by the bound session.
        private void OnInboundChanged() => ApplyInbound(_session);

        private void StepInbound(InboundInput input)
        {
            _inboundStage = InboundStep(_inboundStage, input);
            ApplyInbound(_session);
        }

        // Only from Confirming, so a second click while the write is running
        // does not write twice. The answer is applied only if the panel is still
        // showing the session it was asked for.
        internal async Task ConfirmInboundAsync()
        {
            if (_session is not IRemoteChatPeerInbound inbound || _inboundStage != InboundStage.Confirming) return;

            var session = _session;
            StepInbound(InboundInput.Confirm);

            var ok = await inbound.EnableInboundAsync();
            if (!ReferenceEquals(session, _session)) return;

            StepInbound(ok ? InboundInput.Succeeded : InboundInput.Failed);
        }

        // The session redraws the box through InboundChanged once it knows
        // whether the second attempt was held too.
        internal async Task ResendInboundAsync()
        {
            if (_session is IRemoteChatPeerInbound inbound) await inbound.ResendHeldAsync();
        }

        // Where the read-only link goes. Held rather than read back off the
        // TextBlock, because what is *shown* is a label and what is opened is an
        // address, and putting a URL on screen to have somewhere to keep it is
        // how the two come to disagree.
        private string? _readOnlyUrl;
        private string? _sessionUrl;

        // The address for the live-session link: the session's own, whenever it
        // has one and is not read-only. Pure so each arm is a unit case. A
        // session that is not IRemoteChatReadOnly has no address to offer, and a
        // read-only one gets its link from the read-only box instead.
        internal static string? SessionLinkUrl(IRemoteChatSession? session) =>
            session is IRemoteChatReadOnly { IsReadOnly: false, ReplyUrl: { Length: > 0 } url } ? url : null;

        // Excluded from coverage: the guard is reachable and asserted through
        // ReadOnlyLink's visibility, and the half behind it launches a real
        // browser. Which ProcessStartInfo that is, per platform, is
        // CloudSessionLink.StartInfoFor and is covered there.
        [ExcludeFromCodeCoverage]
        private void OnReadOnlyLinkPressed(object? sender, PointerPressedEventArgs e)
        {
            // At most one of the two is set: SessionLinkUrl is null for a
            // read-only session and _readOnlyUrl is null for a live one.
            var url = _readOnlyUrl ?? _sessionUrl;
            if (url is null) return;

            CloudSessionLink.Open(url);
        }

        // The same decoded frames the orb draws, at a size worth looking at.
        // Animated here too: this is the one place you are actually looking at
        // the picture rather than glancing at it, so a still frame of an
        // animated avatar would be the wrong half of the trade.
        private OpenClawAvatars.Avatar? _avatar;
        private ImageBrush? _avatarBrush;
        private int _avatarFrame;
        private DispatcherTimer? _avatarTimer;

        // The ring is the one part of the portrait that is always visible,
        // whether the circle holds a photo, a colour or nothing, so it is where
        // an identity colour belongs. Default is the flat white the XAML ships
        // with, which is what a session with no colour of its own keeps.
        private static readonly IBrush DefaultRing = new SolidColorBrush(Color.Parse("#40FFFFFF"));

        private void RingFor(Color? color)
        {
            if (color is not { } c)
            {
                Avatar.Stroke = DefaultRing;
                Avatar.StrokeThickness = 1;
                return;
            }

            Avatar.Stroke = new SolidColorBrush(c);
            // Thicker than the default hairline: a coloured ring is carrying
            // information now, and at 1px against a dark panel it reads as an
            // antialiasing artefact rather than a deliberate mark.
            Avatar.StrokeThickness = 2.5;
        }

        // An agent's colour, keyed on its id so the same agent is the same
        // colour everywhere — the orb, the team view and now this header.
        //
        // Asked of OpenClawSessions rather than of AgentPalette directly, which
        // is the difference between that sentence being true and being nearly
        // true. HexFor gives an agent the colour its id hashes to; two agents
        // can hash close enough to be indistinguishable, so the assignment is
        // made across the whole set and moves whichever of them collided.
        // Calling HexFor here would hand this header the pre-collision answer
        // and quietly disagree with the ring on the orb it opened from.
        private static Color? AgentColorFor(string sessionId)
        {
            var agent = OpenClawSessions.AgentIdOf(sessionId);
            if (string.IsNullOrEmpty(agent)) return null;

            var hex = OpenClawSessions.ColourForAgent(agent);
            return Color.TryParse(hex, out var colour) ? colour : null;
        }

        // The header borrows the orb's letters and colours, and borrowed them
        // exactly once — at Bind. Anything that changed the orb afterwards left
        // the panel showing what the orb used to say: a /rename, a /color, a
        // title arriving after the first hook write, or the two-letter setting
        // being toggled while a panel was open. Worse than stale, at open time
        // it could be empty — an orb clicked before its first status write has
        // no glyph yet, and the header copied the nothing and kept it.
        //
        // Same shape as RepositionFor and SetRecording above: the orb tells the
        // panel, the panel checks the message is from the orb it is showing.
        public static void RefreshIdentityFor(OrbWindow orb)
        {
            if (OwnedBy(orb) is not { } panel) return;

            panel.ApplyBorrowedIdentity();
            panel.RefreshSoleSpeaker();
        }

        // Only the case that borrows from the orb. An agent with a portrait or
        // an OpenClaw identity has its own, and re-running the whole of
        // ApplyAvatar here would restart an animated avatar on every hook
        // write — which is several a second while a session is working.
        // The agent whose messages these are.
        //
        // For a gateway session that is the agent in the session key, not the
        // panel's title: "#openclaw-management" is where the conversation is
        // and Aurora is who is talking in it, and a chip reading "Op" would
        // be naming the room as its own speaker. Only a terminal session, whose
        // title *is* its agent, falls back to the title.
        // "Nova — wtvamp" is built as name plus place, so it splits back into
        // the two lines the header has. A name with no place — an agent's own
        // main session — leaves the second line empty.
        //
        // Read from the session every time rather than once at Bind. A terminal
        // session is usually nameless when its panel opens and gets its title
        // from a later hook write; the header used to keep the empty string it
        // was born with.
        private void ApplyTitle()
        {
            var parts = (_session?.DisplayName ?? "").Split(" — ", 2);
            var persona = SessionIdentity.LocalNameFor(_session?.SessionId);

            // A local persona renames the header the same way it renames the
            // orb: the CLAUDE.md beside the work says what this agent is
            // called, and a header still reading the folder name beside an orb
            // reading "Le" is the app disagreeing with itself in the one place
            // both are on screen at once.
            //
            // Only the local half. A gateway session's DisplayName is already
            // built from its identity — "Nova — #general", name and place — and
            // a *room's* is the room, where the identity is whichever agent is
            // in the session key. Overwriting that would put one agent's name on
            // the header of a channel four of them are talking in, which is the
            // distinction RefreshSoleSpeaker's own comment spells out below.
            //
            // The place half is never touched: a persona says who, never where.
            TitleText.Text = string.IsNullOrEmpty(persona) ? parts[0] : persona;
            SubtitleText.Text = parts.Length > 1 ? parts[1] : "";
            SubtitleText.IsVisible = parts.Length > 1;

            // Last, because the meta line's first rule is "not what the title
            // already says" and the title has only just been decided.
            ApplyMeta();
        }

        // --- CB-134: which session, which folder, which machine -------------

        // What this machine is called, asked once.
        //
        // MachineNames.Mine() shells out to scutil on macOS. It caches its own
        // answer, but this runs from ApplyTitle, which runs on every hook write
        // — several a second while a session is working — and taking a lock in
        // that path to be handed back a string that cannot change while the
        // process lives is a cost with nothing on the other side of it.
        //
        // Lazily rather than in a field initializer: the field would run scutil
        // during ChatPanel's type initialiser, which is the first time any test
        // in the UI suite touches the class, and a subprocess in a static
        // constructor is a deadlock waiting for a machine slow enough.
        private static string? _thisMachine;

        private static string ThisMachine => _thisMachine ??= MachineNames.Mine();

        // Dim, because this line is the one you go looking for rather than the
        // one that catches your eye. Matching the TextBlock's own Foreground in
        // the XAML: the Lead run inherits it and only the machine token
        // overrides it, so the two have to agree.
        private static readonly IBrush MetaInk = new SolidColorBrush(Color.Parse("#80FFFFFF"));

        // The machine, when it is not this one. Bright enough to be the thing
        // you notice in a line that is otherwise deliberately quiet, because
        // "this conversation is happening on the mini" is the one fact here
        // that changes what you should do about it.
        //
        // The orb's accent — the session's own colour — is preferred, so the
        // panel and the ring on the orb it opened from say the same thing. Not
        // every session has one (AccentColor is null for anything the app has
        // not been told a colour for), and the fallback is the same light blue
        // the transcript draws links in rather than a fourth colour nobody has
        // seen before.
        private static readonly IBrush RemoteMachineInk = new SolidColorBrush(Color.Parse("#FF9FD0FF"));

        // Fills in the header's third line, or collapses it.
        //
        // Every fact here can arrive after the panel is already open — a title
        // comes from a hook write that may not have happened yet, and a
        // mirrored session's machine comes off a roster that answers a moment
        // later — so this is re-run from ApplyTitle and from OnMachineChanged
        // rather than once at Bind. That is the same lesson ApplyTitle's own
        // comment records: the header used to keep whatever it was born with.
        private void ApplyMeta()
        {
            // The orb first, and SessionManager only where there is no orb
            // status to read — a panel can be bound before the session's first
            // hook write, and in that window the manager may already hold what
            // the orb has not been handed yet.
            var status = _owner?.LastStatus
                ?? SessionManager.Instance?.StatusFor(_session?.SessionId);

            var machine = ChatHeaderMeta.MachineFor(
                (_session as IRemoteChatMachine)?.MachineName, ThisMachine);

            // What the header's first line already says, in full — not just
            // TitleText.Text. A gateway session's title is "Annabel Lee —
            // #cascadia-forensics-marketing", and ApplyTitle above already
            // split that across TitleText and SubtitleText; comparing
            // Unrepeated against TitleText alone sees "Annabel Lee" next to
            // status?.Title's whole "Annabel Lee — #cascadia-forensics-
            // marketing" and calls them different, so the meta row draws the
            // detail a second time directly under the chip that already shows
            // it. Rebuilding the same "name — place" shape the split came from
            // is what makes the comparison see them as the repeat they are.
            var shownTitle = SubtitleText.IsVisible && SubtitleText.Text is { Length: > 0 }
                ? $"{TitleText.Text} — {SubtitleText.Text}"
                : TitleText.Text;

            var meta = ChatHeaderMeta.Compose(
                shownTitle,
                status?.Title,
                status?.Cwd,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                MachineNames.Readable(machine.Name),
                machine.IsLocal);

            MetaRow.IsVisible = meta.IsVisible;

            // On the row rather than on either block, so the hover answers
            // wherever the pointer lands on the line — including the gap the
            // ellipsis just made, which is exactly where somebody who cannot
            // read the path will put it.
            ToolTip.SetTip(MetaRow, meta.IsVisible ? meta.Tooltip : null);

            // Set both rows every time, visibility included. The panel is
            // reused across sessions, so a row left showing from the last
            // conversation is drawn over this one — the same reason ApplyTitle
            // re-reads rather than patching, and here the thing left behind
            // would be another session's machine.
            MetaText.IsVisible = meta.Lead.Length > 0;
            MetaText.Text = meta.Lead;

            MetaMachineText.IsVisible = meta.Machine.Length > 0;
            MetaMachineText.Text = meta.Machine;
            MetaMachineText.Foreground = meta.RemoteMachine
                ? (_owner?.AccentColor is { } accent ? new SolidColorBrush(accent) : RemoteMachineInk)
                : MetaInk;
        }

        private void RefreshSoleSpeaker()
        {
            ApplyTitle();

            var was = _soleSpeaker.Name;

            // Asked of SessionIdentity rather than of OpenClawSessions, which is
            // what makes the chips on a local session's messages say "Leota"
            // instead of the folder. The identityName slot is exactly the right
            // one for a persona: it is "the name we were told, as opposed to
            // whatever the panel is titled", and a CLAUDE.md is being told.
            var identityName = SessionIdentity.NameFor(_session?.SessionId);

            // The rule itself is in ChatSpeaker, pure and tested — including
            // the part that matters here, that a name we already knew is never
            // replaced by not knowing it. That is what made the chips vanish
            // after a while rather than simply never appear.
            var name = ChatSpeaker.Resolve(identityName, TitleText.Text, was);

            if (name == was) return;

            _soleSpeaker.Name = name;

            foreach (var view in _turns) view.SpeakerChanged();
        }

        private void ApplyBorrowedIdentity()
        {
            if (!_borrowedIdentity || _owner is null) return;

            Avatar.Fill = new SolidColorBrush(_owner.OrbColor);
            AvatarEmoji.Foreground = InkOn(_owner.OrbColor);
            RingFor(_owner.AccentColor);

            var letters = BorrowedLetters();

            // Never blank what is already there. Same rule as ChatSpeaker and
            // the last place in the panel that still lacked it: both of this
            // one's sources can be momentarily empty for reasons that are about
            // us rather than about the session — the orb clears its glyph while
            // an avatar loads, and a title is empty until a hook write brings
            // one — and either used to wipe a circle that was reading fine.
            //
            // There is no case where going from letters to nothing is the truth
            // about a conversation. Nothing to say yet is the empty circle at
            // the start; nothing to say any more does not happen.
            if (string.IsNullOrEmpty(letters)) return;
            if (AvatarEmoji.Text == letters) return;

            AvatarEmoji.Text = letters;
            AvatarEmoji.IsVisible = true;
        }

        // What the orb is drawing, or what it would draw if it had got round to
        // it. The fallback matters because the panel can be bound before the
        // orb's first status write, and an empty circle beside a perfectly good
        // title is the one outcome that is never right. It derives them the way
        // the orb would rather than with Initials(), so the two agree on case
        // as well as on letters — "Cb" here and on the orb, not "CB" here.
        private string BorrowedLetters()
        {
            var letters = _owner?.GlyphText ?? "";
            if (!string.IsNullOrEmpty(letters)) return letters;

            var name = TitleText.Text;
            return string.IsNullOrWhiteSpace(name)
                ? ""
                : OrbGlyph.For(name, OrbweaverSettings.TwoLetterGlyphs);
        }

        // Ink that can be read on a given circle.
        //
        // AvatarEmoji had no Foreground at all and inherited the panel's, which
        // is near-black — fine on nothing, because the circle it sits in was
        // invisible until an identity was drawn behind it. Once the fill became
        // the orb's *state* colour it was black on black, and idle is near-black
        // by default. That is the whole of the "initials keep disappearing"
        // report: they were there the entire time, and the letters went from
        // legible to invisible when a session stopped working, because
        // generating and waiting are bright and idle is not.
        //
        // Chosen by luminance rather than fixed at white, which is what the orb
        // does. The orb only ever draws on a state colour and white suits all
        // of them; this circle is also filled with an agent's own colour, and
        // several of those are light enough that white letters vanish the same
        // way black ones just did.
        private static readonly IBrush LightInk = new SolidColorBrush(Color.Parse("#EEFFFFFF"));
        private static readonly IBrush DarkInk = new SolidColorBrush(Color.Parse("#E6000000"));

        private static IBrush InkOn(Color fill)
        {
            // Rec. 709 luminance: the eye is far more sensitive to green than
            // to blue, so a plain average calls mid-blue light and gets it
            // backwards.
            var luminance = (0.2126 * fill.R + 0.7152 * fill.G + 0.0722 * fill.B) / 255.0;

            return luminance > 0.55 ? DarkInk : LightInk;
        }

        private void ApplyAvatar(string sessionId)
        {
            StopAvatarAnimation();

            var face = SessionIdentity.For(sessionId);
            var avatar = face.Avatar;

            // Neither a portrait nor an emoji, which is every local session
            // without a persona picture and a gateway one whose agent list
            // hasn't landed yet. Its orb already carries both halves of an
            // identity — a letter and a colour, the ones just clicked — so the
            // header borrows them. Better than an empty circle, and better than
            // a second scheme invented for this window: the panel ends up
            // looking like the orb it came out of.
            //
            // Keyed on there being no identity to draw rather than on the
            // session's type, because the panel deliberately doesn't know what
            // kinds of session exist. SessionIdentity owns which of the two
            // registries answered and what counts as a face of one's own — a
            // local persona's *name* deliberately does not, because the orb is
            // already carrying it and borrowing is how it gets here.
            if (!face.DrawsItsOwnCircle && _owner is not null)
            {
                _avatar = null;
                _avatarFrame = 0;

                // Fill from the state, ring from the identity — which is
                // exactly how the orb itself is drawn, so the header reads as
                // the same object rather than as a second scheme.
                //
                // Both from OrbColor is what this said first, and that made the
                // ring the state colour twice over: idle is a user setting and
                // is commonly near black, so the "identity ring" was an
                // invisible ring around a circle of its own colour.
                Avatar.Fill = new SolidColorBrush(_owner.OrbColor);
                Avatar.IsVisible = true;
                AvatarEmoji.Foreground = InkOn(_owner.OrbColor);
                RingFor(_owner.AccentColor);

                // An initial wants less room than an emoji does.
                // Set outright rather than through ApplyBorrowedIdentity,
                // which refuses to blank: this is a new conversation and the
                // letters on screen are the last one's. The never-blank rule is
                // about refreshing what is already right, not about carrying
                // one session's identity onto another — the same distinction
                // Unbind draws for the speaker.
                _borrowedIdentity = true;
                AvatarEmoji.Text = BorrowedLetters();
                AvatarEmoji.FontSize = 26;
                AvatarEmoji.IsVisible = !string.IsNullOrEmpty(AvatarEmoji.Text);

                StateDot.HorizontalAlignment = HorizontalAlignment.Right;
                StateDot.VerticalAlignment = VerticalAlignment.Bottom;
                return;
            }

            _avatar = avatar;
            _avatarFrame = 0;
            _borrowedIdentity = false;

            // Reset from whatever the branch above may have left behind.
            AvatarEmoji.FontSize = 38;

            if (avatar is null)
            {
                // No portrait. This used to leave a hollow circle — no fill, no
                // ring, and nothing inside unless the agent happened to have an
                // emoji, which reads as a picture that failed to load rather
                // than as a person.
                //
                // An agent already has both halves of an identity elsewhere in
                // the app: a colour from AgentPalette, keyed on its id so it is
                // stable, and a name. So the header shows what the orb shows —
                // that colour as the fill and ring, and the initials of the
                // name when there is no emoji to use instead.
                var agentColor = AgentColorFor(sessionId);

                AvatarEmoji.Text = !string.IsNullOrEmpty(face.Emoji)
                    ? face.Emoji!
                    : OrbGlyph.Initials(face.Name);
                AvatarEmoji.IsVisible = !string.IsNullOrEmpty(AvatarEmoji.Text);

                // Initials are letterforms, not a pictograph, so they want the
                // smaller size an emoji would overflow at.
                if (string.IsNullOrEmpty(face.Emoji)) AvatarEmoji.FontSize = 26;

                if (agentColor is { } c)
                {
                    Avatar.Fill = new SolidColorBrush(c);
                    Avatar.IsVisible = true;
                    AvatarEmoji.Foreground = InkOn(c);
                    RingFor(c);
                }
                else
                {
                    Avatar.Fill = null;
                    Avatar.IsVisible = false;
                }

                // With a filled circle the badge has somewhere to sit, so it
                // keeps its corner. Only a genuinely empty circle centres it.
                var filled = Avatar.IsVisible || AvatarEmoji.IsVisible;
                StateDot.HorizontalAlignment = filled
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Center;
                StateDot.VerticalAlignment = filled
                    ? VerticalAlignment.Bottom
                    : VerticalAlignment.Center;

                return;
            }

            AvatarEmoji.IsVisible = false;
            StateDot.HorizontalAlignment = HorizontalAlignment.Right;
            StateDot.VerticalAlignment = VerticalAlignment.Bottom;

            _avatarBrush ??= new ImageBrush { Stretch = Stretch.UniformToFill };
            _avatarBrush.Source = avatar.Frames[0];
            Avatar.Fill = _avatarBrush;
            Avatar.IsVisible = true;
            // A portrait gets the ring too. Without it the one avatar with a
            // picture is the only one in the app not wearing its own colour.
            //
            // A local persona has no agent colour to ask for, there being no
            // agent — so it takes the orb's accent instead, which is the colour
            // the orb is drawing its own ring in around this same picture. The
            // fallback is deliberately not offered to a gateway session: there,
            // a null answer means the agent list has not landed yet, and the
            // hairline is the honest thing to draw until it does.
            RingFor(AgentColorFor(sessionId) ?? (face.Gateway ? null : _owner?.AccentColor));

            if (!avatar.IsAnimated) return;

            _avatarTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(avatar.DelaysMs[0])
            };

            // The tick closes over its own timer and its own avatar rather than
            // reading the fields. A tick already queued on the dispatcher when
            // the panel rebinds to another agent would otherwise fire against
            // whatever is there now: a null timer if that agent's picture is
            // static, or the previous picture's frame delay if it isn't.
            var timer = _avatarTimer;
            var frames = avatar;
            var frame = 0;

            timer.Tick += (_, _) =>
            {
                if (!ReferenceEquals(_avatar, frames) || _avatarBrush is null) return;

                frame = (frame + 1) % frames.Frames.Count;
                _avatarBrush.Source = frames.Frames[frame];
                timer.Interval = TimeSpan.FromMilliseconds(frames.DelaysMs[frame]);
            };

            _avatarTimer.Start();
        }

        // Stopped when the panel goes away: a hidden window animating a GIF is
        // work nobody asked for, and the panel is hidden far more than it is up.
        private void StopAvatarAnimation()
        {
            _avatarTimer?.Stop();
            _avatarTimer = null;
        }

        // --- the heartbeat chip's beat ---------------------------------------
        // Same curve as the orb badge's (OpenClawHeartbeat.Beat), driven by a
        // timer of this panel's own.
        //
        // A timer rather than an Avalonia animation for the reason the orb's
        // pulse gives, and rather than *sharing* the orb's ticker because that
        // one's roster is orb windows: there is at most one chat panel, so a
        // second 20Hz timer that only runs while a heartbeat chat is open costs
        // nothing worth pooling. It stops the moment the chip goes away, which
        // is what keeps that true.

        private DispatcherTimer? _heartTimer;
        private long _heartStartedAt;
        private readonly ScaleTransform _heartScale = new();

        private void ApplyHeartbeat(bool heartbeat)
        {
            HeartChip.IsVisible = heartbeat;

            if (!heartbeat)
            {
                StopHeartAnimation();
                return;
            }

            HeartChipText.RenderTransform = _heartScale;
            HeartChipText.RenderTransformOrigin = RelativePoint.Center;
            _heartStartedAt = Environment.TickCount64;

            if (_heartTimer is not null) return;

            _heartTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / 20)
            };
            _heartTimer.Tick += (_, _) =>
            {
                var beat = OpenClawHeartbeat.Beat(
                    (Environment.TickCount64 - _heartStartedAt) / OpenClawHeartbeat.PeriodMs);

                // Smaller swell than the orb's: this heart sits on a line of
                // text, and one that grew by a third would shift the chip's
                // neighbours every beat.
                _heartScale.ScaleX = _heartScale.ScaleY = 1.0 + (0.14 * beat);
                HeartChipText.Opacity = 0.55 + (0.45 * beat);
            };
            _heartTimer.Start();
        }

        private void StopHeartAnimation()
        {
            _heartTimer?.Stop();
            _heartTimer = null;

            _heartScale.ScaleX = _heartScale.ScaleY = 1.0;
            HeartChipText.Opacity = 1.0;
        }

        // Clicking a picture opens it full size in whatever this machine views
        // pictures with. Handled so the click doesn't travel on to the panel
        // behind it, and so it can't be mistaken for a click-away dismiss.
        private void Image_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is TurnView turn) turn.OpenFullSize();
        }

        // CB-272: the same click, for a video tile — handled for the same two
        // reasons, and it fetches the clip only now rather than when the row
        // was drawn.
        private void Audio_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is TurnView turn) turn.OpenAudio();
        }

        private void Document_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is TurnView turn) turn.OpenDocument();
        }

        private void Video_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is TurnView turn) turn.OpenVideo();
        }

        // Avalonia.Native's macOS cursor factory (libAvaloniaNative.dylib)
        // only ever asks AppKit for arrow, crosshair, hand, I-beam, and the
        // two straight resize cursors (up/down, left/right) — nothing in it
        // answers to a diagonal, because AppKit itself has no public diagonal
        // resize NSCursor to hand back. TopLeftCorner and friends fall
        // through to plain crosshairCursor there, and Hand doesn't read as
        // "resize" at all. So the corners get a real double-headed arrow,
        // drawn once here and shared by both ends of each diagonal — NwSeCursor
        // for the ↖↘ corners, NeSwCursor (the same arrow mirrored) for ↗↙.
        private static readonly Cursor NwSeCursor = BuildDiagonalCursor(mirrored: false);
        private static readonly Cursor NeSwCursor = BuildDiagonalCursor(mirrored: true);

        private static Cursor BuildDiagonalCursor(bool mirrored)
        {
            // Avalonia's macOS cursor images came out at the bitmap's raw
            // pixel count, not that divided by dpiScale — so the first version
            // of this, drawn on a 22-unit canvas at 2x for retina crispness,
            // rendered as a 44px cursor and looked about double the size it
            // should have. Sizes below are fractions of `size` rather than
            // the original design's absolute numbers, so this can be tuned
            // again without redoing the arithmetic by hand.
            const double size = 11;
            const double dpiScale = 2;

            double margin = size / 11; // corner inset for a head's right-angle vertex
            double headEnd = size * 9 / 22; // how far a head's short legs reach
            double shaftA = size * 3 / 11; // shaft's near end
            double shaftB = size * 8 / 11; // shaft's far end
            double farHeadStart = size - headEnd;
            double far = size - margin;

            double X(double x) => mirrored ? size - x : x;

            var target = new RenderTargetBitmap(
                new PixelSize((int)(size * dpiScale), (int)(size * dpiScale)),
                new Vector(96 * dpiScale, 96 * dpiScale));

            using (var ctx = target.CreateDrawingContext())
            {
                // The shaft, drawn as a black line with a white one on top of
                // it rather than a single stroke, so it reads against either
                // a light or a dark background — the same reason this panel's
                // own bubbles carry an outline color at all.
                var outline = new Pen(Brushes.Black, size * 4.5 / 22, lineCap: PenLineCap.Round);
                ctx.DrawLine(outline, new Point(X(shaftA), shaftA), new Point(X(shaftB), shaftB));
                var inner = new Pen(Brushes.White, size * 2 / 22, lineCap: PenLineCap.Round);
                ctx.DrawLine(inner, new Point(X(shaftA), shaftA), new Point(X(shaftB), shaftB));

                // Two right-triangle heads, one at each end of the shaft, each
                // with its right angle at the corner it points into — the
                // same shape Windows' own SIZENWSE/SIZENESW cursors use.
                var heads = new StreamGeometry();
                using (var g = heads.Open())
                {
                    g.BeginFigure(new Point(X(margin), margin), true);
                    g.LineTo(new Point(X(headEnd), margin), true);
                    g.LineTo(new Point(X(margin), headEnd), true);
                    g.EndFigure(true);

                    g.BeginFigure(new Point(X(far), far), true);
                    g.LineTo(new Point(X(farHeadStart), far), true);
                    g.LineTo(new Point(X(far), farHeadStart), true);
                    g.EndFigure(true);
                }

                ctx.DrawGeometry(Brushes.White, new Pen(Brushes.Black, size * 1.25 / 22), heads);
            }

            return new Cursor(target, new PixelPoint(target.PixelSize.Width / 2, target.PixelSize.Height / 2));
        }

        // BeginResizeDrag hands the drag to the platform's window manager, which
        // is where Win32 and X11 implement it — but Avalonia.Native carries no
        // such hook on macOS (nothing in libAvaloniaNative.dylib answers to it),
        // so the call is a silent no-op there: the cursor still swaps, because
        // that part is pure managed code, but nothing ever moves. Tracked by
        // hand instead, uniformly on every platform, rather than branching on
        // OS to use the native call where it happens to exist.
        private WindowEdge? _resizeEdge;
        private PixelPoint _resizeStartPos;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private PixelPoint _resizeStartScreen;

        private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            e.Handled = true;
            _resizeEdge = edge;
            _resizeStartPos = Position;
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
            _resizeStartScreen = this.PointToScreen(e.GetPosition(this));

            // Captured on the window rather than the strip under the pointer:
            // the drag routinely carries the pointer off a 6px strip, and
            // capture is what keeps the events coming anyway.
            e.Pointer.Capture(this);
        }

        private void OnResizePointerMoved(object? sender, PointerEventArgs e)
        {
            if (_resizeEdge is not { } edge) return;

            var nowScreen = this.PointToScreen(e.GetPosition(this));
            var scale = RenderScaling;
            var dx = (nowScreen.X - _resizeStartScreen.X) / scale;
            var dy = (nowScreen.Y - _resizeStartScreen.Y) / scale;

            var west = edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;
            var east = edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;
            var north = edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;
            var south = edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast;

            var pos = _resizeStartPos;
            var width = _resizeStartWidth;
            var height = _resizeStartHeight;

            if (east) width = Math.Clamp(_resizeStartWidth + dx, MinWidth, MaxWidth);
            if (west)
            {
                width = Math.Clamp(_resizeStartWidth - dx, MinWidth, MaxWidth);
                pos = pos.WithX(_resizeStartPos.X - (int)Math.Round((width - _resizeStartWidth) * scale));
            }

            if (south) height = Math.Clamp(_resizeStartHeight + dy, MinHeight, MaxHeight);
            if (north)
            {
                height = Math.Clamp(_resizeStartHeight - dy, MinHeight, MaxHeight);
                pos = pos.WithY(_resizeStartPos.Y - (int)Math.Round((height - _resizeStartHeight) * scale));
            }

            Width = width;
            Height = height;
            Position = pos;
        }

        private void OnResizePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_resizeEdge is null) return;

            _resizeEdge = null;
            e.Pointer.Capture(null);

            // Once per drag, not once per pixel: OnResizePointerMoved fires
            // continuously and this writes a file. Saved against the orb rather
            // than the session so the size follows the agent across runs — see
            // ChatPanelSizes in OrbweaverSettings for why a session id would
            // not have survived one.
            if (_owner is { } owner)
            {
                OrbweaverSettings.SetChatPanelSize(owner.PositionKey, Width, Height);
            }
        }

        // The size this agent's panel should open at: what it was last dragged
        // to, or the shipped default if it never has been.
        //
        // Clamped against this build's own Min/Max rather than trusted: the
        // bounds are in the XAML and could tighten in a later version, and a
        // hand-edited settings.json is a normal thing to find. An out-of-range
        // Width set on a Window is not politely ignored — it is honoured, and a
        // panel wider than any screen has no visible way back.
        private void ApplySavedSize(OrbWindow orb)
        {
            var saved = OrbweaverSettings.ChatPanelSizeFor(orb.PositionKey);

            Width = saved is null
                ? _defaultWidth
                : Math.Clamp(saved.Width, MinWidth, MaxWidth);
            Height = saved is null
                ? _defaultHeight
                : Math.Clamp(saved.Height, MinHeight, MaxHeight);
        }

        private void Reposition()
        {
            if (_owner is null) return;

            // Anchor on the orb's centre, the same point EnsureFlyoutShown
            // uses. PointToScreen because Position is physical pixels and these
            // are DIPs, and the two only agree at 100% scaling.
            var anchor = _owner.PointToScreen(new Point(_owner.CentreDip, _owner.CentreDip));
            var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary;
            if (screen is null) return;

            var scale = screen.Scaling;
            var work = screen.WorkingArea;

            // Width and Height are the resize target (see the XAML comment),
            // so unlike the old SizeToContent world these are already final —
            // no need to wait on Root's laid-out bounds.
            var size = new PixelSize((int)(Width * scale), (int)(Height * scale));
            var gap = (int)(OrbSizing.ChatPanelGap(_owner.OrbSize) * scale);

            // Below the orb, flipped above when below would run off the bottom,
            // clamped into the work area — the maths this method used to do
            // inline, now in ChatPanelPlacement so it can be swept by a unit
            // test and so it has somewhere to put the part that is new: not
            // landing on top of a panel somebody pinned there.
            Position = ChatPanelPlacement.Resolve(anchor, size, gap, work, OccupiedBy(screen));
        }

        // The pinned panels already on this screen, as physical rectangles.
        //
        // Pinned only. The transient is the panel being placed in every case
        // that reaches here, and a panel cannot be asked to avoid itself; and
        // there is never a second unpinned one to avoid, which is the first
        // invariant in the class comment doing a job rather than just being
        // true.
        //
        // Same screen only: a rectangle on another display can never overlap
        // this one, and including it would push a panel sideways to dodge
        // something the user cannot see beside it. Decided by whether the
        // screen's bounds hold the panel's top-left rather than by comparing
        // two Screen objects, which is a question about identity that Avalonia
        // does not promise an answer to.
        private IReadOnlyList<PixelRect> OccupiedBy(Screen screen)
        {
            var occupied = new List<PixelRect>();

            foreach (var panel in Panels)
            {
                if (!panel._pinned || !panel.IsVisible) continue;
                if (ReferenceEquals(panel, this)) continue;
                if (!screen.Bounds.Contains(panel.Position)) continue;

                // Width and Height are DIPs and Position is physical, the same
                // mismatch Reposition converts across a few lines above. This
                // panel is on `screen` by the test just made, so its scaling is
                // the right one to convert with.
                occupied.Add(new PixelRect(
                    panel.Position,
                    new PixelSize(
                        (int)(panel.Width * screen.Scaling),
                        (int)(panel.Height * screen.Scaling))));
            }

            return occupied;
        }

        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            // Only a session that has somewhere to put a picture gets its
            // paste intercepted at all — see IRemoteChatImages. Anything
            // else falls straight through to the TextBox's own paste, which
            // is exactly what happened here before this feature existed.
            if (_session is IRemoteChatImages { CanSendImages: true } && IsPasteGesture(e))
            {
                e.Handled = true;
                _ = HandlePasteAsync();
                return;
            }

            // While suggestions are up, the keys that would otherwise send or
            // insert a newline instead drive the popup — the same keys a
            // terminal's own "/" autocomplete would claim.
            if (_slashMatches.Count > 0)
            {
                if (e.Key == Key.Down)
                {
                    _slashSelected = (_slashSelected + 1) % _slashMatches.Count;
                    RenderSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Up)
                {
                    _slashSelected = (_slashSelected - 1 + _slashMatches.Count) % _slashMatches.Count;
                    RenderSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    // Dismisses the popup only. A second Escape then reaches
                    // OnPanelKeyDown and closes the panel — the same
                    // two-step precedent recording already sets below.
                    HideSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                var accepting = e.Key == Key.Tab
                    || (e.Key is Key.Enter or Key.Return && !e.KeyModifiers.HasFlag(KeyModifiers.Shift));

                if (accepting)
                {
                    AcceptSlashSuggestion(_slashMatches[_slashSelected]);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key != Key.Enter && e.Key != Key.Return) return;

            // Shift+Enter is left entirely alone so the TextBox inserts the
            // newline itself, with its own caret handling and undo entry.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

            e.Handled = true;
            Send();
        }

        // TextBox's own PasteGesture rather than a hard-coded Ctrl/Cmd+V:
        // it is already the platform-correct chord (Cmd on macOS, Ctrl
        // elsewhere), and asking Input for its own answer means this never
        // drifts from whatever the TextBox itself would have matched.
        private static bool IsPasteGesture(KeyEventArgs e)
        {
            var gesture = TextBox.PasteGesture;
            return gesture is not null && e.Key == gesture.Key && e.KeyModifiers == gesture.KeyModifiers;
        }

        // Read off TextBox rather than spelled out, exactly as IsPasteGesture
        // above does, which is also what makes this right on both platforms
        // without a single OS check: Avalonia resolves the gesture to Cmd+C on
        // macOS and Ctrl+C on Windows.
        private static bool IsCopyGesture(KeyEventArgs e)
        {
            var gesture = TextBox.CopyGesture;
            return gesture is not null && e.Key == gesture.Key && e.KeyModifiers == gesture.KeyModifiers;
        }

        // The copy keystroke, claimed before the composer can swallow it.
        //
        // Tunnel and registered on the window, for a reason worth spelling out
        // because the bug it avoids is silent: Avalonia's TextBox handles the
        // copy gesture in OnKeyDown and marks it handled *unconditionally* —
        // even with nothing selected, where it copies nothing at all. The
        // composer is always the focused control here, so an ordinary bubbling
        // handler would never run, and a message selection would look copyable
        // while the clipboard never changed. Getting there first is the only
        // way. Same reasoning, and the same routing strategy, as the Enter
        // interception in OnInputKeyDown.
        private void OnCopyKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsCopyGesture(e)) return;

            var selected = SelectedMessageText();
            var composerHasSelection = !string.IsNullOrEmpty(Input.SelectedText);

            if (ChatCopy.Decide(composerHasSelection, selected is not null) != ChatCopy.Target.Message)
                return;

            // Handled only in the one case the panel is actually answering for.
            // Left alone otherwise, so the composer's own copy — and its own
            // no-op on an empty selection — behave exactly as they always have.
            e.Handled = true;
            _ = CopyToClipboardAsync(selected!);
        }

        // The selection a person can see in the transcript, if there is one.
        //
        // First rather than joined: SelectOnly keeps exactly one bubble
        // selected at a time, so "first" and "only" are the same block, and
        // asking for the first is how that invariant gets stated rather than
        // assumed.
        private string? SelectedMessageText()
        {
            foreach (var turn in _turns)
            {
                foreach (var block in turn.Blocks)
                {
                    if (!string.IsNullOrEmpty(block.SelectedText)) return block.SelectedText;
                }
            }

            return null;
        }

        // Every rule that spans more than one bubble, applied to a block as
        // TurnView builds it.
        private void Adopt(SelectableTextBlock block)
        {
            // Only one bubble may hold a selection at a time, so that the copy
            // gesture is never ambiguous about which of two highlighted
            // passages it meant. Tunnel, so the previous selection is gone
            // before SelectableTextBlock's own handler starts the new one.
            block.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) => SelectOnly(block),
                RoutingStrategies.Tunnel);

            // One menu shared by every bubble rather than one built per
            // paragraph: a long transcript is hundreds of paragraphs, and they
            // would be hundreds of identical two-item menus. Avalonia sets
            // Target to whichever control opened it, which is the only thing
            // the two items need to know.
            block.ContextFlyout = _bubbleMenu;
        }

        // Right-click on a message: copy what is selected, or the message
        // whole.
        //
        // The second item is not a convenience — it is the answer to the one
        // real limit of doing this with SelectableTextBlock. A rendered reply
        // is a stack of controls (a paragraph here, a code block there) and
        // Avalonia has no selection that spans two of them, so a drag can only
        // ever take one of those pieces. "Copy message" is how a person gets
        // the whole thing, and it hands back the original Markdown rather than
        // the rendering, which is what anyone pasting it into a terminal or an
        // editor actually wants.
        // Built once with the panel rather than on first use. Lazy would save
        // a two-item menu on a panel nobody right-clicks, and cost a null to
        // reason about on every path that asks whether it is open — including
        // the deactivate guard, which runs whether or not a bubble was ever
        // drawn.
        private readonly MenuFlyout _bubbleMenu;

        private MenuFlyout BuildBubbleMenu()
        {
            var copy = new MenuItem { Header = "Copy" };
            copy.Click += (_, _) =>
            {
                var text = SelectedMessageText();
                if (text is not null) _ = CopyToClipboardAsync(text);
            };

            var copyAll = new MenuItem { Header = "Copy message" };
            copyAll.Click += (_, _) =>
            {
                var text = MessageTextOf(_bubbleMenu.Target as SelectableTextBlock);
                if (!string.IsNullOrEmpty(text)) _ = CopyToClipboardAsync(text!);
            };

            var menu = new MenuFlyout { ItemsSource = new[] { copy, copyAll } };

            // Greyed rather than hidden when there is no selection, so the menu
            // is the same shape every time it opens and "Copy" is where it was
            // last time.
            menu.Opening += (_, _) => copy.IsEnabled = SelectedMessageText() is not null;

            return menu;
        }

        // The Markdown a bubble was drawn from, found by asking which turn
        // owns the block the menu opened on.
        private string? MessageTextOf(SelectableTextBlock? block)
        {
            if (block is null) return null;

            foreach (var turn in _turns)
            {
                foreach (var candidate in turn.Blocks)
                {
                    if (ReferenceEquals(candidate, block)) return turn.Text;
                }
            }

            return null;
        }

        // Whether a bubble's context menu is standing open.
        //
        // Asked by the deactivate handler for the same reason it already asks
        // about the enlarged portrait: a menu is its own window on this
        // platform, so opening one deactivates the panel, and hiding the panel
        // out from under a menu the user just opened would be a strange answer
        // to a right-click.
        private bool ContextMenuIsOpen => _bubbleMenu.IsOpen;

        // Everything except the block a drag has just started in.
        //
        // Avalonia selections are per-control and know nothing about each
        // other, so without this every paragraph a person had ever dragged
        // across would stay highlighted and the copy gesture would have to pick
        // between them.
        private void SelectOnly(SelectableTextBlock pressed)
        {
            foreach (var turn in _turns)
            {
                foreach (var block in turn.Blocks)
                {
                    if (!ReferenceEquals(block, pressed)) block.ClearSelection();
                }
            }
        }

        // Excluded from coverage for the same reason as TryClipboardText above:
        // the catch is the platform's clipboard refusing, which a headless
        // clipboard never does. The decision in front of it — whether this text
        // is copied at all — is the half worth covering, and is covered.
        [ExcludeFromCodeCoverage]
        private async Task CopyToClipboardAsync(string text)
        {
            var clipboard = Clipboard;
            if (clipboard is null) return;

            try { await clipboard.SetTextAsync(text); }
            catch { }
        }

        // Whether the paste this preempted turns out to be a picture can
        // only be known asynchronously, so the keystroke is always taken
        // first and one of two things is done with it here: a picture
        // becomes a pending attachment, and anything else — plain text, or
        // a clipboard with nothing this app can read — is pasted by hand,
        // since the TextBox's own paste handler never got the chance to.
        private async Task HandlePasteAsync()
        {
            var clipboard = Clipboard;
            if (clipboard is null) return;

            var bitmap = await TryClipboardBitmap(clipboard);

            if (bitmap is not null)
            {
                await AttachImageAsync(bitmap);
                return;
            }

            var text = await TryClipboardText(clipboard);

            if (!string.IsNullOrEmpty(text)) PasteText(text);
        }

        // Excluded from coverage: both exist to be a try/catch around another
        // process's clipboard, and neither catch is arrangeable here. Avalonia's
        // headless clipboard is a real implementation rather than a fake that can
        // be told to fail, so there is no way to make TryGetBitmapAsync or
        // TryGetTextAsync throw on cue — and what they throw for is a clipboard
        // owner that has gone away or is handing over a format it cannot actually
        // produce, which belongs to whatever app was copied from.
        //
        // The paste paths themselves are covered: a bitmap arriving is
        // AttachImageAsync, plain text is PasteText, and an empty clipboard is the
        // null both of these return.
        [ExcludeFromCodeCoverage]
        private static async Task<Bitmap?> TryClipboardBitmap(IClipboard clipboard)
        {
            try { return await clipboard.TryGetBitmapAsync(); }
            catch { return null; }
        }

        [ExcludeFromCodeCoverage]
        private static async Task<string?> TryClipboardText(IClipboard clipboard)
        {
            try { return await clipboard.TryGetTextAsync(); }
            catch { return null; }
        }

        // What TextBox.Paste() would have done with the same string: replace
        // the selection, or insert at the caret when there isn't one.
        private void PasteText(string text)
        {
            var current = Input.Text ?? "";
            var start = Math.Clamp(Math.Min(Input.SelectionStart, Input.SelectionEnd), 0, current.Length);
            var end = Math.Clamp(Math.Max(Input.SelectionStart, Input.SelectionEnd), 0, current.Length);

            Input.Text = current[..start] + text + current[end..];
            Input.CaretIndex = start + text.Length;
        }

        // Saved to disk immediately rather than held as a bitmap until Send:
        // Send needs a path to type into the terminal, and writing it once
        // here means a picture that sits pasted for an hour is written once
        // rather than re-encoded at the moment it is finally needed.
        //
        // The encode itself runs off the UI thread — the same reasoning
        // TurnView.LoadImage gives for decoding a received picture there:
        // a full-screen screenshot is large enough that PNG-encoding it is a
        // visible hitch on the thread that draws, and nothing here needs the
        // result before the next frame.
        private async Task AttachImageAsync(Bitmap bitmap)
        {
            var path = await Task.Run(() => ChatAttachments.Save(bitmap));

            _pendingImages.Add(new PendingImage(path, bitmap));
            Attachments.IsVisible = true;
        }

        private void Attachment_Remove_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is not PendingImage image) return;

            _pendingImages.Remove(image);
            Attachments.IsVisible = _pendingImages.Count > 0;
        }

        // What a pasted picture looks like before it has been sent: a path
        // already on disk (see AttachImage), and the same decoded bitmap the
        // clipboard handed over, small enough at 40pt that reusing it as
        // its own thumbnail costs nothing worth a second decode.
        private sealed record PendingImage(string Path, Bitmap Thumbnail);
        // Only while the input's first word is still being typed and starts
        // with "/" — a slash command is the whole message, not something
        // that can appear after other text, so anything past the first space
        // isn't a command being completed any more.
        private void UpdateSlashSuggestions()
        {
            var commands = SlashCommands;
            if (commands.Count == 0) { HideSlashSuggestions(); return; }

            var text = Input.Text ?? "";
            var caret = Math.Clamp(Input.CaretIndex, 0, text.Length);
            var token = text[..caret];

            if (token.Length == 0 || token[0] != '/' || token.Contains(' ') || token.Contains('\n'))
            {
                HideSlashSuggestions();
                return;
            }

            _slashMatches = commands
                .Where(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Also closes once the only match is exactly what's already
            // typed — otherwise finishing a command by hand and pressing
            // Enter would "accept" it into itself instead of sending.
            if (_slashMatches.Count == 0
                || (_slashMatches.Count == 1 && string.Equals(_slashMatches[0].Name, token, StringComparison.OrdinalIgnoreCase)))
            {
                HideSlashSuggestions();
                return;
            }

            _slashSelected = 0;
            RenderSlashSuggestions();
            SlashBox.IsVisible = true;
        }

        private static readonly IBrush SlashRowFill = new SolidColorBrush(Colors.Transparent);
        private static readonly IBrush SlashRowSelected = new SolidColorBrush(Color.Parse("#33FFFFFF"));

        private void RenderSlashSuggestions()
        {
            SlashList.ItemsSource = _slashMatches
                .Select((c, i) => new SlashSuggestionView(c, i == _slashSelected ? SlashRowSelected : SlashRowFill))
                .ToList();
        }

        private void HideSlashSuggestions()
        {
            if (_slashMatches.Count == 0 && !SlashBox.IsVisible) return;

            _slashMatches = new List<SlashCommand>();
            _slashSelected = 0;
            SlashBox.IsVisible = false;
            SlashList.ItemsSource = null;
        }

        // Replaces the token being completed with the chosen command, the
        // way every other editor's autocomplete does — not sent outright.
        // Deciding a bare "/rename" is done and should go is the same
        // judgement call Send() already leaves to whoever is typing.
        private void AcceptSlashSuggestion(SlashCommand command)
        {
            var text = Input.Text ?? "";
            var caret = Math.Clamp(Input.CaretIndex, 0, text.Length);
            var rest = text[caret..];
            var replacement = command.Name + " ";

            Input.Text = replacement + rest;
            Input.CaretIndex = replacement.Length;

            // Last, not first: setting Text above already re-ran this via
            // TextChanged, and calling it again here is what makes the
            // outcome "closed" regardless of what that intermediate pass
            // computed.
            HideSlashSuggestions();
            Input.Focus();
        }

        private void SlashSuggestion_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is SlashSuggestionView view) AcceptSlashSuggestion(view.Command);
        }

        private sealed record SlashSuggestionView(SlashCommand Command, IBrush RowFill)
        {
            public string Name => Command.Name;
            public string Description => Command.Description;
        }

        // Cmd+= / Cmd+- / Cmd+0, Ctrl on Windows — see ChatZoom, which owns
        // both the key mapping and the ladder of sizes.
        private void OnZoomKeyDown(object? sender, KeyEventArgs e)
        {
            var command = ChatZoom.Gesture(e.Key, e.KeyModifiers);
            if (command == ChatZoom.Command.None) return;

            // Handled even when the size does not change — at either end of the
            // ladder the gesture was still understood, and letting it travel on
            // would put a "0" in the composer for someone pressing Cmd+0 twice.
            e.Handled = true;

            var next = ChatZoom.Apply(command, OrbweaverSettings.ChatTextScale);
            if (next.Equals(_textScale.Value)) return;

            OrbweaverSettings.ChatTextScale = next;
            ApplyTextScale();
        }

        // Pushes the saved scale into everything that draws chat text. Called
        // when the panel is built, after a zoom keystroke, and from the
        // settings slider through ReapplyTextScale below.
        private void ApplyTextScale()
        {
            var scale = OrbweaverSettings.ChatTextScale;

            _textScale.Value = scale;

            // The composer is chat text too — you read back what you typed. Its
            // heights go with it, or a doubled font size would be drawn into a
            // box still sized for one line of the old one.
            Input.FontSize = 11.5 * scale;
            Input.MinHeight = 26 * scale;
            Input.MaxHeight = 66 * scale;

            // The permission dialog sits between the transcript and the
            // composer and is the most important text in the window when it is
            // there at all — it is the thing being agreed to. PromptOptions
            // carries the size for its items, which have no handle of their own
            // (see the template's comment).
            PromptTitle.FontSize = 11 * scale;

            // A whole-number line height, so n lines are a whole number of pixels
            // tall. At the font's own height (13.431 at 11 pt) three lines measure
            // 40.29, layout rounding makes the box 40, and the text is then laid out
            // again to fit 40: the third line no longer fits and is not drawn. That
            // would drop the last line of an approval, which is where the request
            // id sits, on a display without sub-pixel layout.
            PromptTitle.LineHeight = Math.Ceiling(PromptTitle.FontSize * 1.25);
            PromptOptions.FontSize = 11 * scale;
            PromptElsewhere.FontSize = 10.5 * scale;

            // CB-267's box sits right over the composer and is read before
            // typing, so it scales with everything else that is read.
            InboundText.FontSize = 11 * scale;
            InboundPath.FontSize = 10 * scale;
            InboundAskLabel.FontSize = 11 * scale;
            InboundConfirmLabel.FontSize = 11 * scale;
            InboundCancelLabel.FontSize = 11 * scale;
            InboundResendLabel.FontSize = 11 * scale;

            foreach (var turn in _turns) turn.Rescaled();
        }

        // The settings slider changes the same number this window's keyboard
        // does, so every open panel has to hear about it — the scale is one
        // global setting, and two panels drawing it at different sizes would
        // be a bug you could only see by putting them side by side, which
        // pinning now makes easy. Visibility-blind on purpose: a panel that
        // exists but is hidden still holds rows that will be shown again
        // without being rebuilt.
        internal static void ReapplyTextScale()
        {
            foreach (var panel in Panels) panel.ApplyTextScale();
        }

        private void OnPanelKeyDown(object? sender, KeyEventArgs e)
        {
            var isClose = e.Key == Key.Escape
                || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta));

            if (!isClose) return;

            e.Handled = true;

            // Escape while recording stops the recording and leaves the panel
            // up. Not a new rule: ScheduleFlyoutHide already refuses to hide the
            // arc while recording, because the control that stops it is on it. A
            // second Escape then dismisses.
            if (_owner?.IsRecording == true)
            {
                _owner.ToggleRecording();
                return;
            }

            Dismiss();
        }

        private void Send()
        {
            var text = (Input.Text ?? "").Trim();
            if ((text.Length == 0 && _pendingImages.Count == 0) || _session is null) return;

            // CB-35: no longer cleared here. It used to be — unconditionally,
            // before the send was even attempted — which is exactly the bug:
            // a session that cannot send at all (replying switched off, no
            // pane, nobody in the room) still returns from SendAsync having
            // written nothing but a note, and the sentence that produced that
            // note was already gone from the box by the time the note
            // appeared. SendAndClearOnSuccessAsync below clears it once the
            // send has actually told us whether it went anywhere.
            var session = _session;

            var images = _pendingImages.Select(p => p.Path).ToList();
            _pendingImages.Clear();
            Attachments.IsVisible = false;

            // Sending is the one time the view should jump to the bottom
            // regardless. The autoscroll rule elsewhere deliberately leaves you
            // where you are reading, but a message you just sent landing
            // somewhere off screen reads as it not having sent at all.
            ScrollToEndAfterLayout();

            // Deliberately not inserting the user's turn here: the session
            // raises TurnAdded for it, so one thing owns the transcript and a
            // failed send leaves nothing behind to clean up beyond the note it
            // writes itself.
            _ = SendAndClearOnSuccessAsync(session, text, images);
        }

        // The part of Send() that has to wait for an answer before deciding
        // what happens to the box.
        //
        // Pictures are cleared from the composer in Send() itself regardless
        // of outcome, not held back the way text is: they are already written
        // to disk (see AttachImageAsync), so nothing is lost by clearing the
        // strip, and a failed image send still explains itself in the
        // transcript the same way a failed text-only one does. Retrying a
        // failed picture means pasting it again, which is unchanged from
        // before this ticket and out of its scope — CB-35 is about the one
        // thing that had no copy anywhere else once the box was cleared: the
        // sentence itself.
        //
        // Not disabling Input while this is in flight, on purpose. Every
        // implementation's SendAsync returns as soon as the message has been
        // handed to a gateway, typed into a terminal, or queued on a socket —
        // none of them wait for a reply — so the window this covers is a
        // single write, not the minutes a mirror's first fetch can take (see
        // IRemoteChatFetchWait). Disabling the box for that instant would add
        // a visible flicker for no protection worth having, and would need
        // its own recovery path if the awaited call never completed.
        private async Task SendAndClearOnSuccessAsync(
            IRemoteChatSession session, string text, List<string> images)
        {
            var outcome = images.Count > 0 && session is IRemoteChatImages { CanSendImages: true } withImages
                ? await withImages.SendWithImagesAsync(text, images)
                : await session.SendAsync(text);

            if (outcome == ChatSendOutcome.Sent) Input.Text = "";
        }

        // The button. Everything it decides beyond "am I already speaking" now
        // belongs to SpeechRequest, which the orb's own speak button also enters
        // — see that file's header for why this is one path and not two.
        //
        // No longer excluded from coverage: the utterance moved out with the
        // rest, so what is left here is the cancel branch, the lookup of the
        // last assistant turn, and a call. All three are reachable headlessly.
        internal void SpeakLatest()
        {
            if (TextToSpeech.IsSpeaking)
            {
                TextToSpeech.Cancel();
                return;
            }

            var last = _turns.LastOrDefault(t => t.Role == ChatRole.Assistant);
            SpeechRequest.Speak(last?.Text, _session?.SessionId);
        }

        private void ApplySpeakState(TextToSpeech.SpeakState state)
        {
            SpeakFill.Fill = state switch
            {
                TextToSpeech.SpeakState.Speaking => SpeakActiveFill,
                TextToSpeech.SpeakState.Preparing => SpeakPreparingFill,
                _ => IdleFill
            };

            var (glyph, mark) = SymbolMarks.SpeakLook(state);

            SpeakGlyph.Text = glyph;
            SpeakGlyph.IsVisible = glyph is not null;
            SpeakStopMark.Data = mark is null ? null : StreamGeometry.Parse(mark);
            SpeakStopMark.IsVisible = mark is not null;
        }

        private static readonly IBrush MuteActiveFill = new SolidColorBrush(Color.Parse("#E0A04040"));

        // The mute button's whole look, from the one flag: glyph, a desaturated
        // red fill while muted (readable beside the amber Preparing and blue
        // Speaking fills), and a tooltip naming the chord that is really bound.
        private void ApplyMuted(bool muted)
        {
            MuteGlyph.Text = SymbolMarks.MuteLook(muted);
            MuteFill.Fill = muted ? MuteActiveFill : IdleFill;
            ToolTip.SetTip(MuteButton, SymbolMarks.MuteTip(muted, HotkeyActions.Bound(HotkeyAction.ToggleMute)));
        }

        private bool _loadingOlder;

        // Older messages, fetched when the transcript is scrolled to its top.
        //
        // The awkward part is not the fetch, it is that content appearing above
        // where you are reading pushes what you were reading down the screen.
        // So the extent is measured before and after, and the offset is moved by
        // the difference — which leaves the same words under the pointer and the
        // new ones above, the way every message app that does this behaves.
        private async Task LoadOlderAsync()
        {
            if (_loadingOlder) return;
            if (_session is not IRemoteChatBacklog chat || !chat.HasMore) return;

            _loadingOlder = true;

            try
            {
                var before = Scroll.Extent.Height;

                if (!await chat.LoadOlderAsync(CancellationToken.None)) return;

                // The prepend itself happens on the event below; this only has
                // to restore the position once layout has caught up with it.
                //
                // Twice, at two priorities: one yield gets the items into the
                // tree, and the measure that gives them height happens after
                // that. Measuring too early reads the old extent, and the
                // correction is then silently zero — the failure looks like the
                // scroll jumping rather than like a missing yield.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var grew = Scroll.Extent.Height - before;
                if (grew > 0) Scroll.Offset = Scroll.Offset.WithY(Scroll.Offset.Y + grew);
            }
            finally
            {
                _loadingOlder = false;
            }
        }

        private void OnHistoryPrepended(int count)
        {
            if (_session is null) return;

            // Inserted at the front in order, rather than rebuilding the whole
            // list: rebuilding would discard every already-fetched picture and
            // start them downloading again.
            for (var i = 0; i < count && i < _session.History.Count; i++)
            {
                _turns.Insert(i, new TurnView(_session.History[i], _defaultBubble, _soleSpeaker, Adopt, _textScale, AskingKey()));
            }
        }

        private void OnHistoryReplaced()
        {
            if (_session is null) return;

            _turns.Clear();
            foreach (var turn in _session.History) _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker, Adopt, _textScale, AskingKey()));

            // Re-read rather than left at what Bind found.
            //
            // A remote panel can change what it *is* while open: it starts as a
            // messaging channel and upgrades to a live view of the far session
            // the moment that machine's Buddy answers, and the box's own
            // description of where a message goes changes with it — from
            // "message this session" to "type into its terminal". Read once at
            // bind, the box would go on describing the panel it used to be.
            ApplyComposerAffordances(_session);

            // Straight to the bottom rather than the pinned-only rule: a
            // transcript that has just been replaced wholesale has no scroll
            // position worth preserving, and the newest turn is the one you
            // opened the panel to read.
            ScrollToEndAfterLayout();
        }

        private void OnTurnAdded(ChatTurn turn)
        {
            _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker, Adopt, _textScale, AskingKey()));

            // Your own turn always brings the view with it; everything else
            // respects where you were reading.
            if (turn.Role == ChatRole.User)
            {
                ScrollToEndAfterLayout();
                return;
            }

            ScrollToEndIfPinned();
        }

        // CB-270: a run's live rows replaced by what history says at its end.
        // The rows that take their place arrive as ordinary TurnAdded.
        private void OnTurnsRemoved(int from, int count)
        {
            for (var i = 0; i < count && from < _turns.Count; i++) _turns.RemoveAt(from);
        }

        private void OnTurnUpdated(ChatTurn turn)
        {
            // Nothing to do to the collection: the view wraps the same object
            // and forwards its own change notification, so no row is recreated
            // and nothing can steal focus by being re-templated.
            ScrollToEndIfPinned();
        }

        private void OnStateChanged(RemoteChatState state)
        {
            StateDot.Fill = state switch
            {
                RemoteChatState.Connected => ConnectedFill,
                RemoteChatState.Connecting => SpeakPreparingFill,
                RemoteChatState.Error => RecordingFill,
                _ => IdleFill
            };
        }

        private void OnPromptChanged() => ApplyPrompt();

        // The far machine's name arrives on the roster, which normally lands
        // after the panel is already open — so the chip is redrawn rather than
        // only being set once at Bind.
        private void OnMachineChanged()
        {
            if (_owner is null) return;

            ApplyKindChip(_owner.KindGlyphText, _owner.KindMarkData, _owner.KindLabel, _owner.PresenceLabel,
                (_session as IRemoteChatMachine)?.MachineName);

            // The same answer arriving changes the meta line too, and it is the
            // half that changes colour: until the roster names the machine, a
            // mirrored session is indistinguishable from a local one and the
            // line says this machine in the ordinary dim ink. Left out at
            // first, which meant the panel picked the accent up only on the
            // next hook write — and a mirrored session's hook writes happen on
            // the other machine, so for that case there was no next one.
            ApplyMeta();
        }

        // What the chip says, including which machine when that is known.
        //
        // "another machine" is true and is not an answer: somebody with two of
        // them cannot act on it, which is what prompted this. The machine name
        // replaces the generic word rather than being appended to it — "⇄
        // another machine · avatar" says the same thing twice and is longer than
        // the header has room for.
        //
        // Falls back the moment the name is unknown, which is the ordinary case
        // for the first second a panel is open: the roster has not answered yet,
        // and naming no machine beats naming a guessed one.
        //
        // Pure and static so the wording is a unit test rather than a
        // screenshot. See CB-59.
        //
        // A null glyph means the kind's mark is drawn beside the text instead
        // (see ApplyKindChip), so the words stand alone rather than behind a
        // gap where a character used to be.
        internal static string KindChipLabel(
            string? glyph, string? kind, string? presence, string? machine)
        {
            if (kind is null) return "";

            var what = string.IsNullOrWhiteSpace(machine) ? kind : machine;
            var words = presence is null ? what : $"{what} · {presence}";

            return glyph is null ? words : $"{glyph}  {words}";
        }

        // The chip wears the orb's own either-or: the drawn mark when the kind
        // has one, the typed character when it does not (@, #). Typing ⏱ or ☁
        // here is the CB-173 defect in a second place — on Windows they fall
        // back to Segoe UI Emoji and ignore the chip's ink entirely.
        private void ApplyKindChip(
            string? glyph, string? mark, string? kind, string? presence, string? machine)
        {
            KindChipMark.Data = mark is null ? null : StreamGeometry.Parse(mark);
            KindChipMark.IsVisible = mark is not null;
            KindChipText.Text = KindChipLabel(mark is null ? glyph : null, kind, presence, machine);
        }


        // --- the wait, while it is happening --------------------------------

        // Ticks the elapsed figure while a fetch is in flight.
        //
        // A timer rather than a binding because the thing that changes is the
        // clock, not the session: nothing raises an event once a second, and
        // asking the session to would put a timer in the transport instead of
        // in the one place that draws it.
        private DispatcherTimer? _waitTick;

        private void OnWaitChanged()
        {
            if (_session is IRemoteChatFetchWait waiting) ShowWait(waiting);
            else HideWait();
        }

        private void ShowWait(IRemoteChatFetchWait waiting)
        {
            if (waiting.WaitingSince is not { } since)
            {
                HideWait();
                return;
            }

            FetchWaitBox.IsVisible = true;
            FetchWaitHint.Text = RemoteControlChatSession.WaitHint;

            void Paint() => FetchWaitText.Text = RemoteControlChatSession.WaitLabel(
                DateTimeOffset.Now - since, waiting.WaitingFor);

            Paint();

            // A second, because that is the resolution of what it says. Half of
            // one would redraw twice for every change and a longer one would
            // make a counter that is supposed to prove liveness look stopped.
            _waitTick ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _waitTick.Tick -= OnWaitTick;
            _waitTick.Tick += OnWaitTick;
            _waitTick.Start();
        }

        private void OnWaitTick(object? sender, EventArgs e)
        {
            if (_session is IRemoteChatFetchWait waiting
                && waiting.WaitingSince is { } since)
            {
                FetchWaitText.Text = RemoteControlChatSession.WaitLabel(
                    DateTimeOffset.Now - since, waiting.WaitingFor);
                return;
            }

            HideWait();
        }

        private void HideWait()
        {
            _waitTick?.Stop();
            FetchWaitBox.IsVisible = false;
        }


        // A dialog the session has stopped on, or nothing.
        //
        // The options are shown whether or not replying is switched on, and
        // clicking one while it is off produces the same explanation in the
        // transcript that sending a message would. Same reasoning as the
        // composer: the panel doesn't hide what is happening because you can't
        // act on it yet, and the session — which owns the rule — is the thing
        // that states it.
        // CB-270 Stage 6. A prompt marked ArmedActivation (an approval: an answer cannot
        // be taken back) is armed, not instant: its options ignore presses for a
        // moment after the prompt first shows or its content changes, so a click
        // that was aimed at something else, or at what was there a moment ago,
        // cannot answer it; and it answers on release over the same option that was
        // pressed, so a press that slid off, or a press and release on different
        // options, answers nothing. A permission bar read off a terminal is not
        // marked, and answers on a press as it always has.
        //
        // The clock is a seam so a test does not sleep.
        internal static Func<DateTime> PromptClock { get; set; } = () => DateTime.UtcNow;
        internal static readonly TimeSpan PromptArmDelay = TimeSpan.FromMilliseconds(500);

        private string? _promptSignature;
        private DateTime _promptShownAt;
        private bool _promptArmed;
        private Control? _pressedPromptOption;

        private void ApplyPrompt()
        {
            var prompt = (_session as IRemoteChatPrompts)?.Prompt;

            if (prompt is null)
            {
                _promptSignature = null;
                _promptArmed = false;
                _pressedPromptOption = null;
                PromptBox.IsVisible = false;
                PromptOptions.ItemsSource = null;
                return;
            }

            // The arming clock restarts whenever what is being asked changes, not
            // only when the box first appears: a new request replacing one under
            // the pointer is exactly the moment a click must not carry over.
            var signature = prompt.Title + "\n" + string.Join("\n",
                prompt.Options.Select(o => o.Key + "|" + o.ApprovalId));
            if (signature != _promptSignature)
            {
                _promptSignature = signature;
                _promptShownAt = PromptClock();
                _pressedPromptOption = null;
            }

            _promptArmed = prompt.ArmedActivation;

            PromptTitle.Text = prompt.Title;
            PromptElsewhere.Text = prompt.ElsewhereLabel ?? "Answer in the terminal";

            // No options means the screen couldn't be read. The box still
            // appears — something is waiting and the transcript won't say so —
            // but the only thing offered is the terminal.
            PromptOptions.ItemsSource = prompt.Options.Count > 0 ? prompt.Options : null;
            PromptOptions.IsVisible = prompt.Options.Count > 0;

            PromptBox.IsVisible = true;
        }

        private void PromptOption_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if (_promptArmed)
            {
                // Only remembered: the answer is given on release. And only a press
                // that itself came after the arming window is remembered, so one
                // that began inside it and is held past it answers nothing.
                _pressedPromptOption = PromptClock() - _promptShownAt < PromptArmDelay ? null : sender as Control;
                return;
            }

            ChoosePromptOption(sender as Control);
        }

        private void PromptOption_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_promptArmed) return;

            e.Handled = true;

            var pressed = _pressedPromptOption;
            _pressedPromptOption = null;

            if (sender is not Control option || !ReferenceEquals(pressed, option)) return;

            // Released over the same option, not merely delivered to it by capture.
            var at = e.GetPosition(option);
            if (at.X < 0 || at.Y < 0 || at.X > option.Bounds.Width || at.Y > option.Bounds.Height) return;

            if (PromptClock() - _promptShownAt < PromptArmDelay) return;

            ChoosePromptOption(option);
        }

        private void ChoosePromptOption(Control? control)
        {
            if (control?.DataContext is not ChatPromptOption option) return;
            if (_session is not IRemoteChatPrompts prompts) return;

            _ = prompts.AnswerAsync(option);
        }

        private void PromptElsewhere_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if (_session is not IRemoteChatPrompts prompts) return;

            // Read before the call: answering may change the prompt.
            var staysOpen = prompts.Prompt?.ElsewhereLabel is not null;

            prompts.AnswerElsewhere();

            // A prompt with no terminal to go to answers "where else" with a note in
            // the transcript, which is the thing to read, so the panel stays.
            if (staysOpen) return;

            // Dismissed, because this asked to be somewhere else. Leaving the
            // panel up over the terminal it just brought forward would be
            // covering the dialog it sent you to answer.
            //
            // Dismiss rather than HideNow so a pinned panel goes away properly
            // rather than being hidden: a hidden pinned panel is in the
            // registry, is not the transient, and holds no session, so nothing
            // would ever show it again.
            Dismiss();
        }

        // Whether the view is sitting at the bottom — read *now*, on the same
        // tick as the turn that prompted the question, rather than after a
        // yield.
        //
        // The row that was just added or grown has not been measured yet, so the
        // extent still describes the transcript you were reading and an offset
        // at the bottom of it still reads as the bottom. A yield later the new
        // content has height, the extent has grown past where you are sitting,
        // and the same position answers "no" — so autoscroll switches itself off
        // partway through a streaming reply and the panel is left in the middle
        // of it. That is the intermittent half of the same complaint the bind
        // path above fixes: it depended on whether measure had happened yet.
        private bool IsPinnedToBottom =>
            Scroll.Offset.Y >= Scroll.Extent.Height - Scroll.Viewport.Height - 8;

        // Only when already at the bottom, so reading back through a long reply
        // isn't yanked forward as it grows.
        private void ScrollToEndIfPinned()
        {
            if (IsPinnedToBottom) ScrollToEndAfterLayout();
        }

        // To the newest message, after layout has caught up with the rows that
        // put it there.
        //
        // Starts with two ticks, for the reason LoadOlderAsync spells out at
        // length: one yield gets the rows into the visual tree, and the measure
        // that gives them height happens after that. A single ScrollToEnd() at
        // Loaded priority — which is what every one of these call sites used to
        // be — reads the extent the transcript had *before* those rows existed
        // and lands short of the bottom by however much was added. Loading a
        // whole transcript at once adds a lot, so "short of the bottom" is not a
        // few pixels; it is the middle of the conversation.
        //
        // CB-51 stopped there, on the assumption two ticks were always enough.
        // CB-160 is what happens when they aren't: an inline picture decoding
        // on a worker thread (TurnView.LoadImage/LoadImageBytes both post the
        // decoded bitmap back after a Task.Run, with no promise about which
        // tick that lands on), a markdown block reflowing, an attachment row
        // resizing — any of them can grow a turn's height after the second
        // tick, and a fixed count has no third correction waiting for it. So
        // the second tick now hands off to SettleScrollToEnd, which keeps
        // re-scrolling across further layout passes until the extent genuinely
        // stops changing rather than assuming it already has.
        //
        // The first two calls are kept rather than going straight to the
        // settle loop: they put the view roughly right on the frame the panel
        // appears, so the loop is a correction for whatever arrives late
        // rather than the only thing moving the view at all.
        private void ScrollToEndAfterLayout()
        {
            var generation = ++_scrollSettleGeneration;

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _scrollSettleGeneration) return;
                Scroll.ScrollToEnd();
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _scrollSettleGeneration) return;
                    Scroll.ScrollToEnd();
                    SettleScrollToEnd(generation);
                }, DispatcherPriority.Background);
            }, DispatcherPriority.Loaded);
        }

        // The correction CB-160 added: rather than trusting that the two
        // ticks above were the last time this turn's rows could change height,
        // this keeps re-scrolling to the end across further dispatcher passes
        // until ScrollSettleTracker says the extent has held steady, instead
        // of assuming a fixed count was always enough.
        //
        // Self-driven — a chain of Dispatcher.Post calls, not a subscription
        // to Avalonia's LayoutUpdated event — and deliberately so. The first
        // version of this watched LayoutUpdated instead, on the reasoning
        // that it fires exactly when a layout pass produces a new
        // measurement. It does, but only when something actually invalidates
        // layout, and once every row has already reached its final height
        // nothing does — so the handler stayed subscribed indefinitely,
        // waiting for confirmation readings that would never arrive on their
        // own. It only got them once *something else* touched layout later
        // (a reader scrolling back up, a reply landing in a different
        // conversation), which this settle loop then mistook for one of its
        // own readings and used to justify snapping back to the bottom —
        // fighting exactly the interactions ScrollToEndIfPinned and the
        // pinned-reply tests below exist to protect. Driving the readings
        // itself instead means each one happens on schedule whether or not
        // layout actually changed, so the ordinary case — nothing left to
        // settle — reaches ScrollSettleTracker's stable count in a small,
        // bounded number of ticks and stops for good, while a genuinely
        // late-arriving row (this ticket's actual case) still resets that
        // count for as long as the extent keeps moving.
        //
        // Guarded by the same generation this panel's reuse already needs
        // elsewhere: if a different session gets bound (a new
        // ScrollToEndAfterLayout call, bumping the counter) while this chain
        // is still running, the next scheduled tick notices and stops rather
        // than going on fighting an offset that belongs to a conversation no
        // longer on screen.
        private void SettleScrollToEnd(int generation)
        {
            var tracker = new ScrollSettleTracker();

            void Tick()
            {
                if (generation != _scrollSettleGeneration) return;

                Scroll.ScrollToEnd();
                tracker.Observe(Scroll.Extent.Height);

                if (tracker.ShouldKeepWatching) Dispatcher.UIThread.Post(Tick, DispatcherPriority.Background);
            }

            Tick();
        }

        // The pin toggle, exposed so a test can drive the state change without
        // synthesizing a click on the header — the same reason UpdateFrom is
        // reachable on OrbWindow. The button calls this too, so there is one
        // path rather than a test-only one beside the real one.
        internal void TogglePin()
        {
            if (_pinned) Unpin();
            else Pin();
        }

        private void Pin()
        {
            _pinned = true;
            ApplyPinAffordance();

            // The window does not move. "Pin" means leave this exactly where
            // it is, and a panel that relocated itself to some placement of its
            // own the moment it was told to stay would be answering a question
            // nobody asked. Where the *next* panel opens is a different problem
            // and lives in ChatPanelPlacement.
            //
            // The orb gets its hover arc back, which reads backwards until you
            // say the rule out loud. The arc is suppressed while a chat is open
            // because the panel is drawn ChatPanelGap from the orb's centre —
            // over exactly the radius the arc wants. So the rule is not "a chat
            // is open", it is "the panel has that space", and a pinned panel
            // does not: it can be dragged to the other side of the screen, and
            // the next click on this orb opens a transient beside it anyway.
            _owner?.SetChatOpen(false);

            // CB-111: recorded the instant this becomes true, not only on the
            // first drag afterward — a panel pinned and never moved again
            // still has to come back in the same spot next run, and
            // PositionChanged (see the constructor) never fires for a window
            // that hasn't gone anywhere.
            if (_owner is { PositionKey.Length: > 0 } owner)
            {
                OrbweaverSettings.SetPinnedChatPanelPosition(owner.PositionKey, Position.X, Position.Y);
            }
        }

        private void Unpin()
        {
            // At most one unpinned panel, so whatever holds that role now has
            // to give it up before this window takes it. Dissolved rather than
            // hidden: a hidden unpinned panel is still unpinned, so Transient
            // would go on handing *it* out — every orb click, every arrangement
            // hide — while this window sat in front of the user believing it
            // was the transient. Two panels claiming one role is worse than
            // building a window again on the next orb click.
            if (Transient is { } other && !ReferenceEquals(other, this)) other.Dissolve();

            _pinned = false;
            ApplyPinAffordance();

            // In place, not repositioned: unpinning says "this one can behave
            // normally again", not "put it back". It stays where it was dragged
            // until the next Reposition moves it, and the orb goes back to
            // treating the arc's space as spoken for.
            _owner?.SetChatOpen(true);

            // CB-111: the only place a saved position is forgotten. Unpinning
            // is an explicit "stop keeping this open", and it is the one event
            // that can actually tell that apart from a session ending or the
            // app quitting — both of those close this same window (Dissolve,
            // reached through CloseFor or the desktop lifetime's Shutdown()),
            // and a clear reachable from there would erase every pin on every
            // restart, which is the one thing this ticket exists to prevent.
            // See ClearPinnedChatPanelPosition's own comment.
            if (_owner is { PositionKey.Length: > 0 } owner)
            {
                OrbweaverSettings.ClearPinnedChatPanelPosition(owner.PositionKey);
            }
        }

        private void ApplyPinAffordance()
        {
            // The same blue the speak button wears while it is doing something,
            // for the same reason: a filled circle in this header means "this
            // control is currently on".
            PinFill.Fill = _pinned ? SpeakActiveFill : IdleFill;

            ToolTip.SetTip(PinButton, _pinned ? "Unpin" : "Pin — keep this chat open");
        }

        // What the close button and Escape mean, which is not the same thing
        // for the two kinds of panel. The transient is hidden and kept, because
        // it is the window every future orb click reuses. A pinned one is gone
        // for good — it was a window the user deliberately created, and hiding
        // it would leave something in the registry that nothing can ever show
        // again.
        private void Dismiss()
        {
            if (_pinned) Dissolve();
            else HideNow();
        }

        // Tear-down for a panel that is not coming back: unbound and hidden the
        // ordinary way first, so nothing is left subscribed, then out of the
        // registry and closed.
        private void Dissolve()
        {
            HideNow();

            // Before Close() as well as on the Closed event, so a caller that
            // reads Transient on the very next line sees the truth even if the
            // platform defers the event.
            Panels.Remove(this);
            AudioMute.Changed -= ApplyMuted;
            Close();
        }

        private void HideNow()
        {
            if (_session is not null) Drafts[_session.SessionId] = Input.Text ?? "";

            StopAvatarAnimation();

            // Nothing on screen to beat for. Without this the timer outlives the
            // panel and goes on scaling a hidden TextBlock 20 times a second for
            // as long as the app runs.
            StopHeartAnimation();

            AvatarPopup.Close();

            // Cleared with the panel, not left standing: the next session bound
            // here is very unlikely to be waiting on the same dialog, and a
            // stale one would offer buttons that answer nothing.
            PromptBox.IsVisible = false;
            PromptOptions.ItemsSource = null;

            // Detached while hidden. The transient panel stays alive between
            // openings, and a hidden panel left subscribed goes on
            // appending a row per event for a conversation nobody is watching —
            // the session's own history is bounded, this collection was not.
            // Bind rebuilds from History anyway, so there is nothing to keep.
            Unbind();

            _owner?.SetChatOpen(false);
            Hide();
        }

        // The row's own view. Exists so the template can bind colour, shape and
        // alignment per role without the transport's ChatTurn knowing what a
        // brush is — and so the three roles share one template instead of three.
        private sealed class TurnView : System.ComponentModel.INotifyPropertyChanged
        {
            private readonly ChatTurn _turn;
            private readonly Speaker? _soleSpeaker;
            private readonly Color? _defaultBubble;
            private readonly Action<SelectableTextBlock> _adopt;
            private readonly TextScale _scale;

            // soleSpeaker is who is talking when the transcript does not say.
            // A room stamps every turn with its speaker because there are
            // several; a one-to-one session — a Claude Code or Codex terminal,
            // or a single agent — stamps none, because there is only one and it
            // was obvious to whoever wrote the transport. It is not obvious in
            // the bubbles, which is the whole point of the chip.
            // adopt is how each selectable run of text gets handed to the
            // panel as it is built, for the rules that span more than one
            // bubble. Passed in rather than reached for through the singleton,
            // so a TurnView built by a test is a TurnView and not half a window.
            // askingKey is the gateway key of the conversation this row is in,
            // or null when the panel is not one OpenClaw conversation (a room,
            // another kind of session). Only the clip's refusal tooltip reads
            // it, to say a route was re-keyed (CB-277).
            private readonly string? _askingKey;

            public TurnView(ChatTurn turn, Color? defaultBubble, Speaker? soleSpeaker,
                Action<SelectableTextBlock> adopt, TextScale scale, string? askingKey = null)
            {
                _askingKey = askingKey;
                _turn = turn;
                _defaultBubble = defaultBubble;
                _soleSpeaker = soleSpeaker;
                _adopt = adopt;
                _scale = scale;

                turn.PropertyChanged += (_, e) =>
                {
                    // A streaming turn replaces its whole text, so the rendered
                    // Markdown has to be thrown away with it. Without this the
                    // first snapshot of a reply is the only one ever drawn.
                    if (e.PropertyName == nameof(ChatTurn.Text))
                    {
                        _body = null;
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Body)));
                    }

                    // CB-270: a tool line learns it failed only when the result
                    // arrives, after its row is drawn. The body is rebuilt because
                    // its runs carry the ink they were made with.
                    if (e.PropertyName == nameof(ChatTurn.IsError))
                    {
                        _body = null;
                        foreach (var name in new[] { nameof(Body), nameof(Ink), nameof(ErrorTip) })
                        {
                            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                        }
                    }

                    // A live turn can start with no picture and gain one once
                    // OpenClawChatSession.TryResolveLiveImage resolves it
                    // against the gateway's own history — this row already
                    // exists by then, so it has to notice rather than being
                    // recreated. !HasImage guards against loading twice were
                    // ImageUrl to change again after already resolving once.
                    if (e.PropertyName == nameof(ChatTurn.ImageUrl) && !HasImage
                        && !string.IsNullOrEmpty(turn.ImageUrl))
                    {
                        LoadImage();
                    }

                    // Same shape, for a picture resolved via
                    // OpenClawChatSession.TryResolveLocalMedia (CB-88) rather
                    // than a URL — an agent's own generated file, fetched as
                    // bytes rather than fetched from one.
                    if (e.PropertyName == nameof(ChatTurn.ImageBytes) && !HasImage
                        && turn.ImageBytes is { Length: > 0 } lateBytes)
                    {
                        LoadImageBytes(lateBytes);
                    }

                    // CB-93: ImageNote and ImageNoteDetail are set together
                    // (see LoadImage below), but only ImageNote's own setter
                    // raises a change — ImageNoteDetail is plain, the same
                    // pairing ChatTurn.ImageAlt already has with
                    // ImageBytes/ImageUrl. So the one event that does fire
                    // has to stand in for both bindings, HasImageNote (the
                    // row's own IsVisible) included.
                    if (e.PropertyName == nameof(ChatTurn.ImageNote))
                    {
                        PropertyChanged?.Invoke(this,
                            new System.ComponentModel.PropertyChangedEventArgs(nameof(HasImageNote)));
                        PropertyChanged?.Invoke(this,
                            new System.ComponentModel.PropertyChangedEventArgs(nameof(ImageNoteDetail)));
                    }

                    PropertyChanged?.Invoke(this, e);
                };

                if (!string.IsNullOrEmpty(turn.ImageUrl)) LoadImage();
                else if (turn.ImageBytes is { Length: > 0 } bytes) LoadImageBytes(bytes);

                // CB-274: a failed attachment whose label could not be turned
                // into a route (an empty one, or one that is only dots) has
                // nothing to fetch or ask about, but the user must still be
                // told the gateway never delivered it — a bare caption is the
                // exact symptom this ticket exists to remove. No path, so no
                // gateway question and no "also" sentence.
                else if (turn.Attachment is { Failed: true } lost
                         && turn.Confidence == MediaConfidence.High
                         && turn.ImageNote is null)
                {
                    turn.ImageNoteDetail = string.IsNullOrWhiteSpace(lost.Label) ? null : lost.Label;
                    turn.ImageNote = OpenClawMediaRefusal.ExplainDelivery(lost, null);
                }
            }

            // The turn this row was built from, so the panel can find a row
            // again by identity. Text is not unique enough — two "working…"
            // lines would be the same string.
            public ChatTurn Source => _turn;

            public ChatRole Role => _turn.Role;
            public string Text => _turn.Text;

            public bool HasText => !string.IsNullOrWhiteSpace(_turn.Text);

            // The rendered Markdown, rebuilt when the text changes.
            //
            // A control rather than a bound string because a reply has
            // structure — code blocks want a monospace box, list items want a
            // bullet and a hanging indent, and neither is expressible as one
            // TextBlock. Cached because OpenClaw streams: a snapshot arrives per
            // delta, and reparsing on read would reparse per layout pass too.
            private Control? _body;

            public Control Body => _body ??= BuildBody();

            private Control BuildBody()
            {
                // Thrown away with the body they belong to. A streaming turn
                // rebuilds its body per delta (see the PropertyChanged hook
                // above), and a list that kept growing across those rebuilds
                // would have the panel searching controls that are no longer
                // on screen for a selection the user cannot see.
                _blocks.Clear();

                var stack = new StackPanel { Spacing = 4 };

                foreach (var block in ChatMarkdown.Parse(_turn.Text))
                    stack.Children.Add(BuildBlock(block));

                // A turn whose text is only whitespace still needs something to
                // hand back; HasText hides it either way.
                if (stack.Children.Count == 0)
                    stack.Children.Add(Line(_turn.Text));

                return stack;
            }

            // Every selectable run of text in this bubble, in the order it is
            // drawn. The panel asks for these rather than walking the visual
            // tree: a turn knows what it built, whereas a tree walk would only
            // find what has been realised and laid out, which is a different
            // question with a different answer under a headless renderer.
            //
            // Empty until Body has been read, which is correct rather than
            // merely convenient — a bubble nobody has drawn cannot be holding
            // a selection.
            private readonly List<SelectableTextBlock> _blocks = new();

            public IReadOnlyList<SelectableTextBlock> Blocks => _blocks;

            // A paragraph, heading, list item, quote or code block that a
            // person can drag across and copy out.
            //
            // Focusable is off, which is the whole trick and worth stating
            // plainly: SelectableTextBlock ships Focusable=true, and taking it
            // at face value would break the rule ChatPanel.axaml sets out —
            // that the composer is the only focusable thing in the window, so
            // nothing can pull focus out of it mid-reply. Selection does not
            // need focus: Avalonia drives it entirely from the pointer handlers
            // and never calls Focus() itself. What focus *is* needed for is the
            // copy keystroke, which is why the panel claims that gesture on the
            // tunnel route instead — see OnCopyKeyDown.
            private SelectableTextBlock Selectable(
                string? text = null, FontFamily? font = null, double? size = null, IBrush? ink = null)
            {
                var block = new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Focusable = false,
                    Cursor = TextCursor,
                    SelectionBrush = SelectionFill
                };

                if (text is not null) block.Text = text;
                if (font is not null) block.FontFamily = font;
                if (size is not null) block.FontSize = size.Value;
                if (ink is not null) block.Foreground = ink;

                _blocks.Add(block);

                // Handed to the panel to finish: it owns the rules that span
                // more than one bubble — that only one may hold a selection,
                // and that they share a single context menu — and neither is a
                // TurnView's to enforce from inside one row.
                _adopt(block);

                return block;
            }

            private Control BuildBlock(ChatMarkdown.MdBlock block)
            {
                switch (block.Kind)
                {
                    case ChatMarkdown.MdKind.Code:
                        // Wrapped rather than scrolled: the bubble is 244pt and
                        // a horizontal scrollbar per code block in a column of
                        // them is worse than a wrapped line.
                        return new Border
                        {
                            Background = CodeBackground,
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 4),
                            Margin = new Thickness(0, 1),
                            Child = Selectable(block.Text, Mono, Size - Points(1), CodeInk)
                        };

                    case ChatMarkdown.MdKind.Heading:
                    {
                        var heading = Line(block.Text);
                        heading.FontWeight = FontWeight.SemiBold;

                        // Only two sizes. Six levels of heading inside a bubble
                        // this size would be a distinction nobody could see.
                        heading.FontSize = block.Depth <= 2 ? Size + Points(1) : Size;
                        heading.Margin = new Thickness(0, 2, 0, 0);
                        return heading;
                    }

                    case ChatMarkdown.MdKind.Quote:
                    {
                        var quote = Line(block.Text);
                        quote.Opacity = 0.75;
                        return new Border
                        {
                            BorderBrush = QuoteEdge,
                            BorderThickness = new Thickness(2, 0, 0, 0),
                            Padding = new Thickness(6, 0, 0, 0),
                            Child = quote
                        };
                    }

                    case ChatMarkdown.MdKind.Bullet:
                    case ChatMarkdown.MdKind.Ordered:
                    {
                        // Two columns so the text hangs under itself rather than
                        // wrapping back beneath the bullet.
                        var row = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                            Margin = new Thickness(Points(block.Depth * 10), 0, 0, 0)
                        };

                        var marker = Line(block.Marker);
                        marker.Margin = new Thickness(0, 0, 5, 0);
                        marker.Opacity = 0.7;

                        var text = Line(block.Text);
                        Grid.SetColumn(text, 1);

                        row.Children.Add(marker);
                        row.Children.Add(text);
                        return row;
                    }

                    default:
                        return Line(block.Text);
                }
            }

            // One line of inline Markdown as a TextBlock of styled runs.
            private SelectableTextBlock Line(string text)
            {
                var block = Selectable();
                block.FontSize = Size;
                block.FontStyle = Style;
                block.Foreground = Ink;

                var spans = ChatMarkdown.Inline(text);

                // No markup at all is the common case, and setting Text avoids
                // building an Inlines collection for every plain line.
                if (spans.Count == 1 && spans[0].Style == ChatMarkdown.MdStyle.Normal)
                {
                    block.Text = spans[0].Text;
                    return block;
                }

                foreach (var span in spans)
                {
                    var run = new Run(span.Text);

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

                        // Avalonia's Run has no background, so inline code is
                        // told apart by face and colour rather than by a chip.
                        case ChatMarkdown.MdStyle.Code:
                            run.FontFamily = Mono;
                            run.FontSize = Size - Points(0.5);
                            run.Foreground = CodeInk;
                            break;

                        case ChatMarkdown.MdStyle.Link:
                            run.Foreground = LinkInk;
                            run.TextDecorations = TextDecorations.Underline;
                            break;
                    }

                    block.Inlines?.Add(run);
                }

                return block;
            }

            // An I-beam over text a person can drag across, because nothing else
            // in this window can be dragged across and the cursor is the only
            // thing that says so before they try.
            private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

            // Light enough to read the ink through on a coloured bubble, which
            // a solid system highlight is not — bubbles here are tinted per
            // agent, so the one selection colour has to sit on any of them.
            private static readonly IBrush SelectionFill =
                new SolidColorBrush(Color.Parse("#59FFFFFF"));

            private static readonly FontFamily Mono = new("Menlo,SF Mono,Consolas,monospace");
            private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#33000000"));
            private static readonly IBrush CodeInk = new SolidColorBrush(Color.Parse("#FFD9A0"));
            private static readonly IBrush LinkInk = new SolidColorBrush(Color.Parse("#9FD0FF"));
            private static readonly IBrush QuoteEdge = new SolidColorBrush(Color.Parse("#4DFFFFFF"));
            public bool HasImage => _image is not null;

            // CB-93: why a picture that should have shown didn't, drawn in
            // the slot it would have occupied. See ChatTurn.ImageNote's own
            // header for why this is a line rather than a tooltip on the
            // 📎 marker or a System-turn note appended to the end of the
            // transcript.
            public string? ImageNote => _turn.ImageNote;

            public bool HasImageNote => !string.IsNullOrEmpty(_turn.ImageNote);

            public string? ImageNoteDetail => _turn.ImageNoteDetail;

            private Bitmap? _image;
            private byte[]? _bytes;

            // Excluded from coverage: the catch around it cannot be reached with a
            // picture, and the picture is the only thing this is ever handed.
            // Avalonia's Bitmap.DecodeToWidth does not throw on rubbish — five
            // random bytes and a truncated PNG both come back as an ordinary
            // 456x456 bitmap, which is the finding recorded in
            // ChatPanelMarkdownTests. So the fallback this catch promises never
            // happens, and a test cannot make it happen either.
            [ExcludeFromCodeCoverage]
            private static Bitmap DecodeAtDrawWidth(byte[] bytes)
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, 456);
            }

            // Full size, in the OS's own viewer — see OpenClawMedia for why this
            // isn't a window of ours.
            // Excluded from coverage: ends in OpenClawMedia.Open, which writes the
            // picture to a temporary file and hands it to the OS — Preview.app on
            // macOS. A test that reached it would open a window on the machine
            // running the suite. The guard in front of it, a turn with no bytes to
            // open, is the half worth covering and is covered.
            [ExcludeFromCodeCoverage]
            public void OpenFullSize()
            {
                if (_bytes is null) return;

                OpenInViewer(_bytes, _turn.ImageAlt);
            }

            // OpenClawMedia.Open is itself excluded from coverage — it writes a
            // real file and hands it to the OS's own viewer (/usr/bin/open on
            // macOS). Wrapped here so that exclusion covers only this one call
            // rather than the guard above it; a headless test proves the guard
            // without ever actually launching a viewer, and this one line is
            // left uncovered rather than exercised for real.
            [ExcludeFromCodeCoverage]
            private static void OpenInViewer(byte[] bytes, string alt) => OpenClawMedia.Open(bytes, alt);

            public Bitmap? Image
            {
                get => _image;
                private set
                {
                    _image = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Image)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasImage)));
                }
            }

            // Fetched when the row is created rather than when it scrolls into
            // view. A transcript holds thirty turns at most and only a few carry
            // pictures, so the simpler thing is also cheap enough — and the
            // bytes are cached by url, so reopening the panel costs nothing.
            public async void LoadImage()
            {
                if (string.IsNullOrEmpty(_turn.ImageUrl)) return;

                // CB-272: a clip never goes down the picture path below, which
                // would decode its bytes as a bitmap. It asks the gateway
                // about the clip instead and draws a tile from the answer.
                if (_turn.IsVideo)
                {
                    await LoadVideoAsync();
                    return;
                }

                // CB-275: an audio note and a file take the same detour for the
                // same reason — their bytes are not pictures. (A picture
                // attachment is the url-form image path below.)
                if (_turn.IsAudio)
                {
                    await LoadAudioAsync();
                    return;
                }

                if (_turn.IsDocument)
                {
                    await LoadDocumentChipAsync();
                    return;
                }

                // Fetched verbatim. Since CB-109 the url is a fully-formed
                // request — path, session and agent — built where the turn was
                // built, by whoever knew which session it belonged to. This
                // row reconstructs nothing and has no business knowing what is
                // in it.
                var url = _turn.ImageUrl!;
                var bytes = await OpenClawSessions.FetchMediaAsync(url, CancellationToken.None);
                if (bytes is { Length: > 0 })
                {
                    await DecodeAndShowAsync(bytes);
                    return;
                }

                // CB-93: this is the dominant failure site — every reopen,
                // reconnect and scroll reads history back through here, where
                // the live path above only fires once per streamed reply. A
                // path outside the gateway's media allowlist used to leave
                // this exact row with nothing in it and no explanation.
                if (!OpenClawMediaRefusal.ShouldAskWhy(bytes, url)) return;

                // The path is read, never recovered. CB-93 had to unescape it
                // back out of the url because a ChatTurn carried nothing else;
                // it is now set alongside ImageUrl by both producers
                // (TurnsFromHistory's arms, TryResolveLocalMedia), so there is
                // nothing left to reverse.
                //
                // A null here means an assistant-media url arrived on a turn
                // whose path was not set with it, which no producer does. The
                // note is skipped rather than captioned with a guess — the
                // whole point of CB-93 is that a wrong reason is worse than
                // none — and ATurnWithNoSourcePathIsNeverAskedWhy pins it.
                var path = _turn.ImageSourcePath;
                if (path is null) return;

                // CB-116: a low-confidence candidate — ordinary prose that
                // merely ends in something filename-shaped, with nothing
                // (no explicit "MEDIA:" line, no automation delivery) to say
                // this turn was a real picture — stays silent on a failure
                // rather than asking the gateway why and showing a
                // confident-sounding note for a picture that was never real.
                // "I deleted photo.png" is exactly this case. See
                // ChatTurn.Confidence and MediaConfidence, next to ChatRole
                // in RemoteChat.cs, for the full reasoning.
                if (_turn.Confidence != MediaConfidence.High) return;

                // Asked against the very url that just failed, plus the flag.
                // An explanation asked with a different identity than the
                // fetch does not fail — it *lies*, which is worse than the
                // silence CB-93 set out to remove. Before CB-109 the meta call
                // sent no session at all, which was harmless only because the
                // fetch didn't either.
                var json = await OpenClawSessions.FetchLocalMediaMetaAsync(url, CancellationToken.None);
                _turn.ImageNoteDetail = OpenClawMediaRefusal.Detail(json, path);
                _turn.ImageNote = _turn.Attachment is { Failed: true } failed
                    ? OpenClawMediaRefusal.ExplainDelivery(failed, json)
                    : OpenClawMediaRefusal.Explain(json);
            }

            private ChatAttachment? _video;
            private bool _videoRequested;
            private bool _videoOpening;

            public bool HasVideo => _video is not null;

            public double VideoWidth => OpenClawVideo.TileWidth;

            public double VideoHeight => OpenClawVideo.TileHeight(_video?.Width, _video?.Height);

            public string? VideoDuration => OpenClawVideo.FormatDuration(_video?.DurationMs);

            public bool HasVideoDuration => VideoDuration is not null;

            // The tile is drawn from the gateway's `&meta=1` answer alone —
            // the clip's bytes are not downloaded to draw it, which for a
            // video can be megabytes per clip per scroll. Asked once per row:
            // the ImageUrl change handler can fire again for the same turn.
            //
            // A clip the gateway will not serve follows the picture's CB-93
            // and CB-116 rules exactly: a High-confidence turn gets a note
            // saying why, in the slot the tile would have taken, and a Low one
            // (prose that merely ends in "something.mp4") stays silent and
            // keeps its text, since nothing says a clip was ever meant.
            private async Task LoadVideoAsync()
            {
                if (_videoRequested) return;
                _videoRequested = true;

                // CB-274: a clip the gateway delivered as a block already
                // carries its own meta (duration, size, shape), and its url is
                // the outgoing route, which has no `&meta=1` — asking would
                // build `…/full&meta=1`, get a 404 and caption a working clip
                // with "couldn't ask the gateway why".
                if (_turn.Attachment is { Kind: AttachmentKind.Video, Fetchable: true } block)
                {
                    ShowVideo(block);
                    return;
                }

                var json = await OpenClawVideo.FetchMeta(_turn.ImageUrl!, CancellationToken.None);
                var meta = OpenClawVideo.ParseMeta(json);

                if (meta is { Available: true })
                {
                    ShowVideo(OpenClawVideo.FromMeta(meta.Value, _turn.ImageAlt));
                    return;
                }

                if (_turn.Confidence != MediaConfidence.High) return;

                // Same refusal to caption with a guess as LoadImage's: no path,
                // no note. Every producer that sets a route sets the path with
                // it, so this cannot be null here.
                var path = _turn.ImageSourcePath!;

                // CB-277: only the clip's tooltip names the session, and only
                // when it is not the conversation's own — a clip is the one
                // kind asked for under another agent's key, and a refusal under
                // the asker's own key is the ordinary one and says nothing new.
                _turn.ImageNoteDetail = OpenClawMediaRefusal.Detail(json, path, _turn.ImageUrl, _askingKey);

                // CB-274: a clip the gateway failed to deliver says so first,
                // and then why the fetch of the recovered path also failed.
                _turn.ImageNote = _turn.Attachment is { Failed: true } failed
                    ? OpenClawMediaRefusal.ExplainDelivery(failed, json)
                    : OpenClawMediaRefusal.Explain(json, video: true);
            }

            private void ShowVideo(ChatAttachment attachment)
            {
                _video = attachment;
                foreach (var name in new[]
                {
                    nameof(HasVideo), nameof(VideoHeight),
                    nameof(VideoDuration), nameof(HasVideoDuration),
                    nameof(Pad)
                })
                {
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                }
            }

            // The click: fetch the clip now, then hand it to the system
            // player. Guarded against a second click while the first is still
            // downloading — a clip is large enough that two fetches would be
            // noticed, and two players opened for one click would be worse.
            // A clip that will not come down says so in the same note slot,
            // rather than the click doing nothing at all.
            public async void OpenVideo()
            {
                if (_videoOpening || string.IsNullOrEmpty(_turn.ImageUrl)) return;
                _videoOpening = true;

                try
                {
                    if (OpenClawVideo.TooLargeToOpen(_video))
                    {
                        VideoNote(OpenClawVideo.TooLargeNote(_video!.Value.SizeBytes!.Value));
                        return;
                    }

                    var bytes = await OpenClawVideo.FetchBytes(_turn.ImageUrl!, CancellationToken.None);
                    if (bytes is not { Length: > 0 })
                    {
                        VideoNote(OpenClawVideo.NotSentNote);
                        return;
                    }

                    if (OpenClawVideo.IsTruncated(bytes.Length))
                    {
                        VideoNote(OpenClawVideo.TruncatedNote);
                        return;
                    }

                    // A delivered block's label is untrusted text, so its name
                    // takes its extension from the mime type. A recovered
                    // path's name was already filtered to a clip extension.
                    var name = _turn.Attachment is { Kind: AttachmentKind.Video, Fetchable: true } delivered
                        ? OpenClawAttachment.VideoOpenName(delivered)
                        : OpenClawVideo.OpenName(_turn.ImageAlt, _turn.ImageSourcePath);

                    OpenClawVideo.Open(bytes, name);
                }
                finally
                {
                    _videoOpening = false;
                }
            }

            // ---- CB-275: audio tile and file chip --------------------------

            private ChatAttachment? _audio;
            private ChatAttachment? _document;
            private bool _audioRequested;
            private bool _documentRequested;
            private bool _attachmentOpening;

            public bool HasAudio => _audio is not null;
            public double AudioWidth => OpenClawVideo.TileWidth;
            public string AudioLabel => LabelOrWord(_audio, "audio");
            public string? AudioDuration => _audio?.Duration;
            public bool HasAudioDuration => AudioDuration is not null;

            public bool HasDocument => _document is not null;
            public string DocumentLabel => LabelOrWord(_document, "file");
            public string? DocumentSize => OpenClawAttachment.FormatSize(_document?.SizeBytes);
            public bool HasDocumentSize => DocumentSize is not null;
            public string? DocumentTip => _document?.MimeType;

            // A block that named nothing shows a generic word, never a made-up
            // filename that something might go looking for.
            private static string LabelOrWord(ChatAttachment? a, string word) =>
                a is null ? "" : string.IsNullOrWhiteSpace(a.Value.Label) ? word : a.Value.Label;

            private void Raise(params string[] names)
            {
                foreach (var name in names)
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }

            // Mirrors LoadVideoAsync, without the video's shape: a block the
            // gateway delivered carries its own meta and is never asked about;
            // a recovered path (a streamed `MEDIA:/…/note.m4a`, or a failed
            // block whose path was recovered) asks the assistant-media route.
            // A failed block still draws its tile when the route will serve it,
            // and a refused one draws the note instead, as a clip does — except
            // that a failed audio note keeps its tile, so there is something
            // to click for the honest "the gateway didn't send it".
            private async Task LoadAudioAsync()
            {
                if (_audioRequested) return;
                _audioRequested = true;

                if (_turn.Attachment is { Kind: AttachmentKind.Audio, Fetchable: true } block)
                {
                    ShowAudio(block);
                    return;
                }

                var json = await OpenClawVideo.FetchMeta(_turn.ImageUrl!, CancellationToken.None);
                var meta = OpenClawVideo.ParseMeta(json);
                var failed = _turn.Attachment is { Failed: true, Kind: AttachmentKind.Audio } f ? f : (ChatAttachment?)null;

                if (meta is { Available: true })
                {
                    ShowAudio(failed ?? OpenClawVideo.FromMeta(meta.Value, AudioNameOf()) with { Kind = AttachmentKind.Audio });
                    return;
                }

                if (failed is { } shown) ShowAudio(shown);

                if (_turn.Confidence != MediaConfidence.High) return;

                _turn.ImageNoteDetail = OpenClawMediaRefusal.Detail(json, _turn.ImageSourcePath!);
                _turn.ImageNote = failed is { } why
                    ? OpenClawMediaRefusal.ExplainDelivery(why, json)
                    : OpenClawMediaRefusal.Explain(json, noun: "Audio");
            }

            private string AudioNameOf() =>
                !string.IsNullOrEmpty(_turn.ImageAlt) ? _turn.ImageAlt : System.IO.Path.GetFileName(_turn.ImageSourcePath ?? "");

            private void ShowAudio(ChatAttachment attachment)
            {
                _audio = attachment;
                Raise(nameof(HasAudio), nameof(AudioLabel), nameof(AudioDuration),
                      nameof(HasAudioDuration), nameof(Pad));
            }

            // A file chip is drawn from the block alone — nothing is asked and
            // nothing is downloaded to draw it. A *failed* one is drawn too,
            // then the gateway is asked about the recovered path once; the
            // note appears only if that is refused as well.
            private async Task LoadDocumentChipAsync()
            {
                if (_documentRequested) return;
                _documentRequested = true;

                var attachment = _turn.Attachment!.Value;
                ShowDocument(attachment);

                if (!attachment.Failed || _turn.Confidence != MediaConfidence.High) return;

                var json = await OpenClawVideo.FetchMeta(_turn.ImageUrl!, CancellationToken.None);
                if (OpenClawVideo.ParseMeta(json) is { Available: true }) return;

                _turn.ImageNoteDetail = OpenClawMediaRefusal.Detail(json, _turn.ImageSourcePath!);
                _turn.ImageNote = OpenClawMediaRefusal.ExplainDelivery(attachment, json);
            }

            private void ShowDocument(ChatAttachment attachment)
            {
                _document = attachment;
                Raise(nameof(HasDocument), nameof(DocumentLabel), nameof(DocumentSize),
                      nameof(HasDocumentSize), nameof(DocumentTip), nameof(Pad));
            }

            public async void OpenAudio() => await OpenFetched(_audio, OpenClawAttachment.AudioOpenName);

            public async void OpenDocument() => await OpenFetched(_document, OpenClawAttachment.OpenName);

            // The click, for either kind: the same three guards the clip has —
            // the size the block (or meta) declared, the empty body, the body
            // the socket cut at its ceiling — then the system's own app.
            private async Task OpenFetched(ChatAttachment? attachment, Func<ChatAttachment, string> nameFor)
            {
                if (_attachmentOpening || attachment is not { } a || string.IsNullOrEmpty(_turn.ImageUrl)) return;
                _attachmentOpening = true;

                try
                {
                    if (OpenClawAttachment.TooLargeToOpen(a))
                    {
                        VideoNote(OpenClawAttachment.TooLargeNote(a.Kind, a.SizeBytes!.Value));
                        return;
                    }

                    var bytes = await OpenClawVideo.FetchBytes(_turn.ImageUrl!, CancellationToken.None);
                    if (bytes is not { Length: > 0 })
                    {
                        VideoNote(OpenClawAttachment.NotSentNote(a.Kind));
                        return;
                    }

                    if (OpenClawVideo.IsTruncated(bytes.Length))
                    {
                        VideoNote(OpenClawAttachment.TruncatedNote(a.Kind));
                        return;
                    }

                    OpenClawVideo.Open(bytes, nameFor(a));
                }
                finally
                {
                    _attachmentOpening = false;
                }
            }

            // Detail before note: only the note's setter raises (see
            // ChatTurn.ImageNoteDetail's header).
            private void VideoNote(string text)
            {
                _turn.ImageNoteDetail = _turn.ImageSourcePath;
                _turn.ImageNote = text;
            }

            public string ImageAlt => _turn.ImageAlt;

            // The bytes are already in hand — decoded from a local CLI's own
            // transcript (ChatTranscript's image handling), or read straight
            // back off a picture the panel itself just wrote to disk — so
            // there is nothing to fetch, only to decode.
            public async void LoadImageBytes(byte[] bytes) => await DecodeAndShowAsync(bytes);

            private async Task DecodeAndShowAsync(byte[] bytes)
            {
                // Kept as they arrived, not as they were decoded: opening the
                // picture full size should hand over the original rather than
                // the 456px copy the bubble draws.
                _bytes = bytes;

                try
                {
                    // Decoded on a worker: this awaits a network fetch that
                    // usually starts on the UI thread, so the continuation lands
                    // back there, and decoding an 840x1024 PNG on the thread
                    // that draws is a visible hitch per picture.
                    //
                    // Decoded to the width it is drawn at, twice over for
                    // Retina: keeping them at full size to show them at 228
                    // would be most of a megabyte of pixels each, held for as
                    // long as the panel is open.
                    var bitmap = await Task.Run(() => DecodeAtDrawWidth(bytes));

                    Dispatcher.UIThread.Post(() => Image = bitmap);
                }
                catch
                {
                    // Not an image, or not one we can decode. The message keeps
                    // whatever text it had.
                }
            }

            // The name was filled in after this row was built. Everything
            // drawn from it has to be asked again — the chip is bound to five
            // separate properties and a stale one leaves half a chip.
            public void SpeakerChanged()
            {
                foreach (var name in new[]
                {
                    nameof(HasSpeaker), nameof(SpeakerName), nameof(ShowSpeakerName),
                    nameof(SpeakerInitials), nameof(SpeakerAvatar),
                    nameof(HasSpeakerAvatar), nameof(HasSpeakerInitials),
                    nameof(SpeakerChip), nameof(SpeakerChipInk)
                })
                {
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                }
            }

            private bool IsSystem => _turn.Role == ChatRole.System;

            // A named speaker is never *you*, whatever role the transport gave
            // it. Another agent's message in a channel arrives with role "user"
            // — it is user-role input as far as this agent is concerned — and
            // taking that at face value drew it right-aligned in your own blue,
            // so a room full of agents looked like you talking to yourself.
            private bool IsUser => _turn.Role == ChatRole.User && !HasSpeaker;

            public bool HasSpeaker => !string.IsNullOrEmpty(SpeakerName);

            // Falls back to the session's one agent, but only on the agent's
            // own turns, and never in a room. Your messages are yours
            // whoever else is in the room, and a system note is about the
            // conversation rather than in it — stamping either with the
            // agent's name would say it spoke them.
            //
            // CB-36: the room exclusion is ChatSpeaker.CanFallBackToSoleSpeaker,
            // pure and tested there rather than inlined here — see its own
            // comment for why an unattributed room turn must not borrow
            // _soleSpeaker.Name (the panel's title, i.e. the channel) the way
            // a one-to-one session's genuinely can.
            public string SpeakerName =>
                !string.IsNullOrEmpty(_turn.Speaker) ? _turn.Speaker!
                : _turn.Role == ChatRole.Assistant
                    && ChatSpeaker.CanFallBackToSoleSpeaker(_soleSpeaker?.IsRoom ?? false)
                    ? _soleSpeaker?.Name ?? ""
                : "";

            // The name in words, beside the chip, only when the transcript
            // named the speaker itself. In a room that is worth the line: eight
            // agents talk and the names are how you follow who. In a one-to-one
            // it is the panel's own title repeated down the whole transcript,
            // which says nothing the header has not already said — so the chip
            // goes on alone, the way a messaging app shows a face and not a
            // name against every message from one person.
            public bool ShowSpeakerName => !string.IsNullOrEmpty(_turn.Speaker);

            // The speaker's own picture, when the gateway has one for them.
            //
            // The first frame only, even for an animated avatar. The header
            // animates its portrait with a timer; a room is a scrolling list of
            // dozens of turns, and one timer per row to animate a 16-pixel
            // circle is a lot of machinery for something too small to read a
            // motion in. The portrait is the place you look, and it still moves.
            public Bitmap? SpeakerAvatar =>
                OpenClawSessions.AvatarForAgentName(SpeakerName)?.Frames.FirstOrDefault();

            public bool HasSpeakerAvatar => SpeakerAvatar is not null;

            // Initials are the fallback, not the design: an agent with a face
            // shows the face, and the letters are for the ones without one and
            // for a name this cannot resolve to a single agent.
            public bool HasSpeakerInitials => !HasSpeakerAvatar;

            // The speaker's initials, for the chip beside their name.
            //
            // A name alone in a colour was enough while a room had two or three
            // agents in it. With eight it is a column of similar words in
            // similar hues, and the eye has to read each one — a shape it can
            // recognise without reading is what a room view is for. Same
            // letters the agent's own orb shows, so the chip and the orb are
            // recognisably the same agent.
            public string SpeakerInitials => OrbGlyph.Initials(SpeakerName);

            // Filled in the speaker's own colour, with the panel's own
            // background punched through it for the letters. Ink on a tinted
            // chip was the alternative and reads as a third bubble; a solid
            // dot reads as a person.
            public IBrush SpeakerChip =>
                SpeakerColor is { } c ? new SolidColorBrush(c) : SystemInk;

            public IBrush SpeakerChipInk =>
                SpeakerColor is not null ? ChipInk : SystemInk;

            // Near-black rather than the window's background brush: the chip is
            // a solid colour whatever is behind it, so the letters only have to
            // read against the chip.
            private static readonly IBrush ChipInk =
                new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));

            // The agent's own colour, the one their orb's ring is drawn in.
            private Color? SpeakerColor
            {
                get
                {
                    if (!string.IsNullOrEmpty(_turn.SpeakerColor)
                        && Color.TryParse(_turn.SpeakerColor, out var named))
                    {
                        return named;
                    }

                    // Only what the session said. Your own bubbles keep their
                    // blue, and a system note keeps none — the fallback is the
                    // agent's colour, and neither of those is the agent.
                    return IsSystem || IsUser ? null : _defaultBubble;
                }
            }

            public IBrush SpeakerInk =>
                SpeakerColor is { } c ? new SolidColorBrush(c) : SystemInk;

            public HorizontalAlignment Side => IsSystem
                ? HorizontalAlignment.Center
                : IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

            // Blue for you, grey for the agent — the arrangement every messaging
            // app has trained everyone to read without being told. The blue is
            // the same #4A90D9 the speak button already uses for "live", so the
            // app keeps one accent rather than acquiring a second.
            // A speaker's own colour, at low alpha. Full strength would be a
            // wall of saturated colour in a busy room — the name above it is
            // drawn in the same hue at full strength, which is enough to tie the
            // two together and to the orb.
            public IBrush Bubble => IsSystem
                ? Transparent
                : SpeakerColor is { } c
                    ? new SolidColorBrush(Color.FromArgb(0x3D, c.R, c.G, c.B))
                    : IsUser ? UserBubble : AgentBubble;

            // A tool line whose result reported an error is the same row in the
            // red the panel already uses for trouble; nothing else about it moves.
            private bool IsToolError => IsSystem && _turn.IsError;

            public IBrush Ink => IsToolError ? ErrorInk : IsSystem ? SystemInk : BubbleInk;

            public string? ErrorTip => IsToolError ? "The tool reported an error" : null;

            private static readonly IBrush ErrorInk = new SolidColorBrush(Color.Parse("#E0D93B3B"));

            // The corner nearest the speaker is squared off. It is the one
            // detail that makes a column of bubbles read as two people talking
            // rather than as a list.
            public CornerRadius Corners => IsSystem
                ? new CornerRadius(0)
                : IsUser ? new CornerRadius(11, 11, 3, 11) : new CornerRadius(11, 11, 11, 3);

            public Thickness Pad => IsSystem
                ? new Thickness(0, 1)
                : (HasImage || HasVideo || HasAudio || HasDocument) && !HasText ? new Thickness(5) : new Thickness(9, 6);

            // Bubbles sit closer to their own side's previous bubble than to the
            // other side's, which is what gives a conversation its rhythm.
            public Thickness Gap => IsSystem
                ? new Thickness(0, 2, 0, 2)
                : IsUser ? new Thickness(40, 2, 0, 3) : new Thickness(0, 2, 40, 3);

            // Set from outside — see ChatPanel's Scroll.SizeChanged handler and
            // the _turns.CollectionChanged hook that seeds it on every new
            // turn — rather than read some ambient static, so a TurnView stays
            // a plain value holder that answers what it's told.
            private double _availableWidth = 306;

            public double AvailableWidth
            {
                get => _availableWidth;
                set
                {
                    if (_availableWidth.Equals(value)) return;

                    _availableWidth = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MaxBubbleWidth)));
                }
            }

            // A fraction of what's actually available rather than a fixed
            // 244px, now that the panel is user-resizable: a message keeps a
            // comfortable line length instead of either hugging the old cap
            // forever on a wide window or never using the room a narrow one
            // freed up. System lines run almost the full width because they
            // read as a note about the conversation, not one side of it.
            public double MaxBubbleWidth => Math.Max(140, AvailableWidth * (IsSystem ? 0.95 : 0.8));

            // Every size in a bubble, multiplied. Four different numbers on
            // purpose — a system note is quieter than a message, a name is
            // quieter than what it said, a timestamp quieter still — and the
            // multiplier keeps that hierarchy instead of flattening it, which
            // is what a single "chat font size" setting would have done.
            private double Scale => _scale.Value;

            public double Size => (IsSystem ? 10 : 11.5) * Scale;

            // A measurement written in shipped points, in the size it should be
            // at the current scale.
            //
            // The differences inside a bubble are what make it readable — a
            // heading a point above its prose, code a point below it, a nested
            // list indented from its parent. Left as raw constants those gaps
            // stay the same absolute size while everything around them grows,
            // so the hierarchy quietly flattens exactly when someone has said
            // they are having trouble reading it. Caught by a test asserting
            // that every part of a turn grows by the same factor.
            private double Points(double shipped) => shipped * Scale;

            public double SpeakerNameSize => 9.5 * Scale;

            public double TimeSize => 8.5 * Scale;

            // The chip grows with the name beside it. Left at 16pt it would sit
            // next to 20pt text looking like a bullet point, and the initials
            // inside it would spill out of their own circle.
            public double ChipSize => 16 * Scale;

            public CornerRadius ChipCorners => new(8 * Scale);

            public double ChipTextSize => 8 * Scale;

            // Called when the scale changes under a row that already exists.
            // The body is rendered Markdown with the sizes baked into each
            // TextBlock, so it is thrown away and rebuilt rather than restyled
            // — the same thing a streaming turn's text change already does.
            public void Rescaled()
            {
                _body = null;

                foreach (var name in new[]
                         {
                             nameof(Body), nameof(Size), nameof(SpeakerNameSize), nameof(TimeSize),
                             nameof(ChipSize), nameof(ChipCorners), nameof(ChipTextSize)
                         })
                {
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                }
            }

            public FontStyle Style => IsSystem ? FontStyle.Italic : FontStyle.Normal;

            public bool ShowTime => !IsSystem;

            // Time alone for today, date and time for anything older — a
            // conversation that has been running since yesterday should say so,
            // and one from this afternoon shouldn't waste the width.
            public string TimeText
            {
                get
                {
                    var at = _turn.At.ToLocalTime();
                    return at.Date == DateTimeOffset.Now.Date
                        ? at.ToString("HH:mm")
                        : at.ToString("d MMM HH:mm");
                }
            }

            private static readonly IBrush UserBubble = new SolidColorBrush(Color.Parse("#E04A90D9"));
            private static readonly IBrush AgentBubble = new SolidColorBrush(Color.Parse("#26FFFFFF"));
            private static readonly IBrush Transparent = new SolidColorBrush(Colors.Transparent);
            private static readonly IBrush BubbleInk = new SolidColorBrush(Color.Parse("#F2FFFFFF"));
            private static readonly IBrush SystemInk = new SolidColorBrush(Color.Parse("#8CFFFFFF"));

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
