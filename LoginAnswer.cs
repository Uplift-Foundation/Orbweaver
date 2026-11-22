namespace Orbweaver
{
    // CB-412: answering `/login` screens through verified presses. INTERFACE STUB
    // (plan 1.1); bodies land in the next commit. Codes: "no-prompt",
    // "prompt-changed", "not-empty", "type-failed", "unverified", "press-failed".
    internal delegate Task<string?> Capture(SessionStatus s);
    internal delegate Task<bool> Type(SessionStatus s, string text);
    internal delegate Task<(bool Pressed, string? Before)> CaptureAndPress(SessionStatus s, string key, Func<string?, bool> approved);

    internal static class LoginAnswer
    {
        internal static bool CodeAllowed(string code) => false;

        internal static Task<string?> PressAsync(SessionStatus s, CaptureAndPress cap, string hash, string key) =>
            Task.FromResult<string?>("press-failed");

        internal static Task<string?> SubmitCodeAsync(SessionStatus s, Capture capture, Type type, CaptureAndPress cap,
            Func<TimeSpan, Task> delay, string hash, string code) =>
            Task.FromResult<string?>("type-failed");
    }
}
