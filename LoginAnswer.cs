using System.Text;

namespace Orbweaver
{
    // CB-412: answering `/login` screens, every answer a verified press. The seams
    // come in as arguments, like FormAnswer's, so a test drives it with a scripted
    // pane and no process. Null means done; otherwise a short code, and on every
    // refusal nothing was pressed that the code does not say: "no-prompt",
    // "prompt-changed", "not-empty", "type-failed", "unverified", "press-failed".
    //
    // Secret hygiene: the code is a parameter and a local, never a field, never in
    // a returned string, never in an exception message. Nothing here logs; the
    // codes above are the only thing that leaves.
    internal delegate Task<string?> LoginCapture(SessionStatus s);
    internal delegate Task<bool> LoginType(SessionStatus s, string text);
    internal delegate Task<(bool Pressed, string? Before)> LoginCaptureAndPress(SessionStatus s, string key, Func<string?, bool> approved);

    internal static class LoginAnswer
    {
        internal const string NoPrompt = "no-prompt", PromptChanged = "prompt-changed", NotEmpty = "not-empty",
            TypeFailed = "type-failed", Unverified = "unverified", PressFailed = "press-failed";

        private const int MaxCodeBytes = 1000, MaskTail = 6;
        private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(100);
        private const int PollSteps = 20; // 2 s

        // One line of text a code may be: 1 to 1000 UTF-8 bytes, no whitespace, no
        // C0, DEL or C1 control (so no Enter, Esc or newline can ride in with it).
        internal static bool CodeAllowed(string? code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            if (Encoding.UTF8.GetByteCount(code) > MaxCodeBytes) return false;
            foreach (var c in code)
                if (char.IsWhiteSpace(c) || c < 0x20 || (c >= 0x7F && c <= 0x9F)) return false;
            return true;
        }

        internal static async Task<string?> PressAsync(SessionStatus s, LoginCaptureAndPress cap, string hash, string key)
        {
            var (pressed, before) = await cap(s, key, screen => LoginScreen.Read(screen)?.Hash == hash).ConfigureAwait(false);
            return pressed ? null : Refusal(before, hash);
        }

        internal static async Task<string?> SubmitCodeAsync(SessionStatus s, LoginCapture capture, LoginType type, LoginCaptureAndPress cap,
            Func<TimeSpan, Task> delay, string hash, string code)
        {
            if (!CodeAllowed(code)) return TypeFailed;

            var screen = LoginScreen.Read(await capture(s).ConfigureAwait(false));
            if (screen is null) return NoPrompt;
            if (screen.Step != LoginStep.Url || screen.Hash != hash) return PromptChanged;
            if (!screen.FieldEmpty) return NotEmpty;

            if (!await type(s, code).ConfigureAwait(false)) return TypeFailed;

            // Typed, not sent. The field masks all but the last six characters, so
            // that tail is the only read-back there is; Enter waits for it.
            var tail = code.Length <= MaskTail ? code : code[^MaskTail..];
            for (var i = 0; i <= PollSteps; i++)
            {
                var now = LoginScreen.Read(await capture(s).ConfigureAwait(false));
                if (now is null) return NoPrompt;
                if (now.Step != LoginStep.Url || now.Hash != hash) return PromptChanged;
                if (now.FieldTail == tail) break;
                if (i == PollSteps) return Unverified;
                await delay(PollStep).ConfigureAwait(false);
            }

            var (pressed, before) = await cap(s, "Enter", seen =>
                LoginScreen.Read(seen) is { Step: LoginStep.Url } r && r.Hash == hash && r.FieldTail == tail).ConfigureAwait(false);
            return pressed ? null : Refusal(before, hash);
        }

        private static string Refusal(string? before, string hash)
        {
            var read = LoginScreen.Read(before);
            if (read is null) return NoPrompt;
            return read.Hash != hash ? PromptChanged : PressFailed;
        }
    }
}
