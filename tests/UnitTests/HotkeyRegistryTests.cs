using Avalonia.Input;
using Xunit;

namespace Orbweaver.Tests;

// HotkeyRegistry is pure — no window, no settings read, no OS call — so
// every one of these runs with nothing arranged first. See CB-155: the
// registry is the one part of the global-hotkey feature a headless test
// can reach at all; the two platform hooks that actually register a key
// with the OS are excluded from coverage and named as such in the PR.
public class HotkeyRegistryTests
{
    [Fact]
    public void Default_ToggleOrbsVisible_IsControlAltH()
    {
        var combo = HotkeyRegistry.Default(HotkeyAction.ToggleOrbsVisible);

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, combo.Modifiers);
        Assert.Equal(Key.H, combo.Key);
    }

    [Theory]
    [InlineData("Ctrl+Alt+H", KeyModifiers.Control | KeyModifiers.Alt, Key.H)]
    [InlineData("ctrl+alt+h", KeyModifiers.Control | KeyModifiers.Alt, Key.H)] // case-insensitive
    [InlineData("Control+Shift+P", KeyModifiers.Control | KeyModifiers.Shift, Key.P)]
    [InlineData("Cmd+Shift+H", KeyModifiers.Meta | KeyModifiers.Shift, Key.H)]
    [InlineData("Command+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Win+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Windows+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Super+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Meta+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Option+Ctrl+H", KeyModifiers.Alt | KeyModifiers.Control, Key.H)]
    [InlineData(" Ctrl + Alt + H ", KeyModifiers.Control | KeyModifiers.Alt, Key.H)] // TrimEntries
    public void TryParse_AcceptsEveryDocumentedModifierSpelling(string spec, KeyModifiers expectedMods, Key expectedKey)
    {
        Assert.True(HotkeyRegistry.TryParse(spec, out var combo));
        Assert.Equal(expectedMods, combo.Modifiers);
        Assert.Equal(expectedKey, combo.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("H")]                 // no modifier at all
    [InlineData("Ctrl+")]              // modifier with no key
    [InlineData("Ctrl+NotARealKey")]   // key token doesn't parse
    [InlineData("Ctrl+H+P")]           // two non-modifier tokens
    [InlineData("Frobnicate+H")]       // unrecognised modifier token
    public void TryParse_RejectsMalformedOrIncompleteSpecs(string? spec)
    {
        Assert.False(HotkeyRegistry.TryParse(spec!, out _));
    }

    [Fact]
    public void Format_RoundTripsWhatTryParseAccepted()
    {
        Assert.True(HotkeyRegistry.TryParse("Ctrl+Alt+H", out var combo));
        Assert.Equal("Ctrl+Alt+H", HotkeyRegistry.Format(combo));

        Assert.True(HotkeyRegistry.TryParse("Cmd+Shift+P", out var meta));
        Assert.Equal("Shift+Cmd+P", HotkeyRegistry.Format(meta)); // Format's fixed order: Ctrl, Alt, Shift, Cmd
    }

    [Fact]
    public void Format_OrdersModifiersConsistentlyRegardlessOfInputOrder()
    {
        Assert.True(HotkeyRegistry.TryParse("Shift+Ctrl+Alt+H", out var combo));
        Assert.Equal("Ctrl+Alt+Shift+H", HotkeyRegistry.Format(combo));
    }

    [Fact]
    public void Resolve_UsesTheOverrideWhenItParses()
    {
        var combo = HotkeyRegistry.Resolve(HotkeyAction.ToggleOrbsVisible, "Ctrl+Shift+P");

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, combo.Modifiers);
        Assert.Equal(Key.P, combo.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a real hotkey")]
    public void Resolve_FallsBackToTheDefaultWhenTheOverrideIsMissingOrInvalid(string? overrideSpec)
    {
        var expected = HotkeyRegistry.Default(HotkeyAction.ToggleOrbsVisible);

        var combo = HotkeyRegistry.Resolve(HotkeyAction.ToggleOrbsVisible, overrideSpec);

        Assert.Equal(expected, combo);
    }

    // The new-chat hotkey: Ctrl+Alt+N by default, overridable on the same
    // terms as the toggle, and never on the toggle's chord.
    [Fact]
    public void Default_OpenNewChat_IsControlAltN()
    {
        var combo = HotkeyRegistry.Default(HotkeyAction.OpenNewChat);

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, combo.Modifiers);
        Assert.Equal(Key.N, combo.Key);
        Assert.Equal("Ctrl+Alt+N", HotkeyRegistry.Format(combo));
    }

    [Fact]
    public void Resolve_OpenNewChat_UsesTheOverrideWhenItParses()
    {
        var combo = HotkeyRegistry.Resolve(HotkeyAction.OpenNewChat, "Cmd+Shift+N");

        Assert.Equal(KeyModifiers.Meta | KeyModifiers.Shift, combo.Modifiers);
        Assert.Equal(Key.N, combo.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N")]              // a bare key is not a global hotkey
    [InlineData("Ctrl+Alt+N+M")]   // two keys
    public void Resolve_OpenNewChat_FallsBackToItsOwnDefault(string? overrideSpec)
    {
        // Its *own* default, not the toggle's — the fallback arm is per action.
        var combo = HotkeyRegistry.Resolve(HotkeyAction.OpenNewChat, overrideSpec);

        Assert.Equal(HotkeyRegistry.Default(HotkeyAction.OpenNewChat), combo);
        Assert.NotEqual(HotkeyRegistry.Default(HotkeyAction.ToggleOrbsVisible), combo);
    }

    [Fact]
    public void TryParse_RoundTripsTheNewChatDefault()
    {
        Assert.True(HotkeyRegistry.TryParse("ctrl+alt+n", out var combo));
        Assert.Equal(HotkeyRegistry.Default(HotkeyAction.OpenNewChat), combo);
    }

    // CB-220's usage-orb toggle: Ctrl+Alt+U by default, overridable and
    // falling back on the same terms as the other two.
    [Fact]
    public void Default_ToggleUsageOrbsVisible_IsControlAltU()
    {
        var combo = HotkeyRegistry.Default(HotkeyAction.ToggleUsageOrbsVisible);

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, combo.Modifiers);
        Assert.Equal(Key.U, combo.Key);
        Assert.Equal("Ctrl+Alt+U", HotkeyRegistry.Format(combo));
    }

    [Fact]
    public void Resolve_ToggleUsageOrbsVisible_UsesTheOverrideWhenItParses()
    {
        var combo = HotkeyRegistry.Resolve(HotkeyAction.ToggleUsageOrbsVisible, "Ctrl+Shift+U");

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, combo.Modifiers);
        Assert.Equal(Key.U, combo.Key);
    }

    // CB-298: the fourth action, Ctrl+Alt+M, on the same terms as the rest.
    [Fact]
    public void Default_ToggleMute_IsControlAltM()
    {
        var combo = HotkeyRegistry.Default(HotkeyAction.ToggleMute);

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, combo.Modifiers);
        Assert.Equal(Key.M, combo.Key);
        Assert.Equal("Ctrl+Alt+M", HotkeyRegistry.Format(combo));
    }

    [Fact]
    public void Resolve_ToggleMute_UsesTheOverrideWhenItParses()
    {
        var combo = HotkeyRegistry.Resolve(HotkeyAction.ToggleMute, "Ctrl+Shift+M");

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, combo.Modifiers);
        Assert.Equal(Key.M, combo.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("Ctrl+Alt+F5")]
    public void Resolve_ToggleMute_FallsBackToItsOwnDefault(string? overrideSpec)
    {
        Assert.Equal(HotkeyRegistry.Default(HotkeyAction.ToggleMute),
            HotkeyRegistry.Resolve(HotkeyAction.ToggleMute, overrideSpec));
    }

    [Fact]
    public void Plan_MuteSpelledAsTheTogglesChord_FallsBackToCtrlAltMAndSaysWhy()
    {
        var plan = HotkeyRegistry.Plan(Overrides(mute: "Ctrl+Alt+H"));

        var mute = For(plan, HotkeyAction.ToggleMute);
        Assert.Equal(new HotkeyCombo(KeyModifiers.Control | KeyModifiers.Alt, Key.M), mute.Combo);
        Assert.Contains("ToggleOrbsVisible's", mute.Note);
        Assert.Equal(CtrlAltH, For(plan, HotkeyAction.ToggleOrbsVisible).Combo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("U")]              // a bare key is not a global hotkey
    [InlineData("Ctrl+Alt+F5")]    // a key neither hook has a code for
    [InlineData("Ctrl+Alt+U+I")]   // two keys
    public void Resolve_ToggleUsageOrbsVisible_FallsBackToItsOwnDefault(string? overrideSpec)
    {
        var combo = HotkeyRegistry.Resolve(HotkeyAction.ToggleUsageOrbsVisible, overrideSpec);

        Assert.Equal(HotkeyRegistry.Default(HotkeyAction.ToggleUsageOrbsVisible), combo);
        Assert.NotEqual(HotkeyRegistry.Default(HotkeyAction.ToggleOrbsVisible), combo);
        Assert.NotEqual(HotkeyRegistry.Default(HotkeyAction.OpenNewChat), combo);
    }

    // Two actions on one chord would register twice with the OS: macOS keeps
    // the first and ignores the second, Windows' RegisterHotKey refuses it
    // outright, and on both the second action silently never fires. Every
    // action, not just the pair that exists today, so the next one added is
    // checked without anyone remembering to extend this.
    [Fact]
    public void EveryActionHasADefaultAndNoTwoDefaultsCollide()
    {
        var defaults = Enum.GetValues<HotkeyAction>().Select(HotkeyRegistry.Default).ToList();

        Assert.Equal(defaults.Count, defaults.Distinct().Count());
        Assert.Contains(HotkeyRegistry.Default(HotkeyAction.OpenNewChat), defaults);
        Assert.NotEqual(
            HotkeyRegistry.Default(HotkeyAction.ToggleOrbsVisible),
            HotkeyRegistry.Default(HotkeyAction.OpenNewChat));
    }

    // CB-197: specs Enum.TryParse<Key> used to accept that name no key a
    // person can press. Every one was a *successful* parse, so Resolve never
    // fell back — for either action, since both share TryParse.
    [Theory]
    [InlineData("Ctrl+999")]             // undefined underlying value
    [InlineData("Ctrl+-1")]              // signed number
    [InlineData("Ctrl+Alt+10")]          // two digits is a number, not a key
    [InlineData("Ctrl+Alt+05")]
    [InlineData("Ctrl+A,B")]             // flags OR — used to bind B
    [InlineData("Ctrl+Alt+LeftCtrl")]    // modifier keys as the key
    [InlineData("Ctrl+Alt+RightCtrl")]
    [InlineData("Ctrl+Alt+LeftAlt")]
    [InlineData("Ctrl+Alt+RightAlt")]
    [InlineData("Ctrl+Alt+LeftShift")]
    [InlineData("Ctrl+Alt+RightShift")]
    [InlineData("Ctrl+Alt+LWin")]
    [InlineData("Ctrl+Alt+RWin")]
    [InlineData("Ctrl+Alt+None")]        // "no key" by name
    [InlineData("Ctrl+Alt+N!")]          // junk in a name
    [InlineData("Ctrl+Alt+_N")]
    public void TryParse_RejectsWhatNamesNoPressableKey_AndBothActionsFallBack(string spec)
    {
        Assert.False(HotkeyRegistry.TryParse(spec, out _));

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.Equal(HotkeyRegistry.Default(action), HotkeyRegistry.Resolve(action, spec));
        }
    }

    // A single digit is the digit key — never its ordinal ("5" was Key.Clear,
    // "0" was Key.None) — for both actions.
    [Theory]
    [InlineData("Ctrl+Alt+0", Key.D0)]
    [InlineData("Ctrl+Alt+1", Key.D1)]
    [InlineData("Ctrl+Alt+5", Key.D5)]
    [InlineData("Ctrl+Alt+9", Key.D9)]
    [InlineData("Ctrl+Alt+D5", Key.D5)]  // the enum's own name still works
    public void TryParse_ASingleDigitIsTheDigitKey(string spec, Key expected)
    {
        Assert.True(HotkeyRegistry.TryParse(spec, out var combo));
        Assert.Equal(expected, combo.Key);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, combo.Modifiers);

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.Equal(expected, HotkeyRegistry.Resolve(action, spec).Key);
        }
    }

    // Defined, non-modifier keys neither native hook has a code for. They
    // used to parse, so Resolve kept them, and Register() quietly returned —
    // no hotkey at all and no fallback (QA round 1 on CB-196).
    [Theory]
    [InlineData("Ctrl+Alt+F5")]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Alt+Enter")]
    [InlineData("Ctrl+Alt+Return")]
    [InlineData("Ctrl+Alt+Escape")]
    [InlineData("Ctrl+Alt+Tab")]
    [InlineData("Ctrl+Alt+NumPad1")]
    [InlineData("Ctrl+Alt+OemComma")]
    [InlineData("Ctrl+Alt+Clear")]       // Key.Clear by name, the key CB-197 exists to rule out
    public void TryParse_RejectsKeysNeitherHookCanRegister_AndBothActionsFallBack(string spec)
    {
        Assert.False(HotkeyRegistry.TryParse(spec, out _));

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.Equal(HotkeyRegistry.Default(action), HotkeyRegistry.Resolve(action, spec));
        }
    }

    // The property behind the cases above, over every name Key has: nothing
    // TryParse returns is outside the registrable set.
    [Fact]
    public void EveryKeyTryParseCanReturnIsRegistrable()
    {
        foreach (var name in Enum.GetNames<Key>())
        {
            if (HotkeyRegistry.TryParse("Ctrl+Alt+" + name, out var combo))
            {
                Assert.Contains(combo.Key, HotkeyRegistry.RegistrableKeys);
            }
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            Assert.True(HotkeyRegistry.TryParse($"Ctrl+Alt+{digit}", out var combo));
            Assert.Contains(combo.Key, HotkeyRegistry.RegistrableKeys);
        }

        Assert.Equal(36, HotkeyRegistry.RegistrableKeys.Count);
        Assert.All(Enum.GetValues<HotkeyAction>(),
            a => Assert.Contains(HotkeyRegistry.Default(a).Key, HotkeyRegistry.RegistrableKeys));
    }

    // Both hooks' own tables are exactly the registrable set. Read by
    // reflection because the hooks are OS-facing and excluded from coverage;
    // reading a static dictionary makes no OS call on either platform.
    [Theory]
    [InlineData(typeof(MacOSGlobalHotkeyHook))]
    [InlineData(typeof(WindowsGlobalHotkeyHook))]
    public void EachNativeHookMapsExactlyTheRegistrableKeys(Type hook)
    {
        var field = hook.GetField("VirtualKeyCodes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new MissingFieldException(hook.Name, "VirtualKeyCodes");
        var table = (System.Collections.IDictionary)field.GetValue(null)!;

        Assert.True(HotkeyRegistry.RegistrableKeys.SetEquals(table.Keys.Cast<Key>()));
    }

    // CB-196 AC6: the collision rule, one case per arm. Plan takes the
    // overrides as a function so none of this reads settings.
    private static Func<HotkeyAction, string?> Overrides(
        string? toggle = null, string? newChat = null, string? usage = null, string? mute = null) =>
        action => action switch
        {
            HotkeyAction.ToggleOrbsVisible => toggle,
            HotkeyAction.OpenNewChat => newChat,
            HotkeyAction.ToggleUsageOrbsVisible => usage,
            HotkeyAction.ToggleMute => mute,
            _ => null
        };

    private static HotkeyBinding For(IReadOnlyList<HotkeyBinding> plan, HotkeyAction action) =>
        Assert.Single(plan, b => b.Action == action);

    private static readonly HotkeyCombo CtrlAltH = new(KeyModifiers.Control | KeyModifiers.Alt, Key.H);
    private static readonly HotkeyCombo CtrlAltN = new(KeyModifiers.Control | KeyModifiers.Alt, Key.N);
    private static readonly HotkeyCombo CtrlAltU = new(KeyModifiers.Control | KeyModifiers.Alt, Key.U);

    [Fact]
    public void Plan_NoOverrides_EveryActionGetsItsDefaultAndNoNote()
    {
        var plan = HotkeyRegistry.Plan(Overrides());

        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, plan.Count);
        Assert.Equal(new HotkeyBinding(HotkeyAction.ToggleOrbsVisible, CtrlAltH, null), For(plan, HotkeyAction.ToggleOrbsVisible));
        Assert.Equal(new HotkeyBinding(HotkeyAction.OpenNewChat, CtrlAltN, null), For(plan, HotkeyAction.OpenNewChat));
        Assert.Equal(new HotkeyBinding(HotkeyAction.ToggleUsageOrbsVisible, CtrlAltU, null), For(plan, HotkeyAction.ToggleUsageOrbsVisible));
    }

    // The usage toggle is declared last, so it is the one that gives way on
    // a contested chord — to either of the two that shipped before it.
    [Theory]
    [InlineData("Alt+Ctrl+H", "ToggleOrbsVisible")]
    [InlineData("Ctrl+Alt+N", "OpenNewChat")]
    public void Plan_UsageToggleOnAnEarlierActionsChord_FallsBackToCtrlAltUAndSaysSo(string usage, string holder)
    {
        var plan = HotkeyRegistry.Plan(Overrides(usage: usage));

        var binding = For(plan, HotkeyAction.ToggleUsageOrbsVisible);
        Assert.Equal(CtrlAltU, binding.Combo);
        Assert.Contains($"is already {holder}'s hotkey", binding.Note);
        Assert.Contains("Ctrl+Alt+U", binding.Note);
        Assert.Equal(CtrlAltH, For(plan, HotkeyAction.ToggleOrbsVisible).Combo);
        Assert.Equal(CtrlAltN, For(plan, HotkeyAction.OpenNewChat).Combo);
    }

    [Fact]
    public void Plan_ToggleBoundToCtrlAltU_UsageToggleRegistersNothingAndSaysWhy()
    {
        var plan = HotkeyRegistry.Plan(Overrides(toggle: "Ctrl+Alt+U"));

        Assert.Equal(CtrlAltU, For(plan, HotkeyAction.ToggleOrbsVisible).Combo);
        var usage = For(plan, HotkeyAction.ToggleUsageOrbsVisible);
        Assert.Null(usage.Combo);
        Assert.Contains("no hotkey registered", usage.Note);
        Assert.Contains("ToggleOrbsVisible's", usage.Note);
    }

    [Fact]
    public void Plan_DistinctOverrides_AreBothHonoured()
    {
        var plan = HotkeyRegistry.Plan(Overrides(toggle: "Ctrl+Shift+H", newChat: "Ctrl+Shift+N"));

        Assert.Equal("Ctrl+Shift+H", HotkeyRegistry.Format(For(plan, HotkeyAction.ToggleOrbsVisible).Combo!.Value));
        Assert.Equal("Ctrl+Shift+N", HotkeyRegistry.Format(For(plan, HotkeyAction.OpenNewChat).Combo!.Value));
        Assert.All(plan, b => Assert.Null(b.Note));
    }

    [Fact]
    public void Plan_NewChatOnTheTogglesChord_ToggleWinsAndNewChatFallsBackToCtrlAltN()
    {
        // The toggle's chord respelled — equal once parsed, which is the case
        // a string comparison would have missed.
        var plan = HotkeyRegistry.Plan(Overrides(newChat: "Alt+Ctrl+H"));

        Assert.Equal(new HotkeyBinding(HotkeyAction.ToggleOrbsVisible, CtrlAltH, null), For(plan, HotkeyAction.ToggleOrbsVisible));
        var newChat = For(plan, HotkeyAction.OpenNewChat);
        Assert.Equal(CtrlAltN, newChat.Combo);
        Assert.Contains("Ctrl+Alt+H is already ToggleOrbsVisible's", newChat.Note);
        Assert.Contains("Ctrl+Alt+N", newChat.Note);
    }

    [Fact]
    public void Plan_NewChatOnAnOverriddenTogglesChord_StillFallsBack()
    {
        var plan = HotkeyRegistry.Plan(Overrides(toggle: "Ctrl+Shift+J", newChat: "Shift+Ctrl+J"));

        Assert.Equal(new HotkeyCombo(KeyModifiers.Control | KeyModifiers.Shift, Key.J),
            For(plan, HotkeyAction.ToggleOrbsVisible).Combo);
        Assert.Equal(CtrlAltN, For(plan, HotkeyAction.OpenNewChat).Combo);
        Assert.NotNull(For(plan, HotkeyAction.OpenNewChat).Note);
    }

    [Fact]
    public void Plan_ToggleBoundToCtrlAltN_NewChatRegistersNothingAndSaysWhy()
    {
        var plan = HotkeyRegistry.Plan(Overrides(toggle: "Ctrl+Alt+N"));

        Assert.Equal(new HotkeyBinding(HotkeyAction.ToggleOrbsVisible, CtrlAltN, null), For(plan, HotkeyAction.ToggleOrbsVisible));
        var newChat = For(plan, HotkeyAction.OpenNewChat);
        Assert.Null(newChat.Combo);
        Assert.Contains("no hotkey registered", newChat.Note);
    }

    [Fact]
    public void Plan_BothFallbackArms_NewChatOverrideAndDefaultBothTaken_RegistersNothing()
    {
        // Toggle on Ctrl+Alt+N, New chat asking for the toggle's new chord:
        // neither its override nor its default is free.
        var plan = HotkeyRegistry.Plan(Overrides(toggle: "Ctrl+Alt+N", newChat: "Ctrl+Alt+N"));

        Assert.Null(For(plan, HotkeyAction.OpenNewChat).Combo);
        Assert.NotNull(For(plan, HotkeyAction.OpenNewChat).Note);
    }

    [Fact]
    public void Plan_NeverHandsOutOneComboTwice()
    {
        // Every pairing of a handful of chords, including each other's
        // defaults, respellings and garbage.
        var specs = new string?[] { null, "garbage", "Ctrl+Alt+H", "Alt+Ctrl+H", "Ctrl+Alt+N", "Ctrl+Shift+N", "Ctrl+Alt+U", "Ctrl+Alt+5" };
        foreach (var toggle in specs)
        foreach (var newChat in specs)
        foreach (var usage in specs)
        {
            var plan = HotkeyRegistry.Plan(Overrides(toggle, newChat, usage));
            var combos = plan.Where(b => b.Combo is not null).Select(b => b.Combo!.Value).ToList();
            Assert.Equal(combos.Count, combos.Distinct().Count());

            // The toggle is never the one that loses.
            Assert.NotNull(plan.Single(b => b.Action == HotkeyAction.ToggleOrbsVisible).Combo);
        }
    }

    [Fact]
    public void Default_UnknownAction_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HotkeyRegistry.Default((HotkeyAction)999));
    }
}
