using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Avalonia.Threading;

namespace Orbweaver
{
    // The app's first persistent state.
    //
    // Everything else here is deliberately transient — Claude Code sessions come
    // from status files in the temp directory, and Claude Desktop profiles are
    // whatever directories exist on disk. That stays true: this file only holds
    // *preferences*, never a copy of discovered state. Delete it and the app still
    // works, with derived colours and folder names.
    //
    // Profiles are keyed by folder name rather than by path, so moving the
    // profile root (the ORBWEAVER_PROFILE_ROOT override) keeps your settings,
    // and renaming a folder deliberately starts fresh.
    internal static class OrbweaverSettings
    {
        private const int CurrentVersion = 1;

        // OpenClaw's own default. Only worth changing for a gateway that was
        // moved off it.
        public const int DefaultOpenClawPort = 18789;

        // An hour of history by default. Long enough to cover the conversation
        // you were just having, short enough that a gateway holding a year of
        // Discord channels doesn't fill the screen.
        public const int DefaultOpenClawActiveWithin = 60;
        public const int OpenClawActiveWithinAll = 0;

        // Ten minutes of idle before the Remote Control bridge is shut down.
        // Long enough to read a reply and type another message without paying
        // to start over, short enough that walking away doesn't leave a live
        // session on the user's account all afternoon.
        public const int DefaultRemoteControlIdle = 10;
        public const int RemoteControlIdleNever = 0;

        // The account whose sessions the bridge can see, when the user hasn't
        // said otherwise — the same default config directory the CLI itself uses.
        public const string DefaultRemoteControlProfileDir = ".claude";

        private static readonly object Gate = new();
        private static Model _model = new();
        private static bool _loaded;

        // Keys found in settings.json that this build knows nothing about, kept
        // verbatim so Save can put them back.
        //
        // Save rebuilds the whole document from the model rather than editing the
        // file in place, which means any key it doesn't write is deleted. That is
        // fine while there is only ever one version of the app — and quietly
        // destructive the moment there isn't. Observed exactly that way: an
        // installed build three commits behind was launched for a couple of
        // minutes, saved once for an unrelated reason, and silently erased
        // speakCommand, neuralVoiceEnabled and neuralVoice from a file it had no
        // idea contained them. The visible symptom was speech coming out in the
        // default system voice with no explanation.
        //
        // It costs a user nothing to be wrong about this and quite a lot to
        // discover it: downgrading, running an old copy once, or testing a build
        // from bin/ beside an installed one all do it. Round-tripping the unknown
        // keys makes an older version leave newer settings alone instead.
        private static readonly Dictionary<string, JsonNode?> _unknownKeys =
            new(StringComparer.Ordinal);

        // Every key Save writes. Kept next to _unknownKeys rather than derived
        // from the JsonObject Save builds, because it has to be consulted during
        // Load, before that object exists. Adding a setting means adding it here
        // too — miss it and the key round-trips through _unknownKeys as well as
        // being written properly, which JsonObject rejects as a duplicate.
        private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
        {
            "version", "showOrbs", "showUsageOrbs", "tintActiveWindow", "orbLifetimeMinutes",
            "voiceInputEnabled", "twoLetterGlyphs", "accountUsageEnabled",
            "arrangeShape", "arrangeSpacing",
            "speakVoice", "neuralVoiceEnabled", "neuralVoice",
            "speakCommand", "speakCommandArgs",
            "speakVoicesCommand", "speakVoicesCommandArgs", "speakCommandVoice", "speakEngine",
            "speakScope",
            "muted", "turnSoundsEnabled", "turnFinishedSound", "needsAttentionSound", "orbTurnSounds",
            "orbColors", "claudeCodeProfileDirs", "codexHomes", "grokHomes", "profiles", "orbPositions",
            "collapsedSettingsSections",
            "chatPanelSizes", "pinnedChatPanels", "arrangeAnchor", "chatTextScale",
            "orbSize", "orbSizes", "orbMemberOffsets", "accountOrbOverrides",
            "speechVolume", "alertVolume",
            "openclawEnabled", "openclawHost", "openclawPort", "openclawFingerprint",
            "openclawReplyEnabled", "openclawApproveEnabled", "openclawActiveWithinMinutes",
            "claudeCloudEnabled",
            // Still written, though nothing reads it into the model any more —
            // see OpenClawHeartbeatMode. Listed here so it does not also
            // round-trip through _unknownKeys, which Save would reject as a
            // duplicate key.
            "openclawShowHeartbeats",
            "openclawHeartbeatMode", "openclawHeartbeatShape",
            "openclawCronMode", "openclawCronShape",
            "openclawNativeMode", "openclawNativeShape",
            // CB-268. The legacy shape and anchor keys above stay written next
            // to orbGroups, so an older build still reads the file.
            "orbGroups", "groupOutlinesEnabled", "groupNamesVisible", "groupOutlineStroke",
            "groupOutlineStrokeOpacity", "groupOutlineFill", "groupOutlineFillOpacity",
            "codexChatEnabled", "codexReplyEnabled", "codexAccountUsageEnabled", "autoColorSessions",
            "claudeCodeEnabled", "codexEnabled",
            "grokChatEnabled", "grokReplyEnabled", "grokEnabled", "grokAccountUsageEnabled",
            "grokAutoRefreshEnabled", "peerInboundNoticeSeen",
            "clickAction", "doubleClickAction", "tripleClickAction",
            "personaPoolDir",
            "remoteControlEnabled", "remoteControlProfileDir", "remoteControlProfileDirs",
            "remoteControlIdleMinutes", "remoteControlServeOnLaunch",
            "peerLinkEnabled", "peerLinkPort",
            "newChatRecentFolders", "newChatLastCli", "newChatLastProfile",
            "newChatLastProfiles",
            // Every hotkey override. toggleOrbsHotkey was missing from this
            // list from CB-155 until the new-chat hotkey was added beside it;
            // Save's ContainsKey guard kept that from throwing, but it meant
            // the value was carried in _unknownKeys as well as the model.
            "toggleOrbsHotkey", "newChatHotkey", "toggleUsageOrbsHotkey", "toggleMuteHotkey"
        };

        // JsonNode.ToJsonString(options) needs a TypeInfoResolver on the
        // options it's given — it doesn't fall back to
        // JsonSerializerOptions.Default's own resolver just because that one
        // has one. In a normal `dotnet build` this project's own
        // reflection-based JsonSerializerOptions() default silently covers
        // that gap, but SelfContained + PublishSingleFile (this app's actual
        // shipped Windows build) implicitly trims, which turns the missing
        // resolver into a hard InvalidOperationException on every single
        // Save() call. Confirmed by publishing a minimal repro with this
        // project's exact trimming settings and running it for real: it
        // threw with a bare `new JsonSerializerOptions { WriteIndented =
        // true }, and stopped throwing only once TypeInfoResolver was set
        // explicitly like this — the values here are plain bool/string
        // primitives, so reflection over them is always safe regardless of
        // trimming. This is why settings.json silently never got written on
        // a real machine while every local build/run looked fine.
        private static readonly JsonSerializerOptions SaveOptions = new()
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        // Excluded from coverage: reads the real user profile directory. Every
        // test in this repo runs with ORBWEAVER_SETTINGS_DIR pointed elsewhere,
        // which is the whole point — a suite that read this would be reading, and
        // on a bad day writing, the developer's own settings.json.
        [ExcludeFromCodeCoverage]
        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // %APPDATA%\Orbweaver on Windows, ~/Library/Application Support/Orbweaver
        // on macOS. SpecialFolder.ApplicationData resolves to both, so this is one
        // expression rather than a platform branch.
        //
        // Test seam, same pattern as ORBWEAVER_PROFILE_ROOT
        // (ClaudeDesktopManager.cs): without it, a test that so much as reads a
        // setting touches the developer's real settings.json, and a test that
        // writes one touches it for good — settings.json does not follow HOME on
        // macOS, so there is no per-test-run isolation otherwise. Read through
        // BrandEnv, so the pre-rename spelling still redirects it.
        public static string Directory =>
            BrandEnv.Get(BrandEnv.SettingsDir) is { } scratch
                ? scratch
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Brand.DataDirName);

        public static string Path_ => Path.Combine(Directory, "settings.json");

        // ---- shape ----------------------------------------------------------

        // Where a dragged orb sits, in the same coordinate space as
        // Window.Position: physical pixels on the virtual desktop. Storing DIPs
        // instead would survive a scaling change, but there's no coherent
        // desktop-wide DIP space on a mixed-DPI setup, so raw pixels it is —
        // SessionManager clamps a restored point back onto a real screen.
        //
        // Size (CB-198) is the orb's user size when the spot was saved, and only
        // orb positions ever set it. The point is a top-left corner, so the same
        // spot restored at a different size is off by half the size difference
        // unless the restore knows what size it was saved at — see
        // SessionManager.RestoredTopLeft. Null means "saved before orbs had a
        // size", which is 1.0. The chat-panel and arrange-anchor uses of this
        // record leave it null and never write it.
        internal sealed record OrbPlacement(int X, int Y, double? Size = null);

        // CB-268 T2: where a team member sits relative to its lead inside a laid-out
        // group, CENTRE TO CENTRE: the member's visible centre minus the lead's visible
        // centre, in DIP (so it survives a display-scale change; S7 computes it). Never
        // top-left to top-left: members are drawn at MemberScale and a lead can carry its
        // own size override, so a corner-to-corner offset would change meaning whenever
        // either orb's size changed. See OrbMemberOffsets on the model.
        internal sealed record MemberOffset(double Dx, double Dy);

        // How big one agent's chat panel is, in DIPs. Doubles rather than the
        // ints above because this is a layout size and not a screen coordinate:
        // a resize drag lands on fractions, and the panel's own Min/MaxWidth are
        // doubles it gets clamped against.
        internal sealed record PanelSize(double Width, double Height);

        // One orb's override of the two turn-sound defaults, keyed the same
        // way OrbPositions and ChatPanelSizes are — see SessionManager's
        // SoundKeyFor. Either field null means "use the global default for
        // that trigger", the same reading null gets everywhere else in this
        // file; both null is not a value worth keeping an entry for, which is
        // why the accessors below remove the whole entry rather than storing
        // it. A field holding "off" is a real, distinct choice from null —
        // the override turns that trigger off for this orb specifically,
        // which is different from the orb simply not overriding it.
        internal sealed record OrbTurnSound(string? Finished, string? Attention);

        internal sealed class ProfileSettings
        {
            public string? Name { get; set; }          // display name; null = folder name
            public string? Color { get; set; }         // palette name; null = derived from folder
            public bool ShowSwatch { get; set; } = true;
            public bool TintDockIcon { get; set; } = true;
            public bool TintWindow { get; set; } = true;
        }

        // How long an orb outlives its session's last hook write, in minutes,
        // with 0 meaning "never prune". Five matches what the app did when this
        // was a hard-coded constant.
        public const int DefaultOrbLifetimeMinutes = 5;
        public const int OrbLifetimeForever = 0;

        public const string DefaultArrangeShape = "heart";
        public const double DefaultArrangeSpacing = 0.85;

        // The shapes the timer-driven groups arrange into when the user gives
        // them one of their own. Neither is "heart", deliberately: the point of
        // a separate shape is to be able to tell at a glance which shape you are
        // looking at, and a second heart beside the first defeats that on the
        // one screen it matters most.
        //
        // A circle for heartbeats because it is the plainest of the six and
        // these are the orbs nobody is talking to; a line for crons because a
        // schedule is the one thing on screen that really is a sequence.
        public const string DefaultOpenClawHeartbeatShape = "circle";
        public const string DefaultOpenClawCronShape = "line";

        // CB-270: a diamond for an agent's own sessions, the only one of the
        // seven shapes no other group uses by default.
        public const string DefaultOpenClawNativeShape = "diamond";

        // CB-268's global outline style. Outlines are off by default; these are
        // what a group inherits for any field it does not override.
        public const string DefaultGroupOutlineStroke = "#FFFFFF";
        public const double DefaultGroupOutlineStrokeOpacity = 0.35;
        public const string DefaultGroupOutlineFill = "#FFFFFF";
        public const double DefaultGroupOutlineFillOpacity = 0.06;

        private sealed class Model
        {
            public bool ShowOrbs { get; set; } = true;

            // Whether the account (usage) orbs are shown, apart from the
            // session orbs (CB-220). Null until someone has set it, and null
            // means "whatever ShowOrbs says": a user upgrading with every orb
            // hidden must not have the usage orbs reappear just because they
            // grew a switch of their own. Written back as null, not as a copy,
            // so that stays true until one of the two switches is flipped.
            public bool? ShowUsageOrbs { get; set; }

            public bool TintActiveWindow { get; set; } = true;

            // On by default, because with more than one profile the alternative
            // is a sign-in that silently lands in the wrong account — see
            // ClaudeDesktopUrlRouting. Nothing is claimed until there actually
            // is more than one profile, so a single-profile install is
            // untouched whatever this says.
            public bool RouteClaudeUrls { get; set; } = true;

            // The bundle id that owned `claude:` before we claimed it, so
            // turning routing off can put it back rather than leaving the user
            // with a scheme pointing at whatever we happened to set.
            public string PreviousClaudeUrlHandler { get; set; } = "";
            public int OrbLifetimeMinutes { get; set; } = DefaultOrbLifetimeMinutes;

            // Off by default: one letter is what every existing orb already
            // looks like, and changing that for everyone on upgrade would be
            // a cosmetic surprise nobody asked for.
            public bool TwoLetterGlyphs { get; set; }

            // Off by default. The orbs are useful but they are also four more
            // things on a desktop that already has orbs on it, and the poll
            // behind them starts a `claude` process per account every five
            // minutes — neither is something to begin doing to someone who has
            // not asked.
            public bool AccountUsageEnabled { get; set; }

            // Off by default: turning this on is what triggers the one-time
            // Whisper model download (a few hundred MB), so it must be an
            // explicit opt-in rather than something a fresh install just has.
            public bool VoiceInputEnabled { get; set; }

            // Which macOS `say` / Windows SAPI voice to use for speaking the
            // latest turn. Null means the default ("Samantha" on macOS).
            public string? SpeakVoice { get; set; }

            // Off by default, same reasoning as VoiceInputEnabled: turning it on
            // is what fetches the neural speech engine and its model (~300MB
            // together), so it has to be something the user just asked for.
            // Windows-only in practice — see NeuralSpeech, and note that macOS's
            // own Enhanced and Premium voices are already better than this.
            public bool NeuralVoiceEnabled { get; set; }

            // Off by default, and for a stronger reason than the voice features
            // above: an app that reaches out to a machine on the network is not
            // something a Claude Code tool should start doing because it was
            // upgraded. Most installs will never point at an OpenClaw gateway,
            // and while this is off nothing here is constructed, no socket is
            // opened, and no key is generated — the only trace of the feature is
            // the settings row that turns it on. Same discipline as
            // VoiceInputEnabled and the mic permission prompt.
            public bool OpenClawEnabled { get; set; }

            // Off by default, and it must stay that way: switching it on is what
            // asks the OS for the Claude Code login, and nobody should meet a
            // Keychain prompt they did not ask for.
            public bool ClaudeCloudEnabled { get; set; }

            // Where the gateway lives. An address rather than a name on purpose:
            // the certificate it serves is self-signed with no subjectAltName,
            // so a hostname buys nothing and pinning does the identity work.
            public string? OpenClawHost { get; set; }

            public int OpenClawPort { get; set; } = DefaultOpenClawPort;

            // The certificate fingerprint this install has agreed to trust,
            // recorded the first time it connects. Empty means "trust whatever
            // is presented next and remember it"; once set, a different
            // certificate is refused rather than silently accepted.
            public string? OpenClawFingerprint { get; set; }

            // Off by default, and separately from OpenClawEnabled on purpose:
            // showing what your agents are doing and being able to make them do
            // things are different powers, and the second one should be asked
            // for. Turning it on widens the scopes this device requests, which
            // the gateway treats as a new pairing to approve.
            public bool OpenClawReplyEnabled { get; set; }

            // CB-270 Stage 6: a third, separate power. Allowing replies grants
            // operator.write; this grants operator.approvals, which lets the app
            // see what the gateway is holding for a person's say-so and answer it
            // allow-once or deny. Off by default, independent of replying, and
            // turning it on widens the scopes this device requests, which the
            // gateway treats as a new pairing to approve on its own host.
            public bool OpenClawApproveEnabled { get; set; }

            // The chat panel on a local Claude Code orb. On by default, unlike
            // everything else here that is off: it opens no socket, starts no
            // engine and asks for no permission — it reads a file the hook
            // already tells us about, and only while a panel is up.
            public bool ClaudeCodeChatEnabled { get; set; } = true;

            // Typing into that session from the panel. Off by default, and the
            // same split as OpenClawReplyEnabled for the same reason: watching a
            // session work and being able to drive it are different powers. The
            // second one also covers answering permission prompts and
            // interrupting a run, which are the two places a wrong click costs
            // most.
            public bool ClaudeCodeReplyEnabled { get; set; }

            // CB-267: the one-time card in Settings that explains why Claude Code
            // holds messages sent from an orb. Set once the card has been acted
            // on or dismissed, and never cleared by the app.
            public bool PeerInboundNoticeSeen { get; set; }

            // Codex's own pair. Separate keys rather than one shared "local CLI"
            // switch, because seeing and controlling are separate powers per CLI
            // as well: someone can reasonably want to read a Codex session and
            // never type into it while doing the opposite for Claude Code.
            public bool CodexChatEnabled { get; set; } = true;

            public bool CodexReplyEnabled { get; set; }

            public bool CodexAccountUsageEnabled { get; set; }

            // Whether the hook gives a Claude Code session a colour of its own
            // when it has none. Off by default: it is the only setting in this
            // file that causes a write to a file the app does not own, even
            // though what it writes is the record /color writes.
            public bool AutoColorSessions { get; set; }

            // Whether a CLI is tracked at all. Both default on, because both
            // are only ever visible if the user wired their hooks — which is
            // itself the opt-in. Off means the app ignores that CLI's status
            // files rather than unwiring anything: the hooks are the user's own
            // config, and a display switch that silently rewrote it would be a
            // surprise, and for Codex would cost them their hook trust as well.
            public bool ClaudeCodeEnabled { get; set; } = true;

            public bool CodexEnabled { get; set; } = true;

            // Grok Build's own pair, separate from Codex's for the same reason
            // Codex is separate from Claude Code: seeing and controlling are
            // per-CLI powers. Usage orbs have their own switch too, because
            // someone can want Claude's rings and not Grok's.
            public bool GrokChatEnabled { get; set; } = true;

            public bool GrokReplyEnabled { get; set; }

            public bool GrokEnabled { get; set; } = true;

            public bool GrokAccountUsageEnabled { get; set; }

            // Off even when GrokAccountUsageEnabled is on. Reading a log file
            // and starting the user's real terminal app in the background are
            // different classes of action — see GrokUsageRefresher's own
            // comment for why booting Grok is the only way to get a fresh
            // number, and why that is not something to do without asking.
            public bool GrokAutoRefreshEnabled { get; set; }

            // What clicking an orb does, per number of clicks. One of
            // "terminal", "chat", "speak" or "none" — strings rather than an
            // enum because they go through the same JSON round trip every other
            // setting does, and an unknown value has to degrade rather than
            // throw.
            //
            // The defaults are what the app did before any of this existed: one
            // click goes to the session, and nothing is bound to two or three.
            // That matters beyond taste — see OrbWindow's gesture handling. A
            // second gesture bound to something different forces the first to
            // wait and see whether another click is coming, so leaving these
            // empty is what keeps a single click instant for anyone who never
            // opens this part of the settings.
            public string ClickAction { get; set; } = "terminal";

            public string DoubleClickAction { get; set; } = "none";

            public string TripleClickAction { get; set; } = "none";

            // How far back a gateway session counts as current. Separate from
            // OrbLifetimeMinutes, which decides how long a session lingers after
            // it goes quiet — this decides which of a gateway's many
            // conversations are candidates at all. Zero means all of them.
            public int OpenClawActiveWithinMinutes { get; set; } = DefaultOpenClawActiveWithin;

            // What the sessions the gateway's heartbeat drives do — no orb, an
            // orb in the same shape as everything else, or an orb in a shape of
            // their own. See OpenClawHeartbeat for which sessions those are and
            // OrbClusters for what the three modes mean.
            //
            // WithChats by default, which is deliberately the *noisier* choice
            // and the one the app has always had: these orbs are on screen
            // today, and an upgrade that quietly removed several of somebody's
            // agents would read as the gateway having dropped them rather than
            // as a new setting having a default. The heart badge is what makes
            // the noise explainable, and this is the setting for anyone who,
            // having had it explained, wants them gone or wants them elsewhere.
            //
            // Replaces the `openclawShowHeartbeats` bool, which could only say
            // the first and second of the three. Load migrates a saved one; Save
            // keeps writing it, so a downgrade still hides what the user hid.
            public ClusterMode OpenClawHeartbeatMode { get; set; } = ClusterMode.WithChats;

            // CB-268: these four are no longer fields of their own. They are
            // views over the predefined groups in Groups below, so every caller
            // that read or wrote them keeps working and the group table stays
            // the one place a shape lives. See OrbGroupTable.ReadJson for which
            // copy wins when the file holds both.
            public string OpenClawHeartbeatShape
            {
                get => Groups.Get(OrbGroups.Heartbeats)!.Shape;
                set => Groups.SetShape(OrbGroups.Heartbeats, value);
            }

            // The same three answers for scheduled jobs, which until now had no
            // setting at all — they always got an orb and it always joined the
            // one shape. See OpenClawSessionKind, which reads a cron off the
            // session key's third segment.
            public ClusterMode OpenClawCronMode { get; set; } = ClusterMode.WithChats;

            public string OpenClawCronShape
            {
                get => Groups.Get(OrbGroups.Crons)!.Shape;
                set => Groups.SetShape(OrbGroups.Crons, value);
            }

            // CB-270: the same three answers for an agent's own sessions — its
            // main session and chats started from Orbweaver. See OpenClawNative.
            // WithChats by default, so nothing moves for anyone who does not
            // touch it.
            public ClusterMode OpenClawNativeMode { get; set; } = ClusterMode.WithChats;

            public string OpenClawNativeShape
            {
                get => Groups.Get(OrbGroups.Native)!.Shape;
                set => Groups.SetShape(OrbGroups.Native, value);
            }

            // Whether to show Claude Code sessions running on *other* machines,
            // reached through a hidden local bridge session that has Remote
            // Control on. Off by default, and deliberately more than a display
            // switch: turning it on is what permits Buddy to start a real
            // Claude Code session of its own, which costs the user's quota.
            // The bridge was deleted in 937de9ec; the direct link below is the
            // only way remote sessions arrive now.
            public bool RemoteControlEnabled { get; set; }

            // Talking directly to another machine running Orbweaver, rather
            // than through a hidden Claude Code session relaying text.
            //
            // Off by default, and that is not timidity: switching it on makes
            // this app listen on a socket for the first time, which both
            // platforms gate — macOS behind Local Network consent, Windows
            // behind the firewall. A feature that quietly opened a port on first
            // launch would be a worse neighbour than one the user turns on.
            //
            // Unlike RemoteControlEnabled this costs nothing to leave on: there
            // is no model in the path and no quota to spend, which is the whole
            // point of it.
            public bool PeerLinkEnabled { get; set; }

            // Overrides HotkeyRegistry.Default(ToggleOrbsVisible) — e.g.
            // "Ctrl+Shift+H". Null (the default) means "use the built-in
            // binding"; a value that fails HotkeyRegistry.TryParse is treated
            // the same way rather than leaving the hotkey unregistered, since
            // a typo in a hand-edited settings.json shouldn't cost the whole
            // feature. No settings-window control for this yet — CB-155 didn't
            // confirm a remapping UI was wanted, only that the value be
            // overridable, and settings.json already is.
            public string? ToggleOrbsHotkey { get; set; }

            // Overrides HotkeyRegistry.Default(OpenNewChat), on exactly the
            // same terms as ToggleOrbsHotkey above: null or unparseable means
            // the built-in Ctrl+Alt+N, and settings.json is the only place to
            // set it, because the toggle's override has no settings-window
            // control either.
            public string? NewChatHotkey { get; set; }

            // Overrides HotkeyRegistry.Default(ToggleUsageOrbsVisible), on the
            // same terms again: null or unparseable means Ctrl+Alt+U.
            public string? ToggleUsageOrbsHotkey { get; set; }

            // Overrides HotkeyRegistry.Default(ToggleMute), on the same
            // terms again: null or unparseable means Ctrl+Alt+M.
            public string? ToggleMuteHotkey { get; set; }

            // Which port to listen on. Zero means "let the operating system
            // choose", which is the sensible default because discovery
            // announces whatever was chosen — a fixed port only matters to
            // somebody adding a machine by address through a firewall they
            // control.
            public int PeerLinkPort { get; set; }

            // Which CLI config directory — and therefore which Anthropic
            // account — the bridge runs under. A home-relative name, the same
            // vocabulary ClaudeCodeProfileDirs already uses (".claude",
            // ".claude-work").
            //
            // It matters because Remote Control is account-scoped: the bridge
            // can only see sessions belonging to whichever account it logs in
            // as, so a user whose remote machines are on a second account has to
            // be able to say so.
            // Where the user's shared persona pool lives when ORBWEAVER_PERSONAS is
            // not set (PersonaPool). Null when never chosen, so the default
            // under the home directory is the answer rather than a copy of it.
            public string? PersonaPoolDir { get; set; }

            public string? RemoteControlProfileDir { get; set; }

            // Every account to run a relay for.
            //
            // CB-300: still read, as one part of a union, by the peer link
            // (PeerRosterDirs) — no screen writes it any more, but whatever it
            // holds is still offered, so nothing shared before drops out. A
            // profile added in Settings → Claude Code is offered by the same
            // union through ClaudeCodeProfileDirs.
            //
            // Replaces the single RemoteControlProfileDir above, which is kept
            // only so an existing setting isn't silently dropped — see the
            // RemoteControlProfileDirs accessor, which reads the old key when
            // this list is empty. Remote Control is account-scoped, so two
            // accounts genuinely need two relays; nothing about one relay can be
            // stretched to see both.
            public List<string> RemoteControlProfileDirs { get; init; } = new();

            // How long the bridge may sit unused before it is shut down.
            //
            // The bridge is not free — it is a live Claude Code session on the
            // user's own account — so it is started on demand and does not
            // linger. Zero means never idle-stop, which is honest but expensive
            // and is not the default.
            public int RemoteControlIdleMinutes { get; set; } = DefaultRemoteControlIdle;

            // Ask the relay up when the app starts, instead of waiting for a
            // hand on this machine. Exists for one shape of machine: a headless
            // one whose sessions other Buddies mirror — nobody is there to
            // press "Connect to other machines". Off by default for the same
            // reason the idle stop above exists: a relay is a live session
            // spending the user's quota, so keeping one has to be chosen.
            public bool RemoteControlServeOnLaunch { get; set; }

            // A command of the user's own to speak with, replacing every built-in
            // engine. Null means "use the built-in ones".
            //
            // This is the extension point for any voice or engine this app will
            // never ship: the contract is deliberately the same one TextToSpeech
            // already lives by, because speaking here has always been "a child
            // process is running" and stopping it "kill that process". So the
            // whole interface is: text arrives on stdin as UTF-8, the process
            // exits when it has finished speaking, and being killed means stop.
            // Nothing to implement beyond reading stdin.
            //
            // Whatever is on the other end is the user's business — a Piper
            // wrapper, a voice-conversion chain, a Python script, a cloud API, a
            // batch file. The app makes no assumption about it and reports its
            // failures rather than hiding them. Same posture the project already
            // takes with OrbweaverHook.ps1, which is a user-editable script in
            // %APPDATA% wired into Claude Code by hand.
            public string? SpeakCommand { get; set; }

            // Arguments for it, kept as a list rather than one string so nobody
            // has to guess this app's quoting rules for a path with spaces —
            // they are passed through ArgumentList, which quotes each one
            // correctly by construction.
            public List<string> SpeakCommandArgs { get; init; } = new();

            // Optional companion to SpeakCommand: a command that prints the voice
            // names it can speak with, one per line, so they can be offered in the
            // settings window like any other voice.
            //
            // Needed because the app cannot know what an arbitrary program
            // supports — SpeakCommand is opaque by design. Without this the only
            // honest thing the picker can say is "the command decides", and the
            // chosen voice has to be configured inside the command instead.
            //
            // The selected name reaches SpeakCommand through the CLAUDEBUDDY_VOICE
            // environment variable rather than an appended argument: SpeakCommandArgs
            // belongs to the user, and injecting a positional argument would break
            // any wrapper that takes fixed ones. A command that ignores the variable
            // is unaffected.
            public string? SpeakVoicesCommand { get; set; }

            // Its own arguments, not shared with SpeakCommandArgs. One script
            // usually serves both roles by branching on an argument — ours takes
            // "--list-voices" — and sharing one list would mean handing that same
            // flag to the speaking invocation, which would then list voices
            // instead of talking. Two lists is the difference between "one script,
            // two modes" working and quietly doing the wrong thing.
            public List<string> SpeakVoicesCommandArgs { get; init; } = new();

            // Which of the three engines the selected voice belongs to: "system",
            // "neural" or "custom". The voice itself lives in that engine's own key
            // below, so switching engines and back remembers what was chosen in
            // each.
            //
            // Exists because the engine used to be decided by precedence —
            // configuring a command silently took the neural and system voices out
            // of play, and the settings picker could only ever show one engine's
            // worth of what the machine could do. Making the choice explicit is what
            // lets all three sit in one list.
            public string? SpeakEngine { get; set; }

            // What the speaker reads: the whole reply, or two or three
            // sentences of it. A string rather than a bool because "full" and
            // "summary" are named modes a settings file should say out loud,
            // and because a third is easy to imagine and a bool would have to
            // be replaced rather than extended.
            public string? SpeakScope { get; set; }

            // The master switch for CB-167's turn sounds, on by default: a
            // sound that plays only after the user has found and flipped a
            // setting would miss the entire point of an ambient cue, which is
            // to tell you something before you go looking.
            public bool TurnSoundsEnabled { get; set; } = true;

            // CB-298: the global mute. One flag that silences every sound
            // the app makes, kept apart from the two volumes and the turn
            // sounds switch on purpose — unmuting must put back exactly what
            // was there, and a mute that rewrote those would lose them to a
            // crash between mute and unmute. Off on a fresh install.
            public bool Muted { get; set; }

            // null = the platform default (Glass / Windows Notify Messaging),
            // "off", "summary", a system-sound name, or an absolute path. See
            // SystemSoundCatalog.Resolve and TurnSoundPolicy for how each of
            // those is turned into an actual sound.
            public string? TurnFinishedSound { get; set; }

            // Same shape as TurnFinishedSound, minus "summary" — a vibe
            // summary is offered for a turn finishing, never for a permission
            // prompt, so this field has no value that means it. A hand-edited
            // file that sets one anyway is not specially rejected; it simply
            // fails to resolve to a sound the same way a deleted file would.
            public string? NeedsAttentionSound { get; set; }

            // Which of those names is selected. A fourth voice key rather than
            // reusing SpeakVoice or NeuralVoice for the same reason those two are
            // separate: the name spaces have nothing in common, and a value left
            // behind in another engine's key is a name that engine rejects.
            public string? SpeakCommandVoice { get; set; }

            // Which neural voice ("af_heart"). Kept separate from SpeakVoice
            // rather than sharing one key, because the two name spaces have
            // nothing in common: leaving "af_heart" behind in the field SAPI
            // reads would hand SelectVoice a name it throws on, which is exactly
            // the failure the comment at the top of TextToSpeech describes
            // shipping once already. Two keys means either engine's choice
            // survives a round trip through the other.
            public string? NeuralVoice { get; set; }

            // "#RRGGBB", or null for "use the built-in colour". Null rather than
            // a copy of the default so that retuning a shipped colour later still
            // reaches everyone who never touched it — see the properties below.
            public string? IdleColor { get; set; }
            public string? GeneratingColor { get; set; }
            public string? WaitingColor { get; set; }

            public Dictionary<string, ProfileSettings> Profiles { get; init; } =
                new(StringComparer.Ordinal);

            // Keyed by the session's cwd — see SessionManager.PositionKeyFor.
            // Case-insensitive because Windows paths are, and the same repo
            // shouldn't get two entries over a capitalization difference.
            public Dictionary<string, OrbPlacement> OrbPositions { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);

            // Keyed exactly the way OrbPositions above is — by the orb's
            // position key, which is the closest thing this app has to "which
            // agent is this" across runs. A session id would have been the
            // obvious key and is the wrong one: Claude Code mints a new one
            // every conversation, so a size saved under it would never be found
            // again. Sharing the key with the orb's own saved place also means
            // an agent's panel and its orb agree about what counts as the same
            // agent, rather than drifting apart on a retitle.
            public Dictionary<string, PanelSize> ChatPanelSizes { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);

            // Per-orb overrides of the two turn-sound defaults, keyed by
            // SessionManager.SoundKeyFor exactly the way ChatPanelSizes above
            // is keyed by PositionKeyFor — case-insensitive for the same
            // Windows-path reason. An orb with no entry here uses the global
            // default for both triggers.
            public Dictionary<string, OrbTurnSound> OrbTurnSounds { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);

            // CB-287: per-usage-orb overrides of initials, display name and icon,
            // keyed by AccountOrbs.PositionKey exactly as OrbPositions is keyed
            // by position — case-insensitive for the same Windows-path reason.
            public Dictionary<string, AccountOrbOverride> AccountOrbOverrides { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);

            // CB-198's per-orb size override. Borrows OrbTurnSounds' key —
            // SessionManager.SoundKeyFor, the per-agent identity CB-167 built
            // precisely so teammates sharing one cwd are told apart — rather
            // than PositionKeyFor, which cannot tell them apart and would size
            // every member of a team together. Case-insensitive for the same
            // Windows-path reason. No entry means the orb follows the global
            // OrbSize slider.
            public Dictionary<string, double> OrbSizes { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);

            // CB-268 T2: a member dragged inside a laid-out group keeps its offset
            // from its lead, and ComputeGroups places it at lead + offset instead
            // of fanning it out. Keyed by SoundKey, like OrbSizes and for the same
            // reason: teammates sharing one cwd share a PositionKey, so keying by
            // it would move every member of a team together. No entry means the
            // member takes the fan.
            public Dictionary<string, MemberOffset> OrbMemberOffsets { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);

            // CB-111: which chat panels were pinned when the app last quit,
            // and where each one was — the two things CB-110 deliberately did
            // not persist, because a pin then died with the process. Presence
            // in this dictionary *is* "pinned"; there is no separate bool,
            // the same way a size is only ever recorded for a panel that was
            // actually resized. Keyed by PositionKey rather than session id
            // for the identical reason ChatPanelSizes is — see its own
            // comment — and stored as an OrbPlacement rather than a new
            // record because a pinned panel's saved spot is exactly the same
            // shape of value an orb's is: physical pixels on the virtual
            // desktop, clamped back onto a real screen by whoever restores
            // it rather than by this file.
            public Dictionary<string, OrbPlacement> PinnedChatPanels { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);

            // Distinct from Profiles above (Claude Desktop, the Electron app):
            // these are Claude Code *CLI* config directory names — e.g.
            // ".claude-work" for a CLAUDE_CONFIG_DIR=~/.claude-work alias
            // managing a second account — that the user has explicitly opted
            // into also wiring Orbweaver hooks for, beyond the default
            // ~/.claude. install-windows-hooks.ps1 reads this same list (via
            // this file) as its default -ProfileDir/-WslProfileDir value, so
            // configuring it here is also what "configure via the installer"
            // means in practice: a repair/reinstall re-reads whatever's saved
            // here rather than needing its own separate wizard UI for it.
            public List<string> ClaudeCodeProfileDirs { get; init; } = new();

            // Section ids the user has folded shut, keyed the same way Group()
            // keys _sections in SettingsWindow — a stable id, not the heading
            // text, so renaming a section's prose can't reset its fold state.
            // Absent or empty means every section is open, which is also the
            // default a fresh install has nothing to migrate away from.
            public List<string> CollapsedSettingsSections { get; init; } = new();

            // The Codex analogue: directory names under $HOME that a second
            // account is run out of via CODEX_HOME. Separate from the list
            // above because they are separate products with separate configs,
            // and someone can easily have extras of one and not the other.
            public List<string> CodexHomes { get; init; } = new();

            // GROK_HOME extra accounts, the same shape as CodexHomes.
            public List<string> GrokHomes { get; init; } = new();

            // Folders the "New chat…" dialog has launched into before, most
            // recent first. Capped and de-duplicated by RecentFolders.Merge
            // before it ever reaches here — this list is just what Save
            // writes back out, the same relationship ClaudeCodeProfileDirs
            // has to the installer's own trimming.
            public List<string> NewChatRecentFolders { get; init; } = new();

            // The CLI last chosen in the "New chat…" dialog, so reopening it
            // starts on what was used last rather than always defaulting to
            // the first radio option. Null means "never chosen" — the dialog
            // picks its own first-available default in that case, the same
            // way SpeakVoice being null means "use the platform default"
            // rather than a stored copy of it.
            public string? NewChatLastCli { get; set; }

            // The account (a ClaudeCodeProfileDirs entry) last chosen in the
            // "New chat…" dialog's Account picker (CB-201). Null means either
            // "never chosen" or "Default was chosen" — both read the same way
            // downstream, since Default is what the dialog falls back to
            // anyway when nothing else applies.
            //
            // Per CLI since CB-203, keyed by NewChatCli's name: the three
            // account lists name different kinds of directory, so a Codex pick
            // restored onto Claude Code's picker would match nothing at best,
            // and a same-named directory would start the wrong CLI under it at
            // worst. Saved as "newChatLastProfiles"; the pre-CB-203 single key
            // "newChatLastProfile" is read as the Claude Code entry when the
            // map has none, and is still written from it, so neither an upgrade
            // nor a downgrade loses a saved pick.
            public Dictionary<string, string> NewChatLastProfiles { get; init; } = new(StringComparer.Ordinal);

            // Auto-organize: which shape and how much space between orbs.
            public string ArrangeShape
            {
                get => Groups.Get(OrbGroups.Main)!.Shape;
                set => Groups.SetShape(OrbGroups.Main, value);
            }

            // CB-268: every group, and which orb is in which. Built with the
            // three predefined groups at their default shapes, so a file that
            // predates groups migrates just by being read: the legacy keys set
            // those shapes through the properties here.
            public OrbGroupTable Groups { get; } = new();

            // The global outline style a group inherits field by field, and
            // whether outlines are drawn at all. Off by default so today's look
            // is unchanged. Colours are #RRGGBB strings, kept as text so this
            // file stays free of Avalonia types.
            public bool GroupOutlinesEnabled { get; set; }
            public bool GroupNamesVisible { get; set; }
            public string GroupOutlineStroke { get; set; } = DefaultGroupOutlineStroke;
            public double GroupOutlineStrokeOpacity { get; set; } = DefaultGroupOutlineStrokeOpacity;
            public string GroupOutlineFill { get; set; } = DefaultGroupOutlineFill;
            public double GroupOutlineFillOpacity { get; set; } = DefaultGroupOutlineFillOpacity;
            public double ArrangeSpacing { get; set; } = DefaultArrangeSpacing;

            // How much bigger or smaller than shipped the chat panel draws its
            // text. A multiplier, not a point size — see ChatZoom.
            public double ChatTextScale { get; set; } = ChatZoom.Default;

            // How big every session orb is drawn, as a multiplier over the
            // shipped 56-DIP orb — see OrbSizing. An orb's own entry in
            // OrbSizes wins over this.
            public double OrbSize { get; set; } = OrbSizing.Default;

            // CB-200's two volume levels, each a multiplier from 0 to 1 — see
            // AudioVolume. Separate keys, not one, because they answer
            // different questions: how loud a reply is read aloud, and how
            // loud a turn-sound chime is. Someone who wants chimes as a quiet
            // background cue still wants to hear the voice, and the other way
            // round.
            public double SpeechVolume { get; set; } = AudioVolume.Default;
            public double AlertVolume { get; set; } = AudioVolume.Default;

            // Where the arranged shape is centred on screen — physical pixels,
            // same space as OrbPlacement above. Null means "never arranged
            // yet"; SessionManager fills it in with the screen's centre the
            // first time and keeps reusing it after, which is what stops the
            // shape recentring every time an orb joins or leaves.
            public OrbPlacement? ArrangeAnchor
            {
                get => Groups.Get(OrbGroups.Main)!.Anchor;
                set => Groups.SetAnchor(OrbGroups.Main, value);
            }
        }

        // ---- app-wide -------------------------------------------------------

        public static bool ShowOrbs
        {
            get { Load(); lock (Gate) return _model.ShowOrbs; }
            set { Load(); lock (Gate) _model.ShowOrbs = value; Save(); }
        }

        // The effective answer — the stored flag, or ShowOrbs while nobody has
        // set one. See Model.ShowUsageOrbs for why it inherits.
        public static bool ShowUsageOrbs
        {
            get { Load(); lock (Gate) return _model.ShowUsageOrbs ?? _model.ShowOrbs; }
            set { Load(); lock (Gate) _model.ShowUsageOrbs = value; Save(); }
        }

        // The stored flag itself, null included. Only tests need it: saving
        // and restoring ShowUsageOrbs through the getter above would turn an
        // inherited value into an explicit one and leak it into the next test.
        internal static bool? ShowUsageOrbsStored
        {
            get { Load(); lock (Gate) return _model.ShowUsageOrbs; }
            set { Load(); lock (Gate) _model.ShowUsageOrbs = value; Save(); }
        }

        public static bool TintActiveWindow
        {
            get { Load(); lock (Gate) return _model.TintActiveWindow; }
            set { Load(); lock (Gate) _model.TintActiveWindow = value; Save(); }
        }

        public static bool RouteClaudeUrls
        {
            get { Load(); lock (Gate) return _model.RouteClaudeUrls; }
            set { Load(); lock (Gate) _model.RouteClaudeUrls = value; Save(); }
        }

        public static string PreviousClaudeUrlHandler
        {
            get { Load(); lock (Gate) return _model.PreviousClaudeUrlHandler; }
            set { Load(); lock (Gate) _model.PreviousClaudeUrlHandler = value ?? ""; Save(); }
        }

        // Minutes an orb sticks around after its session stops reporting;
        // OrbLifetimeForever (0) keeps it until the session's status file goes
        // away. Anything negative would silently mean "prune immediately", which
        // no setting should be able to say, so it reads as forever too.
        public static int OrbLifetimeMinutes
        {
            get
            {
                Load();
                lock (Gate) return _model.OrbLifetimeMinutes < 0
                    ? OrbLifetimeForever
                    : _model.OrbLifetimeMinutes;
            }
            set
            {
                Load();
                lock (Gate) _model.OrbLifetimeMinutes = value < 0 ? OrbLifetimeForever : value;
                Save();
            }
        }

        // Gates both the mic flyout on the orb and the one-time Whisper model
        // download — see VoiceRecorder/SpeechTranscriber. Nothing about speech
        // capture or transcription runs while this is false.
        public static bool VoiceInputEnabled
        {
            get { Load(); lock (Gate) return _model.VoiceInputEnabled; }
            set { Load(); lock (Gate) _model.VoiceInputEnabled = value; Save(); }
        }

        public static string SpeakVoice
        {
            get { Load(); lock (Gate) return _model.SpeakVoice ?? TextToSpeech.DefaultVoice; }
            set { Load(); lock (Gate) _model.SpeakVoice = value; Save(); }
        }

        // Settings-file only, with no row in the settings window on purpose: a
        // free-text command box invites pasting something and hoping, and this
        // belongs next to the hook JSON in the README where the rest of the
        // power-user surface is documented. Easy to surface later if it earns it.
        public static string? SpeakCommand
        {
            get { Load(); lock (Gate) return _model.SpeakCommand; }
            set { Load(); lock (Gate) _model.SpeakCommand = value; Save(); }
        }

        public static List<string> SpeakCommandArgs
        {
            // A copy, not the list itself: callers build a ProcessStartInfo from
            // this off the UI thread while Settings could be writing it.
            get { Load(); lock (Gate) return new List<string>(_model.SpeakCommandArgs); }
        }

        public static string? SpeakVoicesCommand
        {
            get { Load(); lock (Gate) return _model.SpeakVoicesCommand; }
            set { Load(); lock (Gate) _model.SpeakVoicesCommand = value; Save(); }
        }

        public static List<string> SpeakVoicesCommandArgs
        {
            get { Load(); lock (Gate) return new List<string>(_model.SpeakVoicesCommandArgs); }
        }

        // Null when nothing has been chosen, unlike SpeakVoice and NeuralVoice
        // which fall back to a default: there is no sensible default name for a
        // command whose voices this app has never seen, and passing a guess would
        // be worse than passing nothing.
        public static string? SpeakCommandVoice
        {
            get { Load(); lock (Gate) return _model.SpeakCommandVoice; }
            set { Load(); lock (Gate) _model.SpeakCommandVoice = value; Save(); }
        }

        // "system" when unset, because the platform voices are the only ones that
        // always exist — a fresh install has no neural engine downloaded and no
        // command configured.
        public static string SpeakEngine
        {
            get { Load(); lock (Gate) return _model.SpeakEngine ?? "system"; }
            set { Load(); lock (Gate) _model.SpeakEngine = value; Save(); }
        }

        // Defaults to the whole reply, which is what this app has always spoken.
        // An unrecognised value reads as Full for the same reason: a settings
        // file written by a newer build, or edited by hand, should degrade to
        // the behaviour nobody had to ask for rather than to silence.
        public static SpeakScope SpeakScope
        {
            get
            {
                Load();
                lock (Gate)
                {
                    return string.Equals(_model.SpeakScope, "summary", StringComparison.OrdinalIgnoreCase)
                        ? Orbweaver.SpeakScope.Summary
                        : Orbweaver.SpeakScope.Full;
                }
            }
            set
            {
                Load();
                lock (Gate) _model.SpeakScope = value == Orbweaver.SpeakScope.Summary ? "summary" : "full";
                Save();
            }
        }

        // ---- turn sounds (CB-167) --------------------------------------------

        // The persisted half of AudioMute (CB-298). Everything else goes
        // through AudioMute, which also stops what is already playing.
        public static bool Muted
        {
            get { Load(); lock (Gate) return _model.Muted; }
            set { Load(); lock (Gate) _model.Muted = value; Save(); }
        }

        public static bool TurnSoundsEnabled
        {
            get { Load(); lock (Gate) return _model.TurnSoundsEnabled; }
            set { Load(); lock (Gate) _model.TurnSoundsEnabled = value; Save(); }
        }

        public static string? TurnFinishedSound
        {
            get { Load(); lock (Gate) return _model.TurnFinishedSound; }
            set { Load(); lock (Gate) _model.TurnFinishedSound = value; Save(); }
        }

        public static string? NeedsAttentionSound
        {
            get { Load(); lock (Gate) return _model.NeedsAttentionSound; }
            set { Load(); lock (Gate) _model.NeedsAttentionSound = value; Save(); }
        }

        internal static AccountOrbOverride? AccountOrbOverrideFor(string positionKey)
        {
            if (string.IsNullOrEmpty(positionKey)) return null;

            Load();
            lock (Gate) return _model.AccountOrbOverrides.GetValueOrDefault(positionKey);
        }

        // Null or an all-blank override removes the entry rather than storing
        // one that overrides nothing.
        internal static void SetAccountOrbOverride(string positionKey, AccountOrbOverride? value)
        {
            if (string.IsNullOrEmpty(positionKey)) return;

            Load();
            lock (Gate)
            {
                var normalised = value?.Normalise();
                if (normalised is null || normalised.IsEmpty)
                {
                    if (!_model.AccountOrbOverrides.Remove(positionKey)) return;
                }
                else
                {
                    if (_model.AccountOrbOverrides.GetValueOrDefault(positionKey) == normalised) return;
                    _model.AccountOrbOverrides[positionKey] = normalised;
                }
            }

            Save();
        }

        // Null when this orb has never had either trigger overridden — the
        // same "absent means default" reading ChatPanelSizeFor gives for a
        // panel nobody has resized.
        internal static OrbTurnSound? OrbTurnSoundFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            Load();
            lock (Gate) return _model.OrbTurnSounds.GetValueOrDefault(key);
        }

        // Both null removes the entry rather than storing an override that
        // overrides nothing — the same rule ChatPanelSizes' neighbours don't
        // need because a size and a pin are never "both absent" in a way
        // worth naming, but two independently nullable fields are.
        internal static void SetOrbTurnSound(string key, string? finished, string? attention)
        {
            if (string.IsNullOrEmpty(key)) return;

            Load();
            lock (Gate)
            {
                if (finished is null && attention is null)
                {
                    _model.OrbTurnSounds.Remove(key);
                }
                else
                {
                    _model.OrbTurnSounds[key] = new OrbTurnSound(finished, attention);
                }
            }

            Save();
        }

        // The common case of the above — "this orb goes back to the global
        // default for both triggers" — named so a caller doing that doesn't
        // have to spell out SetOrbTurnSound(key, null, null) to mean it.
        internal static void ClearOrbTurnSound(string key) => SetOrbTurnSound(key, null, null);

        // Null when this orb has no size of its own and follows the global
        // slider — the same "absent means default" reading OrbTurnSoundFor
        // gives.
        internal static double? OrbSizeFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            Load();
            lock (Gate) return _model.OrbSizes.TryGetValue(key, out var size) ? size : null;
        }

        // Null removes the override, which is what the Size menu's "Default"
        // row means. A value is clamped on the way in, so a size written here
        // is always one the orb can be drawn at.
        internal static void SetOrbSize(string key, double? size)
        {
            if (string.IsNullOrEmpty(key)) return;

            Load();
            lock (Gate)
            {
                if (size is null) _model.OrbSizes.Remove(key);
                else _model.OrbSizes[key] = OrbSizing.Clamp(size.Value);
            }

            Save();
        }

        // ---- team member offsets (CB-268 T2) ----------------------------------

        // Null when the member has no saved offset and takes the fan.
        public static (double Dx, double Dy)? OrbMemberOffsetFor(string? soundKey)
        {
            if (string.IsNullOrEmpty(soundKey)) return null;

            Load();
            lock (Gate)
                return _model.OrbMemberOffsets.TryGetValue(soundKey, out var offset)
                    ? (offset.Dx, offset.Dy)
                    : null;
        }

        // Written once, at the end of a member drag, as member centre minus lead centre
        // (see MemberOffset). Null clears — what "Reset this
        // orb's position" means — and a (0, 0) offset is a real one, the member sitting
        // exactly on its lead's centre, kept distinct from having none. A non-finite
        // number is not an offset and is ignored, so a bad measurement cannot reach the
        // file or ComputeGroups.
        public static void SetOrbMemberOffset(string? soundKey, (double Dx, double Dy)? offset)
        {
            if (string.IsNullOrEmpty(soundKey)) return;
            if (offset is { } o && !(SaneOffset(o.Dx) && SaneOffset(o.Dy))) return;

            Load();
            lock (Gate)
            {
                if (offset is null)
                {
                    if (!_model.OrbMemberOffsets.Remove(soundKey)) return;
                }
                else
                {
                    var next = new MemberOffset(offset.Value.Dx, offset.Value.Dy);
                    if (_model.OrbMemberOffsets.GetValueOrDefault(soundKey) == next) return;
                    _model.OrbMemberOffsets[soundKey] = next;
                }
            }

            Save();
        }

        // No display is ten thousand DIP across, so anything beyond that is a
        // corrupt or hand-edited value, not a place a member was dragged to. Dropped
        // rather than clamped: a clamped garbage value would still put the member
        // somewhere nobody chose. Also rejects NaN and infinity.
        internal const double MaxOffsetDip = 10_000;

        internal static bool SaneOffset(double value) =>
            double.IsFinite(value) && Math.Abs(value) <= MaxOffsetDip;

        // Called when an orb's SoundKey changes (CB-10: an untitled session is keyed
        // by its id until a title arrives). Copied, never moved and never over an
        // offset the new key already has — OrbWindow.RefreshSoundKey's rule, for its
        // reasons: two orbs can share a key, so clearing the old one would take the
        // other's offset with it, and an entry under the new key is somebody's choice.
        internal static void CarryOrbMemberOffset(string? oldKey, string? newKey)
        {
            if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey)) return;

            Load();
            lock (Gate)
            {
                if (!_model.OrbMemberOffsets.TryGetValue(oldKey, out var offset)
                    || _model.OrbMemberOffsets.ContainsKey(newKey)) return;
                _model.OrbMemberOffsets[newKey] = offset;
            }

            Save();
        }

        // Turning this on or off takes effect immediately rather than at the
        // next launch: SessionManager asks OpenClawSessions for a snapshot every
        // scan, and that returns nothing at all while this is false.
        public static bool OpenClawEnabled
        {
            get { Load(); lock (Gate) return _model.OpenClawEnabled; }
            set { Load(); lock (Gate) _model.OpenClawEnabled = value; Save(); }
        }

        public static bool ClaudeCloudEnabled
        {
            get { Load(); lock (Gate) return _model.ClaudeCloudEnabled; }
            set { Load(); lock (Gate) _model.ClaudeCloudEnabled = value; Save(); }
        }

        public static string OpenClawHost
        {
            get { Load(); lock (Gate) return _model.OpenClawHost ?? ""; }
            set { Load(); lock (Gate) _model.OpenClawHost = value; Save(); }
        }

        public static int OpenClawPort
        {
            get { Load(); lock (Gate) return _model.OpenClawPort; }
            set { Load(); lock (Gate) _model.OpenClawPort = value; Save(); }
        }

        // Empty until the first successful connection, which records what it
        // was shown. See OpenClawSocket for why a fingerprint rather than the
        // system trust store.
        public static string OpenClawFingerprint
        {
            get { Load(); lock (Gate) return _model.OpenClawFingerprint ?? ""; }
            set { Load(); lock (Gate) _model.OpenClawFingerprint = value; Save(); }
        }

        public static int OpenClawActiveWithinMinutes
        {
            get { Load(); lock (Gate) return _model.OpenClawActiveWithinMinutes; }
            set { Load(); lock (Gate) _model.OpenClawActiveWithinMinutes = value; Save(); }
        }

        // Read once per scan by OpenClawSessions, like OpenClawActiveWithinMinutes
        // above and for the same reason: setting one of these to Hidden should
        // take the orbs off the screen on the next poll rather than at the next
        // launch.
        public static ClusterMode OpenClawHeartbeatMode
        {
            get { Load(); lock (Gate) return _model.OpenClawHeartbeatMode; }
            set { Load(); lock (Gate) _model.OpenClawHeartbeatMode = value; Save(); }
        }

        public static string OpenClawHeartbeatShape
        {
            get { Load(); lock (Gate) return _model.OpenClawHeartbeatShape; }
            set { Load(); lock (Gate) _model.OpenClawHeartbeatShape = value; Save(); }
        }

        public static ClusterMode OpenClawCronMode
        {
            get { Load(); lock (Gate) return _model.OpenClawCronMode; }
            set { Load(); lock (Gate) _model.OpenClawCronMode = value; Save(); }
        }

        public static string OpenClawCronShape
        {
            get { Load(); lock (Gate) return _model.OpenClawCronShape; }
            set { Load(); lock (Gate) _model.OpenClawCronShape = value; Save(); }
        }

        public static ClusterMode OpenClawNativeMode
        {
            get { Load(); lock (Gate) return _model.OpenClawNativeMode; }
            set { Load(); lock (Gate) _model.OpenClawNativeMode = value; Save(); }
        }

        public static string OpenClawNativeShape
        {
            get { Load(); lock (Gate) return _model.OpenClawNativeShape; }
            set { Load(); lock (Gate) _model.OpenClawNativeShape = value; Save(); }
        }

        public static bool RemoteControlEnabled
        {
            get { Load(); lock (Gate) return _model.RemoteControlEnabled; }
            set { Load(); lock (Gate) _model.RemoteControlEnabled = value; Save(); }
        }

        // Never empty: a blank stored value means "never chosen", and the
        // bridge has to launch under *some* config directory.
        // The shared persona pool, as saved. Null for "use the default"; the
        // environment variable still outranks it (PersonaPool.SharedRoot).
        public static string? PersonaPoolDir
        {
            get { Load(); lock (Gate) return _model.PersonaPoolDir; }
            set { Load(); lock (Gate) _model.PersonaPoolDir = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); Save(); }
        }

        public static string RemoteControlProfileDir
        {
            get
            {
                Load();
                lock (Gate)
                {
                    var dir = _model.RemoteControlProfileDir;
                    return string.IsNullOrWhiteSpace(dir) ? DefaultRemoteControlProfileDir : dir!;
                }
            }
            set { Load(); lock (Gate) _model.RemoteControlProfileDir = value; Save(); }
        }

        // The accounts to run relays for, never empty when the feature is on.
        //
        // Falls back through the old single-account key before the default, so
        // turning this into a list does not quietly reset someone who had
        // already chosen an account.
        public static IReadOnlyList<string> RemoteControlProfileDirs
        {
            get
            {
                Load();
                lock (Gate)
                {
                    if (_model.RemoteControlProfileDirs.Count > 0)
                        return _model.RemoteControlProfileDirs.ToList();

                    var single = _model.RemoteControlProfileDir;
                    return new List<string>
                    {
                        string.IsNullOrWhiteSpace(single) ? DefaultRemoteControlProfileDir : single!
                    };
                }
            }
        }

        public static void SetRemoteControlProfileDirs(IEnumerable<string> dirs)
        {
            Load();
            lock (Gate)
            {
                _model.RemoteControlProfileDirs.Clear();
                foreach (var dir in dirs)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    if (_model.RemoteControlProfileDirs.Contains(dir, StringComparer.Ordinal)) continue;
                    _model.RemoteControlProfileDirs.Add(dir);
                }

                // Cleared so the two keys cannot disagree: once a list exists it
                // is the only answer, and a leftover single value would be a
                // second source of truth that only surfaces if the list is
                // emptied again.
                _model.RemoteControlProfileDir = null;
            }

            Save();
        }

        // Folders the "New chat…" dialog has launched into before, most
        // recent first. Stored exactly as SetNewChatRecentFolders wrote it —
        // this accessor does not itself merge in live sessions or apply the
        // cap; that's RecentFolders.Merge's job, run by the caller before
        // saving.
        public static IReadOnlyList<string> NewChatRecentFolders
        {
            get
            {
                Load();
                lock (Gate)
                {
                    return _model.NewChatRecentFolders.ToList();
                }
            }
        }

        public static void SetNewChatRecentFolders(IEnumerable<string> folders)
        {
            Load();
            lock (Gate)
            {
                _model.NewChatRecentFolders.Clear();
                foreach (var folder in folders)
                {
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    _model.NewChatRecentFolders.Add(folder);
                }
            }

            Save();
        }

        // The CLI last chosen in the "New chat…" dialog. Null means never
        // chosen.
        public static string? NewChatLastCli
        {
            get
            {
                Load();
                lock (Gate)
                {
                    return _model.NewChatLastCli;
                }
            }
        }

        public static void SetNewChatLastCli(string? cli)
        {
            Load();
            lock (Gate)
            {
                _model.NewChatLastCli = string.IsNullOrWhiteSpace(cli) ? null : cli;
            }

            Save();
        }

        // The account last chosen in the "New chat…" dialog for one CLI
        // (CB-201, per CLI since CB-203). Null means never chosen, or Default
        // — the dialog treats both the same.
        internal static string? NewChatLastProfileFor(NewChatCli cli)
        {
            Load();
            lock (Gate)
            {
                return _model.NewChatLastProfiles.TryGetValue(cli.ToString(), out var dir) ? dir : null;
            }
        }

        internal static void SetNewChatLastProfile(NewChatCli cli, string? profileDir)
        {
            var value = string.IsNullOrWhiteSpace(profileDir) ? null : profileDir;

            Load();
            lock (Gate)
            {
                if (value is null) _model.NewChatLastProfiles.Remove(cli.ToString());
                else _model.NewChatLastProfiles[cli.ToString()] = value;
            }

            Save();
        }

        public static int RemoteControlIdleMinutes
        {
            get { Load(); lock (Gate) return _model.RemoteControlIdleMinutes; }

            // Clamped rather than trusted. A negative value would mean "already
            // expired" to every comparison downstream, which would stop the
            // bridge the instant it started and read as the feature being
            // broken; RemoteControlIdleNever is the deliberate way to say that.
            set
            {
                Load();
                lock (Gate) _model.RemoteControlIdleMinutes = Math.Max(RemoteControlIdleNever, value);
                Save();
            }
        }

        public static bool PeerLinkEnabled
        {
            get { Load(); lock (Gate) return _model.PeerLinkEnabled; }
            set { Load(); lock (Gate) _model.PeerLinkEnabled = value; Save(); }
        }

        public static string? ToggleOrbsHotkey
        {
            get { Load(); lock (Gate) return _model.ToggleOrbsHotkey; }
            set { Load(); lock (Gate) _model.ToggleOrbsHotkey = value; Save(); }
        }

        public static string? NewChatHotkey
        {
            get { Load(); lock (Gate) return _model.NewChatHotkey; }
            set { Load(); lock (Gate) _model.NewChatHotkey = value; Save(); }
        }

        public static string? ToggleMuteHotkey
        {
            get { Load(); lock (Gate) return _model.ToggleMuteHotkey; }
            set { Load(); lock (Gate) _model.ToggleMuteHotkey = value; Save(); }
        }

        public static string? ToggleUsageOrbsHotkey
        {
            get { Load(); lock (Gate) return _model.ToggleUsageOrbsHotkey; }
            set { Load(); lock (Gate) _model.ToggleUsageOrbsHotkey = value; Save(); }
        }

        // The port to listen on, with 0 meaning "the one everybody expects".
        //
        // **Found by deploying, not by reading.** The stored default was 0, and
        // 0 asks the OS to pick — so a fresh install listened on an ephemeral
        // port (50816, on the mini) instead of 7677. Discovery announces
        // whatever it bound, so that half kept working and hid it; the half that
        // broke is "add a machine by address", whose whole reason for existing
        // is the case where discovery does *not* work. Its optional `:port`
        // defaults to PeerLink.DefaultPort, so a user typing an address would
        // have been dialling 7677 at a machine listening somewhere else, with
        // nothing anywhere saying so.
        //
        // Pure so the mapping is a rule rather than a `??` in a property, and
        // because 0 still has to mean "let the OS pick" where a test asks for
        // it explicitly — Listen(0) is what keeps two loopback tests from
        // colliding.
        internal static int PortToBind(int stored) =>
            stored is <= 0 or > 65535 ? PeerLink.DefaultPort : stored;

        public static int PeerLinkPort
        {
            get { Load(); lock (Gate) return PortToBind(_model.PeerLinkPort); }

            // Clamped rather than trusted. A port outside the range cannot be
            // bound, and a settings file edited by hand is the ordinary way one
            // arrives — the headless case in this project is administered
            // exactly that way.
            set
            {
                Load();
                lock (Gate) _model.PeerLinkPort = value is < 0 or > 65535 ? 0 : value;
                Save();
            }
        }

        public static bool RemoteControlServeOnLaunch
        {
            get { Load(); lock (Gate) return _model.RemoteControlServeOnLaunch; }
            set { Load(); lock (Gate) _model.RemoteControlServeOnLaunch = value; Save(); }
        }

        public static bool OpenClawReplyEnabled
        {
            get { Load(); lock (Gate) return _model.OpenClawReplyEnabled; }
            set { Load(); lock (Gate) _model.OpenClawReplyEnabled = value; Save(); }
        }

        public static bool OpenClawApproveEnabled
        {
            get { Load(); lock (Gate) return _model.OpenClawApproveEnabled; }
            set { Load(); lock (Gate) _model.OpenClawApproveEnabled = value; Save(); }
        }

        public static bool ClaudeCodeChatEnabled
        {
            get { Load(); lock (Gate) return _model.ClaudeCodeChatEnabled; }
            set { Load(); lock (Gate) _model.ClaudeCodeChatEnabled = value; Save(); }
        }

        public static bool ClaudeCodeReplyEnabled
        {
            get { Load(); lock (Gate) return _model.ClaudeCodeReplyEnabled; }
            set { Load(); lock (Gate) _model.ClaudeCodeReplyEnabled = value; Save(); }
        }

        public static bool PeerInboundNoticeSeen
        {
            get { Load(); lock (Gate) return _model.PeerInboundNoticeSeen; }
            set { Load(); lock (Gate) _model.PeerInboundNoticeSeen = value; Save(); }
        }

        public static bool CodexChatEnabled
        {
            get { Load(); lock (Gate) return _model.CodexChatEnabled; }
            set { Load(); lock (Gate) _model.CodexChatEnabled = value; Save(); }
        }

        public static bool CodexReplyEnabled
        {
            get { Load(); lock (Gate) return _model.CodexReplyEnabled; }
            set { Load(); lock (Gate) _model.CodexReplyEnabled = value; Save(); }
        }

        public static bool AutoColorSessions
        {
            get { Load(); lock (Gate) return _model.AutoColorSessions; }
            set { Load(); lock (Gate) _model.AutoColorSessions = value; Save(); }
        }

        public static bool ClaudeCodeEnabled
        {
            get { Load(); lock (Gate) return _model.ClaudeCodeEnabled; }
            set { Load(); lock (Gate) _model.ClaudeCodeEnabled = value; Save(); }
        }

        public static bool CodexEnabled
        {
            get { Load(); lock (Gate) return _model.CodexEnabled; }
            set { Load(); lock (Gate) _model.CodexEnabled = value; Save(); }
        }

        public static bool CodexAccountUsageEnabled
        {
            get { Load(); lock (Gate) return _model.CodexAccountUsageEnabled; }
            set { Load(); lock (Gate) _model.CodexAccountUsageEnabled = value; Save(); }
        }

        public static bool GrokChatEnabled
        {
            get { Load(); lock (Gate) return _model.GrokChatEnabled; }
            set { Load(); lock (Gate) _model.GrokChatEnabled = value; Save(); }
        }

        public static bool GrokReplyEnabled
        {
            get { Load(); lock (Gate) return _model.GrokReplyEnabled; }
            set { Load(); lock (Gate) _model.GrokReplyEnabled = value; Save(); }
        }

        public static bool GrokEnabled
        {
            get { Load(); lock (Gate) return _model.GrokEnabled; }
            set { Load(); lock (Gate) _model.GrokEnabled = value; Save(); }
        }

        public static bool GrokAccountUsageEnabled
        {
            get { Load(); lock (Gate) return _model.GrokAccountUsageEnabled; }
            set { Load(); lock (Gate) _model.GrokAccountUsageEnabled = value; Save(); }
        }

        public static bool GrokAutoRefreshEnabled
        {
            get { Load(); lock (Gate) return _model.GrokAutoRefreshEnabled; }
            set { Load(); lock (Gate) _model.GrokAutoRefreshEnabled = value; Save(); }
        }

        public static string ClickAction
        {
            get { Load(); lock (Gate) return _model.ClickAction; }
            set { Load(); lock (Gate) _model.ClickAction = value; Save(); }
        }

        public static string DoubleClickAction
        {
            get { Load(); lock (Gate) return _model.DoubleClickAction; }
            set { Load(); lock (Gate) _model.DoubleClickAction = value; Save(); }
        }

        public static string TripleClickAction
        {
            get { Load(); lock (Gate) return _model.TripleClickAction; }
            set { Load(); lock (Gate) _model.TripleClickAction = value; Save(); }
        }

        public static bool NeuralVoiceEnabled
        {
            get { Load(); lock (Gate) return _model.NeuralVoiceEnabled; }
            set { Load(); lock (Gate) _model.NeuralVoiceEnabled = value; Save(); }
        }

        public static string NeuralVoice
        {
            get { Load(); lock (Gate) return _model.NeuralVoice ?? NeuralSpeech.DefaultVoiceName; }
            set { Load(); lock (Gate) _model.NeuralVoice = value; Save(); }
        }

        // One letter (the default) or two initials on every orb's glyph —
        // see OrbWindow.GlyphFor. Purely cosmetic, so there's no lifecycle
        // to guard the way VoiceInputEnabled has; SessionManager.ReapplyGlyphs
        // is what makes an already-open orb notice a flip without waiting
        // for its next hook update.
        public static bool TwoLetterGlyphs
        {
            get { Load(); lock (Gate) return _model.TwoLetterGlyphs; }
            set { Load(); lock (Gate) _model.TwoLetterGlyphs = value; Save(); }
        }

        public static bool AccountUsageEnabled
        {
            get { Load(); lock (Gate) return _model.AccountUsageEnabled; }
            set { Load(); lock (Gate) _model.AccountUsageEnabled = value; Save(); }
        }

        // ---- auto-organize ----------------------------------------------------

        public static string ArrangeShape
        {
            get { Load(); lock (Gate) return _model.ArrangeShape; }
            set { Load(); lock (Gate) _model.ArrangeShape = value; Save(); }
        }

        public static double ArrangeSpacing
        {
            get { Load(); lock (Gate) return _model.ArrangeSpacing; }
            set { Load(); lock (Gate) _model.ArrangeSpacing = value; Save(); }
        }

        // The chat panel's text size, as a multiplier over what it ships at.
        // Clamped in the setter rather than trusted, so the one thing that can
        // never happen is a panel drawn at a size nothing on screen can undo —
        // the keyboard gesture and the settings slider both go through here.
        public static double ChatTextScale
        {
            get { Load(); lock (Gate) return ChatZoom.Clamp(_model.ChatTextScale); }
            set { Load(); lock (Gate) _model.ChatTextScale = ChatZoom.Clamp(value); Save(); }
        }

        // Every session orb's size, as a multiplier over the shipped orb.
        // Clamped both ways for ChatTextScale's reason — and on read against
        // *this* platform's floor, so a file written on a Mac at 0.6 and
        // carried to Windows draws at Windows' 0.7 instead of with a dead
        // click strip under it.
        public static double OrbSize
        {
            get { Load(); lock (Gate) return OrbSizing.Clamp(_model.OrbSize); }
            set { Load(); lock (Gate) _model.OrbSize = OrbSizing.Clamp(value); Save(); }
        }

        // CB-200: how loud spoken replies are, 0 to 1. Clamped both ways,
        // like OrbSize, so neither a hand edit nor a caller can hand a backend
        // a level it would reject — SAPI throws on anything past 100.
        public static double SpeechVolume
        {
            get { Load(); lock (Gate) return AudioVolume.Clamp(_model.SpeechVolume); }
            set { Load(); lock (Gate) _model.SpeechVolume = AudioVolume.Clamp(value); Save(); }
        }

        // CB-200: how loud turn-sound chimes are, 0 to 1 — every chime,
        // previews included, since they all go through ChimePlayer.
        public static double AlertVolume
        {
            get { Load(); lock (Gate) return AudioVolume.Clamp(_model.AlertVolume); }
            set { Load(); lock (Gate) _model.AlertVolume = AudioVolume.Clamp(value); Save(); }
        }

        public static OrbPlacement? ArrangeAnchor
        {
            get { Load(); lock (Gate) return _model.ArrangeAnchor; }
            set { Load(); lock (Gate) _model.ArrangeAnchor = value; Save(); }
        }

        // ---- orb state colours ----------------------------------------------

        // "#RRGGBB", with null meaning the built-in default. OrbColors owns the
        // three defaults and the parsing, which keeps this file free of Avalonia
        // types the way the rest of it is — and means a hand-edited garbage hex
        // costs one colour rather than a Load().
        //
        // Null matters. Storing the *effective* colour instead would bake today's
        // slate blue into every settings.json the first time someone opened the
        // window, and a future retune of the shipped palette would then reach
        // nobody. It's also what the Reset button writes.
        //
        // These three defer their file write — see SaveSoon.
        public static string? IdleColor
        {
            get { Load(); lock (Gate) return _model.IdleColor; }
            set { Load(); lock (Gate) _model.IdleColor = value; SaveSoon(); }
        }

        public static string? GeneratingColor
        {
            get { Load(); lock (Gate) return _model.GeneratingColor; }
            set { Load(); lock (Gate) _model.GeneratingColor = value; SaveSoon(); }
        }

        public static string? WaitingColor
        {
            get { Load(); lock (Gate) return _model.WaitingColor; }
            set { Load(); lock (Gate) _model.WaitingColor = value; SaveSoon(); }
        }

        // ---- orb groups (CB-268) ---------------------------------------------

        // Every group in slot order: main, heartbeats, crons, then user groups
        // by id. A snapshot — records are immutable, so the list is safe to hold
        // after the lock is released.
        public static IReadOnlyList<OrbGroup> OrbGroupList()
        {
            Load();
            lock (Gate) return _model.Groups.Ordered();
        }

        public static OrbGroup? OrbGroupFor(string? id)
        {
            Load();
            lock (Gate) return _model.Groups.Get(id);
        }

        // The group an orb was explicitly put in, or null when nothing was —
        // OrbGroups.GroupIdFor then lets the CB-14 classification decide.
        // `fallbackKey` is the bare-directory key, tried when the full key has
        // no entry, exactly the second chance SessionManager gives
        // OrbPositionFor for Claude Code orbs; pass null for a CLI that does not
        // get one.
        public static string? OrbGroupMembershipFor(string? key, string? fallbackKey = null)
        {
            Load();
            lock (Gate) return _model.Groups.MembershipFor(key, fallbackKey);
        }

        public static OrbGroup CreateOrbGroup(string? shape = null)
        {
            Load();
            OrbGroup made;
            lock (Gate) made = _model.Groups.Create(shape);

            Save();
            return made;
        }

        // `defer` for a name typed into a box, which changes on every keystroke
        // (GroupStyleWindow); a one-off rename leaves it false.
        public static void RenameOrbGroup(string id, string? name, bool defer = false) =>
            ChangeGroups(groups => groups.Rename(id, name), defer);

        public static void SetOrbGroupShape(string id, string? shape) =>
            ChangeGroups(groups => groups.SetShape(id, shape));

        // Where the group's shape is centred, in physical pixels — the landed
        // anchor, saved when it changed. Null forgets it.
        public static void SetOrbGroupAnchor(string id, OrbPlacement? anchor) =>
            ChangeGroups(groups => groups.SetAnchor(id, anchor));

        // The per-group outline override. Null, or a style with nothing set,
        // returns the group to the global one.
        //
        // `defer` is for a caller behind a slider or colour picker, which raise
        // a change on every pointer move: the model updates at once and the file
        // write waits (SaveSoon), like the global outline setters. Anything that
        // is one deliberate change — a checkbox, the menu — leaves it false.
        public static void SetOrbGroupStyle(string id, GroupStyle? style, bool defer = false) =>
            ChangeGroups(groups => groups.SetStyle(id, style), defer);

        public static void DeleteOrbGroup(string id) =>
            ChangeGroups(groups => groups.Delete(id));

        // Puts one orb in a group. A group that loses its last member this way
        // is removed.
        public static void MoveOrbToGroup(string? key, string groupId) =>
            ChangeGroups(groups => groups.Move(key, groupId));

        // Snapping a team: the lead's key and every member's, so the team never
        // splits across groups.
        public static void AssignTeamToGroup(
            string? leadKey, IEnumerable<string?> memberKeys, string groupId) =>
            ChangeGroups(groups => groups.AssignTeam(leadKey, memberKeys, groupId));

        // T3: gives members that have no group the root's. See
        // OrbGroupTable.EnsureTeamMembership — S7 calls it when a late member
        // appears under a lead that has an explicit membership.
        public static void EnsureTeamMembership(
            string? rootKey, IEnumerable<string?> memberKeys, string? rootFallbackKey = null) =>
            ChangeGroups(groups => groups.EnsureTeamMembership(rootKey, memberKeys, rootFallbackKey));

        // T1: the group an orb belongs to once its team is considered — the
        // team root's explicit membership, or null so OrbGroups.GroupIdFor falls
        // back to the classification. `keys[i]` is orb i's position key and
        // `leadOf[i]` the index of its lead.
        public static string? OrbTeamGroupFor(int index, int[] leadOf, IReadOnlyList<string?> keys)
        {
            Load();
            lock (Gate)
                return OrbGroups.TeamGroupOf(index, leadOf, keys, key => _model.Groups.MembershipFor(key));
        }

        // Forgets an orb's explicit group, so the classification decides again.
        // The menu's "Leave group" is MoveOrbToGroup(key, OrbGroups.Main)
        // instead, which holds even a heartbeat orb in main.
        public static void UnassignOrbGroup(string? key) =>
            ChangeGroups(groups => groups.Unassign(key));

        // Called when an orb's position key changes (CB-10: an untitled session
        // is keyed by its session id until a title arrives). Copies, never
        // overwrites — see OrbGroupTable.CarryMembership.
        public static void CarryOrbGroupMembership(string? oldKey, string? newKey) =>
            ChangeGroups(groups => groups.CarryMembership(oldKey, newKey));

        private static void ChangeGroups(Func<OrbGroupTable, bool> change, bool defer = false)
        {
            Load();
            bool changed;
            lock (Gate) changed = change(_model.Groups);

            if (!changed) return;
            if (defer) SaveSoon();
            else Save();
        }

        // ---- group outline style (CB-268) -------------------------------------

        // CB-282: a small caption with each group's name above its outline. Off
        // by default; deferred write like the other outline settings, because it
        // sits behind a switch that is flipped while looking at the result.
        public static bool GroupNamesVisible
        {
            get { Load(); lock (Gate) return _model.GroupNamesVisible; }
            set { Load(); lock (Gate) _model.GroupNamesVisible = value; SaveSoon(); }
        }

        public static bool GroupOutlinesEnabled
        {
            get { Load(); lock (Gate) return _model.GroupOutlinesEnabled; }
            set { Load(); lock (Gate) _model.GroupOutlinesEnabled = value; Save(); }
        }

        // The four setters below defer their write, like the orb colours above:
        // they sit behind a colour picker and a slider, which raise a change on
        // every pointer move. A colour that does not parse is ignored rather
        // than stored, and an opacity is clamped to 0..1.
        public static string GroupOutlineStroke
        {
            get { Load(); lock (Gate) return _model.GroupOutlineStroke; }
            set
            {
                Load();
                if (OrbGroups.NormalizeColor(value) is not { } hex) return;
                lock (Gate) _model.GroupOutlineStroke = hex;
                SaveSoon();
            }
        }

        public static double GroupOutlineStrokeOpacity
        {
            get { Load(); lock (Gate) return _model.GroupOutlineStrokeOpacity; }
            set
            {
                Load();
                if (OrbGroups.ClampOpacity(value) is not { } opacity) return;
                lock (Gate) _model.GroupOutlineStrokeOpacity = opacity;
                SaveSoon();
            }
        }

        public static string GroupOutlineFill
        {
            get { Load(); lock (Gate) return _model.GroupOutlineFill; }
            set
            {
                Load();
                if (OrbGroups.NormalizeColor(value) is not { } hex) return;
                lock (Gate) _model.GroupOutlineFill = hex;
                SaveSoon();
            }
        }

        public static double GroupOutlineFillOpacity
        {
            get { Load(); lock (Gate) return _model.GroupOutlineFillOpacity; }
            set
            {
                Load();
                if (OrbGroups.ClampOpacity(value) is not { } opacity) return;
                lock (Gate) _model.GroupOutlineFillOpacity = opacity;
                SaveSoon();
            }
        }

        // The four globals as the fully populated GroupStyle that
        // GroupStyle.Resolve takes as its `globals` argument.
        public static GroupStyle GroupOutlineGlobals()
        {
            Load();
            lock (Gate)
                return new GroupStyle(
                    _model.GroupOutlineStroke, _model.GroupOutlineStrokeOpacity,
                    _model.GroupOutlineFill, _model.GroupOutlineFillOpacity);
        }

        // ---- orb positions --------------------------------------------------

        public static OrbPlacement? OrbPositionFor(string key)
        {
            Load();
            lock (Gate) return _model.OrbPositions.GetValueOrDefault(key);
        }

        public static void SetOrbPosition(string key, int x, int y, double? size = null)
        {
            Load();
            lock (Gate)
            {
                var placement = new OrbPlacement(x, y, size);
                if (_model.OrbPositions.GetValueOrDefault(key) == placement) return;
                _model.OrbPositions[key] = placement;
            }

            Save();
        }

        public static void ClearOrbPosition(string key)
        {
            Load();
            lock (Gate)
            {
                if (!_model.OrbPositions.Remove(key)) return;
            }

            Save();
        }

        // ---- chat panel sizes -----------------------------------------------

        // Null means "never resized", which is what leaves the panel at the
        // size its XAML ships — the same reason the colours and the voice above
        // store null rather than a copy of today's default. Storing 340x420 the
        // first time a panel opened would freeze the shipped default into every
        // settings.json on disk and a future retune would reach nobody.
        public static PanelSize? ChatPanelSizeFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            Load();
            lock (Gate) return _model.ChatPanelSizes.GetValueOrDefault(key);
        }

        public static void SetChatPanelSize(string key, double width, double height)
        {
            // No key means no stable identity to save under — a local CLI
            // session with no cwd. Silently nothing, the same as an orb in that
            // position gets no saved place.
            if (string.IsNullOrEmpty(key)) return;

            // Rounded here rather than at the call site so the file stays
            // readable whoever writes to it: a drag ends on whatever fraction
            // of a DIP the pointer was at, and nobody wants 341.99998 in their
            // settings.
            var size = new PanelSize(Math.Round(width), Math.Round(height));

            Load();
            lock (Gate)
            {
                if (_model.ChatPanelSizes.GetValueOrDefault(key) == size) return;
                _model.ChatPanelSizes[key] = size;
            }

            Save();
        }

        // ---- pinned chat panels (CB-111) -------------------------------------

        // Null means "not pinned, or pinned with nothing saved yet" — the same
        // reading OrbPositionFor gives, and for the same reason: this is not a
        // setting anyone edits, it is a record of the one thing pinning is
        // asked to remember.
        public static OrbPlacement? PinnedChatPanelPositionFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            Load();
            lock (Gate) return _model.PinnedChatPanels.GetValueOrDefault(key);
        }

        // Called once when a panel is pinned (its position at that instant is
        // worth remembering even though it hasn't moved) and again on every
        // drag of an already-pinned panel — ChatPanel only calls this while
        // _pinned is true, so an unpinned panel being dragged around (there is
        // no such gesture today, but if one existed) would never reach here.
        public static void SetPinnedChatPanelPosition(string key, int x, int y)
        {
            // No key means no stable identity to save under, same guard as
            // SetChatPanelSize and for the same reason: a local CLI session
            // with no cwd has nowhere to be found again next run.
            if (string.IsNullOrEmpty(key)) return;

            Load();
            lock (Gate)
            {
                var existing = _model.PinnedChatPanels.GetValueOrDefault(key);
                if (existing is not null && existing.X == x && existing.Y == y) return;
                _model.PinnedChatPanels[key] = new OrbPlacement(x, y);
            }

            Save();
        }

        // Unpinning is the only thing that clears this — not the panel
        // closing, not the session ending, not the app quitting. Those three
        // all reach the same Window.Closed path (ChatPanel.Dissolve via
        // CloseFor, or the desktop lifetime's own Shutdown() closing every
        // window), and a save that could not tell "the app is quitting" apart
        // from "this conversation is over" would erase every pin on every
        // restart — which is the one thing this ticket exists to stop. See
        // ReturnOrbToStack's identical choice for OrbPositions.
        public static void ClearPinnedChatPanelPosition(string key)
        {
            Load();
            lock (Gate)
            {
                if (!_model.PinnedChatPanels.Remove(key)) return;
            }

            Save();
        }

        // ---- extra Claude Code (CLI) profile directories ---------------------

        // A copy, so callers can't mutate the store without going through
        // Add/Remove — same guard rail as ProfileSettings.For below.
        public static IReadOnlyList<string> ClaudeCodeProfileDirs
        {
            get { Load(); lock (Gate) return _model.ClaudeCodeProfileDirs.ToList(); }
        }

        public static void AddClaudeCodeProfileDir(string dirName)
        {
            Load();
            lock (Gate)
            {
                if (!_model.ClaudeCodeProfileDirs.Contains(dirName, StringComparer.Ordinal))
                {
                    _model.ClaudeCodeProfileDirs.Add(dirName);
                }
            }
            Save();
        }

        public static void RemoveClaudeCodeProfileDir(string dirName)
        {
            Load();
            lock (Gate) { _model.ClaudeCodeProfileDirs.Remove(dirName); }
            Save();
        }

        // ---- settings-window section fold state -------------------------------

        // View state that persists: whether a section id is currently folded.
        // Called from the disclosure header's click handler only — never from
        // SettingsSection.IsOpen's setter, which is the one contract CB-166's
        // two halves share. See the comment on IsOpen in SettingsWindow.cs for
        // why: the filter force-expands a folded section to show a match, and
        // if that setter wrote here too, the first search would silently
        // persist all thirteen sections as open.
        public static bool IsSettingsSectionCollapsed(string id)
        {
            Load();
            lock (Gate) return _model.CollapsedSettingsSections.Contains(id, StringComparer.Ordinal);
        }

        public static void SetSettingsSectionCollapsed(string id, bool collapsed)
        {
            Load();
            bool changed;
            lock (Gate)
            {
                var already = _model.CollapsedSettingsSections.Contains(id, StringComparer.Ordinal);
                if (collapsed && !already)
                {
                    _model.CollapsedSettingsSections.Add(id);
                    changed = true;
                }
                else if (!collapsed && already)
                {
                    _model.CollapsedSettingsSections.Remove(id);
                    changed = true;
                }
                else
                {
                    // Already in the state being asked for — a triangle emits one
                    // event per click, and rebuilding the page (via a Rebuild()
                    // triggered elsewhere) must never cost a disk write on its own.
                    changed = false;
                }
            }

            if (changed) Save();
        }

        public static IReadOnlyList<string> CodexHomes
        {
            get { Load(); lock (Gate) return _model.CodexHomes.ToList(); }
        }

        public static void AddCodexHome(string dirName)
        {
            Load();
            lock (Gate)
            {
                if (!_model.CodexHomes.Contains(dirName, StringComparer.Ordinal))
                {
                    _model.CodexHomes.Add(dirName);
                }
            }
            Save();
        }

        public static void RemoveCodexHome(string dirName)
        {
            Load();
            lock (Gate) { _model.CodexHomes.Remove(dirName); }
            Save();
        }

        public static IReadOnlyList<string> GrokHomes
        {
            get { Load(); lock (Gate) return _model.GrokHomes.ToList(); }
        }

        public static void AddGrokHome(string dirName)
        {
            Load();
            lock (Gate)
            {
                if (!_model.GrokHomes.Contains(dirName, StringComparer.Ordinal))
                {
                    _model.GrokHomes.Add(dirName);
                }
            }
            Save();
        }

        public static void RemoveGrokHome(string dirName)
        {
            Load();
            lock (Gate) { _model.GrokHomes.Remove(dirName); }
            Save();
        }

        // ---- per profile ----------------------------------------------------

        // A copy, so callers can't mutate the store without going through Update.
        public static ProfileSettings For(string folder)
        {
            Load();
            lock (Gate)
            {
                if (!_model.Profiles.TryGetValue(folder, out var found)) return new ProfileSettings();

                return new ProfileSettings
                {
                    Name = found.Name,
                    Color = found.Color,
                    ShowSwatch = found.ShowSwatch,
                    TintDockIcon = found.TintDockIcon,
                    TintWindow = found.TintWindow
                };
            }
        }

        // Forget a profile's name and colour, for when the profile itself is
        // gone. Left behind they would sit waiting for something that no longer
        // exists — and be inherited by the next profile that happened to reuse
        // the folder name, which is not far-fetched: new ones are numbered
        // Claude-Profile-1, -2, and the numbering reuses a gap.
        public static void RemoveProfile(string folder)
        {
            Load();
            lock (Gate)
            {
                if (!_model.Profiles.Remove(folder)) return;
            }
            Save();
        }

        // Named setters for the two per-profile fields the settings window writes
        // from code that cannot itself be run by a test.
        //
        // They exist so those callers hold no lambda: a lambda inside a method
        // carrying [ExcludeFromCodeCoverage] is hoisted to its own method and does
        // NOT inherit the attribute, so `entry => entry.Color = …` was being
        // counted while the method around it was not. Written as ordinary setters
        // rather than as a trick — and they are covered directly, which the
        // one-line Update calls never were.
        public static void SetProfileColor(string folder, string? color) =>
            Update(folder, entry => entry.Color = color);

        public static void SetProfileShowSwatch(string folder, bool show) =>
            Update(folder, entry => entry.ShowSwatch = show);

        public static void Update(string folder, Action<ProfileSettings> change)
        {
            Load();

            lock (Gate)
            {
                if (!_model.Profiles.TryGetValue(folder, out var entry))
                {
                    entry = new ProfileSettings();
                    _model.Profiles[folder] = entry;
                }

                change(entry);
            }

            Save();
        }

        // ---- storage --------------------------------------------------------

        private static void Load()
        {
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;

                try
                {
                    if (!File.Exists(Path_)) return;

                    var root = JsonNode.Parse(File.ReadAllText(Path_)) as JsonObject;
                    if (root is null) return;

                    var model = new Model
                    {
                        ShowOrbs = root["showOrbs"]?.GetValue<bool>() ?? true,
                        ShowUsageOrbs = root["showUsageOrbs"]?.GetValue<bool>(),
                        TintActiveWindow = root["tintActiveWindow"]?.GetValue<bool>() ?? true,
                        RouteClaudeUrls = root["routeClaudeUrls"]?.GetValue<bool>() ?? true,
                        PreviousClaudeUrlHandler = Text(root["previousClaudeUrlHandler"]) ?? "",
                        OrbLifetimeMinutes =
                            root["orbLifetimeMinutes"]?.GetValue<int>() ?? DefaultOrbLifetimeMinutes,
                        VoiceInputEnabled = root["voiceInputEnabled"]?.GetValue<bool>() ?? false,
                        OpenClawEnabled = root["openclawEnabled"]?.GetValue<bool>() ?? false,
                        ClaudeCloudEnabled = root["claudeCloudEnabled"]?.GetValue<bool>() ?? false,
                        OpenClawHost = Text(root["openclawHost"]),
                        OpenClawPort = root["openclawPort"]?.GetValue<int>() ?? DefaultOpenClawPort,
                        OpenClawFingerprint = Text(root["openclawFingerprint"]),
                        OpenClawReplyEnabled = root["openclawReplyEnabled"]?.GetValue<bool>() ?? false,
                        OpenClawApproveEnabled = root["openclawApproveEnabled"]?.GetValue<bool>() ?? false,
                        OpenClawActiveWithinMinutes =
                            root["openclawActiveWithinMinutes"]?.GetValue<int>() ?? DefaultOpenClawActiveWithin,
                        OpenClawHeartbeatMode = ClusterModeFrom(
                            root["openclawHeartbeatMode"], root["openclawShowHeartbeats"]),
                        OpenClawHeartbeatShape = Shape(
                            root["openclawHeartbeatShape"], DefaultOpenClawHeartbeatShape),
                        OpenClawCronMode = ClusterModeFrom(root["openclawCronMode"], null),
                        OpenClawCronShape = Shape(
                            root["openclawCronShape"], DefaultOpenClawCronShape),
                        OpenClawNativeMode = ClusterModeFrom(root["openclawNativeMode"], null),
                        OpenClawNativeShape = Shape(
                            root["openclawNativeShape"], DefaultOpenClawNativeShape),
                        RemoteControlEnabled = root["remoteControlEnabled"]?.GetValue<bool>() ?? false,
                        PersonaPoolDir = Text(root["personaPoolDir"]),
                        RemoteControlProfileDir = Text(root["remoteControlProfileDir"]),
                        RemoteControlIdleMinutes =
                            root["remoteControlIdleMinutes"]?.GetValue<int>() ?? DefaultRemoteControlIdle,
                        RemoteControlServeOnLaunch =
                            root["remoteControlServeOnLaunch"]?.GetValue<bool>() ?? false,
                        PeerLinkEnabled = root["peerLinkEnabled"]?.GetValue<bool>() ?? false,
                        PeerLinkPort = root["peerLinkPort"]?.GetValue<int>() ?? 0,
                        ToggleOrbsHotkey = Text(root["toggleOrbsHotkey"]),
                        NewChatHotkey = Text(root["newChatHotkey"]),
                        ToggleUsageOrbsHotkey = Text(root["toggleUsageOrbsHotkey"]),
                        ToggleMuteHotkey = Text(root["toggleMuteHotkey"]),
                        ClaudeCodeChatEnabled = root["claudeCodeChatEnabled"]?.GetValue<bool>() ?? true,
                        ClaudeCodeReplyEnabled = root["claudeCodeReplyEnabled"]?.GetValue<bool>() ?? false,
                        CodexChatEnabled = root["codexChatEnabled"]?.GetValue<bool>() ?? true,
                        CodexReplyEnabled = root["codexReplyEnabled"]?.GetValue<bool>() ?? false,
                        CodexAccountUsageEnabled = root["codexAccountUsageEnabled"]?.GetValue<bool>() ?? false,
                        AutoColorSessions = root["autoColorSessions"]?.GetValue<bool>() ?? false,
                        PeerInboundNoticeSeen = root["peerInboundNoticeSeen"]?.GetValue<bool>() ?? false,
                        ClaudeCodeEnabled = root["claudeCodeEnabled"]?.GetValue<bool>() ?? true,
                        CodexEnabled = root["codexEnabled"]?.GetValue<bool>() ?? true,
                        GrokChatEnabled = root["grokChatEnabled"]?.GetValue<bool>() ?? true,
                        GrokReplyEnabled = root["grokReplyEnabled"]?.GetValue<bool>() ?? false,
                        GrokEnabled = root["grokEnabled"]?.GetValue<bool>() ?? true,
                        GrokAccountUsageEnabled = root["grokAccountUsageEnabled"]?.GetValue<bool>() ?? false,
                        GrokAutoRefreshEnabled = root["grokAutoRefreshEnabled"]?.GetValue<bool>() ?? false,
                        ClickAction = root["clickAction"]?.GetValue<string>() ?? "terminal",
                        DoubleClickAction = root["doubleClickAction"]?.GetValue<string>() ?? "none",
                        TripleClickAction = root["tripleClickAction"]?.GetValue<string>() ?? "none",
                        TwoLetterGlyphs = root["twoLetterGlyphs"]?.GetValue<bool>() ?? false,
                        AccountUsageEnabled = root["accountUsageEnabled"]?.GetValue<bool>() ?? false,
                        ArrangeShape = root["arrangeShape"]?.GetValue<string>() ?? DefaultArrangeShape,
                        ArrangeSpacing = root["arrangeSpacing"]?.GetValue<double>() ?? DefaultArrangeSpacing,

                        // Clamped on the way in as well as on the way out: this
                        // file is editable by hand and a 40x scale is a chat
                        // panel with one word in it and no way back to the
                        // setting that caused it.
                        ChatTextScale = ChatZoom.Clamp(
                            root["chatTextScale"]?.GetValue<double>() ?? ChatZoom.Default),

                        // Number() rather than GetValue<double>(): a new key,
                        // so it gets the defence Number's comment describes
                        // from the start — a hand-edited "big" costs the orb
                        // size and nothing else. Clamped here too, for
                        // ChatTextScale's reason just above.
                        OrbSize = OrbSizing.Clamp(Number(root["orbSize"]) ?? OrbSizing.Default),

                        // Number() and a clamp for orbSize's reasons: a
                        // hand-edited "loud" costs this one level and nothing
                        // else, and a 40 is full volume rather than a
                        // multiplier nothing downstream expects.
                        SpeechVolume = AudioVolume.Clamp(Number(root["speechVolume"]) ?? AudioVolume.Default),
                        AlertVolume = AudioVolume.Clamp(Number(root["alertVolume"]) ?? AudioVolume.Default),

                        // speakVoice was declared on the model and written by its
                        // property from the start, but never read here and never
                        // written by Save — so every voice anyone picked was
                        // discarded at exit and silently replaced by the default.
                        // Text(), not GetValue<string>(), for the reason Text's
                        // own comment gives: a type mismatch in one hand-edited
                        // key would otherwise throw and reset every setting in
                        // this file, profiles and orb positions included.
                        SpeakVoice = Text(root["speakVoice"]),
                        NeuralVoiceEnabled = root["neuralVoiceEnabled"]?.GetValue<bool>() ?? false,
                        NeuralVoice = Text(root["neuralVoice"]),
                        SpeakCommand = Text(root["speakCommand"]),
                        SpeakVoicesCommand = Text(root["speakVoicesCommand"]),
                        SpeakCommandVoice = Text(root["speakCommandVoice"]),
                        SpeakEngine = Text(root["speakEngine"]),
                        SpeakScope = Text(root["speakScope"]),
                        TurnSoundsEnabled = Bool(root["turnSoundsEnabled"], true),
                        Muted = Bool(root["muted"], false),
                        TurnFinishedSound = Text(root["turnFinishedSound"]),
                        NeedsAttentionSound = Text(root["needsAttentionSound"]),
                        NewChatLastCli = Text(root["newChatLastCli"])
                    };

                    // CB-203's per-CLI map, then the pre-CB-203 single key as
                    // Claude Code's entry if the map did not already name one.
                    // Only real CLI names are kept, so a hand-edited key cannot
                    // sit in the model forever with nothing able to read it.
                    if (root["newChatLastProfiles"] is JsonObject lastProfiles)
                    {
                        foreach (var (cliName, node) in lastProfiles)
                        {
                            if (Enum.TryParse<NewChatCli>(cliName, out var parsedCli)
                                && parsedCli.ToString() == cliName
                                && Text(node) is { } profileDir)
                            {
                                model.NewChatLastProfiles[cliName] = profileDir;
                            }
                        }
                    }

                    if (Text(root["newChatLastProfile"]) is { } legacyProfile)
                    {
                        model.NewChatLastProfiles.TryAdd(nameof(NewChatCli.ClaudeCode), legacyProfile);
                    }

                    // Same shape as claudeCodeProfileDirs below: read as an array
                    // and skipped entirely when absent, so an older settings file
                    // simply has no arguments rather than failing to load.
                    if (root["speakCommandArgs"] is JsonArray speakArgs)
                    {
                        foreach (var argument in speakArgs)
                        {
                            var value = Text(argument);
                            if (!string.IsNullOrEmpty(value)) model.SpeakCommandArgs.Add(value);
                        }
                    }

                    if (root["speakVoicesCommandArgs"] is JsonArray voicesArgs)
                    {
                        foreach (var argument in voicesArgs)
                        {
                            var value = Text(argument);
                            if (!string.IsNullOrEmpty(value)) model.SpeakVoicesCommandArgs.Add(value);
                        }
                    }

                    if (root["orbColors"] is JsonObject orbColors)
                    {
                        model.IdleColor = Text(orbColors["idle"]);
                        model.GeneratingColor = Text(orbColors["generating"]);
                        model.WaitingColor = Text(orbColors["waiting"]);
                    }

                    if (root["claudeCodeProfileDirs"] is JsonArray profileDirs)
                    {
                        foreach (var node in profileDirs)
                        {
                            if (node?.GetValue<string>() is { Length: > 0 } dirName)
                            {
                                model.ClaudeCodeProfileDirs.Add(dirName);
                            }
                        }
                    }

                    if (root["collapsedSettingsSections"] is JsonArray collapsedSections)
                    {
                        foreach (var node in collapsedSections)
                        {
                            // Per entry, not per array: a hand-edited settings.json
                            // with one garbage id — a number, an object — must cost
                            // only that id, not the other twelve, or the catch below
                            // turns one bad line into a full reset of everything
                            // this file holds. JsonValue.TryGetValue returns false
                            // instead of throwing for a non-string scalar, and a
                            // JsonObject/JsonArray entry never matches the pattern
                            // at all, so nothing here needs its own try/catch.
                            if (node is JsonValue value && value.TryGetValue<string>(out var id) &&
                                id.Length > 0)
                            {
                                model.CollapsedSettingsSections.Add(id);
                            }
                        }
                    }

                    if (root["remoteControlProfileDirs"] is JsonArray remoteDirs)
                    {
                        foreach (var node in remoteDirs)
                        {
                            if (node?.GetValue<string>() is { Length: > 0 } dirName)
                            {
                                model.RemoteControlProfileDirs.Add(dirName);
                            }
                        }
                    }

                    if (root["codexHomes"] is JsonArray codexHomes)
                    {
                        foreach (var node in codexHomes)
                        {
                            if (node?.GetValue<string>() is { Length: > 0 } dirName)
                            {
                                model.CodexHomes.Add(dirName);
                            }
                        }
                    }

                    if (root["grokHomes"] is JsonArray grokHomes)
                    {
                        foreach (var node in grokHomes)
                        {
                            if (node?.GetValue<string>() is { Length: > 0 } dirName)
                            {
                                model.GrokHomes.Add(dirName);
                            }
                        }
                    }

                    if (root["newChatRecentFolders"] is JsonArray recentFolders)
                    {
                        foreach (var node in recentFolders)
                        {
                            if (node?.GetValue<string>() is { Length: > 0 } folder)
                            {
                                model.NewChatRecentFolders.Add(folder);
                            }
                        }
                    }

                    if (root["profiles"] is JsonObject profiles)
                    {
                        foreach (var (folder, node) in profiles)
                        {
                            if (node is not JsonObject entry) continue;

                            model.Profiles[folder] = new ProfileSettings
                            {
                                Name = entry["name"]?.GetValue<string>(),
                                Color = entry["color"]?.GetValue<string>(),
                                ShowSwatch = entry["showSwatch"]?.GetValue<bool>() ?? true,
                                TintDockIcon = entry["tintDockIcon"]?.GetValue<bool>() ?? true,
                                TintWindow = entry["tintWindow"]?.GetValue<bool>() ?? true
                            };
                        }
                    }

                    if (root["orbPositions"] is JsonObject positions)
                    {
                        foreach (var (key, node) in positions)
                        {
                            if (node is not JsonObject entry) continue;

                            var x = entry["x"]?.GetValue<int>();
                            var y = entry["y"]?.GetValue<int>();
                            if (x is null || y is null) continue;

                            // Number(), not GetValue: "size" is new with CB-198,
                            // so it gets Number's defence from the start — a bad
                            // one costs the size (read as 1.0), not the spot.
                            model.OrbPositions[key] = new OrbPlacement(x.Value, y.Value, Number(entry["size"]));
                        }
                    }

                    if (root["chatPanelSizes"] is JsonObject panelSizes)
                    {
                        foreach (var (key, node) in panelSizes)
                        {
                            if (node is not JsonObject entry) continue;

                            var w = Number(entry["width"]);
                            var h = Number(entry["height"]);

                            // Either half missing or unreadable drops the
                            // entry, rather than half-restoring a panel to a
                            // width someone chose and a height they didn't.
                            if (w is null || h is null) continue;

                            // Not clamped here. ChatPanel clamps what it reads
                            // against its own Min/Max, which is where those
                            // numbers actually live — and a size saved by a
                            // build with a wider maximum should come back
                            // intact if that build is run again, rather than
                            // being permanently truncated by an older one.
                            model.ChatPanelSizes[key] = new PanelSize(w.Value, h.Value);
                        }
                    }

                    if (root["orbTurnSounds"] is JsonObject turnSounds)
                    {
                        foreach (var (key, node) in turnSounds)
                        {
                            if (node is not JsonObject entry) continue;

                            var finished = Text(entry["finished"]);
                            var attention = Text(entry["attention"]);

                            // Both sides null is not an override worth
                            // keeping — the same rule SetOrbTurnSound applies
                            // on the way in, restated here for a file that
                            // reached this shape by hand rather than through
                            // that accessor.
                            if (finished is null && attention is null) continue;

                            model.OrbTurnSounds[key] = new OrbTurnSound(finished, attention);
                        }
                    }

                    if (root["accountOrbOverrides"] is JsonObject accountOverrides)
                    {
                        foreach (var (key, node) in accountOverrides)
                        {
                            if (node is not JsonObject entry) continue;

                            var parsed = new AccountOrbOverride(
                                Text(entry["initials"]), Text(entry["name"]), Text(entry["iconFile"])).Normalise();

                            // An entry that overrides nothing is dropped, as
                            // SetAccountOrbOverride would have on the way in.
                            if (parsed.IsEmpty) continue;

                            model.AccountOrbOverrides[key] = parsed;
                        }
                    }

                    if (root["orbSizes"] is JsonObject orbSizes)
                    {
                        foreach (var (key, node) in orbSizes)
                        {
                            // Not clamped here, for ChatPanelSizes' reason:
                            // OrbSizing.Effective clamps what it is handed
                            // against this build's range, and a size a build
                            // with a wider range wrote should survive this one.
                            if (Number(node) is double size) model.OrbSizes[key] = size;
                        }
                    }

                    if (root["orbMemberOffsets"] is JsonObject memberOffsets)
                    {
                        foreach (var (key, node) in memberOffsets)
                        {
                            if (node is not JsonObject entry) continue;

                            var dx = Number(entry["dx"]);
                            var dy = Number(entry["dy"]);
                            if (dx is null || dy is null) continue;
                            if (!SaneOffset(dx.Value) || !SaneOffset(dy.Value)) continue;

                            model.OrbMemberOffsets[key] = new MemberOffset(dx.Value, dy.Value);
                        }
                    }

                    if (root["pinnedChatPanels"] is JsonObject pinned)
                    {
                        // Number(), not GetValue<int>() the way orbPositions
                        // above still does — this dictionary is new with
                        // CB-111 rather than a retrofit of a live block (see
                        // Number's own comment on why that distinction
                        // matters), so there is no reason for it to start out
                        // with the same hole: a hand-edited `"x": "wide"`
                        // would otherwise throw out of this loop and be
                        // caught by Load's one catch-all, resetting every
                        // other setting in the file rather than costing only
                        // this one pin.
                        foreach (var (key, node) in pinned)
                        {
                            if (node is not JsonObject entry) continue;

                            var x = Number(entry["x"]);
                            var y = Number(entry["y"]);
                            if (x is null || y is null) continue;

                            model.PinnedChatPanels[key] = new OrbPlacement((int)x.Value, (int)y.Value);
                        }
                    }

                    if (root["arrangeAnchor"] is JsonObject anchor)
                    {
                        var ax = anchor["x"]?.GetValue<int>();
                        var ay = anchor["y"]?.GetValue<int>();
                        if (ax is not null && ay is not null)
                            model.ArrangeAnchor = new OrbPlacement(ax.Value, ay.Value);
                    }

                    // CB-268. After the anchor, so the legacy values are already in
                    // the predefined groups when the block is overlaid on them.
                    model.Groups.ReadJson(root["orbGroups"]);
                    model.GroupOutlinesEnabled = Bool(root["groupOutlinesEnabled"], false);
                    model.GroupNamesVisible = Bool(root["groupNamesVisible"], false);
                    model.GroupOutlineStroke =
                        OrbGroups.NormalizeColor(Text(root["groupOutlineStroke"]))
                        ?? DefaultGroupOutlineStroke;
                    model.GroupOutlineStrokeOpacity =
                        OrbGroups.ClampOpacity(Number(root["groupOutlineStrokeOpacity"]))
                        ?? DefaultGroupOutlineStrokeOpacity;
                    model.GroupOutlineFill =
                        OrbGroups.NormalizeColor(Text(root["groupOutlineFill"]))
                        ?? DefaultGroupOutlineFill;
                    model.GroupOutlineFillOpacity =
                        OrbGroups.ClampOpacity(Number(root["groupOutlineFillOpacity"]))
                        ?? DefaultGroupOutlineFillOpacity;

                    // Anything above this line is a key this build understands.
                    // Whatever is left belongs to a different version and is kept
                    // aside for Save to write back untouched — see _unknownKeys
                    // for why, and note that "version" is deliberately treated as
                    // known so it is not carried around twice.
                    _unknownKeys.Clear();
                    foreach (var (key, node) in root)
                    {
                        if (KnownKeys.Contains(key)) continue;

                        // Detached from the document, because the JsonObject this
                        // came from is about to go out of scope and a JsonNode
                        // cannot belong to two parents.
                        _unknownKeys[key] = node?.DeepClone();
                    }

                    _model = model;
                }
                catch (Exception ex)
                {
                    // A corrupt or half-written settings file must never stop the
                    // app starting; defaults are always a valid answer. The next
                    // Save() overwrites it. Logged for the same reason as Save's
                    // catch above — a silent reset to defaults looks identical
                    // to "nothing was ever saved" otherwise.
                    LogFailure("Load", ex);
                    _model = new Model();
                }
            }
        }

        // A JSON value that is a non-empty string, or null.
        //
        // Being defensive per field is not optional here. Load() sits inside one
        // catch that replaces the *entire* model with defaults, so `"idle": 5`
        // reaching GetValue<string>() — which throws on a type mismatch — would
        // cost someone their profile names and every dragged orb position over
        // one typo in a colour. JsonValue.TryGetValue never throws.
        //
        // Deliberately doesn't check that the string is a *colour*: that's
        // OrbColors' job, since it holds the default to fall back to.
        // Sorted, so the file does not reorder itself from one save to the
        // next depending on which CLI was picked first.
        private static JsonObject NewChatLastProfilesNode(Dictionary<string, string> profiles)
        {
            var node = new JsonObject();
            foreach (var (cliName, dir) in profiles.OrderBy(p => p.Key, StringComparer.Ordinal)) node[cliName] = dir;
            return node;
        }

        private static string? Text(JsonNode? node) =>
            node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;

        // One of the three cluster modes, and the migration off the boolean that
        // used to hold two of them.
        //
        // `legacy` is the old `openclawShowHeartbeats` node, consulted only when
        // the new key is absent — so an install that has been through this
        // version keeps whatever it chose, and one that has not gets exactly the
        // behaviour it had. false becomes Hidden and true becomes WithChats,
        // which is the whole of what the boolean could say.
        //
        // Read through Text/TryGetValue rather than GetValue for the reason
        // Text() states: a hand-edited `"openclawShowHeartbeats": "no"` reaching
        // GetValue<bool>() would throw into the catch that replaces the entire
        // model with defaults, and cost someone every profile name and dragged
        // orb position in the file over one typo about heartbeats.
        private static ClusterMode ClusterModeFrom(JsonNode? node, JsonNode? legacy)
        {
            if (Text(node) is { } named) return OrbClusters.Parse(named);

            if (legacy is JsonValue value && value.TryGetValue<bool>(out var shown))
                return shown ? ClusterMode.WithChats : ClusterMode.Hidden;

            return ClusterMode.WithChats;
        }

        // An arrangement shape name, falling back to the group's default.
        //
        // Deliberately doesn't check the name is one of the six, the same way
        // Text() doesn't check a colour is a colour: OrbArrangement.Unit owns
        // that question and already answers an unrecognised shape with the heart,
        // and SettingsWindow's picker shows an unknown value as its own entry
        // rather than silently swapping it for something else.
        private static string Shape(JsonNode? node, string fallback) =>
            Text(node) ?? fallback;

        // The same defence as Text() above, for a number.
        //
        // Written when chatPanelSizes went in, because that block reads two
        // numbers per entry and GetValue<double>() throws on a type mismatch
        // exactly the way GetValue<string>() does — so a hand-edited
        // `"width": "wide"` would have been thrown at the one catch that
        // replaces the whole model, and cost someone every profile name and
        // dragged orb position in the file over one bad panel size. A garbage
        // size should cost that one panel's size and nothing else.
        //
        // Not retrofitted onto the orbPositions block above, which still reads
        // through GetValue<int>(). That is a real hole of the same shape, but
        // it is pre-existing behaviour the README documents, and quietly
        // changing how a live settings file is parsed is not this change's
        // business — it belongs in its own commit that can be reviewed as
        // such.
        private static double? Number(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<double>(out var number)
                ? number
                : null;

        // The same defence again, for a bool — written for turnSoundsEnabled
        // specifically (QA, CB-167). `root["turnSoundsEnabled"]?.GetValue
        // <bool>()` reaching a hand-edited `"turnSoundsEnabled": "yes"` (or
        // any non-bool JSON value) throws, and that throw lands in Load's one
        // catch that replaces the *entire* model with defaults — a brand new
        // key costing someone every profile name and dragged orb position
        // over one bad value is exactly the hole Text() and Number() above
        // were already written to close for every other type. A fallback
        // rather than null because every other bool setting in this file
        // defaults to a concrete value too, and turnSoundsEnabled's is true.
        private static bool Bool(JsonNode? node, bool fallback) =>
            node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;

        // Test seam: this class is static, so it caches _model and _loaded for
        // the life of the process. A test that points ORBWEAVER_SETTINGS_DIR
        // at a fresh directory between cases still needs this to make that
        // directory actually get read again instead of the previous case's
        // cached model. Not for anything else — production code never needs to
        // re-read a settings.json that changed out from under it.
        internal static void ReloadForTests()
        {
            lock (Gate)
            {
                _deferred?.Stop();
                _model = new Model();
                _unknownKeys.Clear();
                _loaded = false;
            }

            Load();
        }

        private static void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                JsonObject root;
                lock (Gate)
                {
                    var profiles = new JsonObject();
                    foreach (var (folder, entry) in _model.Profiles)
                    {
                        profiles[folder] = new JsonObject
                        {
                            ["name"] = entry.Name,
                            ["color"] = entry.Color,
                            ["showSwatch"] = entry.ShowSwatch,
                            ["tintDockIcon"] = entry.TintDockIcon,
                            ["tintWindow"] = entry.TintWindow
                        };
                    }

                    var positions = new JsonObject();
                    foreach (var (key, placement) in _model.OrbPositions)
                    {
                        var entry = new JsonObject
                        {
                            ["x"] = placement.X,
                            ["y"] = placement.Y
                        };

                        // Only when known, so a spot saved before CB-198 is
                        // written back exactly as it was read. An older build
                        // reading this file takes x and y by name and never
                        // looks at "size".
                        if (placement.Size is double size) entry["size"] = size;

                        positions[key] = entry;
                    }

                    var panelSizes = new JsonObject();
                    foreach (var (key, size) in _model.ChatPanelSizes)
                    {
                        panelSizes[key] = new JsonObject
                        {
                            ["width"] = size.Width,
                            ["height"] = size.Height
                        };
                    }

                    var turnSounds = new JsonObject();
                    foreach (var (key, sound) in _model.OrbTurnSounds)
                    {
                        turnSounds[key] = new JsonObject
                        {
                            ["finished"] = sound.Finished,
                            ["attention"] = sound.Attention
                        };
                    }

                    var accountOverrides = new JsonObject();
                    foreach (var (key, o) in _model.AccountOrbOverrides)
                    {
                        accountOverrides[key] = new JsonObject
                        {
                            ["initials"] = o.Initials,
                            ["name"] = o.Name,
                            ["iconFile"] = o.IconFile
                        };
                    }

                    var orbSizes = new JsonObject();
                    foreach (var (key, size) in _model.OrbSizes) orbSizes[key] = size;

                    var orbMemberOffsets = new JsonObject();
                    foreach (var (key, offset) in _model.OrbMemberOffsets)
                        orbMemberOffsets[key] = new JsonObject { ["dx"] = offset.Dx, ["dy"] = offset.Dy };

                    var pinnedChatPanels = new JsonObject();
                    foreach (var (key, placement) in _model.PinnedChatPanels)
                    {
                        pinnedChatPanels[key] = new JsonObject
                        {
                            ["x"] = placement.X,
                            ["y"] = placement.Y
                        };
                    }

                    var arrangeAnchor = _model.ArrangeAnchor is { } anchor
                        ? new JsonObject { ["x"] = anchor.X, ["y"] = anchor.Y }
                        : null;

                    var profileDirs = new JsonArray();
                    foreach (var dirName in _model.ClaudeCodeProfileDirs) profileDirs.Add(dirName);

                    var collapsedSections = new JsonArray();
                    foreach (var id in _model.CollapsedSettingsSections) collapsedSections.Add(id);

                    var codexHomeDirs = new JsonArray();
                    foreach (var dirName in _model.CodexHomes) codexHomeDirs.Add(dirName);

                    var grokHomeDirs = new JsonArray();
                    foreach (var dirName in _model.GrokHomes) grokHomeDirs.Add(dirName);

                    var newChatRecentFolders = new JsonArray();
                    foreach (var folder in _model.NewChatRecentFolders) newChatRecentFolders.Add(folder);

                    var speakArgs = new JsonArray();
                    foreach (var argument in _model.SpeakCommandArgs) speakArgs.Add(argument);

                    var remoteProfileDirs = new JsonArray();
                    foreach (var dir in _model.RemoteControlProfileDirs) remoteProfileDirs.Add(dir);

                    var voicesArgs = new JsonArray();
                    foreach (var argument in _model.SpeakVoicesCommandArgs) voicesArgs.Add(argument);

                    root = new JsonObject
                    {
                        ["version"] = CurrentVersion,
                        ["showOrbs"] = _model.ShowOrbs,
                        ["showUsageOrbs"] = _model.ShowUsageOrbs,
                        ["tintActiveWindow"] = _model.TintActiveWindow,
                        ["routeClaudeUrls"] = _model.RouteClaudeUrls,
                        ["previousClaudeUrlHandler"] = _model.PreviousClaudeUrlHandler,
                        ["orbLifetimeMinutes"] = _model.OrbLifetimeMinutes,
                        ["voiceInputEnabled"] = _model.VoiceInputEnabled,
                        ["openclawEnabled"] = _model.OpenClawEnabled,
                        ["claudeCloudEnabled"] = _model.ClaudeCloudEnabled,
                        ["openclawHost"] = _model.OpenClawHost,
                        ["openclawPort"] = _model.OpenClawPort,
                        ["openclawFingerprint"] = _model.OpenClawFingerprint,
                        ["openclawReplyEnabled"] = _model.OpenClawReplyEnabled,
                        ["openclawApproveEnabled"] = _model.OpenClawApproveEnabled,
                        ["openclawActiveWithinMinutes"] = _model.OpenClawActiveWithinMinutes,
                        // Still written, and derived rather than stored: an older
                        // build reading this file has no idea what
                        // openclawHeartbeatMode means, and the one thing it can
                        // still honour is whether the user wanted these orbs at
                        // all. Dropping the key would have that build show them
                        // again, which is the downgrade _unknownKeys exists to
                        // prevent for keys it has never heard of and this line
                        // does for one it has.
                        ["openclawShowHeartbeats"] =
                            _model.OpenClawHeartbeatMode != ClusterMode.Hidden,
                        ["openclawHeartbeatMode"] = OrbClusters.Name(_model.OpenClawHeartbeatMode),
                        ["openclawHeartbeatShape"] = _model.OpenClawHeartbeatShape,
                        ["openclawCronMode"] = OrbClusters.Name(_model.OpenClawCronMode),
                        ["openclawCronShape"] = _model.OpenClawCronShape,
                        ["openclawNativeMode"] = OrbClusters.Name(_model.OpenClawNativeMode),
                        ["openclawNativeShape"] = _model.OpenClawNativeShape,
                        ["remoteControlEnabled"] = _model.RemoteControlEnabled,
                        ["peerLinkEnabled"] = _model.PeerLinkEnabled,
                        ["peerLinkPort"] = _model.PeerLinkPort,
                        ["toggleOrbsHotkey"] = _model.ToggleOrbsHotkey,
                        ["newChatHotkey"] = _model.NewChatHotkey,
                        ["toggleUsageOrbsHotkey"] = _model.ToggleUsageOrbsHotkey,
                        ["toggleMuteHotkey"] = _model.ToggleMuteHotkey,
                        // Null when never chosen rather than a copy of the
                        // current default, the same as speakVoice below — so
                        // changing which profile ships as the default still
                        // reaches everyone who never picked one.
                        ["personaPoolDir"] = _model.PersonaPoolDir,
                        ["remoteControlProfileDir"] = _model.RemoteControlProfileDir,
                        ["remoteControlProfileDirs"] = remoteProfileDirs,
                        ["remoteControlIdleMinutes"] = _model.RemoteControlIdleMinutes,
                        ["remoteControlServeOnLaunch"] = _model.RemoteControlServeOnLaunch,
                        ["claudeCodeChatEnabled"] = _model.ClaudeCodeChatEnabled,
                        ["claudeCodeReplyEnabled"] = _model.ClaudeCodeReplyEnabled,
                        ["codexChatEnabled"] = _model.CodexChatEnabled,
                        ["codexReplyEnabled"] = _model.CodexReplyEnabled,
                        ["codexAccountUsageEnabled"] = _model.CodexAccountUsageEnabled,
                        ["autoColorSessions"] = _model.AutoColorSessions,
                        ["peerInboundNoticeSeen"] = _model.PeerInboundNoticeSeen,
                        ["claudeCodeEnabled"] = _model.ClaudeCodeEnabled,
                        ["codexEnabled"] = _model.CodexEnabled,
                        ["grokChatEnabled"] = _model.GrokChatEnabled,
                        ["grokReplyEnabled"] = _model.GrokReplyEnabled,
                        ["grokEnabled"] = _model.GrokEnabled,
                        ["grokAccountUsageEnabled"] = _model.GrokAccountUsageEnabled,
                        ["grokAutoRefreshEnabled"] = _model.GrokAutoRefreshEnabled,
                        ["clickAction"] = _model.ClickAction,
                        ["doubleClickAction"] = _model.DoubleClickAction,
                        ["tripleClickAction"] = _model.TripleClickAction,
                        ["twoLetterGlyphs"] = _model.TwoLetterGlyphs,
                        ["accountUsageEnabled"] = _model.AccountUsageEnabled,
                        ["arrangeShape"] = _model.ArrangeShape,
                        ["arrangeSpacing"] = _model.ArrangeSpacing,
                        ["chatTextScale"] = _model.ChatTextScale,
                        ["orbSize"] = _model.OrbSize,
                        ["speechVolume"] = _model.SpeechVolume,
                        ["alertVolume"] = _model.AlertVolume,
                        // Null when never chosen, like the colours below rather
                        // than a copy of the current default — so changing which
                        // voice ships as the default still reaches everyone who
                        // never picked one.
                        ["speakVoice"] = _model.SpeakVoice,
                        ["neuralVoiceEnabled"] = _model.NeuralVoiceEnabled,
                        ["neuralVoice"] = _model.NeuralVoice,
                        ["speakCommand"] = _model.SpeakCommand,
                        ["speakCommandArgs"] = speakArgs,
                        ["speakVoicesCommand"] = _model.SpeakVoicesCommand,
                        ["speakVoicesCommandArgs"] = voicesArgs,
                        ["speakCommandVoice"] = _model.SpeakCommandVoice,
                        ["speakEngine"] = _model.SpeakEngine,
                        ["speakScope"] = _model.SpeakScope,
                        ["turnSoundsEnabled"] = _model.TurnSoundsEnabled,
                        ["muted"] = _model.Muted,
                        ["turnFinishedSound"] = _model.TurnFinishedSound,
                        ["needsAttentionSound"] = _model.NeedsAttentionSound,
                        ["newChatRecentFolders"] = newChatRecentFolders,
                        ["newChatLastCli"] = _model.NewChatLastCli,
                        // Still written, from the map's Claude Code entry, so
                        // a build from before CB-203 reading this file keeps
                        // its remembered account.
                        ["newChatLastProfile"] = _model.NewChatLastProfiles.TryGetValue(
                            nameof(NewChatCli.ClaudeCode), out var claudeLastProfile) ? claudeLastProfile : null,
                        ["newChatLastProfiles"] = NewChatLastProfilesNode(_model.NewChatLastProfiles),
                        // Grouped rather than three top-level keys: it reads as
                        // one setting in the file the way it reads as one card in
                        // the window. A null entry — which is what a colour left
                        // at its default writes — is the same shape the profile
                        // entries already use for "not set".
                        ["orbColors"] = new JsonObject
                        {
                            ["idle"] = _model.IdleColor,
                            ["generating"] = _model.GeneratingColor,
                            ["waiting"] = _model.WaitingColor
                        },
                        ["claudeCodeProfileDirs"] = profileDirs,
                        ["collapsedSettingsSections"] = collapsedSections,
                        ["codexHomes"] = codexHomeDirs,
                        ["grokHomes"] = grokHomeDirs,
                        ["profiles"] = profiles,
                        ["orbPositions"] = positions,
                        ["chatPanelSizes"] = panelSizes,
                        ["orbTurnSounds"] = turnSounds,
                        ["orbSizes"] = orbSizes,
                        ["orbMemberOffsets"] = orbMemberOffsets,
                        ["accountOrbOverrides"] = accountOverrides,
                        ["pinnedChatPanels"] = pinnedChatPanels,
                        ["arrangeAnchor"] = arrangeAnchor,
                        ["orbGroups"] = _model.Groups.ToJson(),
                        ["groupOutlinesEnabled"] = _model.GroupOutlinesEnabled,
                        ["groupNamesVisible"] = _model.GroupNamesVisible,
                        ["groupOutlineStroke"] = _model.GroupOutlineStroke,
                        ["groupOutlineStrokeOpacity"] = _model.GroupOutlineStrokeOpacity,
                        ["groupOutlineFill"] = _model.GroupOutlineFill,
                        ["groupOutlineFillOpacity"] = _model.GroupOutlineFillOpacity
                    };

                    // Keys this build doesn't understand, put back exactly as they
                    // were found. Without this, saving here *deletes* every
                    // setting a newer version added — see _unknownKeys. Added last
                    // so a key that becomes known later can never be written
                    // twice: the guard below keeps the two sources from colliding
                    // if KnownKeys and this object ever drift apart.
                    foreach (var (key, node) in _unknownKeys)
                    {
                        if (root.ContainsKey(key)) continue;
                        root[key] = node?.DeepClone();
                    }
                }

                // Write beside the target and rename over it, so a crash midway
                // can't leave an unparseable settings file. UTF-8 without a BOM:
                // System.Text.Json treats a leading BOM as an invalid start of
                // value, and this file is read back by JsonNode.
                var temporary = Path_ + ".tmp";
                File.WriteAllText(
                    temporary,
                    root.ToJsonString(SaveOptions),
                    new UTF8Encoding(false));
                File.Move(temporary, Path_, overwrite: true);
            }
            catch (Exception ex)
            {
                // Losing a preference is not worth taking the app down for,
                // but silently losing it with zero trace is exactly the "no
                // error, just doesn't work" trap this project's own hook
                // script goes out of its way to avoid elsewhere (see
                // OrbweaverHook.ps1's header comment) — so leave a
                // breadcrumb somewhere known-writable rather than nothing.
                // Added after hitting a real machine where this path
                // silently failed on every single attempt, with no way to
                // tell why short of adding this.
                LogFailure("Save", ex);
            }
        }

        // Excluded from coverage: writes to a log file in the temp directory, and
        // its catch is the last stop — getting there means the directory could not
        // be created AND the append failed, on a machine where writing
        // settings.json had already failed. That nothing can be reported at that
        // point is the whole design, and it is not a state a test can produce.
        //
        // That the log IS written on an ordinary failure is covered from the
        // outside, in SettingsListParsingTests and SettingsDeferredWriteTests,
        // which read the file back and assert it grew.
        [ExcludeFromCodeCoverage]
        private static void LogFailure(string what, Exception ex)
        {
            try
            {
                var dir = StatusDirectory.Path();
                System.IO.Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "settings-errors.log"),
                    $"{DateTime.Now:O} {what} failed: {ex}{Environment.NewLine}");
            }
            catch
            {
                // If even this fails, there's nowhere left to report it.
                //
                // Unreached, and unreachable in any useful sense: getting here
                // means the temp directory could not be created AND the append
                // failed, on a machine where writing settings.json had already
                // failed. Kept as the last stop rather than deleted, and named
                // here so a coverage report does not read as a missing test.
            }
        }

        // ---- deferred write -------------------------------------------------

        // The colour pickers raise ColorChanged on every pointer move across the
        // spectrum rather than once on commit, and ColorPicker's drop down lives
        // inside its own template so there is no "closed" event to commit at
        // instead. An auto-saving setter would therefore rebuild, write and
        // rename settings.json for as long as someone drags — hundreds of temp
        // files and renames to land one preference, each of them rewriting every
        // profile and every orb position too.
        //
        // Only the file write waits. The in-memory model is updated at once and
        // everything reads through that (OrbColors -> the orbs and the tray
        // icon), so the live preview is unaffected.
        //
        // A DispatcherTimer rather than a threadpool one on purpose: Save()
        // writes a temp file and renames it over the target, and two of those in
        // flight end with the second rename failing on a file the first already
        // moved — silently, inside the catch above. Every other Save() in this
        // class happens on the UI thread, so this one does too and the question
        // doesn't arise.
        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(250);
        private static DispatcherTimer? _deferred;

        // Excluded from coverage: exists to be the try/catch, and the catch is
        // not reachable — which the tests say out loud rather than leaving as a
        // mystery in a report. An Avalonia DispatcherTimer constructs and starts
        // quite happily in a process with no dispatcher loop running, so nothing
        // throws here and the write is simply deferred to a tick that never
        // comes. See SettingsDeferredWriteTests, which asserts that behaviour
        // rather than the behaviour the comment here used to promise.
        //
        // Kept because "no dispatcher at all" is a real shape for this class —
        // it is a process-wide static that a console tool could load — and
        // losing a preference is a worse outcome than an unreachable line.
        [ExcludeFromCodeCoverage]
        private static void SaveSoon()
        {
            try { RestartTheDeferredWrite(); }
            catch { Save(); }
        }

        private static void RestartTheDeferredWrite()
        {
            if (_deferred is null)
            {
                _deferred = new DispatcherTimer { Interval = SaveDelay };
                _deferred.Tick += OnDeferredTick;
            }

            // Restart rather than let it run out: keep pushing the write
            // further off for as long as changes keep arriving.
            _deferred.Stop();
            _deferred.Start();
        }

        // Excluded from coverage: fires only when the debounce interval actually
        // elapses, and no test waits on a real timer — this branch has fixed five
        // flakes of exactly that shape. What it does when it fires is Save(),
        // which is covered every other way, and FlushPendingSave is the seam the
        // app itself uses from anything that might be the last thing to happen.
        [ExcludeFromCodeCoverage]
        private static void OnDeferredTick(object? sender, EventArgs e)
        {
            _deferred!.Stop();
            Save();
        }

        // A deferred write that never happens is a preference silently lost, so
        // anything that might be the last thing to happen calls this.
        public static void FlushPendingSave()
        {
            if (_deferred is null || !_deferred.IsEnabled) return;

            _deferred.Stop();
            Save();
        }
    }
}
