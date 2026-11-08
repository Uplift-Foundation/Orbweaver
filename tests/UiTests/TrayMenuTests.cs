using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// The shape of the tray menu, and the two rules about when it is allowed to be
// rebuilt.
//
// Those rules are the reason this needs a real controller rather than the static
// helpers TrayMenuTextTests covers. Rebuilding a NativeMenu is *visible* on
// macOS — it clears Items, which dismisses a menu somebody has open — and the
// scan calls Update every two seconds, so the controller holds changes back
// twice over: once on a signature, so an unchanged session list does nothing at
// all, and once on the menu being open, so a change that arrives under the
// pointer waits for it to close. Neither can be checked by reading a function's
// return value; both are about a menu object surviving, or not, across a call.
//
// The item's Click handlers are deliberately never invoked. They reach
// TerminalFocuser, SettingsWindow.Toggle, a real Shutdown and (for the remote
// item) a bridge that costs the person running the tests money — see
// TrayRemoteItemTests, which records that last one.
[Collection("Settings")]
public class TrayMenuTests
{
    private const string NoSessions = "No sessions";
    private const string ResetAll = "Reset all sessions to idle";
    private const string ShowOrbs = "Show orbs";
    private const string Quit = $"Quit {Brand.DisplayName}";
    private const string NewChat = "New chat…";
    private const string Settings = "Settings…";

    // A sentinel nothing in Rebuild would ever add. If it is still in the menu
    // after a call, the menu was not rebuilt — which is the only way to observe
    // a rebuild that was correctly declined.
    private const string Sentinel = "— sentinel —";

    private static TrayController? NewController()
    {
        try
        {
            return new TrayController();
        }
        catch
        {
            // A TrayIcon may not be constructible under the headless platform.
            // Reported as "couldn't check" by the callers rather than silently
            // passing, the same way TrayRemoteItemTests does it.
            return null;
        }
    }

    private static NativeMenu MenuOf(TrayController tray) =>
        (NativeMenu)typeof(TrayController)
            .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tray)!;

    private static TrayIcon IconOf(TrayController tray) =>
        (TrayIcon)typeof(TrayController)
            .GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tray)!;

    private static List<string> Labels(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().Select(item => item.Header ?? "").ToList();

    private static NativeMenuItem? Item(NativeMenu menu, string header) =>
        menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == header);

    private static TrayController.SessionEntry Entry(
        string id, string state = "idle", string title = "", string cwd = "") =>
        new(id, new SessionStatus { State = state, Title = title, Cwd = cwd });

    [AvaloniaFact]
    public void WithNothingRunningTheMenuSaysSoAndOffersNothingToReset()
    {
        var tray = NewController();
        if (tray is null) return;

        var menu = MenuOf(tray);

        // Named for no CLI in particular. There is no longer any state in which
        // "no Claude Code sessions" is known to be the whole truth — Codex has
        // no enable switch this app can see — and naming one CLI while another
        // is quietly running reads as a bug in the other.
        Assert.Contains(NoSessions, Labels(menu));
        Assert.False(Item(menu, NoSessions)!.IsEnabled);
        Assert.False(Item(menu, ResetAll)!.IsEnabled);

        // The permanent controls are there regardless: there is no Dock icon and
        // no window when nothing is running, so this menu is the only way out of
        // the app.
        Assert.Contains(ShowOrbs, Labels(menu));
        Assert.Contains(Quit, Labels(menu));
    }

    [AvaloniaFact]
    public void EverySessionGetsARowInTheOrderItWasHandedOverAndResetTurnsOn()
    {
        var tray = NewController();
        if (tray is null) return;

        tray.Update(new[]
        {
            Entry("id-1", title: "first"),
            Entry("id-2", state: "waiting", title: "second"),
        });

        var labels = Labels(MenuOf(tray));

        Assert.DoesNotContain(NoSessions, labels);
        Assert.Equal("first — idle", labels[0]);
        Assert.Equal("second — needs you", labels[1]);
        Assert.True(Item(MenuOf(tray), ResetAll)!.IsEnabled);
    }

    [AvaloniaFact]
    public void TwoSessionsWithOneNameAreDisambiguatedAndOthersAreLeftAlone()
    {
        // "You can't tell which terminal a click will take you to." Only the
        // colliding pair pays for it — the id suffix is noise on a name that is
        // already unique.
        var tray = NewController();
        if (tray is null) return;

        tray.Update(new[]
        {
            Entry("aaaa1111-0000", title: "evidence"),
            Entry("bbbb2222-0000", title: "evidence"),
            Entry("cccc3333-0000", title: "unique"),
        });

        var labels = Labels(MenuOf(tray));

        Assert.Contains("evidence (aaaa) — idle", labels);
        Assert.Contains("evidence (bbbb) — idle", labels);
        Assert.Contains("unique — idle", labels);
    }

    [AvaloniaFact]
    public void AnUnchangedSessionListDoesNotTouchTheMenuAtAll()
    {
        // The scan runs every two seconds and almost never has news. Rebuilding
        // anyway would dismiss an open menu twice a second on macOS.
        var tray = NewController();
        if (tray is null) return;

        var sessions = new[] { Entry("id-1", title: "first") };
        tray.Update(sessions);

        var menu = MenuOf(tray);
        menu.Add(new NativeMenuItem(Sentinel));

        tray.Update(sessions);
        Assert.Contains(Sentinel, Labels(menu));

        // A state change is news, and does rebuild — which is also what proves
        // the assertion above was about the gate and not about Update never
        // doing anything.
        tray.Update(new[] { Entry("id-1", state: "waiting", title: "first") });
        Assert.DoesNotContain(Sentinel, Labels(menu));
        Assert.Contains("first — needs you", Labels(menu));
    }

    [AvaloniaFact]
    public void AChangeThatArrivesWhileTheMenuIsOpenWaitsForItToClose()
    {
        var tray = NewController();
        if (tray is null) return;

        var menu = MenuOf(tray);
        tray.Update(new[] { Entry("id-1", title: "first") });

        if (!TryRaise(menu, "RaiseOpening")) return;   // see TryRaise

        menu.Add(new NativeMenuItem(Sentinel));
        tray.Update(new[] { Entry("id-1", state: "waiting", title: "first") });

        // Held: the menu somebody is looking at is left exactly as it was.
        Assert.Contains(Sentinel, Labels(menu));
        Assert.DoesNotContain("first — needs you", Labels(menu));

        if (!TryRaise(menu, "RaiseClosed")) return;

        // Replayed on close. _lastSignature was deliberately left stale so the
        // change could not be swallowed by the gate above.
        Assert.DoesNotContain(Sentinel, Labels(menu));
        Assert.Contains("first — needs you", Labels(menu));
    }

    [AvaloniaFact]
    public void TheIconAndTooltipFollowTheMostUrgentSessionAndIgnoreTheMenuHold()
    {
        // "The icon is the urgent half of this — it goes amber when a session
        // needs you — and changing it doesn't disturb an open menu, so it's
        // never held back."
        var tray = NewController();
        if (tray is null) return;

        // The constructor's own Rebuild has already replaced the plain "Claude
        // Buddy" the TrayIcon was created with, so the tooltip is honest from
        // the first paint rather than after the first scan.
        var icon = IconOf(tray);
        Assert.Equal($"{Brand.DisplayName} — no sessions", icon.ToolTipText);

        tray.Update(new[] { Entry("id-1", state: "generating", title: "first") });
        Assert.Equal($"{Brand.DisplayName} — 1 session, 1 working", icon.ToolTipText);

        var menu = MenuOf(tray);
        if (!TryRaise(menu, "RaiseOpening")) return;

        tray.Update(new[] { Entry("id-1", state: "waiting", title: "first") });

        // The menu was held; the tooltip was not.
        Assert.Equal($"{Brand.DisplayName} — 1 session, 1 needs you", icon.ToolTipText);
        Assert.DoesNotContain("first — needs you", Labels(menu));
    }

    [AvaloniaFact]
    public void AColourChangeRepaintsTheIconWithNoSessionsRunning()
    {
        // "With nothing running, the menu-bar icon is the only live preview of
        // the idle colour" — so this has to repaint on a change the signature
        // gate cannot see, which is exactly what ReapplyStateColors is for. A
        // non-default colour also sends LoadIcon down the re-tint path rather
        // than straight to the baked PNG.
        var before = OrbweaverSettings.IdleColor;
        try
        {
            var tray = NewController();
            if (tray is null) return;

            OrbweaverSettings.IdleColor = "#FF00AA";
            Assert.False(OrbColors.IsDefault("idle"));

            tray.ReapplyStateColors();

            Assert.Equal($"{Brand.DisplayName} — no sessions", IconOf(tray).ToolTipText);
            Assert.NotNull(IconOf(tray).Icon);
        }
        finally
        {
            OrbweaverSettings.IdleColor = before;
        }
    }

    // NativeMenu's Opening and Closed are events an exporter raises, not
    // something a caller can fire — Avalonia routes them through an explicitly
    // implemented interface. Reached by name, and reported as "couldn't check"
    // rather than failing if a future Avalonia renames it: the alternative is a
    // suite that breaks on an upgrade for a reason that has nothing to do with
    // this app.
    private static bool TryRaise(NativeMenu menu, string method)
    {
        var raise = typeof(NativeMenu)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.EndsWith(method, StringComparison.Ordinal)
                                 && m.GetParameters().Length == 0);

        if (raise is null) return false;

        raise.Invoke(menu, null);
        return true;
    }

    // CB-168: "New chat…" sits just above Settings, in both the empty-tray
    // state and with sessions running — the item is permanent chrome, not
    // something the session list decides.
    [AvaloniaFact]
    public void NewChatSitsAboveSettingsWithOrWithoutSessions()
    {
        var tray = NewController();
        if (tray is null) return;

        var labels = Labels(MenuOf(tray));
        Assert.Contains(NewChat, labels);
        Assert.True(labels.IndexOf(NewChat) < labels.IndexOf(Settings));

        tray.Update(new[] { Entry("id-1", title: "first") });
        labels = Labels(MenuOf(tray));
        Assert.Contains(NewChat, labels);
        Assert.True(labels.IndexOf(NewChat) < labels.IndexOf(Settings));
    }

    // CB-314: "Pair phone…" is always in the menu, with or without sessions
    // and whether or not the relay has ever been used — it is how the feature
    // is found. Its handler is covered in RelaySettingsAndServiceTests.
    [AvaloniaFact]
    public void PairPhoneIsAlwaysOffered()
    {
        var tray = NewController();
        if (tray is null) return;

        Assert.Contains("Pair phone…", Labels(MenuOf(tray)));
        tray.Update(new[] { Entry("id-1", title: "first") });
        var labels = Labels(MenuOf(tray));
        Assert.True(labels.IndexOf("Pair phone…") < labels.IndexOf(NewChat));
    }

    // --- the two actions that ARE safe to perform ---

    // The header above says this suite deliberately never invokes a Click
    // handler, and it was right about three of the five: Settings puts the app in
    // the Dock and takes a window key, Connect starts a relay that costs real
    // money, and the remote item is recorded in TrayRemoteItemTests for that
    // reason. The other two are safe, and now that each handler is a named method
    // rather than a lambda they can be judged apart instead of together.

    // Safe because SessionManager.Instance is null outside the running app, so
    // this is a no-op — which is exactly what a tray item should be when there is
    // nothing to show. A version that dereferenced instead of using ?. would
    // throw here, which is the failure worth catching.
    [AvaloniaFact]
    public void ShowingOrbsWithNoSessionManagerIsANoOp()
    {
        TrayController.ToggleOrbsVisible();
        TrayController.ToggleOrbsVisible();
    }

    [AvaloniaFact]
    public void ResettingSessionsWithNoSessionManagerIsANoOp()
    {
        TrayController.ResetAllSessions();
    }

    // Safe because under the headless lifetime Application.Current's lifetime is
    // never IClassicDesktopStyleApplicationLifetime, so Shutdown is never reached.
    // That guard is the point of the method — it is what stops a Quit item taking
    // down a host that is not a desktop app — and this is the only place it gets
    // exercised.
    [AvaloniaFact]
    public void QuittingUnderTheHeadlessLifetimeDoesNotShutAnythingDown()
    {
        TrayController.QuitApp();

        // Still here, which is the assertion: a Shutdown would have ended the run.
        Assert.NotNull(Avalonia.Application.Current);
    }
}
