using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Orbweaver
{
    // Reads Claude Code's JSONL transcript to extract the latest assistant
    // message text. Reads from the tail (transcripts reach tens of MB) and
    // stops at the first assistant record found.
    public static class TranscriptReader
    {
        private const int TailBytes = 262144;
        private const int MaxSpokenChars = 1500;

        // The last thing a Codex session said, for the orb's speak button.
        //
        // Separate entry point rather than a branch inside LatestAssistantText,
        // and the separation is the point: that method falls back to searching
        // ~/.claude/projects when it finds nothing, which for a Codex session
        // would find a *Claude Code* transcript for the same directory and speak
        // an unrelated session's last turn out of a Codex orb. There is no
        // equivalent fallback here and there should not be one — a rollout that
        // cannot be read is silence, which is the honest answer.
        //
        // Excluded from coverage: the try/catch only. Everything it wraps is in
        // NewestCodexText below and stays measured.
        //
        // The catch has never been reached and reasoning says it cannot be:
        // TailLines already swallows a read that fails, and CodexTranscript.Map
        // returns nothing for a row it cannot parse rather than throwing — which
        // CodexTranscriptItemTests asserts directly. It stays because this runs
        // inside a hook call, where a throw is not free, and because the format it
        // reads belongs to somebody else.
        [ExcludeFromCodeCoverage]
        public static string? LatestCodexAgentText(string? transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath))
                return null;

            try { return NewestCodexText(transcriptPath); }
            catch { return null; }
        }

        // The last thing a Grok session said. Separate from LatestAssistantText
        // for the same reason LatestCodexAgentText is: that method falls back
        // into ~/.claude/projects, which would speak a Claude Code session out
        // of a Grok orb.
        [ExcludeFromCodeCoverage]
        public static string? LatestGrokAgentText(string? transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath))
                return null;

            try { return NewestGrokText(transcriptPath); }
            catch { return null; }
        }

        private static string? NewestGrokText(string transcriptPath)
        {
            var lines = TailLines(transcriptPath);
            var mapped = GrokTranscript.Map(lines);
            var text = mapped
                .Where(t => t.Turn.Role == ChatRole.Assistant)
                .Select(t => t.Turn.Text)
                .LastOrDefault();

            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.Length > MaxSpokenChars ? text[..MaxSpokenChars] + "…" : text;
        }

        private static string? NewestCodexText(string transcriptPath)
        {
            var lines = TailLines(transcriptPath);
            {
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    // The same pre-filter CodexTranscript uses, for the same
                    // reason: the rows that carry the bytes are the ones this
                    // does not want.
                    if (!CodexTranscript.IsInteresting(lines[i])) continue;

                    var turns = CodexTranscript.Map(new[] { lines[i] });
                    var text = turns
                        .Where(t => t.Turn.Role == ChatRole.Assistant)
                        .Select(t => t.Turn.Text)
                        .LastOrDefault();

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    return text.Length > MaxSpokenChars ? text[..MaxSpokenChars] + "…" : text;
                }
            }

            return null;
        }

        // Excluded from coverage: the try/catch only, for the same reason as
        // LatestCodexAgentText above.
        [ExcludeFromCodeCoverage]
        public static string? LatestAssistantText(string? transcriptPath, string? sessionId = null)
        {
            if (string.IsNullOrEmpty(transcriptPath) && !string.IsNullOrEmpty(sessionId))
                transcriptPath = FindTranscript(sessionId);

            if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath))
                return null;

            try { return NewestAssistantText(transcriptPath); }
            catch { return null; }
        }

        private static string? NewestAssistantText(string transcriptPath)
        {
            var lines = TailLines(transcriptPath);
            {
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    if (!line.Contains("\"type\":\"assistant\""))
                        continue;

                    var text = ExtractText(line);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Length > MaxSpokenChars
                            ? text[..MaxSpokenChars] + "…"
                            : text;
                }
            }

            return null;
        }

        // Find the most recently written transcript in any project
        // directory whose path encodes the given CWD.  Used when a
        // controller session has no transcript of its own — its
        // background jobs live in sibling project dirs with the same
        // CWD prefix.
        // home is a parameter with the real one as its default, so the walk can be
        // pointed at a temp directory instead of the machine's own transcripts.
        public static string? LatestTranscriptForCwd(string cwd, string? home = null)
        {
            if (string.IsNullOrEmpty(cwd)) return null;

            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string> { Path.Combine(home, ".claude") };
            foreach (var extra in OrbweaverSettings.ClaudeCodeProfileDirs)
                dirs.Add(Path.Combine(home, extra));

            var encoded = EncodeCwd(cwd);

            string? best = null;
            DateTime bestTime = DateTime.MinValue;

            foreach (var configDir in dirs)
            {
                var projects = Path.Combine(configDir, "projects");
                if (!Directory.Exists(projects)) continue;

                foreach (var dir in SafeDirectories(projects))
                {
                    if (!ProjectDirMatches(Path.GetFileName(dir), encoded)) continue;

                    foreach (var file in SafeFiles(dir, "*.jsonl"))
                    {
                        var mod = SafeWriteTime(file);
                        if (mod is null || mod <= bestTime) continue;

                        bestTime = mod.Value;
                        best = file;
                    }
                }
            }

            return best;
        }

        // When the hook hasn't written transcript_path yet (old status
        // file), find <session-id>.jsonl under the known Claude Code
        // config directories.
        //
        // Public as well as used internally: the chat panel needs the same
        // answer for the same reason, and a session whose status file predates
        // transcript_path is exactly the one whose orb you'd click wondering
        // what it had been doing.
        public static string? FindTranscriptFor(string sessionId, string? home = null) =>
            FindTranscript(sessionId, home);

        private static string? FindTranscript(string sessionId, string? home = null)
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string> { Path.Combine(home, ".claude") };

            foreach (var extra in OrbweaverSettings.ClaudeCodeProfileDirs)
                dirs.Add(Path.Combine(home, extra));

            var filename = sessionId + ".jsonl";
            foreach (var configDir in dirs)
            {
                var projects = Path.Combine(configDir, "projects");
                if (!Directory.Exists(projects)) continue;

                var match = SafeFindDeep(projects, filename);
                if (match is not null) return match;
            }

            return null;
        }

        // Claude Code encodes /Users/foo/Source/Bar as -Users-foo-Source-Bar
        // inside its projects directory. Pure, and worth its own name because the
        // encoding is lossy — a directory whose name contains a dash is
        // indistinguishable from a path separator once encoded — which is exactly
        // why the match below cannot be a plain prefix test.
        internal static string EncodeCwd(string cwd)
        {
            var encoded = cwd.Replace(Path.DirectorySeparatorChar, '-');

            return encoded.Length > 0 && encoded[0] != '-' ? "-" + encoded : encoded;
        }

        // Whether one projects-directory name is this cwd or something under it.
        //
        // The second condition is the one that matters: a plain StartsWith would
        // match -Users-foo-Source-Barn for -Users-foo-Source-Bar and hand back a
        // transcript from an entirely different project. Requiring the next
        // character to be the separator is what keeps sibling directories with a
        // shared prefix apart.
        internal static bool ProjectDirMatches(string dirName, string encoded)
        {
            if (!dirName.StartsWith(encoded, StringComparison.Ordinal)) return false;

            return dirName.Length <= encoded.Length || dirName[encoded.Length] == '-';
        }

        // Four thin wrappers, all excluded, all for the same thing: these walk a
        // directory tree that live sessions are writing to, so a directory can
        // stop existing between being listed and being read, and a file between
        // being listed and being asked its age. None of that is a state a test can
        // hold still, and none of it is worth failing a lookup over — a transcript
        // that cannot be read is simply one this search does not return.
        //
        // Separated so the walk itself stays measured: which directory names match
        // an encoded cwd, and which of the files under them is newest, is the part
        // with a decision in it.
        [ExcludeFromCodeCoverage]
        internal static IEnumerable<string> SafeDirectories(string root)
        {
            try { return Directory.EnumerateDirectories(root).ToList(); }
            catch { return Array.Empty<string>(); }
        }

        [ExcludeFromCodeCoverage]
        internal static IEnumerable<string> SafeFiles(string dir, string pattern)
        {
            try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
            catch { return Array.Empty<string>(); }
        }

        [ExcludeFromCodeCoverage]
        private static DateTime? SafeWriteTime(string file)
        {
            try { return File.GetLastWriteTimeUtc(file); }
            catch { return null; }
        }

        [ExcludeFromCodeCoverage]
        private static string? SafeFindDeep(string root, string filename)
        {
            try
            {
                return Directory.EnumerateFiles(root, filename, SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        // Excluded from coverage: the try/catch only. The window logic it wraps is
        // in ReadTail below and stays covered — what cannot be arranged is the
        // catch, which is for the file disappearing between the caller's
        // File.Exists and this open. That is a real race, since the file belongs
        // to a session that may be ending, but it is a race and not a state a test
        // can hold still.
        // internal rather than private because SessionManager reads the same
        // window for a different question — what the session is called — and it
        // matters that the two windows are the same one. The hook decides a
        // title from the last 256KB; if the app looked at a different amount it
        // could disagree with the hook about a session's name, which is the one
        // thing TranscriptIdentity exists to avoid.
        [ExcludeFromCodeCoverage]
        internal static string[] TailLines(string path)
        {
            try { return ReadTail(path, TailBytes); }
            catch { return Array.Empty<string>(); }
        }

        // The same window, sized by the caller. TranscriptHandoff asks a
        // different question of a different amount — its comment says why its
        // window is smaller and why that is safe for *its* question — and what
        // matters here is that both read through one implementation, so "drop
        // the torn first line" cannot drift into two differently-wrong copies.
        [ExcludeFromCodeCoverage]
        internal static string[] TailLines(string path, int tailBytes)
        {
            try { return ReadTail(path, tailBytes); }
            catch { return Array.Empty<string>(); }
        }

        private static string[] ReadTail(string path, int tailBytes)
        {
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long start = Math.Max(0, fs.Length - tailBytes);
                fs.Seek(start, SeekOrigin.Begin);

                using var reader = new StreamReader(fs);
                var chunk = reader.ReadToEnd();

                // If we seeked past the beginning, the first partial line is
                // garbage — drop it.
                if (start > 0)
                {
                    int nl = chunk.IndexOf('\n');
                    if (nl >= 0)
                        chunk = chunk[(nl + 1)..];
                }

                return chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        // Transcript assistant records have a top-level "type":"assistant"
        // (not necessarily the first key) and a "message" object whose
        // "content" array holds text and tool_use blocks. We extract and
        // concatenate the text blocks.
        private static string? ExtractText(string jsonLine)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp)
                    || typeProp.GetString() != "assistant")
                    return null;

                if (!root.TryGetProperty("message", out var message))
                    return null;
                if (!message.TryGetProperty("content", out var content))
                    return null;
                if (content.ValueKind != JsonValueKind.Array)
                    return null;

                var sb = new System.Text.StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType)
                        && blockType.GetString() == "text"
                        && block.TryGetProperty("text", out var textProp))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(textProp.GetString());
                    }
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
