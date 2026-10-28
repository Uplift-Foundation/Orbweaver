using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// CB-287: a usage orb wearing what the user chose. Driven through UpdateFrom
// with a hand-built look, and through AccountOrbs for the wiring, and asserted
// on what a person would have seen.
[Collection("Settings")]
public class AccountOrbOverrideTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly bool _twoLetter = OrbweaverSettings.TwoLetterGlyphs;

    public AccountOrbOverrideTests() => OrbweaverSettings.TwoLetterGlyphs = true;

    private readonly System.Collections.Generic.List<AccountOrbs> _made = new();

    private AccountOrbs Make(IUsageSource source)
    {
        var orbs = new AccountOrbs(source);
        _made.Add(orbs);
        return orbs;
    }

    public void Dispose()
    {
        foreach (var orbs in _made) orbs.CloseAll();
        OrbweaverSettings.TwoLetterGlyphs = _twoLetter;
        AccountOrbWindow.ClearBreathingForTests();
        AccountOrbs.OpenEditorForTests = null;
    }

    private static AccountUsage Usage(
        string? configDir = null,
        string label = "board",
        AccountUsageSource source = AccountUsageSource.ClaudeCode,
        DateTimeOffset? readAt = null) =>
        new(configDir, label, true, "team",
            new UsageWindow(20, Now.AddHours(3)), new UsageWindow(30, Now.AddDays(3)),
            null, readAt ?? Now, source);

    private static string Mint(string key, SKColor colour)
    {
        var dir = Path.Combine(Path.GetTempPath(), "orb287u-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var bmp = new SKBitmap(16, 16);
        bmp.Erase(colour);
        using var img = SKImage.FromBitmap(bmp);
        var src = Path.Combine(dir, "i.png");
        File.WriteAllBytes(src, img.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        var r = AccountOrbIcons.Import(src, key, AccountOrbIcons.IconsDirectory);
        Assert.Null(r.Error);
        return r.FileName!;
    }

    private static string TipTitle(AccountOrbWindow orb) =>
        ((Control)orb.CurrentThoughtBubble!).GetLogicalDescendants().OfType<TextBlock>().First().Text!;

    private static AccountOrbLook Look(string glyph = "Bo", string name = "board", string? icon = null) =>
        new(glyph, name, icon);

    [AvaloniaFact]
    public void InitialsOverride_ChangesGlyphText()
    {
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(), Look("ZZ"), Now);
        Assert.Equal("ZZ", orb.GlyphText);
        Assert.True(orb.GlyphVisible);
        Assert.False(orb.IconVisible);
    }

    [AvaloniaFact]
    public void NameOverride_ChangesTooltipTitle()
    {
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(), Look(name: "Acme Corp"), Now);
        Assert.Equal("Acme Corp", TipTitle(orb));
    }

    [AvaloniaFact]
    public void Rename_RebuildsTooltipEvenWithSameSummary()
    {
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(), Look(name: "one"), Now);
        var first = orb.CurrentThoughtBubble;
        orb.UpdateFrom(Usage(), Look(name: "one"), Now);
        Assert.Same(first, orb.CurrentThoughtBubble);

        orb.UpdateFrom(Usage(), Look(name: "two"), Now);
        Assert.NotSame(first, orb.CurrentThoughtBubble);
        Assert.Equal("two", TipTitle(orb));
    }

    [AvaloniaFact]
    public void Icon_HidesGlyph_ShowsImage()
    {
        var file = Mint("k1", SKColors.Red);
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(), Look(icon: file), Now);
        Assert.True(orb.IconVisible);
        Assert.False(orb.GlyphVisible);
        Assert.Equal(file, orb.IconFileShown);
    }

    [AvaloniaFact]
    public void MissingIcon_FallsBackToLetters()
    {
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(), Look(icon: "0123456789ab-0123456789abcdef.png"), Now);
        Assert.False(orb.IconVisible);
        Assert.True(orb.GlyphVisible);
        Assert.Null(orb.IconFileShown);
        Assert.Equal("Bo", orb.GlyphText);
    }

    [AvaloniaFact]
    public void CorruptIcon_FallsBackToLetters()
    {
        var file = Mint("k2", SKColors.Red);
        File.WriteAllBytes(Path.Combine(AccountOrbIcons.IconsDirectory, file), [1, 2, 3]);
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(), Look(icon: file), Now);
        Assert.False(orb.IconVisible);
        Assert.True(orb.GlyphVisible);
    }

    // CB-148: a failed load is not remembered. The file comes back and the very
    // next redraw shows it, without the name changing.
    [AvaloniaFact]
    public void FailedIcon_RetriedAfterFileFixed()
    {
        var file = Mint("k3", SKColors.Red);
        var path = Path.Combine(AccountOrbIcons.IconsDirectory, file);
        var good = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [1, 2, 3]);

        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(), Look(icon: file), Now);
        Assert.False(orb.IconVisible);

        File.WriteAllBytes(path, good);
        orb.UpdateFrom(Usage(), Look(icon: file), Now);
        Assert.True(orb.IconVisible);
    }

    [AvaloniaFact]
    public void GoodIcon_NotRedecodedPerRedraw_ButNewNameIs()
    {
        var a = Mint("k4", SKColors.Red);
        var b = Mint("k4", SKColors.Blue);
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(), Look(icon: a), Now);
        orb.UpdateFrom(Usage(), Look(icon: a), Now);
        orb.UpdateFrom(Usage(), Look(icon: a), Now);
        Assert.Equal(1, orb.IconDecodeCount);

        orb.UpdateFrom(Usage(), Look(icon: b), Now);
        Assert.Equal(2, orb.IconDecodeCount);
        Assert.Equal(b, orb.IconFileShown);
    }

    [AvaloniaFact]
    public void Reset_RestoresLettersHidesIcon()
    {
        var file = Mint("k5", SKColors.Red);
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(), Look("ZZ", "Acme", file), Now);
        Assert.True(orb.IconVisible);

        orb.UpdateFrom(Usage(), Look("Bo", "board"), Now);
        Assert.False(orb.IconVisible);
        Assert.True(orb.GlyphVisible);
        Assert.Equal("Bo", orb.GlyphText);
        Assert.Null(orb.IconFileShown);
    }

    [AvaloniaFact]
    public void Stale_DimsAndKeepsTheIcon_AndCliBadgeStaysVisible()
    {
        var file = Mint("k6", SKColors.Red);
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(readAt: Now.AddHours(-2), source: AccountUsageSource.Codex), Look(icon: file), Now);
        Assert.True(orb.IsDimmed);
        Assert.True(orb.IconVisible);
        Assert.True(orb.CliMarkVisible);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ContextMenu_HasCustomize(int source)
    {
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(Usage(source: (AccountUsageSource)source), Look(), Now);
        Assert.Equal(new[] { "Customize…" }, orb.ContextMenuHeaders);
    }

    [AvaloniaFact]
    public void Customize_RaisesCustomizeRequested()
    {
        var orb = new AccountOrbWindow("k");
        AccountOrbWindow? got = null;
        orb.CustomizeRequested += o => got = o;
        orb.FindControl<MenuItem>("CustomizeItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Same(orb, got);
    }

    // The two-argument overload still resolves the look from settings, so every
    // caller that predates CB-287 keeps working.
    [AvaloniaFact]
    public void TwoArgumentUpdateFrom_ReadsOverridesFromSettings()
    {
        var key = AccountOrbs.PositionKey("/home/u/.claude-ov");
        AccountOrbCustomization.SetInitials(key, "Qx");
        try
        {
            var orb = new AccountOrbWindow("/home/u/.claude-ov");
            orb.UpdateFrom(Usage("/home/u/.claude-ov"), Now);
            Assert.Equal("Qx", orb.GlyphText);
        }
        finally { AccountOrbCustomization.Reset(key); }
    }

    // ---- UsageCard ----

    [AvaloniaFact]
    public void Card_DisplayNameReplacesName_DotStaysOnDerivedLabel()
    {
        var card = new UsageCard();
        card.UpdateFrom(Usage(label: "board"), null, Now);
        var plain = card.AccountDotHex;
        Assert.Equal("board", card.AccountNameText);

        card.UpdateFrom(Usage(label: "board"), null, Now, "Acme");
        Assert.Equal("Acme", card.AccountNameText);
        Assert.Equal(plain, card.AccountDotHex);
    }

    // ---- AccountOrbs wiring ----

    private sealed class NoSource : IUsageSource
    {
        public System.Collections.Generic.IReadOnlyList<AccountUsage> Read() => Array.Empty<AccountUsage>();
    }

    [AvaloniaFact]
    public void Accounts_ListsKeySourceLabel()
    {
        var orbs = Make(new NoSource());
        orbs.Apply(new[]
        {
            Usage(null, "wt"),
            Usage("/h/.codex", "cx", AccountUsageSource.Codex)
        }, Now);

        var list = orbs.Accounts;
        Assert.Equal(2, list.Count);
        Assert.Equal(new AccountOrbIdentity("", "account:~", AccountUsageSource.ClaudeCode, "wt"), list[0]);
        Assert.Equal(new AccountOrbIdentity("/h/.codex", "account:/h/.codex", AccountUsageSource.Codex, "cx"), list[1]);
        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void CustomizationChange_RedrawsOrbAndCard_WithoutAPoll()
    {
        var orbs = Make(new NoSource());
        orbs.Apply(new[] { Usage(null, "wt") }, Now);
        orbs.OpenCardForTests(string.Empty);
        var orb = orbs.Orbs[string.Empty];

        try
        {
            AccountOrbCustomization.SetInitials("account:~", "Zq");
            AccountOrbCustomization.SetName("account:~", "Acme");
            Assert.Equal("Zq", orb.GlyphText);
            Assert.Equal("Acme", orbs.Cards[string.Empty].AccountNameText);
            Assert.Equal("Acme", TipTitle(orb));

            AccountOrbCustomization.Reset("account:~");
            Assert.Equal("wt", orbs.Cards[string.Empty].AccountNameText);
        }
        finally
        {
            AccountOrbCustomization.Reset("account:~");
            orbs.CloseAll();
        }
    }

    // A write from a pool thread is posted to the UI thread rather than
    // redrawing windows from where it stands; the on-thread write is the
    // control that the same redraw happens inline.
    [AvaloniaFact]
    public void ChangeFromAnotherThread_IsPostedToTheUiThread_AndRedraws()
    {
        var orbs = Make(new NoSource());
        orbs.Apply(new[] { Usage(null, "wt") }, Now);
        var orb = orbs.Orbs[string.Empty];

        var uiThread = Environment.CurrentManagedThreadId;
        var ranOn = new System.Collections.Concurrent.ConcurrentQueue<int>();
        orbs.RefreshedOnThreadForTests = ranOn.Enqueue;

        try
        {
            // On-thread control: redraws inline, on this thread.
            AccountOrbCustomization.SetInitials("account:~", "Aa");
            Assert.Equal("Aa", orb.GlyphText);
            Assert.Equal(new[] { uiThread }, ranOn.ToArray());

            // From a pool thread: whenever the dispatcher pumps it, the redraw
            // must run on the UI thread and never on the writer's.
            //
            // A dedicated thread, not Task.Run(...).GetResult(): waiting on a
            // task that has not started yet lets the waiter run it inline, so
            // the "pool" write sometimes happened on the UI thread itself —
            // the flake QA measured at 5 failures in 7 runs.
            var writer = 0;
            var thread = new System.Threading.Thread(() =>
            {
                writer = Environment.CurrentManagedThreadId;
                AccountOrbCustomization.SetInitials("account:~", "Bb");
            });
            thread.Start();
            thread.Join();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.NotEqual(uiThread, writer);
            Assert.Equal("Bb", orb.GlyphText);
            Assert.Equal(new[] { uiThread, uiThread }, ranOn.ToArray());
        }
        finally { AccountOrbCustomization.Reset("account:~"); }
    }

    [AvaloniaFact]
    public void AfterTheLastOrbCloses_ChangesNoLongerReachThisInstance()
    {
        var orbs = Make(new NoSource());
        orbs.Apply(new[] { Usage(null, "wt") }, Now);
        orbs.CloseAll();
        Assert.Empty(orbs.Orbs);

        // Would throw or resurrect an orb if the instance were still listening.
        AccountOrbCustomization.SetName("account:~", "x");
        AccountOrbCustomization.Reset("account:~");
        Assert.Empty(orbs.Orbs);
    }

    [AvaloniaFact]
    public void Customize_OpensEditorForThatAccount()
    {
        var orbs = Make(new NoSource());
        orbs.Apply(new[]
        {
            Usage(null, "wt"),
            Usage("/h/.claude-b", "b")
        }, Now);

        AccountOrbIdentity? opened = null;
        Action? refresh = null;
        AccountOrbs.OpenEditorForTests = (id, onChanged) => { opened = id; refresh = onChanged; };

        orbs.Orbs["/h/.claude-b"].FindControl<MenuItem>("CustomizeItem")!
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(new AccountOrbIdentity("/h/.claude-b", "account:/h/.claude-b", AccountUsageSource.ClaudeCode, "b"), opened);
        Assert.NotNull(refresh);
        refresh!(); // the callback the editor is handed redraws; must not throw
        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void Customize_ForAnOrbWithNoReading_OpensNothing()
    {
        var orbs = Make(new NoSource());
        orbs.Apply(new[] { Usage(null, "wt") }, Now);
        var opened = false;
        AccountOrbs.OpenEditorForTests = (_, _) => opened = true;

        // Simulate an orb the manager no longer holds a reading for.
        var stray = new AccountOrbWindow("nope");
        orbs.RaiseCustomizeForTests(stray);
        Assert.False(opened);
        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void ReapplyGlyphs_ReachesAccountOrbs()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "orb287m-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var manager = new SessionManager(scratch);
        var was = OrbweaverSettings.TwoLetterGlyphs;
        try
        {
            OrbweaverSettings.TwoLetterGlyphs = true;
            manager.AccountOrbsForTests.Apply(new[] { Usage(null, "claude-buddy") }, Now);
            var orb = manager.AccountOrbsForTests.Orbs[string.Empty];
            Assert.Equal("Cb", orb.GlyphText);

            OrbweaverSettings.TwoLetterGlyphs = false;
            manager.ReapplyGlyphs();
            Assert.Equal("C", orb.GlyphText);

            Assert.Single(manager.AccountOrbList());
            AccountOrbCustomization.SetInitials("account:~", "Zz");
            try { manager.RefreshAccountOrbLooks(); Assert.Equal("Z", orb.GlyphText); }
            finally { AccountOrbCustomization.Reset("account:~"); }
        }
        finally
        {
            OrbweaverSettings.TwoLetterGlyphs = was;
            manager.AccountOrbsForTests.CloseAll();
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }
}
