using Xunit;

namespace Orbweaver.Tests;

// The two new "New chat…" settings — the recent-folders list and the last
// CLI chosen — round-tripped through a real settings.json.
//
// Same collection and repointing dance as SettingsListsAndProfilesTests, for
// the reason its own header gives: OrbweaverSettings is one static model
// for the whole process.
[Collection("Settings")]
public class NewChatSettingsTests
{
    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-settings-newchat-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    [Fact]
    public void WithNothingSavedTheRecentFolderListIsEmpty()
    {
        FreshSettings();

        Assert.Empty(OrbweaverSettings.NewChatRecentFolders);
    }

    [Fact]
    public void TheRecentFolderListSurvivesARestart()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;

        OrbweaverSettings.SetNewChatRecentFolders(new[] { "/repo/one", "/repo/two" });

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();

        Assert.Equal(new[] { "/repo/one", "/repo/two" }, OrbweaverSettings.NewChatRecentFolders);
    }

    // Blank entries are skipped rather than stored — the same rule the other
    // list settings in this file apply, so a caller that hands the setter a
    // half-formed value doesn't end up with an empty combo row.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFoldersAreSkippedRatherThanStored(string blank)
    {
        FreshSettings();

        OrbweaverSettings.SetNewChatRecentFolders(new[] { "/repo/one", blank });

        Assert.Equal(new[] { "/repo/one" }, OrbweaverSettings.NewChatRecentFolders);
    }

    // Setting the list replaces it outright rather than merging — the caller
    // (RecentFolders.Merge) is what decides ordering/de-duplication/the cap,
    // so this setter has no rule of its own to apply beyond dropping blanks.
    [Fact]
    public void SettingTheListReplacesWhatWasThereBefore()
    {
        FreshSettings();

        OrbweaverSettings.SetNewChatRecentFolders(new[] { "/repo/one" });
        OrbweaverSettings.SetNewChatRecentFolders(new[] { "/repo/two", "/repo/three" });

        Assert.Equal(new[] { "/repo/two", "/repo/three" }, OrbweaverSettings.NewChatRecentFolders);
    }

    [Fact]
    public void WithNothingSavedTheLastCliIsNull()
    {
        FreshSettings();

        Assert.Null(OrbweaverSettings.NewChatLastCli);
    }

    [Fact]
    public void TheLastCliSurvivesARestart()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;

        OrbweaverSettings.SetNewChatLastCli("codex");

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();

        Assert.Equal("codex", OrbweaverSettings.NewChatLastCli);
    }

    // A blank value is not a choice — same rule NewChatRecentFolders applies,
    // restated for the scalar setting so a caller passing through an empty
    // combo selection doesn't store a value that reads as "chosen".
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankLastCliIsStoredAsNull(string? blank)
    {
        FreshSettings();

        OrbweaverSettings.SetNewChatLastCli("codex");
        OrbweaverSettings.SetNewChatLastCli(blank);

        Assert.Null(OrbweaverSettings.NewChatLastCli);
    }

    // --- NewChatLastProfile (CB-201's Account picker) --------------------

    [Fact]
    public void WithNothingSavedTheLastProfileIsNull()
    {
        FreshSettings();

        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    [Fact]
    public void TheLastProfileSurvivesARestart()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;

        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();

        Assert.Equal(".claude-work", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    // A blank value is not a choice — same rule ABlankLastCliIsStoredAsNull
    // applies to the CLI setting beside this one.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankLastProfileIsStoredAsNull(string? blank)
    {
        FreshSettings();

        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, blank);

        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    // Null means Default was chosen just as much as it means never chosen —
    // both read the same way, so setting it back to null after a real
    // profile is the ordinary "picked Default this time" case, not an edge
    // case of the blank rule above.
    [Fact]
    public void SettingTheLastProfileBackToNullReplacesTheSavedOne()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;

        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, null);

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();

        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    // --- CB-203: one remembered account per CLI -------------------------

    // Each CLI's last account round-trips through a real file on its own key,
    // and setting one never disturbs another — a Codex pick must never come
    // back as Claude Code's (or Grok's) remembered account.
    [Fact]
    public void EachCliRemembersItsOwnLastAccountAcrossAReload()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;

        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.Codex, ".codex-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.Grok, ".grok-work");

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();

        Assert.Equal(".claude-work", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
        Assert.Equal(".codex-work", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.Codex));
        Assert.Equal(".grok-work", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.Grok));

        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        var map = root["newChatLastProfiles"]!.AsObject();
        Assert.Equal(new[] { "ClaudeCode", "Codex", "Grok" }, map.Select(p => p.Key));
        Assert.Equal(".codex-work", (string?)map["Codex"]);

        // The pre-CB-203 key is still written, from the Claude Code entry,
        // so an older build reading this file keeps its account.
        Assert.Equal(".claude-work", (string?)root["newChatLastProfile"]);
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Grok")]
    public void ClearingOneCliLastAccountLeavesTheOthersAlone(string cliName)
    {
        var cli = Enum.Parse<NewChatCli>(cliName);
        FreshSettings();

        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        OrbweaverSettings.SetNewChatLastProfile(cli, ".other-work");
        OrbweaverSettings.SetNewChatLastProfile(cli, "  ");

        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(cli));
        Assert.Equal(".claude-work", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    // A settings file written before CB-203 has only newChatLastProfile. It
    // stays Claude Code's, and Codex and Grok start with nothing remembered
    // rather than inheriting it.
    [Fact]
    public void APreCb203FileKeepsItsAccountForClaudeCodeOnly()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"newChatLastProfile\": \".claude-board\" }");

        OrbweaverSettings.ReloadForTests();

        Assert.Equal(".claude-board", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.Codex));
        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.Grok));
    }

    // The map wins over the legacy key when both name Claude Code's account,
    // and a key that is not a NewChatCli name is dropped on load.
    [Fact]
    public void TheMapWinsOverTheLegacyKeyAndUnknownCliNamesAreDropped()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"newChatLastProfile\": \".claude-old\", "
            + "\"newChatLastProfiles\": { \"ClaudeCode\": \".claude-new\", \"codex\": \".lowercase\", "
            + "\"0\": \".numeric\", \"OpenClaw\": \".nope\", \"Grok\": \" \" } }");

        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.SetNewChatLastCli("Codex");

        Assert.Equal(".claude-new", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.Codex));
        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.Grok));

        var map = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!
            ["newChatLastProfiles"]!.AsObject();
        Assert.Equal(new[] { "ClaudeCode" }, map.Select(p => p.Key));
    }

    // Choosing Default for Claude Code clears both the map entry and the
    // legacy key, so the legacy key cannot resurrect the old pick on reload.
    [Fact]
    public void ClearingClaudeCodeClearsTheLegacyKeyToo()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;

        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, null);
        OrbweaverSettings.ReloadForTests();

        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        Assert.Null(root["newChatLastProfile"]);
    }

    // CB-316: the account a phone launch runs under when the phone names none.
    [Fact]
    public void ThePhoneLaunchDefaultsSurviveARestartPerCliAndCanBeCleared()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;
        Assert.Null(OrbweaverSettings.PhoneLaunchProfileFor(NewChatCli.ClaudeCode));

        OrbweaverSettings.SetPhoneLaunchProfile(NewChatCli.ClaudeCode, ".claude-uplift");
        OrbweaverSettings.SetPhoneLaunchProfile(NewChatCli.Codex, "default");

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        Assert.Equal(".claude-uplift", OrbweaverSettings.PhoneLaunchProfileFor(NewChatCli.ClaudeCode));
        Assert.Equal("default", OrbweaverSettings.PhoneLaunchProfileFor(NewChatCli.Codex));
        Assert.Null(OrbweaverSettings.PhoneLaunchProfileFor(NewChatCli.Grok));
        Assert.Contains("\"phoneLaunchProfiles\"", File.ReadAllText(Path.Combine(dir, "settings.json")));

        OrbweaverSettings.SetPhoneLaunchProfile(NewChatCli.ClaudeCode, "  ");
        Assert.Null(OrbweaverSettings.PhoneLaunchProfileFor(NewChatCli.ClaudeCode));
    }

    [Fact]
    public void AHandEditedPhoneLaunchKeyForAnUnknownCliIsDroppedNotKept()
    {
        FreshSettings();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"phoneLaunchProfiles\":{\"Gemini\":\".x\",\"ClaudeCode\":\".claude-kmart\"}}");
        OrbweaverSettings.ReloadForTests();
        Assert.Equal(".claude-kmart", OrbweaverSettings.PhoneLaunchProfileFor(NewChatCli.ClaudeCode));
    }
}
