using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Orbweaver;

// CB-323: frames-v7-vectors.json, with the desktop's part written by the
// shipped desktop code.
//
// The architect authored the file with Orbweaver-Mobile contracts/
// make_v7_vectors.py, and check_v7_vectors.py checks it independently. The
// relay's half — the timeline's pushes, replies, probes and decisions, the
// route, the counts on the alert — is the relay's to reproduce (relay/tests)
// and is carried here unchanged. The desktop's half comes out of the code the
// app runs: every activity frame a desktop sends (the accepted frames and
// every snapshot in the timeline) is rebuilt by RelayActivity.Snapshot over
// rows that put that frame's sessions in the book and its prompt in the
// pipeline, then RelayActivity.Frame; the cut table by PromptFrames.Cut; the
// desktop's limits from RelayActivity's constants; and the content-state
// example's canonical form, change_key and hash by CanonicalJson. The refused
// frames are inputs — frames the desktop must never produce, which
// RelayV7Tests checks it cannot. Written in json.dumps(indent=1,
// ensure_ascii=False) layout, so a regeneration is byte-equal to the committed
// file everywhere but `_source`, the comparison V7VectorsReproduceTests makes.
internal static class V7Vectors
{
    internal const string GeneratedBy =
        "Uplift-Foundation/Orbweaver tools/relay-vectors (V7Vectors.cs: RelayActivity.Snapshot, RelayActivity.Frame, PromptFrames.Cut, CanonicalJson); the relay's timeline, route and counts carried from Orbweaver-Mobile contracts/make_v7_vectors.py for relay/tests to reproduce";

    // committed: a frames-v7-vectors.json whose inputs, relay half and prose are reused.
    internal static string Build(string committed, string sha, string? generatedBy = null)
    {
        var v = JsonNode.Parse(File.ReadAllText(committed))!.AsObject();
        var source = v["_source"]!;
        source["generated_by"] = generatedBy ?? GeneratedBy;
        source["orbweaver_sha"] = sha;

        var limits = v["limits"]!;
        limits["activity_min_interval_s"] = (int)RelayActivity.MinInterval.TotalSeconds;
        limits["activity_counts_max"] = RelayActivity.CountsMax;
        limits["max_title_utf16"] = RelayActivity.MaxTitle;
        limits["max_tool_utf16"] = RelayActivity.MaxTool;
        limits["max_machine_utf16"] = RelayActivity.MaxMachine;
        limits["max_command_utf16"] = RelayActivity.MaxCommand;
        limits["max_session_id_utf16"] = RelayActivity.MaxSessionId;
        limits["boot_hex"] = RelayActivity.NewBoot().Length;

        foreach (var c in v["cut_rule"]!.AsArray())
            c!["out"] = PromptFrames.Cut(Text(c["in"]), c["limit"]!.GetValue<int>());

        var accepted = v["snapshot_grammar"]!["accepted"]!.AsArray();
        for (var i = 0; i < accepted.Count; i++) accepted[i] = Desktop(accepted[i]!);

        foreach (var e in v["timeline"]!["events"]!.AsArray())
            if (Text(e!["kind"]) == "snapshot") e["frame"] = Desktop(e["frame"]!);

        var example = v["content_state_example"]!;
        var state = example["content_state"]!.AsObject();
        example["canonical"] = Canonical(state);
        var key = ChangeKey(state);
        example["change_key"] = key;
        example["hash"] = Hash(key);

        return PyJson.Pretty(v, indent: 1) + "\n";
    }

    // The frame this desktop sends for the sessions and prompt a vector frame
    // describes: its oldest amber waiting since `since` under its title, the
    // rest of its ambers waiting later, its working and idle sessions, and the
    // oldest's prompt minted when the frame names one. Through the app's own
    // snapshot and frame writer, so the bytes, the key order, null, the cuts
    // and the clamp are the app's.
    internal static JsonNode Desktop(JsonNode frame)
    {
        var rows = new List<RelaySession>();
        ActivityPrompt? prompt = null;
        var oldest = frame["oldest"];
        var amber = frame["amber"]!.GetValue<int>();
        var since = DateTimeOffset.FromUnixTimeSeconds(oldest?["since"]!.GetValue<long>() ?? 0);
        if (oldest is not null)
        {
            rows.Add(Row(Text(oldest["session_id"]), RelayRoster.Waiting, since, Text(oldest["title"])));
            var minted = new ActivityPrompt(Text(oldest["request_id"]), Text(oldest["tool"]), Text(oldest["command"]));
            if (minted != new ActivityPrompt("", "", "")) prompt = minted;
        }
        for (var i = rows.Count; i < amber; i++) rows.Add(Row($"zz-amber-{i:D5}", RelayRoster.Waiting, since.AddSeconds(1), "x"));
        for (var i = 0; i < frame["working"]!.GetValue<int>(); i++) rows.Add(Row($"zz-working-{i:D5}", RelayRoster.Generating, since, "x"));
        for (var i = 0; i < frame["idle"]!.GetValue<int>(); i++) rows.Add(Row($"zz-idle-{i:D5}", RelayRoster.Idle, since, "x"));

        var oldestId = oldest is null ? null : Text(oldest["session_id"]);
        var snapshot = RelayActivity.Snapshot(rows, id => id == oldestId ? prompt : null, Text(frame["machine"]));
        return JsonNode.Parse(RelayActivity.Frame(Text(frame["ref"]), Text(frame["boot"]), frame["seq"]!.GetValue<long>(), snapshot))!;
    }

    private static RelaySession Row(string id, string state, DateTimeOffset since, string title) =>
        new RelaySession(id, "claude", title, "/c", state, null, null) { Since = since };

    // frames-v7 aggregate.content_state, canonical: keys in the contract's
    // order (the vector's own order, which is that order), oldest an object or
    // null. change_key: the same without as_of. hash: 16 hex of SHA-256 over it.
    internal static string Canonical(JsonObject state)
    {
        var j = new CanonicalJson();
        foreach (var (k, val) in state)
        {
            if (val is null) j.Raw(k, "null");
            else if (val is JsonObject o) j.Raw(k, Canonical(o));
            else if (val.GetValueKind() == System.Text.Json.JsonValueKind.String) j.Str(k, val.GetValue<string>());
            else j.Num(k, val.GetValue<long>());
        }
        return j.ToString();
    }

    internal static string ChangeKey(JsonObject state)
    {
        var copy = state.DeepClone().AsObject();
        copy.Remove("as_of");
        return Canonical(copy);
    }

    internal static string Hash(string changeKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changeKey)))[..16].ToLowerInvariant();

    private static string Text(JsonNode? node) => node!.GetValue<string>();
}
