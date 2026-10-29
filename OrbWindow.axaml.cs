using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Orbweaver
{
    public partial class OrbWindow : Window
    {
        // The three state colours live in OrbColors now — they're settable, and
        // the tray icon reads the same three, so a static field here would have
        // been a second copy for both reasons.
        //
        // A session's /color goes on the orb's border and letter, leaving the
        // fill to mean what it always has.
        //
        // These are Claude Code's own accent colors, which it renders as
        // xterm-256 indices (index = 16 + 36r + 6g + b over the levels
        // 0/95/135/175/215/255). Three are confirmed from what Claude Code
        // actually emitted in a terminal — green is index 35, and the two
        // auto-assigned accents seen in other sessions were 37 and 175. The
        // rest are the same-band cube colors for their hue, i.e. educated
        // guesses; correct one by reading the escape sequence Claude Code
        // writes for that color (`tmux capture-pane -p -e`, look for
        // `38;5;<n>`) if one ever looks off.
        private static readonly Dictionary<string, Color> AgentColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = Color.Parse("#D75F5F"),     // 167
            ["orange"] = Color.Parse("#D7875F"),  // 173
            ["yellow"] = Color.Parse("#D7AF5F"),  // 179
            ["green"] = Color.Parse("#00AF5F"),   // 35  — confirmed
            ["teal"] = Color.Parse("#00AFAF"),    // 37  — confirmed (auto-assigned)
            ["cyan"] = Color.Parse("#00AFAF"),    // 37
            ["blue"] = Color.Parse("#5F87D7"),    // 68
            ["purple"] = Color.Parse("#875FD7"),  // 98
            ["violet"] = Color.Parse("#875FD7"),  // 98
            ["magenta"] = Color.Parse("#D787AF"), // 175 — confirmed (auto-assigned)
            ["pink"] = Color.Parse("#D787AF"),    // 175
            ["gray"] = Color.Parse("#808080"),    // 244
            ["grey"] = Color.Parse("#808080"),    // 244
            ["white"] = Color.Parse("#FFFFFF")
        };

        // What an orb looks like with no /color set: the original faint hairline
        // and near-white letter. PlainLink is the same idea for the team arrow,
        // but brighter — the hairline works because it sits on the orb's own
        // fill, and an arrow has nothing behind it but the desktop.
        private static readonly Color PlainStroke = Color.Parse("#22FFFFFF");
        private static readonly Color PlainGlyph = Color.Parse("#DDFFFFFF");
        private static readonly Color PlainLink = Color.Parse("#FFCCCCCC");

        public string SessionId { get; }

        private string _lastState = "";
        private string _lastColor = "";
        private Color? _accentColor;
        private string _lastGlyphName = "";
        private string? _lastTipTitle;
        private string? _lastTipPath;

        // Colour for the team arrow leaving this orb, when it has one. Follows
        // /color so several members pointing at one lead stay apart; sessions
        // without a colour share the neutral. See TeamLinks.
        public Color LinkColor { get; private set; } = PlainLink;

        // Seeded from the settings-backed colour at field-init time, the same way
        // SessionManager seeds OrbsVisible from OrbweaverSettings.ShowOrbs.
        private readonly SolidColorBrush _orbBrush = new(OrbColors.Idle);

        // The two halves of this orb's identity, for the chat panel's header.
        // A local session has no portrait and no emoji to draw there, and these
        // are what it has instead — read from the orb rather than re-derived, so
        // the header cannot disagree with the thing that was clicked.
        public string GlyphText => Glyph.Text ?? "";
        public Color OrbColor => _orbBrush.Color;

        // This session's own colour — /color for a Claude Code session, the
        // derived one for a gateway agent — or null where it has none. Distinct
        // from OrbColor, which is the *state* and changes as the session works.
        public Color? AccentColor => _accentColor;

        // The status this orb last drew, for the chat panel's header line.
        //
        // Read off the orb rather than looked up in SessionManager for the same
        // reason KindLabel, PresenceLabel and IsHeartbeat are: the panel and the
        // badge on the thing that was clicked must not be able to disagree, and
        // a second lookup is a second chance for them to. It is the same object
        // either way — SessionManager pushes each scan's status here through
        // UpdateFrom — but this one is reachable from a headless test that has
        // no SessionManager running at all, and the lookup is not.
        //
        // Null before the first status write, which is an ordinary state and
        // not an error: an orb can be clicked before its hook has ever fired.
        // UpdateFrom calls ChatPanel.RefreshIdentityFor, so the panel re-reads
        // this and fills the line in when the write lands.
        public SessionStatus? LastStatus => _lastStatus;

        private readonly RadialGradientBrush _glowBrush = new()
        {
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientStops = GlowStops(OrbColors.Idle)
        };

        private readonly ColorTransition _colorTransition;
        private readonly ScaleTransform _orbScale = new();

        // CB-268 M4: the drag squash. A matrix over the whole visible disc, the
        // identity whenever the orb is not being dragged or motion is reduced.
        private readonly MatrixTransform _squashTransform = new();
        private readonly DragSquash _squash = new();
        private DispatcherTimer? _squashTimer;

        // The heart badge's own beat, separate from the orb's breath so the two
        // can run at different rates without fighting over one transform.
        private readonly ScaleTransform _heartScale = new();

        // The halo's own transform, used by nothing but Acknowledge below. Its own
        // rather than shared with _orbScale because the two say different things:
        // one is the session's breath, the other is this app answering a click.
        private readonly ScaleTransform _glowScale = new();

        // Flat red rather than a fourth entry in OrbColors: this isn't a
        // session state Claude Code reports, it's purely local UI feedback
        // for "the mic is listening", so it has no reason to be user
        // configurable the way idle/generating/waiting are.
        private static readonly Color RecordingColor = Color.Parse("#D93B3B");

        private bool _recording;
        private VoiceRecorder? _recorder;
        private DispatcherTimer? _recordingCap;

        // Created lazily on first hover (see EnsureFlyoutShown), not here —
        // most orbs are never hovered in a given run, and none of them should
        // pay for a second window until one actually is.
        private OrbFlyout? _flyout;

        // Test-only window into the lazily-created flyout: a test that drives
        // EnsureFlyoutShown directly (see that method's own comment) has no other
        // way to reach the window it just built to click one of its own buttons.
        internal OrbFlyout? Flyout => _flyout;

        // Bridges hover between two separate OS windows (the orb and its
        // flyout): a bare PointerExited on either one would hide the flyout
        // the instant the cursor crosses from one window into the other,
        // before it ever reaches the second window. Scheduling the hide and
        // cancelling it if either window reports the pointer back within the
        // grace period turns that into a single smooth handoff instead.
        private DispatcherTimer? _hideFlyoutTimer;

        // True while this orb's chat panel is open. The arc and the panel want
        // the same piece of screen — ArcRadius is 56, directly below the orb —
        // and the arc's mic and speak duplicate what the panel already offers,
        // so one hides for the other rather than both being there.
        private bool _chatOpen;

        // The flyout used to open the instant the pointer touched an orb,
        // which made orbs hostile to each other: the arc one orb throws out
        // covers its neighbours, so a cursor travelling toward a second orb
        // summoned a menu that then sat in the way of the click it was on its
        // way to make. Requiring the pointer to *rest* on the orb separates
        // "I want this orb's menu" from "I am passing over this orb", and
        // costs a deliberate hover nothing it would notice.
        private DispatcherTimer? _showFlyoutTimer;
        private static readonly TimeSpan FlyoutHoverDelay = TimeSpan.FromMilliseconds(450);

        public OrbWindow(string sessionId)
        {
            SessionId = sessionId;
            InitializeComponent();

            _colorTransition = new ColorTransition
            {
                Property = SolidColorBrush.ColorProperty,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new QuadraticEaseOut()
            };
            _orbBrush.Transitions = new Transitions { _colorTransition };

            Orb.Fill = _orbBrush;

            Glow.Fill = _glowBrush;
            Orb.RenderTransform = _orbScale;

            // CB-268 M4: the drag squash rides on the Disc cell (see the
            // comment in OrbWindow.axaml), so it never competes with the
            // breath on Orb and AvatarImage.
            Disc.RenderTransform = _squashTransform;

            // One transform for both, so an agent's picture breathes with the
            // ring drawn around it rather than holding still inside it.
            AvatarImage.RenderTransform = _orbScale;

            // Centred, so the acknowledgment halo expands evenly out of the orb
            // rather than growing towards one corner.
            Glow.RenderTransform = _glowScale;
            Glow.RenderTransformOrigin = RelativePoint.Center;

            // Centred, so the beat grows the heart in place rather than pushing
            // it towards the orb's rim.
            HeartGlyph.Data = StreamGeometry.Parse(SymbolMarks.Heart);
            HeartGlyph.RenderTransform = _heartScale;
            HeartGlyph.RenderTransformOrigin = RelativePoint.Center;

            Root.PointerEntered += (_, _) =>
            {
                CancelFlyoutHide();
                ScheduleFlyoutShow();
            };
            Root.PointerExited += (_, _) =>
            {
                CancelFlyoutShow();
                ScheduleFlyoutHide();
            };

            ConfigureThoughtBubblePlacement(Root);

            // Unlike WPF, Loaded fires *after* the first UpdateFrom here, so
            // honor any state that already arrived instead of stomping it.
            Loaded += (_, _) =>
            {
                ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);

                // ...and then take the breath straight back off a parked orb.
                //
                // Every arm of ApplyState calls StartPulse, unconditionally, and
                // it has no idea about presence — so an orb whose *first* status
                // was already parked was dimmed by ApplyPresence and then set
                // breathing again by this handler a moment later. That is the
                // shape the whole ticket started from ("fifteen breathing orbs on
                // a machine with nothing running on it"), surviving in the one
                // window where the order is guaranteed to produce it: the scan
                // creates the orb, calls UpdateFrom, and shows it, so a session
                // that is parked when its orb first appears took this path every
                // single time.
                //
                // Re-asserted here rather than by teaching ApplyState about
                // presence, and the same way StopRecording already hands the orb
                // back: ApplyState answers "what is this session doing", which is
                // a different question from "is anyone on the other end of it",
                // and the two channels are kept separate everywhere else in this
                // file.
                if (_presence != OrbPresence.Present) ApplyPresence(_presence, force: true);
            };

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();

                // Otherwise the first click on an orb is spent activating the
                // app and never reaches it — see AcceptFirstClick.
                this.AcceptFirstClick();
            };

            // A closed orb must leave the shared ticker or it keeps being ticked.
            Closed += (_, _) =>
            {
                Pulsing.Remove(this);
                StopAvatarAnimation();

                // CloseFor, not HideFor: the session behind this orb is gone,
                // and a pinned panel is the one thing HideFor deliberately
                // will not touch. Pinning is "keep this chat open", not "keep
                // this chat open after the conversation ends" — a panel left
                // behind here would be a transcript nothing can add to and no
                // orb can reopen.
                ChatPanel.CloseFor(SessionId);
            };

            // A session going away mid-dictation (the window closing) must
            // not leave a capture thread or a native mic handle running.
            Closed += (_, _) => CancelRecording();

            // Same reason for the other half of the voice feature: speech
            // outlives the window that started it unless it's cancelled.
            Closed += (_, _) => TextToSpeech.Cancel();

            // The flyout is a second, independent top-level window — it
            // outlives this one unless told otherwise. Stopping the hide
            // timer first, not just closing the flyout, matters because a
            // tick already queued on the dispatcher would otherwise run
            // after this and touch a window that no longer exists.
            Closed += (_, _) =>
            {
                _hideFlyoutTimer?.Stop();
                _flyout?.Close();
            };
        }

        public void UpdateFrom(SessionStatus status)
        {
            _lastStatus = status;
            RefreshSoundKey(status);

            var folder = string.IsNullOrEmpty(status.Cwd)
                ? ""
                : System.IO.Path.GetFileName(status.Cwd.TrimEnd('\\', '/'));

            // The chat's own name is the better label — it says what the
            // session is *doing*, and two sessions in one repo no longer look
            // identical. Falls back to the folder until Claude Code names it.
            var label = string.IsNullOrEmpty(status.Title) ? folder : status.Title;

            // An agent's own name beats all of it. Every member of a team
            // inherits the team session's title, so a team of four drew the
            // same letter four times and said nothing about which agent was
            // which — while the terminal had been calling them MenuUX,
            // Narrative and HitReactSpec the whole time. The title still gets
            // said, in the tooltip, because "which team" is worth knowing too.
            //
            // Between those two sits the persona the session's own CLAUDE.md
            // names. It outranks the title because it is the more deliberate of
            // the two — a title is whatever Claude Code decided this
            // conversation was about, where a name written into a repository's
            // CLAUDE.md is somebody saying what this agent is called — and it
            // loses to an agent name because that one is more specific still:
            // every member of a team shares the repo and so would share the
            // persona, which is the exact collision the Agent branch exists to
            // break. The rule itself is LocalPersona.OrbLabel, pure and tested,
            // for the same reason OrbGlyph is: it was checkable only by looking
            // at the screen otherwise.
            var personaName = status.IsLocalCli
                ? LocalPersonas.For(SessionId)?.Name
                : status.Source == SessionSource.RemoteControl ? PeerPersonas.For(SessionId)?.Name : null;
            var name = LocalPersona.OrbLabel(status.Agent, personaName, status.Title, folder);

            // Whoever the orb is named for, followed by what the session is —
            // which is the half a persona replaces on the orb and must not also
            // replace here. "Leota · claude-buddy" is a tooltip worth opening;
            // "Leota", on an orb already reading Le, is not.
            var namedFor = string.IsNullOrEmpty(status.Agent) ? personaName : status.Agent;

            var described = string.IsNullOrEmpty(namedFor) || string.IsNullOrEmpty(label)
                ? name
                : $"{namedFor} · {label}";

            // The presence word goes in the tooltip as well as on the badge,
            // because a mark says *that* there is something and only a word says
            // what. "needs input" is the daemon's own phrase for it, so the
            // tooltip and `claude agents` agree rather than inventing a synonym.
            var presenceWord = PresenceMarkFor(status.Presence)?.Label;

            var tipTitle = string.IsNullOrEmpty(described) ? SessionId : described;
            if (presenceWord is not null)
            {
                tipTitle = tipTitle + " · " + presenceWord;
            }

            // The second line is normally where this session is, which for a
            // cloud session is nowhere on this disk — so the roster's own words
            // take the slot instead. Both halves can be absent and the line is
            // then simply not drawn: an orb saying "unknown" is worse than an
            // orb saying nothing, and a placeholder would be on screen far more
            // often than a real detail.
            var tipPath = status.Source == SessionSource.ClaudeCloud
                ? CloudTipDetail(status.StatusDetail, status.RecentAction)
                : string.IsNullOrEmpty(status.Cwd) ? null : status.Cwd;

            // UpdateFrom runs on every session poll, but SetTip always builds
            // a fresh Border. Doing that while the pointer is resting on the
            // orb and the tooltip is already open made Avalonia close and
            // reopen the popup on every poll tick — a flicker, not a redraw,
            // for as long as the mouse stayed still. Content is small and
            // cheap to compare, so skip the rebuild when nothing changed.
            if (tipTitle != _lastTipTitle || tipPath != _lastTipPath)
            {
                ToolTip.SetTip(Root, ThoughtBubble(tipTitle, tipPath));
                _lastTipTitle = tipTitle;
                _lastTipPath = tipPath;
            }

            _lastGlyphName = name;
            ApplyAvatar(status);
            if (!_hasAvatar) Glyph.Text = _agentEmoji ?? GlyphFor(name);

            // An open chat panel wears this orb's letters and colours. It used
            // to copy them once, when it opened, so a title that arrived after
            // the panel did — or a /rename, or a /color — left the header
            // showing the old ones. Cheap to call: the panel ignores it unless
            // it is showing this orb, and does nothing unless something moved.
            ChatPanel.RefreshIdentityFor(this);
            ApplyAccent(status.Color);
            // Rooms wear the badge too, now that their glyph is the channel's
            // initials rather than a hash. It was suppressed when the two would
            // have said the same thing; with the glyph saying *which* channel,
            // the badge saying *that* it is one is the other half rather than a
            // repeat — and without it a room orb is indistinguishable from an
            // ordinary one.
            ApplyKind(status.Kind);
            ApplyCli(status.Source, status.RemoteCli);
            ApplyHeartbeat(status.Heartbeat);

            // A room orb is named for its channel, like every other orb is named
            // for its session: "#arch" draws "Ar". The hash it used to draw
            // instead said only "this is a channel", which is true of the badge
            // on every member orb too and so distinguished one room from
            // another not at all.
            // ...unless it has a picture, which it does as soon as anyone in
            // the channel has one: the composite is the better answer to both
            // "which channel" and "who is in it", and letters drawn over it
            // would be unreadable anyway.
            if (status.IsRoom && !_hasAvatar)
            {
                Glyph.IsVisible = true;
            }
            SetTeamRole(!string.IsNullOrEmpty(status.Lead));

            SessionInfoItem.Header = string.IsNullOrEmpty(described) ? SessionId : described;
            SessionPathItem.Header = status.Cwd;
            SessionPathItem.IsVisible = !string.IsNullOrEmpty(status.Title)
                                        && !string.IsNullOrEmpty(status.Cwd);

            // Which of the two lifecycle actions this session can be offered.
            // Asked of SessionPresence rather than decided here, so the menu and
            // the manager's own guards cannot disagree about what is possible —
            // an item that is offered and then silently does nothing is worse
            // than one that is not there.
            AgentsViewItem.IsVisible = ClickRouting.OffersTheAgentsView(status);
            DismissItem.IsVisible = SessionPresence.CanDismiss(status);
            EndSessionItem.IsVisible = SessionPresence.CanEndSession(status);

            // CB-170. Asked only of an OpenClaw orb — the context is read off
            // the live gateway connection, and no other orb has one.
            var openClaw = status.Source == SessionSource.OpenClaw
                ? OpenClawCapabilities(SessionId)
                : OpenClawActionContext.None;
            InterruptRunItem.IsVisible = SessionPresence.CanInterruptOpenClaw(status, openClaw);
            EndConversationItem.IsVisible = SessionPresence.CanEndOpenClawConversation(status, openClaw);

            // Left alone while a row is armed, in flight or showing what the
            // gateway said. This runs every couple of seconds, and resetting the
            // wording here would erase "Couldn't end: …" before anyone had read
            // it; the menu closing is what puts the plain wording back.
            if (!_openClawRowsHeld) RestoreOpenClawRows();

            // CB-225. Asked of CloudOrbActions rather than decided here, for
            // the reason the lifecycle rows above give, and held exactly as
            // the OpenClaw rows are: an armed row or an answer survives the
            // two-second refresh, and the menu closing puts the words back.
            var cloud = CloudOrbActions.Offer(status.Source, SessionId, OrbweaverSettings.ClaudeCloudEnabled);
            ArchiveCloudItem.IsVisible = cloud.Archive;
            DeleteCloudItem.IsVisible = cloud.Delete;
            if (!_cloudRowsHeld) RestoreCloudRows();

            // CB-168: a local-CLI orb pre-fills the dialog with its own cwd
            // and CLI; an OpenClaw orb pre-fills the dialog's agent picker
            // with its own agent (NewChatPrefillFor, via
            // OpenClawSessions.AgentIdOf). A remote-control or Claude-Cloud
            // orb offers neither — there's no local CLI to relaunch and no
            // OpenClaw agent to point at.
            NewChatHereItem.IsVisible = status.IsLocalCli || status.Source == SessionSource.OpenClaw;

            // Back to the plain wording on every pass, so a refusal explained
            // once does not outlive the thing it was about. A husk whose job has
            // since finished has no daemon under it any more, and the row it
            // left behind would otherwise go on refusing an action that is now
            // ordinary — a stale sentence being the one failure mode a menu
            // written at open-time can still have. SessionMenu_Opening puts the
            // real answer back a moment before anyone can read it.
            //
            // Except while the menu is open (CB-228): the answer on screen then
            // is the one the user is reading, and RefreshEndSessionGuardAsync
            // owns it until SessionMenu_Closed lets go.
            if (!_endGuardHeld) ApplyEndSessionGuard(SessionDependents.Nothing);
            var resetIdleExplanation = status.Source switch
            {
                SessionSource.OpenClaw => (
                    "OpenClaw controls this session's state",
                    $"This session is managed by OpenClaw, so {Brand.DisplayName} cannot reset its state."),
                SessionSource.RemoteControl => (
                    "This session's state is controlled on its other machine",
                    "This session is managed on its other machine, so it must be reset there."),
                SessionSource.ClaudeCloud => (
                    "Anthropic's cloud controls this session's state",
                    "This session runs in Anthropic's cloud, so its state is theirs to change "
                    + "and not this machine's."),
                _ => (
                    "Reset this session to idle",
                    "Changes this orb's displayed state only; the next hook event may update it again.")
            };
            ResetIdleItem.IsEnabled = status.IsLocalCli;
            ResetIdleItem.Header = resetIdleExplanation.Item1;
            ToolTip.SetTip(ResetIdleItem, resetIdleExplanation.Item2);

            if (status.State != _lastState)
            {
                _lastState = status.State;
                if (IsLoaded && !_recording)
                {
                    ApplyState(status.State);
                }
                // else if !IsLoaded: Loaded handler applies _lastState once the
                // window is up. Else (_recording): the mic's red pulse owns
                // the orb's colour/motion right now — StopRecording restores
                // whatever _lastState ends up being once dictation finishes,
                // so a state change mid-recording isn't lost, just deferred.
            }

            // Last, and after the state block above deliberately: parking is
            // about stillness, and the state block is the thing that starts
            // motion. Applied in the other order, an orb that parked and
            // changed state in one update would be left breathing.
            ApplyPresence(status.Presence);

            // After the state block too, and for a milder version of the same
            // reason: the ring is drawn over the orb's own glow, and applying it
            // before a colour fade starts would leave it underneath a brush that
            // is still animating.
            ApplyContextRing(status.ContextPercent);
        }

        // --- presence ---------------------------------------------------------
        // A third axis, beside identity and state. The orb's colour says which
        // session this is, its fill says what that session is doing, and its
        // opacity and stillness say whether anything is on the other end of it
        // at all — a background job parked between turns, or a team member whose
        // lead has gone. See SessionPresence for what counts as either.
        //
        // Dimmed and kept, rather than hidden. A parked job is real, resumable
        // and worth clicking, and the user's own "Keep orbs for" setting is what
        // decides how long a quiet session stays on screen; hiding one would
        // trade "why are fifteen orbs breathing" for "where did my job go",
        // which is the worse of the two questions because nothing on screen
        // would answer it.

        // Dim enough to read as background at a glance across a screen, light
        // enough that the letter and the ring are still legible — the orb has to
        // stay identifiable, because the whole point is that the user can find
        // the parked job again.
        private const double ParkedOpacity = 0.45;

        private OrbPresence _presence = OrbPresence.Present;

        // The two things a dimmed orb can be saying, and nothing for the two that
        // have nothing to add.
        //
        // "?" for a job the daemon is holding for you: several of the ones this
        // was written for are literally questions waiting on an answer, and a
        // question mark is the one glyph nobody has to be taught. "✓" for a job
        // that is over — the opposite instruction, and worth being unmistakable
        // from across a screen, because the difference between "this wants you"
        // and "this wants nothing ever again" is the whole of why they are two
        // states rather than one dim one.
        //
        // Neither is a *kind*: the gear says what the session is and does not
        // change while it runs. These are presence, they change under it, and
        // they live in their own corner for exactly that reason.
        private static (string Glyph, string Label)? PresenceMarkFor(OrbPresence presence) =>
            presence switch
            {
                OrbPresence.NeedsInput => ("?", "needs input"),
                OrbPresence.Finished => ("\u2713", "finished"),
                _ => null
            };

        // The hover line for a cloud session: what it says it is doing, and the
        // last thing it was seen to do underneath.
        //
        // Pure and separate because it is the one part of this that is a
        // decision rather than a paint: which of four combinations of two
        // nullable strings produces a line, and in what order. Null in, null
        // out — the tooltip then shows the name alone, which is what every orb
        // without a directory already does.
        //
        // Joined with a middle dot rather than stacked into a third TextBlock.
        // The bubble is two lines by construction everywhere else in this app,
        // and a cloud orb growing a taller tooltip than its neighbours would be
        // a difference the user has to account for to read either.
        internal static string? CloudTipDetail(string? detail, string? recent)
        {
            var left = string.IsNullOrWhiteSpace(detail) ? null : detail!.Trim();
            var right = string.IsNullOrWhiteSpace(recent) ? null : recent!.Trim();

            if (left is null) return right;
            if (right is null) return left;
            return left + " \u00B7 " + right;
        }

        // --- the context ring -------------------------------------------------

        // How full this session's context window is, drawn as an arc around the
        // orb — for the sessions whose roster says, which today is the cloud
        // ones and nothing else.
        //
        // The arithmetic and the palette are the account orbs', not a second
        // set: UsageRingGeometry decides the sweep and the colour band, and the
        // three hexes come from AccountOrbWindow so a ring at 90% is the same
        // red wherever it is drawn. Two scales that agree by coincidence stop
        // agreeing the first time one of them is tuned.
        //
        // Null percent hides the whole layer, track included. Nobody reporting
        // a number is not a session at zero, and an empty track drawn around an
        // orb claims a measurement that was never taken — the same distinction
        // AccountOrbWindow.ApplyExtra draws, arrived at independently and worth
        // saying twice.
        private static readonly Point ContextRingCentre = new(28, 28);
        private const double ContextRingRadius = 22;

        internal void ApplyContextRing(int? percent)
        {
            if (percent is not { } value)
            {
                ContextRingLayer.IsVisible = false;
                ContextArc.Data = null;
                ContextArc.Stroke = null;
                ContextRingColour = null;
                return;
            }

            ContextRingLayer.IsVisible = true;

            var colour = UsageRingGeometry.ColourFor(
                value, AccountOrbWindow.CalmHex, AccountOrbWindow.WarnHex, AccountOrbWindow.DangerHex);

            ContextArc.Data = UsageRingGeometry.GeometryFor(
                ContextRingCentre, ContextRingRadius, value);
            ContextArc.Stroke = new SolidColorBrush(Color.Parse(colour));
            ContextRingColour = colour;
        }

        // What a person would have seen, without a test reading a brush back off
        // a shape. Null when no ring is drawn. Same seam, and the same reason,
        // as AccountOrbWindow.ApplyRing returning its colour.
        internal string? ContextRingColour { get; private set; }

        // What the chat panel and the tooltip say about presence, or null when
        // there is nothing to say. Read the same way KindLabel is, so the two
        // cannot disagree about one orb.
        public string? PresenceLabel => PresenceMarkFor(_lastStatus?.Presence ?? OrbPresence.Present)?.Label;

        // internal: driven directly by the UI suite, the same trade ApplyState
        // documents — and worth asserting on its own, because the arms are not
        // symmetrical.
        //
        // Deliberately not folded into ApplyState. That method is gated on the
        // *state* having changed, and presence does not change it: a parked job's
        // state is a truthful "idle" both before and after, so the gate would
        // never fire and nothing would dim. Presence needs its own gate for
        // exactly that reason.
        //
        // force is for a re-assertion the presence itself did not ask for — see
        // StopRecording, which hands the orb's motion back after dictation and
        // would otherwise leave a dimmed orb breathing. Same shape as
        // ApplyAccent's force above and there for the same kind of reason.
        internal void ApplyPresence(OrbPresence presence, bool force = false)
        {
            if (!force && _presence == presence) return;
            _presence = presence;

            var mark = PresenceMarkFor(presence);
            if (mark is null)
            {
                PresenceBadge.IsVisible = false;
            }
            else
            {
                PresenceGlyph.Text = mark.Value.Glyph;
                PresenceBadge.IsVisible = true;
            }

            if (presence != OrbPresence.Present)
            {
                Root.Opacity = ParkedOpacity;

                // Off the shared roster as well as stopped. StopPulse alone
                // leaves an orb on it when a heart is beating, which no local
                // session has — but "no local orb has a heartbeat badge" is a
                // fact about another feature, and a dimmed orb that kept being
                // ticked would breathe again the moment that changed.
                StopPulse();
                Pulsing.Remove(this);
                return;
            }

            Root.Opacity = 1.0;

            // Back to whatever it was doing. Guarded exactly as UpdateFrom's
            // state block is, and for the same two reasons: before Loaded there
            // is no point (the Loaded handler applies _lastState anyway), and
            // during dictation the mic owns the orb's colour and motion —
            // StopRecording is what restores it, and this must not race that.
            if (IsLoaded && !_recording)
            {
                ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);
            }
        }

        // How this orb is currently drawn. Read by the UI suite, and by nothing in
        // the app — the answer lives in the status. There used to be an IsParked
        // bool beside this; it went with the bool it was derived from, since
        // "dimmed" is now three different statements and a test that only asked
        // whether an orb was dim could not tell which one it was making.
        internal OrbPresence Presence => _presence;

        // /color identifies *which* session; the fill keeps saying what it's
        // doing. An unknown or missing color name leaves the orb looking the
        // way it always has, so a future addition to Claude Code's palette
        // degrades quietly instead of throwing.
        //
        // A "#RRGGBB" is accepted as well as a name, which is how a gateway
        // agent gets an accent: it has no /color to give, so one is derived
        // from its id (see AgentPalette). Taking it through the same field
        // rather than adding a second one means the ring, the glyph and the
        // team arrow all pick it up with no further wiring.
        private void ApplyAccent(string colorName, bool force = false)
        {
            // force is for a redraw the colour itself did not ask for — a
            // picture arriving or going changes how the ring is drawn while
            // leaving the colour alone, and the early return would swallow it.
            if (!force && colorName == _lastColor) return;
            _lastColor = colorName;

            Color accent = default;
            var known = !string.IsNullOrEmpty(colorName)
                        && (AgentColors.TryGetValue(colorName, out accent)
                            || (colorName[0] == '#' && Color.TryParse(colorName, out accent)));

            _accentColor = known ? accent : null;

            // The ring says *who*, including over a picture.
            //
            // It used to carry the state there instead, on the reasoning that a
            // picture takes the fill and leaves the ring as the only solid
            // colour. That was wrong in practice for a reason the reasoning
            // couldn't see: the idle colour is a user setting, and set near
            // black — which most installs are, since idle is meant to be quiet —
            // the "state ring" is a **black band** around the picture for the
            // 95% of the time an agent is idle. It reads as a rendering fault,
            // not as a status.
            //
            // Nothing is lost by giving it up. State on these orbs is carried by
            // the glow, which appears only for the states worth noticing
            // (GlowsFor) and pulses while they last — so "working" still
            // announces itself, and "idle" correctly says nothing at all.
            Orb.Stroke = new SolidColorBrush(known ? accent : _hasAvatar ? _orbBrush.Color : PlainStroke);

            // Thicker over a picture: it is a ring around a photograph rather
            // than an outline on a flat circle, and at 2px it reads as an edge.
            Orb.StrokeThickness = _hasAvatar ? 3 : known ? 2 : 1;

            Glyph.Foreground = new SolidColorBrush(known ? accent : PlainGlyph);
            LinkColor = known ? accent : PlainLink;

            if (Glow.IsVisible)
                _glowBrush.GradientStops = GlowStops(_accentColor ?? _orbBrush.Color);
        }

        // Re-runs ApplyAccent when something other than the colour has changed —
        // a picture arriving or going, which changes how thick the ring is and
        // what it falls back to. ApplyAccent returns early when the colour is
        // the same, and here it is: where it is *drawn* is what moved.
        private void RefreshAccent() => ApplyAccent(_lastColor, force: true);

        // Same 22 DIP as the CLI mark. The kind/heart/presence discs used to
        // be 16 against a 22px lobster, which is the size the user pointed at
        // and said the right-hand icons looked tiny.
        private const double BadgeSize = 22;

        private const double BadgeGlyphSize = 13;

        // The drawn heart's square, a little under the badge glyph size: the
        // outline fills its box edge to edge where a font's heart sits inside
        // an em with room around it, and 11 is what matches the macOS glyph
        // the heart was designed against, measured off the osx-arm64 capture.
        private const double HeartMarkSize = 11;

        // What the CLI mark shows, so a test can assert on the disc a person
        // would have seen without reading a brush back off the control.
        internal string? CliMarkName { get; private set; }

        internal string? CliMarkFill { get; private set; }

        internal bool CliMarkVisible => CliBadge.IsVisible;

        // Reference identity of the tooltip's current content, so a test can
        // assert the flicker fix without a real popup: UpdateFrom must reuse
        // this instance across polls whose title/path didn't change, and
        // swap it for a new one when they did.
        internal Control? CurrentThoughtBubble => ToolTip.GetTip(Root) as Control;

        private void ApplyCli(SessionSource source, string? remoteCli = null)
        {
            if (source == SessionSource.RemoteControl)
            {
                source = string.Equals(remoteCli, MirrorProtocol.CliGrok, StringComparison.OrdinalIgnoreCase)
                    ? SessionSource.Grok
                    : string.Equals(remoteCli, MirrorProtocol.CliCodex, StringComparison.OrdinalIgnoreCase)
                        ? SessionSource.Codex : SessionSource.ClaudeCode;
            }
            var mark = CliMark.For(source);
            if (mark is null)
            {
                CliBadge.IsVisible = false;
                CliMarkName = null;
                CliMarkFill = null;
                return;
            }

            CliBadge.Background = new SolidColorBrush(Color.Parse(mark.Value.FillHex));
            CliGlyph.Data = StreamGeometry.Parse(mark.Value.GlyphPath);
            CliBadge.IsVisible = true;
            CliMarkName = mark.Value.Name;
            CliMarkFill = mark.Value.FillHex;
        }

        // A scheduled job, a private message, or a room with other people in
        // it. Nothing at all for a local session or for an agent's own main
        // session: every agent has a main, so badging it would put a mark on
        // almost every orb and distinguish nothing.
        //
        // @ and # are the symbols the surfaces themselves use for these two
        // things, so they need no learning. The clock is the odd one out and
        // has to be: a cron session is the one kind with nobody on the other
        // end, which is the distinction most worth seeing from across a screen.
        internal static (string Glyph, string Label)? BadgeFor(SessionKind kind) => kind switch
        {
            SessionKind.Cron => ("\u23F1", "cron"),
            SessionKind.Direct => ("@", "direct message"),
            SessionKind.Channel => ("#", "channel"),

            // Two arrows going opposite ways: the same "this is somewhere else,
            // and it answers" that every sync icon means, which is as close to
            // learned-without-explaining as this one gets. The label says the
            // machine part, since the glyph can't.
            SessionKind.Remote => ("\u21C4", "another machine"),

            // A gear, for a job the daemon runs with nobody on the other end.
            // Machinery is the idea, which is as close as this gets to
            // learned-without-explaining, and it is the one badge that says
            // something about *this* machine rather than somewhere else.
            //
            // Worn by a working background job as well as a parked one. The
            // badge channel says what a session is, which does not change while
            // it runs; whether anything is happening in it rides the orb's
            // opacity — see ApplyPresence. Badging only the parked ones would
            // smuggle a state into the kind channel, and then two orbs of the
            // same kind would carry different marks depending on what they were
            // doing, which is what every other badge here avoids.
            SessionKind.Background => ("\u2699", "background job"),

            // A cloud, for a session running in Anthropic's cloud rather than
            // on any machine the user owns. The most literal glyph in this
            // switch and deliberately so: everything else here is a convention
            // borrowed from a chat surface, where this one is the word people
            // already use for the thing.
            //
            // It is also the only mark such an orb carries. CliMark returns
            // nothing for this source, which is right — a Claude spark says
            // "local Claude Code" from across a room, and that is the one thing
            // this session is not.
            SessionKind.Cloud => ("\u2601", "in the cloud"),
            _ => null
        };

        // The kinds whose badge is drawn rather than typed, as SVG path data
        // in a 16x16 box — see SymbolMarks for the marks themselves and for
        // the measurements behind drawing them at all.
        //
        // Every non-ASCII badge is here, and the test for whether a glyph
        // belongs here is not "is it in the font". U+2601 *is* reachable on
        // both platforms (Hiragino Sans on macOS, Segoe UI Emoji on Windows)
        // and neither face draws a cloud at 13px: the macOS one is a white
        // lump and the Windows one is a colour-emoji glyph, which arrives at
        // its own size and in its own colours. The clock, gear and arrows
        // were the same defect on Windows (CB-173) — legible, but lavender and
        // oversized against a badge whose whole design is one white mark on
        // near-black, so the same orb wore a different badge per platform.
        //
        // @ and # stay as text deliberately. They are ASCII, every font has
        // them, no emoji face is ever involved, they are the characters the
        // surfaces themselves use, and redrawing them as paths would be worse
        // at every size.
        internal static string? KindMarkFor(SessionKind kind) => kind switch
        {
            SessionKind.Cron => SymbolMarks.Stopwatch,
            SessionKind.Remote => SymbolMarks.Arrows,
            SessionKind.Background => SymbolMarks.Gear,
            SessionKind.Cloud => SymbolMarks.Cloud,
            _ => null
        };

        // What the chat panel puts in its header. Null where there is no badge,
        // so the panel shows nothing rather than the word "unknown".
        public string? KindLabel => BadgeFor(_lastStatus?.Kind ?? SessionKind.Unknown)?.Label;

        public string? KindGlyphText => BadgeFor(_lastStatus?.Kind ?? SessionKind.Unknown)?.Glyph;

        // The drawn form of the same badge, for the chat panel's kind chip.
        // Null where the badge is typed (@, #) or absent, and then the chip
        // types KindGlyphText instead — the same either-or ApplyKind makes, so
        // the orb and its panel cannot disagree about which kinds are drawn.
        public string? KindMarkData => KindMarkFor(_lastStatus?.Kind ?? SessionKind.Unknown);

        private void ApplyKind(SessionKind kind)
        {
            var badge = BadgeFor(kind);

            if (badge is null)
            {
                KindBadge.IsVisible = false;
                return;
            }

            var mark = KindMarkFor(kind);

            if (mark is null)
            {
                KindMark.IsVisible = false;
                KindGlyph.Text = badge.Value.Glyph;
                KindGlyph.IsVisible = true;
            }
            else
            {
                // Cleared rather than left behind: the two share a Panel, and a
                // stale glyph under a drawn mark would show through the gaps in
                // it on the one orb that changes kind in place.
                KindGlyph.Text = null;
                KindGlyph.IsVisible = false;
                KindMark.Data = StreamGeometry.Parse(mark);
                KindMark.IsVisible = true;
            }

            KindBadge.IsVisible = true;
        }

        // Whether the chat panel should say this conversation is heartbeat-driven.
        // Read the same way KindLabel is, so the panel asks the orb rather than
        // re-deriving it from a status it does not hold.
        public bool IsHeartbeat => _lastStatus?.Heartbeat ?? false;

        private void ApplyHeartbeat(bool heartbeat)
        {
            if (HeartBadge.IsVisible == heartbeat) return;

            HeartBadge.IsVisible = heartbeat;

            if (heartbeat)
            {
                _heartStartedAt = Environment.TickCount64;

                // Joins the pulse ticker on its own account. Every branch of
                // ApplyState currently ends in a StartPulse, so an orb is
                // normally on the roster already and this is redundant — but the
                // heart's motion is the whole signal, and having it depend on
                // that staying true of a switch statement elsewhere is the kind
                // of coupling that breaks quietly.
                if (!Pulsing.Contains(this)) Pulsing.Add(this);
                EnsureTicker();
            }
            else
            {
                _heartScale.ScaleX = _heartScale.ScaleY = 1.0;
                HeartGlyph.Opacity = 1.0;
            }
        }

        // --- agent teams ------------------------------------------------------
        // A team member is drawn smaller than the session that leads it, so a
        // team reads as one lead with its agents rather than as several equal
        // sessions that happen to be next to each other. Deliberately only the
        // *drawing* shrinks: the window stays 56x56, so the stack spacing, the
        // drag target, and every remembered position keep working unchanged,
        // and a member that later loses its team grows back with no relayout.

        private const double MemberScale = 0.72;

        // Half the orb's drawn width, in DIPs — where TeamLinks stops the arrow
        // so it doesn't run under the orb. Both the team role and the user's
        // size go into it, and neither setter's early return can skip the
        // other's contribution because both recompute it here.
        public double OrbRadius { get; private set; } = 18;

        private void UpdateOrbRadius() =>
            OrbRadius = 18 * (_isTeamMember ? MemberScale : 1.0) * OrbSize;

        // --- the user's orb size (CB-198) --------------------------------------
        // A multiplier over the whole 56-DIP orb, applied as a LayoutTransform
        // over Root (see the axaml) rather than threaded through SetTeamRole's
        // twenty `* scale` sites: those already mix one factor into the circle,
        // and a fourth number folded into the third is exactly how this orb's
        // geometry has gone wrong before. Everything inside Root keeps its
        // 56-space coordinates; only the window and the anchors outside it
        // (TeamLinks, the chat panel, SessionManager's stack) ask for the size.

        internal double OrbSize { get; private set; } = OrbSizing.Default;

        // Raised when this orb's size changed in a way the orbs around it have
        // to make room for — a changed SoundKey bringing a different override
        // (RefreshSoundKey), or the Size menu (SetSizeOverride). The owning
        // SessionManager subscribes when it creates the window and re-lays
        // every orb out. An event rather than SessionManager.Instance: the orb
        // has no business knowing which manager is current, and a test's
        // manager never is.
        internal event Action? SizeRelayoutRequested;

        // The orb's centre in this window's own DIPs — what every (28,28)
        // outside Root used to hard-code.
        internal double CentreDip => OrbSizing.CentreDip(OrbSize);

        // Resizes around the orb's visible centre, so an orb grows and shrinks
        // in place rather than out of its top-left corner.
        //
        // DesktopScaling is Avalonia's own declared factor between a window's
        // DIPs and its Position units, which is the conversion this needs. Not
        // PointToScreen, which was the first version: under the headless
        // platform it does not follow a Position set in code, so the centre it
        // reported was not the one the orb was drawn at — a test of the
        // offscreen rescue caught that on the sibling call site.
        internal bool ApplyOrbSize(double size)
        {
            size = OrbSizing.Clamp(size);
            if (size == OrbSize) return false;

            var scaling = DesktopScaling;
            var centreX = Position.X + CentreDip * scaling;
            var centreY = Position.Y + CentreDip * scaling;

            OrbSize = size;
            SizeTransform.LayoutTransform = new ScaleTransform(size, size);
            Width = Height = OrbSizing.WindowDip(size);
            UpdateOrbRadius();

            Position = new PixelPoint(
                (int)Math.Round(centreX - CentreDip * scaling),
                (int)Math.Round(centreY - CentreDip * scaling));
            return true;
        }

        // This orb's own override if it has one, the global slider if not.
        // Keyed by SoundKey — the per-agent key the override is stored under,
        // see OrbweaverSettings.OrbSizes.
        internal bool ApplyEffectiveOrbSize() =>
            ApplyOrbSize(OrbSizing.Effective(OrbweaverSettings.OrbSizeFor(SoundKey), OrbweaverSettings.OrbSize));

        private bool _isTeamMember;

        public void SetTeamRole(bool isTeamMember)
        {
            if (_isTeamMember == isTeamMember) return;
            _isTeamMember = isTeamMember;

            var scale = isTeamMember ? MemberScale : 1.0;

            Orb.Width = Orb.Height = 36 * scale;
            AvatarImage.Width = AvatarImage.Height = 36 * scale;
            AvatarImage.Clip = new EllipseGeometry(new Rect(0, 0, 36 * scale, 36 * scale));
            Glow.Width = Glow.Height = 56 * scale;

            // Kept on the orb's edge rather than in the window's corner. The
            // orb is a circle of radius 18*scale centred at (28,28), so its
            // lower-right edge is at 28 + 18*scale*sin45. Solving for the
            // margin that puts the badge's centre there is what keeps it
            // touching the rim at both sizes instead of drifting off a team
            // member's smaller circle.
            KindBadge.Width = KindBadge.Height = BadgeSize * scale;
            KindBadge.CornerRadius = new CornerRadius(BadgeSize * scale / 2);
            KindGlyph.FontSize = BadgeGlyphSize * scale;

            // The drawn mark shrinks with the disc for the same reason
            // CliGlyph does: a 13px cloud inside a 16px badge overflows it.
            KindMark.Width = KindMark.Height = BadgeGlyphSize * scale;

            var inset = 28 - (18 * scale * 0.7071) - (BadgeSize * scale / 2);
            KindBadge.Margin = new Thickness(0, 0, Math.Max(0, inset), Math.Max(0, inset));

            // The same sum mirrored into the opposite corner: the heart rides the
            // orb's upper-right rim, so it has to move with the circle for the
            // same reason the kind badge does.
            HeartBadge.Width = HeartBadge.Height = BadgeSize * scale;
            HeartBadge.CornerRadius = new CornerRadius(BadgeSize * scale / 2);
            HeartGlyph.Width = HeartGlyph.Height = HeartMarkSize * scale;
            HeartBadge.Margin = new Thickness(0, Math.Max(0, inset), Math.Max(0, inset), 0);

            // And mirrored once more into the corner this one lives in. Same sum
            // because it is the same circle: a team member's orb is smaller, and
            // a mark left at the full-size margin would float off its rim.
            PresenceBadge.Width = PresenceBadge.Height = BadgeSize * scale;
            PresenceBadge.CornerRadius = new CornerRadius(BadgeSize * scale / 2);
            PresenceGlyph.FontSize = BadgeGlyphSize * scale;
            PresenceBadge.Margin = new Thickness(Math.Max(0, inset), Math.Max(0, inset), 0, 0);

            CliBadge.Width = CliBadge.Height = CliMark.Size * scale;
            CliBadge.CornerRadius = new CornerRadius(CliMark.Size * scale / 2);
            CliGlyph.Width = CliGlyph.Height = CliMark.GlyphSize * scale;
            CliBadge.Margin = new Thickness(Math.Max(0, inset), 0, 0, Math.Max(0, inset));

            Glyph.FontSize = BaseGlyphFontSize * scale;
            UpdateOrbRadius();
        }

        // Smaller with two letters than with one, so the wider glyph still
        // fits inside the same 36px circle rather than crowding its edge.
        private static double BaseGlyphFontSize => OrbweaverSettings.TwoLetterGlyphs ? 12.0 : 16.0;

        // Settings' "Two-letter initials" toggle changes how every already-
        // open orb's glyph reads without waiting for that session's next
        // hook update — see SessionManager.ReapplyGlyphs, which calls this
        // on each one. Re-derives from _lastGlyphName rather than the full
        // SessionStatus: nothing else about the orb needs to change, just
        // the text and the font size sitting under it.
        public void ReapplyGlyph()
        {
            Glyph.Text = GlyphFor(_lastGlyphName);
            Glyph.FontSize = BaseGlyphFontSize * (_isTeamMember ? MemberScale : 1.0);
            ChatPanel.RefreshIdentityFor(this);
        }

        // An agent's own picture, drawn as the orb itself.
        //
        // This is the one place the app's usual rule bends: normally the fill is
        // the state and the letter is which session. A face says which session
        // far better than a letter can, so the state moves outward to the ring —
        // which still carries the colour, and the pulse and halo were always
        // doing most of that work anyway. Sessions with no picture keep the
        // ordinary filled orb and get their agent's emoji instead of a letter,
        // which is why both paths stay.
        private bool _hasAvatar;
        private string? _agentEmoji;

        // The state ring on an avatar orb. One brush with its own transition,
        // rather than a fresh SolidColorBrush per state change: the fill it
        // replaced faded over 300ms and a ring that snaps instead reads as a
        // different, cruder thing. Also stops allocating a brush every time a
        // session changes state, which for a busy gateway is often.
        private SolidColorBrush? _ringBrush;
        private OpenClawAvatars.Avatar? _avatar;
        private int _avatarFrame;
        private DispatcherTimer? _avatarTimer;

        private void ApplyAvatar(SessionStatus status)
        {
            var avatar = AvatarFor(status);
            if (avatar is null)
            {
                ClearAvatar();
                return;
            }

            ShowAvatar(avatar);
        }

        // Which picture, if any — the only part of drawing a portrait that
        // differs between a gateway agent and a local one. Everything after it
        // is identical, which is why it is a separate method: the two branches
        // were a copy of forty lines apart from these ten, and a copy is where
        // the ring stops matching the fill on one of them.
        private OpenClawAvatars.Avatar? AvatarFor(SessionStatus status)
        {
            if (status.Source == SessionSource.OpenClaw)
            {
                _agentEmoji = OpenClawSessions.IdentityForSession(SessionId)?.Emoji;

                // Asked of OpenClawSessions rather than assembled here, because
                // a room's picture is not an agent's picture — it is a composite
                // of everyone in the channel, and that is a question about who
                // is in the room, which this window has no business knowing. The
                // chat panel's header asks the same function, so the two cannot
                // end up wearing different faces for the same session.
                return OpenClawSessions.AvatarForSession(SessionId);
            }

            // A persona has no emoji to leave _agentEmoji holding — see
            // SessionIdentity for why the grammar deliberately has no such
            // field — so a local orb with no picture keeps falling back to its
            // letters, which is what it did before any of this.
            if (status.Source == SessionSource.RemoteControl)
            {
                var persona = PeerPersonas.For(SessionId);
                return persona?.Avatar is null
                    ? null
                    : OpenClawAvatars.For(PeerPersonas.AvatarKey(SessionId), persona.Avatar);
            }

            if (!status.IsLocalCli) return null;

            var localPersona = LocalPersonas.For(SessionId);
            return localPersona?.AvatarPath is null
                ? null
                : OpenClawAvatars.ForFile(LocalPersonas.AvatarKey(SessionId), localPersona.AvatarPath);
        }

        private void ShowAvatar(OpenClawAvatars.Avatar avatar)
        {
            if (ReferenceEquals(avatar, _avatar)) return;

            _avatar = avatar;
            _avatarFrame = 0;
            _hasAvatar = true;

            Glyph.IsVisible = false;

            // Drawn by AvatarImage beneath the ellipse, not as its fill — see the
            // axaml for why an ImageBrush goes soft under the orb-size
            // transform. The ellipse goes clear so the picture shows through,
            // and keeps drawing the ring.
            AvatarImage.Source = avatar.Frames[0];
            AvatarImage.IsVisible = true;
            Orb.Fill = Brushes.Transparent;

            _ringBrush ??= new SolidColorBrush(_orbBrush.Color)
            {
                Transitions = new Transitions
                {
                    new ColorTransition
                    {
                        Property = SolidColorBrush.ColorProperty,
                        Duration = TimeSpan.FromMilliseconds(300),
                        Easing = new QuadraticEaseOut()
                    }
                }
            };

            _ringBrush.Color = _orbBrush.Color;

            // The picture lands long after the accent did, and it is what
            // decides how the ring is drawn — so the accent is applied again
            // rather than assumed to have got there first. An agent with no
            // colour at all falls back to the state ring inside ApplyAccent,
            // which is what _ringBrush is still here for.
            Orb.Stroke = _ringBrush;
            RefreshAccent();

            StartAvatarAnimation();
        }

        private void ClearAvatar()
        {
            if (!_hasAvatar)
            {
                Glyph.IsVisible = true;
                return;
            }

            _hasAvatar = false;
            _avatar = null;
            StopAvatarAnimation();

            AvatarImage.IsVisible = false;
            AvatarImage.Source = null;
            Orb.Fill = _orbBrush;
            Orb.Stroke = new SolidColorBrush(Color.Parse("#22FFFFFF"));
            Orb.StrokeThickness = 1;
            Glyph.IsVisible = true;

            // Thinner ring, and the accent back on a flat circle.
            RefreshAccent();
        }

        // Its own timer rather than the shared pulse ticker: frame delays are
        // whatever each GIF's author chose, and are neither 60fps nor the same
        // between two agents.
        //
        // CB-218: only while the orb is on screen. "Show orbs" off hides every
        // orb window, and each animated avatar used to keep swapping frames
        // nobody could see, a frame's worth of work per window at the GIF's rate.
        // OnPropertyChanged below starts it again when the orb is shown, from
        // the frame it stopped on, so the animation picks up rather than
        // restarting.
        private void StartAvatarAnimation()
        {
            StopAvatarAnimation();

            if (!OrbAvatarAnimation.ShouldRun(_avatar?.IsAnimated == true, IsVisible)) return;

            _avatarTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_avatar!.DelaysMs[_avatarFrame])
            };

            _avatarTimer.Tick += (_, _) =>
            {
                if (_avatar is null) return;

                _avatarFrame = (_avatarFrame + 1) % _avatar.Frames.Count;
                AvatarImage.Source = _avatar.Frames[_avatarFrame];
                _avatarTimer!.Interval = TimeSpan.FromMilliseconds(_avatar.DelaysMs[_avatarFrame]);

                // The breath rides this frame rather than presenting one of its
                // own in between (OrbBreathCadence).
                if (_breathing) StepBreath(Environment.TickCount64);
            };

            _avatarTimer.Start();
        }

        private void StopAvatarAnimation()
        {
            _avatarTimer?.Stop();
            _avatarTimer = null;
        }

        // Whether the avatar is animating now, for the tests that pin when it
        // is not.
        internal bool AvatarAnimating => _avatarTimer is { IsEnabled: true };

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property != IsVisibleProperty) return;

            if (IsVisible) StartAvatarAnimation();
            else StopAvatarAnimation();
        }

        // The letters themselves live in OrbGlyph, which is pure and tested;
        // this only supplies the one thing that is not — the user's setting.
        private static string GlyphFor(string label) =>
            OrbGlyph.For(label, OrbweaverSettings.TwoLetterGlyphs);

        // The colour comes from OrbColors so this switch is about *motion* only —
        // one state-to-colour mapping in the app, not two that can drift apart.
        // internal: the state-to-motion mapping is a rule worth asserting, and
        // UpdateFrom deliberately does not apply it directly — it stores the
        // state and lets Loaded/Opened apply it, because Avalonia fires Loaded
        // *after* the first UpdateFrom. A window that is never shown therefore
        // never applies anything, so a test drives this instead of the lifecycle.
        internal void ApplyState(string state)
        {
            var color = OrbColors.For(state);

            switch (state)
            {
                case "waiting":
                    AnimateColor(color, TimeSpan.FromMilliseconds(300), state);
                    StartPulse(1.22, TimeSpan.FromMilliseconds(500), new QuadraticEaseOut());
                    break;
                case "generating":
                    AnimateColor(color, TimeSpan.FromMilliseconds(300), state);
                    StartPulse(1.14, TimeSpan.FromMilliseconds(900), new SineEaseInOut());
                    break;
                default:
                    StopPulse();
                    AnimateColor(color, TimeSpan.FromMilliseconds(400), state);
                    StartPulse(1.06, TimeSpan.FromSeconds(2.2), new SineEaseInOut(), idle: true);
                    break;
            }
        }

        // The halo is a claim on your attention, so only the two states that
        // have something to say make it. Idle is what most orbs are in most of
        // the time, and glowing about it spends the screen's whole attention
        // budget on the one state that wants none of it — the slow breath is
        // enough to say the session is still there, and the fill and hairline
        // still say where it is.
        //
        // A custom idle colour makes the point sharply: a dark one (the default
        // is already nearly black) renders as a smudge that darkens whatever
        // sits under it rather than as light.
        //
        // Asked in one place, from the state alone, because it's read both by
        // ApplyState and by ReapplyStateColors and the two must not drift —
        // the same reason the colours themselves live in OrbColors.
        private static bool GlowsFor(string state) => state is "waiting" or "generating";

        // Changing a colour in settings is not a state change, and UpdateFrom only
        // calls ApplyState when status.State actually differs — so without this an
        // orb would keep its old fill until its session next did something, which
        // for a quiet session is never.
        //
        // Two things it deliberately doesn't do. It doesn't re-run ApplyState:
        // StartPulse resets the breath's phase, so every orb on screen would jerk
        // in step with the pointer. And it barely fades — 60ms, not the 300-400ms
        // a real state change gets — because the picker raises its change event on
        // every pointer move, and a third of a second of easing leaves the orb
        // trailing the cursor, reading as lag rather than as a live preview. At
        // 60ms each frame lands most of the way there and the orb tracks the
        // spectrum. The glow already snaps, since GlowStops is assigned rather
        // than animated, so this also stops the two disagreeing mid-drag.
        private static readonly TimeSpan SettingsColorFade = TimeSpan.FromMilliseconds(60);

        public void ReapplyStateColors()
        {
            // Not up yet: the Loaded handler applies _lastState with the new
            // colours anyway, which also covers an orb created while orbs were
            // hidden.
            if (!IsLoaded) return;

            var state = string.IsNullOrEmpty(_lastState) ? "idle" : _lastState;
            AnimateColor(OrbColors.For(state), SettingsColorFade, state);
        }

        private void AnimateColor(Color to, TimeSpan duration, string state)
        {
            _colorTransition.Duration = duration;
            _orbBrush.Color = to;

            // Hidden rather than made transparent: an invisible ellipse isn't
            // rendered at all, and there's no point rebuilding four gradient
            // stops for something nobody can see.
            Glow.IsVisible = GlowsFor(state);
            if (Glow.IsVisible) _glowBrush.GradientStops = GlowStops(_accentColor ?? to);

            // With a picture in the fill, the ring is the only thing left
            // carrying the colour, so it has to follow the same changes — and
            // fade rather than snap, the way the fill did.
            if (_hasAvatar && _ringBrush is not null) _ringBrush.Color = to;
        }

        // Opaque at the centre, gone by the edge — the same falloff a blur gave,
        // without re-blurring 56x56 pixels sixty times a second.
        // The glow's gradient offsets are fractions of the *radius* (28px), and
        // the orb covers the inner 18px — so anything before offset 0.64 is hidden
        // behind the orb and contributes nothing. Hold the colour flat out to
        // there and fade over the visible ring, which is where the blur used to
        // put its bloom.
        private static GradientStops GlowStops(Color color) => new()
        {
            new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0.0),
            new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0.64),
            new GradientStop(Color.FromArgb(95, color.R, color.G, color.B), 0.82),
            new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0)
        };

        // Shared with the flyout's buttons, which are a different window but the
        // same app: a second tooltip look would be a second answer to a question
        // this already answered. The global ToolTip style is stripped to nothing
        // precisely so this control *is* the tooltip, which also means anything
        // handing ToolTip a bare string gets unstyled text floating on the
        // desktop — how the flyout's first set of tips shipped.
        // `compact` drops the tail and tightens the type: a caption for a
        // button, rather than a thought belonging to an orb.
        //
        // The tail is two dots *below* the bubble, so it reads as a thought
        // rising from whatever is underneath — right for an orb's own tooltip,
        // which sits above the orb and points back down at it. A button's label
        // sits below the button, where the same dots point at nothing and read
        // as a rendering fault. Sharing the palette and dropping the tail keeps
        // one look without claiming a button is thinking.
        // Both orb windows are tiny (56x56 here, 72x72 for an account orb) and
        // Topmost, so their thought bubble has to render outside the window's
        // own bounds — Avalonia backs that with a real, separate native popup
        // window rather than drawing inside Root. `PlacementMode.Top`'s own
        // math put that popup's bounds overlapping Root's: caught live off
        // the window server (CGWindowListCopyWindowInfo), a fresh popup
        // window id opening and closing every ~20-25ms for as long as the
        // hover lasted. The mechanism: the popup's bounds overlap Root's
        // pixel under the cursor, the OS hands "topmost under the cursor" to
        // the newly-opened popup, Avalonia reads that as the pointer leaving
        // Root and closes the tooltip, the OS hands the cursor back to Root,
        // Avalonia reopens it — a self-sustaining ~40Hz loop, which reads as
        // flicker rather than as discrete blinks. AccountOrbWindow never set
        // a Placement at all, which defaults to Pointer — the popup opens
        // wherever the cursor already is, i.e. inside its own anchor, the
        // same failure by a different route.
        //
        // Two earlier rounds (CB-104) each fixed a real bug in the tooltip's
        // *content* churning open/closed on every session poll while the
        // pointer sat still. Both fixes stay; this is a separate mechanism —
        // geometry, not content — so it needed a separate fix.
        //
        // PlacementMode.Custom sidesteps whatever Top's default math is
        // actually doing (undocumented, and this Avalonia version's source
        // wasn't available locally to read it): the callback below computes
        // the box itself, anchored to Root's own top edge and grown upward,
        // so it cannot land on top of Root regardless of platform quirks.
        // Call once, from the constructor — not every poll tick — since the
        // callback never changes and there is no reason to touch a popup's
        // placement while it may be open.
        internal static void ConfigureThoughtBubblePlacement(Control anchor)
        {
            ToolTip.SetPlacement(anchor, PlacementMode.Custom);
            ToolTip.SetCustomPopupPlacementCallback(anchor, PlaceThoughtBubbleAboveAnchor);
        }

        // Daylight above the anchor's own top edge, past whatever rounding
        // either side of the popup positioner does. Not a tuned magic
        // number: the overlap this replaces measured 78px deep into a 56px-
        // tall window, so a few px here is a floor against rounding, not an
        // offset chosen to clear one captured case.
        private const double ThoughtBubbleClearance = 6;

        private static void PlaceThoughtBubbleAboveAnchor(CustomPopupPlacement placement)
        {
            // Anchor = the target's own top-center point; Gravity = the
            // popup grows in the "up" direction from that point, so its
            // bottom-center lands on the anchor point before the offset
            // pushes it clear. Both fully determined by us, not by
            // PlacementMode.Top's own (evidently unreliable) math.
            placement.Anchor = PopupAnchor.Top;
            placement.Gravity = PopupGravity.Top;
            placement.Offset = new Point(0, -ThoughtBubbleClearance);

            // Never let a screen-edge constraint flip this to Bottom or
            // Center — flipping back onto the anchor is exactly the bug
            // being fixed here. An orb pinned at the very top of a display
            // draws its tooltip partly off-screen instead, which is a far
            // smaller problem than reopening the flicker loop.
            placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX;
        }

        internal static Control ThoughtBubble(string title, string? path, bool compact = false)
        {
            var bg = Color.Parse("#E6EAECF0");
            var fg = Color.Parse("#FF2A2A35");
            var font = new FontFamily(
                "SF Pro Rounded, .AppleSystemUIFontRounded, Segoe UI, sans-serif");

            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = compact ? 11 : 12.5,
                FontFamily = font,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(fg),
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = 17
            });

            if (path is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = path,
                    FontSize = 11.5,
                    FontFamily = font,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, fg.R, fg.G, fg.B)),
                    TextWrapping = TextWrapping.NoWrap,
                    LineHeight = 15
                });
            }

            var bubble = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(compact ? 9 : 14),
                Padding = compact ? new Thickness(9, 5) : new Thickness(14, 9),
                BoxShadow = BoxShadows.Parse("0 2 8 0 #30000000"),
                Child = content
            };

            if (compact) return bubble;

            var canvas = new Canvas
            {
                Width = 16, Height = 16,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0)
            };

            var dot1 = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromArgb(180, bg.R, bg.G, bg.B))
            };
            Canvas.SetLeft(dot1, 4);
            Canvas.SetTop(dot1, 0);

            var dot2 = new Ellipse
            {
                Width = 5, Height = 5,
                Fill = new SolidColorBrush(Color.FromArgb(120, bg.R, bg.G, bg.B))
            };
            Canvas.SetLeft(dot2, 6);
            Canvas.SetTop(dot2, 10);

            canvas.Children.Add(dot1);
            canvas.Children.Add(dot2);

            var stack = new StackPanel();
            stack.Children.Add(bubble);
            stack.Children.Add(canvas);
            return stack;
        }

        // One shared ticker drives every orb's pulse instead of an Avalonia
        // Animation per window. Avalonia animations run at the display's frame
        // rate, and each frame re-renders the whole (transparent, topmost) orb
        // window — measured at roughly 8% of a core per orb at 60Hz. The pulse is
        // a slow breath, so a much lower rate is indistinguishable and costs a
        // fraction as much. Hidden orbs are skipped entirely, which the old
        // animation never did: Hide() left it running.
        //
        // Ten, not the twenty this said for most of its life. Twenty was never
        // what anybody saw: from 29 Aug every DispatcherTimer fired late by the
        // startup offset DispatcherClock fixes, and once CB-219 stopped the
        // danger ring's animation promoting timers on time, this ticker ran at
        // about 4 Hz. Putting the clock right put it at a real 20 Hz on every
        // orb — every state breathes, idle included — and Buddy went from 7% to
        // about 20% of a core on the MacBook with the same sessions. Ten keeps
        // the breath smooth: the idle one moves under a pixel a step.
        private const double PulseFps = 10;

        private static readonly List<OrbWindow> Pulsing = new();
        private static DispatcherTimer? _ticker;

        private double _pulseTo = 1.0;
        private double _pulsePeriodMs = 2200;
        private long _pulseStartedAt;

        // Whether the *breath* is running, as distinct from whether this orb is on
        // the shared roster. They used to be the same thing and are not any more:
        // the click acknowledgment below borrows the roster for a quarter of a
        // second on orbs that are deliberately held still, and a parked orb whose
        // scale started swelling because it had been added to the ticker would be
        // saying something about its state that is not true.
        private bool _breathing;

        // The idle breath steps at half the ticker's rate, and an orb with an
        // animated avatar breathes on its own frames. OrbBreathCadence has why.
        private bool _idleBreath;
        private long _lastBreathStepAt;

        // Its own clock rather than the orb's, because the orb's period changes
        // with the session's state — and a heart that sped up when the agent
        // started working would be saying something this badge does not know.
        private const double HeartPeriodMs = OpenClawHeartbeat.PeriodMs;
        private long _heartStartedAt;

        private void StartPulse(double to, TimeSpan duration, Easing easing, bool idle = false)
        {
            // Duration is a half-cycle in the old alternating animation, so a full
            // breath is twice it. Easing is implied by the cosine below.
            _pulseTo = to;
            _pulsePeriodMs = duration.TotalMilliseconds * 2;
            _pulseStartedAt = Environment.TickCount64;
            _breathing = true;
            _idleBreath = idle;

            if (!Pulsing.Contains(this)) Pulsing.Add(this);
            EnsureTicker();
        }

        private static void EnsureTicker()
        {
            // Restart as well as create: the tick handler stops the timer once the
            // last orb stops pulsing, so a returning session has to be able to
            // wake it again.
            if (_ticker is not null)
            {
                if (!_ticker.IsEnabled) _ticker.Start();
                return;
            }

            _ticker = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / PulseFps)
            };
            _ticker.Tick += (_, _) => TickAllPulses();
            _ticker.Start();
        }

        // internal: the shared ticker's Tick handler calls this on every
        // pulsing orb once a real frame elapses, which a headless test only
        // gets by pumping the dispatcher against wall time. Driving it directly
        // is the same trade ApplyState already makes above.
        internal void TickPulse() => TickPulse(Environment.TickCount64);

        // With the clock passed in, so a test can step the idle breath's
        // cadence at exact times instead of racing the wall clock.
        internal void TickPulse(long now)
        {
            // Nothing on screen, nothing to animate — the whole point of the
            // "Show orbs" toggle was to stop this work, and it never did.
            if (!IsVisible)
            {
                _orbScale.ScaleX = _orbScale.ScaleY = 1.0;
                return;
            }

            if (_ackStartedAt != 0) TickAcknowledgement();

            // Only when the breath is actually running. An orb that is on the
            // roster solely to finish an acknowledgment must not start swelling:
            // scale is the state channel, and a parked orb that breathed for a
            // quarter of a second would be claiming to have come back to life.
            if (_breathing && OrbBreathCadence.PulseTickSetsScale(AvatarAnimating, _idleBreath, now, _lastBreathStepAt))
                StepBreath(now);

            if (HeartBadge.IsVisible) TickHeart();
        }

        private void StepBreath(long now)
        {
            var scale = OrbBreathCadence.ScaleAt(now - _pulseStartedAt, _pulsePeriodMs, _pulseTo);

            _orbScale.ScaleX = scale;
            _orbScale.ScaleY = scale;
            _lastBreathStepAt = now;
        }

        // The heart badge's beat, on the same ticker as the breath above. Two
        // thumps close together and then a rest, rather than the orb's cosine:
        // the orb is already breathing, and a second smooth swell next to it
        // reads as one thing wobbling rather than as a pulse. Lub-dub is the
        // shape everyone recognises without being told, which is the only reason
        // it is worth the extra few lines.
        //
        // Opacity moves with the scale because 15px of heart cannot get much
        // bigger before it leaves its own badge — most of the visible motion is
        // the fade, and the scale is what stops the fade reading as a blink.
        internal void TickHeart()
        {
            var beat = OpenClawHeartbeat.Beat(
                (Environment.TickCount64 - _heartStartedAt) / HeartPeriodMs);

            _heartScale.ScaleX = _heartScale.ScaleY = 1.0 + (0.28 * beat);
            HeartGlyph.Opacity = 0.5 + (0.5 * beat);
        }

        private void StopPulse()
        {
            _breathing = false;

            // Stays on the roster while the heart is beating. Every caller
            // currently follows this immediately with a StartPulse, so leaving
            // would be momentary — but a momentary stop is exactly what a
            // heartbeat badge should not do, and the alternative is that this
            // stays correct only by luck of the call order.
            if (!HeartBadge.IsVisible) Pulsing.Remove(this);

            _orbScale.ScaleX = _orbScale.ScaleY = 1.0;
        }

        // --- "handled": the click that deliberately did nothing --------------

        // A single outward halo, once, when a click resolved without creating
        // anything.
        //
        // Round eight exists because round seven worked and nobody could tell.
        // The click on an orb whose session was already on screen correctly did
        // nothing at all — verified from outside the app: dispatch fired, the
        // pane scan matched, no window was created — and the user's verdict was
        // "still not working", because a silent success is indistinguishable from
        // the broken clicks the whole ticket is about. Invisible success is
        // failure, so the orb says it handled the gesture.
        //
        // Deliberately *not* the scale channel. Scale is the breath, which is
        // this app's word for what a session is doing, and a swell on click would
        // read as the session changing state — the one thing an acknowledgment
        // must not imply, since nothing about the session changed. The glow's
        // *size* is otherwise unused (its colour carries state, its scale carries
        // nothing), so a halo that expands and fades is a free channel and reads
        // as a ripple leaving the orb rather than as the orb doing something.
        //
        // On the shared ticker rather than an Avalonia animation or a timer of its
        // own, for the reason the breath is: one ticker for every orb, and this
        // borrows it for a quarter of a second. It is also the only reason
        // _breathing exists — a parked orb is deliberately off the roster and has
        // to be able to come back onto it for an acknowledgment without its held
        // stillness being mistaken for a state change.
        private const double AckMs = 280;
        private const double AckHaloTo = 1.45;

        private long _ackStartedAt;
        private bool _ackBorrowedTheRoster;

        // internal: reached in production from GoToSession's callback, off the
        // click path, and driven directly by the UI suite for the same reason
        // ApplyState and ApplyPresence are.
        internal void Acknowledge()
        {
            // The mic owns the orb's colour and motion while it is recording, and
            // StopRecording is what hands it back. Same guard as ApplyPresence's,
            // and for the same reason: an acknowledgment that fought that would
            // leave the orb mid-halo when dictation ended.
            if (_recording) return;

            _ackStartedAt = Environment.TickCount64;

            if (!Pulsing.Contains(this))
            {
                Pulsing.Add(this);
                _ackBorrowedTheRoster = true;
            }

            EnsureTicker();
        }

        // What GoToSession hands the focuser as its acknowledgment callback.
        //
        // A named method rather than a lambda at the call site, because the
        // marshalling is the whole content of it and a lambda body there is
        // reachable only by a real click resolving through TerminalFocuser — which
        // this suite must never do, so it would be an uncovered line that looks
        // like an oversight rather than the seam it is.
        //
        // Marshalled because Focus answers from a pool thread and this touches the
        // orb's own visual tree.
        internal void AcknowledgeFromAnyThread() => Dispatcher.UIThread.Post(Acknowledge);

        // internal for the same reason TickPulse is: a headless test cannot wait
        // for real frames, and driving the tick directly is how every other
        // animation in this file is asserted.
        internal void TickAcknowledgement()
        {
            var progress = (Environment.TickCount64 - _ackStartedAt) / AckMs;

            if (progress >= 1.0)
            {
                EndAcknowledgement();
                return;
            }

            // Out and away: the halo grows the whole time and fades the whole
            // time, so the motion has one direction. A swell-and-return would be
            // the breath's shape at a different speed, which is the confusion this
            // whole channel exists to avoid.
            _glowScale.ScaleX = _glowScale.ScaleY = 1.0 + (AckHaloTo - 1.0) * progress;
            Glow.Opacity = 1.0 - progress;
        }

        private void EndAcknowledgement()
        {
            _ackStartedAt = 0;
            _glowScale.ScaleX = _glowScale.ScaleY = 1.0;
            Glow.Opacity = 1.0;

            // Off the roster again if this put it there, following StopPulse's own
            // rule about the heart: a beating badge keeps the orb on the ticker
            // whatever else stops.
            if (_ackBorrowedTheRoster && !_breathing && !HeartBadge.IsVisible)
            {
                Pulsing.Remove(this);
            }

            _ackBorrowedTheRoster = false;
        }

        // How the acknowledgment is drawn right now, for the UI suite. Nothing in
        // the app reads it.
        internal bool IsAcknowledging => _ackStartedAt != 0;

        // --- Voice dictation mic ---
        // Hover shows a small flyout window below the orb with action
        // buttons in a semicircular arc (see OrbFlyout — its own window,
        // not a control drawn inside this one's 56x56 bounds). The mic
        // button records, transcribes locally via Whisper, and types the
        // result into this session's terminal. See VoiceRecorder,
        // SpeechTranscriber and TerminalFocuser.SendText.

        // Created on first hover, not in the constructor — see the field's
        // own comment for why. A no-op when the feature is off, so nothing
        // here ever constructs a VoiceRecorder — and triggers macOS's
        // mic-permission prompt — for someone who hasn't opted in.
        // internal: only reachable in production via a real hover (see
        // ScheduleFlyoutShow/OnFlyoutShowTick), which needs a pointer genuinely
        // resting on the orb. Driven directly for the same reason ApplyState is.
        internal void EnsureFlyoutShown()
        {
            if (_flyout is null)
            {
                _flyout = new OrbFlyout();
                _flyout.MicClicked += ToggleRecording;
                _flyout.ArrangeClicked += ArrangeAllOrbs;
                _flyout.SettingsClicked += OpenSettings;
                _flyout.SpeakClicked += OnSpeakClicked;
                _flyout.ChatClicked += OpenChat;

                // The other half of the hover bridge described on
                // _hideFlyoutTimer: entering the flyout must cancel a hide
                // that Root.PointerExited already scheduled, and leaving it
                // must schedule one of its own in case the pointer doesn't
                // land back on the orb either.
                _flyout.PointerEntered += (_, _) => CancelFlyoutHide();
                _flyout.PointerExited += (_, _) => ScheduleFlyoutHide();
            }

            // Shown for both kinds now. It used to be hidden on gateway orbs
            // because dictation had nowhere to go for them; the chat panel is
            // that somewhere, and StopRecording opens it with the words in its
            // input box rather than sending them.
            bool micOn = OrbweaverSettings.VoiceInputEnabled;
            _flyout.SetMicVisible(micOn);

            // Only on local sessions. A gateway orb opens its panel when you
            // click it, so a button that did the same thing one ring further out
            // would be a second way to do the thing the orb already does.
            // Any local CLI, each behind its own setting. A gateway orb still
            // doesn't get the button: clicking it already opens its panel, so a
            // second way to do the same thing one ring further out is noise.
            _flyout.SetChatVisible(
                (_lastStatus?.IsLocalCli ?? false)
                && CliChatFormat.For(_lastStatus!.Source).ChatEnabled());

            _flyout.SetArranged(SessionManager.Instance?.IsArranged ?? false);

            // Speech is global rather than per-orb, so a flyout opening
            // while something is already being read has to show the stop
            // glyph rather than offer to start a second one. Reads the real
            // state now rather than a flag some click left behind.
            _flyout.SetSpeakState(TextToSpeech.State);

            // The arc's virtual centre (ArcOrigin) aligns with the orb's
            // centre so the semicircle sits concentric with the orb. The
            // animation starts with the flyout centred on the orb so the
            // buttons are hidden behind it and emerge downward.
            //
            // PointToScreen, not raw arithmetic: Position is physical screen
            // pixels, these are DIP measurements, and the two only line up
            // at 100% display scaling.
            var target = new Point(
                OrbCentre - _flyout.ArcOriginX,
                OrbCentre - _flyout.ArcOriginY);
            var from = new Point(
                OrbCentre - _flyout.Width / 2,
                OrbCentre - _flyout.Height / 2);

            _flyout.ShowNear(
                from: this.PointToScreen(from),
                to: this.PointToScreen(target),
                owner: this);
        }

        // Centre of the orb in its own window's DIPs — half of Root's pinned
        // 56x56 at the default size, scaled with it otherwise (CB-198).
        // Unchanged by MemberScale: a team member is drawn smaller around this
        // same point, never moved off it.
        private double OrbCentre => CentreDip;

        // --- Speak latest turn --------------------------------------------------

        // Deliberately tells the flyout nothing. It used to push the glyph itself
        // either side of the call, which was a guess that happened to be right on
        // the way in and always wrong on the way out — speech that ended by itself
        // left the stop glyph up until the flyout was reopened, and the neural
        // engine's several seconds of preparation looked identical to playing.
        // TextToSpeech.StateChanged is the single source now; see SessionManager,
        // which broadcasts it to every orb because speech is global rather than
        // per-orb.
        internal void OnSpeakClicked()
        {
            if (TextToSpeech.IsSpeaking)
            {
                TextToSpeech.Cancel();
                return;
            }

            // A gateway session has no transcript on this machine, so the text
            // comes from the conversation itself — fetched if this session has
            // never been opened, which is why this branch is async where the
            // local one isn't.
            if (_lastStatus?.Source == SessionSource.OpenClaw)
            {
                _ = SpeakRemoteAsync();
                return;
            }

            SpeakIfThereIsAnything(FindSpeakableText());
        }

        // No longer excluded from coverage, and that is the fix rather than a
        // side effect of it. What used to be here spoke `text` verbatim, having
        // never asked SpeechPlan or the scope setting anything — the "does not
        // summarise" half of CB-165's second trip through QA — and it was
        // invisible precisely because the method was excluded for containing the
        // utterance. Handing the reply to SpeechRequest instead leaves nothing
        // here that a test cannot see.
        private void SpeakIfThereIsAnything(string? text)
        {
            if (text is null) return;
            SpeechRequest.Speak(text, SessionId);
        }

        // Safe to call: SessionManager.Instance is null outside the running app, so
        // this is a no-op there — which is what an arrange should be when there
        // are no orbs to arrange.
        internal static void ArrangeAllOrbs() => SessionManager.Instance?.ArrangeOrbsInPattern();

        // Excluded from coverage: SettingsWindow.Toggle puts the app in the Dock,
        // shows a window and takes it key, then starts a status ticker. Its own
        // exclusion says the same; this is the call site, and calling it would do
        // all of that for real.
        [ExcludeFromCodeCoverage]
        private static void OpenSettings() => SettingsWindow.Toggle();

        internal async Task SpeakRemoteAsync()
        {
            var title = _lastStatus?.Title ?? "";
            var text = await OpenClawSessions.LastAssistantTextAsync(SessionId, title);

            if (string.IsNullOrWhiteSpace(text)) return;

            // Back to the UI thread before anything is decided, not merely
            // before the utterance: SpeechRequest moves TextToSpeech's state,
            // which every orb's flyout is bound to.
            Dispatcher.UIThread.Post(() => SpeechRequest.Speak(text, SessionId));
        }

        // CB-167's "vibe summary" turn-finished sound. TurnSounds.Deliver
        // calls this through a callback SessionManager hands it, once the
        // scan has already decided this orb's turn just finished and that
        // summary is what should be spoken for it — everything upstream of
        // this point (coalescing, the rate limit, the busy-speech fallback to
        // an ordinary chime) has already happened, so this has nothing left
        // to decide except *which text*, and whether there was any.
        //
        // Deliberately not OnSpeakClicked with a flag bolted on: that method
        // opens with "if already speaking, cancel" because it is a button a
        // user toggles, and a scan-driven summary is not a button press —
        // nothing here should ever stop speech that is already in progress on
        // this orb's own say-so. TurnSoundPolicy's speechBusy check is what
        // keeps this from being called at all while something else is
        // talking; this method's job starts after that question is settled.
        //
        // Async and bool-returning (QA, CB-167): a caller that gets false
        // back knows nothing was said — no transcript, no gateway history, or
        // an orb kind this can't read at all (RemoteControl, ClaudeCloud) —
        // and TurnSounds uses exactly that to fall back to an ordinary chime
        // rather than the turn finishing in total silence. The false return
        // is not an error; a summary that never had anything to summarise is
        // an ordinary outcome, the same way FindSpeakableText returning null
        // always has been.
        internal async Task<bool> SpeakTurnSummaryAsync()
        {
            if (_lastStatus?.Source == SessionSource.OpenClaw)
            {
                return await SpeakTurnSummaryRemoteAsync().ConfigureAwait(false);
            }

            // Every other orb kind that isn't a local CLI — RemoteControl,
            // ClaudeCloud — has no transcript this can read at all.
            // FindSpeakableText's own guard says the same thing for the same
            // reason; this is checked first only so the walk below is never
            // attempted for a kind that can never produce anything from it.
            if (!(_lastStatus?.IsLocalCli ?? false)) return false;

            // Off whatever thread called this: FindSpeakableText walks a
            // transcript file and, on the cwd-fallback path, a directory
            // tree — neither belongs on the Avalonia UI thread
            // ScanAndUpdateCore runs on, which is exactly the thread QA
            // found this reaching before this fix.
            var text = await Task.Run(() => FindSpeakableText()).ConfigureAwait(false);
            if (text is null) return false;

            // Posted rather than called directly, the same as the remote
            // branch below and OnSpeakClicked's own local path once did
            // before CB-165 — SpeechRequest moves TextToSpeech's state,
            // which every orb's flyout is bound to, and this continuation is
            // running on a thread pool thread by the time it gets here.
            Dispatcher.UIThread.Post(() => SpeechRequest.SpeakTurnSummary(text, SessionId));
            return true;
        }

        internal async Task<bool> SpeakTurnSummaryRemoteAsync()
        {
            var title = _lastStatus?.Title ?? "";
            var text = await OpenClawSessions.LastAssistantTextAsync(SessionId, title);

            if (string.IsNullOrWhiteSpace(text)) return false;

            Dispatcher.UIThread.Post(() => SpeechRequest.SpeakTurnSummary(text, SessionId));
            return true;
        }

        // Called by SessionManager when speech starts, changes phase or stops.
        public void SetFlyoutSpeakState(TextToSpeech.SpeakState state) =>
            _flyout?.SetSpeakState(state);

        // This session's own transcript first, then a search by directory.
        //
        // The fallback is for a session that dispatches work rather than
        // doing it: a controller has no transcript of its own, but the
        // background jobs it launched write theirs into project dirs named
        // for the same cwd, and the most recent of those is what "read the
        // last turn" means when you click its orb.
        // `home` exists so the cwd fallback below is reachable without a
        // transcript in the developer's real ~/.claude. TranscriptReader's search
        // already takes one for the same reason; this just passes it through.
        // One frame of the shared pulse, for every orb currently pulsing.
        //
        // A named method rather than the Tick lambda it used to be, because the
        // ticker is process-wide and accumulates every orb ever shown for the life
        // of the process — nothing closes a window to remove one. A test that
        // waited on the real timer passed alone and failed once the rest of the
        // suite loaded the machine, even with a ten-second budget. Driving it is
        // the fix, and it also covers the arm that matters: the ticker stops
        // itself when the last orb finishes, rather than spinning at 30fps
        // forever.
        internal static void TickAllPulses()
        {
            for (var i = Pulsing.Count - 1; i >= 0; i--) Pulsing[i].TickPulse();
            if (Pulsing.Count == 0) _ticker?.Stop();
        }

        internal string? FindSpeakableText(string? home = null)
        {
            // Not for a gateway session. It has no transcript on this machine,
            // and the cwd fallback below would match a *local* project directory
            // with the same path and speak an unrelated local session's last
            // turn as though it were the remote agent's. The lookup also walks
            // every project directory recursively, on the UI thread, before
            // getting there.
            //
            if (!(_lastStatus?.IsLocalCli ?? false)) return null;

            // Codex reads through its own entry point, which understands a
            // rollout and — the part that matters — has no cwd fallback. Sharing
            // the path below would have found no Claude Code rows in a rollout
            // and then searched ~/.claude/projects for the same directory,
            // speaking an unrelated Claude Code session's last turn out of a
            // Codex orb.
            if (_lastStatus?.Source == SessionSource.Codex)
                return TranscriptReader.LatestCodexAgentText(_lastStatus?.TranscriptPath);

            if (_lastStatus?.Source == SessionSource.Grok)
                return TranscriptReader.LatestGrokAgentText(_lastStatus?.TranscriptPath);

            var path = _lastStatus?.TranscriptPath;
            var text = TranscriptReader.LatestAssistantText(path, SessionId);
            if (text is not null) return text;

            var cwd = _lastStatus?.Cwd;
            if (string.IsNullOrEmpty(cwd)) return null;

            var fallback = TranscriptReader.LatestTranscriptForCwd(cwd, home);
            if (fallback is not null)
            {
                text = TranscriptReader.LatestAssistantText(fallback);
                if (text is not null) return text;
            }

            return null;
        }

        // Called by SessionManager when the arrangement state changes, so
        // every orb's flyout (if it exists) reflects whether clicking the
        // arrange button would arrange or restore.
        public void SetFlyoutArranged(bool arranged) => _flyout?.SetArranged(arranged);

        // Called by ChatPanel when it closes itself, so the arc becomes
        // available again without the orb having to watch the window.
        public void SetChatOpen(bool open) => _chatOpen = open;

        // The flyout's keyboard button. Same destination a gateway orb's click
        // reaches, arrived at differently because for a local session the click
        // is already spoken for.
        //
        // ...and for a cloud session too, which is the reason this deliberately
        // does *not* carry the TryOpenInBrowser guard that GoToSession and the
        // dictation path both do. A cloud orb's click is spoken for — it goes to
        // claude.ai, where the session can actually be replied to — so the
        // keyboard button is the way to the panel, which is where it can be
        // read. Guarding here as well would leave both gestures going to the
        // browser and the panel reachable by nothing, which is what the first
        // draft of CB-164's chat wiring did.
        // Excluded from coverage: needs SessionManager.Instance to hand back a
        // session, and this suite deliberately never sets it — making one current
        // starts the status-directory watcher, the two-second scan timer and a
        // tray icon, none of which a test should own. RemoteScanTests,
        // SessionScanTests and GatewayScanTests all make the same choice and say
        // so.
        //
        // What the chat panel does once opened is covered directly in the
        // ChatPanel suites, which construct one against a FakeChatSession rather
        // than going through an orb.
        [ExcludeFromCodeCoverage]
        internal void OpenChat()
        {
            var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
            if (chat is null) return;

            _chatOpen = true;
            HideFlyoutNow();
            ChatPanel.OpenFor(this, chat);
        }

        // Whether a dictation capture is in progress. The panel mirrors it on
        // its own mic button and refuses to be dismissed while it is true.
        public bool IsRecording => _recording;

        // The panel's mic drives this orb's recorder rather than constructing a
        // second one — that keeps one recorder per session, along with the red
        // pulse and the 30-second cap, all working exactly as they already do.
        // Excluded from coverage: both arms reach a live VoiceRecorder — one to
        // construct and start it (PvRecorder.Create opens the microphone), the
        // other to stop it and push captured audio through Whisper.net. There is
        // no third path, so there is nothing here a headless runner can execute.
        // The panel's own mirroring of IsRecording is covered where it lives.
        [ExcludeFromCodeCoverage]
        public void ToggleRecording()
        {
            if (_recording) StopRecording();
            else StartRecording();
        }

        // Hides the flyout unconditionally — used by SessionManager before
        // starting an arrangement animation, since a flyout anchored to a
        // moving orb would look broken.
        public void HideFlyout() => HideFlyoutNow();

        // Immediate, not scheduled — dragging moves the orb every pointer
        // move, and a flyout animating toward a stale position underneath a
        // moving orb would read as broken rather than as a hover effect.
        private void HideFlyoutNow()
        {
            CancelFlyoutShow();
            _hideFlyoutTimer?.Stop();
            _flyout?.Hide();
        }

        internal void CancelFlyoutHide() => _hideFlyoutTimer?.Stop();

        internal void CancelFlyoutShow() => _showFlyoutTimer?.Stop();

        // The delay is only for *opening* the flyout from nothing. Coming back
        // onto the orb from its own open flyout is the other half of the hover
        // bridge, not a new request, and pausing there would be a stutter in
        // the middle of an interaction the user is already having.
        internal void ScheduleFlyoutShow()
        {
            // On the method rather than on PointerEntered, the same way
            // ScheduleFlyoutHide carries its own _recording guard: the rule is
            // "the arc does not open while the chat panel has that space", and a
            // rule about the arc belongs where the arc is scheduled. Left on the
            // handler it is one caller's business, and the next caller has to
            // remember it.
            if (_chatOpen) return;

            if (_flyout?.IsVisible == true)
            {
                EnsureFlyoutShown();
                return;
            }

            _showFlyoutTimer ??= new DispatcherTimer { Interval = FlyoutHoverDelay };
            _showFlyoutTimer.Stop();

            // One handler, however many hovers — same reason as the hide timer.
            _showFlyoutTimer.Tick -= OnFlyoutShowTick;
            _showFlyoutTimer.Tick += OnFlyoutShowTick;
            _showFlyoutTimer.Start();
        }

        internal void OnFlyoutShowTick(object? sender, EventArgs e)
        {
            _showFlyoutTimer!.Stop();

            // PointerExited cancels this timer, but a drag that carries the orb
            // out from under a stationary cursor, or an orb closing mid-wait,
            // doesn't necessarily raise one — so confirm rather than assume.
            if (!Root.IsPointerOver) return;

            EnsureFlyoutShown();
        }

        // A no-op while recording: the flyout is the only way to stop, so it
        // must stay up regardless of where the pointer wanders.
        internal void ScheduleFlyoutHide()
        {
            if (_recording) return;

            _hideFlyoutTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _hideFlyoutTimer.Stop();

            // Re-subscribing on every schedule would stack a duplicate Tick
            // handler per hover; there both is and only ever needs to be one.
            _hideFlyoutTimer.Tick -= OnFlyoutHideTick;
            _hideFlyoutTimer.Tick += OnFlyoutHideTick;
            _hideFlyoutTimer.Start();
        }

        internal void OnFlyoutHideTick(object? sender, EventArgs e)
        {
            _hideFlyoutTimer!.Stop();

            // The grace period ends with the pointer having genuinely landed
            // on one of the two windows after all (a slow, deliberate move
            // across the gap) — nothing to hide in that case.
            if (Root.IsPointerOver || (_flyout?.IsPointerOverFlyout ?? false)) return;

            _flyout?.Hide();
        }

        // Excluded from coverage: constructs and starts a real VoiceRecorder,
        // which is itself excluded wholesale for the same reason (it opens an
        // actual microphone). There is no seam to test the surrounding
        // dispatch logic without either a working input device (which a CI
        // runner may or may not have, making the catch-vs-success branch
        // nondeterministic across machines — the exact kind of platform
        // dependence this ticket's rules forbid relying on) or a real
        // recording actually starting and running for up to 30 seconds on a
        // background capture thread, which is not something a headless suite
        // should leave running.
        [ExcludeFromCodeCoverage]
        private void StartRecording()
        {
            if (_recording) return;

            try
            {
                _recorder = new VoiceRecorder();

                // Fired from VoiceRecorder's own capture thread, so this has
                // to hop back to the UI thread before touching anything here
                // — StopRecording ends up updating Avalonia controls and
                // awaiting the transcription pipeline, none of which is safe
                // to do from off the dispatcher.
                _recorder.SilenceDetected += () => Dispatcher.UIThread.Post(StopRecording);

                _recorder.Start();
            }
            catch (Exception ex)
            {
                // No input device, permission denied, device busy — a
                // convenience feature failing to start is not worth a crash.
                _recorder = null;
                Console.Error.WriteLine($"{Brand.DisplayName}: couldn't start recording: {ex.Message}");
                return;
            }

            _recording = true;
            ChatPanel.SetRecording(this, true);

            // Flat red, fast — visibly distinct from the waiting/generating
            // pulses, so "listening" reads as its own thing rather than as
            // the session itself having changed state.
            AnimateColor(RecordingColor, TimeSpan.FromMilliseconds(150), _lastState);
            StartPulse(1.18, TimeSpan.FromMilliseconds(350), new SineEaseInOut());

            // A hard cap, not just a courtesy: this runs whether or not the
            // user remembers to click again, so a missed second click can't
            // leave the mic — and VoiceRecorder's own capture thread — running
            // indefinitely.
            _recordingCap = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _recordingCap.Tick += (_, _) => StopRecording();
            _recordingCap.Start();
        }

        // Excluded from coverage along with StartRecording: only reachable
        // after a real recording actually started (a live VoiceRecorder,
        // itself excluded), and its meaningful body pipes real captured audio
        // through SpeechTranscriber — a local Whisper.net model load and
        // inference, not something a headless CI runner should be doing
        // either. The early-return guard is inseparable from that same
        // feature rather than a decision worth a seam of its own.
        [ExcludeFromCodeCoverage]
        private async void StopRecording()
        {
            if (!_recording || _recorder is null) return;

            _recording = false;
            ChatPanel.SetRecording(this, false);
            _recordingCap?.Stop();
            _recordingCap = null;

            // Back to whatever the session's own state actually is —
            // StartRecording never changed _lastState, only the pulse and
            // colour drawn over it.
            ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);

            // ...and back to still, if this orb is parked. The line above puts
            // it on the pulse roster, which is right for every orb except a
            // parked one — and ApplyPresence's own gate would decline, since
            // nothing about the presence changed. Forced rather than reasoned
            // around: the mic owns the orb's motion while it is recording, and
            // this is where that ownership is handed back.
            if (_presence != OrbPresence.Present) ApplyPresence(_presence, force: true);

            // The pointer is very likely still over the mic right after a
            // click, but the recording that was forcing the flyout to stay
            // up just ended — re-derive from where the pointer actually is
            // now rather than assuming either way.
            if (Root.IsPointerOver || (_flyout?.IsPointerOverFlyout ?? false))
            {
                CancelFlyoutHide();
            }
            else
            {
                HideFlyoutNow();
            }

            var recorder = _recorder;
            _recorder = null;

            float[] pcm;
            try
            {
                pcm = recorder.Stop();
            }
            finally
            {
                recorder.Dispose();
            }

            if (pcm.Length == 0) return;

            var text = await SpeechTranscriber.TranscribeAsync(pcm);
            if (string.IsNullOrWhiteSpace(text)) return;

            // With a chat panel open for this session, the words belong in its
            // input box — unsent, for the reason TerminalFocuser.SendText gives
            // at its own definition: transcription is a typing aid and doesn't
            // get to decide you meant it. Reviewing before Enter is the whole
            // contract, and it is the same one either way.
            if (ChatPanel.IsOpenFor(SessionId))
            {
                ChatPanel.AppendToInput(this, text);
                return;
            }

            // Dictated at a cloud orb, which has neither of the two places the
            // branches below put words: no panel, because nothing here can read
            // or write that conversation, and no terminal, because it is not on
            // this machine or on any machine the user owns.
            //
            // **The transcription is lost, and there is nowhere for it to go.**
            // That is stated rather than hidden because the alternative is
            // worse: falling through reaches TerminalFocuser.SendText, which
            // would hunt for a pane and type the words into whichever session
            // happened to match — somebody else's terminal, silently. Opening
            // the browser at least lands the user where they can say it again.
            if (TryOpenInBrowser()) return;

            // Dictated at a gateway orb with no panel up: open one and put the
            // words in it. Still unsent — the panel is what makes "review before
            // Enter" possible for a session that has no terminal to review in.
            if (_lastStatus?.Source == SessionSource.OpenClaw)
            {
                var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
                if (chat is not null)
                {
                    _chatOpen = true;
                    HideFlyoutNow();
                    ChatPanel.OpenFor(this, chat);
                    ChatPanel.AppendToInput(this, text);
                }

                return;
            }

            var status = _lastStatus;
            if (status is null) return;

            await TerminalFocuser.SendText(status, text);
        }

        // Ends an in-progress recording without transcribing or sending
        // anything — only reachable from Closed, where the orb (and the
        // session it belongs to) is going away regardless.
        //
        // Excluded from coverage: its only caller is the Closed event
        // (rule of this suite: never Close() a headless orb, which corrupts a
        // process-wide font cache — see OrbWindowStateTests), and its
        // meaningful body needs the same live VoiceRecorder StartRecording
        // does.
        [ExcludeFromCodeCoverage]
        private void CancelRecording()
        {
            _recordingCap?.Stop();
            _recordingCap = null;

            if (!_recording || _recorder is null) return;

            _recording = false;
            ChatPanel.SetRecording(this, false);
            try { _recorder.Stop(); } catch { }
            _recorder.Dispose();
            _recorder = null;
        }

        // --- Click, dragging & context menu ---
        // Left-press starts as a potential click; it becomes a drag once the
        // pointer moves past a small threshold. A clean click jumps to the
        // session's terminal (macOS, best-effort — see TerminalFocuser).
        //
        // Dragging an orb pins it: it keeps that spot as sessions come and go
        // (SessionManager.ReflowPositions steps over pinned orbs) and the spot
        // is remembered across restarts, keyed by the session's directory. The
        // context menu's "Reset this orb's position" undoes both.

        // Where the user dragged this orb is remembered against this key — the
        // session's cwd, set by SessionManager. Empty for a session with no cwd
        // reported, which pins for this run only since there's nothing stable
        // to remember it against.
        public string PositionKey { get; set; } = "";

        // CB-167's key for a per-orb sound override (SessionManager.SoundKeyFor),
        // cached here the same way PositionKey is. Not PositionKey itself:
        // SoundKeyFor appends the agent name to close the one gap
        // PositionKeyFor leaves open — two team members sharing a cwd and an
        // auto-generated title, which is fine for a shared *position* but
        // wrong for a sound override, where muting one teammate would
        // silently mute the other. Read by the Sound submenu on Opening.
        //
        // QA round 2 (HIGH): originally set once, in RestoreOrbPosition, on
        // the theory that it was as stable as PositionKey. It isn't —
        // SoundKeyFor depends on Title, and an untitled session keys on its
        // own session id until Claude Code names it — so a menu selection
        // made before that point wrote an override under a key the scan
        // stopped looking up under the moment the title arrived. Now
        // recomputed on every UpdateFrom (see there for the migration this
        // requires) rather than left to settle once at creation.
        public string SoundKey { get; set; } = "";

        // True once the user has placed this orb by hand, whether in this run or
        // in an earlier one.
        public bool IsPinned { get; private set; }

        private SessionStatus? _lastStatus;
        private bool _pressed;
        private bool _dragging;
        private PixelPoint _windowStart;
        private PixelPoint _pointerStart;

        // Who moves with this orb, and what releasing it does to groups, is an
        // OrbDragSession (CB-268): DragPlan decides the followers — plain drag
        // moves the orb's whole team, Shift moves its whole group — and
        // OrbDragCommit decides whether the drop joins, leaves or changes
        // nothing. Created on press, because membership can change mid-drag and
        // an orb that joins the team while you are moving it should not jump.
        private OrbDragSession? _session;

        // How many PointToScreen units one DIP spans, measured rather than read
        // off RenderScaling: on macOS PointToScreen is already in points, so
        // RenderScaling (2 on a Retina Mac) would double the drag threshold.
        // 1 on macOS, the display scaling on Windows. See LinkWindow.Apply.
        internal static double UnitsPerDip(Visual visual) =>
            (visual.PointToScreen(new Point(64, 0)).X - visual.PointToScreen(new Point(0, 0)).X) / 64.0;

        // The entry point a group backdrop calls (GroupBackdrops.BackdropPressed):
        // dragging empty space inside a group's shape moves the whole group. The
        // pointer is captured by the backdrop window that took the press and
        // the session follows it from there, so the backdrop needs no drag
        // code of its own. Excluded from coverage for the same reason as the
        // orb handlers below: it forwards real pointer events.
        [ExcludeFromCodeCoverage]
        internal static void BeginBackdropDrag(
            string groupId, PointerPressedEventArgs e, Window source)
        {
            if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
            if (OrbDragHost.Current is not { } host || host.OrbIdsInGroup(groupId).FirstOrDefault() is not { } first)
                return;

            var session = new OrbDragSession(host, first, DragSource.Backdrop, false, UnitsPerDip(source));
            var start = source.PointToScreen(e.GetPosition(source));
            var dragging = false;
            e.Pointer.Capture(source);

            void Moved(object? _, PointerEventArgs m)
            {
                var now = source.PointToScreen(m.GetPosition(source));
                int dx = now.X - start.X, dy = now.Y - start.Y;
                if (!dragging && !DragPlan.Exceeded(dx, dy, UnitsPerDip(source))) return;
                dragging = true;
                session.Move(dx, dy);
            }

            void Released(object? _, PointerReleasedEventArgs r)
            {
                source.PointerMoved -= Moved;
                source.PointerReleased -= Released;
                r.Pointer.Capture(null);
                if (dragging) session.End();
            }

            source.PointerMoved += Moved;
            source.PointerReleased += Released;
        }

        // Excluded from coverage: a synthesized press on an orb ends in a real
        // click being resolved, and OnClicked's default action is
        // TerminalFocuser.Focus — which fires tmux, ps and osascript as real
        // processes off-thread, with no OS guard at its own entry point. On a CI
        // runner that is an unpredictable side effect rather than a test, and on
        // a developer's machine it moves their windows.
        //
        // The decisions these three share are not lost with them: who moves is
        // DragPlan, what a drop does is OrbSnap and OrbDragCommit, and the
        // moving itself is OrbDragSession, all covered without a pointer (see
        // OrbWindowDragSnapTests). What is excluded here is the plumbing from a
        // pointer to those answers, and the click resolution itself is covered
        // directly in OrbWindowClickResolutionTests via OnClicked/ActionFor
        // rather than through a pointer.
        [ExcludeFromCodeCoverage]
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _pressed = true;
                _dragging = false;

                // Recorded here because only the press carries it; the release
                // that acts on it does not.
                _clickCount = e.ClickCount;

                _windowStart = Position;
                _pointerStart = this.PointToScreen(e.GetPosition(this));

                _session = OrbDragHost.Current is { } host
                    ? new OrbDragSession(
                        host, SessionId, DragSource.Orb,
                        e.KeyModifiers.HasFlag(KeyModifiers.Shift), UnitsPerDip(this))
                    : null;

                e.Pointer.Capture(this);
            }
        }

        // Excluded from coverage: same reason as OnPointerPressed — this only runs
        // between a real press and a real release.
        [ExcludeFromCodeCoverage]
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_pressed) return;

            var current = this.PointToScreen(e.GetPosition(this));
            var dx = current.X - _pointerStart.X;
            var dy = current.Y - _pointerStart.Y;

            if (!_dragging && !DragPlan.Exceeded(dx, dy, UnitsPerDip(this))) return;

            // Only on the transition into dragging, not every move after —
            // a flyout animating toward a stale position underneath a moving
            // orb would read as broken, so it's simplest to just take it off
            // screen the instant a drag actually starts.
            if (!_dragging) HideFlyoutNow();

            _dragging = true;
            if (_session is { } session) session.Move(dx, dy);
            else Position = new PixelPoint(_windowStart.X + dx, _windowStart.Y + dy);

            ChatPanel.RepositionFor(this);
        }

        // Excluded from coverage: the end of the same gesture. Either it commits a
        // drag — OrbDragSession.End — or it resolves a click, which is
        // TerminalFocuser again.
        [ExcludeFromCodeCoverage]
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_pressed) return;

            _pressed = false;
            e.Pointer.Capture(null);

            if (_dragging)
            {
                if (_session is { } session) session.End();
                else
                {
                    SetPinned(true);
                    SessionManager.Instance?.RememberOrbPosition(this);
                }
            }
            else
            {
                // A team member has no window of its own — its tmux server has
                // no client attached anywhere — so a click that finds nothing
                // falls through to the session leading it. See
                // TerminalFocuser.Focus.
                // A click has always meant "take me to this session". For a
                // Claude Code session that is its terminal; for a gateway
                // session there is no terminal anywhere, and the honest answer
                // is a place to read and reply — so the panel *is* the
                // destination rather than an extra affordance.
                //
                // Guarded on the source rather than on RemoteChatFor answering,
                // which it now does for local sessions as well: that is what the
                // flyout's keyboard button opens, and it must not quietly become
                // what a click does instead. Going to the terminal is the oldest
                // behaviour this app has and people reach for it without looking.
                OnClicked(_clickCount);
            }

            _session = null;
        }

        // ---- drag squash (CB-268 M4) -------------------------------------------

        // The clock the motion work reads, in milliseconds. A seam so a test
        // steps frames at exact times instead of racing the wall clock.
        internal static Func<long> MotionClock { get; set; } = () => Environment.TickCount64;

        // The disc's one render transform is the drag squash and the arrival pop
        // composed — squash first, then the uniform pop scale — and OrbWindow is
        // the only writer, so the two animations can run together without
        // fighting over it.
        private Matrix _squashOnly = Matrix.Identity;
        private double _popScale = 1.0;

        /// <summary>The matrix currently on the orb's disc (squash x pop scale); the identity at rest.</summary>
        internal Matrix SquashMatrix => _squashTransform.Matrix;

        /// <summary>
        /// The arrival pop's uniform scale of the disc (1.0 is rest), set by the
        /// arrangement's glide. A value that is not a finite positive number
        /// reads as 1.
        /// </summary>
        internal void SetPopScale(double scale)
        {
            _popScale = Finite.All(scale) && scale > 0 ? scale : 1.0;
            ApplyDiscTransform();
        }

        private void ApplyDiscTransform() =>
            _squashTransform.Matrix = _popScale == 1.0
                ? _squashOnly
                : _squashOnly * Matrix.CreateScale(_popScale, _popScale);

        /// <summary>The pointer is at (x, y) DIP while this orb is dragged.</summary>
        internal void SquashObserve(double x, double y)
        {
            var now = MotionClock();
            _squash.Observe(x, y, now);
            StepSquash(now);
            EnsureSquashTimer();
        }

        /// <summary>The drag ended: the stretch relaxes from here (the timer keeps stepping until it is flat).</summary>
        internal void SquashRelease()
        {
            _squash.Release();
            EnsureSquashTimer();
        }

        internal void StepSquash(long nowMs)
        {
            _squashOnly = _squash.Step(nowMs);
            ApplyDiscTransform();
            if (_squash.AtRest) _squashTimer?.Stop();
        }

        private void EnsureSquashTimer()
        {
            if (_squash.AtRest) return;

            _squashTimer ??= CreateSquashTimer();
            if (!_squashTimer.IsEnabled) _squashTimer.Start();
        }

        private DispatcherTimer CreateSquashTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => StepSquash(MotionClock());
            return timer;
        }

        // --- what a click does ------------------------------------------------

        // One, two or three clicks, each bound to whatever the user chose.
        //
        // The awkward part is that these gestures are prefixes of each other: a
        // double click is a single click that turns out to have company. So a
        // single click can only be acted on immediately if nothing longer is
        // bound to something different — otherwise it has to wait long enough to
        // find out, and going to a terminal is the most common thing this app
        // does. The wait is therefore paid only by people who asked for it by
        // binding a second gesture, and never by anyone leaving the defaults
        // alone, where two and three clicks do nothing at all.
        private const int MultiClickMs = 300;

        private DispatcherTimer? _clickTimer;
        private int _pendingClicks;
        private int _clickCount = 1;

        // How many clicks this orb last resolved, and how many gestures have
        // reached that point at all. Written here and read by the UI suite, by
        // nothing in the app.
        //
        // It exists because the two ways a click can come to nothing are
        // indistinguishable from outside, and telling them apart is the whole of
        // the team-orb bug this was added for: a gesture that was *eaten* before
        // it ever became a click, and a gesture that arrived here and found
        // "none" bound to it, both look exactly like an orb that ignored you.
        // Every other observable is downstream of RunClickAction, so a test that
        // asserts on one of those is asserting the destination and cannot say
        // whether the journey started.
        internal int ResolvedGestures { get; private set; }

        internal int LastResolvedClicks { get; private set; }

        internal void OnClicked(int clicks)
        {
            // Beyond three there is nothing to bind, and treating a fourth click
            // as a fresh single would fire the single-click action in the middle
            // of somebody drumming on the orb.
            if (clicks > 3) return;

            ResolvedGestures++;
            LastResolvedClicks = clicks;

            _clickTimer?.Stop();
            _clickTimer = null;
            _pendingClicks = clicks;

            if (!AwaitsMoreClicks(clicks))
            {
                RunClickAction(clicks);
                return;
            }

            _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MultiClickMs) };
            _clickTimer.Tick += (_, _) =>
            {
                _clickTimer?.Stop();
                _clickTimer = null;
                RunClickAction(_pendingClicks);
            };
            _clickTimer.Start();
        }

        // Whether a longer gesture is bound to something this one isn't already
        // going to do. Same action on two and three as on one means the wait
        // would change nothing, so there is no reason to pay it.
        internal static bool AwaitsMoreClicks(int clicks)
        {
            var mine = ActionFor(clicks);

            for (var longer = clicks + 1; longer <= 3; longer++)
            {
                var other = ActionFor(longer);
                if (other != "none" && other != mine) return true;
            }

            return false;
        }

        internal static string ActionFor(int clicks) => clicks switch
        {
            1 => OrbweaverSettings.ClickAction,
            2 => OrbweaverSettings.DoubleClickAction,
            _ => OrbweaverSettings.TripleClickAction
        };

        internal void RunClickAction(int clicks)
        {
            switch (ActionFor(clicks))
            {
                case "chat":
                    OpenChat();
                    break;

                case "speak":
                    OnSpeakClicked();
                    break;

                case "none":
                    break;

                default:
                    GoToSession();
                    break;
            }
        }

        // "Take me to this session", which for a local CLI is its terminal and
        // for a gateway agent is the only place it exists — the panel. That
        // second case is not a fallback bolted on for this feature; it is what a
        // click on a gateway orb has always done, because there is no terminal
        // anywhere to go to.
        internal void GoToSession()
        {
            // Before either of the two below, because a cloud session can
            // satisfy neither. There is no chat session to hand back for it —
            // it is not on the gateway and not on a paired machine — and there
            // is certainly no terminal, so without this arm the click fell all
            // the way through to TerminalFocuser.Focus and went hunting a pane
            // for a conversation that has never been on this disk.
            if (TryOpenInBrowser()) return;

            // A Claude Desktop-hosted session has no terminal; its window is the
            // destination. Before the terminal path for the same reason as the
            // arm above — that path would go hunting a pane that does not exist.
            if (TryOpenClaudeDesktop()) return;

            if (!(_lastStatus?.IsLocalCli ?? false) && TryOpenRemoteChat()) return;

            TerminalFocuser.Focus(
                _lastStatus,
                SessionManager.Instance?.StatusFor(_lastStatus?.Lead),
                SessionId,
                SessionManager.Instance?.PaneClaimsByOthers(SessionId),
                acknowledge: AcknowledgeFromAnyThread);
        }

        // Put the orb at a position it was dragged to in an earlier run, without
        // treating it as a fresh drag (nothing to write back).
        public void PinAt(PixelPoint position)
        {
            Position = position;
            SetPinned(true);
        }

        public void Unpin() => SetPinned(false);

        private void SetPinned(bool pinned)
        {
            IsPinned = pinned;
            // Only worth offering once there's something to undo.
            ResetPositionItem.IsVisible = pinned;
        }

        // The roster, for a background orb that wants it. Delegates the same way
        // the two lifecycle handlers below do: the orb knows which session was
        // right-clicked and nothing else, and every rule about where a gesture
        // goes lives in ClickRouting with the subprocesses in TerminalFocuser.
        //
        // Safe with no status yet — TerminalFocuser.OpenAgentsView returns on
        // null — which matters because the menu is built before the first
        // UpdateFrom, and a right-click during that window would otherwise be
        // the one gesture that could throw.
        internal void AgentsView_Click(object? sender, RoutedEventArgs e)
        {
            TerminalFocuser.OpenAgentsView(_lastStatus);
        }

        internal void ResetIdle_Click(object? sender, RoutedEventArgs e)
        {
            // The disabled menu row explains why this is unavailable for
            // OpenClaw and remote-control sessions. Keep the manager's guard
            // as well: a stale click must not turn a remote id into a path.
            SessionManager.Instance?.ResetSessionToIdle(SessionId);
        }

        internal void ResetPosition_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.ReturnOrbToStack(SessionId);
        }

        // Both of these delegate rather than acting, the same way ResetIdle_Click
        // does: the orb knows which session was right-clicked and nothing else,
        // and every rule about what may be done to a session lives with the
        // manager that owns its file and its pid.
        internal void Dismiss_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.DismissSession(SessionId);
        }

        internal void EndSession_Click(object? sender, RoutedEventArgs e)
        {
            _ = EndSessionRecordingFailureAsync();
        }

        // The click's task, observed (CB-228). EndSession fails closed — a read
        // or a kill that throws ends nothing — but it now fails on a pool
        // thread, into a task the click used to discard, so a failure left no
        // trace at all. It goes to crash.log, the one log every user has, under
        // a source naming the gesture. There is still nothing to show on screen:
        // the app has no dialog vocabulary, and the orb staying put is what the
        // user sees.
        internal async Task EndSessionRecordingFailureAsync()
        {
            var manager = CurrentManager();
            if (manager is null) return;

            try
            {
                await manager.EndSession(SessionId);
            }
            catch (Exception error)
            {
                CrashLog.Record("End this session", error);
            }
        }

        // --- CB-170: an OpenClaw conversation's Interrupt and End rows -------
        //
        // Seams rather than direct calls, so the UI suite can drive the rows
        // against a fake the way ChatPanel's suites drive FakeChatSession. The
        // defaults are the real thing; nothing in the app sets them.
        internal Func<string, OpenClawActionContext> OpenClawCapabilities { get; set; } =
            OpenClawSessions.CapabilitiesFor;

        internal Func<string, CancellationToken, Task<(OpenClawActionOutcome Outcome, string? Detail)>>
            InterruptAction { get; set; } = OpenClawSessions.InterruptAsync;

        internal Func<string, CancellationToken, Task<(OpenClawActionOutcome Outcome, string? Detail)>>
            EndAction { get; set; } = (id, ct) => OpenClawSessions.EndConversationAsync(id, ct);

        // How long an armed End waits for its second click. The same six
        // seconds the settings window's profile delete gives — the one confirm
        // this app already has, and the shape this row copies: the row says
        // what it is about to do while it waits, and gives up on its own so a
        // stray click cannot leave it armed.
        //
        // Settable per orb only so a test can watch the timer itself fire
        // instead of waiting six real seconds for it.
        internal TimeSpan EndDisarmAfter { get; set; } = TimeSpan.FromSeconds(6);

        // Held: the rows are saying something UpdateFrom must not overwrite.
        // Busy: a request is out, and a second click must not send another.
        // Release: the menu closed while busy, so the answer, when it lands,
        // has nobody to be shown to and the rows go back to their plain words.
        private bool _openClawRowsHeld;
        private bool _openClawBusy;
        private bool _openClawReleaseWhenDone;
        private DispatcherTimer? _endDisarm;

        internal bool EndConversationArmed => _endDisarm is not null;

        internal void RestoreOpenClawRows()
        {
            SetRow(InterruptRunItem, OpenClawActionText.Header(OpenClawAction.Interrupt),
                OpenClawActionText.Tip(OpenClawAction.Interrupt), enabled: true);
            SetRow(EndConversationItem, OpenClawActionText.Header(OpenClawAction.End),
                OpenClawActionText.Tip(OpenClawAction.End), enabled: true);
        }

        private static void SetRow(MenuItem item, string header, string tip, bool enabled)
        {
            item.Header = header;
            ToolTip.SetTip(item, tip);
            item.IsEnabled = enabled;
        }

        // async void is the event's shape; the work, and everything a test
        // awaits, is in the Task-returning methods below.
        internal async void InterruptRun_Click(object? sender, RoutedEventArgs e) =>
            await InterruptRunAsync();

        internal async void EndConversation_Click(object? sender, RoutedEventArgs e) =>
            await EndConversationClickAsync();

        // No arm step: stopping a run loses nothing but the rest of the reply,
        // and is a no-op when nothing is running.
        internal async Task InterruptRunAsync()
        {
            if (_openClawBusy) return;

            _openClawBusy = true;
            _openClawRowsHeld = true;
            SetRow(InterruptRunItem, OpenClawActionText.Working(OpenClawAction.Interrupt),
                OpenClawActionText.Tip(OpenClawAction.Interrupt), enabled: false);

            var (outcome, detail) = await InterruptAction(SessionId, CancellationToken.None);
            Report(InterruptRunItem, OpenClawAction.Interrupt, outcome, detail);
        }

        // First click arms, second click acts. Archiving changes state every
        // other client of the gateway can see, which is the reason for the
        // confirm; it is reversible from OpenClaw, which is the reason it is two
        // clicks on the row rather than a dialog.
        internal async Task EndConversationClickAsync()
        {
            if (_openClawBusy) return;

            if (_endDisarm is null)
            {
                _openClawRowsHeld = true;
                EndConversationItem.Header = OpenClawActionText.Armed;

                _endDisarm = new DispatcherTimer { Interval = EndDisarmAfter };
                _endDisarm.Tick += (_, _) => DisarmEndConversation();
                _endDisarm.Start();
                return;
            }

            StopEndDisarm();
            _openClawBusy = true;
            SetRow(EndConversationItem, OpenClawActionText.Working(OpenClawAction.End),
                OpenClawActionText.Tip(OpenClawAction.End), enabled: false);

            var (outcome, detail) = await EndAction(SessionId, CancellationToken.None);
            Report(EndConversationItem, OpenClawAction.End, outcome, detail);
        }

        // The timer giving up on an armed End. Only the End row's wording goes
        // back: an Interrupt answer showing beside it is still worth reading.
        internal void DisarmEndConversation()
        {
            StopEndDisarm();
            EndConversationItem.Header = OpenClawActionText.Header(OpenClawAction.End);
        }

        private void StopEndDisarm()
        {
            _endDisarm?.Stop();
            _endDisarm = null;
        }

        // The answer, on the row that asked, with the whole sentence in the
        // tooltip as well — a gateway refusal can run longer than a menu row is
        // wide.
        private void Report(MenuItem item, OpenClawAction action, OpenClawActionOutcome outcome, string? detail)
        {
            _openClawBusy = false;

            if (_openClawReleaseWhenDone)
            {
                ReleaseOpenClawRows();
                return;
            }

            var text = OpenClawActionText.For(outcome, detail, action);
            SetRow(item, text, text, enabled: true);
        }

        // The menu closing: whatever the rows were saying was said to someone
        // who has now looked away. Unless a request is still out, in which case
        // its answer releases them when it lands.
        internal void SessionMenu_Closed(object? sender, RoutedEventArgs e)
        {
            // CB-228: an End-row read still in flight writes nothing now, and
            // the next scan's UpdateFrom is free to put the plain wording back.
            _endGuardRequests.Retire();
            _endGuardHeld = false;

            if (_cloudBusy) _cloudReleaseWhenDone = true;
            else ReleaseCloudRows();

            if (_openClawBusy)
            {
                _openClawReleaseWhenDone = true;
                return;
            }

            ReleaseOpenClawRows();
        }

        private void ReleaseOpenClawRows()
        {
            StopEndDisarm();
            _openClawRowsHeld = false;
            _openClawReleaseWhenDone = false;
            RestoreOpenClawRows();
        }

        // --- a cloud session's Archive and Delete (CB-225) -----------------------

        // The request itself. Settable so a test can answer it without
        // api.anthropic.com or a Keychain; production asks as the session's
        // owner and tombstones the orb on success (ClaudeCloudSessions).
        internal Func<CloudLifecycleAction, string, CancellationToken, Task<CloudLifecycleResult>>
            CloudAction { get; set; } = (action, key, ct) => ClaudeCloudSessions.RunLifecycleAsync(action, key, ct);

        // How long an armed row waits for its second click: the six seconds
        // EndDisarmAfter gives, for the same reason. Settable only so a test
        // can watch the timer fire.
        internal TimeSpan CloudDisarmAfter { get; set; } = TimeSpan.FromSeconds(6);

        // Separate from the OpenClaw rows' state on purpose. An orb is never
        // both kinds today, but sharing one "busy" between two transports
        // would let one's answer release the other's rows — the kind of
        // coupling that only shows itself the day something changes.
        private bool _cloudRowsHeld;
        private bool _cloudBusy;
        private bool _cloudReleaseWhenDone;
        private DispatcherTimer? _cloudDisarm;
        private CloudLifecycleAction? _cloudArmed;

        // What each row said when its action succeeded. **A success is never
        // released.** The orb is about to go — its session is tombstoned — but
        // until the next scan takes it the menu can still be opened, and a
        // row restored to its plain, enabled words would offer to archive or
        // delete again what has just been archived or deleted. Hana found the
        // way there: a success answered after the menu had closed went through
        // the release path and came back enabled. One orb is one session, so
        // nothing ever needs to clear this.
        private readonly Dictionary<CloudLifecycleAction, string> _cloudDone = new();

        internal CloudLifecycleAction? CloudArmed => _cloudArmed;

        private MenuItem CloudRow(CloudLifecycleAction action) =>
            action == CloudLifecycleAction.Archive ? ArchiveCloudItem : DeleteCloudItem;

        internal void RestoreCloudRows()
        {
            foreach (var action in new[] { CloudLifecycleAction.Archive, CloudLifecycleAction.Delete })
            {
                if (_cloudDone.TryGetValue(action, out var done))
                    SetRow(CloudRow(action), done, done, enabled: false);
                else
                    SetRow(CloudRow(action), CloudActionText.Header(action), CloudActionText.Tip(action), enabled: true);
            }
        }

        internal async void ArchiveCloud_Click(object? sender, RoutedEventArgs e) =>
            await CloudActionClickAsync(CloudLifecycleAction.Archive);

        internal async void DeleteCloud_Click(object? sender, RoutedEventArgs e) =>
            await CloudActionClickAsync(CloudLifecycleAction.Delete);

        // First click arms, second click acts — for Archive as well as Delete.
        // Archive is not permanent, but it disconnects the session and takes
        // an orb away, which is more than one stray click should do. Arming one
        // row disarms the other, so a second click can only ever act on the row
        // that says "Click again". A click while a request is out does nothing:
        // two fast clicks after arming send one request.
        internal async Task CloudActionClickAsync(CloudLifecycleAction action)
        {
            // A disabled row ignores the click in code as well as on screen:
            // a row that has just said "Archived" must not re-arm because a
            // click raced the disable, and Avalonia gives no promise that a
            // click already dispatched checks IsEnabled again.
            if (_cloudBusy || !CloudRow(action).IsEnabled) return;

            if (_cloudArmed != action)
            {
                // Only the other *armed* row goes back to its plain words; an
                // answer showing on it is still worth reading.
                DisarmCloud();
                _cloudRowsHeld = true;
                _cloudArmed = action;
                CloudRow(action).Header = CloudActionText.Armed(action);

                _cloudDisarm = new DispatcherTimer { Interval = CloudDisarmAfter };
                _cloudDisarm.Tick += (_, _) => DisarmCloud();
                _cloudDisarm.Start();
                return;
            }

            StopCloudDisarm();
            _cloudBusy = true;
            var row = CloudRow(action);
            SetRow(row, CloudActionText.Working(action), CloudActionText.Tip(action), enabled: false);

            CloudLifecycleResult result;
            try
            {
                result = await CloudAction(action, SessionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // An async void handler that throws takes the app with it. The
                // action layer turns every answer into a result, so this is for
                // the one thing it cannot: something nobody anticipated.
                result = new CloudLifecycleResult(CloudLifecycleVerdict.Refused, ex.Message);
            }

            _cloudBusy = false;

            // A success stays disabled, on this pass and every later restore:
            // the orb is about to go, and the row must not offer to do again
            // what has just been done.
            var text = CloudActionText.For(action, result);
            if (result.Succeeded) _cloudDone[action] = text;

            if (_cloudReleaseWhenDone)
            {
                ReleaseCloudRows();
                return;
            }

            SetRow(row, text, text, enabled: !result.Succeeded);
        }

        internal void DisarmCloud()
        {
            if (_cloudArmed is { } armed) CloudRow(armed).Header = CloudActionText.Header(armed);
            StopCloudDisarm();
        }

        private void StopCloudDisarm()
        {
            _cloudDisarm?.Stop();
            _cloudDisarm = null;
            _cloudArmed = null;
        }

        private void ReleaseCloudRows()
        {
            StopCloudDisarm();
            _cloudRowsHeld = false;
            _cloudReleaseWhenDone = false;
            RestoreCloudRows();
        }

        // What this orb would pre-fill the "New chat…" dialog with, or null
        // if there's no session bound here to pre-fill from at all. Pure —
        // no window, no Toggle() call — so the mapping is testable without
        // ever opening a real NewChatWindow. NewChatOrbWatch.CliOf is shared
        // with the dialog's own "did an orb appear" watch, so the two never
        // map a session's CLI differently.
        //
        // sessionId is a separate parameter rather than read off status,
        // because OpenClawSessions.AgentIdOf parses the session *id*
        // ("openclaw:agent:<id>:<surface>"), not anything SessionStatus
        // itself carries — the same reason SessionManager.OrbFor keys off
        // ids rather than statuses.
        internal static (NewChatCli? Cli, string Cwd, string? AgentId)? NewChatPrefillFor(
            SessionStatus? status, string sessionId) =>
            status is null
                ? null
                : (NewChatOrbWatch.CliOf(status.Source), status.Cwd,
                    status.Source == SessionSource.OpenClaw ? OpenClawSessions.AgentIdOf(sessionId) : null);

        // CB-168: opens NewChatWindow pre-filled per NewChatPrefillFor
        // above. Excluded from coverage the same way OpenSettings is —
        // Toggle() does real OS-facing work headless tests have no business
        // doing — but narrowed to this one call rather than the whole
        // handler, so the prefill mapping above stays covered.
        internal void NewChatHere_Click(object? sender, RoutedEventArgs e)
        {
            if (NewChatPrefillFor(_lastStatus, SessionId) is not { } prefill) return;
            OpenNewChatWindow(prefill.Cli, prefill.Cwd, prefill.AgentId);
        }

        [ExcludeFromCodeCoverage]
        private static void OpenNewChatWindow(NewChatCli? cli, string cwd, string? agentId) =>
            NewChatWindow.Toggle(cli, cwd, agentId);

        // What is running underneath this session, asked once, as the menu
        // opens — CB-26.
        //
        // Here rather than in UpdateFrom because of what it costs: the answer
        // comes from a `ps` over the whole process table, and UpdateFrom runs
        // for every orb on screen every couple of seconds. Opening a context
        // menu is a gesture a person makes a few times an hour, and the answer
        // is cached for two seconds either side of it, so the click that follows
        // shares this read rather than paying for a second one.
        //
        // The manager comes through CurrentManager, so a test can open the menu
        // against a manager it built without making it the process-wide one.
        internal void SessionMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Ahead of the manager-null guard below, and unconditionally —
            // the Sound submenus have nothing to do with DependentsOfAsync and
            // should still populate for a test or a standalone window that
            // never made a SessionManager current.
            RebuildSoundSubmenus();
            RebuildSizeSubmenu();
            RebuildGroupSubmenu();

            // "Reset position" undoes a hand placement: a free pin, or (CB-268
            // T2 revised) a team member's saved offset from its lead in a
            // laid-out group, which is never a pin.
            ResetPositionItem.IsVisible = IsPinned || OrbweaverSettings.OrbMemberOffsetFor(SoundKey) is not null;

            var manager = CurrentManager();
            if (manager is null) return;

            _ = RefreshEndSessionGuardAsync(manager.DependentsOfAsync);
        }

        // The End row's answer, read off the UI thread (CB-228).
        //
        // This used to be a synchronous call from the Opening handler, and on
        // Windows the read under it is a WMI query over the whole process
        // table — 218-273 ms per read on the Windows PC, which the menu waited
        // for before it appeared. Now the menu opens straight away with the row
        // disabled and saying it is checking (SessionDependents.CheckingHeader
        // has why it starts refused rather than plain), and the answer replaces
        // that when it arrives. The continuation after the await is back on the
        // UI thread, so the row is only ever touched there.
        //
        // Asked only when the row is offered: an orb whose row is hidden
        // (gateway, remote, pid-less) has nothing to check, and the manager
        // would answer Nothing without reading anyway.
        //
        // A read that throws, or has not answered by ReadTimeout, leaves the
        // row disabled and saying it could not check — never enabled.
        // SessionDependents.UnknownHeader has why that is not the fail-open
        // direction Nothing takes.
        //
        // Held while the menu is open, so UpdateFrom's reset to the plain
        // wording — which runs for this orb every scan — cannot overwrite the
        // answer the user is reading. Before this, a scan landing while the
        // menu was open put a husk's row back to an enabled "End this session"
        // under the cursor; the click was still refused, but the row had stopped
        // telling the truth about it.
        internal async Task RefreshEndSessionGuardAsync(
            Func<string, Task<SessionDependents.Verdict>> read, TimeSpan? timeout = null)
        {
            if (!EndSessionItem.IsVisible) return;

            var ticket = _endGuardRequests.Begin();
            _endGuardHeld = true;

            EndSessionItem.IsEnabled = false;
            EndSessionItem.Header = SessionDependents.CheckingHeader;
            ToolTip.SetTip(EndSessionItem, SessionDependents.CheckingTip);

            SessionDependents.Verdict verdict;
            try
            {
                verdict = await read(SessionId).WaitAsync(timeout ?? SessionDependents.ReadTimeout);
            }
            catch
            {
                if (!_endGuardRequests.IsCurrent(ticket)) return;

                EndSessionItem.IsEnabled = false;
                EndSessionItem.Header = SessionDependents.UnknownHeader;
                ToolTip.SetTip(EndSessionItem, SessionDependents.UnknownTip);
                return;
            }

            if (!_endGuardRequests.IsCurrent(ticket)) return;

            ApplyEndSessionGuard(verdict);
        }

        // Which manager this orb's menu asks (CB-228). The process-wide one in
        // the app; a test sets its own, so the Opening and End-click paths can
        // be driven without SessionManager.Start() and its watcher, timer and
        // tray icon.
        internal Func<SessionManager?> CurrentManager { get; set; } = () => SessionManager.Instance;

        // The groups with an orb on screen, which "Move to" is filtered to; null
        // (no manager) filters nothing. A test says which they are here.
        internal Func<IReadOnlySet<string>?>? GroupsOnScreenForTests { get; set; }

        private IReadOnlySet<string>? GroupsOnScreen() =>
            GroupsOnScreenForTests is { } forTests ? forTests() : CurrentManager()?.GroupsOnScreen();


        private readonly SessionDependents.GuardRequests _endGuardRequests = new();
        private bool _endGuardHeld;

        // internal so a test can drive it directly rather than through the
        // real ContextMenu.Opening event, which needs a shown window with a
        // working popup — the same reason ApplyEndSessionGuard above is
        // exercised the same way.
        //
        // Rebuilt from scratch on every open rather than cached: the disk
        // listing and the saved override can both have changed since this
        // orb's menu was last shown (a file picked from another orb, a
        // system sound removed), and a MenuItem tree is cheap enough to
        // throw away and redo that caching it would only optimise a cost
        // this doesn't have.
        internal void RebuildSoundSubmenus()
        {
            var over = OrbweaverSettings.OrbTurnSoundFor(SoundKey);

            BuildSoundSubmenu(SoundFinishedMenuItem, over?.Finished,
                SystemSoundCatalog.DefaultFinishedSoundName, includeSummary: true,
                SetFinishedSoundOverride);

            BuildSoundSubmenu(SoundAttentionMenuItem, over?.Attention,
                SystemSoundCatalog.DefaultAttentionSoundName, includeSummary: false,
                SetAttentionSoundOverride);
        }

        // The same Default/Off/[Vibe summary]/system sounds/Choose file…
        // list SettingsWindow's pickers offer, reused rather than
        // reimplemented — SettingsWindow.SoundChoices' own comment says why
        // a second copy of this list is exactly the kind of drift CB-153's
        // doubled colour switch already cost this project once.
        //
        // MenuItemToggleType.CheckBox rather than a "✓ " text prefix on the
        // active item's Header: CB-173 is an open bug about a font glyph
        // rendering as a colour emoji on Windows for the settings-disclosure
        // chevron, and a checkmark character is exactly that kind of small,
        // easy-to-miss-in-review glyph. The toggle check Avalonia draws for
        // CheckBox is template chrome, not a font lookup, so it can't have
        // that problem.
        private void BuildSoundSubmenu(
            MenuItem parent, string? current, string defaultName, bool includeSummary, Action<string?> write)
        {
            parent.Items.Clear();

            foreach (var (label, value) in SettingsWindow.SoundChoices(current, defaultName, includeSummary))
            {
                if (value == SettingsWindow.ChooseFileValue)
                {
                    var chooseItem = new MenuItem { Header = label };

                    // async void: a MenuItem's Click has nowhere to return a
                    // Task to, the same reason SettingsWindow.SoundPicker's
                    // own SelectionChanged handler is. A cancelled pick
                    // (null) leaves the override exactly as it was — there
                    // is no combo selection to revert here, unlike the
                    // settings picker, since a context menu simply closes
                    // rather than sitting on a stale choice.
                    chooseItem.Click += async (_, _) =>
                    {
                        var path = await SettingsWindow.ChooseSoundFile(this);
                        if (path is not null) write(path);
                    };

                    parent.Items.Add(chooseItem);
                    continue;
                }

                var item = new MenuItem
                {
                    Header = label,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = value == current
                };
                item.Click += (_, _) => write(value);
                parent.Items.Add(item);
            }
        }

        // CB-198's per-orb size. Rebuilt on Opening for the Sound submenus'
        // reason: the slider (which the Default row names) and this orb's own
        // override can both have moved since the menu was last shown. The same
        // CheckBox toggle rather than a text checkmark, for CB-173's reason
        // given on BuildSoundSubmenu.
        //
        // Default is checked only when there is no override — an override that
        // happens to equal the slider is still an override, and will stop
        // following the slider the moment the slider moves, so the menu says
        // which of the two it is rather than which number it currently draws.
        internal void RebuildSizeSubmenu()
        {
            var over = OrbweaverSettings.OrbSizeFor(SoundKey);

            SizeMenuItem.Items.Clear();

            var defaultItem = new MenuItem
            {
                Header = $"Default ({SizeLabel(OrbweaverSettings.OrbSize)})",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = over is null
            };
            defaultItem.Click += (_, _) => SetSizeOverride(null);
            SizeMenuItem.Items.Add(defaultItem);

            SizeMenuItem.Items.Add(new Separator());

            foreach (var preset in OrbSizing.Presets)
            {
                var item = new MenuItem
                {
                    Header = SizeLabel(preset),
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = over is double o && Math.Abs(o - preset) < 1e-9
                };
                item.Click += (_, _) => SetSizeOverride(preset);
                SizeMenuItem.Items.Add(item);
            }
        }

        // CB-268's Group submenu, rebuilt on Opening for the Size submenu's
        // reason: the group table, this orb's membership and its team can all
        // have changed since the menu was last shown (a drag-snap, another
        // orb's menu). Every write goes to the *team* — a team is the unit of
        // grouping, so moving a member moves its lead and its siblings with it.
        internal void RebuildGroupSubmenu()
        {
            var context = CurrentGroupContext();
            var group = OrbweaverSettings.OrbGroupFor(context.GroupId) ?? OrbGroups.DefaultFor(context.GroupId);

            GroupMenuItem.Items.Clear();

            GroupMenuItem.Items.Add(new MenuItem { Header = $"In: {group.Name}", IsEnabled = false });
            GroupMenuItem.Items.Add(new Separator());

            var shape = new MenuItem { Header = "Shape" };
            foreach (var (label, value) in SettingsWindow.ShapeChoices)
            {
                var item = new MenuItem
                {
                    Header = label,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = value == group.Shape
                };
                item.Click += (_, _) =>
                {
                    OrbweaverSettings.SetOrbGroupShape(group.Id, value);
                    CurrentManager()?.ReapplyArrangement();
                };
                shape.Items.Add(item);
            }
            GroupMenuItem.Items.Add(shape);

            var moveTo = new MenuItem { Header = "Move to" };
            foreach (var other in OrbGroupMenu.MoveTargets(
                         OrbweaverSettings.OrbGroupList(), group.Id, GroupsOnScreen()))
            {
                var target = other.Id;
                var item = new MenuItem { Header = other.Name };
                item.Click += (_, _) => MoveTeamToGroup(context, target);
                moveTo.Items.Add(item);
            }
            moveTo.IsEnabled = moveTo.Items.Count > 0;
            GroupMenuItem.Items.Add(moveTo);

            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += (_, _) => GroupStyleWindow.Toggle(group.Id, focusName: true);
            GroupMenuItem.Items.Add(rename);

            var style = new MenuItem { Header = "Style…" };
            style.Click += (_, _) => GroupStyleWindow.Toggle(group.Id, focusName: false);
            GroupMenuItem.Items.Add(style);

            GroupMenuItem.Items.Add(new Separator());

            // Hidden, not greyed, in main: there is nowhere further to leave to,
            // and a disabled row would suggest something is missing.
            var leave = new MenuItem { Header = "Leave group", IsVisible = group.Id != OrbGroups.Main };
            leave.Click += (_, _) => MoveTeamToGroup(context, OrbGroups.Main);
            GroupMenuItem.Items.Add(leave);
        }

        // A test seam, the same shape as CurrentManager: the context a manager
        // would compute from the live statuses, which a menu test has no
        // manager to supply.
        internal Func<GroupMenuContext?>? GroupContextOverride { get; set; }

        private GroupMenuContext CurrentGroupContext()
        {
            if (GroupContextOverride?.Invoke() is { } overridden) return overridden;
            if (CurrentManager()?.GroupMenuContextFor(SessionId) is { } fromManager) return fromManager;

            // No manager (a standalone window): this orb alone, in whatever its
            // own key says.
            var key = string.IsNullOrEmpty(PositionKey) ? null : PositionKey;
            var membership = OrbweaverSettings.OrbGroupMembershipFor(key);
            return new GroupMenuContext(
                OrbGroups.GroupIdFor(membership, OrbCluster.Chats, ClusterMode.WithChats, ClusterMode.WithChats,
                    ClusterMode.WithChats),
                key, Array.Empty<string>());
        }

        private void MoveTeamToGroup(GroupMenuContext context, string groupId)
        {
            OrbweaverSettings.AssignTeamToGroup(context.LeadKey, context.MemberKeys, groupId);
            CurrentManager()?.ReapplyArrangement();
        }

        internal static string SizeLabel(double size) =>
            $"{Math.Round(size * 100).ToString(System.Globalization.CultureInfo.InvariantCulture)}%";

        // Every orb, not just this one: a size change moves the stack or the
        // arrangement around the orb that changed, so the owning manager does
        // it (SizeRelayoutRequested). With no manager listening — a standalone
        // window — there is nothing else to move, so this orb resizes itself.
        private void SetSizeOverride(double? size)
        {
            OrbweaverSettings.SetOrbSize(SoundKey, size);

            if (SizeRelayoutRequested is { } relayout) relayout();
            else ApplyEffectiveOrbSize();
        }

        // Reads the other trigger's current override so writing one never
        // clobbers the other — OrbweaverSettings.SetOrbTurnSound takes
        // both fields together, and OrbTurnSound has no "leave unchanged"
        // value of its own to pass instead.
        private void SetFinishedSoundOverride(string? value)
        {
            var over = OrbweaverSettings.OrbTurnSoundFor(SoundKey);
            OrbweaverSettings.SetOrbTurnSound(SoundKey, value, over?.Attention);
        }

        private void SetAttentionSoundOverride(string? value)
        {
            var over = OrbweaverSettings.OrbTurnSoundFor(SoundKey);
            OrbweaverSettings.SetOrbTurnSound(SoundKey, over?.Finished, value);
        }

        // QA round 2 (HIGH): called from UpdateFrom on every poll rather
        // than once at creation — see SoundKey's own comment for why a
        // one-time value goes stale the moment an untitled session gets its
        // real title.
        //
        // A changed key migrates its override rather than simply moving on:
        // if the old key still carries one and the new key doesn't already
        // have its own (an orb that never had an override at all, or one
        // whose new key happens to collide with an existing choice, is left
        // alone either way), it is copied onto the new key. Without this, a
        // choice a user already made — "Off" clicked from the menu before
        // the title arrived — would keep silently reverting to the default
        // the instant Claude Code named the session, which is exactly the
        // bug QA found: the menu showed Off ticked while the orb went on
        // chiming.
        //
        // QA round 3, finding 2 (LOW-MEDIUM): a blank key reads as "free" to
        // both OrbTurnSoundFor (its own empty-key guard returns null) and
        // SetOrbTurnSound (an empty-key guard that makes the write a
        // no-op) — so migrating *into* one used to look exactly like a safe
        // migration onto an unoccupied key, write nothing there, and still
        // clear the override out from under the old key on the way out.
        // Reachable on a real machine, not a contrived edge case: the hook
        // writes cwd with no fallback (OrbweaverHook.sh:70), so a single
        // status write missing it is enough to blank the key for one poll.
        // Skipped entirely instead — the window keeps using its last real
        // key, the same resilience a missing title already gets by falling
        // back to the session id rather than an empty string.
        //
        // QA round 3, finding 3 (LOW-MEDIUM): migration used to clear the
        // old key on the way out, which is exactly wrong when two orbs
        // share one key (same cwd and title, no agent name to tell them
        // apart, which is exactly the case SoundKeyFor cannot distinguish).
        // Renaming one of them moved the shared override onto its own new
        // key and cleared the shared key out from under the other, silently
        // unmuting a session that never asked to be. Copied rather than
        // moved now: an orphaned entry under a key nothing looks up any
        // more is harmless, and losing a mute is not.
        internal void RefreshSoundKey(SessionStatus status)
        {
            var key = SessionManager.SoundKeyFor(status, SessionId);
            if (key == SoundKey || string.IsNullOrEmpty(key)) return;

            var hadKey = !string.IsNullOrEmpty(SoundKey);
            if (hadKey)
            {
                var stale = OrbweaverSettings.OrbTurnSoundFor(SoundKey);
                if (stale is not null && OrbweaverSettings.OrbTurnSoundFor(key) is null)
                {
                    OrbweaverSettings.SetOrbTurnSound(key, stale.Finished, stale.Attention);
                }

                // CB-198's size override rides the same key, so it migrates by
                // the same rule — copied, not moved, and never over a size the
                // new key already has — for the same two QA findings above: a
                // 200% chosen before the title arrived must not snap back to
                // the slider the moment it does, and a shared key must not be
                // emptied out from under the other orb using it.
                // The team-member offset (CB-268 T2) rides the same key and the
                // same rule.
                OrbweaverSettings.CarryOrbMemberOffset(SoundKey, key);

                var staleSize = OrbweaverSettings.OrbSizeFor(SoundKey);
                if (staleSize is not null && OrbweaverSettings.OrbSizeFor(key) is null)
                {
                    OrbweaverSettings.SetOrbSize(key, staleSize);
                }
            }

            SoundKey = key;

            // The first real key is also the first moment this orb can know its
            // own size, and the manager's layout pass for a new orb places it
            // (SessionManager calls UpdateFrom before RestoreOrbPosition). A
            // *later* key can carry a different override on an orb already in
            // a stack or an arrangement, and nothing else would re-lay those
            // out around its new size — so that case says so, to whichever
            // manager owns this orb; see SizeRelayoutRequested.
            if (ApplyEffectiveOrbSize() && hadKey)
            {
                SizeRelayoutRequested?.Invoke();
            }
        }

        // The row that says what it will do, or why it will not.
        //
        // Disabled rather than hidden, and worded as what the user would lose
        // rather than as what the app refuses — SessionDependents.Explain and
        // ExplainTip own both sentences, so the menu and the manager's guard
        // cannot end up describing different rules. The visibility above is
        // untouched: whether this session can be ended *at all* is
        // CanEndSession's question, and this only answers whether it may be
        // ended right now.
        internal void ApplyEndSessionGuard(SessionDependents.Verdict dependents)
        {
            EndSessionItem.IsEnabled = !SessionDependents.BlocksTermination(dependents);
            EndSessionItem.Header = SessionDependents.Explain(dependents);
            ToolTip.SetTip(EndSessionItem, SessionDependents.ExplainTip(dependents));
        }

        // Excluded from coverage: needs SessionManager.Instance to hand back a
        // session, and this suite never sets it — making one current starts the
        // status-directory watcher, the scan timer and a tray icon. Same reason
        // OpenChat above carries the attribute, and the panel it would open is
        // covered directly in the ChatPanel suites against a FakeChatSession.
        // Where a cloud session actually is. False for everything else, so this
        // can sit in front of the two destinations that assume a conversation
        // this machine can reach.
        //
        // Guarded on the source rather than on the URL being non-empty. A cloud
        // session whose address never arrived should open nothing and stop
        // there — falling through to a terminal focus would send the click to
        // whatever pane happened to match, which is the failure this arm exists
        // to prevent and is worse than the click doing nothing.
        // Excluded from coverage: its one effect is CloudSessionLink.Open, which
        // launches a real browser on whichever machine runs the suite. Both
        // halves of what it decides are reachable elsewhere — the rule is
        // ClickRouting.OpensInABrowser and the launch is
        // CloudSessionLink.StartInfoFor, and both are pure and covered.
        [ExcludeFromCodeCoverage]
        internal bool TryOpenInBrowser()
        {
            if (!ClickRouting.OpensInABrowser(_lastStatus)) return false;

            CloudSessionLink.Open(_lastStatus!.Url);
            return true;
        }

        // Excluded from coverage for TryOpenInBrowser's reason: its one effect
        // raises another application's window, which a headless run must not do
        // (and a headless suite cannot click a local orb for the same reason the
        // terminal path is not clicked). The rule is ClickRouting.OpensClaudeDesktop
        // and the walk is DesktopSessionFocus.Focus, both seamed and covered.
        [ExcludeFromCodeCoverage]
        internal bool TryOpenClaudeDesktop()
        {
            if (!ClickRouting.OpensClaudeDesktop(_lastStatus)) return false;

            var status = _lastStatus!;
            System.Threading.Tasks.Task.Run(() =>
                DesktopSessionFocus.Focus(status, AcknowledgeFromAnyThread));
            return true;
        }

        [ExcludeFromCodeCoverage]
        private bool TryOpenRemoteChat()
        {
            var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
            if (chat is null) return false;

            _chatOpen = true;
            HideFlyoutNow();
            ChatPanel.OpenFor(this, chat);
            return true;
        }

        // The orb menu's own quit item, which OrbWindow.axaml reads with
        // x:Static. A const rather than a binding with a StringFormat: a const
        // can't fail to resolve at runtime, and the menu reads the same as the
        // tray's "Quit" item builds it. Public because x:Static resolves nothing
        // less.
        public const string ExitMenuHeader = $"Exit {Brand.DisplayName}";

        internal void Exit_Click(object? sender, RoutedEventArgs e) =>
            ShutdownIfDesktop(Application.Current?.ApplicationLifetime);

        // Excluded from coverage: its guard is the only reachable half under a
        // headless lifetime, and the half behind it ends the process. Splitting
        // the two so the guard could be measured would leave the more interesting
        // question — is this lifetime a desktop one? — measured in a method that
        // does nothing with the answer.
        //
        // IsDesktopLifetime is that question asked where a test can ask it, and
        // ItRefusesToQuitAHostThatIsNotADesktopApp asserts the answer that keeps
        // this harmless.
        [ExcludeFromCodeCoverage]
        private static void ShutdownIfDesktop(IApplicationLifetime? lifetime)
        {
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop) Shutdown(desktop);
        }

        internal static bool IsDesktopLifetime(IApplicationLifetime? lifetime) =>
            lifetime is IClassicDesktopStyleApplicationLifetime;

        // Excluded from coverage: ends the process. Scoped to this one call so the
        // guard stays measured — under the headless lifetime it is false, which is
        // what keeps this harmless, and that is worth asserting rather than
        // excluding along with it.
        [ExcludeFromCodeCoverage]
        private static void Shutdown(IClassicDesktopStyleApplicationLifetime desktop) =>
            desktop.Shutdown();
    }
}
