using Xunit;

namespace Orbweaver.Tests;

// cloud-chat.log on a real disk (CB-285): where it lands, that it rotates
// rather than going deaf, and that it never throws.
//
// Scoped with CrashLog.ScopeForTests rather than ORBWEAVER_LOG_DIR, for the
// reason that method gives. Unlike persona.log and hotkeys.log there is no
// said-once set to reset: these lines legitimately repeat.
public class CloudChatLogFileTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "cb-cloud-chat-log-" + Guid.NewGuid());
    private readonly IDisposable _logScope;

    public CloudChatLogFileTests() => _logScope = CrashLog.ScopeForTests(_logDir);

    public void Dispose()
    {
        _logScope.Dispose();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private static readonly DateTimeOffset When = new(2026, 11, 14, 17, 0, 0, TimeSpan.Zero);

    private string[] Lines() =>
        File.Exists(CloudChatLog.Path_) ? File.ReadAllLines(CloudChatLog.Path_) : Array.Empty<string>();

    [Fact]
    public void ALineLandsBesideCrashLogInPersonaLogsShape()
    {
        CloudChatLog.Record("send session_a: sent, watching by poll");

        Assert.Equal(Path.Combine(_logDir, "cloud-chat.log"), CloudChatLog.Path_);
        var line = Assert.Single(Lines());
        Assert.EndsWith("  send session_a: sent, watching by poll", line);
    }

    // Unlike persona.log: the same decision made twice is two lines.
    [Fact]
    public void ARepeatedLineIsWrittenEachTime()
    {
        CloudChatLog.Write(When, "send session_a: sent, watching by stream");
        CloudChatLog.Write(When, "send session_a: sent, watching by stream");

        Assert.Equal(2, Lines().Length);
        Assert.All(Lines(), l => Assert.Equal(PersonaLog.Format(When, "send session_a: sent, watching by stream").TrimEnd(), l));
    }

    // At the cap the file becomes .1, replacing any older one, and writing
    // carries on in a fresh file rather than stopping.
    [Fact]
    public void AFullFileIsRotatedAndWritingCarriesOn()
    {
        Directory.CreateDirectory(_logDir);
        File.WriteAllText(CloudChatLog.Path_ + ".1", "the generation before last");
        File.WriteAllText(CloudChatLog.Path_, new string('x', 100));

        CloudChatLog.Write(When, "live session_a: turn over after 4s (reply in transcript)", maxBytes: 100);

        Assert.Equal(new string('x', 100), File.ReadAllText(CloudChatLog.Path_ + ".1"));
        var line = Assert.Single(Lines());
        Assert.EndsWith("turn over after 4s (reply in transcript)", line);
    }

    [Fact]
    public void AFileUnderTheCapIsAppendedTo()
    {
        CloudChatLog.Write(When, "first", maxBytes: 1000);
        CloudChatLog.Write(When, "second", maxBytes: 1000);

        Assert.Equal(2, Lines().Length);
        Assert.False(File.Exists(CloudChatLog.Path_ + ".1"));
    }

    [Fact]
    public void ALogDirectoryThatIsSomebodysFileIsSurvivedInSilence()
    {
        Directory.CreateDirectory(_logDir);
        var blocked = Path.Combine(_logDir, "not-a-directory");
        File.WriteAllText(blocked, "I am a file");
        using var onAFile = CrashLog.ScopeForTests(blocked);

        CloudChatLog.Record("send session_a: sent, watching by poll");

        Assert.Equal("I am a file", File.ReadAllText(blocked));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(2, "2s")]
    [InlineData(12.4, "12s")]
    [InlineData(29.6, "30s")]
    public void DurationsAreWholeSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, CloudChatLog.Seconds(TimeSpan.FromSeconds(seconds)));
    }
}
