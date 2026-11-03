using Avalonia.Input;

namespace Orbweaver
{
    // What a global hotkey does, once pressed. CB-155 asks for one — hide/show
    // orbs — "plus room for a couple more" without saying which, so this is an
    // enum rather than a hardcoded shortcut wired straight into App.axaml.cs:
    // the next hotkey is a new case here, a Default() arm, an arm in each of
    // HotkeyActions' two switches, and nothing else — no new registry, no new
    // settings-parsing code, no new platform plumbing.
    //
    // OpenNewChat is that second case: the tray's "New chat…" item, reachable
    // without finding the menu bar icon first. It proved the claim above —
    // neither native hook changed to add it.
    //
    // ToggleUsageOrbsVisible is the third (CB-220): hide only the account
    // (usage) orbs, leaving the session orbs where they are. The orb toggle
    // above still means every orb, usage ones included.
    //
    // ToggleMute is the fourth (CB-298): the global mute, the same switch as
    // the tray's "Mute" item and the chat panel's speaker button.
    //
    // Declaration order is also priority: HotkeyRegistry.Plan gives a chord
    // two actions both resolve to to whichever is declared first, so a new
    // action goes at the end, where it can never take a chord from one that
    // shipped before it.
    public enum HotkeyAction
    {
        ToggleOrbsVisible,
        OpenNewChat,
        ToggleUsageOrbsVisible,
        ToggleMute
    }

    // One action's registration, as HotkeyRegistry.Plan decided it: the combo
    // to register, or null for none, and a sentence for hotkeys.log when the
    // action did not get what its setting asked for.
    public readonly record struct HotkeyBinding(HotkeyAction Action, HotkeyCombo? Combo, string? Note);

    // One key combination: the modifiers held down plus the key that completes
    // them. A record rather than a tuple so ToString()/Equals() are free and
    // the two platform hooks can compare "what's registered" against "what
    // settings now says" without hand-rolling equality.
    public readonly record struct HotkeyCombo(KeyModifiers Modifiers, Key Key)
    {
        public override string ToString() => HotkeyRegistry.Format(this);
    }

    // Parses, formats and resolves hotkey bindings — pure string/enum logic
    // with no window, no settings read and no OS call anywhere in it, on the
    // same principle as OrbArrangement and OrbGlyph: the *rule* for which key
    // combo an action maps to is cheap to get wrong and cheap to test, so it
    // lives somewhere a unit test can reach it without constructing anything.
    //
    // GlobalHotkeys (the platform-facing half) is what actually reads
    // OrbweaverSettings and calls into a native hook; this class never
    // touches either.
    public static class HotkeyRegistry
    {
        // macOS reserves plain Cmd+H to hide the frontmost app, and Cmd+Shift+H
        // is Finder's "move to trash"-adjacent muscle memory for a lot of
        // people, so this leans on Ctrl+Alt rather than Cmd/Option alone.
        // Windows has no standing claim on Ctrl+Alt+H, and testing showed no
        // conflict with the handful of default Windows/Explorer bindings that
        // use H (Ctrl+Shift+H in some browsers is "History", not the same
        // chord). One default across both platforms is simpler to document and
        // simpler for a settings.json override to reason about than a
        // per-platform pair would be, and nothing here rules out moving to
        // per-platform defaults later if a real conflict turns up.
        private static readonly HotkeyCombo ToggleOrbsDefault =
            new(KeyModifiers.Control | KeyModifiers.Alt, Key.H);

        // Same Ctrl+Alt reasoning as the toggle, with N for "new". Cmd+N is
        // every app's own "new document" and would be stolen from whatever is
        // frontmost; Ctrl+Alt+N has no standing claim on either platform.
        // Distinct from ToggleOrbsDefault by construction, and
        // HotkeyRegistryTests pins that every default is distinct from every
        // other — two actions on one chord would register once and silently
        // drop the second on both platforms.
        private static readonly HotkeyCombo OpenNewChatDefault =
            new(KeyModifiers.Control | KeyModifiers.Alt, Key.N);

        // Ctrl+Alt again, with U for "usage". Nothing else this app registers
        // uses U, and neither platform has a standing system claim on
        // Ctrl+Alt+U.
        private static readonly HotkeyCombo ToggleUsageOrbsDefault =
            new(KeyModifiers.Control | KeyModifiers.Alt, Key.U);

        // Ctrl+Alt+M, for "mute". Neither platform has a standing system
        // claim on it. Where Ctrl+Alt doubles as AltGr (German, Polish and
        // Hungarian layouts on Windows) AltGr+M types a µ and RegisterHotKey
        // will take it — the three defaults above share that exposure, the key
        // is rebindable, and hotkeys.log records a refusal.
        private static readonly HotkeyCombo ToggleMuteDefault =
            new(KeyModifiers.Control | KeyModifiers.Alt, Key.M);

        public static HotkeyCombo Default(HotkeyAction action) => action switch
        {
            HotkeyAction.ToggleOrbsVisible => ToggleOrbsDefault,
            HotkeyAction.OpenNewChat => OpenNewChatDefault,
            HotkeyAction.ToggleUsageOrbsVisible => ToggleUsageOrbsDefault,
            HotkeyAction.ToggleMute => ToggleMuteDefault,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        // The binding actually in effect: whatever settings.json names for
        // this action, or the built-in default if it names nothing or names
        // something that doesn't parse. A bad string in a hand-edited
        // settings.json is a reason to fall back, not a reason to leave the
        // hotkey unregistered — the toggle other than the file is still worth
        // having.
        public static HotkeyCombo Resolve(HotkeyAction action, string? overrideSpec)
        {
            if (!string.IsNullOrWhiteSpace(overrideSpec) && TryParse(overrideSpec, out var combo))
            {
                return combo;
            }

            return Default(action);
        }

        // Every action's registration at once, with no combo ever handed out
        // twice. Two registrations of one chord don't share it: macOS keeps
        // the first and the second never fires, and Windows' RegisterHotKey
        // refuses the second outright — either way one hotkey silently does
        // nothing. Resolving each action on its own can't see that, because
        // "Alt+Ctrl+H" is a perfectly good override for New chat right up
        // until it turns out to be the toggle's chord respelled.
        //
        // Actions are taken in declaration order and the earlier one keeps a
        // contested chord (CB-196: the toggle shipped first, so it wins). The
        // later one falls back to its own default; if that is taken too —
        // someone bound the toggle to Ctrl+Alt+N — it registers nothing,
        // rather than stealing a chord or registering it twice. Both outcomes
        // carry a Note, because in both the person's setting did not do what
        // they wrote it to do and nothing on screen will say so.
        public static IReadOnlyList<HotkeyBinding> Plan(Func<HotkeyAction, string?> overrideFor)
        {
            var taken = new Dictionary<HotkeyCombo, HotkeyAction>();
            var plan = new List<HotkeyBinding>();

            foreach (var action in Enum.GetValues<HotkeyAction>())
            {
                var wanted = Resolve(action, overrideFor(action));
                if (!taken.TryGetValue(wanted, out var holder))
                {
                    taken[wanted] = action;
                    plan.Add(new HotkeyBinding(action, wanted, null));
                    continue;
                }

                var fallback = Default(action);
                if (!taken.TryGetValue(fallback, out var fallbackHolder))
                {
                    taken[fallback] = action;
                    plan.Add(new HotkeyBinding(action, fallback,
                        $"{action}: {Format(wanted)} is already {holder}'s hotkey, so {action} uses its default {Format(fallback)} instead."));
                    continue;
                }

                plan.Add(new HotkeyBinding(action, null,
                    $"{action}: no hotkey registered — {Format(wanted)} is {holder}'s hotkey and the default {Format(fallback)} is {fallbackHolder}'s."));
            }

            return plan;
        }

        // "Ctrl+Alt+H", "Cmd+Shift+P", "Control+Alt+H" — case-insensitive,
        // any order, "+"-joined, the last token the key and everything before
        // it a modifier. Cmd/Command/Win/Super/Meta all mean the same
        // KeyModifiers.Meta bit, since which physical key that is differs by
        // platform and the settings.json author shouldn't have to know which
        // OS they're writing for.
        public static bool TryParse(string spec, out HotkeyCombo combo)
        {
            combo = default;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false; // a bare key with no modifier isn't a global hotkey

            var modifiers = KeyModifiers.None;
            Key? key = null;

            foreach (var part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= KeyModifiers.Control;
                        break;
                    case "alt":
                    case "option":
                        modifiers |= KeyModifiers.Alt;
                        break;
                    case "shift":
                        modifiers |= KeyModifiers.Shift;
                        break;
                    case "cmd":
                    case "command":
                    case "win":
                    case "windows":
                    case "super":
                    case "meta":
                        modifiers |= KeyModifiers.Meta;
                        break;
                    default:
                        // Only one non-modifier token is allowed; a second one
                        // ("Ctrl+H+P") is a malformed spec, not "pick the last".
                        if (key is not null) return false;
                        if (!TryParseKey(part, out var parsedKey)) return false;
                        key = parsedKey;
                        break;
                }
            }

            if (key is null || modifiers == KeyModifiers.None) return false;

            combo = new HotkeyCombo(modifiers, key.Value);
            return true;
        }

        // The key token, and only the forms a person means by it. This used to
        // be a bare Enum.TryParse<Key>, which accepts far more than key
        // names (CB-197): a number is read as the enum's underlying value, so
        // "Ctrl+Alt+5" bound Key.Clear and "Ctrl+Alt+0" bound Key.None, and
        // "Ctrl+999" an undefined key — all *successful* parses, so Resolve
        // never fell back and the hotkey sat on a key nobody can press. A
        // comma is read as a flags OR ("A,B" is B), and a modifier key parses
        // as a key like any other ("Ctrl+Alt+LeftCtrl").
        //
        // A defined, non-modifier key is still not enough: both native hooks
        // map only A–Z and D0–D9, and each Register() quietly returns on
        // anything else, so "Ctrl+Alt+F5" parsed, never fell back, and
        // registered nothing at all. The rule is therefore the hooks' own
        // table, RegistrableKeys, and nothing wider.
        //
        // So: a single digit is the digit key, since that is what anyone
        // typing "Ctrl+Alt+5" means. Anything else must be a *name* — ASCII
        // letters and digits, starting with a letter, which rules out
        // numbers, signs and commas before Enum.TryParse ever sees them —
        // naming a key in RegistrableKeys. That excludes the modifiers, None
        // and Clear by construction rather than by a list of exceptions.
        private static bool TryParseKey(string token, out Key key)
        {
            key = default;

            if (token.Length == 1 && char.IsAsciiDigit(token[0]))
            {
                key = Key.D0 + (token[0] - '0');
                return true;
            }

            // A loop rather than token.All(char.IsAsciiLetterOrDigit): the
            // method group compiles to a cached delegate whose null check the
            // MTP coverage engine reports as two phantom branch arcs.
            if (!char.IsAsciiLetter(token[0])) return false;
            foreach (var c in token)
            {
                if (!char.IsAsciiLetterOrDigit(c)) return false;
            }
            if (!Enum.TryParse(token, ignoreCase: true, out key)) return false;

            return RegistrableKeys.Contains(key);
        }

        // Every key a hotkey can end in: the ones both native hooks have a
        // code for. MacOSGlobalHotkeyHook and WindowsGlobalHotkeyHook each
        // keep their own table, because the codes differ, and
        // HotkeyRegistryTests checks both tables against this set, so
        // widening one without the other and without this fails a test
        // rather than a user. Widening it at all needs each new key's native
        // code on both platforms, confirmed by a real key press.
        public static readonly IReadOnlySet<Key> RegistrableKeys = BuildRegistrableKeys();

        private static HashSet<Key> BuildRegistrableKeys()
        {
            var keys = new HashSet<Key>();
            for (var k = Key.A; k <= Key.Z; k++) keys.Add(k);
            for (var k = Key.D0; k <= Key.D9; k++) keys.Add(k);
            return keys;
        }

        // The inverse of TryParse, and the form a settings.json author can
        // paste straight back in — Format(Resolve(a, s)) round-trips for any
        // spec TryParse accepted, which HotkeyRegistryTests checks directly.
        public static string Format(HotkeyCombo combo)
        {
            var pieces = new List<string>(4);
            if (combo.Modifiers.HasFlag(KeyModifiers.Control)) pieces.Add("Ctrl");
            if (combo.Modifiers.HasFlag(KeyModifiers.Alt)) pieces.Add("Alt");
            if (combo.Modifiers.HasFlag(KeyModifiers.Shift)) pieces.Add("Shift");
            if (combo.Modifiers.HasFlag(KeyModifiers.Meta)) pieces.Add("Cmd");
            pieces.Add(combo.Key.ToString());
            return string.Join("+", pieces);
        }
    }
}
