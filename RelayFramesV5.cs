using System.Security.Cryptography;
using System.Text;

namespace Orbweaver
{
    // Milestone 5's plaintexts (CB-321, Orbweaver-Mobile contracts/frames-v5.json):
    // the `personas` frame, the `persona_image` request and its windows, and the
    // grammar both apps hold a hash and a persona id to. Pure, so every rule the
    // contract states — the ms clamp, the chunker's maximality, the header on
    // every window — is a unit test and a byte-for-byte vector rather than a
    // reading of prose.
    //
    // Additive over v4: everything here is written through CanonicalJson, the
    // same writer the v4 frames use, so a window's size is measured in exactly
    // the bytes the phone will be handed.
    internal static class PersonaFrames
    {
        // frames-v5.json `limits`, every one of them.
        internal const int MaxWindowBytes = TranscriptWatch.MaxWindowBytes;
        internal const int MaxFrameBytes = 32768;
        internal const int MaxFrames = 120;
        internal const int MinSide = 16;
        internal const int MaxSide = 256;
        internal const int MinFrameMs = 20;
        internal const int MaxFrameMs = 10000;
        internal const long MaxSourceBytes = PersonaFiles.MaxAvatarBytes;
        internal const int MaxImageBytes = 2 * 1024 * 1024;
        internal const int ImageWindowsPerSecond = 10;
        internal const int FetchTimeoutSeconds = 30;

        // The ck prefix a picture's windows are coalesced under, and the state
        // frame's: Ck(ck_key, "img:" + hash) and Ck(ck_key, "personas").
        internal const string PersonasCk = "personas";
        internal static string ImageCk(string hash) => "img:" + hash;

        internal readonly record struct Frame(int Ms, byte[] Data);

        // One window's plaintext and the frames it carries (From..From+Count-1).
        internal sealed record ImageWindow(string Json, int From, int Count);

        internal sealed record Row(string Id, string Name, string Initials, bool Default, string? ImageHash);

        // 64 lowercase hex: SHA-256 of the picture file's bytes.
        internal static bool IsHash(string? value) => IsLowerHex(value, 64);

        // 16 lowercase hex: the desktop's stable id for a persona.
        internal static bool IsPersonaId(string? value) => IsLowerHex(value, 16);

        private static bool IsLowerHex(string? value, int length)
        {
            if (value is null || value.Length != length) return false;
            foreach (var c in value)
                if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
            return true;
        }

        internal static bool SideAllowed(int w, int h) => w is >= MinSide and <= MaxSide && h is >= MinSide and <= MaxSide;

        internal static bool FrameAllowed(int bytes) => bytes is > 0 and <= MaxFrameBytes;

        // The delay a frame is sent with: OpenClawAvatars' rule (a GIF delay of
        // 10 ms or less is the author meaning 100), then the contract's clamp to
        // 20..10000. A still is one frame and always 0.
        internal static int SentMs(int gifMs, int frameCount)
        {
            if (frameCount <= 1) return 0;
            if (gifMs <= 10) return 100;
            return Math.Clamp(gifMs, MinFrameMs, MaxFrameMs);
        }

        // The persona id the desktop sends: the first 16 hex of SHA-256 over the
        // UTF-8 of the file that named the persona's picture, else the first
        // file its answer came from. Stable across restarts and across sessions
        // wearing the same persona; a nearer file shadowing the user-level one
        // is a different path and so a different id. The path itself never
        // leaves the machine. A persona with no file at all (a test's
        // hand-built one) is identified by its name, so `id` is still present
        // whenever `persona` is.
        internal static string PersonaId(string? origin, string name)
        {
            var basis = string.IsNullOrEmpty(origin) ? "name:" + name : origin;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..16].ToLowerInvariant();
        }

        // The id half of MirrorProtocol.AvatarIdOf ("<sha256 hex>:<length>").
        internal static string? HashOf(string? avatarId)
        {
            if (avatarId is null) return null;
            var colon = avatarId.IndexOf(':');
            var hash = colon < 0 ? avatarId : avatarId[..colon];
            return IsHash(hash) ? hash : null;
        }

        // ---- frames ---------------------------------------------------------------

        internal static string Request(string hash) =>
            new CanonicalJson().Str("t", "persona_image").Str("hash", hash).ToString();

        // Rows ordered by id (ordinal), `default` only when true, `image_hash`
        // only when offered.
        internal static string Personas(IEnumerable<Row> rows) =>
            new CanonicalJson().Str("t", "personas")
                .Array("personas", rows.OrderBy(r => r.Id, StringComparer.Ordinal).Select(RowJson))
                .ToString();

        private static string RowJson(Row r)
        {
            var j = new CanonicalJson().Str("id", r.Id).Str("name", r.Name).Str("initials", r.Initials).Flag("default", r.Default);
            if (r.ImageHash is { } hash) j.Str("image_hash", hash);
            return j.ToString();
        }

        private static string FrameJson(Frame f) =>
            new CanonicalJson().Num("ms", f.Ms).Str("data", RelayCrypto.Base64Url(f.Data)).ToString();

        internal static string Window(string hash, int w, int h, int total, int from, IEnumerable<string> frameJsons) =>
            new CanonicalJson().Str("t", "persona_image").Str("hash", hash).Num("w", w).Num("h", h)
                .Num("frames_total", total).Num("from_frame", from).Array("frames", frameJsons).ToString();

        // Greedy, in order, exactly as TranscriptWatch.Chunk: a frame joins the
        // open window while the canonical window stays within 48,000 bytes, else
        // it opens the next one, so every window but the last is maximal and
        // every window carries the full header. A frame is at most 32,768 bytes,
        // whose base64url plus the header always fits an empty window.
        internal static List<ImageWindow> Chunk(string hash, int w, int h, IReadOnlyList<Frame> frames)
        {
            var windows = new List<ImageWindow>();
            var open = new List<string>();
            var start = 0;
            var size = 0;
            foreach (var frame in frames)
            {
                var json = FrameJson(frame);
                var bytes = Encoding.UTF8.GetByteCount(json);
                // The header with an empty frames array is the window's fixed cost.
                var header = Encoding.UTF8.GetByteCount(Window(hash, w, h, frames.Count, start, []));
                var trial = header + size + (open.Count > 0 ? 1 : 0) + bytes;
                if (open.Count > 0 && trial > MaxWindowBytes)
                {
                    windows.Add(new ImageWindow(Window(hash, w, h, frames.Count, start, open), start, open.Count));
                    start += open.Count;
                    open = [json];
                    size = bytes;
                }
                else
                {
                    size += (open.Count > 0 ? 1 : 0) + bytes;
                    open.Add(json);
                }
            }
            windows.Add(new ImageWindow(Window(hash, w, h, frames.Count, start, open), start, open.Count));
            return windows;
        }

        // The log's name for a picture: 12 hex, never the path or the persona.
        internal static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
    }

    // A phone asking for one picture by the hash a roster or personas frame
    // named (frames-v5 persona_image_request).
    internal sealed record PersonaImageRequest(string Ref, string Hash) : RelayRequest(Ref);
}

namespace Orbweaver
{
    // Which persona record a session's roster row and the personas frame carry
    // (CB-321). Pure: the registry persona, the default persona and the picture
    // store are all handed in, so the join — name from the persona cache, id
    // and picture from the registry, the default marked — is a unit test.
    internal static class RelayPersonaRows
    {
        // The file a persona's id is derived from: the markdown that named its
        // picture, else the first file its answer came from.
        internal static string? Origin(LocalPersona.Persona? p) => p is null ? null : p.AvatarSource ?? p.Files.FirstOrDefault();

        // A session's roster persona. The registry's record lends the id and the
        // picture only when it names the same persona the roster does: the name
        // comes from PeerPersonaCache (off-thread, as before CB-321), and a
        // registry entry that disagrees with it is a different persona, whose
        // picture must not be drawn over this one's name.
        internal static RelayPersona? For(string? name, SessionStatus status, LocalPersona.Persona? local, Func<string, string?> offer)
        {
            var match = local is not null && local.Name == name ? local : null;
            var hash = match?.AvatarPath is { } path ? offer(path) : null;
            return RelayRoster.PersonaFrom(name, status, Origin(match), hash);
        }

        // The personas frame's rows: every persona a live session wears, once,
        // plus the default persona (the user config dirs' own), marked. A
        // default with no name is not a persona and adds nothing.
        internal static IReadOnlyList<PersonaFrames.Row> Rows(
            IEnumerable<RelaySession> sessions, LocalPersona.Persona? defaultPersona, Func<string, string?> offer)
        {
            var byId = new Dictionary<string, PersonaFrames.Row>(StringComparer.Ordinal);
            foreach (var p in sessions.Select(s => s.Persona).OfType<RelayPersona>())
                if (p.Id is { } id && !byId.ContainsKey(id))
                    byId[id] = new PersonaFrames.Row(id, p.Name, p.Initials, false, p.ImageHash);

            if (defaultPersona?.Name is { } name && !string.IsNullOrWhiteSpace(name))
            {
                var id = PersonaFrames.PersonaId(Origin(defaultPersona), name);
                var hash = byId.TryGetValue(id, out var worn) ? worn.ImageHash
                    : defaultPersona.AvatarPath is { } path ? offer(path) : null;
                byId[id] = new PersonaFrames.Row(id, name, OrbGlyph.Initials(name), true, hash);
            }
            return byId.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        }
    }
}
