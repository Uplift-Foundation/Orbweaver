using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// The decoded-picture cache, driven from the scan rather than from a persona
// handed over by hand.
//
// LocalPersonaScanTests already proves the scan notices a portrait replaced in
// place and puts the new bytes in the registry. That is only half of what a
// user sees. OpenClawAvatars keeps one decoded Avatar per key, and an orb draws
// whatever is in that cache — so new bytes in the registry and a stale entry in
// the cache still puts the old face on the screen, and the two failures are
// indistinguishable from anywhere except here.
//
// It lives in tests/UiTests because that is the only suite where the decode
// actually happens: OpenClawAvatars.For builds an Avalonia WriteableBitmap, and
// in a process with no Avalonia behind it every decode returns null and every
// assertion below would pass against a cache that was never dropped. The same
// reason ChatPanelAvatarTests and OrbAvatarTests are here.
//
// [AvaloniaFact] rather than [Fact], and that is the whole reason this file is
// not in tests/IntegrationTests: the decode ends in a WriteableBitmap, which
// needs the platform up. Without it every decode returns null, both assertions
// below hold trivially, and the suite would report a cache it never touched.
// No window is constructed all the same.
//
// The user-level config directories are pinned here for CB-143's reason, and
// AnUntouchedPortraitKeepsItsDecodedFrames is why this file cares rather than
// only tests/IntegrationTests. ApplyPersona used to ask
// LocalPersona.UserConfigDirs on every pass, which reads CLAUDE_CONFIG_DIR and
// ClaudeCodeProfileDirs; either one moving between two passes lengthens the
// candidate list, changes the signature, resolves the persona again and hands
// the registry a new object — and a new object is precisely what makes
// LocalPersonas.Set drop the decoded bitmap. So the shared-state coupling and
// the cost this file measures are the same fact seen twice: a portrait
// re-decoded on every tick, per session, on a machine running twenty or thirty
// agents. Pinning the provider is what makes the claim below about the cache
// rather than about what else was running.
//
// [Collection("Settings")] is kept, both because the cases here write settings
// of their own and as a second line behind the pin. The cache and the registry
// are process-wide too, so each case uses its own session id and cleans up
// after itself.
[Collection("Settings")]
public class PersonaPortraitCacheTests : IDisposable
{
    private readonly string _project =
        Path.Combine(Path.GetTempPath(), "cb-portrait-cache-" + Guid.NewGuid());

    private readonly string _statusDir =
        Path.Combine(Path.GetTempPath(), "cb-portrait-status-" + Guid.NewGuid());

    private readonly string _sessionId = "portrait-cache-" + Guid.NewGuid();

    public PersonaPortraitCacheTests()
    {
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_statusDir);
    }

    public void Dispose()
    {
        OpenClawAvatars.Forget(LocalPersonas.AvatarKey(_sessionId));
        LocalPersonas.Forget(_sessionId);
        try { Directory.Delete(_project, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_statusDir, recursive: true); } catch (IOException) { }
    }

    // A real PNG, for the reason LocalPersonaUiTests' own helper gives: these
    // bytes go through the actual decoder, and only a file it can decode proves
    // anything about the cache in front of it.
    private static byte[] Portrait(byte r, byte g, byte b)
    {
        var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(r, g, b, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private SessionStatus Status() => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Cwd = _project,
        Title = "cb-portrait-cache",
    };

    private Dictionary<(string Cwd, SessionSource Source, string Agent, string ConfigDir), IReadOnlyList<LocalPersona.Candidate>> Pass() => new();

    // Nothing above the project tree. See the header — and note this also stops
    // these cases reading the developer's own ~/.claude/CLAUDE.md, which they
    // did before and which would put a real portrait in front of the fixture's
    // on a machine whose owner had written one.
    private SessionManager Manager() =>
        new(_statusDir, null, userConfigDirs: () => Array.Empty<string>());

    // The decoded frames an orb would draw for this session, asked the way the
    // orb asks for them.
    // Asked the way the orb asks for it — by path, since CB-135 stopped the
    // persona record carrying the bytes. Null path means no picture, which is
    // the guard both drawing sites keep in front of this call.
    private OpenClawAvatars.Avatar? Decoded()
    {
        var path = LocalPersonas.For(_sessionId)?.AvatarPath;
        return path is null ? null : OpenClawAvatars.ForFile(LocalPersonas.AvatarKey(_sessionId), path);
    }

    [AvaloniaFact]
    public void APortraitReplacedInPlaceIsDecodedAgainRatherThanServedFromTheCache()
    {
        var picture = Path.Combine(_project, "leota.png");
        File.WriteAllText(
            Path.Combine(_project, "CLAUDE.md"),
            "Her name is Leota.\nHer profile picture is leota.png.\n");
        File.WriteAllBytes(picture, Portrait(0x8A, 0x6F, 0xD4));

        var manager = Manager();
        manager.ApplyPersona(_sessionId, Status(), Pass());

        var before = Decoded();
        Assert.NotNull(before);

        // Two solid 16x16 PNGs can encode to the same number of bytes, so the
        // timestamp is what has to move here — set outright rather than slept
        // for, since the claim is about the signature reading it and not about
        // this filesystem's clock resolution.
        File.WriteAllBytes(picture, Portrait(0x2E, 0xA0, 0x43));
        File.SetLastWriteTimeUtc(picture, File.GetLastWriteTimeUtc(picture).AddSeconds(5));

        manager.ApplyPersona(_sessionId, Status(), Pass());

        var after = Decoded();
        Assert.NotNull(after);

        // Not the same object. OpenClawAvatars answers from its cache on a
        // hit and never looks at the bytes it was handed, so an entry that
        // survived the change would come back identical however new the bytes
        // in the registry are — which is exactly what the orb was drawing.
        Assert.NotSame(before, after);
        // The registry carries the path, and the file behind it is the new
        // portrait — which is the whole of what the decode above had to go and
        // read for the orb to change.
        var portrait = LocalPersonas.For(_sessionId)!.AvatarPath;
        Assert.Equal(picture, portrait);
        Assert.Equal(Portrait(0x2E, 0xA0, 0x43), File.ReadAllBytes(portrait!));
    }

    // The other side of it: nothing moved, so nothing is thrown away. A cache
    // dropped on every tick would re-decode a portrait twice a second for the
    // life of the session, which is the cost this whole signature mechanism
    // exists to avoid.
    [AvaloniaFact]
    public void AnUntouchedPortraitKeepsItsDecodedFrames()
    {
        File.WriteAllText(
            Path.Combine(_project, "CLAUDE.md"),
            "Her name is Leota.\nHer profile picture is leota.png.\n");
        File.WriteAllBytes(Path.Combine(_project, "leota.png"), Portrait(0x8A, 0x6F, 0xD4));

        var manager = Manager();
        manager.ApplyPersona(_sessionId, Status(), Pass());

        var before = Decoded();
        Assert.NotNull(before);

        manager.ApplyPersona(_sessionId, Status(), Pass());
        manager.ApplyPersona(_sessionId, Status(), Pass());

        Assert.Same(before, Decoded());
    }

    // CB-143, at the level where it actually costs something. The scan used to
    // ask which Claude Code accounts this machine had on every single pass, so
    // an account appearing in settings — or a CLAUDE_CONFIG_DIR that moved
    // under a test in another xUnit collection — lengthened the candidate list,
    // changed the signature, resolved the persona again, and handed the
    // registry an object that was new by identity and identical by content.
    // LocalPersonas.Set compares by reference, so that threw away the decoded
    // frames: the orb re-decoded a portrait it was already drawing, for a
    // change to something that has nothing to do with the picture.
    //
    // Asserted on the decoded Avatar rather than on the Persona because that is
    // the thing a user pays for. A new Persona record costs a few allocations;
    // a re-decode is a PNG through Skia and a WriteableBitmap, twice a second,
    // per agent.
    [AvaloniaFact]
    public void AnAccountAppearingInSettingsDoesNotCostThePortraitItsDecodedFrames()
    {
        File.WriteAllText(
            Path.Combine(_project, "CLAUDE.md"),
            "Her name is Leota.\nHer profile picture is leota.png.\n");
        File.WriteAllBytes(Path.Combine(_project, "leota.png"), Portrait(0x8A, 0x6F, 0xD4));

        var manager = Manager();
        manager.ApplyPersona(_sessionId, Status(), Pass());

        var before = Decoded();
        Assert.NotNull(before);

        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-cb143-ui");
        try
        {
            manager.ApplyPersona(_sessionId, Status(), Pass());
            Assert.Same(before, Decoded());
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-cb143-ui");
        }

        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(before, Decoded());
    }
}
