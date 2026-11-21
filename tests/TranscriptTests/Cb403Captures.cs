using System.Runtime.CompilerServices;
using Orbweaver;

namespace Orbweaver.Tests
{
    // One check per real capture in Captures/cb403 (CB-403): what the typing
    // gate makes of a Claude Code lead pane that has an agent team, and so a
    // footer of member rows under its status line. Loaded from the files for
    // the reason Cb327Captures gives; the README beside them says how each
    // screen was reached and what typing into it did.
    internal static class Cb403Captures
    {
        private static readonly string Dir = Path.Combine(
            Path.GetDirectoryName(Where())!, "Captures", "cb403");

        private static string Where([CallerFilePath] string path = "") => path;

        internal static string Load(string name) => File.ReadAllText(Path.Combine(Dir, name + ".txt"));

        internal static void Run(CheckFn check)
        {
            // The lead is in view and its box is on screen: text typed here is a
            // message to the lead. The two `sel-` rows are the reported refusal
            // (no-box-top) and `sel-stopped` is the same at 80 columns.
            foreach (var name in new[]
            {
                "team-idle-w24", "team-draft-w24", "team-after-send-w24",
                "team-sel-main-w24", "team-sel-teammate-w24", "team-sel-stopped-w80",
            })
            {
                var (reading, why) = PaneScreen.Read(Load(name));
                check($"cb403 {name} reads as the prompt", reading == PaneReading.Prompt && why == "prompt", $"{reading} ({why})");
            }

            // A teammate is in view: the same box, and text typed here goes to
            // the teammate. The first two read as the prompt before CB-403.
            foreach (var name in new[]
            {
                "teammate-view-w24", "teammate-view-draft-w24",
                "teammate-view-sel-teammate-w24", "teammate-view-sel-main-w24",
            })
            {
                var (reading, why) = PaneScreen.Read(Load(name));
                check($"cb403 {name} is refused as a teammate's view",
                    reading == PaneReading.Unknown && why == "teammate-view", $"{reading} ({why})");
            }

            // Left arrow at the prompt: "Background this session?".
            var (dialog, check2) = PaneScreen.Read(Load("background-dialog-w24"));
            check("cb403 background-dialog-w24 reads as a dialog", dialog == PaneReading.Dialog, $"{dialog} ({check2})");

            // Each signal on its own, cut out of a real capture so neither can
            // lean on the other: the footer without the `@` title, then the
            // title without the footer.
            var view = ChatTranscript.ScreenLines(Load("teammate-view-w24")).Select(l => l.Trim()).ToArray();
            var top = Array.FindIndex(view, l => l.Contains("@scratch-two ─", StringComparison.Ordinal));
            check("cb403: the capture has the titled box top", top >= 0);
            if (top >= 0)
            {
                var footerOnly = (string[])view.Clone();
                footerOnly[top] = new string('─', 24);
                var a = PaneScreen.ClaudePrompt(footerOnly, 0);
                check("cb403: `◯ main` alone refuses", a == (PaneReading.Unknown, "teammate-view"), $"{a.Reading} ({a.Check})");

                var titleOnly = view.Where(l => !PaneScreen.IsUnviewedMain(l)).ToArray();
                var b = PaneScreen.ClaudePrompt(titleOnly, 0);
                check("cb403: the `@` title alone refuses", b == (PaneReading.Unknown, "teammate-view"), $"{b.Reading} ({b.Check})");
            }

            // Both sides of each small rule.
            check("cb403: `❯ ⏺ main` is a selected team row", PaneScreen.IsSelectedTeamRow("❯ ⏺ main"));
            check("cb403: `❯ ◯ scratch-two 1m 54s` is a selected team row", PaneScreen.IsSelectedTeamRow("❯ ◯ scratch-two 1m 54s"));
            check("cb403: an empty prompt is not a team row", !PaneScreen.IsSelectedTeamRow("❯"));
            check("cb403: a draft is not a team row", !PaneScreen.IsSelectedTeamRow("❯ zq"));
            check("cb403: a draft that only starts with the mark's glyph is not a team row", !PaneScreen.IsSelectedTeamRow("❯ ⏺"));
            check("cb403: an unselected row is not a selected team row", !PaneScreen.IsSelectedTeamRow("◯ scratch-two 1m 54s"));
            check("cb403: `◯ main` is the lead out of view", PaneScreen.IsUnviewedMain("◯ main"));
            check("cb403: `❯ ◯ main` is the lead out of view", PaneScreen.IsUnviewedMain("❯ ◯ main"));
            check("cb403: `⏺ main` is the lead in view", !PaneScreen.IsUnviewedMain("⏺ main"));
            check("cb403: a teammate's row is not the lead's", !PaneScreen.IsUnviewedMain("◯ mainframe 2m"));
            check("cb403: a pure rule has no title", PaneScreen.TitleOf("────────────────────────").Length == 0);
            check("cb403: a session name is a title without `@`", PaneScreen.TitleOf("────────── CB-403 lead ─") == "CB-403 lead");
            check("cb403: a teammate's title keeps its `@`", PaneScreen.TitleOf("───────── @scratch-two ─") == "@scratch-two");

            // A selected footer row is only skipped, never trusted: with the box
            // gone there is no prompt.
            var sel = ChatTranscript.ScreenLines(Load("team-sel-teammate-w24")).Select(l => l.Trim()).ToArray();
            var noBox = sel.Where(l => l != "❯").ToArray();
            var c = PaneScreen.ClaudePrompt(noBox, 0);
            check("cb403: a selected team row with no box above is not a prompt", c.Reading == PaneReading.Unknown, $"{c.Reading} ({c.Check})");
        }
    }
}
