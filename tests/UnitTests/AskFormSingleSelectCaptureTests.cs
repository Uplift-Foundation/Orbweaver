using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-373: a peer asked for the prompt of a session showing a single-select AskUserQuestion
/// (a header, three options each with a description) and was answered no-prompt every time.
/// The screen is a real tmux capture of that dialog taken on the Mac mini (Captures/cb373,
/// recipe in the PR); until it is committed these tests fail on the missing file, by design.
/// </summary>
public class AskFormSingleSelectCaptureTests
{
    private const string Question = "Which database should the project use?";

    private static string Screen => Cb373Captures.Screen("askform-single-100x40");
    private static string Rows => Cb373Captures.PathOf("askform-single-pending.jsonl");

    [Fact]
    public void The_screen_alone_offers_the_question_and_its_three_options()
    {
        var prompt = MirrorProtocol.PromptFromScreen(Screen);

        Assert.NotNull(prompt);
        Assert.Contains(Question, prompt!.Title);
        Assert.False(prompt.MultiSelect);
        foreach (var label in new[] { "Postgres", "SQLite", "MySQL" })
            Assert.Contains(prompt.Options, o => o.Label.Contains(label));
    }

    [Fact]
    public void With_the_pending_call_the_form_is_offered_with_words_from_the_transcript()
    {
        var call = AskFormTranscript.CallFor(Screen, Rows);
        Assert.NotNull(call);

        var prompt = MirrorProtocol.PromptFromScreen(Screen, call);

        Assert.NotNull(prompt);
        Assert.Contains(Question, prompt!.Title);
        Assert.False(prompt.MultiSelect);
        foreach (var label in new[] { "Postgres", "SQLite", "MySQL" })
            Assert.Contains(prompt.Options, o => o.Label.Contains(label));
    }
}
