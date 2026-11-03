using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;


namespace Orbweaver
{
    // Speaks text aloud using the platform's built-in TTS: `say` on macOS,
    // PowerShell's SpeechSynthesizer on Windows. A second call while speech
    // is in progress cancels the first, so the flyout button toggles
    // naturally between speak and stop.
    public static class TextToSpeech
    {
        // On Windows this is a last resort, not a real default: SAPI voice names
        // are fully qualified ("Microsoft David Desktop"), and the bare "David"
        // this used to be is not a name SelectVoice accepts. Measured in the
        // host Speak actually uses — Windows PowerShell 5.1 — SelectVoice('David')
        // and even SelectVoice('Microsoft David') both threw "No matching voice
        // is installed or the voice was disabled", so speaking failed outright on
        // Windows for every session that never picked a voice by hand.
        //
        // "Microsoft David Desktop" is the voice that ships with Windows itself,
        // so it is the safest literal to fall back to; anything actually
        // installed is preferred over it — see ResolveDefaultVoice.
        private const string WindowsFallbackVoice = "Microsoft David Desktop";

        public static readonly string DefaultVoice =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "Susan (Enhanced)"
                : WindowsFallbackVoice;

        private static Process? _speaking;
        private static readonly object Gate = new();

        // CB-168: the actual audio boundary, guarded process-wide rather than
        // trusted to every call site remembering to set a seam of its own.
        // Every test assembly's TestBootstrap sets this true in a
        // [ModuleInitializer] — before any test, including one nobody has
        // written yet, can reach a real `say`/SAPI/Kokoro process. It exists
        // because SpeechRequest.UtteranceForTests is a seam a *caller* has to
        // remember to set, and two tests (OrbWindowSpeakTests.
        // SpeakRemoteAsyncFindsARealHistoryEntryAndSchedulesTheRead,
        // TurnSummarySpeechTests.SpeakTurnSummaryRemoteAsyncFindsARealHistory
        // EntryAndSchedulesTheRead) reached this method via a
        // Dispatcher.UIThread.Post(...) they never awaited — the posted job
        // ran later, on whichever unrelated test next pumped the dispatcher,
        // by which point the seam that test itself might have set was long
        // gone. A per-caller seam cannot close that; a boundary this one
        // guard sits in front of can, regardless of how the call got here or
        // which test happens to be running when it arrives.
        //
        // Defaults false: production code never sets this, so the app itself
        // is never silenced by a line that exists only for the test suite.
        internal static bool SilenceForTests;

        // The one question Speak asks before it does anything: the test guard
        // above, or the user's mute (CB-298). A property of its own, not
        // inlined, because Speak is excluded from coverage for starting a real
        // engine and this decision is worth a test.
        internal static bool Suppressed => SilenceForTests || AudioMute.IsMuted;

        private static List<string>? _cachedVoices;
        private static List<VoiceOption>? _cachedOptions;

        public enum SpeakState { Idle, Preparing, Speaking }

        private static SpeakState _state = SpeakState.Idle;

        public static event Action<SpeakState>? StateChanged;

        public static SpeakState State
        {
            get { lock (Gate) return _state; }
        }

        // Still the shape callers use to decide whether the button means "speak"
        // or "stop" — Preparing counts, because pressing it again must cancel
        // rather than start a second utterance.
        public static bool IsSpeaking => State != SpeakState.Idle;

        // internal, not private: the rule below — no event when the state has not
        // actually changed — is what stops a speak button flickering between
        // "speak" and "stop", and it is decided here rather than by any process.
        internal static void Enter(SpeakState state)
        {
            lock (Gate)
            {
                if (_state == state) return;
                _state = state;
            }

            // Raised outside the lock: handlers reach into Avalonia, and holding
            // this lock across UI work is how a click that cancels deadlocks
            // against a callback that reports.
            StateChanged?.Invoke(state);
        }

        // How many times somebody has actually asked for speech to stop.
        //
        // The state alone cannot answer that question, and reading it as if it
        // could is what made a spoken summary vanish. `_state` is one static
        // shared by every orb in the process, so a pending summary that compared
        // it against Preparing on completion was really asking "has *anything*
        // touched speech in the last seven seconds" — and on a machine running
        // twenty agents, the answer is routinely yes for reasons that have
        // nothing to do with the user: a previous utterance's own Exited handler
        // calling Enter(Idle) is enough. The summary was then dropped with no
        // sound and no message.
        //
        // A counter that only Cancel moves separates the two facts. "The user
        // stopped it" is this number changing; "the state is no longer what it
        // was" is not evidence of anything, because it never was.
        private static int _stopGeneration;

        internal static int StopGeneration
        {
            get { lock (Gate) return _stopGeneration; }
        }

        public static void Cancel()
        {
            Process? victim;
            lock (Gate)
            {
                victim = _speaking;
                _speaking = null;
                _stopGeneration++;
            }

            if (victim is not null) KillTree(victim);

            Enter(SpeakState.Idle);
        }

        // Stops the process and everything it started, which for anything but the
        // simplest engine is where the sound actually comes from: a `.cmd` wrapper
        // is cmd.exe that spawns the real speaker, and killing only the tracked
        // child leaves that grandchild talking with nothing left to stop it.
        //
        // Written against the pid rather than the Process object on purpose. The
        // object can be disposed concurrently by the Exited handler the instant the
        // parent dies — observed in a real run, where the id was already
        // unreadable by the time the kill returned — and anything that reads
        // `victim.Id` or `victim.HasExited` after that point is working with a
        // corpse. The pid is captured once, first, and everything else is done with
        // that number.
        //
        // Windows gets taskkill /T /F because it is a single call that kills a tree
        // and does not care about the state of our Process object. Process.Kill(
        // entireProcessTree: true) is meant to do exactly this and could not be
        // made to fail in isolation — every standalone attempt killed the
        // grandchild correctly — yet a real run through the app orphaned one
        // surviving speech process per utterance, every time. That mechanism is
        // therefore not understood well enough to be the only thing standing
        // between a user and audio they cannot stop.
        //
        // Verified after the change: a log of the descendants either side of the
        // kill showed "conhost.exe, powershell.exe" before and "(none)" after,
        // across repeated cycles, with no survivors left running.
        // Excluded from coverage: runs taskkill and Process.Kill against a real
        // speech process tree.
        [ExcludeFromCodeCoverage]
        private static void KillTree(Process victim)
        {
            int pid;
            try
            {
                pid = victim.Id;
            }
            catch
            {
                return;   // already gone; nothing to kill and no id to kill it by
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var taskkill = Process.Start(new ProcessStartInfo("taskkill")
                    {
                        ArgumentList = { "/PID", pid.ToString(), "/T", "/F" },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    taskkill?.WaitForExit(3000);
                }
                catch { /* fall through to Kill below */ }
            }

            // Also asked directly, and last: on macOS this is the whole mechanism,
            // and on Windows it catches the case where taskkill is unavailable.
            try
            {
                if (!victim.HasExited) victim.Kill(entireProcessTree: true);
            }
            catch { /* already gone, or the object is disposed — both fine here */ }
        }

        // Cleared when the engine choice changes. The cache below lasts for the
        // process, which is right for a list the OS won't change underneath us —
        // but the *which list* question changes the moment the neural toggle
        // moves, and without this the picker would keep offering the other
        // engine's names, every one of which the new engine rejects.
        public static void InvalidateVoiceCache()
        {
            lock (Gate)
            {
                _cachedVoices = null;
                _cachedOptions = null;
            }
        }

        // Which of the three ways of speaking a voice belongs to.
        //
        // These used to be decided by precedence — a configured command beat the
        // neural engine, which beat the system voices — which meant installing one
        // silently took the others away, and the settings picker could only ever
        // show a third of what the machine could do. The engine is now a property
        // of the chosen voice instead: pick "af_bella" and you are using Kokoro,
        // pick "Microsoft Zira Desktop" and you are using SAPI, and all of them sit
        // in one list.
        public enum SpeakEngine { System, Neural, Custom }

        // One selectable voice. Label is what the settings window shows — the bare
        // name plus where it comes from, because "af_bella" and "Microsoft Zira
        // Desktop" and whatever a user's own command calls its voices are otherwise
        // three naming conventions in one dropdown with no clue which is which.
        public sealed record VoiceOption(SpeakEngine Engine, string Name, string Label);

        public static bool CustomCommandConfigured =>
            !string.IsNullOrWhiteSpace(OrbweaverSettings.SpeakCommand);

        // Everything this machine can speak with, from every engine that is
        // actually available, in the order they are worth trying: the system voices
        // always exist, Kokoro only once downloaded, a user command only once
        // configured.
        // Excluded from coverage: asks the neural engine to enumerate itself and
        // runs the listing command — two process launches; choosing from the
        // result is SelectedFrom, which is tested.
        [ExcludeFromCodeCoverage]
        public static List<VoiceOption> AllVoiceOptions()
        {
            // Cached with the same lifetime as the system list, and for a sharper
            // reason: building this asks the neural engine to enumerate itself and
            // runs the user's listing command, both of which are process launches.
            // The settings window rebuilds its whole content on every toggle and
            // every theme change, so without this each of those would spawn two
            // programs and wait for them.
            if (_cachedOptions is not null) return _cachedOptions;

            var options = new List<VoiceOption>();

            foreach (var name in SystemVoices())
            {
                options.Add(new VoiceOption(SpeakEngine.System, name, $"{name} (system)"));
            }

            if (NeuralSpeech.Available)
            {
                foreach (var name in NeuralSpeech.Voices())
                {
                    options.Add(new VoiceOption(SpeakEngine.Neural, name, $"{name} (Kokoro)"));
                }
            }

            foreach (var name in CustomCommandVoices())
            {
                options.Add(new VoiceOption(SpeakEngine.Custom, name, $"{name} (custom)"));
            }

            // A command that speaks but lists nothing still needs to be selectable,
            // or there is no way to choose it at all. It gets one entry standing for
            // "whatever that command decides".
            if (CustomCommandConfigured && !options.Any(o => o.Engine == SpeakEngine.Custom))
            {
                options.Add(new VoiceOption(SpeakEngine.Custom, "", "Custom command"));
            }

            lock (Gate) _cachedOptions = options;
            return options;
        }

        // The voice list if it has already been built, or null — never a
        // reason to build it. For the settings window's Speech row, which
        // wants to know which engines orbs' own voices land on but must not
        // launch the neural engine and the user's listing command just to draw
        // a note. In practice it is built the first time anything speaks.
        internal static IReadOnlyList<VoiceOption>? CachedVoiceOptions
        {
            get { lock (Gate) return _cachedOptions; }
        }

        // A test seam for CachedVoiceOptions, and for the option cache
        // AllVoiceOptions() returns from: the only thing that fills it in
        // production is AllVoiceOptions, which is two process launches (and on
        // macOS `say -v ?`). Seeding it lets a UI test open the real settings
        // dropdown and drive its real FillVoiceList without that scan.
        // InvalidateVoiceCache empties it again.
        internal static void SetVoiceOptionsForTests(List<VoiceOption> options)
        {
            lock (Gate) _cachedOptions = options;
        }

        // The voice currently selected, resolved against what is actually
        // available. Falls back rather than failing: a saved selection can name an
        // engine that has since been uninstalled or a voice that no longer exists,
        // and speaking in the wrong voice beats not speaking.
        public static VoiceOption? SelectedVoice() => SelectedFrom(AllVoiceOptions());

        // The resolution, separated from the enumeration that feeds it. Building
        // the option list asks the neural engine to enumerate itself and runs the
        // user's listing command — two process launches — while *choosing* from a
        // list is settings arithmetic, and it is the half with the interesting
        // rules: fall back within the engine, then fall back off it, and never
        // return nothing when there is something to speak with.
        internal static VoiceOption? SelectedFrom(List<VoiceOption> options)
        {
            if (options.Count == 0) return null;

            var engine = EngineNamed(OrbweaverSettings.SpeakEngine);

            var name = engine switch
            {
                SpeakEngine.Custom => OrbweaverSettings.SpeakCommandVoice ?? "",
                SpeakEngine.Neural => OrbweaverSettings.NeuralVoice,
                _ => OrbweaverSettings.SpeakVoice
            };

            return options.FirstOrDefault(o => o.Engine == engine
                       && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? options.FirstOrDefault(o => o.Engine == engine)
                   ?? options[0];
        }

        // The engine a stored "speakEngine" value names. Split out of
        // SelectedFrom for CB-200: the settings window has to ask "is the
        // selected engine one that can be told how loud to speak?" without
        // enumerating a single voice to find out, and that question starts
        // from exactly this mapping.
        internal static SpeakEngine EngineNamed(string? setting) => setting switch
        {
            "custom" => SpeakEngine.Custom,
            "neural" => SpeakEngine.Neural,
            _ => SpeakEngine.System
        };

        // The engine that will actually speak, as far as the settings alone can
        // say — what the Speech volume row greys and labels itself on.
        //
        // Differs from EngineNamed in one case (CB-200 QA): speakEngine still
        // reads "custom" after the speakCommand behind it was cleared. No
        // custom option exists then, so SelectedFrom falls off the engine to a
        // system voice and Speak never reaches StartCustomCommand — and a
        // system voice honours the level, so a greyed "not supported" slider
        // would be describing an engine that is not going to run. Neural needs
        // no such rule: it falls back to a system voice too, but both honour a
        // level, and the fallback-engine note already asks NeuralSpeech.
        internal static SpeakEngine EngineThatWillSpeak(string? setting, bool customCommandConfigured) =>
            EngineNamed(setting) is SpeakEngine.Custom && !customCommandConfigured
                ? SpeakEngine.System
                : EngineNamed(setting);

        // Records a choice made in the settings window, writing both which engine
        // speaks and that engine's own voice key. The per-engine keys are kept
        // separate so switching away from an engine and back remembers what was
        // chosen there — see the comments on them in OrbweaverSettings.
        public static void SelectVoice(VoiceOption option)
        {
            switch (option.Engine)
            {
                case SpeakEngine.Custom:
                    OrbweaverSettings.SpeakCommandVoice = option.Name;
                    OrbweaverSettings.SpeakEngine = "custom";
                    break;
                case SpeakEngine.Neural:
                    OrbweaverSettings.NeuralVoice = option.Name;
                    OrbweaverSettings.SpeakEngine = "neural";
                    break;
                default:
                    OrbweaverSettings.SpeakVoice = option.Name;
                    OrbweaverSettings.SpeakEngine = "system";
                    break;
            }
        }

        // The voices a user's own command says it has, one name per line on its
        // stdout. Empty when no listing command is configured, which is the normal
        // case — most wrappers speak with one fixed voice and have nothing to list.
        // Excluded from coverage: runs the user's own listing command as a
        // subprocess.
        [ExcludeFromCodeCoverage]
        public static List<string> CustomCommandVoices()
        {
            var found = new List<string>();

            var command = OrbweaverSettings.SpeakVoicesCommand;
            if (string.IsNullOrWhiteSpace(command)) return found;

            try
            {
                var startInfo = new ProcessStartInfo(command)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Its own arguments, deliberately not the speak command's: one
                // script serving both roles branches on a flag, and reusing the
                // speak arguments would hand that flag to the speaking invocation
                // too, which would list voices instead of talking.
                foreach (var argument in OrbweaverSettings.SpeakVoicesCommandArgs)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                var process = Process.Start(startInfo);
                if (process is null) return found;

                var output = process.StandardOutput.ReadToEnd();

                // Bounded, unlike the speak path: this runs while a settings
                // window is being built, and a wrapper that hangs must not hang
                // the UI with it.
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    Console.Error.WriteLine(
                        $"{Brand.DisplayName}: the speak voices command took too long and was stopped");
                    return found;
                }

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = line.Trim();
                    if (name.Length > 0) found.Add(name);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Brand.DisplayName}: couldn't list voices from the speak command: {ex.Message}");
            }

            return found;
        }

        // The platform's own voices — `say` on macOS, SAPI on Windows — and only
        // those. Kept separate from the neural and custom lists because all three
        // are now offered side by side rather than one shadowing the others; see
        // AllVoiceOptions.
        // Excluded from coverage: runs /usr/bin/say -v ? or PowerShell; the
        // parsing is ParseSayVoices and the ordering is OrderVoices, both tested.
        [ExcludeFromCodeCoverage]
        public static List<string> SystemVoices()
        {
            if (_cachedVoices is not null) return _cachedVoices;

            var voices = new List<string>();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/usr/bin/say",
                        ArgumentList = { "-v", "?" },
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });
                    if (proc is null) return voices;

                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);

                    voices.AddRange(ParseSayVoices(output));
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    voices.AddRange(WindowsVoices());
                }
            }
            catch { }

            if (voices.Count == 0) voices.Add(DefaultVoice);

            OrderVoices(voices);

            _cachedVoices = voices;
            return voices;
        }

        // Workspace metadata is written by people, while platform voices come
        // from two unrelated operating systems. Exact names win; otherwise a
        // deliberately narrow normalized match accepts a common shorthand only
        // when it identifies one installed voice. A guess between two Davids is
        // worse than falling back to the user's global voice.
        internal static string? MatchSystemVoice(string? requested, IEnumerable<string> installed)
        {
            return MatchVoiceOption(requested,
                installed.Select(name => new VoiceOption(SpeakEngine.System, name, name)))?.Name;
        }

        // A workspace voice is an engine-neutral identifier: agents may move
        // from a machine with Kokoro to one with SAPI, or use a custom command
        // alongside either.  Resolve it over every currently usable option,
        // retaining the engine as well as the name so the later speak call does
        // not accidentally send a neural identifier to the system synthesizer.
        // Exact, given-name, normalized, and shorthand matches must each be
        // unique; guessing between two engines that happen to offer the same
        // name is worse than the user's global voice.
        internal static VoiceOption? MatchVoiceOption(string? requested, IEnumerable<VoiceOption> options)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;

            var choices = options.Where(option => !string.IsNullOrWhiteSpace(option.Name)).ToList();
            var exact = choices.Where(option =>
                string.Equals(option.Name, requested.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];

            var wanted = ComparableVoice(requested);
            if (wanted.Length == 0) return null;

            // The name a person would actually write. Kokoro's identifiers
            // carry a two-letter locale-and-gender prefix — af_bella,
            // bf_isabella — and nobody writing "her voice is Bella" in a
            // CLAUDE.md means anything by the "af"; they very often do not know
            // it is there. Stripping it and comparing what is left is exact
            // rather than fuzzy, which is what keeps this above the shorthand
            // step and out of its way: Bella reaches af_bella and stops, where
            // shorthand alone matched neither af_bella nor bf_isabella and
            // gave up. Isabella still reaches bf_isabella, because their given
            // names differ — which is the case that makes prefix-stripping
            // safe to do before, rather than instead of, the checks below.
            var given = choices.Where(option => ComparableVoice(GivenName(option.Name)) == wanted).ToList();
            if (given.Count == 1) return given[0];

            var normalized = choices.Where(option => ComparableVoice(option.Name) == wanted).ToList();
            if (normalized.Count == 1) return normalized[0];

            var shorthand = choices.Where(option =>
            {
                var candidate = ComparableVoice(option.Name);
                return candidate.StartsWith(wanted, StringComparison.Ordinal)
                    || wanted.StartsWith(candidate, StringComparison.Ordinal);
            }).ToList();

            return shorthand.Count == 1 ? shorthand[0] : null;
        }

        // A persona's stated voice, which may be one name or a mixture of
        // several, resolved over the options this machine actually has.
        //
        // One entry point for both the local CLAUDE.md persona and the gateway
        // agent's profile, because they read the same grammar out of the same
        // parser and a blend honoured on one orb and ignored on the other is
        // exactly the drift PersonaMarkdown's own header exists to prevent.
        //
        // A value that is not blend-shaped goes to MatchVoiceOption unchanged,
        // so nothing about a single voice name — including a parenthesised
        // system voice like `Ava (Premium)` — moves.
        internal static VoiceOption? VoiceForPersona(
            string? requested, IEnumerable<VoiceOption> options)
        {
            var blend = VoiceBlend.Parse(requested);
            return blend is null
                ? MatchVoiceOption(requested, options)
                : VoiceBlends.Option(blend, options);
        }

        internal static string NormalizeVoice(string value) => new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        // Kokoro's two-letter locale-and-gender prefix, with its underscore,
        // or the empty string for a name that has none. Two lowercase ASCII
        // letters and an underscore is the whole convention; anything else
        // keeping an underscore in third position ("Microsoft David Desktop"
        // has none, "hi_there" would need to be a voice) is left alone by the
        // letter test rather than by a list of engines.
        //
        // Internal because VoiceBlend needs the same rule to name a blended
        // file: the engine reads a voice's language off exactly this prefix
        // and drops anything it files under another language out of an English
        // listing, so a blend of two `af_` voices has to be an `af_` voice or
        // it is invisible.
        internal static string LocalePrefix(string name)
        {
            var trimmed = name.Trim();
            return trimmed.Length > 3 && trimmed[2] == '_'
                   && char.IsAsciiLetterLower(trimmed[0]) && char.IsAsciiLetterLower(trimmed[1])
                ? trimmed[..3]
                : "";
        }

        // An engine's identifier with that prefix taken off, and the name
        // unchanged when there is no such prefix.
        private static string GivenName(string name)
        {
            var trimmed = name.Trim();
            return trimmed[LocalePrefix(trimmed).Length..];
        }

        private static string ComparableVoice(string value)
        {
            var normalized = NormalizeVoice(value);
            return normalized.StartsWith("microsoft", StringComparison.Ordinal)
                ? normalized["microsoft".Length..]
                : normalized;
        }

        // `say -v ?` output, one voice per line, as the names to offer.
        //
        // Split out of SystemVoices because this reads a format another program
        // defines, which CLAUDE.md says to cover at the parsing level as well as
        // at the seam — the two fail differently, and a parser that drops a voice
        // means a voice the user cannot pick with nothing on screen saying why.
        // The seam itself (launching /usr/bin/say) stays excluded.
        internal static List<string> ParseSayVoices(string output)
        {
            var voices = new List<string>();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Lines: "Ava (Premium)       en_US    # Hello! My name is Ava."
                // Strip the sample text after '#' first, then the locale is the
                // last whitespace-delimited token in what remains.
                var hashIdx = line.IndexOf('#');
                var meta = hashIdx >= 0 ? line[..hashIdx] : line;

                var parts = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var locale = parts[^1];
                if (!locale.StartsWith("en_")) continue;

                var localeStart = meta.LastIndexOf(locale, StringComparison.Ordinal);
                if (localeStart < 1) continue;
                var name = meta[..localeStart].TrimEnd();

                voices.Add(name);
            }

            return voices;
        }

        // Premium and Enhanced voices first — they're what people want. In place,
        // and internal, because the order is what the settings window shows and
        // "the good ones are at the top" is a claim worth asserting.
        internal static void OrderVoices(List<string> voices)
        {
            int Tier(string n) =>
                n.Contains("(Premium)") ? 0 :
                n.Contains("(Enhanced)") ? 1 : 2;

            voices.Sort((a, b) =>
            {
                var cmp = Tier(a).CompareTo(Tier(b));
                return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Asked of the same host that does the speaking — `powershell`, i.e.
        // Windows PowerShell 5.1 — and that detail is the whole point rather
        // than an implementation convenience. Enumerated from PowerShell 7 this
        // machine reported five voices including "Microsoft David"; from 5.1 it
        // reported two, "Microsoft David Desktop" and "Microsoft Zira Desktop".
        // 5.1's System.Speech sees only the SAPI5 registry hive, while 7's sees
        // the Speech_OneCore hive too, so a list gathered from the wrong host
        // offers voices that SelectVoice then refuses. Both names 5.1 reported
        // were confirmed to select successfully.
        //
        // A consequence worth knowing, and the reason the settings link says
        // what it says: voices added through Windows' own Speech settings land
        // in Speech_OneCore, so they will not appear here until this speaks
        // through something that reads that hive.
        // Excluded from coverage: runs Windows PowerShell 5.1 to enumerate SAPI
        // voices.
        [ExcludeFromCodeCoverage]
        private static List<string> WindowsVoices()
        {
            var found = new List<string>();

            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "Add-Type -AssemblyName System.Speech; " +
                    "(New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | " +
                    "Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq 'en' } | " +
                    "ForEach-Object { $_.VoiceInfo.Name }"
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true   // a WinExe parent shows a console without this
            });
            if (proc is null) return found;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim();
                if (name.Length > 0) found.Add(name);
            }

            return found;
        }

        // Excluded from coverage: starts a speech engine and makes the machine
        // make a noise.
        [ExcludeFromCodeCoverage]
        public static void Speak(string text, VoiceOption voice, double? rate = null) =>
            Speak(text, voice.Name, forceSystemVoice: voice.Engine == SpeakEngine.System,
                forceEngine: voice.Engine, rate: rate);

        [ExcludeFromCodeCoverage]
        public static void Speak(string text, string? voice = null, bool forceSystemVoice = false,
            SpeakEngine? forceEngine = null, double? rate = null)
        {
            // The guard, first thing, ahead of even Cancel(): a silenced call
            // has no side effects at all, not even stopping whatever a real
            // (non-test) utterance elsewhere might be doing. See
            // SilenceForTests' own comment on why this exists.
            // CB-298: the mute sits on the same line, for the same reason —
            // every way of speaking comes through here, so this is the one
            // place "muted" cannot be bypassed by a caller that forgot to ask.
            if (Suppressed) return;

            Cancel();

            if (string.IsNullOrWhiteSpace(text)) return;

            // Legacy callers can explicitly force a system label. Workspace
            // metadata now carries a resolved VoiceOption instead, but this
            // guard still prevents old callers from handing SAPI or say a name
            // which its own engine cannot use.
            if (forceSystemVoice && (string.IsNullOrWhiteSpace(voice)
                || !SystemVoices().Contains(voice, StringComparer.OrdinalIgnoreCase)))
            {
                voice = null;
                forceSystemVoice = false;
            }

            // Whichever engine owns the selected voice, rather than a fixed
            // precedence: all three are offered together now, so the choice made in
            // the settings window is the choice, not a hint that something else can
            // override.
            var selected = forceEngine is { } engine
                ? new VoiceOption(engine, voice ?? "", "")
                : SelectedVoice();

            // A user command is *not* fallen back from. Someone who configured
            // their own engine wants that engine; a silent substitution to a
            // robotic system voice would look like their command working badly
            // rather than not running, which is the harder failure to diagnose. It
            // reports and stays quiet instead.
            if (!forceSystemVoice && selected?.Engine == SpeakEngine.Custom
                && StartCustomCommand(text, selected.Name)) return;

            // The neural engine *is* fallen through from rather than trusted: if it
            // can't start — a partial download, a model deleted by hand — the same
            // click still speaks with a system voice. Nobody hand-configured this
            // one, so quietly using another is a repair rather than a substitution,
            // and speaking worse is a much better failure than not speaking, which
            // is the shape of bug the comment at the top of this file exists
            // because of.
            if (!forceSystemVoice && selected?.Engine == SpeakEngine.Neural
                && NeuralSpeech.Available
                && StartNeural(text, selected.Name, rate))
            {
                return;
            }

            // A system voice, either because it was the one chosen or because
            // whichever engine was chosen could not be reached.
            voice ??= selected?.Engine == SpeakEngine.System && selected.Name.Length > 0
                ? selected.Name
                : DefaultVoice;

            // Linux and anything else: no system voice to start.
            var startInfo = SystemSpeechStartInfo(text, voice,
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
                : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
                : OSPlatform.Linux);
            if (startInfo is null) return;

            var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            proc.Exited += (_, _) => Finished(proc);

            lock (Gate) _speaking = proc;

            // Straight to Speaking: `say` and SAPI both start audio in about half
            // a second, so a Preparing state would flicker rather than inform.
            Enter(SpeakState.Speaking);

            try
            {
                proc.Start();
            }
            catch
            {
                lock (Gate) _speaking = null;
                proc.Dispose();
                Enter(SpeakState.Idle);
            }
        }

        // What starts one system-voice utterance on `platform`, or null where
        // there is no system voice. Split out of Speak, which is excluded for
        // launching a real engine, because this is where the Speech level is
        // read and applied — and CB-200 QA mutated exactly those lines (the
        // read replaced with 1.0, SayText replaced with the bare text) and the
        // suites stayed green, since nothing outside the exclusion saw them.
        //
        // The platform is a parameter rather than asked of the OS so both
        // arms run on either CI leg; Speak passes the real one.
        //
        // Read once per utterance, so moving the slider mid-sentence takes
        // effect from the next one rather than half-way through.
        internal static ProcessStartInfo? SystemSpeechStartInfo(string text, string voice, OSPlatform platform) =>
            SystemSpeechStartInfo(text, voice, platform, OrbweaverSettings.SpeechVolume);

        internal static ProcessStartInfo? SystemSpeechStartInfo(string text, string voice, OSPlatform platform,
            double volume)
        {
            ProcessStartInfo startInfo;
            if (platform == OSPlatform.OSX)
            {
                // `say` has no volume flag; [[volm]] embedded in the text is
                // how it is told. See AudioVolume.SayText.
                startInfo = new ProcessStartInfo("/usr/bin/say")
                {
                    ArgumentList = { "-v", voice, AudioVolume.SayText(text, volume) }
                };
            }
            else if (platform == OSPlatform.Windows)
            {
                startInfo = new ProcessStartInfo("powershell")
                {
                    ArgumentList = { "-NoProfile", "-Command", WindowsSpeakScript(volume) }
                };
                startInfo.EnvironmentVariables[SpeakTextEnvVar] = text;
                startInfo.EnvironmentVariables[SpeakVoiceEnvVar] = voice;
            }
            else
            {
                return null;
            }

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            return startInfo;
        }

        // The Windows PowerShell script that speaks one utterance through SAPI.
        // A value rather than built inline inside Speak, which is excluded for
        // starting a real engine, so the CB-200 volume line — and the escaping
        // it sits beside — can be asserted on without a Windows machine to run
        // it on.
        //
        // SelectVoice is guarded so an unusable voice name costs the *choice*
        // of voice, not the speech. It throws rather than returning false when
        // a name doesn't match, and with stderr discarded the only symptom was
        // silence — which is how a bad default ("David", never a real SAPI
        // name) read as "the speak button does nothing". A voice saved before
        // that fix, or one that has since been uninstalled, lands in the same
        // place and still speaks.
        //
        // Volume is only set below full, so at the default this is exactly the
        // script every earlier build ran; SAPI's own default is 100. The value
        // is an integer SapiVolume has already clamped, because the property
        // throws outside 0-100 — measured, not assumed (see AudioVolume).
        //
        // CB-184: neither the text nor the voice is in the script. Both were
        // once interpolated into single-quoted literals with ' doubled, but
        // PowerShell also accepts U+2018 through U+201B as quote delimiters,
        // so a curly quote in either one closed the literal early: a
        // ParserError (silent, since stderr is discarded) or whatever followed
        // it run as a command. The spoken text is the likelier carrier, being
        // whatever an LLM wrote, and it was exactly as exposed as the voice.
        // They now travel in the environment — the shape CB-167 gave
        // ChimePlayer — and the script only reads them back, so no character
        // in either can be parsed as anything.
        internal const string SpeakTextEnvVar = "CLAUDEBUDDY_SPEAK_TEXT";
        internal const string SpeakVoiceEnvVar = "CLAUDEBUDDY_SPEAK_VOICE";

        internal static string WindowsSpeakScript(double volume)
        {
            var volumeLine = AudioVolume.IsFull(volume)
                ? ""
                : "$s.Volume = "
                  + AudioVolume.SapiVolume(volume).ToString(System.Globalization.CultureInfo.InvariantCulture)
                  + "; ";

            return "Add-Type -AssemblyName System.Speech; " +
                   "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                   $"try {{ $s.SelectVoice($env:{SpeakVoiceEnvVar}) }} catch {{ }}; " +
                   volumeLine +
                   $"$s.Speak($env:{SpeakTextEnvVar})";
        }

        // Everything about starting the user's speak command except the start:
        // its argv and environment. Out of StartCustomCommand, which is
        // excluded for launching someone else's program, for the reason
        // SystemSpeechStartInfo and NeuralSpeech.StartInfoFor are — the Speech
        // level is set here, and a line inside an exclusion is a line no
        // mutant can be caught removing.
        //
        // The shorter overload is what StartCustomCommand calls, and it is
        // where the level is read.
        internal static ProcessStartInfo CustomCommandStartInfo(string command, string? voice) =>
            CustomCommandStartInfo(command, voice, OrbweaverSettings.SpeechVolume);

        internal static ProcessStartInfo CustomCommandStartInfo(string command, string? voice, double volume)
        {
            var startInfo = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in OrbweaverSettings.SpeakCommandArgs)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // The chosen voice arrives as an environment variable rather than an
            // argument. SpeakCommandArgs belongs to the user, and appending a
            // positional argument would break any wrapper that takes fixed ones;
            // a command that doesn't read this never notices it. Set even when
            // empty so a wrapper can tell "no choice made" from a stale value
            // inherited from this process's own environment.
            startInfo.Environment["CLAUDEBUDDY_VOICE"] = voice ??
                OrbweaverSettings.SpeakCommandVoice ?? "";

            // CB-200: the Speech level, 0 to 1, invariant ("0.5", never "0,5"),
            // under the same name the Kokoro engine reads. Optional for the
            // command — one that ignores it speaks exactly as it always did —
            // which is why it can be offered at all when this contract once
            // had nowhere to put a level.
            //
            // Always set, including "1" at full volume, unlike the engine's
            // copy: for the same reason CLAUDEBUDDY_VOICE is always set, a
            // wrapper reading it must never see a stale value inherited from
            // this process's own environment. The engine can leave it unset
            // because unset has always meant full volume there; a user's
            // wrapper has no such history to rely on.
            startInfo.Environment[AudioVolume.SpeechVolumeEnvVar] = AudioVolume.Format(volume);

            return startInfo;
        }

        // Whatever the user pointed OrbweaverSettings.SpeakCommand at. Returns
        // false only when no command is configured — a configured command that
        // fails to launch returns true, having reported why, because falling
        // through to a system voice would disguise the problem.
        //
        // The interface is the one this class already had for `say` and
        // PowerShell, which is the point: text on stdin, exit when finished,
        // killed to cancel. Nothing else is required of it — printing "speaking"
        // on stdout when audio starts is optional and only sharpens the button's
        // state, never a condition of working.
        // Excluded from coverage: starts the user's command as a subprocess.
        [ExcludeFromCodeCoverage]
        private static bool StartCustomCommand(string text, string? voice = null)
        {
            var command = OrbweaverSettings.SpeakCommand;
            if (string.IsNullOrWhiteSpace(command)) return false;

            var startInfo = CustomCommandStartInfo(command, voice);

            var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // Preparing until the command says otherwise, which is the opposite
            // default to the system voices and deliberate. Those start audio in
            // about half a second; someone else's chain could be instant or could
            // load a model for ten seconds, and there is no way to tell from out
            // here. So the hourglass is the honest starting claim — "we asked, it
            // is running" — and printing the readiness marker is what upgrades it
            // to a stop square at the real moment.
            //
            // A command that never prints anything therefore shows the hourglass
            // for its whole run. That is the right way round: the button still
            // cancels either way, and an hourglass over speech is a smaller lie
            // than a stop square over silence.
            Enter(SpeakState.Preparing);

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null && e.Data.StartsWith("speaking", StringComparison.Ordinal))
                {
                    Enter(SpeakState.Speaking);
                }
            };

            // Surfaced, not swallowed: this is somebody else's program, and its
            // stderr is the only explanation anyone will get for why their voice
            // didn't happen.
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Console.Error.WriteLine($"{Brand.DisplayName}: speak command: {e.Data}");
                }
            };

            proc.Exited += (_, _) => Finished(proc);
            lock (Gate) _speaking = proc;

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                proc.StandardInput.Write(text);
                proc.StandardInput.Close();

                // Exited can fire before the handler above is attached for a
                // command that fails instantly, which would otherwise leave the
                // button stuck showing stop with nothing behind it.
                if (proc.HasExited) Finished(proc);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"{Brand.DisplayName}: couldn't start the configured speak command '{command}': {ex.Message}");

                lock (Gate) _speaking = null;
                proc.Dispose();
                Enter(SpeakState.Idle);
                return true;
            }
        }

        // Excluded from coverage: starts the Kokoro side-car process.
        [ExcludeFromCodeCoverage]
        private static bool StartNeural(string text, string? voice = null, double? rate = null)
        {
            // Announced before the process exists, because starting it is itself
            // part of the wait being announced.
            Enter(SpeakState.Preparing);

            var proc = NeuralSpeech.Start(
                text,
                voice ?? OrbweaverSettings.NeuralVoice,
                rate,
                onSpeaking: () => Enter(SpeakState.Speaking));

            if (proc is null)
            {
                Enter(SpeakState.Idle);
                return false;
            }

            proc.Exited += (_, _) => Finished(proc);
            lock (Gate) _speaking = proc;

            // Exited can fire before the handler above is attached for a process
            // that failed instantly, which would leave the button stuck showing
            // Preparing forever. Checking afterwards closes that window.
            if (proc.HasExited) Finished(proc);

            return true;
        }

        // One exit path for both engines. Guarded on identity rather than a flag:
        // a cancel replaces _speaking immediately, so the *previous* process's
        // Exited arriving late must not report Idle over speech that has already
        // started.
        // Excluded from coverage: an Exited handler for a real process.
        [ExcludeFromCodeCoverage]
        private static void Finished(Process proc)
        {
            bool current;
            lock (Gate)
            {
                current = _speaking == proc;
                if (current) _speaking = null;
            }

            try { proc.Dispose(); } catch { }

            if (current) Enter(SpeakState.Idle);
        }
    }
}
