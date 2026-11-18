using System;
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
        internal readonly record struct Decision(FileLinkAction Action, string? Refusal);

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
            _open = open ?? ((_, _) => false); // ChatLinks.OpenFile once celeste's seam lands
        }

        // The result of one click, so the caller knows whether to clear the
        // note (opened or revealed) or leave a refusal on screen.
        internal static Decision DefaultDecide(string path, bool executableOnServer) =>
            throw new NotImplementedException("wired to FileLinkPolicy once its signature lands");

        // Runs one click. note receives the line to show under the bubble —
        // null clears it. Never throws: a fetch that blows up is a refusal
        // line, because a link click that crashes the panel is worse than one
        // that says it failed.
        internal async Task ClickAsync(string path, Action<string?> note, CancellationToken ct = default)
        {
            var machine = _files.FileHostName;

            try
            {
                ChatFileResult result;

                if (_files.FileHost == ChatFileHost.Peer)
                {
                    note(ChatFileLinkText.Fetching(path, machine, 0, 0));
                    var progress = new Progress<(long Done, long Total)>(p =>
                        note(ChatFileLinkText.Fetching(path, machine, p.Done, p.Total)));
                    result = await _files.FetchFileAsync(path, progress, ct).ConfigureAwait(true);
                }
                else
                {
                    result = await _files.FetchFileAsync(path, null, ct).ConfigureAwait(true);
                }

                if (!result.Ok)
                {
                    note(ChatFileLinkText.Refusal(result.Refusal ?? "failed", machine));
                    return;
                }

                var decision = _decide(result.LocalPath!, result.Executable);
                if (decision.Action == FileLinkAction.Refuse)
                {
                    note(ChatFileLinkText.Refusal(decision.Refusal ?? "refused", machine));
                    return;
                }

                if (!_open(result.LocalPath!, decision.Action))
                {
                    note(ChatFileLinkText.Refusal("failed", machine));
                    return;
                }

                note(decision.Action == FileLinkAction.Reveal
                    ? ChatFileLinkText.Revealed(result.LocalPath!)
                    : null);
            }
            catch (OperationCanceledException)
            {
                note(null);
            }
            catch (Exception)
            {
                note(ChatFileLinkText.Refusal("failed", machine));
            }
        }
    }

    // Placeholder until FileLinkPolicy lands (celeste-ngata, surface A).
    internal enum FileLinkAction { Open, Reveal, Refuse }
}
