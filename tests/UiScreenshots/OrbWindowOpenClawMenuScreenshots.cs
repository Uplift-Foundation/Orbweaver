using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Orbweaver.Tests;

// CB-170: an OpenClaw orb's right-click menu with its two gateway rows, in
// the three states a reviewer needs to see — offered, End armed, and End
// refused with the gateway's own sentence on the row.
//
// The real menu, opened on a shown orb, rather than its rows copied into a
// stand-in window: the thing under review is where the rows sit among the
// others and what they say there. The clicks go through the orb's seams, never
// the pointer — the same rule as OrbWindowScreenshots, whose pointer handling
// would reach TerminalFocuser — and the actions are fakes, so no gateway is
// involved.
public class OrbWindowOpenClawMenuScreenshots
{
    private static OrbWindow OpenClawOrb(
        Func<Task<(OpenClawActionOutcome, string?)>>? end = null, bool pinned = false)
    {
        var orb = new OrbWindow("openclaw:agent:main:dashboard:abc")
        {
            OpenClawCapabilities = _ => new OpenClawActionContext(
                true,
                new HashSet<string> { "chat.abort", "sessions.patch" },
                new[] { "operator.read", "operator.write" },
                false, "sid-1", "agent:main:dashboard:abc", pinned, "Scratch pad"),
            EndAction = (_, _) => end?.Invoke()
                ?? Task.FromResult((OpenClawActionOutcome.Done, (string?)null)),
        };

        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            State = "idle",
            Title = "Nova — dashboard",
        });

        return orb;
    }

    // Opens the orb's own context menu and captures it.
    //
    // The headless platform has no separate popup windows: a context menu is
    // hosted as an overlay inside the window it belongs to, and so is clipped to
    // it. At the orb's own 56x56 that left a picture of the menu's scroll
    // chevron and nothing else — measured, the menu arranged at exactly 56x56.
    // The window is widened for the capture alone so the overlay has room to lay
    // out at its natural size; on a real desktop the menu is its own popup and
    // the orb's size never constrains it.
    private static void CaptureMenu(OrbWindow orb, string fileName)
    {
        orb.Show();
        orb.Width = 360;
        orb.Height = 560;
        ScreenshotHelper.Flush();

        var menu = orb.FindControl<Grid>("Root")!.ContextMenu!;
        menu.Open(orb.FindControl<Grid>("Root")!);
        ScreenshotHelper.Flush();

        try
        {
            ScreenshotHelper.CaptureControl(menu, fileName);
        }
        finally
        {
            menu.Close();
        }
    }

    [AvaloniaFact]
    public void AnOpenClawOrbsMenuOffersInterruptAndEnd()
    {
        CaptureMenu(OpenClawOrb(), "orb-window-openclaw-menu-interrupt-and-end.png");
    }

    [AvaloniaFact]
    public async Task TheEndRowArmedAsksForASecondClick()
    {
        var orb = OpenClawOrb();
        await orb.EndConversationClickAsync();

        CaptureMenu(orb, "orb-window-openclaw-menu-end-armed.png");
    }

    [AvaloniaFact]
    public async Task ARefusedEndShowsTheGatewaysSentenceOnTheRow()
    {
        var orb = OpenClawOrb(end: () => Task.FromResult(
            (OpenClawActionOutcome.Refused, (string?)"missing scope: operator.write")));

        await orb.EndConversationClickAsync();
        await orb.EndConversationClickAsync();

        CaptureMenu(orb, "orb-window-openclaw-menu-end-refused.png");
    }

    // CB-270: the conversation's other two rows. Keep on screen is a checkbox
    // mirroring the gateway's pin, so the capture is of the checked state, which
    // is the one that shows the row is doing something; Rename conversation… sits
    // beneath it.
    [AvaloniaFact]
    public void ThePatchRowsShowKeepOnScreenCheckedAndRenameConversation()
    {
        CaptureMenu(OpenClawOrb(pinned: true), "orb-window-openclaw-menu-keep-and-rename.png");
    }

    // CB-270: the Rename window, prefilled with the conversation's current name.
    // Built through the constructor and captured without a Show(), the way the
    // group style window is.
    [AvaloniaFact]
    public void TheRenameWindowIsPrefilledWithTheCurrentName()
    {
        var window = new OpenClawRenameWindow(
            "openclaw:agent:main:dashboard:abc", "Scratch pad",
            (_, _, _) => Task.FromResult((OpenClawActionOutcome.Done, (string?)null)));

        ScreenshotHelper.Capture(window, "openclaw-rename-window.png");
    }

    // ...and after a refusal, which is the state a person needs the wording of.
    [AvaloniaFact]
    public async Task TheRenameWindowShowsTheGatewaysSentenceAfterARefusal()
    {
        var window = new OpenClawRenameWindow(
            "openclaw:agent:main:dashboard:abc", "Scratch pad",
            (_, _, _) => Task.FromResult((OpenClawActionOutcome.Refused, (string?)"label too long")));

        window.NameBox.Text = "A name the gateway will not take";
        await window.SaveAsync();

        ScreenshotHelper.Capture(window, "openclaw-rename-window-refused.png");
    }
}
