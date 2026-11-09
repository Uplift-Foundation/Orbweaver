using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Orbweaver
{
    // The real world behind PhoneService: the scan's sessions, transcript
    // files on disk, the typing gate and the terminal. Excluded from coverage
    // like the other live seams — a test must never type into a terminal or
    // read the developer's transcripts; PhoneService's suite drives fakes.
    [ExcludeFromCodeCoverage]
    internal sealed class PhoneSeams
    {
        private readonly object _gate = new();
        private Dictionary<string, PhoneService.Session> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Length, DateTime Written, List<PhoneFrames.Line> Lines)> _parsed = new();

        // Fed by every scan: the book a watch or an input is resolved against.
        internal void OnScan(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            var next = new Dictionary<string, PhoneService.Session>(StringComparer.Ordinal);
            foreach (var (id, status) in snapshot)
                if (status.IsLocalCli) next[id] = new PhoneService.Session(id, status, RelayRoster.MapState(status.State));
            lock (_gate) _sessions = next;
        }

        internal PhoneService.Session? Find(string id)
        {
            lock (_gate) return _sessions.GetValueOrDefault(id);
        }

        // The whole transcript, parsed by the CLI's own parser into the rows
        // the desktop's chat panel shows. Re-read only when the file's length
        // or time changed, so a 250 ms tick over a quiet file costs one stat.
        internal IReadOnlyList<PhoneFrames.Line>? Load(PhoneService.Session session)
        {
            var path = session.Status.TranscriptPath;
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                lock (_gate)
                {
                    if (_parsed.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc)
                        return hit.Lines;
                }
                var lines = new List<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                    while (reader.ReadLine() is { } line) lines.Add(line);
                var rows = CliChatFormat.For(session.Status.Source).Map(lines).Select(r => PhoneFrames.FromTurn(r.Turn)).ToList();
                lock (_gate)
                {
                    if (_parsed.Count > 16) _parsed.Clear();
                    _parsed[path] = (info.Length, info.LastWriteTimeUtc, rows);
                }
                return rows;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static bool CanType(SessionStatus status) =>
            status.IsLocalCli && !status.IsDesktopHosted && TerminalFocuser.CanSendQuietly(status);

        // TypingGate first (the record, then the screen), with the settle rule
        // from PR #227 generalised to typing: two screen reads 150 ms apart
        // must be identical, and both must read as a prompt, before any key goes
        // in. A screen that moved between the reads is not a prompt to type into.
        internal static async Task<(bool Typed, string? Refusal)> TypeAsync(PhoneService.Session session, string text)
        {
            var status = session.Status;
            var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
                status, text,
                TerminalFocuser.SendTextAndSubmit,
                s => s.SessionPid > 0 ? SessionPark.RecordStatus(s.SessionPid, session.Id) : null,
                SettledCapture,
                TerminalFocuser.CanReadPane,
                shadow: null, log: null, who: "phone").ConfigureAwait(false);
            return (typed, refusal);
        }

        private static async Task<string?> SettledCapture(SessionStatus status)
        {
            var first = await TerminalFocuser.CapturePane(status).ConfigureAwait(false);
            await Task.Delay(150).ConfigureAwait(false);
            var second = await TerminalFocuser.CapturePane(status).ConfigureAwait(false);
            return first is not null && first == second ? second : null;
        }
    }
}
