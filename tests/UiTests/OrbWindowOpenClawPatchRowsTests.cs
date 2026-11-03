using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;

namespace Orbweaver.Tests;

// CB-270: an OpenClaw orb's "Keep on screen" and "Rename conversation…" rows —
// which orbs offer them, what the checkbox mirrors, what a click says while it
// waits and afterwards, and that Rename opens its window through the seam.
//
// Driven through the orb's own seams (OpenClawCapabilities, PinAction,
// RenameAction), for the reason OrbWindowOpenClawActionsTests gives: the
// requests are covered over an in-memory socket in tests/UnitTests, and the
// eligibility clause by clause in OpenClawOrbActionsTests. What is left, and
// only reachable here, is the menu.
//
// A new file rather than more cases in that one, so the CB-170 tests stay
// byte-for-byte as they were.
[Collection("Settings")]
public class OrbWindowOpenClawPatchRowsTests : IDisposable
{
    private const string Orb = "openclaw:agent:main:dashboard:abc";

    public void Dispose()
    {
        OpenClawRenameWindow.OpenForTests = null;
        OpenClawRenameWindow.PresentForTests = null;
        OpenClawRenameWindow.BringForwardForTests = null;
    }

    private static OpenClawActionContext Context(
        string[]? scopes = null, bool pinned = false, string? label = "Scratch pad",
        string? sessionId = "sid-1", bool isMain = false) =>
        new(true,
            new HashSet<string> { "chat.abort", "sessions.patch" },
            scopes ?? new[] { "operator.read", "operator.write" },
            isMain, sessionId, "agent:main:dashboard:abc", pinned, label);

    private static SessionStatus OpenClaw(bool room = false) => new()
    {
        Source = SessionSource.OpenClaw,
        State = "idle",
        IsRoom = room,
    };

    private sealed class Harness
    {
        public bool Pinned;
        public OpenClawActionContext? Fixed;
        public readonly List<string> Calls = new();
        public Func<Task<(OpenClawActionOutcome, string?)>>? Pin;
        public readonly List<string> Renames = new();
    }

    private static (OrbWindow Orb, Harness H) OrbWith(
        Harness? h = null, OpenClawActionContext? context = null, SessionStatus? status = null)
    {
        h ??= new Harness();
        h.Fixed = context;

        var orb = new OrbWindow(Orb)
        {
            OpenClawCapabilities = _ => h.Fixed ?? Context(pinned: h.Pinned),
            PinAction = async (id, pin, _) =>
            {
                h.Calls.Add($"pin {id} {pin}");
                var result = h.Pin is null
                    ? (OpenClawActionOutcome.Done, (string?)null)
                    : await h.Pin();

                // What the real request does on Done: the list catches up.
                if (result.Item1 == OpenClawActionOutcome.Done) h.Pinned = pin;
                return result;
            },
            RenameAction = (id, label, _) =>
            {
                h.Renames.Add($"{id}|{label}");
                return Task.FromResult((OpenClawActionOutcome.Done, (string?)null));
            },
        };

        orb.UpdateFrom(status ?? OpenClaw());
        return (orb, h);
    }

    private static MenuItem Row(OrbWindow orb, string name) => orb.FindControl<MenuItem>(name)!;
    private static MenuItem Keep(OrbWindow orb) => Row(orb, "KeepOnScreenItem");
    private static MenuItem Rename(OrbWindow orb) => Row(orb, "RenameConversationItem");

    // --- which orbs offer them ---

    [AvaloniaFact]
    public void AnOpenClawOrbWithReplyingOnOffersBothRows()
    {
        var (orb, _) = OrbWith();

        Assert.True(Keep(orb).IsVisible);
        Assert.Equal("Keep on screen", Keep(orb).Header);
        Assert.Equal(MenuItemToggleType.CheckBox, Keep(orb).ToggleType);
        Assert.Contains("on the gateway", (string)ToolTip.GetTip(Keep(orb))!);

        Assert.True(Rename(orb).IsVisible);
        Assert.Equal("Rename conversation…", Rename(orb).Header);
    }

    [AvaloniaFact]
    public void WithoutWriteScopeNeitherRowIsOffered()
    {
        var (orb, _) = OrbWith(context: Context(scopes: new[] { "operator.read" }));

        Assert.False(Keep(orb).IsVisible);
        Assert.False(Rename(orb).IsVisible);
    }

    [AvaloniaFact]
    public void ARoomOrbOffersNeither()
    {
        var (orb, _) = OrbWith(status: OpenClaw(room: true));

        Assert.False(Keep(orb).IsVisible);
        Assert.False(Rename(orb).IsVisible);
    }

    // Unlike End, an agent's main session may be pinned and renamed.
    [AvaloniaFact]
    public void AnAgentsMainSessionOffersBoth()
    {
        var (orb, _) = OrbWith(context: Context(isMain: true));

        Assert.True(Keep(orb).IsVisible);
        Assert.True(Rename(orb).IsVisible);
    }

    // A local orb never asks the gateway.
    [AvaloniaFact]
    public void ALocalOrbOffersNeither()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString())
        {
            OpenClawCapabilities = _ => throw new InvalidOperationException("asked the gateway")
        };

        orb.UpdateFrom(new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321, State = "idle" });

        Assert.False(Keep(orb).IsVisible);
        Assert.False(Rename(orb).IsVisible);
    }

    // --- the checkbox mirrors the gateway ---

    [AvaloniaFact]
    public void TheCheckboxMirrorsThePin()
    {
        var (orb, h) = OrbWith(context: Context(pinned: true));
        Assert.True(Keep(orb).IsChecked);

        h.Fixed = Context(pinned: false);
        orb.UpdateFrom(OpenClaw());
        Assert.False(Keep(orb).IsChecked);
    }

    // --- clicking it ---

    [AvaloniaFact]
    public async Task ClickingAnUnpinnedOrbPinsItAndSaysSo()
    {
        var gate = new TaskCompletionSource<(OpenClawActionOutcome, string?)>();
        var h = new Harness { Pin = () => gate.Task };
        var (orb, _) = OrbWith(h);

        var pending = orb.KeepOnScreenAsync();

        Assert.Equal(new[] { $"pin {Orb} True" }, h.Calls);
        Assert.Equal("Keeping…", Keep(orb).Header);
        Assert.False(Keep(orb).IsEnabled);

        gate.SetResult((OpenClawActionOutcome.Done, null));
        await pending;

        Assert.Equal("Kept on screen", Keep(orb).Header);
        Assert.True(Keep(orb).IsEnabled);
        Assert.True(Keep(orb).IsChecked);
    }

    [AvaloniaFact]
    public async Task ClickingAPinnedOrbReleasesIt()
    {
        var h = new Harness { Pinned = true };
        var (orb, _) = OrbWith(h);
        Assert.True(Keep(orb).IsChecked);

        await orb.KeepOnScreenAsync();

        Assert.Equal(new[] { $"pin {Orb} False" }, h.Calls);
        Assert.Equal("No longer kept on screen", Keep(orb).Header);
        Assert.False(Keep(orb).IsChecked);
    }

    [AvaloniaFact]
    public async Task ARefusalIsShownAndTheBoxStaysWhereItWas()
    {
        var h = new Harness { Pin = () => Task.FromResult((OpenClawActionOutcome.Refused, (string?)"missing scope: operator.write")) };
        var (orb, _) = OrbWith(h);

        await orb.KeepOnScreenAsync();

        Assert.Equal("Couldn't keep it on screen: missing scope: operator.write", Keep(orb).Header);
        Assert.False(Keep(orb).IsChecked);
        Assert.True(Keep(orb).IsEnabled);
    }

    // The scan runs every couple of seconds; it must not wipe the answer or
    // flip the box before anyone has read it.
    [AvaloniaFact]
    public async Task TheScanDoesNotOverwriteAnAnswerBeingRead()
    {
        var (orb, h) = OrbWith();

        await orb.KeepOnScreenAsync();
        h.Pinned = false;                 // the gateway's list lags behind
        orb.UpdateFrom(OpenClaw());

        Assert.Equal("Kept on screen", Keep(orb).Header);
        Assert.True(Keep(orb).IsChecked);

        // The menu closing hands the wording, and then the box, back to the scan.
        orb.SessionMenu_Closed(null, new RoutedEventArgs());
        Assert.Equal("Keep on screen", Keep(orb).Header);
        orb.UpdateFrom(OpenClaw());
        Assert.False(Keep(orb).IsChecked);
    }

    [AvaloniaFact]
    public async Task ASecondClickWhileBusyIsIgnored()
    {
        var gate = new TaskCompletionSource<(OpenClawActionOutcome, string?)>();
        var h = new Harness { Pin = () => gate.Task };
        var (orb, _) = OrbWith(h);

        var first = orb.KeepOnScreenAsync();
        await orb.KeepOnScreenAsync();

        Assert.Single(h.Calls);

        gate.SetResult((OpenClawActionOutcome.Done, null));
        await first;
    }

    // Closed while the request is out: the answer has nobody to be shown to.
    [AvaloniaFact]
    public async Task AnAnswerAfterTheMenuClosedReleasesTheRows()
    {
        var gate = new TaskCompletionSource<(OpenClawActionOutcome, string?)>();
        var h = new Harness { Pin = () => gate.Task };
        var (orb, _) = OrbWith(h);

        var pending = orb.KeepOnScreenAsync();
        orb.SessionMenu_Closed(null, new RoutedEventArgs());
        Assert.Equal("Keeping…", Keep(orb).Header);

        gate.SetResult((OpenClawActionOutcome.Done, null));
        await pending;

        Assert.Equal("Keep on screen", Keep(orb).Header);
        Assert.True(Keep(orb).IsEnabled);
        Assert.True(Keep(orb).IsChecked);
    }

    // The click handler is the same work behind an async-void event.
    [AvaloniaFact]
    public void TheClickEventStartsThePin()
    {
        var (orb, h) = OrbWith();

        orb.KeepOnScreen_Click(null, new RoutedEventArgs());

        Assert.Equal(new[] { $"pin {Orb} True" }, h.Calls);
    }

    // --- Rename ---

    [AvaloniaFact]
    public void RenameOpensItsWindowPrefilledWithTheCurrentLabel()
    {
        OpenClawRenameWindow? shown = null;
        OpenClawRenameWindow.PresentForTests = window => shown = window;
        var (orb, h) = OrbWith(context: Context(label: "Scratch pad"));

        orb.RenameConversation_Click(null, new RoutedEventArgs());

        Assert.NotNull(shown);
        Assert.Equal(Orb, shown!.OrbSessionId);
        Assert.Equal("Scratch pad", shown.NameBox.Text);

        // And Save goes through the orb's own seam.
        shown.NameBox.Text = "  New name ";
        shown.SaveAsync().GetAwaiter().GetResult();
        Assert.Equal(new[] { $"{Orb}|New name" }, h.Renames);
    }

    [AvaloniaFact]
    public void RenameOfAnUnnamedConversationStartsWithAnEmptyBox()
    {
        OpenClawRenameWindow? shown = null;
        OpenClawRenameWindow.PresentForTests = window => shown = window;
        var (orb, _) = OrbWith(context: Context(label: null));

        orb.RenameConversation_Click(null, new RoutedEventArgs());

        Assert.Equal("", shown!.NameBox.Text);
    }
}
