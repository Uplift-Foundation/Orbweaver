using System.Runtime.CompilerServices;
using Orbweaver;

namespace Orbweaver.Tests
{
    // One check per real capture in Captures/cb327 (CB-327): what the typing
    // gate makes of it, and for a form what the form parser reads. The table
    // is the README's "should be" column, so a capture that stops reading as
    // the README says fails here by name.
    //
    // The captures are loaded from the files rather than copied into
    // PaneFixtures: they are byte-for-byte tmux output, 36 of them, and a
    // second copy in a raw string would be a second place to be wrong. The
    // directory is found from this source file's own path, which holds for the
    // console exe and for tests/UnitTests (both compile this file in).
    internal delegate void CheckFn(string name, bool ok, string detail = "");

    internal static class Cb327Captures
    {
        private static readonly string Dir = Path.Combine(
            Path.GetDirectoryName(Where())!, "Captures", "cb327");

        private static string Where([CallerFilePath] string path = "") => path;

        internal static string Load(string name) => File.ReadAllText(Path.Combine(Dir, name + ".txt"));

        // A capture by its full file name, for the transcript rows (.jsonl) that
        // pair with the ask-* screens (CB-327 items 4 and 5).
        internal static string PathOf(string file) => Path.Combine(Dir, file);

        private const string Question = "Which venue style should the team offsite use?";

        internal static void Run(CheckFn check)
        {
            // --- the gate's reading of every capture ---

            foreach (var name in new[]
            {
                "idle-after-turns-w20", "idle-after-turns-w23", "idle-named-draft-w23", "idle-named-w100",
                "idle-named-w12", "idle-named-w15", "idle-named-w18", "idle-named-w20", "idle-named-w23",
                "idle-named-w32", "idle-named-w40", "idle-named-w60", "idle-named-w80",
                "idle-short-w23", "idle-unnamed-w23",
            })
            {
                var (reading, why) = PaneScreen.Read(Load(name));
                check($"{name} reads as the prompt", reading == PaneReading.Prompt, $"{reading} ({why})");
            }

            foreach (var name in new[]
            {
                "form1-w23", "form1-w32", "form1-w60", "form1-w100", "form4-w32", "form4-w60", "form4-w100",
                "form4long-w40", "form4long-w60", "form4long-w80", "form4long-w100",
                "modelswitch-w20", "modelswitch-w23", "modelswitch-w60",
                "model-w100", "model-w60", "model-w40", "model-w32-h60",
            })
            {
                var (reading, why) = PaneScreen.Read(Load(name));
                check($"{name} reads as a dialog", reading == PaneReading.Dialog, $"{reading} ({why})");
            }

            // Negative controls: every one was Unknown on develop and must stay so.
            foreach (var name in new[]
            {
                "form4-w23", "model-w23", "model-w32", "permissions-w23", "trust-w32", "trust-w100",
            })
            {
                var (reading, why) = PaneScreen.Read(Load(name));
                check($"{name} is never a prompt or a dialog", reading == PaneReading.Unknown, $"{reading} ({why})");
            }

            // CB-400: the 32-column /permissions capture has the `▔` edge and an `Esc to cancel`
            // footer, so it is a pane Esc leaves: still never a prompt, now named as an overlay.
            var (permReading, permWhy) = PaneScreen.Read(Load("permissions-w32"));
            check("permissions-w32 is a pane to leave, never a prompt", permReading == PaneReading.Dialog && permWhy == "overlay", $"{permReading} ({permWhy})");

            // --- the forms ---

            foreach (var (name, tabs, options) in new[]
            {
                ("form1-w23", 1, 5), ("form1-w32", 1, 5), ("form1-w60", 1, 5), ("form1-w100", 1, 5),
                ("form4-w32", 5, 6), ("form4-w60", 5, 6), ("form4-w100", 5, 6),
                ("form4long-w40", 5, 6), ("form4long-w60", 5, 6), ("form4long-w80", 5, 6), ("form4long-w100", 5, 6),
            })
            {
                var form = ChatTranscript.ParseAskForm(Load(name));
                check($"{name} yields a form", form is not null);
                if (form is null) continue;

                check($"{name}: {tabs} tab(s)", form.Tabs.Count == tabs, form.Tabs.Count.ToString());
                check($"{name}: the whole question, joined", form.Prompt.Title == Question, form.Prompt.Title);
                check($"{name}: {options} options keyed 1..{options}",
                    form.Prompt.Options.Select(o => o.Key).SequenceEqual(Enumerable.Range(1, options).Select(i => i.ToString())),
                    string.Join(",", form.Prompt.Options.Select(o => o.Key)));

                // Labels begin as the question's own options do. Whether an option's
                // description ends up in its label depends on whether the label's last
                // line filled the row (the screen alone cannot say) - that is what
                // reading the transcript is for - so only the start is pinned.
                var starts = new[] { "Book a lakeside", "Rent a downtown", "Hold the offsite", "Use a restored" };
                var real = options - 2;
                for (var i = 0; i < real; i++)
                    check($"{name}: option {i + 1} is \"{starts[i]}…\"",
                        form.Prompt.Options[i].Label.StartsWith(starts[i], StringComparison.Ordinal), form.Prompt.Options[i].Label);

                check($"{name}: Type something is {options - 1}", form.FreeTextKey == (options - 1).ToString(), form.FreeTextKey ?? "none");
                check($"{name}: Chat about this is {options}", form.ChatAboutKey == options.ToString(), form.ChatAboutKey ?? "none");
            }

            // The names a 32-column bar had no room for come from its second row.
            var narrow = ChatTranscript.ParseAskForm(Load("form4-w32"))!;
            check("form4-w32: tab names come from the bar's second row",
                narrow.Tabs.Select(t => t.Name).SequenceEqual(new[] { "Venue", "C…", "S…", "T…", "Submit" }),
                string.Join("|", narrow.Tabs.Select(t => t.Name)));
            check("form4-w32: the last tab is Submit",
                narrow.Tabs[^1].State == AskTabState.Submit && narrow.Tabs.Take(4).All(t => t.State == AskTabState.Open));

            var wide = ChatTranscript.ParseAskForm(Load("form4-w60"))!;
            check("form4-w60: full tab names",
                wide.Tabs.Select(t => t.Name).SequenceEqual(new[] { "Venue", "Catering", "Schedule", "Transport", "Submit" }),
                string.Join("|", wide.Tabs.Select(t => t.Name)));

            // A one-question form has no bar, only "☐ Venue".
            check("form1-w23: the single tab is Venue",
                ChatTranscript.ParseAskForm(Load("form1-w23"))!.Tabs.Single().Name == "Venue");

            // A form with nothing above it to name it, below the screen's top.
            check("form4-w23 yields no form: its head is not on the screen",
                ChatTranscript.ParseAskForm(Load("form4-w23")) is null);
        }
    }
}
