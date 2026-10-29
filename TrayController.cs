using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Orbweaver
{
    // The menu-bar / notification-area presence: one status item whose icon
    // reflects the most urgent state across all sessions (waiting beats
    // generating beats idle), plus a menu that lists the live sessions and
    // lets you jump to any of their terminals without hunting for its orb.
    //
    // This is also the app's only permanent, always-there control surface —
    // there's no Dock icon and no window when nothing is running, so "Quit"
    // and the orb visibility toggle live here rather than only on an orb's
    // right-click menu (which is unreachable when there are zero orbs).
    internal sealed class TrayController
    {
        private readonly TrayIcon _tray;

        // One menu instance for the app's lifetime, repopulated in place.
        // Assigning a *new* NativeMenu to an already-exported TrayIcon throws
        // on macOS ("The menu being updated does not match") — Avalonia's
        // native exporter caches the menu it was handed and only tracks
        // changes to that same object's Items.
        private readonly NativeMenu _menu = new();

        // Keyed by asset name (TrayIconLook.AssetName), not by colour: there are
        // only ever six live entries — three states, muted or not — and a colour
        // change clears the lot (see ReapplyStateColors).
        //
        // This cache is what keeps the re-tint's pixel loop off the 2s tick.
        // Apply() is signature-gated, Rebuild calls UpdateIcon, and UpdateIcon
        // asks LoadIcon every time — so remove either the gate or this cache and
        // three PNGs start getting decoded and recoloured twice a second.
        private readonly Dictionary<string, WindowIcon> _iconCache = new();

        // Rebuilding a NativeMenu is visible on macOS (it can dismiss an open
        // menu), and ScanAndUpdate runs every 2s, so only touch the menu when
        // something a user could actually see has changed.
        private string _lastSignature = "";

        // The Claude Desktop section refreshes off its own background probe, so
        // it needs a way back in that doesn't involve SessionManager.
        internal static TrayController? Instance { get; private set; }

        private IReadOnlyList<SessionEntry> _lastSessions = Array.Empty<SessionEntry>();

        // Rebuild() clears Items, which on macOS dismisses a menu that's
        // currently open — and people linger over submenus. Hold changes until
        // it closes rather than yanking the menu out from under the pointer.
        //
        // The deadline is a safety valve, not a feature: if the platform ever
        // raises Opening without a matching Closed, the menu would otherwise
        // freeze permanently. A menu genuinely left open this long is rare, and
        // a rebuild under the pointer beats a menu that stops updating.
        private static readonly TimeSpan MenuOpenDeadline = TimeSpan.FromSeconds(60);

        private bool _menuOpen;
        private bool _pendingRebuild;
        private long _menuOpenedAt;

        public TrayController()
        {
            Instance = this;

            _tray = new TrayIcon
            {
                Icon = LoadIcon("idle", AudioMute.IsMuted),
                ToolTipText = Brand.DisplayName,
                IsVisible = true,
                Menu = _menu
            };

            // A mute from the hotkey or a chat panel has to reach the menu's
            // checkbox and the icon too. Refresh is the existing way in, so the
            // open-menu hold-back still applies to the menu — the icon, set
            // before it, flips regardless (CB-298).
            AudioMute.Changed += _ => Refresh();

            _menu.Opening += (_, _) =>
            {
                _menuOpen = true;
                _menuOpenedAt = Environment.TickCount64;
                ClaudeDesktopManager.KickRefresh();
            };

            _menu.Closed += (_, _) =>
            {
                _menuOpen = false;
                if (!_pendingRebuild) return;

                _pendingRebuild = false;
                Refresh();
            };

            if (Application.Current is { } app)
            {
                TrayIcon.SetIcons(app, new TrayIcons { _tray });
            }

            // Tints the frontmost Claude Desktop window in its profile's colour.
            // Owns its own timer; nothing else here depends on it.
            ClaudeDesktopOverlay.Start();

            Rebuild(Array.Empty<SessionEntry>());
        }

        public readonly record struct SessionEntry(string SessionId, SessionStatus Status);

        public void Update(IReadOnlyList<SessionEntry> sessions)
        {
            _lastSessions = sessions;

            // The 2s poll is what keeps the Claude Desktop section honest: the
            // probe runs off the UI thread and only calls back when its digest
            // changes, so the menu-open hook below is an improvement on
            // latency, not something the section depends on for correctness.
            ClaudeDesktopManager.KickRefresh();

            // Which Claude Desktop instance the user is in, sampled on the same
            // tick. The URL router needs it to answer "which profile asked for
            // this sign-in callback" once the browser has taken focus and the
            // frontmost app is no longer Claude at all.
            ClaudeDesktopUrlRouter.NoteFrontmost();

            Apply(sessions);
        }

        // Re-render from the last session list we were handed.
        // ClaudeDesktopManager posts this to the UI thread when its snapshot
        // changes; it deliberately doesn't kick another probe, which would loop.
        internal void Refresh() => Apply(_lastSessions);

        private void Apply(IReadOnlyList<SessionEntry> sessions)
        {
            var signature = string.Join("|",
                                sessions.Select(s => $"{s.SessionId}:{s.Status.State}:{s.Status.Cwd}:{s.Status.Title}"))
                            + $"|orbs={SessionManager.Instance?.OrbsVisible}"
                            + $"|usage={SessionManager.Instance?.UsageOrbsVisible}"
                            + $"|muted={AudioMute.IsMuted}"
                            + $"|{ClaudeDesktopManager.Digest()}";
            if (signature == _lastSignature) return;

            // The icon is the urgent half of this — it goes amber when a
            // session needs you — and changing it doesn't disturb an open menu,
            // so it's never held back.
            UpdateIcon(sessions);

            // The menu is. Leave _lastSignature stale on purpose so the change
            // isn't lost; Closed replays it.
            if (_menuOpen && Environment.TickCount64 - _menuOpenedAt < MenuOpenDeadline.TotalMilliseconds)
            {
                _pendingRebuild = true;
                return;
            }

            _menuOpen = false;
            _pendingRebuild = false;

            _lastSignature = signature;

            Rebuild(sessions);
        }

        private void UpdateIcon(IReadOnlyList<SessionEntry> sessions)
        {
            var waiting = sessions.Count(s => s.Status.State == "waiting");
            var generating = sessions.Count(s => s.Status.State == "generating");

            var muted = AudioMute.IsMuted;
            _tray.Icon = LoadIcon(TrayIconLook.State(waiting, generating), muted);
            _tray.ToolTipText = Summary(sessions.Count, waiting, generating, muted);
        }

        // A colour change doesn't change the session list, so Apply's signature is
        // unchanged and nothing would repaint. Drop the cached icons and set one
        // again directly — the same explicit-kick shape as
        // ClaudeDesktopManager.KickRefresh.
        //
        // UpdateIcon assigns the icon unconditionally, so this repaints even with
        // zero sessions. That matters: with nothing running, the menu-bar icon is
        // the only live preview of the idle colour.
        internal void ReapplyStateColors()
        {
            _iconCache.Clear();
            UpdateIcon(_lastSessions);
        }

        private void Rebuild(IReadOnlyList<SessionEntry> sessions)
        {
            UpdateIcon(sessions);

            var menu = _menu;
            menu.Items.Clear();

            if (sessions.Count == 0)
            {
                // Just "No sessions", for everyone.
                //
                // This used to name Claude Code unless the OpenClaw feature was
                // on, on the reasoning that naming the only kind of session
                // that could exist is more helpful than a generic phrase. Codex
                // retires that reasoning: it has no enable switch to test —
                // wiring its hooks is the whole of turning it on, and that
                // happens in Codex's config where this app cannot see it. So
                // there is no longer any state in which "no Claude Code
                // sessions" is known to be the whole truth, and naming one CLI
                // while another is quietly running reads as a bug in the other.
                menu.Add(new NativeMenuItem("No sessions") { IsEnabled = false });
            }
            else
            {
                // Two sessions that resolve to the same name would otherwise
                // produce identical menu entries, which is worse than useless —
                // you can't tell which terminal a click will take you to.
                var ambiguous = sessions.GroupBy(DisplayName)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToHashSet();

                foreach (var session in sessions)
                {
                    var item = new NativeMenuItem(SessionLabel(session, ambiguous.Contains(DisplayName(session))));
                    var status = session.Status;
                    var id = session.SessionId;
                    item.Click += (_, _) => TerminalFocuser.Focus(status, null, id);
                    menu.Add(item);
                }
            }

            ClaudeDesktopSection.Append(menu);

            menu.Add(new NativeMenuItemSeparator());

            var orbsItem = new NativeMenuItem("Show orbs")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = SessionManager.Instance?.OrbsVisible ?? true
            };
            orbsItem.Click += (_, _) => ToggleOrbsVisible();
            menu.Add(orbsItem);

            // Beside "Show orbs" rather than under Settings, because it is the
            // same kind of switch: the account orbs on their own (CB-220),
            // which "Show orbs" still turns off along with everything else.
            var usageOrbsItem = new NativeMenuItem("Show usage orbs")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = SessionManager.Instance?.UsageOrbsVisible ?? true
            };
            usageOrbsItem.Click += (_, _) => ToggleUsageOrbsVisible();
            menu.Add(usageOrbsItem);

            // The same switch as the hotkey and the chat panel's speaker button
            // (CB-298): one flag, three surfaces, all through ToggleMute.
            var muteItem = new NativeMenuItem("Mute")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = AudioMute.IsMuted
            };
            muteItem.Click += (_, _) => ToggleMute();
            menu.Add(muteItem);

            var resetItem = new NativeMenuItem("Reset all sessions to idle")
            {
                IsEnabled = sessions.Count > 0
            };
            resetItem.Click += (_, _) => ResetAllSessions();
            menu.Add(resetItem);

            // The on-demand trigger for sessions on other machines.
            //
            // In the tray rather than as a new window or an orb affordance
            // because it is a verb the app performs once, not a thing to look
            // at — and because there is nowhere else for it to live: the orbs
            // it produces do not exist until it runs, so it cannot hang off one
            // of them. Reaching for the relay is exactly the kind of
            // occasionally-needed action this menu is already full of.
            //
            // Hidden entirely unless the feature is on, so the menu does not
            // grow a line about machines for the majority who have not asked
            // for any.
            if (OrbweaverSettings.PeerLinkEnabled)
            {
                var remoteItem = new NativeMenuItem("Connect to other machines");
                remoteItem.Click += (_, _) => ConnectToOtherMachines();
                menu.Add(remoteItem);
            }

            menu.Add(new NativeMenuItemSeparator());

            var newChatItem = new NativeMenuItem("New chat…");
            newChatItem.Click += (_, _) => OpenNewChat();
            menu.Add(newChatItem);

            var settingsItem = new NativeMenuItem("Settings…");
            settingsItem.Click += (_, _) => OpenSettings();
            menu.Add(settingsItem);

            var quitItem = new NativeMenuItem($"Quit {Brand.DisplayName}");
            quitItem.Click += (_, _) => QuitApp();
            menu.Add(quitItem);
        }

        // The five things the tray menu can do, as named methods rather than the
        // lambdas they used to be — so each can be judged on its own rather than
        // the whole menu being written off. TrayMenuTests' own header records the
        // standing decision not to invoke Click handlers, and it was right about
        // three of the five.

        // Safe to call: SessionManager.Instance is null outside the running app,
        // so this is a no-op there — which is exactly what a tray item should be
        // when there is nothing to show.
        internal static void ToggleOrbsVisible() =>
            SessionManager.Instance?.SetOrbsVisible(!SessionManager.Instance.OrbsVisible);

        // Safe to call for the same reason as ToggleOrbsVisible.
        internal static void ToggleUsageOrbsVisible() =>
            SessionManager.Instance?.SetUsageOrbsVisible(!SessionManager.Instance.UsageOrbsVisible);

        // The hotkey and the tray item both land here, as do the chat panel's
        // button and anything else that mutes (CB-298). Unlike the toggles
        // above it needs no SessionManager, so it works — and is tested — in
        // a headless run.
        internal static void ToggleMute() => AudioMute.Toggle();

        internal static void ResetAllSessions() =>
            SessionManager.Instance?.ResetAllSessionsToIdle();

        // Safe to call: under the headless lifetime Application.Current's lifetime
        // is never IClassicDesktopStyleApplicationLifetime, so the guard is false
        // and Shutdown is never reached. That guard is the point — it is what
        // keeps a Quit item from taking down a host that is not a desktop app.
        //
        // Excluded from coverage for the half a headless run cannot reach, which
        // is the half that ends the process. The guard itself is
        // OrbWindow.IsDesktopLifetime, asked there because the two Quit paths in
        // this app have to agree about it, and asserted there.
        [ExcludeFromCodeCoverage]
        internal static void QuitApp()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Shutdown(desktop);
            }
        }

        // Excluded from coverage: ends the process. Scoped to this one call so the
        // guard in front of it — the thing that stops a Quit item taking down a
        // host that is not a desktop app — stays measured, and is the only part a
        // headless run can reach anyway.
        [ExcludeFromCodeCoverage]
        private static void Shutdown(IClassicDesktopStyleApplicationLifetime desktop)
        {
            GlobalHotkeys.Stop();

            // QA (CB-167) round 3, finding 5: the ChimePlayer.StopAll call
            // that used to live here moved to App.OnFrameworkInitializationCompleted's
            // desktop.Exit handler, because this Shutdown was only ever the
            // tray menu's own Quit — the orb menu's "Exit Orbweaver" and
            // the OS's Cmd-Q both call desktop.Shutdown() directly and never
            // ran through here, so either one used to leave a chime
            // orphaned. Exit fires for every path that ends in
            // desktop.Shutdown(), this one included, so nothing is lost by
            // no longer calling it from this one call site specifically.
            desktop.Shutdown();
        }

        // Excluded from coverage: SettingsWindow.Toggle puts the app in the Dock
        // via MacOSActivation.SetRegular, shows a window and takes it key, then
        // starts a one-second status ticker. Its own exclusion says the same; this
        // is the call site, and calling it would do all of that for real.
        [ExcludeFromCodeCoverage]
        internal static void OpenSettings() => SettingsWindow.Toggle();

        // The tray item and the OpenNewChat global hotkey both land here, so
        // the two can't disagree about what "New chat" means. No longer
        // excluded from coverage the way OpenSettings still is:
        // NewChatWindow.Toggle's OS-facing halves have test seams now, so
        // HotkeyActionsTests calls this for real with nothing shown.
        internal static void OpenNewChat() => NewChatWindow.Toggle();

        // Excluded from coverage: starts a relay, which is a live Claude Code
        // session in a tmux pane on another machine — and that costs the person
        // running the tests real money. TrayRemoteItemTests records the same thing
        // from the other side, which is why it checks the item exists and stops
        // there.
        [ExcludeFromCodeCoverage]
        // Nothing to start on demand any more: the link is either listening or
        // it is not, and turning it on is a switch in Settings rather than an
        // act with a cost. Kept as the menu item's handler so the item still
        // does something useful — it retries the machines we are paired with
        // rather than waiting out the next ten-second tick.
        internal static void ConnectToOtherMachines() => PeerSessions.Restart();

        // A mute left on is readable from the hover text too, not only from
        // the slash on the icon (CB-298).
        internal static string Summary(int total, int waiting, int generating, bool muted = false)
        {
            var muteSuffix = muted ? ", muted" : "";
            if (total == 0) return $"{Brand.DisplayName} — no sessions{muteSuffix}";

            var parts = new List<string> { total == 1 ? "1 session" : $"{total} sessions" };
            if (waiting > 0) parts.Add($"{waiting} needs you");
            if (generating > 0) parts.Add($"{generating} working");
            return $"{Brand.DisplayName} — " + string.Join(", ", parts) + muteSuffix;
        }

        // An agent's name within its team if it has one, else the chat name if
        // Claude Code has named the session, else its folder. The agent name
        // comes first because a team's members all inherit the team session's
        // title, and four identical rows only differed by the id this menu
        // appends when it can't tell them apart.
        internal static string DisplayName(SessionEntry session)
        {
            if (!string.IsNullOrEmpty(session.Status.Agent)) return session.Status.Agent;
            if (!string.IsNullOrEmpty(session.Status.Title)) return session.Status.Title;

            var cwd = session.Status.Cwd;
            if (string.IsNullOrEmpty(cwd)) return session.SessionId;

            var folder = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(folder) ? cwd : folder; // cwd was a filesystem root
        }

        private const int MaxLabelLength = 44;

        internal static string SessionLabel(SessionEntry session, bool disambiguate)
        {
            var folder = DisplayName(session);

            // Chat names are sentence-ish and can run long; a menu that wide
            // covers half the screen. Cut at a word boundary when there's one
            // nearby — "...and Mac…" reads better than "...and Mac launch…".
            if (folder.Length > MaxLabelLength)
            {
                var cut = folder[..(MaxLabelLength - 1)];
                var space = cut.LastIndexOf(' ');
                if (space >= MaxLabelLength / 2) cut = cut[..space];
                folder = cut.TrimEnd() + "…";
            }

            if (disambiguate && session.SessionId.Length >= 4)
            {
                folder += $" ({session.SessionId[..4]})";
            }

            var state = session.Status.State switch
            {
                "waiting" => "needs you",
                "generating" => "working",
                _ => "idle"
            };
            return $"{folder} — {state}";
        }

        private WindowIcon LoadIcon(string state, bool muted)
        {
            var asset = TrayIconLook.AssetName(state, muted);
            if (_iconCache.TryGetValue(asset, out var cached)) return cached;

            var icon = Tinted(state, muted)
                       ?? new WindowIcon(AssetLoader.Open(
                           new Uri($"avares://{Brand.AssemblyName}/Assets/{asset}.png")));
            _iconCache[asset] = icon;
            return icon;
        }

        // Recolours the baked tray artwork to a chosen state colour.
        //
        // The three PNGs are single-colour alpha masks and always have been:
        // make-icons.py's tray_shader returns one constant RGB and varies only
        // alpha (opaque in the ring, 0.30 in the core, nothing outside), so every
        // pixel with any alpha in tray-idle.png carries exactly #5B7A94.
        // Substituting the RGB and keeping the alpha therefore reproduces the
        // icon *exactly*, down to the supersampled rim — which redrawing the
        // annulus with a DrawingContext would not. That would mean a second copy
        // of the Python's geometry here, antialiased by Skia instead of
        // supersampled, so the icon's shape would visibly change the moment
        // someone picked a colour.
        //
        // Unlike ClaudeDesktopBundles.WriteTinted there's no un-premultiply round
        // trip to do: the new colour times the existing alpha *is* the new
        // premultiplied pixel, so nothing is lost at the antialiased edge.
        //
        // Null means "use the file as it is" — either this state has never been
        // recoloured, in which case the baked PNG already is that colour, or
        // something about the re-tint failed, and then the baked PNG is a
        // graceful answer: the icon still says which state we're in, just not in
        // the chosen hue.
        //
        // The muted variants (CB-298) are the same single-colour alpha mask with
        // a slash cut through it, so this works on them unchanged; the colour is
        // still the base state's.
        internal static WindowIcon? Tinted(string state, bool muted = false)
        {
            if (OrbColors.IsDefault(state)) return null;

            var color = OrbColors.For(state);

            try
            {
                using var source = new Bitmap(AssetLoader.Open(
                    new Uri($"avares://{Brand.AssemblyName}/Assets/{TrayIconLook.AssetName(state, muted)}.png")));

                // 64x64, which is what make-icons.py renders so the menu bar has
                // retina pixels to downsample from.
                var size = source.PixelSize;
                var stride = size.Width * 4;
                var pixels = new byte[stride * size.Height];

                var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    source.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                        pinned.AddrOfPinnedObject(), pixels.Length, stride);

                    for (var i = 0; i < pixels.Length; i += 4)
                    {
                        var alpha = pixels[i + 3];
                        if (alpha == 0) continue;

                        pixels[i] = (byte)(color.B * alpha / 255);
                        pixels[i + 1] = (byte)(color.G * alpha / 255);
                        pixels[i + 2] = (byte)(color.R * alpha / 255);
                    }
                }
                finally
                {
                    pinned.Free();
                }

                using var tinted = new WriteableBitmap(
                    size, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var frame = tinted.Lock())
                {
                    Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
                }

                // Through a stream because that's the WindowIcon constructor this
                // file already proves exists. Encoding 64x64 to PNG in memory
                // costs well under a millisecond, and it happens three times per
                // colour change rather than once per tray tick.
                var encoded = new MemoryStream();
                tinted.Save(encoded);
                encoded.Position = 0;
                return new WindowIcon(encoded);
            }
            catch
            {
                return null;
            }
        }
    }
}
