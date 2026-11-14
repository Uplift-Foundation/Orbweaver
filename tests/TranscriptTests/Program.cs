using Orbweaver;
using Orbweaver.Tests;

// Two things that turn text nobody controls into things the panel shows, and
// both fail quietly when they fail.
//
// The transcript mapper decides what a conversation *was*, and its failure mode
// is a message silently missing — a row type it didn't recognise, a filter that
// caught too much. Nothing on screen says so.
//
// The dialog parser is worse. Its output becomes buttons that send keystrokes
// into a live session, so a mis-parse doesn't show a wrong label, it *presses
// the wrong thing*. Most of the cases below are attempts to make it do that.
//
// Run with `dotnet run --project tests/TranscriptTests`. Non-zero exit means
// something regressed and each failure prints what it expected.
//
// The cases themselves live in TranscriptSuite.cs, so tests/UnitTests can
// compile them in and run the identical checks - this project is a plain console
// exe with no test SDK reference, so tools/coverage.sh counted none of what it
// verifies. What stayed here is the half that only makes sense at a terminal:
// the report below, and the file-parsing mode above.

// Given a file, says what the real parsers make of it instead of running the
// suite. A .jsonl is read as a transcript, anything else as a captured pane:
//
//   dotnet run --project tests/TranscriptTests -- ~/.claude/projects/<dir>/<id>.jsonl
//
//   tmux capture-pane -p -t %30 > /tmp/pane.txt
//   dotnet run --project tests/TranscriptTests -- /tmp/pane.txt
//
// Every fixture below was confirmed this way against a live Claude Code pane
// before being written down, which is the only reason they match real output
// rather than plausible output. The first version of the dialog parser was
// written against an invented fixture and got the shape wrong three ways.
if (args.Length > 0)
{
    var path = args[0];

    if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
    {
        // The tail, the same window ClaudeCodeChatSession opens with.
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var from = Math.Max(0, fs.Length - 512 * 1024);
        fs.Seek(from, SeekOrigin.Begin);
        var text = new StreamReader(fs).ReadToEnd();
        if (from > 0 && text.IndexOf('\n') >= 0) text = text[(text.IndexOf('\n') + 1)..];

        // Which CLI wrote it. Both write .jsonl, so the extension decides
        // nothing — and handing a Codex rollout to ChatTranscript prints "0
        // turns", which reads as a parser bug rather than as the wrong parser.
        //
        // The first row of a rollout is Codex's own session header, which is
        // unambiguous; the filename is the fallback for a copy that has been
        // trimmed of it.
        fs.Seek(0, SeekOrigin.Begin);
        var header = new StreamReader(fs, leaveOpen: true).ReadLine() ?? "";
        var grok = header.Contains("session/update", StringComparison.Ordinal)
                    || header.Contains("updates.jsonl", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("updates.jsonl", StringComparison.OrdinalIgnoreCase);
        var codex = header.Contains("\"type\":\"session_meta\"", StringComparison.Ordinal)
                    || Path.GetFileName(path).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var turns = grok ? GrokTranscript.Map(lines)
            : codex ? CodexTranscript.Map(lines)
            : ChatTranscript.Map(lines);
        var label = grok ? "grok" : codex ? "codex" : "claude code";
        Console.WriteLine(
            $"{turns.Count} turns from the last {(fs.Length - from) / 1024}KB, read as {label}\n");

        foreach (var t in turns)
        {
            var first = t.Turn.Text.Split('\n')[0];
            if (first.Length > 96) first = first[..96] + "…";
            Console.WriteLine($"{t.Turn.Role,-9} {first}");
        }

        return 0;
    }

    var screen = File.ReadAllText(path);

    // What the typing gate makes of the same screen, and which check decided
    // it (CB-327): "no dialog" alone cannot tell a prompt from a screen the
    // gate refuses, and that difference is the whole bug on a narrow pane.
    var (reading, check) = PaneScreen.Read(screen);
    Console.WriteLine($"pane: {reading} ({check})");

    var form = ChatTranscript.ParseAskForm(screen);
    Console.WriteLine(form is null
        ? "form: none"
        : $"form: {form.Tabs.Count} tab(s), {form.Prompt.Options.Count} option(s), free text [{form.FreeTextKey ?? "-"}]");

    var dialog = ChatTranscript.ParseDialog(screen);

    if (dialog is null)
    {
        Console.WriteLine("no dialog — the panel would offer the terminal instead");
        return 0;
    }

    Console.WriteLine($"title: {dialog.Title}");
    foreach (var o in dialog.Options) Console.WriteLine($"  [{o.Key}] {o.Label}");
    return 0;
}

var failures = TranscriptSuite.RunAll();

// --- report ---

if (failures.Count == 0)
{
    Console.WriteLine("all passed");
    return 0;
}

Console.WriteLine($"{failures.Count} failed:");
foreach (var f in failures) Console.WriteLine("  ✗ " + f);
return 1;
