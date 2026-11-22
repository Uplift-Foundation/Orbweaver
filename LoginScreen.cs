using System.Security.Cryptography;
using System.Text;

namespace Orbweaver
{
    // CB-412: the screens of Claude Code's in-session `/login`, read off a captured
    // pane. Pure, like OverlayScreen: text in, value out, no window, process or
    // setting. Every step shares one anchor, a horizontal rule followed by a row
    // that is exactly "Login" with no input box below it; no permission dialog or
    // form has that, so a screen without it is null and falls through to whatever
    // read it before. An unknown screen is never "success".
    internal enum LoginStep { Picker, Opening, Url, Error }

    internal sealed record LoginScreen(
        LoginStep Step, string Title, IReadOnlyList<string> Preview,
        IReadOnlyList<ChatPromptOption> Options, string? Url, bool FieldEmpty, string? FieldTail, string Hash)
    {
        internal const string DeskSentence = "Open the sign-in link in a browser, then paste the code it shows.";
        internal const string RetryKey = "Enter";
        internal const int MaxUrlLength = 2048;

        internal static LoginScreen? Read(string? screen)
        {
            if (string.IsNullOrEmpty(screen)) return null;

            var lines = ChatTranscript.ScreenLines(screen);

            // The anchor: the last rule, then "Login". Nothing may follow with a
            // rule of its own (the input box of a live conversation).
            var anchor = -1;
            for (var i = 0; i + 1 < lines.Length; i++)
                if (ChatTranscript.IsHorizontalRule(lines[i].Trim()) && lines[i + 1].Trim() == "Login") anchor = i + 1;
            if (anchor < 0) return null;
            for (var i = anchor + 1; i < lines.Length; i++)
                if (ChatTranscript.IsHorizontalRule(lines[i].Trim())) return null;

            var body = lines[(anchor + 1)..];

            if (body.Any(l => l.Contains("Opening browser to sign in", StringComparison.Ordinal)))
                return Make(LoginStep.Opening, "Opening the browser…", Array.Empty<string>(), Array.Empty<ChatPromptOption>(), null, false, null);

            if (ReadUrl(body) is { } url) return url;
            if (ReadError(body) is { } error) return error;
            return ReadPicker(lines, anchor);
        }

        private static LoginScreen? ReadPicker(string[] lines, int anchor)
        {
            // The rows under the anchor, read by the one dialog reader there is, so
            // options and labels are its own. The conversation above is cut away
            // first, otherwise its prose could be taken for a numbered run.
            var below = string.Join("\n", lines[anchor..]);
            var dialog = ChatTranscript.ParseDialogCore(below, out var cleaned, out var first);
            if (dialog is null || first < 0 || dialog.Options.Count == 0) return null;

            var preview = cleaned.Take(first).Where(l => l.Length > 0 && l != "Login" && !l.StartsWith("Select ", StringComparison.Ordinal)).ToList();
            return Make(LoginStep.Picker, dialog.Title, preview, dialog.Options, null, false, null);
        }

        private static LoginScreen? ReadError(string[] body)
        {
            var start = Array.FindIndex(body, l => l.TrimStart().StartsWith('✘'));
            if (start < 0 || !body.Any(l => l.Contains("Press Enter to retry", StringComparison.Ordinal))) return null;

            // The error's own text: its first row and the indented rows that wrap
            // from it, up to the first blank one.
            var parts = new List<string> { body[start].Trim().TrimStart('✘').Trim() };
            for (var i = start + 1; i < body.Length && body[i].Trim().Length > 0; i++) parts.Add(body[i].Trim());
            var text = string.Join(" ", parts);

            return Make(LoginStep.Error, text, new[] { text },
                new[] { new ChatPromptOption(RetryKey, "Try again") }, null, false, null);
        }

        private static LoginScreen? ReadUrl(string[] body)
        {
            var paste = Array.FindIndex(body, l => l.Contains("Paste code here if prompted", StringComparison.Ordinal));
            if (paste < 0 || !body.Any(l => l.Contains("Esc to cancel", StringComparison.Ordinal))) return null;

            var start = Array.FindIndex(body, 0, paste, l => l.StartsWith("https://", StringComparison.Ordinal));
            if (start < 0) return null;

            // The URL is its first row plus every row up to the first blank one, each
            // trimmed on both sides and joined with nothing between: tmux wraps at
            // column 0, a reader that re-flows may pad or indent the continuation. A
            // blank row has to end it before the Paste row; with none, there is no
            // telling where the URL stops (or that it was not cut off), so the screen
            // is not read and never offers a truncated link. A non-URL row that slips
            // in carries whitespace and fails the check below.
            var sb = new StringBuilder();
            var i = start;
            for (; i < paste && body[i].Trim().Length > 0; i++) sb.Append(body[i].Trim());
            if (i >= paste) return null;

            var url = sb.ToString();
            if (url.Length > MaxUrlLength || url.Any(char.IsWhiteSpace)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;

            // The box row between the rules: blank, or masked with the tail shown.
            var boxStart = Array.FindIndex(body, paste, l => l.TrimStart().StartsWith('╭'));
            var empty = true;
            string? tail = null;
            if (boxStart >= 0 && boxStart + 1 < body.Length)
            {
                var field = body[boxStart + 1].Trim().Trim('│').Trim();
                empty = field.Length == 0;
                if (!empty) tail = field[(field.LastIndexOf('*') + 1)..];
            }
            else return null;

            return Make(LoginStep.Url, "Sign in", new[] { DeskSentence }, Array.Empty<ChatPromptOption>(), url, empty, tail);
        }

        // The hash covers what a press would act on: step, text, URL and the keys
        // with their labels. Field contents are left out, so typing a code does
        // not change what the screen is.
        private static LoginScreen Make(LoginStep step, string title, IReadOnlyList<string> preview,
            IReadOnlyList<ChatPromptOption> options, string? url, bool empty, string? tail)
        {
            var sb = new StringBuilder().Append(step).Append('\n');
            foreach (var p in preview) sb.Append(p).Append('\n');
            sb.Append(url).Append('\n');
            foreach (var o in options) sb.Append(o.Key).Append(". ").Append(o.Label).Append('\n');
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
            return new LoginScreen(step, title, preview, options, url, empty, tail, hash);
        }

        // The panel's view. The URL goes only into Link; a route that cannot be
        // pressed silently gets the link and no field and no options.
        internal static ChatPrompt ToPrompt(LoginScreen s, bool canPress) => s.Step switch
        {
            LoginStep.Url => new ChatPrompt(s.Title, Array.Empty<ChatPromptOption>(), Body: s.Preview, Link: s.Url, CodeEntry: canPress),
            LoginStep.Opening => new ChatPrompt(s.Title, Array.Empty<ChatPromptOption>(), Body: s.Preview),
            _ => new ChatPrompt(s.Title, canPress ? s.Options : Array.Empty<ChatPromptOption>(), Body: s.Step == LoginStep.Error ? s.Preview : null),
        };
    }
}
