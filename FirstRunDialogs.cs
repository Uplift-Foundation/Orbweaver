namespace Orbweaver
{
    // The dialogs a CLI shows the first time it runs in a folder, or when it
    // has an update: "Do you trust this directory?", "Update available",
    // "Hooks can run outside the sandbox", "Allow external CLAUDE.md file
    // imports?". They are drawn like a permission prompt and some of them even
    // parse as a numbered dialog, but they are not one: a phone that offered
    // Approve on "Update now" would be running an installer from a lock screen.
    //
    // CB-313 captured every one (the *-trustdialog, *-importdialog,
    // *-hooksdialog and *-updatedialog fixtures). The prompt pipeline asks
    // here before it builds a phone prompt, and a dialog that is one of these
    // is sent as "Open on desk" with no actions.
    //
    // Matched on the dialog's own wording over the bottom of the screen, not on
    // its options, because the wording is what is stable across versions and
    // the options ("Yes, continue" / "No, quit") are generic enough to appear
    // in a real prompt. Add a phrase when a capture shows another such dialog.
    internal static class FirstRunDialogs
    {
        // Each entry is a set of phrases that must ALL be on the screen. One phrase is enough for the
        // dialogs whose wording is unique to them; the update prompt needs its choice as well, because
        // Codex also draws "Update available!" as a one-line banner on an idle screen
        // (wt-codex-none), and a banner is not a dialog.
        private static readonly string[][] Phrases =
        {
            // Claude Code: folder trust and external imports.
            new[] { "Do you trust the contents of this directory" },
            new[] { "Do you trust the files in this folder" },
            new[] { "Allow external CLAUDE.md file imports" },
            // Codex and Grok: folder trust.
            new[] { "Trusting the directory allows" },
            new[] { "Grok Build may run or modify contents in this directory" },
            // Codex: hooks review and update prompt.
            new[] { "Hooks can run outside the sandbox after you trust them" },
            new[] { "Update available!", "Update now" },
        };

        internal static bool Is(string? screen)
        {
            if (string.IsNullOrWhiteSpace(screen)) return false;

            // Only the bottom of the screen: a trust dialog is always the thing
            // being asked, never something scrolled up behind a later prompt.
            var lines = ChatTranscript.ScreenLines(screen);
            var from = ChatTranscript.WindowStart(lines);
            var window = string.Join("\n", lines[from..]);
            return Phrases.Any(set => set.All(phrase => window.Contains(phrase, StringComparison.Ordinal)));
        }
    }
}
