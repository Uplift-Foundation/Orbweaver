using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-340: the desktop's frames-v12 projection beyond what the committed vectors pin — each of the
/// five usage keys and each of the two limit_e keys present and absent, the fitting rule that drops
/// them in order, the look an account gets from this PC's override, the override stamp's pure
/// rules, and the second image store that keeps account icons from evicting persona pictures.
/// </summary>
public class RelayUsageV12Tests
{
    private const string Id = "ac7d4a85766f600f0bbb94dfc5321dc3";
    private static readonly string Hash = new('a', 64);

    private static RelayUsage.Account Acct(
        string? id = Id, string? display = null, string? initials = null, string? icon = null, long? stamp = null,
        string name = "user@example.com", int? pct = 81, long? resets = 1794117192) =>
        new(name, "claude", "max", pct, resets, 31, 1794543792, 1794111792, true, id, display, initials, icon, stamp);

    private static JsonElement Sent(RelayUsage.Account a) => JsonDocument.Parse(RelayUsage.AccountJson(a)).RootElement;

    // ---- the usage account -------------------------------------------------------------

    [Fact]
    public void An_account_with_nothing_new_is_the_v4_account_and_the_keys_follow_as_of_in_the_contracts_order()
    {
        var bare = RelayUsage.AccountJson(Acct(id: null));
        Assert.EndsWith("\"as_of\":1794111792}", bare);

        var full = RelayUsage.AccountJson(Acct(display: "Work", initials: "WK", icon: Hash, stamp: 1794100000));
        Assert.EndsWith(
            "\"as_of\":1794111792,\"account_id\":\"" + Id + "\",\"display_name\":\"Work\",\"initials\":\"WK\"," +
            "\"icon_hash\":\"" + Hash + "\",\"override_updated_at\":1794100000}", full);
    }

    [Fact]
    public void Each_new_key_is_present_without_the_others()
    {
        Assert.Equal(["account_id"], Keys(Acct(), after: "as_of"));
        Assert.Equal(["display_name"], Keys(Acct(id: null, display: "Work"), after: "as_of"));
        Assert.Equal(["initials"], Keys(Acct(id: null, initials: "WK"), after: "as_of"));
        Assert.Equal(["icon_hash"], Keys(Acct(id: null, icon: Hash), after: "as_of"));
        Assert.Equal(["override_updated_at"], Keys(Acct(id: null, stamp: 0), after: "as_of"));
    }

    private static string[] Keys(RelayUsage.Account a, string after) =>
        Sent(a).EnumerateObject().Select(p => p.Name).SkipWhile(n => n != after).Skip(1).ToArray();

    [Theory]
    [InlineData("AC7D4A85766F600F0BBB94DFC5321DC3")]    // uppercase
    [InlineData("ac7d4a85766f600f0bbb94dfc5321dc")]     // 31
    [InlineData("ac7d4a85766f600f0bbb94dfc5321dc30")]   // 33
    [InlineData("user@example.com")]
    [InlineData("zc7d4a85766f600f0bbb94dfc5321dc3")]    // not hex
    [InlineData("")]
    public void An_account_id_outside_the_grammar_is_left_off_not_sent_to_be_refused(string id) =>
        Assert.False(Sent(Acct(id: id)).TryGetProperty("account_id", out _));

    [Fact]
    public void A_display_name_is_trimmed_cut_and_dropped_when_blank()
    {
        Assert.Equal("Work", Sent(Acct(display: "  Work ")).GetProperty("display_name").GetString());
        Assert.False(Sent(Acct(display: "  ")).TryGetProperty("display_name", out _));
        Assert.False(Sent(Acct(display: null)).TryGetProperty("display_name", out _));

        var cut = Sent(Acct(display: new string('N', 65))).GetProperty("display_name").GetString()!;
        Assert.Equal(64, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal(new string('N', 64), Sent(Acct(display: new string('N', 64))).GetProperty("display_name").GetString());
    }

    [Fact]
    public void Initials_over_sixteen_units_or_blank_are_omitted_never_cut()
    {
        Assert.Equal("Q✦", Sent(Acct(initials: "Q✦")).GetProperty("initials").GetString());
        Assert.Equal(new string('x', 16), Sent(Acct(initials: new string('x', 16))).GetProperty("initials").GetString());
        Assert.False(Sent(Acct(initials: new string('x', 17))).TryGetProperty("initials", out _));
        Assert.False(Sent(Acct(initials: " ")).TryGetProperty("initials", out _));
        Assert.False(Sent(Acct(initials: null)).TryGetProperty("initials", out _));
    }

    [Theory]
    [InlineData("0650E7361CBF60D869AC6CEEA2E934A4B8CC7070B4075338D4235D413CD045F5")]
    [InlineData("0650e7361cbf60d869ac6ceea2e934a4b8cc7070b4075338d4235d413cd045f")]
    public void An_icon_hash_outside_the_grammar_is_left_off(string hash) =>
        Assert.False(Sent(Acct(icon: hash)).TryGetProperty("icon_hash", out _));

    [Fact]
    public void An_override_stamp_is_sent_for_zero_and_not_for_a_negative_or_missing_one()
    {
        Assert.Equal(0, Sent(Acct(stamp: 0)).GetProperty("override_updated_at").GetInt64());
        Assert.False(Sent(Acct(stamp: -1)).TryGetProperty("override_updated_at", out _));
        Assert.False(Sent(Acct(stamp: null)).TryGetProperty("override_updated_at", out _));
    }

    [Fact]
    public void From_carries_the_readings_account_id_and_the_look_and_nothing_without_one()
    {
        var reading = new AccountUsage(null, "river", true, "Max",
            new UsageWindow(62, DateTimeOffset.FromUnixTimeSeconds(1794117192)), null, null,
            DateTimeOffset.FromUnixTimeSeconds(1794111792), AccountUsageSource.ClaudeCode, null, Id);

        var plain = RelayUsage.From(reading, "river@example.com");
        Assert.Equal((Id, null, null, null, null), (plain.AccountId, plain.DisplayName, plain.Initials, plain.IconHash, plain.OverrideUpdatedAt));

        var looked = RelayUsage.From(reading, "river@example.com", new RelayUsage.Look("Quill", "RQ", Hash, 5));
        Assert.Equal((Id, "Quill", "RQ", Hash, (long?)5), (looked.AccountId, looked.DisplayName, looked.Initials, looked.IconHash, looked.OverrideUpdatedAt));
    }

    [Fact]
    public void The_shape_moves_with_each_new_key_and_not_with_as_of_alone()
    {
        var a = Acct(display: "Work", initials: "WK", icon: Hash, stamp: 1);
        Assert.True(a.Shape.Equals((a with { AsOf = 5 }).Shape));
        Assert.True(a.Shape.Equals((a with { Plan = "pro" }).Shape));    // frames-v12 shape: plan_only does not resend
        foreach (var changed in new[]
                 {
                     a with { AccountId = new string('b', 32) }, a with { DisplayName = "Home" }, a with { Initials = "HM" },
                     a with { IconHash = new string('c', 64) }, a with { OverrideUpdatedAt = 2 }, a with { OverrideUpdatedAt = null },
                 })
            Assert.False(a.Shape.Equals(changed.Shape));
    }

    // ---- limit_e ----------------------------------------------------------------------

    [Fact]
    public void Limit_e_appends_account_id_then_display_name_after_the_persona()
    {
        var persona = PushPersona.From("3f7a9c1e5b2d8046", "Aurora", null)!;
        var e = RelayUsage.LimitE(Acct(display: "Work"), persona);
        Assert.EndsWith("\"persona\":{\"id\":\"3f7a9c1e5b2d8046\",\"name\":\"Aurora\"},\"account_id\":\"" + Id + "\",\"display_name\":\"Work\"}", e);
        Assert.EndsWith("\"resets_at\":1794117192}", RelayUsage.LimitE(Acct(id: null)));
        Assert.DoesNotContain("display_name", RelayUsage.LimitE(Acct(display: "Work"), persona, displayName: false));
        Assert.DoesNotContain("account_id", RelayUsage.LimitE(Acct(display: "Work"), persona, accountId: false));
        Assert.DoesNotContain("account_id", RelayUsage.LimitE(Acct(id: "nope")));
        Assert.DoesNotContain("display_name", RelayUsage.LimitE(Acct(display: " ")));
    }

    // The sealed e is capped. Scanning the name's length finds every stage of the rule: with a long
    // enough name the full e does not fit, then dropping display_name does, then the persona must
    // go too, then account_id, and last of all e itself.
    [Fact]
    public void The_limit_fitting_rule_drops_display_name_then_persona_then_account_id_then_e()
    {
        var persona = PushPersona.From("3f7a9c1e5b2d8046", "Aurora", Hash)!;
        var outcomes = new List<string>();

        for (var n = 1500; n <= 3000; n += 7)
        {
            var a = Acct(display: new string('D', 64), name: new string('n', n));
            var (bytes, withPersona) = RelayLink.LimitPlaintext(a, persona);

            string[] stages =
            [
                RelayUsage.LimitE(a, persona, true, true), RelayUsage.LimitE(a, persona, true, false),
                RelayUsage.LimitE(a, null, true, false), RelayUsage.LimitE(a, null, false, false),
            ];
            var expected = stages.FirstOrDefault(s => PushSeal.SealedLength(Encoding.UTF8.GetByteCount(s)) <= PushSeal.MaxE);
            Assert.Equal(expected, bytes is null ? null : Encoding.UTF8.GetString(bytes));
            Assert.Equal(expected is not null && expected.Contains("\"persona\""), withPersona);
            outcomes.Add(expected is null ? "none" : stages.ToList().IndexOf(expected).ToString());
        }

        Assert.Equal(["0", "1", "2", "3", "none"], outcomes.Distinct().ToArray());
    }

    [Fact]
    public void Without_a_persona_the_limit_fitting_rule_still_starts_with_everything()
    {
        var (bytes, withPersona) = RelayLink.LimitPlaintext(Acct(display: "Work"), null);
        Assert.False(withPersona);
        Assert.Equal(RelayUsage.LimitE(Acct(display: "Work")), Encoding.UTF8.GetString(bytes!));
    }

    // ---- the look this PC gives an account ---------------------------------------------

    private static readonly AccountUsage River = new(null, "river.quill", true, "Max", null, null, null,
        DateTimeOffset.FromUnixTimeSeconds(1794111792));

    private const string IconFile = "0123456789ab-0123456789abcdef.png";

    [Fact]
    public void An_account_with_no_override_is_told_its_derived_two_letter_glyph_and_nothing_else()
    {
        var look = AccountOrbs.LookFor(River, null, "/icons", _ => throw new InvalidOperationException("no icon to offer"));
        Assert.Equal(new RelayUsage.Look(null, "Ri", null, null), look);
    }

    [Fact]
    public void An_override_gives_its_name_its_initials_and_its_stamp_with_a_missing_stamp_as_zero()
    {
        var named = AccountOrbs.LookFor(River, new AccountOrbOverride(null, "Quillwork", null, 1794100000), "/icons", _ => null);
        Assert.Equal(new RelayUsage.Look("Quillwork", "Qu", null, 1794100000), named);   // letters re-derived from the name

        var typed = AccountOrbs.LookFor(River, new AccountOrbOverride("Q✦", "Quillwork", null), "/icons", _ => null);
        Assert.Equal(new RelayUsage.Look("Quillwork", "Q✦", null, 0), typed);
    }

    [Fact]
    public void The_glyph_is_two_letters_whatever_this_pcs_display_setting_is()
    {
        // Resolved with twoLetter: true, so flipping the one-letter setting on this PC does not
        // change what the phone derives from it.
        Assert.Equal(2, AccountOrbs.LookFor(River, null, "/icons", _ => null).Initials!.Length);
    }

    [Fact]
    public void An_icon_is_offered_by_its_full_path_and_its_hash_is_null_until_a_bundle_exists()
    {
        string? asked = null;
        var over = new AccountOrbOverride(null, null, IconFile, 7);

        var building = AccountOrbs.LookFor(River, over, "/icons", p => { asked = p; return null; });
        Assert.Equal(Path.Combine("/icons", IconFile), asked);
        Assert.Null(building.IconHash);

        Assert.Equal(Hash, AccountOrbs.LookFor(River, over, "/icons", _ => Hash).IconHash);
    }

    [Fact]
    public void A_stored_icon_name_that_is_not_one_this_app_minted_is_never_offered()
    {
        var over = new AccountOrbOverride(null, "X", "../../etc/passwd");
        var look = AccountOrbs.LookFor(River, over, "/icons", _ => throw new InvalidOperationException("must not offer"));
        Assert.Null(look.IconHash);
    }

    // ---- the override's stamp, pure rules -----------------------------------------------

    [Fact]
    public void The_stamp_is_not_part_of_what_an_override_is()
    {
        Assert.True(new AccountOrbOverride(null, null, null, 99).IsEmpty);
        Assert.Equal(99, new AccountOrbOverride("AB", null, null, 99).Normalise().UpdatedAt);
        Assert.True(AccountOrbOverride.SameLook(new("AB", "N", null, 1), new("AB", "N", null, 2)));
        Assert.True(AccountOrbOverride.SameLook(null, new AccountOrbOverride(" ", null, null, 3)));   // nothing is nothing
        Assert.False(AccountOrbOverride.SameLook(new("AB", "N", null, 1), new("AB", "M", null, 1)));
        Assert.False(AccountOrbOverride.SameLook(null, new AccountOrbOverride("AB", null, null)));
    }

    // ---- the second image store ----------------------------------------------------------

    private sealed class Store
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<Action> Workers = [];
        public readonly PersonaImageStore Images;

        public Store() => Images = new PersonaImageStore(
            p => Files.TryGetValue(p, out var f) ? (f.Length, DateTime.UnixEpoch) : null,
            p => Files.GetValueOrDefault(p),
            b => new PersonaImageBundle(PersonaImageBundle.HashOf(b), 24, 24, [new PersonaFrames.Frame(0, new byte[10])]),
            Workers.Add, _ => { });

        public string Offer(string name, string content)
        {
            Files[name] = Encoding.UTF8.GetBytes(content);
            Images.Offer(name);
            return PersonaImageBundle.HashOf(Files[name]);
        }

        public void RunAll() { var w = Workers.ToList(); Workers.Clear(); w.ForEach(a => a()); }
    }

    [Fact]
    public void A_bundle_becoming_offerable_raises_Built_once_per_build()
    {
        var s = new Store();
        var built = 0;
        s.Images.Built += () => built++;
        s.Offer("/i/a.png", "A");
        Assert.Equal(0, built);                  // building: the phone has nothing to fetch yet
        s.RunAll();
        Assert.Equal(1, built);
    }

    [Fact]
    public void A_failed_build_raises_nothing()
    {
        var s = new Store();
        var built = 0;
        s.Images.Built += () => built++;
        s.Files["/i/a.png"] = []; s.Images.Offer("/i/a.png");
        s.Files.Remove("/i/a.png");              // gone before the worker reads it
        s.RunAll();
        Assert.Equal(0, built);
    }

    [Fact]
    public void Find_looks_in_the_first_store_then_the_second_and_account_icons_do_not_evict_personas()
    {
        var personas = new Store();
        var icons = new Store();

        var personaHash = personas.Offer("/p/aurora.gif", "P");
        personas.RunAll();
        var iconHashes = Enumerable.Range(0, PersonaImageStore.Capacity + 3).Select(i => icons.Offer($"/i/{i}.png", $"icon{i}")).ToList();
        icons.RunAll();

        // More icons than the store holds: the oldest are forgotten, from the icon store only.
        Assert.Equal(PersonaImageStore.Capacity, icons.Images.Count);
        Assert.NotNull(PersonaImageStore.Find(personaHash, personas.Images, icons.Images));
        Assert.NotNull(PersonaImageStore.Find(iconHashes[^1], personas.Images, icons.Images));
        Assert.Null(PersonaImageStore.Find(iconHashes[0], personas.Images, icons.Images));
        Assert.Null(PersonaImageStore.Find(new string('0', 64), personas.Images, icons.Images));
        Assert.Equal(1, personas.Images.Count);
    }

    [Fact]
    public void The_app_has_one_store_for_account_icons_apart_from_the_persona_one() =>
        Assert.NotSame(PersonaImageStore.Shared, PersonaImageStore.Accounts);
}
