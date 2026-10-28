using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Orbweaver.Tests;

// AgentTeamFiles against real files: the seam the unit tests substitute. A
// member's team is read from a transcript Claude Code is still appending to,
// out of a config directory with the layout it actually writes, so the things
// worth proving here are the ones an in-memory dictionary cannot — sharing,
// size bounds, and a path that is not a file.
public class AgentTeamFilesDiskTests : IDisposable
{
    private const string LeadId = "0a1b2c3d-1111-4222-8333-444455556666";

    private readonly string _config = Path.Combine(Path.GetTempPath(), "cb-teamfiles-" + Guid.NewGuid(), ".claude-x");

    public AgentTeamFilesDiskTests() => Directory.CreateDirectory(_config);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_config)!, recursive: true); } catch { }
    }

    private string Transcript(string id) => Path.Combine(_config, "projects", "-home-user-project", id + ".jsonl");

    private void WriteTeam(string team, string json)
    {
        var dir = Path.Combine(_config, "teams", team);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), json);
    }

    private static string MemberLines(string team, string agent, string hugeAfter = "") =>
        "{\"type\":\"mode\",\"mode\":\"normal\"}\n" +
        $"{{\"parentUuid\":null,\"teamName\":\"{team}\",\"agentName\":\"{agent}\",\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"go\"}}}}\n" +
        // The entry after the first one carries the member's whole CLAUDE.md in
        // a real transcript — hundreds of kilobytes on one line.
        $"{{\"teamName\":\"{team}\",\"agentName\":\"{agent}\",\"attachment\":{{\"content\":\"{hugeAfter}\"}}}}\n";

    private static AgentTeamFiles.Reader RealReader() => new(
        head: path => AgentTeamFiles.ReadHead(path, AgentTeamFiles.HeadBytes),
        config: path => AgentTeamFiles.ReadHead(path, AgentTeamFiles.ConfigBytes),
        locate: path => path);

    [Fact]
    public void AMemberIsLinkedToItsLeadFromFilesOnDisk()
    {
        var path = Transcript("5e6f7a8b");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, MemberLines("session-0a1b2c3d", "probe_live", new string('x', 400_000)));
        WriteTeam("session-0a1b2c3d",
            "{\"leadSessionId\":\"" + LeadId + "\",\"members\":[{\"name\":\"probe_live\",\"color\":\"orange\"}]}");

        var found = RealReader().ReadMany(new[] { ("5e6f7a8b", path) });

        Assert.Equal(new AgentTeam.Membership(LeadId, "orange", "probe_live"), found["5e6f7a8b"]);
    }

    [Fact]
    public void ALeadWithNoTeamFieldsIsNotAMember()
    {
        var path = Transcript(LeadId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"type\":\"mode\",\"mode\":\"normal\"}\n{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");
        WriteTeam("session-0a1b2c3d", "{\"leadSessionId\":\"" + LeadId + "\",\"members\":[]}");

        Assert.Empty(RealReader().ReadMany(new[] { (LeadId, path) }));
    }

    [Fact]
    public void AMemberWhoseTeamConfigIsNotThereYetIsNotATeam()
    {
        var path = Transcript("5e6f7a8b");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, MemberLines("session-0a1b2c3d", "probe_live"));

        Assert.Empty(RealReader().ReadMany(new[] { ("5e6f7a8b", path) }));
    }

    [Fact]
    public void OnlyTheStartOfAFileIsRead()
    {
        // A transcript can be tens of megabytes. Reading it whole to learn two
        // strings would be the most expensive thing the scan did.
        var path = Path.Combine(_config, "big.txt");
        File.WriteAllText(path, new string('a', 100_000));

        Assert.Equal(1_000, AgentTeamFiles.ReadHead(path, 1_000)!.Length);
        Assert.Equal(100_000, AgentTeamFiles.ReadHead(path, 200_000)!.Length);
    }

    [Fact]
    public void AFileTheSessionIsStillWritingCanBeRead()
    {
        // Claude Code appends to the transcript for as long as the session
        // lives, with its own handle open. A read that insists on exclusive
        // access fails exactly when the session is live — the only time it
        // matters.
        var path = Path.Combine(_config, "live.jsonl");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Encoding.UTF8.GetBytes("{\"teamName\":\"t\",\"agentName\":\"a\"}\n"));
        writer.Flush();

        Assert.Equal(new AgentTeamFiles.Identity("t", "a"),
            AgentTeamFiles.IdentityIn(AgentTeamFiles.ReadHead(path, 4096)));
    }

    [Fact]
    public void APathThatIsNotAReadableFileIsNothingNotAnException()
    {
        Assert.Null(AgentTeamFiles.ReadHead(Path.Combine(_config, "missing.jsonl"), 1_000));
        Assert.Null(AgentTeamFiles.ReadHead(_config, 1_000));                       // a directory
        Assert.Null(AgentTeamFiles.ReadHead(Path.Combine(_config, "a\0b"), 1_000)); // not a path
    }

    [Fact]
    public void AMultiByteCharacterCutAtTheBoundDoesNotBreakTheRead()
    {
        // The bound can land in the middle of a UTF-8 sequence; the decoder
        // substitutes a replacement character and the lines before it are
        // unaffected.
        var path = Path.Combine(_config, "utf8.jsonl");
        var first = "{\"teamName\":\"t\",\"agentName\":\"a\"}\n";
        File.WriteAllText(path, first + new string('é', 50_000));

        var head = AgentTeamFiles.ReadHead(path, first.Length + 5);

        Assert.Equal(new AgentTeamFiles.Identity("t", "a"), AgentTeamFiles.IdentityIn(head));
    }
}
