using System.Text.Json;

namespace Orbweaver
{
    // A Claude Code session that OpenClaw itself started to run one of its
    // agents, and which of the gateway's clusters it belongs to (CB-306).
    //
    // OpenClaw's claude-cli backend runs every gateway session as a `claude -p`
    // process of its own, in the agent's workspace. Measured on the mini
    // (gateway's own store, read read-only): each session row carries its
    // backing CLI session in `cliSessionIds["claude-cli"]`. An agent's main
    // session (a heartbeat target) is backed by one, and so is every
    // Discord channel, DM, cron run, subagent and dashboard session, each by its
    // own. Those processes run the ordinary hooks, so they write ordinary status
    // files, and once CB-306's status-directory fix let the scan find them they
    // became orbs. Each one is a duplicate of a gateway row the OpenClaw
    // integration already draws, or deliberately hides by the user's cluster
    // settings. An "sid:…" orb saying HEARTBEAT_OK is an agent's heartbeat
    // drawn a second time, with the user's "hide heartbeats" ignored.
    //
    // **The gateway does not say which CLI session backs which row.**
    // `sessions.list` was read off the same gateway, 79 rows and 67 fields
    // each, and `cliSessionIds` is not among them. Only the gateway's internal
    // session-store modules read it. So the answer comes from the CLI
    // session's own data, in two halves:
    //
    //   - **Is it one?** `entrypoint: sdk-cli` (how OpenClaw starts it) and a
    //     cwd that is an OpenClaw agent workspace: a `workspace` or
    //     `workspace-*` folder directly inside `.openclaw`, or any workspace the
    //     connected gateway's agents.list names. Both halves are needed. sdk-cli
    //     alone is every `claude -p` anyone runs, and the workspace alone is a
    //     person opening a terminal in an agent's folder, which is their own
    //     session. TMPDIR is not a marker: on the mini three ordinary tmux
    //     sessions write their status into ~/.openclaw/tmp/orbweaver too.
    //
    //   - **Which cluster?** The gateway writes a recognisable first prompt
    //     into each kind of run, and its transcript keeps them: a cron run opens
    //     `[cron:<job-id> <name>]`, a heartbeat is the scheduled-reminder
    //     wrapper around a prompt that says HEARTBEAT, and a chat surface opens
    //     with the `Conversation info: ⟦openclaw:ctx⟧` block (a subagent with
    //     `[Subagent Context]`, an inter-session send with
    //     `[Inter-session message]`). The first few user turns are read, not
    //     only the first, because a main session that resumed opens with an
    //     `[OpenClaw: N earlier messages…]` preamble and only meets its
    //     heartbeat later.
    //
    // A session with none of those markers is a dashboard or New Chat, or a
    // main session whose head named no heartbeat. Both are Native, which is the
    // gateway's own word for "no chat surface behind it". A main that is
    // really a heartbeat target is then governed by the native setting rather
    // than the heartbeat one. That direction only matters when no gateway is
    // connected, because with one connected the duplicate rule below hides the
    // orb whatever its cluster.
    //
    // Pure except ReadCluster, so every rule is a unit test. ReadCluster is the
    // one disk read, and it is memoised.
    public static class OpenClawCliSession
    {
        public const string SdkEntrypoint = "sdk-cli";

        // How many user turns are read before giving up on a marker, and how
        // many characters in total. The first user row was line 3 in every
        // transcript measured, but one prompt carries the whole resumed history
        // and runs long, so the bound is on characters rather than lines.
        internal const int MaxUserTurns = 20;
        internal const int MaxChars = 4 * 1024 * 1024;

        // Whether this session is one OpenClaw spawned. knownWorkspaces is the
        // connected gateway's agents.list workspaces, verbatim. That is the
        // gateway's own string, so ordinal equality is the right comparison.
        public static bool Spawned(string? entrypoint, string? cwd, IEnumerable<string>? knownWorkspaces = null)
        {
            if (!string.Equals(entrypoint, SdkEntrypoint, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(cwd)) return false;

            var trimmed = cwd.TrimEnd('/', '\\');
            if (knownWorkspaces is not null
                && knownWorkspaces.Any(w => string.Equals(w.TrimEnd('/', '\\'), trimmed, StringComparison.Ordinal)))
                return true;

            var parts = trimmed.Split('/', '\\');
            if (parts.Length < 2) return false;

            var leaf = parts[^1];
            var parent = parts[^2];
            return string.Equals(parent, ".openclaw", StringComparison.Ordinal)
                   && (leaf == "workspace" || leaf.StartsWith("workspace-", StringComparison.Ordinal));
        }

        // The cluster one user prompt marks, or null when it marks none.
        // Cron first: a cron job's own prompt can say HEARTBEAT (one measured
        // heartbeat prompt names another job), and the `[cron:` prefix is the
        // gateway's, not the prompt author's.
        public static OrbCluster? ClusterOfPrompt(string? prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return null;

            var text = prompt.TrimStart();
            if (text.StartsWith("[cron:", StringComparison.Ordinal)) return OrbCluster.Crons;

            if (text.StartsWith("Conversation info: ⟦openclaw:ctx⟧", StringComparison.Ordinal)
                || text.Contains("[Subagent Context]", StringComparison.Ordinal)
                || text.StartsWith("[Inter-session message]", StringComparison.Ordinal))
                return OrbCluster.Chats;

            if (text.StartsWith("A scheduled reminder has been triggered", StringComparison.Ordinal)
                && text.Contains("HEARTBEAT", StringComparison.Ordinal))
                return OrbCluster.Heartbeats;

            return null;
        }

        // The cluster a transcript's head marks: the first marked user turn
        // within the bounds above, and Native when none is marked. Null when
        // the head holds no user turn at all yet, so a session whose first
        // prompt has not landed is asked again rather than remembered as Native.
        public static OrbCluster? ClusterOfTranscript(IEnumerable<string> lines) => HeadOf(lines).Cluster;

        // The same answer, and whether it can change. A marked answer cannot,
        // and nor can an unmarked one once the bounds are used up: past them
        // nothing more is read whatever the file grows to. Only an unmarked
        // head still inside the bounds is worth reading again.
        internal static (OrbCluster? Cluster, bool Settled) HeadOf(IEnumerable<string> lines)
        {
            var turns = 0;
            long chars = 0;

            foreach (var line in lines)
            {
                chars += line.Length;
                if (chars > MaxChars) return (turns == 0 ? null : OrbCluster.Native, true);

                var prompt = UserPromptOf(line);
                if (prompt is null) continue;

                turns++;
                if (ClusterOfPrompt(prompt) is { } marked) return (marked, true);
                if (turns >= MaxUserTurns) return (OrbCluster.Native, true);
            }

            return (turns == 0 ? null : OrbCluster.Native, false);
        }

        // A transcript row's user prompt text, or null for any other row. A
        // tool result is a user row too, and carries no text parts, so it reads
        // as an empty prompt and is not counted.
        internal static string? UserPromptOf(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "user"
                    || !root.TryGetProperty("message", out var message)
                    || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content))
                    return null;

                if (content.ValueKind == JsonValueKind.String)
                {
                    var s = content.GetString();
                    return string.IsNullOrEmpty(s) ? null : s;
                }

                if (content.ValueKind != JsonValueKind.Array) return null;

                var text = string.Concat(content.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.Object
                                && p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                                && t.GetString() == "text"
                                && p.TryGetProperty("text", out var v) && v.ValueKind == JsonValueKind.String)
                    .Select(p => p.GetProperty("text").GetString()));
                return text.Length == 0 ? null : text;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Whether the gateway the viewer is connected to already represents
        // this session: its cwd is a workspace that gateway's agents.list
        // names. Then the gateway's orb is the session, shown or hidden by the
        // cluster settings in OpenClawSessions.Parse, and this orb would be the
        // same agent drawn twice.
        public static bool RepresentedByGateway(
            string? cwd, bool openClawEnabled, IEnumerable<string> gatewayWorkspaces)
        {
            if (!openClawEnabled || string.IsNullOrWhiteSpace(cwd)) return false;

            var trimmed = cwd.TrimEnd('/', '\\');
            return gatewayWorkspaces.Any(w => string.Equals(w.TrimEnd('/', '\\'), trimmed, StringComparison.Ordinal));
        }

        // The decision. A session OpenClaw did not spawn (cluster null) is
        // always shown, which is the rule for every ordinary session. One it
        // did spawn is hidden when the connected gateway already represents
        // it, and otherwise follows the viewer's own cluster settings, the
        // same OrbClusters.Visible the gateway rows go through.
        public static bool Show(
            OrbCluster? cluster, bool representedByGateway,
            ClusterMode heartbeats, ClusterMode crons, ClusterMode native)
        {
            if (cluster is not { } c) return true;
            if (representedByGateway) return false;

            return OrbClusters.Visible(c, heartbeats, crons, native);
        }

        // --- wire format, for the peer roster ----------------------------------

        public static string Name(OrbCluster cluster) => cluster switch
        {
            OrbCluster.Heartbeats => "heartbeats",
            OrbCluster.Crons => "crons",
            OrbCluster.Native => "native",
            _ => "chats"
        };

        // An unrecognised word is a newer Buddy's cluster this one does not
        // know. It reads as Chats, the cluster with no setting that hides it,
        // for the reason OrbClusters.Parse gives: an orb missing for no stated
        // reason is the hardest bug to notice. Null or empty means the far
        // Buddy did not mark the session as OpenClaw's at all.
        public static OrbCluster? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            return value.Trim().ToLowerInvariant() switch
            {
                "heartbeats" => OrbCluster.Heartbeats,
                "crons" => OrbCluster.Crons,
                "native" => OrbCluster.Native,
                _ => OrbCluster.Chats
            };
        }

        // --- the one disk read ---------------------------------------------------

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Memo> Memos = new(StringComparer.Ordinal);

        private readonly record struct Memo(long Length, OrbCluster? Cluster, bool Settled);

        // The cluster of a session that Spawned says is OpenClaw's, from its
        // transcript. A settled answer (see HeadOf) is permanent because the
        // head of a transcript does not change. An unsettled one is read again
        // only once the file has grown, so a scan every two seconds costs a
        // stat per session rather than a parse. A transcript that cannot be
        // read is Native: the session is still OpenClaw's, and Native is the
        // cluster OpenClaw itself gives a session it has no surface for.
        internal static OrbCluster ReadCluster(string? transcriptPath, Func<string, IEnumerable<string>>? readLines = null,
            Func<string, long>? lengthOf = null)
        {
            if (string.IsNullOrEmpty(transcriptPath)) return OrbCluster.Native;

            try
            {
                var length = (lengthOf ?? LengthOf)(transcriptPath);

                lock (Gate)
                {
                    if (Memos.TryGetValue(transcriptPath, out var memo)
                        && (memo.Settled || memo.Length == length))
                        return memo.Cluster ?? OrbCluster.Native;
                }

                var (cluster, settled) = HeadOf((readLines ?? File.ReadLines)(transcriptPath));
                lock (Gate) Memos[transcriptPath] = new Memo(length, cluster, settled);
                return cluster ?? OrbCluster.Native;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OrbCluster.Native;
            }
        }

        private static long LengthOf(string path) => new FileInfo(path).Length;

        internal static void ForgetForTests()
        {
            lock (Gate) Memos.Clear();
        }

        // The viewer's own answer for a status on this machine: the gateway it
        // is connected to, and its own three settings. The scan calls this for
        // every session it is about to draw, and an ordinary session leaves at
        // the first line of ClusterOf without touching the disk.
        internal static bool ShowHere(SessionStatus status)
        {
            var workspaces = OpenClawSessions.OwnerIndexSnapshot().AgentWorkspaces.Values.ToList();
            var cluster = ClusterOf(status, workspaces);
            if (cluster is null) return true;

            return Show(cluster,
                RepresentedByGateway(status.Cwd, OrbweaverSettings.OpenClawEnabled, workspaces),
                OrbweaverSettings.OpenClawHeartbeatMode, OrbweaverSettings.OpenClawCronMode,
                OrbweaverSettings.OpenClawNativeMode);
        }

        // What a status says, for a caller holding one: null when OpenClaw did
        // not spawn it, its cluster when it did.
        internal static OrbCluster? ClusterOf(SessionStatus status, IEnumerable<string>? knownWorkspaces = null) =>
            status.Source == SessionSource.ClaudeCode && Spawned(status.Entrypoint, status.Cwd, knownWorkspaces)
                ? ReadCluster(status.TranscriptPath)
                : null;
    }
}
