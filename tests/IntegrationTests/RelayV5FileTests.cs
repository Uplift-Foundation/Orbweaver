using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-321: persona pictures against real files — a real animated GIF on disk,
/// read through PersonaFiles' own reader and stat, decoded and re-encoded by
/// Skia, offered through the store; a source over the 16 MiB cap never offered;
/// and the default persona resolved from a real config dir's CLAUDE.md with no
/// cwd at all, which is the assumption the architect's plan asked the desktop
/// engineer to confirm.
/// </summary>
public class RelayV5FileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb321-" + Guid.NewGuid().ToString("N")[..8]);

    public RelayV5FileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private readonly List<string> _log = [];

    // The real file system, PersonaFiles' reader and Skia; only the worker is
    // run inline so the test knows when the build has finished.
    private PersonaImageStore RealStore() => new(
        PeerAvatarStore.StatFile, PersonaFiles.ReadAvatarFile, b => PersonaImageBundle.Build(b), work => work(), _log.Add);

    private string Gif(string name, int w, int h, int frames)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, TinyGif.Write(w, h,
            Enumerable.Range(0, frames).Select(i => TinyGif.Solid(w, h, (byte)(i % 4))).ToList(),
            Enumerable.Repeat(10, frames).ToList()));
        return path;
    }

    [Fact]
    public void A_real_animated_gif_on_disk_is_offered_as_frames_under_every_cap()
    {
        var path = Gif("aurora.gif", 300, 300, 12);
        var store = RealStore();
        Assert.Null(store.Offer(path));                               // the first offer builds
        var hash = store.Offer(path);
        Assert.Equal(PersonaImageBundle.HashOf(File.ReadAllBytes(path)), hash);

        var b = store.ById(hash!)!;
        Assert.Equal((216, 216, 12), (b.W, b.H, b.Frames.Count));
        Assert.True(b.Bytes <= PersonaFrames.MaxImageBytes);
        Assert.All(b.Frames, f => Assert.True(PersonaFrames.FrameAllowed(f.Data.Length)));
        Assert.All(b.Frames, f => Assert.Equal(100, f.Ms));
        // Skia wrote WebP on this machine — the contract's first choice, which
        // the plan assumed rather than ran.
        Assert.All(b.Frames, f => Assert.Equal("RIFF", Encoding.ASCII.GetString(f.Data, 0, 4)));
        Assert.All(b.Frames, f => Assert.Equal("WEBP", Encoding.ASCII.GetString(f.Data, 8, 4)));
        foreach (var w in b.Windows)
        {
            Assert.True(Encoding.UTF8.GetByteCount(w.Json) <= PersonaFrames.MaxWindowBytes);
            Assert.Equal(hash, JsonDocument.Parse(w.Json).RootElement.GetProperty("hash").GetString());
        }
    }

    [Fact]
    public void A_source_over_16_MiB_is_never_offered()
    {
        var path = Path.Combine(_dir, "huge.gif");
        var big = new byte[17 * 1024 * 1024];
        TinyGif.Write(16, 16, [TinyGif.Solid(16, 16, 1)], [10]).CopyTo(big, 0);
        File.WriteAllBytes(path, big);
        var store = RealStore();
        Assert.Null(store.Offer(path));
        Assert.Null(store.Offer(path));
        Assert.Equal(0, store.Count);
        Assert.Contains("persona_image not offered: unreadable, over the cap, or no frames fit", _log);
    }

    [Fact]
    public void A_changed_picture_on_disk_is_offered_under_its_new_hash()
    {
        var path = Gif("nova.gif", 32, 32, 2);
        var store = RealStore();
        store.Offer(path);
        var first = store.Offer(path);
        File.WriteAllBytes(path, TinyGif.Write(32, 32, [TinyGif.Solid(32, 32, 3)], [10]));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Null(store.Offer(path));
        var second = store.Offer(path);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Single(store.ById(second!)!.Frames);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(20);
        }
    }

    // The app's own wiring, end to end: PersonaImageStore.Shared stats, reads
    // and builds on its own thread, and a later offer names the picture.
    [Fact]
    public async Task The_shared_store_builds_on_its_own_thread_and_then_offers()
    {
        var path = Gif("shared.gif", 48, 48, 3);
        Assert.Null(PersonaImageStore.Shared.Offer(path));
        string? hash = null;
        await Until(() => (hash = PersonaImageStore.Shared.Offer(path)) is not null);
        Assert.Equal(PersonaImageBundle.HashOf(File.ReadAllBytes(path)), hash);
        Assert.Equal(3, PersonaImageStore.Shared.ById(hash!)!.Frames.Count);
    }

    // And the default persona's cache: read off-thread, the stand-in (none)
    // until it has, then the config dir's own persona.
    [Fact]
    public async Task The_cached_default_persona_is_read_from_home_and_the_config_dirs_off_thread()
    {
        var home = Path.Combine(_dir, "home-cached");
        var config = Path.Combine(_dir, "cached");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "CLAUDE.md"), "# Nova\n\n- Name: Nova\n");
        try
        {
            RelayService.Home = () => home;                                                  // never the real home
            await Until(() => RelayService.CachedDefaultPersona([config])?.Name == "Nova");  // home names nobody: the config dir
            await Until(() => RelayService.CachedDefaultPersona([]) is { Name: null });      // nothing anywhere: no persona
            RelayService.Home = () => null;
            await Until(() => RelayService.CachedDefaultPersona([config])?.Name == "Nova");  // no home at all: the config dirs alone
        }
        finally { RelayService.Home = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
    }

    // CB-321, from Juno's simulator rehearsal: on a real Mac the default persona
    // is named by ~/CLAUDE.md through an @-import, and the config dirs say
    // nothing. Resolving from home finds it, picture and all; the config dirs
    // alone (the first reading) find nothing in the same tree — the negative
    // control that shows the test can tell the two apart.
    [Fact]
    public void The_default_persona_comes_from_home_s_own_claude_md_and_its_import()
    {
        var home = Path.Combine(_dir, "home");
        var config = Path.Combine(home, ".claude");
        Directory.CreateDirectory(Path.Combine(home, ".claude", "persona"));
        File.WriteAllText(Path.Combine(config, "CLAUDE.md"), "# Global notes\n\nNothing about who I am.\n");
        File.WriteAllText(Path.Combine(home, "CLAUDE.md"), "@.claude/persona/persona.md\n");
        File.WriteAllText(Path.Combine(home, ".claude", "persona", "persona.md"), "# Aurora\n\n- Name: Aurora\n- Avatar: aurora.gif\n");
        var gif = Gif("home/.claude/persona/aurora.gif", 24, 24, 2);

        var persona = RelayService.ResolveDefault(home, [config]);
        Assert.Equal("Aurora", persona.Name);
        Assert.Equal(Path.GetFullPath(gif), Path.GetFullPath(persona.AvatarPath!));
        Assert.Equal(Path.GetFullPath(Path.Combine(home, ".claude", "persona", "persona.md")), Path.GetFullPath(persona.AvatarSource!));
        Assert.Null(RelayService.ResolveDefault(null, [config]).Name);                       // the config dirs alone: nobody
        Assert.Null(RelayService.ResolveDefault("", [config]).Name);

        var store = RealStore();
        store.Offer(persona.AvatarPath!);
        var row = Assert.Single(RelayPersonaRows.Rows([], persona, store.Offer));
        Assert.Equal((true, "Aurora", PersonaImageBundle.HashOf(File.ReadAllBytes(gif))), (row.Default, row.Name, row.ImageHash));
    }

    [Fact]
    public void The_default_persona_resolves_from_the_config_dirs_alone_with_no_cwd()
    {
        var config = Path.Combine(_dir, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "CLAUDE.md"), "# Aurora\n\n- Name: Aurora\n- Avatar: aurora.gif\n");
        var gif = Gif("config/aurora.gif", 24, 24, 3);

        var candidates = LocalPersona.Candidates(null, [config], SessionSource.ClaudeCode);
        Assert.Equal([Path.Combine(config, "CLAUDE.md")], candidates.Select(c => c.Path));
        // Any other CLI has no config-dir persona at all.
        Assert.Empty(LocalPersona.Candidates(null, [config], SessionSource.Codex));

        var persona = LocalPersona.ResolveFrom(candidates);
        Assert.Equal("Aurora", persona.Name);
        Assert.Equal(Path.GetFullPath(gif), Path.GetFullPath(persona.AvatarPath!));

        var store = RealStore();
        store.Offer(persona.AvatarPath!);
        var rows = RelayPersonaRows.Rows([], persona, store.Offer);
        var row = Assert.Single(rows);
        Assert.Equal((true, "Aurora", PersonaImageBundle.HashOf(File.ReadAllBytes(gif))), (row.Default, row.Name, row.ImageHash));
        Assert.Equal(PersonaFrames.PersonaId(persona.AvatarSource, "Aurora"), row.Id);
        Assert.DoesNotContain(config, PersonaFrames.Personas(rows));         // no path on the wire
    }
}
