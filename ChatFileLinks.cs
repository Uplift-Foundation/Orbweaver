using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // What a click on a file link does (CB-363): fetch if the file lives on a
    // peer, ask the policy what to do with the real file, then hand the answer
    // to the launcher. Everything the panel shows about it goes through the
    // two callbacks, so this class has no window and no dispatcher — each
    // decision is a unit case.
    //
    // The policy and the launcher are injected as delegates (defaulting to the
    // real ones) so the orchestration can be driven without touching a disk or
    // starting a process. ChatLinks.OpenFile is the production seam both UI
    // suites no-op; this class never starts a process itself.
    internal sealed class ChatFileLinks
    {
        // What the policy was told about a file. Local sessions read the real
        // file; a peer's cached copy is real too, but its executable bit
        // comes from the serving side, because a cache entry is written
        // without one.
        //
        // Reason is the policy's own sentence ("Executable files are shown,
        // not run."), shown as the note for a Refuse and for a Reveal, so the
        // reader is told why a click showed a folder window instead of a
        // document.
        internal readonly record struct Decision(FileLinkAction Action, string? Reason);

        private readonly IRemoteChatFiles _files;
        private readonly Func<string, bool, Decision> _decide;
        private readonly Func<string, FileLinkAction, bool> _open;

        internal ChatFileLinks(
            IRemoteChatFiles files,
            Func<string, bool, Decision>? decide = null,
            Func<string, FileLinkAction, bool>? open = null)
        {
            _files = files;
            _decide = decide ?? DefaultDecide;
            _open = open ?? DefaultOpen;
        }

        // The real decision: the policy, on the real facts of the local path,
        // with the serving side's executable bit folded in — a cache entry is
        // written without one, so the file on disk alone would call a peer's
        // script harmless.
        internal static Decision DefaultDecide(string path, bool executableOnServer)
        {
            var link = FileLink.FromDecodedPath(path);
            if (link is null) return new Decision(FileLinkAction.Refuse, "That is not a path this computer can open.");

            var facts = FileFacts.Probe(path);
            var verdict = FileLinkPolicy.Decide(
                link.Value, facts with { Executable = facts.Executable || executableOnServer }, OperatingSystem.IsWindows());
            return new Decision(verdict.Action, verdict.Reason);
        }

        // Hands the decided path to the matching launcher seam: Open to
        // ChatLinks.OpenFile, Reveal to ChatLinks.RevealFile. Reveal has its
        // own seam because the launcher behind OpenFile decides again from the
        // disk and cannot see the serving side's executable bit, so it would
        // open a peer's script that this class decided to show.
        internal static bool DefaultOpen(string path, FileLinkAction action) =>
            action == FileLinkAction.Reveal ? ChatLinks.RevealFile(path) : ChatLinks.OpenFile(path);

        // Paths with a click already in flight. One FILE request per peer is
        // all the serving side allows, so a second click on a link that is
        // still fetching would only earn "still sending another file".
        private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

        internal async Task ClickAsync(string path, Action<string?> note, CancellationToken ct = default)
        {
            if (!_inFlight.Add(path)) return;

            var machine = _files.FileHostName;
            var host = _files.FileHost;

            // Progress is delivered through the context the click started on,
            // like Progress<T>, but gated: once the final note has been
            // written, a report still queued behind it is dropped rather than
            // landing afterwards as a "Fetching… 99%" line nothing will clear.
            // The gate is read and set on that same context, so the two cannot
            // interleave.
            var closed = false;
            var context = SynchronizationContext.Current;
            void Say(string? text)
            {
                if (!closed) note(text);
            }

            void Final(string? text)
            {
                closed = true;
                note(text);
            }

            var progress = new GatedProgress(p =>
            {
                void Apply() => Say(ChatFileLinkText.Fetching(path, machine, p.Done, p.Total));
                if (context is null) Apply();
                else context.Post(_ => Apply(), null);
            });

            try
            {
                ChatFileResult result;

                if (host == ChatFileHost.Peer)
                {
                    Say(ChatFileLinkText.Fetching(path, machine, 0, 0));
                    result = await _files.FetchFileAsync(path, progress, ct).ConfigureAwait(true);
                }
                else
                {
                    result = await _files.FetchFileAsync(path, null, ct).ConfigureAwait(true);
                }

                if (!result.Ok)
                {
                    Final(ChatFileLinkText.Refusal(result.Refusal ?? "failed", machine, host));
                    return;
                }

                var decision = _decide(result.LocalPath!, result.Executable);
                if (decision.Action == FileLinkAction.Refuse)
                {
                    Final(decision.Reason is { Length: > 0 } why ? why : ChatFileLinkText.Refusal("refused", machine, host));
                    return;
                }

                if (!_open(result.LocalPath!, decision.Action))
                {
                    Final(ChatFileLinkText.Refusal("failed", machine, host));
                    return;
                }

                Final(decision.Action == FileLinkAction.Reveal
                    ? (decision.Reason is { Length: > 0 } shown ? shown : ChatFileLinkText.Revealed(result.LocalPath!))
                    : null);
            }
            catch (OperationCanceledException)
            {
                Final(null);
            }
            catch (Exception)
            {
                Final(ChatFileLinkText.Refusal("failed", machine, host));
            }
            finally
            {
                _inFlight.Remove(path);
            }
        }

        private sealed class GatedProgress : IProgress<(long Done, long Total)>
        {
            private readonly Action<(long Done, long Total)> _report;
            public GatedProgress(Action<(long Done, long Total)> report) => _report = report;
            public void Report((long Done, long Total) value) => _report(value);
        }
    }

    // What the policy decided; FileLinkPolicy and the launcher share it.
    internal enum FileLinkAction { Open, Reveal, Refuse }
}
