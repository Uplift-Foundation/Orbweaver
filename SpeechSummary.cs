using System.IO;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Orbweaver
{
    // What the speaker reads: the whole reply, or two or three sentences of it.
    //
    // The motivation is length. A reply that takes minutes to read aloud is not
    // something anyone listens to, so the speaker is most useful for knowing
    // *what happened* without reading — and that is a different artefact from
    // the reply itself, not a shorter rendering of it.
    internal enum SpeakScope
    {
        // Everything the assistant said, which is what this app has always done
        // and remains the default. Nobody's speaker changes character on
        // upgrade.
        Full,

        // The requester's own phrase, kept rather than sanded down to "Summary":
        // it says what the mode is for better than the neutral word does.
        Summary,
    }

    // The decision about *which text* to speak, with no process, no settings and
    // no speech engine behind it.
    //
    // Split out of ChatPanel.SpeakLatest deliberately. The utterance itself is
    // excluded from coverage — TextToSpeech.Speak "starts a speech engine and
    // makes the machine make a noise" — and leaving the choice inside the method
    // that utters means the choice is excluded along with it. Pure, it gets a
    // case per outcome.
    internal static class SpeechPlan
    {
        // What to do about one reply: the text to work from, and whether it has
        // to be summarised first.
        internal sealed record Plan(string? Text, bool NeedsSummary)
        {
            // Nothing to say. Distinct from a plan whose Text is empty only in
            // that callers read this rather than re-deriving it.
            internal bool Silent => string.IsNullOrWhiteSpace(Text);
        }

        internal static readonly Plan Nothing = new(null, false);

        // Below this, summarising costs more than it saves.
        //
        // The round trip was measured at 6.3-7.3 seconds across four samples on
        // one machine. A reply already about as long as the summary would be
        // therefore buys several seconds of silence in exchange for nothing, and
        // the mode would read as broken on exactly the short replies where it is
        // least needed. 400 characters is roughly three spoken sentences.
        //
        // Deliberately a character count rather than a sentence count: sentence
        // splitting on model output is its own guessing game (abbreviations,
        // code, ellipses), and being approximately right here costs one
        // avoidable round trip at worst.
        internal const int ShortEnoughChars = 400;

        internal static Plan For(string? lastAssistantText, SpeakScope scope)
        {
            if (string.IsNullOrWhiteSpace(lastAssistantText)) return Nothing;

            if (scope == SpeakScope.Full) return new Plan(lastAssistantText, false);

            // Trimmed before measuring: a reply padded with blank lines is not a
            // long reply, and the whitespace is not spoken either way.
            var trimmed = lastAssistantText.Trim();
            return trimmed.Length <= ShortEnoughChars
                ? new Plan(trimmed, false)
                : new Plan(trimmed, true);
        }
    }

    // Producing those two or three sentences.
    //
    // **It does not ask the user's own session.** The CLI is right there and
    // already holds the context, which makes it the tempting answer and the
    // wrong one: it would inject a turn into the conversation this very panel is
    // displaying, spend the user's tokens and context, and appear in their
    // history as something they did not say. For a feature whose whole point is
    // to be ambient, that is close to disqualifying.
    //
    // So this is a separate, throwaway `claude -p` — a different process, a
    // different conversation, nothing written into the user's transcript. It
    // needs no credential of its own, which is the reason this shape wins over a
    // direct API call: the CLI is already authenticated, and UsagePoller has
    // established the pattern of spawning it for an account-scoped answer rather
    // than this app ever holding a token.
    // Which of two things is being summarised. A reply's summary answers
    // "what did the assistant just say"; a turn-finished summary answers a
    // different question — "what did the session just do, and what's next"
    // — which is CB-167's vibe-summary sound rather than CB-165's speak
    // button. Kept as a parameter on the existing methods rather than a
    // second parallel class, because everything downstream of the prompt —
    // cleaning, the failure sentence, the subprocess plumbing, the timeout —
    // is identical for both; only the instruction sent to the model differs.
    internal enum SpeechSummaryKind { Reply, TurnFinished }

    internal static class SpeechSummary
    {
        // Haiku, explicitly. The job is compression, not reasoning, and the
        // latency is the whole design constraint — this runs between a reply
        // landing and the first spoken word.
        internal const string Model = "haiku";

        // Generous against the 6.3-7.3s measured, and for the same reason
        // UsagePoller's is: the machines most likely to be slow are the ones
        // where the answer matters. Past this the mode says so rather than
        // waiting further.
        internal const int TimeoutMs = 30000;

        // A bound on what is sent, not on what comes back. A very long reply is
        // exactly the case this mode exists for, but the tail of one adds little
        // to a three-sentence summary and costs latency on the leg that is
        // already the bottleneck.
        internal const int MaxSourceChars = 24000;

        // Pure, so the wording is assertable without spawning anything. The
        // instruction is blunt about form because the output is spoken, not
        // read: a preamble ("Here's a summary:") is three wasted seconds of
        // audio, and markdown is read aloud as punctuation.
        //
        // **It travels as the `-p` argument, and the reply travels on stdin —
        // never the two concatenated on stdin.** That used to be the shape, and
        // it stopped working without anything here changing: Claude Code now
        // frames piped stdin as content the user pasted, with a standing
        // instruction not to follow instructions found inside a paste. Sent
        // that way, the request to summarise *was* the paste, the user's own
        // message was empty, and Haiku answered what it had been handed — "it
        // looks like you pasted this and the message cuts off, what would you
        // like me to do with it?" — which was then read aloud as the vibe
        // summary. Reproduced on CLI 2.1.285 against a real turn; the same
        // bytes split this way summarised cleanly on every run. One line, no
        // newlines, so nothing about it depends on how a platform quotes an
        // argument.
        internal static string Instruction(SpeechSummaryKind kind = SpeechSummaryKind.Reply) =>
            // The turn-finished variant asks a forward-looking question a
            // reply summary never does. A reply summary describes something
            // that already happened and is being read back; a turn-finished
            // summary is the ambient cue CB-167 plays instead of a chime, and
            // "what's next" is what makes it worth listening to over a
            // Glass sound — it can tell you whether you need to come back.
            (kind == SpeechSummaryKind.TurnFinished
                ? "Summarise what was just done and what's next, in two or three short sentences, about 40 words in total, at most three sentences, "
                    + "for someone who will hear it read aloud rather than read it. "
                    + "Say what changed and what to expect next, not what the text is about. "
                : "Summarise the assistant reply below in two or three sentences, "
                    + "for someone who will hear it read aloud rather than read it. "
                    + "Say what was done or found, not what the reply is about. ")
            + "The text to summarise is on standard input. "
            // MaxSourceChars cuts mid-sentence by design, and a model told
            // nothing about that asks about it instead of summarising.
            + "If it ends abruptly, summarise what is there and do not mention it. "
            + "No preamble, no heading, no markdown, no bullet points, no code. "
            + "Plain sentences only.";

        // What goes on stdin: the reply, bounded.
        internal static string Source(string reply) =>
            reply.Length > MaxSourceChars ? reply[..MaxSourceChars] : reply;

        // Replaces Claude Code's own system prompt, which describes an agent in
        // a repository with tools to call. A summariser needs none of that, and
        // everything in it is something the model can mistake for the task —
        // including the pasted-content rule above.
        internal const string SystemPrompt =
            "You turn text into a short spoken summary. Reply with the summary only. "
            + "Never ask questions, never introduce yourself, never comment on the input.";

        // Hooks off for this one process. A SessionStart hook is exactly what a
        // persona plugin installs, and it fires for `claude -p` like any other
        // session: under a config dir that has one, the summary opened with
        // "I'm Claude Haiku, the assistant for this codebase" — CB-174's
        // symptom arriving by a route the neutral working directory cannot
        // close, because hooks come from settings, not from CLAUDE.md.
        internal const string SettingsOverride = "{\"disableAllHooks\":true}";

        // Model output is not a summary until the preamble it was told not to
        // write has been taken off it anyway. Pure, and lenient: anything this
        // does not recognise is passed through rather than mangled, because a
        // slightly untidy summary is a far better outcome than an empty one.
        internal static string? Clean(string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            var text = output.Trim();

            // A fenced block: the instruction said no code, and a model that
            // wrapped its prose in one anyway would otherwise have the fence
            // spoken as "backtick backtick backtick".
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0) text = text[(firstBreak + 1)..];
                if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
                text = text.Trim();
            }

            // The preamble it was told not to write. Only removed when a colon
            // ends it within the first line and something follows, so a summary
            // whose own first sentence happens to contain a colon survives.
            const string marker = ":";
            var newline = text.IndexOf('\n');
            var firstLine = newline < 0 ? text : text[..newline];
            if (firstLine.Length <= 40
                && firstLine.EndsWith(marker, StringComparison.Ordinal)
                && newline >= 0
                && !string.IsNullOrWhiteSpace(text[(newline + 1)..]))
            {
                text = text[(newline + 1)..].Trim();
            }

            // Spoken, so the paragraph breaks are not information. Collapsing
            // them keeps the engines from pausing as if a new topic had started.
            var collapsed = new StringBuilder(text.Length);
            var lastWasSpace = false;
            foreach (var ch in text)
            {
                var isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!lastWasSpace) collapsed.Append(' ');
                }
                else
                {
                    collapsed.Append(ch);
                }

                lastWasSpace = isSpace;
            }

            var result = collapsed.ToString().Trim();
            return result.Length == 0 ? null : result;
        }

        // The prompt already said "one to three sentences" and Haiku wrote six or
        // seven short ones anyway (250-470 chars in the bridge log), so the count
        // is enforced here as well as asked for.
        internal const int TurnFinishedMaxSentences = 3;
        internal const int TurnFinishedMaxChars = 400;

        // Dotted words that end in a full stop without ending a sentence.
        private static readonly HashSet<string> Abbreviations =
            new(StringComparer.OrdinalIgnoreCase) { "e.g", "i.e", "vs", "mr", "mrs", "ms", "dr", "approx" };

        // Closing quotes and brackets that may sit between a stop and the
        // whitespace after it: He said "done." Then...
        private const string Closers = "\"')]}\u201D\u2019\u00BB";

        // Where each sentence ends: `.`, `!` or `?` followed by whitespace or the
        // end of the text. "0.5" has no whitespace after its dot, an ellipsis is
        // skipped (a trailing "..." is a pause, not a stop), and the
        // abbreviations above are skipped.
        internal static List<int> SentenceEnds(string t)
        {
            var ends = new List<int>();
            for (var i = 0; i < t.Length; i++)
            {
                if (t[i] is not ('.' or '!' or '?')) continue;

                // The stop counts only when whitespace or the end follows it,
                // after any closing quotes or brackets.
                var after = i + 1;
                while (after < t.Length && Closers.Contains(t[after])) after++;
                if (after < t.Length && !char.IsWhiteSpace(t[after])) continue;

                if (t[i] == '.' && i > 0 && t[i - 1] == '.') continue;
                if (t[i] == '.')
                {
                    var start = i;
                    while (start > 0 && !char.IsWhiteSpace(t[start - 1])) start--;
                    var word = t[start..i].TrimStart('(', '"', '\'', '[', '\u201C', '\u2018');
                    if (Abbreviations.Contains(word) || IsInitials(word)) continue;
                }
                ends.Add(after - 1);
            }
            return ends;
        }

        // "U.S", "a.m" — dotted single letters are initials. A lone letter is not:
        // "plan B." and "step A." really do end sentences, and a summary naming a
        // person by initial is rarer than either.
        private static bool IsInitials(string word)
        {
            if (word.Length < 3) return false;
            for (var i = 0; i < word.Length; i++)
                if (i % 2 == 0 ? !char.IsLetter(word[i]) : word[i] != '.') return false;
            return word.Length % 2 == 1;
        }

        // Keep at most `maxSentences` sentences, then at most `maxChars`
        // characters: cut at the last sentence end inside the cap, else the last
        // word boundary, else hard (one unbroken token). Text that already fits
        // both limits comes back trimmed but otherwise unchanged.
        internal static string Shorten(string? text, int maxSentences, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text.Trim();

            var ends = SentenceEnds(t);
            if (ends.Count > maxSentences) t = t[..(ends[maxSentences - 1] + 1)];
            if (t.Length <= maxChars) return t;

            var lastEnd = ends.LastOrDefault(e => e < maxChars, -1);
            if (lastEnd >= 0) return t[..(lastEnd + 1)];

            // Any whitespace is a word boundary, not only a space. The hard cut
            // steps back over a split surrogate pair rather than leave half an emoji.
            var window = t[..maxChars];
            if (char.IsWhiteSpace(t[maxChars])) return window.TrimEnd();
            var space = -1;
            for (var i = window.Length - 1; i > 0; i--)
                if (char.IsWhiteSpace(window[i])) { space = i; break; }
            if (space > 0) return window[..space].TrimEnd();
            return char.IsHighSurrogate(window[^1]) ? window[..^1] : window;
        }

        // What the speaker says when the summary could not be produced.
        //
        // Deliberately not "fall back to the full text". Somebody who chose this
        // mode chose it to avoid a five-minute reading, and handing them one
        // because the summariser failed is the opposite of what they asked for.
        // Deliberately not silence either: the acceptance criteria call for a
        // stated failure, and a speaker that simply says nothing is
        // indistinguishable from a broken one.
        internal const string Unavailable = "Summary unavailable.";

        // The one failure worth naming out loud. An account over its spend
        // limit fails every summary until the limit resets, and "unavailable"
        // on every turn reads exactly like a bug in this app — it was chased as
        // one before anyone ran the CLI by hand and saw the reason it had been
        // printing all along. The CLI says so on stdout with exit 1:
        // "You've hit your org's monthly spend limit · run /usage-credits ...".
        internal const string SpendLimitReached = "Spend limit reached.";

        // What a failed CLI run should be spoken as, from what it printed.
        // Pure, so the matching is pinned without spawning anything; null means
        // the output named nothing more specific than Unavailable already says.
        internal static string? FailureSentence(string? output) =>
            output is not null && output.Contains("spend limit", StringComparison.OrdinalIgnoreCase)
                ? SpendLimitReached
                : null;

        // A failure that already knows what to say. Thrown rather than returned
        // so the seam keeps its string-or-null shape; SummarizeOrSayWhyAsync
        // speaks its message instead of Unavailable.
        internal sealed class SpokenFailureException(string sentence) : Exception(sentence);

        // The seam. UI and unit tests drive the whole path through this without
        // ever spawning a CLI; null means the real one.
        internal static Func<string, Task<string?>>? SummarizerForTests;

        // The same seam for a test that is about *whose account* the summary
        // runs on (CB-248) rather than what comes back: it is handed the
        // account directory alongside the reply. Checked first, so the many
        // tests that only care about the text keep the one-argument shape.
        internal static Func<string, string?, Task<string?>>? AccountSummarizerForTests;

        // `kind` only ever changes what RunAsync sends as the prompt — the
        // seam itself stays kind-agnostic, because a test driving it has
        // already decided what comes back and does not need to know which
        // question would have produced it. Reply is the default so every
        // existing caller of the one-argument shape is unchanged.
        //
        // `accountDir` is the Claude Code config directory of the session being
        // summarised, from AccountDirFor; null when it is not known.
        internal static Task<string?> SummarizeAsync(
            string reply, SpeechSummaryKind kind = SpeechSummaryKind.Reply, string? accountDir = null)
        {
            var withAccount = AccountSummarizerForTests;
            if (withAccount is not null) return withAccount(reply, accountDir);

            var seam = SummarizerForTests;
            return seam is not null ? seam(reply) : RunAsync(reply, kind, accountDir);
        }

        // Everything the summary leg decides, with the utterance left to the
        // caller. Separated for the reason OrbWindowSpeakTests' header sets out
        // at length: TextToSpeech.Speak starts a real speech engine, so no
        // headless test may reach it, and a decision left inside the method that
        // speaks is a decision no test can see. This returns the exact string
        // that will be spoken, and is where every arm of "what happens when the
        // summariser does not work" is actually pinned.
        //
        // Never returns null. A failure is a different sentence, not silence.
        internal static Task<string> SummarizeOrSayWhyAsync(string reply) =>
            SummarizeOrSayWhyAsync(reply, SpeechSummaryKind.Reply);

        internal static async Task<string> SummarizeOrSayWhyAsync(
            string reply, SpeechSummaryKind kind, string? accountDir = null)
        {
            try
            {
                var summary = await SummarizeAsync(reply, kind, accountDir).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(summary)) return Unavailable;

                // Only the model's own words are capped. The failure sentences
                // above and below are fixed text and never pass through here.
                return kind == SpeechSummaryKind.TurnFinished ? Shorten(summary, TurnFinishedMaxSentences, TurnFinishedMaxChars) : summary;
            }
            catch (SpokenFailureException ex)
            {
                Console.Error.WriteLine($"{Brand.DisplayName}: couldn't summarise for speech: {ex.Message}");
                return ex.Message;
            }
            catch (Exception ex)
            {
                // A summariser that threw is a summariser that failed, and the
                // answer is the same as any other failure. Surfaced rather than
                // swallowed, for the same reason the speak command's stderr is:
                // this is the only explanation anyone gets for why they heard a
                // sentence instead of their summary.
                Console.Error.WriteLine($"{Brand.DisplayName}: couldn't summarise for speech: {ex.Message}");
                return Unavailable;
            }
        }

        // Where the summariser is run from, and why it is not wherever the app
        // happens to be.
        //
        // `claude -p` discovers the CLAUDE.md at or above its working
        // directory. Inheriting the app's cwd therefore hands the throwaway
        // summariser the whole project's instructions — and CB-174 is what that
        // cost: asked to summarise a reply, it answered *in the project's
        // persona*, declined to summarise without more context, and volunteered
        // an unrelated sentence about a release build earlier that day. All of
        // which was then read aloud.
        //
        // The design CB-165 argued for was "a different process, a different
        // conversation" — this is the line that makes that true rather than
        // merely intended. Measured both ways on one machine, same binary, same
        // model, same stdin: from the project directory a persona reply, from a
        // neutral directory a clean two-sentence summary.
        //
        // The temp directory, because it is the one place guaranteed to have no
        // CLAUDE.md at or above it that belongs to any project. A home-level
        // ~/.claude/CLAUDE.md was checked and does not colour the output here.
        internal static string NeutralWorkingDirectory => Path.GetTempPath();

        // Which Claude account a summary is billed to: the one the summarised
        // session runs under (CB-248).
        //
        // Before this, the child simply inherited this app's environment, so
        // every summary ran on whichever account Buddy itself had been launched
        // under — for a Buddy started from the Start menu or a login item, the
        // default ~/.claude. With one terminal on the default account and
        // another on CLAUDE_CONFIG_DIR=~/.claude-<other>, both sessions'
        // replies were summarised on, and counted against, the default one.
        // It coupled failures too: measured on a real Windows machine, the
        // default account's OAuth expired and every summary for *every*
        // account came back "Summary unavailable.", including the one the user
        // was actively working in and was logged in fine.
        //
        // The account is read off the transcript path, because Claude Code
        // writes every transcript under its own config root —
        // `<root>/projects/<slug>/<id>.jsonl`, or deeper for a subagent's — so
        // the root is the parent of the nearest `projects` above it. Nothing
        // has to be configured for that to hold, which matters: an account the
        // user never listed in ClaudeCodeProfileDirs still bills correctly.
        //
        // Null — today's behaviour, inheriting — whenever that cannot be
        // trusted: no transcript (a gateway or cloud session), no `projects`
        // ancestor (Codex and Grok keep theirs under `sessions`), or a root
        // that does not exist on this machine (a WSL session's Linux path seen
        // from Windows). Naming a nonexistent directory would start a CLI in a
        // fresh, logged-out context, which is the failure this exists to stop.
        internal static string? AccountDirFor(string? transcriptPath, Func<string, bool>? directoryExists = null)
        {
            if (string.IsNullOrWhiteSpace(transcriptPath)) return null;

            var dir = Path.GetDirectoryName(transcriptPath.Trim());
            while (!string.IsNullOrEmpty(dir))
            {
                if (string.Equals(Path.GetFileName(dir), "projects", StringComparison.OrdinalIgnoreCase))
                {
                    var root = Path.GetDirectoryName(dir);
                    return !string.IsNullOrEmpty(root) && (directoryExists ?? Directory.Exists)(root) ? root : null;
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        // The invocation, as a value, so a test can assert it without spawning
        // anything. Extracted for the reason OrbWindowSpeakTests gives about
        // Speak: a decision left inside the method that performs the side
        // effect is a decision nothing can assert — which is exactly how CB-174
        // shipped, since the prompt and the cleaning were both covered while
        // the thing actually wrong was never looked at.
        internal static ProcessStartInfo StartInfoFor(
            string claude, SpeechSummaryKind kind = SpeechSummaryKind.Reply,
            string? accountDir = null, string? home = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = claude,
                WorkingDirectory = NeutralWorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(Instruction(kind));
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(Model);
            startInfo.ArgumentList.Add("--system-prompt");
            startInfo.ArgumentList.Add(SystemPrompt);
            // Empty means no tools at all: nothing here should read a file or
            // run a command on the strength of a reply it was handed.
            startInfo.ArgumentList.Add("--tools");
            startInfo.ArgumentList.Add("");
            startInfo.ArgumentList.Add("--settings");
            startInfo.ArgumentList.Add(SettingsOverride);
            // A throwaway conversation, so it leaves no transcript behind for
            // the session scan or `claude --resume` to find.
            startInfo.ArgumentList.Add("--no-session-persistence");

            // The session's account, three ways (CB-248). Unknown leaves the
            // inherited environment alone, as before. An extra account is named.
            // The default account is *unset* rather than named, for CB-42's
            // reason (see ClaudeProfile): CLAUDE_CONFIG_DIR=~/.claude is a
            // different context from no variable at all — and unset rather than
            // inherited, because a Buddy launched under another account would
            // otherwise bill that one for a default-account session, which is
            // this bug pointing the other way.
            if (accountDir is not null)
            {
                home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var configDir = ClaudeProfile.ConfigDirFor(
                    home, accountDir, OrbweaverSettings.DefaultRemoteControlProfileDir);

                if (configDir is null) startInfo.Environment.Remove("CLAUDE_CONFIG_DIR");
                else startInfo.Environment["CLAUDE_CONFIG_DIR"] = configDir;
            }

            return startInfo;
        }

        // Excluded from coverage: spawns the Claude Code CLI as a subprocess.
        // Everything it decides — the prompt, the tidying, the failure answer,
        // and now the working directory — is pure and covered above; what
        // remains here is process plumbing of the same shape UsagePoller already
        // carries, and running it in a test would make a real billed request on
        // the developer's own account.
        [ExcludeFromCodeCoverage]
        private static async Task<string?> RunAsync(string reply, SpeechSummaryKind kind, string? accountDir)
        {
            var claude = ClaudeBinary.Path;
            if (claude is null) return null;

            try
            {
                var startInfo = StartInfoFor(claude, kind, accountDir);

                using var proc = new Process { StartInfo = startInfo };
                if (!proc.Start()) return null;

                // Before the first byte goes in, because the hook that writes
                // this child's status file fires on its own schedule and the
                // scan runs on a timer: claiming the pid after the round trip
                // would leave a window in which an orb is drawn for it. Released
                // in the finally below rather than here, so it covers the
                // timeout and throw paths as well as the ordinary one.
                InternalSessions.Remember(proc.Id);

                try
                {
                    await proc.StandardInput.WriteAsync(Source(reply)).ConfigureAwait(false);
                    proc.StandardInput.Close();

                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    // Drained as well, and concurrently: a redirected stream
                    // nobody reads can fill its pipe and stall the child, and
                    // it is half of where a failure explains itself.
                    var stderr = proc.StandardError.ReadToEndAsync();

                    using var cts = new CancellationTokenSource(TimeoutMs);
                    try
                    {
                        await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { ProcessTreeKill.Kill(proc); } catch (InvalidOperationException) { }
                        return null;
                    }

                    if (proc.ExitCode != 0)
                    {
                        var said = await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
                        var sentence = FailureSentence(said);
                        if (sentence is not null) throw new SpokenFailureException(sentence);
                        return null;
                    }

                    return Clean(await stdout.ConfigureAwait(false));
                }
                finally
                {
                    InternalSessions.Forget(proc.Id);
                }
            }
            // The same two arms UsagePoller keeps, and for the same reason: a
            // summariser that throws into the UI thread would take the panel
            // with it, and every failure here has one answer anyway.
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
