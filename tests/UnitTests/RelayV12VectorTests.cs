using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-340: the desktop's half of frames-v12 (account identity on the usage frame), pinned against
/// the committed frames-v12-vectors.json, whose independent check is Orbweaver-Mobile's
/// check_v12_vectors.py. The derivation, source and grammar tables run through the shipped
/// AccountIdentity and RelayUsage; the shape table through Account.Shape; every sealed usage box
/// and every limit e is written again by the desktop, byte for byte. The phone's tables (merge,
/// limit_dedupe, limit_label) are the phones' to prove and are not read.
/// </summary>
public class RelayV12VectorTests
{
    internal static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v12-vectors.json"))).RootElement;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static string? OptStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static readonly JsonElement In = V.GetProperty("inputs");
    private static readonly byte[] Key = Convert.FromHexString(Str(In, "session_key_epoch0"));

    // One usage account as decoded from a plaintext's JSON, the way the desktop would have held it.
    internal static RelayUsage.Account Account(JsonElement a) => new(
        Str(a, "name"), Str(a, "cli"), Str(a, "plan"),
        a.TryGetProperty("window_5h_pct", out var p) ? p.GetInt32() : null,
        a.TryGetProperty("resets_at", out var r) ? r.GetInt64() : null,
        a.TryGetProperty("weekly_pct", out var w) ? w.GetInt32() : null,
        a.TryGetProperty("weekly_resets_at", out var wr) ? wr.GetInt64() : null,
        a.GetProperty("as_of").GetInt64(), true,
        OptStr(a, "account_id"), OptStr(a, "display_name"), OptStr(a, "initials"), OptStr(a, "icon_hash"),
        a.TryGetProperty("override_updated_at", out var o) ? o.GetInt64() : null);

    private static List<RelayUsage.Account> Accounts(string plaintext) =>
        JsonDocument.Parse(plaintext).RootElement.GetProperty("accounts").EnumerateArray().Select(Account).ToList();

    private static void AssertBox(JsonElement box, string plaintext)
    {
        Assert.Equal(Str(box, "plaintext"), plaintext);
        Assert.Equal(box.GetProperty("plaintext_bytes").GetInt32(), Encoding.UTF8.GetByteCount(plaintext));
        var aad = Convert.FromHexString(Str(box, "aad"));
        Assert.Equal(Aad(Str(box, "from"), Str(box, "to"), Str(box, "class")), aad);
        var opened = Open(Key, Str(box, "ct"), aad, DesktopToPhone);
        Assert.NotNull(opened);
        Assert.Equal(plaintext, Encoding.UTF8.GetString(opened));
        Assert.Equal(Str(box, "ct"), Seal(Key, Convert.FromHexString(Str(box, "nonce")), Encoding.UTF8.GetBytes(plaintext), aad));
        Assert.Equal(Str(In, "ck_usage"), Str(box, "ck"));
    }

    // ---- account_id -----------------------------------------------------------------

    public static TheoryData<string> DerivationRows
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var r in V.GetProperty("derivation").EnumerateArray()) data.Add(Str(r, "name"));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DerivationRows))]
    public void Derive_gives_each_derivation_row_its_account_id(string name)
    {
        var r = V.GetProperty("derivation").EnumerateArray().First(x => Str(x, "name") == name);
        Assert.Equal(OptStr(r, "account_id"), AccountIdentity.Derive(Str(r, "cli"), OptStr(r, "id1"), OptStr(r, "id2")));
    }

    [Fact]
    public void The_named_provider_ids_give_the_named_account_ids()
    {
        var ids = In.GetProperty("provider_ids");
        foreach (var account in In.GetProperty("account_ids").EnumerateObject())
        {
            var pair = ids.GetProperty(account.Name);
            var cli = account.Name.Split('_')[0];
            Assert.Equal(account.Value.GetString(), AccountIdentity.Derive(cli, pair[0].GetString(), pair[1].GetString()));
        }
        Assert.Equal("orbweaver/account-id/v1", Str(In, "hmac_key"));
    }

    public static TheoryData<string> SourceRows
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var r in V.GetProperty("sources").EnumerateArray()) data.Add(Str(r, "name") + "|" + Str(r, "cli"));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SourceRows))]
    public void Each_reader_gives_each_source_file_its_account_id(string row)
    {
        var parts = row.Split('|');
        var r = V.GetProperty("sources").EnumerateArray().First(x => Str(x, "name") == parts[0] && Str(x, "cli") == parts[1]);
        var json = r.GetProperty("file").GetRawText();
        var read = parts[1] switch
        {
            "claude" => AccountIdentity.Claude(json),
            "codex" => AccountIdentity.Codex(json),
            _ => AccountIdentity.Grok(json),
        };
        Assert.Equal(OptStr(r, "account_id"), read);
    }

    // ---- the account's new keys ------------------------------------------------------

    public static TheoryData<int> GrammarRows
    {
        get
        {
            var data = new TheoryData<int>();
            for (var i = 0; i < V.GetProperty("grammar").GetArrayLength(); i++) data.Add(i);
            return data;
        }
    }

    // The grammar table is what a phone accepts; the desktop's half is that it never sends what the
    // phone would refuse. A value the grammar marks unused is either left off or, for a name or
    // initials over the cap, cut by the shared rule until it is used.
    [Theory]
    [MemberData(nameof(GrammarRows))]
    public void The_sender_emits_only_what_the_grammar_accepts(int index)
    {
        var row = V.GetProperty("grammar").EnumerateArray().ElementAt(index);
        var key = Str(row, "key");
        var value = row.GetProperty("value");
        // Values of the wrong JSON type cannot be held by the desktop at all.
        if (key is "account_id" or "display_name" or "initials" or "icon_hash" && value.ValueKind != JsonValueKind.String) return;
        if (key == "override_updated_at" && value.ValueKind != JsonValueKind.Number) return;
        if (key == "override_updated_at" && !value.TryGetInt64(out _)) return;

        var a = new RelayUsage.Account("n", "claude", "", null, null, null, null, 1, true,
            key == "account_id" ? value.GetString() : null,
            key == "display_name" ? value.GetString() : null,
            key == "initials" ? value.GetString() : null,
            key == "icon_hash" ? value.GetString() : null,
            key == "override_updated_at" ? value.GetInt64() : null);
        var sent = JsonDocument.Parse(RelayUsage.AccountJson(a)).RootElement;

        if (row.GetProperty("used").GetBoolean())
        {
            Assert.True(sent.TryGetProperty(key, out var got));
            Assert.Equal(value.GetRawText(), got.GetRawText());
        }
        else if (sent.TryGetProperty(key, out var got))
        {
            // Over the cap: sent, but cut to within it and ending in the cut mark.
            Assert.True(key is "display_name" or "initials");
            Assert.True(got.GetString()!.Length <= (key == "display_name" ? 64 : 16));
            Assert.EndsWith("…", got.GetString());
        }
    }

    [Fact]
    public void The_limits_table_is_the_constants_the_desktop_uses()
    {
        var l = V.GetProperty("limits");
        Assert.Equal(l.GetProperty("account_id_hex").GetInt32(), AccountIdentity.HexLength);
        Assert.Equal(l.GetProperty("max_display_name_utf16").GetInt32(), RelayUsage.MaxDisplayName);
        Assert.Equal(l.GetProperty("max_initials_utf16").GetInt32(), RelayUsage.MaxInitials);
        Assert.Equal(l.GetProperty("account_id_hex").GetInt32(), Str(In.GetProperty("account_ids"), "claude_main").Length);
    }

    public static TheoryData<string> ShapeRows
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var r in V.GetProperty("shape").EnumerateArray()) data.Add(Str(r, "name"));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ShapeRows))]
    public void Shape_resends_exactly_where_the_vectors_say(string name)
    {
        var row = V.GetProperty("shape").EnumerateArray().First(x => Str(x, "name") == name);
        var prev = Account(row.GetProperty("prev"));
        var next = Account(row.GetProperty("new"));
        Assert.Equal(row.GetProperty("sends").GetBoolean(), !prev.Shape.Equals(next.Shape));

        // The same through the tracker that decides, so Shape is not only equal but acted on.
        var tracker = new RelayUsageTracker();
        Assert.Single(tracker.Next([prev]));
        Assert.Equal(row.GetProperty("sends").GetBoolean(), tracker.Next([next]).Count > 0);
    }

    // ---- sealed frames ---------------------------------------------------------------

    [Theory]
    [InlineData("usage_full")]
    [InlineData("usage_absent")]
    [InlineData("limit_frame")]
    public void Usage_boxes_are_byte_for_byte(string name)
    {
        var box = V.GetProperty("sealed").GetProperty(name);
        var accounts = Accounts(Str(box, "plaintext"));
        AssertBox(box, RelayUsage.Frame(accounts));
    }

    [Fact]
    public void A_usage_account_with_nothing_new_is_the_v4_account_byte_for_byte()
    {
        var v4 = RelayV4VectorTests.V.GetProperty("usage").GetProperty("frame").GetProperty("plaintext").GetString()!;
        var absent = Str(V.GetProperty("sealed").GetProperty("usage_absent"), "plaintext");
        Assert.Equal(v4, absent);
        Assert.Equal(v4, RelayUsage.Frame(Accounts(absent)));
    }

    private static PushPersona? Persona(JsonElement e)
    {
        if (!e.TryGetProperty("persona", out var p)) return null;
        return PushPersona.From(Str(p, "id"), Str(p, "name"), OptStr(p, "image_hash"));
    }

    private static readonly KeyPair PhoneKa = new(
        Convert.FromHexString(Str(In.GetProperty("phone_agreement"), "private")),
        Convert.FromHexString(Str(In.GetProperty("phone_agreement"), "public")));

    [Theory]
    [InlineData("full", true, true)]
    [InlineData("account_id_only", true, false)]
    [InlineData("no_new_keys", false, false)]
    [InlineData("persona_only", false, false)]
    public void Limit_e_is_byte_for_byte(string name, bool accountId, bool displayName)
    {
        var box = V.GetProperty("limit_e").GetProperty(name);
        var plain = JsonDocument.Parse(Str(box, "plaintext")).RootElement;
        var a = new RelayUsage.Account(Str(plain, "name"), Str(plain, "cli"), "",
            plain.GetProperty("window_5h_pct").GetInt32(), plain.GetProperty("resets_at").GetInt64(), null, null, 0, true,
            OptStr(plain, "account_id"), OptStr(plain, "display_name"));
        var ours = RelayUsage.LimitE(a, Persona(plain));
        Assert.Equal(Str(box, "plaintext"), ours);
        Assert.Equal(accountId, plain.TryGetProperty("account_id", out _));
        Assert.Equal(displayName, plain.TryGetProperty("display_name", out _));

        if (box.TryGetProperty("e", out var e))
        {
            Assert.Equal(ours, Encoding.UTF8.GetString(PushSeal.Open(PhoneKa, e.GetString())!));
            var eph = new KeyPair(Convert.FromHexString(Str(box.GetProperty("eph"), "private")),
                Convert.FromHexString(Str(box.GetProperty("eph"), "public")));
            Assert.Equal(e.GetString(), PushSeal.SealWith(eph, Convert.FromHexString(Str(box, "nonce")), PhoneKa.Public, Encoding.UTF8.GetBytes(ours)));
        }
    }

    [Fact]
    public void The_no_new_keys_and_persona_only_limit_e_equal_the_v4_and_v6_ones()
    {
        Assert.Equal(
            Str(RelayV4VectorTests.V.GetProperty("usage").GetProperty("limit_e"), "plaintext"),
            Str(V.GetProperty("limit_e").GetProperty("no_new_keys"), "plaintext"));
        Assert.Equal(
            Str(RelayV6VectorTests.V.GetProperty("limit").GetProperty("e"), "plaintext"),
            Str(V.GetProperty("limit_e").GetProperty("persona_only"), "plaintext"));
    }

    [Fact]
    public void An_icon_hash_is_a_frames_v5_picture_hash()
    {
        var icon = V.GetProperty("icon_fetch");
        var v5 = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v5-vectors.json"))).RootElement;
        var still = v5.GetProperty("pictures").EnumerateArray().First(p => Str(p, "name") == Str(icon, "v5_picture"));
        Assert.Equal(Str(icon, "icon_hash"), Str(still, "image_hash"));
        Assert.Equal(Str(icon, "icon_hash"), Str(In.GetProperty("icons"), "still"));
        Assert.True(PersonaFrames.IsHash(Str(icon, "icon_hash")));
    }
}
