using Xunit;

namespace Orbweaver.Tests;

// CB-339 plan 3A: the persona badge on a picker row means "a persona is named by a file at THIS
// folder's own level". The plan assumed LocalPersona's walk, with a ceiling at the folder, gives
// exactly that; these are the cases that assumption stands on, written before BrowsePersona was
// relied on. In the Settings collection because LocalPersona reaches process-wide settings.
[Collection("Settings")]
public class RelayBrowsePersonaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb339-persona-" + Guid.NewGuid());

    public RelayBrowsePersonaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Folder(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void AFolderWithItsOwnPersonaIsMarked()
    {
        var child = Folder("project");
        File.WriteAllText(Path.Combine(child, "CLAUDE.md"), "Her name is Leota");
        Assert.True(BrowsePersona.At(child));
    }

    [Fact]
    public void APersonaInTheParentDoesNotMarkTheChild()
    {
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "Her name is Leota");
        var child = Folder("project");

        // The positive control: the same persona does mark the folder that holds it.
        Assert.True(BrowsePersona.At(_root));
        Assert.False(BrowsePersona.At(child));
    }

    [Fact]
    public void APersonaInAnImportedFileAtTheFoldersOwnLevelIsMarked()
    {
        var child = Folder("project");
        File.WriteAllText(Path.Combine(child, "CLAUDE.md"), "@persona.md");
        File.WriteAllText(Path.Combine(child, "persona.md"), "Her name is Leota");
        Assert.True(BrowsePersona.At(child));
    }

    [Fact]
    public void AFolderWithOnlyAnUnnamedClaudeMdIsNotAPersona()
    {
        var child = Folder("project");
        File.WriteAllText(Path.Combine(child, "CLAUDE.md"), "Build with dotnet build.");
        Assert.False(BrowsePersona.At(child));
    }

    [Fact]
    public void AFolderThatDoesNotExistIsNotAPersona() =>
        Assert.False(BrowsePersona.At(Path.Combine(_root, "nope")));
}
