using Orbweaver;

namespace Orbweaver.Tests
{
    // CB-412: the checks the console suite runs over the Captures/cb412 files. Apart
    // from Cb412Captures (the loader) because CheckFn lives in Cb327Captures, which
    // the UI suite does not compile.
    internal static class Cb412Checks
    {
        private static readonly System.Text.Json.JsonSerializerOptions Raw =
            new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        internal static void Run(CheckFn check)
        {
            LoginScreen? Read(string n) => LoginScreen.Read(Cb412Captures.Screen(n));

            check("login: the picker reads as three options", Read("login-picker-w100") is { Step: LoginStep.Picker, Options.Count: 3 });
            check("login: the url step at 60 columns rejoins to the url at 100",
                Read("login-url-w60")?.Url is { Length: 79 } u && u == Read("login-url-w100")?.Url);
            check("login: the error screen has one Try again button", Read("login-badcode-error-w100") is { Step: LoginStep.Error, Options.Count: 1 });
            check("login: Esc's screen and the onboarding screens are not login screens",
                new[] { "login-interrupted-w100", "onboarding-picker-w100", "onboarding-url-w100" }.All(n => Read(n) is null));

            // The fixture file is written with spaces after its colons; Claude Code's own
            // rows are compact, and IsInteresting is a substring test on the compact form.
            var lines = File.ReadAllLines(Cb412Captures.PathOf("login-rows.jsonl"))
                .Select(l => System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(l).RootElement, Raw)).ToArray();
            var rows = ChatTranscript.Map(lines);
            var texts = rows.Select(r => r.Turn.Text).ToList();
            var limit = texts.FindIndex(t => t.Contains("Login reached its time limit"));
            check("login: the time-limit turn stays in history", limit >= 0);
            check("login: the outcome shows verbatim after it, as a system turn",
                limit >= 0 && texts.Count > limit + 1 && texts[limit + 1] == "Login interrupted"
                && rows[limit + 1].Turn.Role == ChatRole.System);
            check("login: the caveat and the command row are still not shown",
                texts.All(t => !t.Contains("<command-name>") && !t.Contains("Caveat")));

            // The same stdout row with no /login command before it is still noise.
            var lone = ChatTranscript.Map(lines.Where(l => !l.Contains("<command-name>")));
            check("login: a stdout row not directly after /login stays hidden", lone.All(r => r.Turn.Text != "Login interrupted"));
        }
    }
}
