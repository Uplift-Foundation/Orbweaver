using System.Text.Json;

namespace Orbweaver.Tests;

// A model filesystem for BrowseRules, built from the `desks` of frames-v11-vectors.json (or by
// hand): every folder, file and link in directory order, with `target` on a link, `alias` for a
// Windows short name, `hidden_attr`, `unreadable` and `persona` as the vectors' _rules describe.
// Name lookups ignore case and return the stored spelling, as APFS and NTFS do by default.
internal sealed class FakeBrowseFileSystem : IBrowseFileSystem
{
    internal sealed record Node(string Path, BrowseKind Kind, string? Target = null, string? Alias = null,
        bool HiddenAttr = false, bool Unreadable = false, bool Persona = false, bool Dir = true);

    private readonly List<Node> _nodes = [];

    internal FakeBrowseFileSystem(string home, bool windows, IEnumerable<string> deniedExact, IEnumerable<string> deniedTrees)
    {
        Home = home;
        IsWindows = windows;
        DeniedExact = deniedExact.ToArray();
        DeniedTrees = deniedTrees.ToArray();
    }

    public string Home { get; }
    public bool IsWindows { get; }
    public bool CaseInsensitive => true;
    public IReadOnlyList<string> DeniedExact { get; }
    public IReadOnlyList<string> DeniedTrees { get; }

    // Anything the test wants to throw from a read, to reach the rules' and the handler's catch.
    internal Func<string, Exception?>? OnRead { get; set; }

    // Counts, so a test can show the rules did (or did not) look at a folder.
    internal List<string> Enumerated { get; } = [];

    private char Sep => IsWindows ? '\\' : '/';

    internal FakeBrowseFileSystem Add(string path, BrowseKind kind = BrowseKind.Dir, string? target = null,
        string? alias = null, bool hidden = false, bool unreadable = false, bool persona = false)
    {
        _nodes.Add(new Node(path, kind, target, alias, hidden, unreadable, persona));
        return this;
    }

    internal static FakeBrowseFileSystem FromVectors(JsonElement desk)
    {
        var fs = new FakeBrowseFileSystem(desk.GetProperty("home").GetString()!, desk.GetProperty("platform").GetString() == "win",
            desk.GetProperty("denied_exact").EnumerateArray().Select(e => e.GetString()!),
            desk.GetProperty("denied_trees").EnumerateArray().Select(e => e.GetString()!));
        foreach (var n in desk.GetProperty("nodes").EnumerateArray())
        {
            var kind = n.GetProperty("kind").GetString() switch { "dir" => BrowseKind.Dir, "file" => BrowseKind.File, _ => BrowseKind.Link };
            bool Flag(string name) => n.TryGetProperty(name, out var v) && v.GetBoolean();
            fs.Add(n.GetProperty("path").GetString()!, kind,
                n.TryGetProperty("target", out var t) ? t.GetString() : null,
                n.TryGetProperty("alias", out var a) ? a.GetString() : null,
                Flag("hidden_attr"), Flag("unreadable"), Flag("persona"));
        }
        foreach (var g in desk.GetProperty("generated").EnumerateArray())
        {
            var parent = g.GetProperty("parent").GetString()!;
            var width = g.GetProperty("width").GetInt32();
            for (var i = 0; i < g.GetProperty("count").GetInt32(); i++)
                fs.Add(fs.Child(parent, g.GetProperty("prefix").GetString()! + i.ToString("D" + width)));
        }
        return fs;
    }

    private string Child(string parent, string name) => parent.EndsWith(Sep) ? parent + name : parent + Sep + name;

    private string? ParentOf(string path)
    {
        var i = path.LastIndexOf(Sep);
        if (IsWindows ? i <= 2 && path.Length <= 3 : path == "/") return null;
        return i <= (IsWindows ? 2 : 0) ? path[..(i + 1)] : path[..i];
    }

    private Node? Exact(string path) =>
        _nodes.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<Node> ChildrenOf(string folder) =>
        _nodes.Where(n => ParentOf(n.Path) is { } p && string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));

    public BrowseEntry? Lookup(string parent, string name)
    {
        if (Exact(parent) is not { Kind: BrowseKind.Dir, Unreadable: false }) return null;
        foreach (var n in ChildrenOf(parent))
        {
            var stored = n.Path[(n.Path.LastIndexOf(Sep) + 1)..];
            if (string.Equals(stored, name, StringComparison.OrdinalIgnoreCase)
                || (n.Alias is not null && string.Equals(n.Alias, name, StringComparison.OrdinalIgnoreCase)))
                return new BrowseEntry(stored, n.Kind, n.Target);
        }
        return null;
    }

    public bool DirectoryExists(string path) => Exact(path) is { Kind: BrowseKind.Dir };

    public (IReadOnlyList<BrowseEntry> Entries, bool More)? Enumerate(string folder, int cap)
    {
        Enumerated.Add(folder);
        if (OnRead?.Invoke(folder) is { } boom) throw boom;
        if (Exact(folder) is not { Kind: BrowseKind.Dir, Unreadable: false }) return null;
        var all = ChildrenOf(folder)
            .Select(n => new BrowseEntry(n.Path[(n.Path.LastIndexOf(Sep) + 1)..], n.Kind, n.Target)).ToList();
        return (all.Take(cap).ToList(), all.Count > cap);
    }

    public bool HiddenAttribute(string folder, string name) =>
        Exact(Child(folder, name)) is { HiddenAttr: true };

    public bool PersonaAt(string folder) => Exact(folder) is { Persona: true };
}
