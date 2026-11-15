using System.Runtime.CompilerServices;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-327 items 4 and 5 at the seam with a file this process does not own: the
/// session's real .jsonl, written to a temp file the way Claude Code writes it
/// (row by row, the tool_result appended once the form is answered), and read
/// through AskFormTranscript.CallFor exactly as the pipeline, the panel and the
/// mirror read it. The rows are real (tests/TranscriptTests/Captures/cb327).
/// </summary>
public class AskFormTranscriptFileTests : IDisposable
{
    private static readonly string Captures = Path.Combine(
        Path.GetDirectoryName(Here())!, "..", "TranscriptTests", "Captures", "cb327");

    private static string Here([CallerFilePath] string path = "") => path;

    private static string[] Rows(string file) => File.ReadAllLines(Path.Combine(Captures, file));
    private static string Screen(string name) => File.ReadAllText(Path.Combine(Captures, name + ".txt"));

    private readonly string _path = Path.Combine(Path.GetTempPath(), "cb327-ask-" + Guid.NewGuid() + ".jsonl");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void A_pending_call_in_the_file_mints_the_tall_form_and_its_tool_result_ends_it()
    {
        var pending = Rows("ask-venue-pending.jsonl");
        var answered = Rows("ask-venue-answered.jsonl");
        var tall = Screen("ask-venue-tall-w23h30");

        File.WriteAllLines(_path, pending);
        var call = AskFormTranscript.CallFor(tall, _path);
        Assert.Equal("Which venue style should we book for the team offsite?",
            AskFormTranscript.ReadForm(tall, call)!.Prompt.Title);

        // The answer arrives as the next row; the same screen read again (a stale
        // capture) is no longer minted from anything.
        File.AppendAllLines(_path, answered.Skip(pending.Length));
        Assert.Null(AskFormTranscript.CallFor(tall, _path));
    }

    // Transcripts reach tens of megabytes; the call is near the end, and only
    // the tail is read. A row torn at the window's edge is dropped, not parsed.
    [Fact]
    public void The_call_is_found_in_the_tail_of_a_long_transcript()
    {
        var filler = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('x', 4000) + "\"}}";
        File.WriteAllLines(_path, Enumerable.Repeat(filler, 200).Concat(Rows("ask-venue-pending.jsonl")));
        Assert.True(new FileInfo(_path).Length > 512 * 1024);
        Assert.NotNull(AskFormTranscript.CallFor(Screen("ask-venue-tall-w23h30"), _path));
    }

    // The mirror's answer procedure reads the file itself, from the session's
    // TranscriptPath: an option scrolled off the pane is pressed by its digit.
    [Fact]
    public async Task FormAnswer_presses_an_off_screen_option_of_a_tall_form()
    {
        File.WriteAllLines(_path, Rows("ask-venue-pending.jsonl"));
        var tall = Screen("ask-venue-tall-w23h30");
        var status = new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, TranscriptPath = _path };
        var fp = MirrorProtocol.PromptFromScreen(tall, AskFormTranscript.CallFor(tall, _path))!.Fingerprint;
        var pressed = new List<string>();

        var code = await FormAnswer.AnswerAsync(status, _ => Task.FromResult<string?>(tall),
            (_, key) => { pressed.Add(key); return Task.FromResult(true); }, null, fp, "1", null, null);

        Assert.Null(code);
        Assert.Equal(["1"], pressed);

        // Without the transcript the same screen holds no form at all.
        status.TranscriptPath = "";
        Assert.Equal(MirrorProtocol.ErrNoPrompt, await FormAnswer.AnswerAsync(status, _ => Task.FromResult<string?>(tall),
            (_, key) => { pressed.Add(key); return Task.FromResult(true); }, null, fp, "1", null, null));
        Assert.Single(pressed);
    }
}
