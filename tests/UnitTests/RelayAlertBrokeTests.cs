using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>CB-428 (frames-v16 prefs): broke_alerts beside CB-358's two keys. Off until a phone says the JSON true, kept per phone across a restart.</summary>
public class RelayAlertBrokeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-alert-broke-" + Guid.NewGuid());

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Theory]
    [InlineData("""{"done_alerts":"all","broke_alerts":true}""", true)]
    [InlineData("""{"done_alerts":"all","broke_alerts":false}""", false)]
    [InlineData("""{"done_alerts":"all"}""", false)]
    [InlineData("""{"done_alerts":"all","broke_alerts":"true"}""", false)]
    [InlineData("""{"done_alerts":"all","broke_alerts":1}""", false)]
    [InlineData("""{"done_alerts":"all","broke_alerts":null}""", false)]
    public void Only_the_json_true_turns_broke_alerts_on_and_the_rest_of_the_frame_still_parses(string frame, bool broke)
    {
        var p = AlertPrefs.Parse(J(frame));
        Assert.NotNull(p);
        Assert.Equal((DoneAlerts.All, true, broke), (p!.Value.DoneAlerts, p.Value.AmberMembers, p.Value.BrokeAlerts));
        Assert.Equal(broke, RelayAlertRules.PushBroke(p.Value));
    }

    [Fact]
    public void A_frame_with_a_bad_done_alerts_is_still_ignored_whatever_broke_alerts_says_and_the_default_is_off()
    {
        Assert.Null(AlertPrefs.Parse(J("""{"done_alerts":"sometimes","broke_alerts":true}""")));
        Assert.False(RelayAlertRules.PushBroke(AlertPrefs.Default));
        Assert.True(AlertPrefs.FromStored("all", true, true)!.Value.BrokeAlerts);
        Assert.False(AlertPrefs.FromStored("all", true, null)!.Value.BrokeAlerts);           // a file from before the key
        Assert.Null(AlertPrefs.FromStored(null, null, true));                                // no choice at all stays no choice
    }

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { lock (_items) _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) { lock (_items) return _items.TryGetValue(reference[4..], out var v) ? v : null; }
        public void Forget(string reference) { }
    }

    [Fact]
    public void The_choice_survives_a_restart_and_a_later_prefs_frame_replaces_it()
    {
        var store = new MemoryStore();
        var id = new RelayIdentity(_dir, store);
        id.Keys();
        var phone = RelayCrypto.GenerateKeyPair();
        id.AddPhone("ph_00000000000000e1", "Test iPhone", phone.Public, RelayCrypto.GenerateKeyPair().Public);
        Assert.True(id.SaveAlerts("ph_00000000000000e1", new AlertPrefs(DoneAlerts.Leads, false, true)));

        var again = new RelayIdentity(_dir, store);
        var got = again.Phones.Single().Alerts!.Value;
        Assert.Equal((DoneAlerts.Leads, false, true), (got.DoneAlerts, got.AmberMembers, got.BrokeAlerts));

        Assert.True(again.SaveAlerts("ph_00000000000000e1", new AlertPrefs(DoneAlerts.Leads, false, false)));
        Assert.False(new RelayIdentity(_dir, store).Phones.Single().Alerts!.Value.BrokeAlerts);
        Assert.False(again.SaveAlerts("ph_unknown", AlertPrefs.Default));
    }
}
