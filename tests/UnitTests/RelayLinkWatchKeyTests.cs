using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-323 watch-key addendum through RelayLink: a sealed key.add or key.remove
/// from a paired phone, opened under the pair's key, decided against what the
/// identity holds, persisted, and answered sealed (class pair) to that phone.
/// A decision that could not be written is not answered at all, and a frame
/// from a phone unpaired in the meantime gets nothing.
/// </summary>
public partial class RelayLinkTests
{
    private static string KeyFrame(string op, KeyPair signer, byte[] pub, string phoneId, long at, string? signedPhoneId = null)
    {
        var pubHex = Hex(pub);
        var sig = Sign(signer, RelayWatchKey.Message(RelayWatchKey.Body(op, "watch", pubHex, signedPhoneId ?? phoneId, at)));
        return JsonSerializer.Serialize(new { t = "key." + op, kind = "watch", pub = pubHex, at, sig = Base64Url(sig) });
    }

    private static JsonElement Reply(FakePhone phone, IReadOnlyList<string> frames)
    {
        var env = J(Assert.Single(frames));
        Assert.Equal(("pair", "none", ""), (env.GetProperty("class").GetString(), env.GetProperty("push").GetString(), env.GetProperty("ck").GetString()));
        return phone.OpenFromDesktop(frames[0]);
    }

    [Fact]
    public void A_watch_key_is_added_confirmed_again_on_a_repeat_removed_and_its_old_add_is_stale()
    {
        var (link, identity, phone, notes, _) = Paired();
        var watch = GenerateKeyPair();

        var add = phone.Sealed("pair", KeyFrame("add", phone.IdKey, watch.Public, phone.Id, 100));
        var ok = Reply(phone, link.OnFrame(add));
        Assert.Equal("""{"t":"key.ok","op":"add","kind":"watch","at":100}""", ok.GetRawText());
        Assert.Equal((Hex(watch.Public), 100L), (Hex(identity.WatchPublicOf(phone.Id)!), identity.PhoneById(phone.Id)!.WatchAt!.Value));
        Assert.Contains($"key.add from {phone.Id}: ok", notes);

        // The phone missed the reply and sends the same frame again.
        Assert.Equal("key.ok", Reply(phone, link.OnFrame(phone.Sealed("pair", KeyFrame("add", phone.IdKey, watch.Public, phone.Id, 100)))).GetProperty("t").GetString());

        var removed = Reply(phone, link.OnFrame(phone.Sealed("pair", KeyFrame("remove", watch, watch.Public, phone.Id, 200))));
        Assert.Equal(("key.ok", "remove", 200L), (removed.GetProperty("t").GetString(), removed.GetProperty("op").GetString(), removed.GetProperty("at").GetInt64()));
        Assert.Null(identity.WatchPublicOf(phone.Id));
        Assert.Equal(200L, identity.PhoneById(phone.Id)!.WatchAt);

        var replayed = Reply(phone, link.OnFrame(phone.Sealed("pair", KeyFrame("add", phone.IdKey, watch.Public, phone.Id, 100))));
        Assert.Equal(("key.err", "stale"), (replayed.GetProperty("t").GetString(), replayed.GetProperty("code").GetString()));
        Assert.Null(identity.WatchPublicOf(phone.Id));
    }

    [Fact]
    public void A_key_add_signed_for_another_phone_is_refused_and_changes_nothing()
    {
        var (link, identity, phone, notes, _) = Paired();
        var watch = GenerateKeyPair();
        var err = Reply(phone, link.OnFrame(phone.Sealed("pair", KeyFrame("add", phone.IdKey, watch.Public, phone.Id, 100, signedPhoneId: "ph_someoneelse"))));
        Assert.Equal(("key.err", "bad_signature"), (err.GetProperty("t").GetString(), err.GetProperty("code").GetString()));
        Assert.Null(identity.PhoneById(phone.Id)!.WatchAt);
        Assert.Contains($"key.add from {phone.Id}: bad_signature", notes);
    }

    [Fact]
    public void An_accepted_key_that_cannot_be_written_is_not_confirmed()
    {
        var (link, identity, phone, notes, _) = Paired();
        Directory.CreateDirectory(identity.FilePath + ".tmp");       // where Save writes first: now it cannot
        var watch = GenerateKeyPair();
        Assert.Empty(link.OnFrame(phone.Sealed("pair", KeyFrame("add", phone.IdKey, watch.Public, phone.Id, 100))));
        Assert.Contains($"key.add from {phone.Id}: accepted but not persisted; no reply", notes);
        Assert.Null(identity.WatchPublicOf(phone.Id));                // put back as it was
        Assert.Null(identity.PhoneById(phone.Id)!.WatchAt);

        Directory.Delete(identity.FilePath + ".tmp");
        Assert.Equal("key.ok", Reply(phone, link.OnFrame(phone.Sealed("pair", KeyFrame("add", phone.IdKey, watch.Public, phone.Id, 100)))).GetProperty("t").GetString());
    }

    [Fact]
    public void A_key_frame_from_a_phone_unpaired_meanwhile_gets_nothing()
    {
        var (link, identity, phone, _, _) = Paired();
        var frame = phone.Sealed("pair", KeyFrame("add", phone.IdKey, GenerateKeyPair().Public, phone.Id, 100));
        identity.RemovePhone(phone.Id);                              // the channel is still open in the link
        Assert.Empty(link.OnFrame(frame));
    }

    [Fact]
    public void A_key_frame_on_any_other_class_is_not_a_key_frame()
    {
        var (link, identity, phone, _, _) = Paired();
        Assert.Empty(link.OnFrame(phone.Sealed("action", KeyFrame("add", phone.IdKey, GenerateKeyPair().Public, phone.Id, 100))));
        Assert.Null(identity.PhoneById(phone.Id)!.WatchAt);
        _ = Encoding.UTF8;
    }
}
