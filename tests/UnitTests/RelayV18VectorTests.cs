using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-447: the desk's half of frames-v18 (Paperclip on the phone), replayed against the committed
/// frames-v18-vectors.json, whose independent check is Orbweaver-Mobile's check_v18_vectors.py. Every
/// backfill is written by TranscriptWatch over the vectors' rows, every tail by PaperclipWindowDiff, every
/// reply by RelayReplies, every prefs frame read by AlertParse, every persona id by PersonaFrames and every
/// sealed box opened and written again by the desk. The phones' own tables are the phones' to prove and are
/// not read.
/// </summary>
public class RelayV18VectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v18-vectors.json"))).RootElement;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static readonly string Session = Str(V.GetProperty("inputs"), "agent_session_id");

    private static List<TranscriptWatch.WireLine> Lines(JsonElement rows) => rows.EnumerateArray().Select(r =>
        TranscriptWatch.Line(Str(r, "role"), Str(r, "text"), r.GetProperty("at").GetInt64(),
            r.TryGetProperty("error", out var e) && e.GetBoolean())).ToList();

    private static void AssertWindows(JsonElement expected, IReadOnlyList<string> actual)
    {
        var want = expected.EnumerateArray().ToList();
        Assert.Equal(want.Count, actual.Count);
        for (var i = 0; i < want.Count; i++)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(want[i].GetRawText()), JsonNode.Parse(actual[i])), $"window {i}: {actual[i]}");
    }

    // ---- caps ---------------------------------------------------------------------------------

    [Fact]
    public void The_caps_frame_names_paperclip_chat_only_while_paperclip_is_connected_in_byte_order()
    {
        var caps = V.GetProperty("caps");
        foreach (var c in caps.GetProperty("cases").EnumerateArray())
        {
            var name = Str(c, "name");
            var plaintext = Str(caps.GetProperty("plaintexts"), name);
            var names = c.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray();
            Assert.Equal(names.Order(StringComparer.Ordinal), names);
            Assert.Equal(plaintext, BrowseFrames.CapsFrame(names));
            if (name != "only-paperclip") Assert.Equal(plaintext, BrowseFrames.CapsFor(c.GetProperty("paperclip_connected").GetBoolean()));
        }
        Assert.Equal(BrowseFrames.CapsFor(false), BrowseFrames.Caps);   // the frame an unconnected desk always sent
    }

    // ---- the thread ---------------------------------------------------------------------------

    [Fact]
    public void Every_backfill_is_what_TranscriptWatch_writes_for_the_rows()
    {
        foreach (var b in V.GetProperty("backfills").EnumerateArray())
            AssertWindows(b.GetProperty("windows"), TranscriptWatch.Backfill(Session, Lines(b.GetProperty("rows"))));
    }

    [Fact]
    public void Every_tail_is_what_PaperclipWindowDiff_says_the_thread_owes_the_phone()
    {
        var count = 0;
        foreach (var t in V.GetProperty("tails").EnumerateArray())
        {
            var previous = Lines(t.GetProperty("previous"));
            var now = Lines(t.GetProperty("rows"));
            var change = PaperclipWindowDiff.Diff(previous, "task-1", now, t.GetProperty("task_changed").GetBoolean() ? "task-2" : "task-1");
            AssertWindows(t.GetProperty("windows"), PaperclipWindowDiff.Windows(Session, now, change));
            count++;
        }
        Assert.Equal(11, count);                                  // a resync that adds a case has to be read, not slipped in
    }

    [Fact]
    public void The_watch_input_and_send_failed_replies_are_what_RelayReplies_writes()
    {
        foreach (var o in V.GetProperty("watch").GetProperty("ok").EnumerateArray())
        {
            var j = JsonDocument.Parse(Str(o, "plaintext")).RootElement;
            Assert.Equal(Str(o, "plaintext"), RelayReplies.Ok(Str(j, "ref"), RelayReplies.Watching, Session, j.GetProperty("total").GetInt64()));
        }
        var input = V.GetProperty("input");
        var ok = JsonDocument.Parse(Str(input, "ok")).RootElement;
        Assert.Equal(Str(input, "ok"), RelayReplies.Ok(Str(ok, "ref"), RelayReplies.Typed, Session));
        foreach (var f in input.GetProperty("send_failed").EnumerateArray())
        {
            var j = JsonDocument.Parse(Str(f, "plaintext")).RootElement;
            Assert.Equal(Str(f, "plaintext"), RelayReplies.Err(Str(j, "ref"), RelayReplies.SendFailed, Session, Str(f, "why")));
            Assert.True(Str(f, "why").Length <= RelayReplies.SendFailedWhyLimit);
        }
        Assert.Equal(7, input.GetProperty("send_failed").GetArrayLength());
    }

    // The desk's own six sentences are the vectors' first six, from the code that says them.
    [Fact]
    public void The_desks_six_sentences_are_the_ones_the_vectors_list()
    {
        var sentences = V.GetProperty("input").GetProperty("send_failed").EnumerateArray().Select(f => Str(f, "why")).ToList();
        Assert.Equal(sentences[0], PaperclipChatSession.RefusalFor(new(PaperclipActionResult.NeedsReconnect), "Atlas"));
        Assert.Equal(sentences[1], PaperclipChatSession.RefusalFor(new(PaperclipActionResult.Unreachable), "Atlas"));
        Assert.Equal(sentences[2], PaperclipChatSession.RefusalFor(new(PaperclipActionResult.Refused), "Atlas"));
        Assert.Equal(sentences[3], PaperclipActions.TaskGoneWhy);
        Assert.Equal(sentences[4], PaperclipChatSession.NotConnectedText);
        Assert.Equal(sentences[5], PaperclipActions.NothingToSendWhy);
        Assert.Equal(sentences[6], PaperclipChatSession.RefusalFor(new(PaperclipActionResult.Refused, sentences[6]), "Atlas"));   // Paperclip's own
    }

    // ---- prefs and the persona ----------------------------------------------------------------

    [Fact]
    public void Every_prefs_frame_reads_as_the_vectors_say()
    {
        foreach (var p in V.GetProperty("prefs").EnumerateArray())
        {
            var prefs = AlertPrefs.Parse(JsonDocument.Parse(Str(p, "plaintext")).RootElement)!.Value;
            Assert.Equal(p.GetProperty("paperclip_personas").GetBoolean(), prefs.PaperclipPersonas);
            Assert.Equal(p.GetProperty("paperclip_unreachable").GetBoolean(), prefs.PaperclipUnreachable);
        }
    }

    [Fact]
    public void The_persona_ids_and_the_picture_hash_are_the_vectors()
    {
        var persona = V.GetProperty("persona");
        foreach (var i in persona.GetProperty("ids").EnumerateArray())
            Assert.Equal(Str(i, "id"), PersonaFrames.PaperclipPersonaId(Str(i, "agent_id")));
        foreach (var s in persona.GetProperty("picture_sources").EnumerateArray())
            Assert.Equal(Str(s, "image_hash"), Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Str(s, "source_ascii")))).ToLowerInvariant());
    }

    // ---- the sealed boxes ---------------------------------------------------------------------

    // The windows the desk writes, keyed the way the boxes name them: backfills.<case>[i], tails.<case>[i].
    private static Dictionary<string, string> DeskWindows()
    {
        var map = new Dictionary<string, string>();
        foreach (var b in V.GetProperty("backfills").EnumerateArray())
        {
            var windows = TranscriptWatch.Backfill(Session, Lines(b.GetProperty("rows")));
            for (var i = 0; i < windows.Count; i++) map[$"backfills.{Str(b, "name")}[{i}]"] = windows[i];
        }
        foreach (var t in V.GetProperty("tails").EnumerateArray())
        {
            var now = Lines(t.GetProperty("rows"));
            var change = PaperclipWindowDiff.Diff(Lines(t.GetProperty("previous")), "task-1", now, t.GetProperty("task_changed").GetBoolean() ? "task-2" : "task-1");
            var windows = PaperclipWindowDiff.Windows(Session, now, change);
            for (var i = 0; i < windows.Count; i++) map[$"tails.{Str(t, "name")}[{i}]"] = windows[i];
        }
        return map;
    }

    [Fact]
    public void Every_sealed_box_opens_under_the_epoch_zero_key_and_is_written_again_by_the_desk()
    {
        var sealedBlock = V.GetProperty("sealed");
        var key = Convert.FromHexString(Str(sealedBlock.GetProperty("inputs"), "session_key_epoch0"));
        var boxes = sealedBlock.GetProperty("boxes").EnumerateArray().ToList();
        var windows = DeskWindows();
        var viaDesk = 0;
        foreach (var box in boxes)
        {
            var aad = Convert.FromHexString(Str(box, "aad"));
            Assert.Equal(Aad(Str(box, "from"), Str(box, "to"), Str(box, "class")), aad);
            var direction = Str(box, "from").StartsWith("dsk_", StringComparison.Ordinal) ? DesktopToPhone : PhoneToDesktop;

            // A window box names the window it seals; the plaintext sealed is the one the desk writes.
            string plaintext;
            if (box.TryGetProperty("plaintext_of", out var of)) { plaintext = windows[of.GetString()!]; viaDesk++; }
            else plaintext = Str(box, "plaintext");

            Assert.Equal(plaintext, Encoding.UTF8.GetString(Open(key, Str(box, "ct"), aad, direction)!));
            Assert.Equal(Str(box, "ct"), Seal(key, Convert.FromHexString(Str(box, "nonce")), Encoding.UTF8.GetBytes(plaintext), aad));
        }
        Assert.Equal(29, boxes.Count);
        Assert.Equal(15, viaDesk);
    }

    // A phone that did not ask for personas is sent the paperclip_roster rows of frames-v15, byte for byte.
    [Fact]
    public void Stripping_the_persona_from_the_with_key_roster_gives_the_roster_a_phone_without_the_key_is_sent()
    {
        var f = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "roster-fixtures.json"))).RootElement;
        string? Opt(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
        var with = f.GetProperty("paperclip_roster_personas");
        var sessions = with.GetProperty("sessions").EnumerateArray().Select(s => new RelaySession(
            Str(s, "session_id"), Str(s, "cli"), Str(s, "title"), Str(s, "cwd"), Str(s, "state"), Opt(s, "parent_session_id"),
            s.TryGetProperty("persona", out var p) && s.GetProperty("cli").GetString() == "paperclip"
                ? new RelayPersona(Str(p, "name"), Str(p, "initials"), Opt(p, "color"), Opt(p, "id"), Opt(p, "image_hash")) : null)
        { Since = DateTimeOffset.FromUnixTimeSeconds(s.GetProperty("since").GetInt64()) }).ToList();

        var without = f.GetProperty("paperclip_roster");
        var stripped = RelayRoster.ForPhone(sessions, paperclipPersonas: false);
        Assert.Equal(JsonSerializer.Serialize(without), Encoding.UTF8.GetString(RelayRoster.RosterJson(without.GetProperty("seq").GetInt64(), stripped)));
    }

    // The answer refusal: its frame is frames-v3's shape, and the table is the desk's decision per outcome and per phone.
    [Fact]
    public void The_unreachable_answer_refusal_is_a_v3_frame_and_the_table_is_what_the_desk_decides()
    {
        var r = V.GetProperty("answer_refusal");
        var frame = JsonDocument.Parse(Str(r, "frame")).RootElement;
        Assert.Equal(Str(r, "frame"), PromptFrames.Refusal(Str(frame, "request_id"), Str(frame, "session_id"), PaperclipPrompts.UnreachableCode));
        Assert.Equal("paperclip_unreachable", PaperclipPrompts.UnreachableCode);

        var decided = new Dictionary<string, PaperclipActionResult>
        {
            ["decided"] = PaperclipActionResult.Done,
            ["already_decided"] = PaperclipActionResult.AlreadyDecided,
            ["unreachable"] = PaperclipActionResult.Unreachable,
            ["refusing"] = PaperclipActionResult.Refused,
            ["reconnect"] = PaperclipActionResult.NeedsReconnect,
        };
        var rows = r.GetProperty("table").EnumerateArray().ToList();
        foreach (var row in rows)
        {
            var sent = PaperclipPrompts.RefusalCode(decided[Str(row, "outcome")]);
            var expected = row.GetProperty("code").ValueKind == JsonValueKind.Null ? null : Str(row, "code");
            var wire = sent is null ? null : RelayLink.RefusalFor(sent, row.GetProperty("paperclip_unreachable").GetBoolean());
            Assert.Equal(expected, wire);
        }
        Assert.Equal(10, rows.Count);
    }
}

