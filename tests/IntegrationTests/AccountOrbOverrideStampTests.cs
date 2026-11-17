using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-340: an override's `updatedAt` through a real settings.json and the one write path that
// stamps it, with the clock held by the test. The stamp is what lets a phone choose between two
// PCs' overrides for one account, so the rules that matter are when it moves and when it must not.
[Collection("Settings")]
public class AccountOrbOverrideStampTests : IDisposable
{
    private long _now = 1794100000;
    private readonly Func<DateTimeOffset> _clock;

    public AccountOrbOverrideStampTests()
    {
        _clock = AccountOrbCustomization.Clock;
        AccountOrbCustomization.Clock = () => DateTimeOffset.FromUnixTimeSeconds(_now);
    }

    public void Dispose() => AccountOrbCustomization.Clock = _clock;

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orb340-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void PointAt(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject Entry(string dir, string key) =>
        ((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!)["accountOrbOverrides"]![key]!.AsObject();

    [Fact]
    public void A_write_stamps_the_second_it_happened_and_the_stamp_survives_a_reload()
    {
        var dir = NewDir(); PointAt(dir);
        AccountOrbCustomization.SetName("account:~", "Work");

        Assert.Equal(1794100000, AccountOrbCustomization.Current("account:~")!.UpdatedAt);
        Assert.Equal(1794100000, Entry(dir, "account:~")["updatedAt"]!.GetValue<long>());

        PointAt(dir);   // a real reload from the file
        Assert.Equal(1794100000, OrbweaverSettings.AccountOrbOverrideFor("account:~")!.UpdatedAt);
    }

    [Fact]
    public void Each_edit_that_changes_the_look_restamps()
    {
        PointAt(NewDir());
        AccountOrbCustomization.SetName("k", "Work");
        _now += 60;
        AccountOrbCustomization.SetInitials("k", "WK");
        Assert.Equal(1794100060, AccountOrbCustomization.Current("k")!.UpdatedAt);
        _now += 60;
        AccountOrbCustomization.SetName("k", "Home");
        Assert.Equal(1794100120, AccountOrbCustomization.Current("k")!.UpdatedAt);
    }

    [Fact]
    public void Saving_the_same_look_again_does_not_make_this_pc_the_newest()
    {
        PointAt(NewDir());
        AccountOrbCustomization.SetName("k", "Work");
        _now += 3600;
        AccountOrbCustomization.SetName("k", "Work");
        AccountOrbCustomization.SetName("k", "  Work  ");   // trims to the same
        Assert.Equal(1794100000, AccountOrbCustomization.Current("k")!.UpdatedAt);
    }

    [Fact]
    public void A_reset_removes_the_override_and_with_it_the_stamp()
    {
        var dir = NewDir(); PointAt(dir);
        AccountOrbCustomization.SetName("k", "Work");
        AccountOrbCustomization.Reset("k");

        Assert.Null(AccountOrbCustomization.Current("k"));
        Assert.False(((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!)["accountOrbOverrides"]!.AsObject().ContainsKey("k"));
    }

    [Fact]
    public void Clearing_the_last_field_removes_the_entry_rather_than_stamping_an_empty_one()
    {
        PointAt(NewDir());
        AccountOrbCustomization.SetName("k", "Work");
        _now += 5;
        AccountOrbCustomization.SetName("k", null);
        Assert.Null(AccountOrbCustomization.Current("k"));
    }

    [Fact]
    public void A_file_written_before_the_stamp_loads_without_one_and_is_rewritten_without_one()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{ "accountOrbOverrides": { "k": { "initials": "AB", "name": "Old", "iconFile": null } } }""");
        PointAt(dir);

        Assert.Null(OrbweaverSettings.AccountOrbOverrideFor("k")!.UpdatedAt);   // projected to the phone as 0

        // Another key written: the old entry is saved back byte-shape for byte-shape, no updatedAt key.
        OrbweaverSettings.SetAccountOrbOverride("other", new("CD", null, null));
        Assert.False(Entry(dir, "k").ContainsKey("updatedAt"));
        Assert.Equal(0, AccountOrbs.LookFor(
            new AccountUsage(null, "x", true, null, null, null, null, DateTimeOffset.UnixEpoch),
            OrbweaverSettings.AccountOrbOverrideFor("k"), "/icons", _ => null).OverrideUpdatedAt);
    }

    [Fact]
    public void Editing_an_old_entry_stamps_it_for_the_first_time()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "accountOrbOverrides": { "k": { "name": "Old" } } }""");
        PointAt(dir);
        AccountOrbCustomization.SetInitials("k", "AB");
        Assert.Equal(1794100000, AccountOrbCustomization.Current("k")!.UpdatedAt);
        Assert.Equal("Old", AccountOrbCustomization.Current("k")!.Name);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("\"soon\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void A_hand_edited_stamp_that_is_not_a_non_negative_number_reads_as_none(string raw)
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"accountOrbOverrides\": { \"k\": { \"name\": \"N\", \"updatedAt\": " + raw + " } } }");
        PointAt(dir);
        Assert.Null(OrbweaverSettings.AccountOrbOverrideFor("k")!.UpdatedAt);
        Assert.Equal("N", OrbweaverSettings.AccountOrbOverrideFor("k")!.Name);
    }

    [Fact]
    public void A_zero_stamp_is_kept_and_a_fractional_one_is_truncated()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{ "accountOrbOverrides": { "z": { "name": "Z", "updatedAt": 0 }, "f": { "name": "F", "updatedAt": 12.7 } } }""");
        PointAt(dir);
        Assert.Equal(0, OrbweaverSettings.AccountOrbOverrideFor("z")!.UpdatedAt);
        Assert.Equal(12, OrbweaverSettings.AccountOrbOverrideFor("f")!.UpdatedAt);
    }

    [Fact]
    public void Unknown_keys_inside_an_override_entry_do_not_stop_the_stamp_round_tripping()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{ "fromNewerBuild": 1, "accountOrbOverrides": { "k": { "name": "N", "updatedAt": 77 } } }""");
        PointAt(dir);
        OrbweaverSettings.SetAccountOrbOverride("other", new("AB", null, null));
        Assert.Equal(77, Entry(dir, "k")["updatedAt"]!.GetValue<long>());
    }
}
