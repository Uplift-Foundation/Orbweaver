using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // CB-434 (frames-v17): a phone listing this desk's past sessions, and the
    // `sessions` frames that answer it. Sealed and unsigned, like list_dir: it
    // reads and changes nothing. Resume itself is the v4 `launch` carrying
    // resume_session_id, signed (RelayLaunch).

    // Query is null when the shape was wrong: the contract orders the rate
    // limit first and the shape second, as list_dir does, so the handler and
    // not the parser answers bad_frame.
    internal sealed record ListSessionsRequest(string Ref, PastSessionQuery? Query) : RelayRequest(Ref);

    internal static class SessionFrames
    {
        // The capability name a desk that answers list_sessions and a resume
        // launch puts in its caps frame (one name for both, amendment 1).
        internal const string Cap = "resume";

        // {t, cli, account?, older?, q?, folder?, from?}. Each optional key, when
        // present, must be its type: account a string (absent is the desk's
        // designated account, as a launch's is), older a boolean, q a string of
        // at most 256 characters, folder a path list_dir would accept, from a
        // non-negative integer. A page is always 50 rows to a phone.
        internal static ListSessionsRequest Parse(JsonElement f, string reference)
        {
            static ListSessionsRequest Bad(string r) => new(r, null);

            if (Str(f, "cli") is not { Length: > 0 } cli) return Bad(reference);

            var account = "";
            if (f.TryGetProperty("account", out var a))
            {
                if (a.ValueKind != JsonValueKind.String) return Bad(reference);
                account = a.GetString()!;
            }

            var older = false;
            if (f.TryGetProperty("older", out var o))
            {
                if (o.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Bad(reference);
                older = o.ValueKind == JsonValueKind.True;
            }

            var text = "";
            if (f.TryGetProperty("q", out var q))
            {
                if (q.ValueKind != JsonValueKind.String) return Bad(reference);
                text = q.GetString()!;
                if (text.Length > PastSessionsRemote.MaxText || text.Any(char.IsControl)) return Bad(reference);
            }

            var folder = "";
            if (f.TryGetProperty("folder", out var d))
            {
                if (d.ValueKind != JsonValueKind.String) return Bad(reference);
                folder = d.GetString()!;
                if (!BrowseRules.PathShapeOk(folder)) return Bad(reference);
            }

            var from = 0;
            if (f.TryGetProperty("from", out var n))
            {
                if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out from) || from < 0) return Bad(reference);
            }

            return new(reference, new PastSessionQuery(cli, account, older, text, folder, from));
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // One row: id, cwd, title, at, n, missing, live, flags only when true.
        internal static string Entry(PeerPastSession r)
        {
            var j = new CanonicalJson().Str("id", r.Id);
            if (r.Cwd is not null) j.Str("cwd", r.Cwd);
            j.Str("title", r.Title).Num("at", r.At);
            if (r.Count is { } count) j.Num("n", count);
            return j.Flag("missing", r.Missing).Flag("live", r.Live).ToString();
        }

        private static string Frame(string reference, string cli, PeerPastSessionsResult page, int entriesTotal, int fromEntry,
            IEnumerable<string> entries, IReadOnlyList<string>? folders)
        {
            var j = new CanonicalJson().Str("t", "sessions").Str("ref", reference).Str("cli", cli)
                .Num("total", page.Total).Num("from", page.From)
                .Num("entries_total", entriesTotal).Num("from_entry", fromEntry)
                .Array("entries", entries);
            if (folders is { Count: > 0 }) j.Array("folders", folders.Select(CanonicalJson.Quote));
            return j.ToString();
        }

        // The folder filter's choices, newest first, as many as fit in half a
        // frame: 64 folders of 4,096 bytes would not fit one at all, and the
        // rows must still have room. Real folders are a few dozen bytes.
        internal const int FolderBudgetBytes = BrowseLimits.MaxDirPlaintextBytes / 2;

        private static List<string> FoldersThatFit(IReadOnlyList<string> folders)
        {
            var kept = new List<string>();
            var size = 0;
            foreach (var quoted in folders.Select(CanonicalJson.Quote))
            {
                size += Encoding.UTF8.GetByteCount(quoted) + 1;
                if (size > FolderBudgetBytes) break;
                kept.Add(JsonSerializer.Deserialize<string>(quoted)!);
            }
            return kept;
        }

        // The page's rows split greedily over one or more frames with the same
        // ref, each canonical frame at most 40,000 bytes (dir's arithmetic).
        // total and from are the same on every frame; folders rides the first
        // frame of a from = 0 page only. An empty page is one frame, no rows.
        internal static List<string> Sessions(string reference, string cli, PeerPastSessionsResult page)
        {
            var entries = page.Rows.Select(Entry).ToList();
            var kept = FoldersThatFit(page.Folders);
            var frames = new List<string>();
            var from = 0;
            do
            {
                var folders = from == 0 && page.From == 0 ? kept : null;
                var count = 0;
                var size = Encoding.UTF8.GetByteCount(Frame(reference, cli, page, entries.Count, from, [], folders));
                while (from + count < entries.Count)
                {
                    var add = Encoding.UTF8.GetByteCount(entries[from + count]) + (count > 0 ? 1 : 0);
                    if (count > 0 && size + add > BrowseLimits.MaxDirPlaintextBytes) break;
                    size += add;
                    count++;
                }
                frames.Add(Frame(reference, cli, page, entries.Count, from, entries.Skip(from).Take(count), folders));
                from += count;
            } while (from < entries.Count);
            return frames;
        }
    }
}
