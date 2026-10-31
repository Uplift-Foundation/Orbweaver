using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // CB-277: what the gateway has said about who owns which folder, and which
    // sessions exist — the two facts needed to pick the session a file should be
    // asked for under. Both come from requests the app already makes
    // (agents.list carries each agent's `workspace`; sessions.list is the
    // snapshot), so nothing here is derived by guessing.
    internal readonly record struct OwnerIndex(
        IReadOnlyDictionary<string, string> AgentWorkspaces,
        IReadOnlyCollection<string> SessionKeys)
    {
        // Every rule below is a no-op against this, which is what a fixture
        // that predates CB-277 effectively passes.
        internal static OwnerIndex Empty { get; } = new(
            new Dictionary<string, string>(), Array.Empty<string>());
    }

    // CB-277: the session a clip should be asked for under.
    //
    // The gateway's assistant-media route judges a file against the roots of
    // the *named session's* agent, and lets a file outside those roots through
    // only if it is an image (the "Not an image" refusal CB-272 explains). A
    // clip an agent made in its own workspace and a different agent delivered
    // to the user is therefore refused under the user's session with that
    // second agent and served, with full playback meta, under the session of the
    // agent whose workspace holds it. Measured against a real clip, with a paired
    // control that a *third* agent's key is refused (see
    // docs/openclaw-findings.md). CB-274 declined to try another agent's key
    // because it could only have been a guess; agents.list publishes each
    // workspace, so the owner is read here rather than guessed.
    //
    // Pure — no window, no settings, no I/O — so the exact key chosen for a
    // given path is one call away from a test.
    internal static class OpenClawMediaOwner
    {
        // The key to build the route with. Returns askingKey itself — the same
        // string, so a route that works today is byte-identical — whenever the
        // path is not under another agent's workspace, or no session of that
        // agent is known. It never returns a key that is not in the snapshot:
        // that is the line between reading an identity and guessing one.
        internal static string SessionKeyFor(string path, string askingKey, OwnerIndex index)
        {
            var owner = OwningAgent(path, index.AgentWorkspaces);
            if (owner is null) return askingKey;

            var asker = OpenClawSessions.AgentIdOf(askingKey);
            if (string.Equals(owner, asker, StringComparison.Ordinal)) return askingKey;

            return SiblingKeyFor(owner, askingKey, index.SessionKeys) ?? askingKey;
        }

        // The agent whose workspace is the longest directory-prefix of path,
        // or null when none matches or two agents tie for it.
        // Absolute POSIX paths only: a "~/…" path is the shared media folder,
        // which every agent's default roots already include, so it has no owner.
        internal static string? OwningAgent(string path, IReadOnlyDictionary<string, string> workspaces)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/') return null;

            string? best = null;
            var bestLength = -1;
            var tied = false;

            foreach (var (agent, workspace) in workspaces)
            {
                var dir = workspace.TrimEnd('/');

                // An empty dir is "/" (or ""): a workspace of the filesystem
                // root would claim everything, which is never the intent.
                if (dir.Length == 0) continue;

                // On a "/" boundary, or /x/workspace-a would claim
                // /x/workspace-ab/f.mp4.
                if (!path.StartsWith(dir + "/", StringComparison.Ordinal)) continue;

                if (dir.Length > bestLength)
                {
                    best = agent;
                    bestLength = dir.Length;
                    tied = false;
                }
                else if (dir.Length == bestLength)
                {
                    // Two agents whose longest-matching workspace is the same
                    // folder (an identical workspace) have no single owner.
                    // Answering the first one would make the result depend on
                    // the order agents.list happened to arrive in, and could
                    // re-key a file the asker itself owns. No owner means the
                    // asking key stays, which is what works today.
                    tied = true;
                }
            }

            return tied ? null : best;
        }

        // The owner's session in the same conversation if there is one
        // (agent:<asker>:<rest> -> agent:<owner>:<rest>), else the owner's main
        // session, else null. Only keys present in the snapshot are ever returned.
        internal static string? SiblingKeyFor(
            string ownerAgent, string askingKey, IReadOnlyCollection<string> keys)
        {
            var asker = OpenClawSessions.AgentIdOf(askingKey);
            if (asker is not null)
            {
                var head = "agent:" + asker + ":";
                if (askingKey.StartsWith(head, StringComparison.Ordinal))
                {
                    var same = "agent:" + ownerAgent + ":" + askingKey[head.Length..];
                    if (Contains(keys, same)) return same;
                }
            }

            var main = "agent:" + ownerAgent + ":main";
            return Contains(keys, main) ? main : null;
        }

        // The other agents' sessions in the same conversation as askingKey:
        // agent:<asker>:<rest> -> every agent:<other>:<rest> in the snapshot,
        // never askingKey itself. Snapshot order, so the requests made (and
        // the test that counts them) are deterministic.
        internal static List<string> SiblingConversationKeys(string askingKey, IEnumerable<string> keys)
        {
            var result = new List<string>();
            var asker = OpenClawSessions.AgentIdOf(askingKey);
            if (asker is null) return result;

            var head = "agent:" + asker + ":";
            if (!askingKey.StartsWith(head, StringComparison.Ordinal)) return result;
            var rest = askingKey[head.Length..];

            foreach (var key in keys)
            {
                if (string.Equals(key, askingKey, StringComparison.Ordinal)) continue;

                var other = OpenClawSessions.AgentIdOf(key);
                if (other is null || other == asker) continue;
                if (!key.EndsWith(":" + rest, StringComparison.Ordinal)) continue;
                if (!string.Equals(key, "agent:" + other + ":" + rest, StringComparison.Ordinal)) continue;

                result.Add(key);
            }

            return result;
        }

        // What several siblings said about one basename: the path they agree
        // on, or null when none named it or two named different directories.
        // A basename seen under two directories is refused, as the page
        // harvest already refuses it, because picking one would be a guess.
        internal static string? AgreedPath(IEnumerable<string?> answers)
        {
            string? agreed = null;
            foreach (var answer in answers)
            {
                if (answer is null) continue;
                if (agreed is null) agreed = answer;
                else if (!string.Equals(agreed, answer, StringComparison.Ordinal)) return null;
            }

            return agreed;
        }

        private static bool Contains(IReadOnlyCollection<string> keys, string key)
        {
            foreach (var k in keys)
                if (string.Equals(k, key, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
