namespace Orbweaver
{
    // CB-412: the four screens of Claude Code's in-session `/login`. Pure: text in,
    // value out. INTERFACE STUB — signatures are the agreed contract (plan 1.1);
    // the reading lands in the next commit. Read returns null until then.
    internal enum LoginStep { Picker, Opening, Url, Error }

    internal sealed record LoginScreen(
        LoginStep Step, string Title, IReadOnlyList<string> Preview,
        IReadOnlyList<ChatPromptOption> Options, string? Url, bool FieldEmpty, string? FieldTail, string Hash)
    {
        internal static LoginScreen? Read(string? screen) => null;

        internal static ChatPrompt ToPrompt(LoginScreen s, bool canPress) =>
            new(s.Title, s.Options, Body: s.Preview, Link: s.Url, CodeEntry: s.Step == LoginStep.Url && canPress);
    }
}
