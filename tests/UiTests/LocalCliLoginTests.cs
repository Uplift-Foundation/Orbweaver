using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-412: `/login` through the local chat session: the picker as buttons, the URL
// step as a link and a code field, verified presses, route choice, secret hygiene.
[Collection("Settings")]
public class LocalCliLoginTests
{
    private sealed class Pane(string screen)
    {
        public string Screen = screen;
        public List<string> Pressed = new(), Typed = new(), Verified = new();
        public Func<string, string?> AfterPress = _ => null;
        public Func<string, string?> AfterType = _ => null;
        public bool PressOk = true;

        public Task<string?> Capture(SessionStatus _) => Task.FromResult<string?>(Screen);

        public Task<bool> Type(SessionStatus _, string text)
        {
            Typed.Add(text);
            if (AfterType(text) is { } next) Screen = next;
            return Task.FromResult(true);
        }

        public Task<(bool Pressed, string? Before)> CaptureAndPress(SessionStatus _, string key, Func<string?, bool> approved)
        {
            Verified.Add(key);
            var before = Screen;
            if (!approved(before) || !PressOk) return Task.FromResult((false, (string?)before));
            Pressed.Add(key);
            if (AfterPress(key) is { } next) Screen = next;
            return Task.FromResult((true, (string?)before));
        }
    }

    private static SessionStatus Waiting() => new()
    {
        Source = SessionSource.ClaudeCode, State = "waiting", Title = "", TmuxPane = "%42",
    };

    private static async Task Pump(Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static async Task<LocalCliChatSession> On(
        Pane pane, bool canPress = true, Func<DateTime>? clock = null, Func<SessionStatus, Task<string?>>? capture = null)
    {
        var session = new LocalCliChatSession("s1", Waiting())
        {
            Capture = capture ?? pane.Capture,
            TypeLiteral = pane.Type,
            PressKey = (_, _) => Task.FromResult(true),
            CaptureAndPress = pane.CaptureAndPress,
            CanPress = _ => canPress,
            LoginDelay = _ => Task.CompletedTask,
            Clock = clock ?? (() => DateTime.UtcNow),
        };
        session.UpdateStatus(Waiting());
        await Pump(() => session.Prompt is not null);
        Assert.NotNull(session.Prompt);
        return session;
    }

    private static async Task Rescan(LocalCliChatSession session)
    {
        session.UpdateStatus(Waiting());
        for (var i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
    }

    private static async Task WithReplyOn(Func<Task> work)
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try { OrbweaverSettings.ClaudeCodeReplyEnabled = true; await work(); }
        finally { OrbweaverSettings.ClaudeCodeReplyEnabled = before; }
    }

    private static string Screen(string name) => Cb412Captures.Screen(name);

    // --- the picker ---

    [AvaloniaFact]
    public async Task The_picker_shows_three_buttons_not_a_permission_dialog()
    {
        var session = await On(new Pane(Screen("login-picker-w100")));
        Assert.Equal(new[] { "1", "2", "3" }, session.Prompt!.Options.Select(o => o.Key));
        Assert.Null(session.Prompt.Link);
        Assert.False(session.Prompt.CodeEntry);
    }

    [AvaloniaFact]
    public async Task A_picker_button_is_a_verified_press_and_clears_the_prompt()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-picker-w100")) { AfterPress = _ => Screen("login-url-w100") };
            var session = await On(pane);

            await session.AnswerAsync(session.Prompt!.Options[0]);

            Assert.Equal(new[] { "1" }, pane.Verified);
            Assert.Equal(new[] { "1" }, pane.Pressed);
            Assert.Null(session.Prompt);
            Assert.Empty(session.History);
        });
    }

    [AvaloniaFact]
    public async Task A_press_after_the_screen_changed_sends_no_key_and_says_so()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-picker-w100"));
            var session = await On(pane);
            pane.Screen = Screen("login-badcode-error-w100");

            await session.AnswerAsync(session.Prompt!.Options[0]);

            Assert.Empty(pane.Pressed);
            Assert.Equal(LocalCliChatSession.LoginChangedNote, Assert.Single(session.History).Text);
            Assert.Equal("Try again", session.Prompt!.Options.Single().Label);
        });
    }

    [AvaloniaFact]
    public async Task A_press_the_terminal_refuses_is_a_failed_note()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-picker-w100")) { PressOk = false };
            var session = await On(pane);
            await session.AnswerAsync(session.Prompt!.Options[0]);
            Assert.Equal(LocalCliChatSession.LoginFailedNote, Assert.Single(session.History).Text);
        });
    }

    [AvaloniaFact]
    public async Task The_screen_just_pressed_is_held_back_until_it_moves_on()
    {
        await WithReplyOn(async () =>
        {
            var now = DateTime.UtcNow;
            var pane = new Pane(Screen("login-picker-w100"));
            var session = await On(pane, clock: () => now);

            await session.AnswerAsync(session.Prompt!.Options[0]);
            await Rescan(session);
            Assert.Null(session.Prompt);

            now += TimeSpan.FromSeconds(30);
            await Rescan(session);
            Assert.NotNull(session.Prompt);
        });
    }

    [AvaloniaFact]
    public async Task Replying_off_presses_nothing_for_a_login_screen()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = false;
            var pane = new Pane(Screen("login-picker-w100"));
            var session = await On(pane);
            await session.AnswerAsync(session.Prompt!.Options[0]);
            Assert.Empty(pane.Verified);
        }
        finally { OrbweaverSettings.ClaudeCodeReplyEnabled = before; }
    }

    // --- the URL step ---

    [AvaloniaFact]
    public async Task The_url_step_has_a_link_a_code_field_and_no_buttons()
    {
        var session = await On(new Pane(Screen("login-url-w100")));
        var p = session.Prompt!;
        Assert.StartsWith("https://claude.ai/oauth/authorize?", p.Link);
        Assert.True(p.CodeEntry);
        Assert.Empty(p.Options);
        Assert.DoesNotContain(p.Body!, l => l.Contains("https://"));
    }

    [AvaloniaFact]
    public async Task On_a_route_that_cannot_be_pressed_the_link_shows_without_a_field()
    {
        var session = await On(new Pane(Screen("login-url-w100")), canPress: false);
        Assert.NotNull(session.Prompt!.Link);
        Assert.False(session.Prompt.CodeEntry);
    }

    [AvaloniaFact]
    public async Task A_code_is_typed_read_back_and_sent_and_kept_nowhere()
    {
        await WithReplyOn(async () =>
        {
            const string code = "QA-CANARY-9f3e2c-lstate";
            var pane = new Pane(Screen("login-url-w100"));
            pane.AfterType = t => Screen("login-code-typed-w100").Replace("lstate", t[^6..]);
            var session = await On(pane);

            await session.SubmitLoginCodeAsync(code);

            Assert.Equal(new[] { code }, pane.Typed);
            Assert.Equal(new[] { "Enter" }, pane.Pressed);
            Assert.Null(session.Prompt);
            Assert.DoesNotContain(session.History, t => t.Text.Contains("CANARY"));
        });
    }

    [AvaloniaFact]
    public async Task A_code_that_cannot_be_read_back_is_not_sent_and_says_to_finish_in_the_terminal()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-url-w100"));
            var session = await On(pane);

            await session.SubmitLoginCodeAsync("QA-CANARY-abcdef");

            Assert.Empty(pane.Pressed);
            var note = Assert.Single(session.History).Text;
            Assert.Equal(LocalCliChatSession.LoginUnverifiedNote, note);
            Assert.DoesNotContain("CANARY", note);
        });
    }

    [AvaloniaFact]
    public async Task A_code_with_no_url_step_on_show_types_nothing()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-picker-w100"));
            var session = await On(pane);
            await session.SubmitLoginCodeAsync("abc");
            Assert.Empty(pane.Typed);
            Assert.Equal(LocalCliChatSession.LoginGoneNote, Assert.Single(session.History).Text);
        });
    }

    [AvaloniaFact]
    public async Task A_code_on_an_unpressable_route_types_nothing()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-url-w100"));
            var session = await On(pane, canPress: false);
            await session.SubmitLoginCodeAsync("abc");
            Assert.Empty(pane.Typed);
        });
    }

    [AvaloniaFact]
    public async Task A_code_with_replying_off_types_nothing()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = false;
            var pane = new Pane(Screen("login-url-w100"));
            var session = await On(pane);
            await session.SubmitLoginCodeAsync("abc");
            Assert.Empty(pane.Typed);
        }
        finally { OrbweaverSettings.ClaudeCodeReplyEnabled = before; }
    }

    [AvaloniaFact]
    public async Task A_typing_failure_is_a_failed_note_without_the_code()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-url-w100"));
            var session = new LocalCliChatSession("s1", Waiting())
            {
                Capture = pane.Capture,
                TypeLiteral = (_, _) => throw new InvalidOperationException("boom QA-CANARY"),
                CaptureAndPress = pane.CaptureAndPress,
                CanPress = _ => true,
                LoginDelay = _ => Task.CompletedTask,
            };
            session.UpdateStatus(Waiting());
            await Pump(() => session.Prompt is not null);

            await session.SubmitLoginCodeAsync("QA-CANARY-abcdef");

            var note = Assert.Single(session.History).Text;
            Assert.Equal(LocalCliChatSession.LoginFailedNote, note);
            Assert.DoesNotContain("CANARY", note);
        });
    }

    [AvaloniaFact]
    public async Task A_press_that_throws_is_a_failed_note()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-picker-w100"));
            var session = new LocalCliChatSession("s1", Waiting())
            {
                Capture = pane.Capture,
                CaptureAndPress = (_, _, _) => throw new InvalidOperationException("boom"),
                CanPress = _ => true,
            };
            session.UpdateStatus(Waiting());
            await Pump(() => session.Prompt is not null);

            await session.AnswerAsync(session.Prompt!.Options[0]);
            Assert.Equal(LocalCliChatSession.LoginFailedNote, Assert.Single(session.History).Text);
        });
    }

    [AvaloniaFact]
    public async Task Typing_a_code_does_not_rebuild_the_prompt()
    {
        var pane = new Pane(Screen("login-url-w100"));
        var session = await On(pane);
        var before = session.Prompt;
        pane.Screen = Screen("login-code-typed-w100");
        await Rescan(session);
        Assert.Same(before, session.Prompt);
    }

    // --- the error screen and the opening moment ---

    [AvaloniaFact]
    public async Task The_error_screen_offers_try_again_and_presses_Enter()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-badcode-error-w100"));
            var session = await On(pane);
            Assert.Equal("Try again", session.Prompt!.Options.Single().Label);
            await session.AnswerAsync(session.Prompt.Options[0]);
            Assert.Equal(new[] { "Enter" }, pane.Pressed);
        });
    }

    [AvaloniaFact]
    public async Task The_opening_moment_shows_a_prompt_with_no_buttons()
    {
        var session = await On(new Pane(Screen("login-opening-browser-w100")));
        Assert.Empty(session.Prompt!.Options);
    }

    // --- an unreadable pane ---

    [AvaloniaFact]
    public async Task A_login_screen_on_the_wire_that_goes_away_leaves_the_ordinary_fallback()
    {
        var pane = new Pane(Screen("login-picker-w100"));
        var session = await On(pane);
        pane.Screen = Screen("login-interrupted-w100");
        await Rescan(session);
        Assert.Equal("Waiting for input", session.Prompt!.Title);
    }

    // --- route choice ---

    [Theory]
    [InlineData("Tmux", true)]
    [InlineData("ITerm2", false)]
    [InlineData("Kitty", false)]
    [InlineData("WezTerm", false)]
    [InlineData("WindowsConsole", false)]
    [InlineData("None", false)]
    public void The_seams_pick_tmux_calls_for_tmux_and_the_route_aware_ones_otherwise(string routeName, bool tmux)
    {
        var route = Enum.Parse<TerminalTyping.Route>(routeName);
        Assert.Equal(tmux, LocalCliChatSession.PickCapture(route).Method == ((Func<SessionStatus, Task<string?>>)TerminalFocuser.CapturePane).Method);
        Assert.Equal(tmux, LocalCliChatSession.PickPress(route).Method == ((Func<SessionStatus, string, Task<bool>>)TerminalFocuser.SendPaneKey).Method);
        Assert.Equal(tmux, LocalCliChatSession.PickType(route).Method == ((Func<SessionStatus, string, Task<bool>>)TerminalFocuser.SendPaneText).Method);
    }

    [Theory]
    [InlineData("Tmux", false)]
    [InlineData("None", false)]
    [InlineData("ITerm2", true)]
    [InlineData("Kitty", true)]
    public void Only_a_real_non_tmux_route_gets_the_verified_dialog_press(string routeName, bool verified) =>
        Assert.Equal(verified, LocalCliChatSession.IsVerifiedRoute(Enum.Parse<TerminalTyping.Route>(routeName)));

    // An ordinary dialog on iTerm2 and the like: pressed only if still the dialog shown.
    [AvaloniaFact]
    public async Task An_ordinary_dialog_on_a_non_tmux_route_is_pressed_through_the_verified_press()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.MiniExitDialog);
            var plain = 0;
            var session = new LocalCliChatSession("s1", Waiting())
            {
                Capture = pane.Capture, CaptureAndPress = pane.CaptureAndPress,
                PressKey = (_, _) => { plain++; return Task.FromResult(true); },
                RouteOf = _ => TerminalTyping.Route.ITerm2,
            };
            session.UpdateStatus(Waiting());
            await Pump(() => session.Prompt is not null);

            await session.AnswerAsync(session.Prompt!.Options[0]);
            Assert.Equal(0, plain);
            Assert.Single(pane.Verified);
        });
    }

    [AvaloniaFact]
    public async Task A_code_typed_while_the_field_already_has_text_types_nothing_and_says_so()
    {
        await WithReplyOn(async () =>
        {
            // The url screen is read as empty first; then somebody types into the
            // terminal's own field, and the screen on re-read has a tail.
            var pane = new Pane(Screen("login-url-w100"));
            var session = await On(pane);
            pane.Screen = Screen("login-code-typed-w100");

            await session.SubmitLoginCodeAsync("QA-CANARY-abcdef");

            Assert.Empty(pane.Typed);
            Assert.Empty(pane.Pressed);
            var note = Assert.Single(session.History).Text;
            Assert.Equal("The code field in the terminal isn't empty, so nothing was typed. Clear it there first.", note);
            Assert.DoesNotContain("CANARY", note);
        });
    }

    [AvaloniaFact]
    public async Task A_code_that_does_not_read_back_says_so_exactly_and_presses_no_Enter()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(Screen("login-url-w100"));
            var session = await On(pane);

            await session.SubmitLoginCodeAsync("QA-CANARY-abcdef");

            Assert.Empty(pane.Pressed);
            var note = Assert.Single(session.History).Text;
            Assert.Equal("Couldn't confirm the code was typed, so it was not sent. Finish in the terminal.", note);
            Assert.DoesNotContain("CANARY", note);
        });
    }

    [Theory]
    [InlineData("prompt-changed", "That sign-in screen had changed, so nothing was pressed. Here is the current one.")]
    [InlineData("no-prompt", "That sign-in screen has already closed.")]
    [InlineData("not-empty", "The code field in the terminal isn't empty, so nothing was typed. Clear it there first.")]
    [InlineData("unverified", "Couldn't confirm the code was typed, so it was not sent. Finish in the terminal.")]
    [InlineData("press-failed", "Couldn't answer that in the terminal. Finish signing in there.")]
    [InlineData("type-failed", "Couldn't answer that in the terminal. Finish signing in there.")]
    public void Every_refusal_code_has_its_own_exact_note(string code, string note) =>
        Assert.Equal(note, LocalCliChatSession.LoginRefusalNote(code));
}
