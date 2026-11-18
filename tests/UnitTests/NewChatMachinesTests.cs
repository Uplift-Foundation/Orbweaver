using Xunit;

namespace Orbweaver.Tests;

public class NewChatMachinesTests
{
    private static PeerLaunchInfo Info(bool allowed = true, bool terminal = true, string[]? installed = null,
        string[]? recents = null, string? home = "/Users/w", params (string cli, string id, string label)[] accounts) =>
        new(allowed, "macos", home, terminal,
            (installed ?? ["claude", "codex"]).Select(c => new PeerLaunchCli(c, true)).Append(new PeerLaunchCli("grok", false)).ToList(),
            accounts.Select(a => new PeerLaunchAccount(a.cli, a.id, a.label)).ToList(),
            recents?.ToList());

    [Fact]
    public void No_row_when_the_link_is_off_or_nobody_is_paired()
    {
        Assert.Empty(NewChatMachines.Build(false, [new("mini", true)], false));
        Assert.Empty(NewChatMachines.Build(true, [], false));
    }

    [Theory]
    [InlineData(false, "This Mac")]
    [InlineData(true, "This PC")]
    public void Self_comes_first_with_the_platforms_name(bool windows, string label)
    {
        var list = NewChatMachines.Build(true, [new("mini", true)], windows);
        Assert.Equal(label, list[0].Label);
        Assert.True(list[0].IsSelf);
        Assert.True(list[0].Selectable);
        Assert.False(list[1].IsSelf);
    }

    [Fact]
    public void Online_peers_then_offline_each_sorted_and_offline_marked_and_unselectable()
    {
        var list = NewChatMachines.Build(true, [new("zed", false), new("beta", true), new("Alpha", false), new("alpha2", true)], false);
        Assert.Equal(["This Mac", "alpha2", "beta", "Alpha — offline", "zed — offline"], list.Select(e => e.Label));
        Assert.Equal([true, true, true, false, false], list.Select(e => e.Selectable));
        Assert.Equal("Alpha", list[3].Name);
    }

    [Fact]
    public void Loading_scope_disables_start_and_says_asking()
    {
        var s = NewChatMachines.Loading("mini");
        Assert.False(s.StartEnabled);
        Assert.Equal("Asking mini…", s.Note);
        Assert.Empty(s.Folders());
        Assert.Empty(s.Accounts("claude"));
        Assert.False(s.CliEnabled("claude"));
    }

    [Fact]
    public void No_answer_and_not_allowed_disable_start_with_a_reason()
    {
        var none = NewChatMachines.From("mini", null);
        Assert.Equal(NewChatMachines.InfoState.NoAnswer, none.State);
        Assert.False(none.StartEnabled);
        Assert.Contains("didn't answer", none.Note);
        Assert.Empty(none.Folders());

        var off = NewChatMachines.From("mini", Info(allowed: false));
        Assert.Equal(NewChatMachines.InfoState.NotAllowed, off.State);
        Assert.False(off.StartEnabled);
        Assert.Contains("doesn't allow other desktops", off.Note);
        Assert.False(off.CliEnabled("claude"));
        Assert.Empty(off.Folders());
        Assert.Empty(off.Accounts("claude"));
    }

    [Fact]
    public void Ready_scope_has_no_note_and_enables_installed_clis_only()
    {
        var s = NewChatMachines.From("mini", Info());
        Assert.True(s.StartEnabled);
        Assert.Null(s.Note);
        Assert.True(s.CliEnabled("claude"));
        Assert.True(s.CliEnabled("codex"));
        Assert.False(s.CliEnabled("grok"));      // listed, not installed
        Assert.False(s.CliEnabled("gemini"));    // not listed at all
    }

    [Fact]
    public void A_ready_machine_with_nowhere_or_nothing_to_start_says_so_and_disables_start()
    {
        var noTerm = NewChatMachines.From("m", Info(terminal: false));
        Assert.False(noTerm.StartEnabled);
        Assert.Equal(PeerLaunchCopy.NoTerminal("m"), noTerm.Note);

        var noCli = NewChatMachines.From("m", Info(installed: []));
        Assert.False(noCli.AnyCli);
        Assert.False(noCli.StartEnabled);
        Assert.Equal("No supported CLI is installed on m.", noCli.Note);
    }

    [Fact]
    public void No_terminal_or_no_cli_list_enables_nothing()
    {
        Assert.False(NewChatMachines.From("m", Info(terminal: false)).CliEnabled("claude"));
        Assert.False(NewChatMachines.From("m", new PeerLaunchInfo(true, "macos")).CliEnabled("claude"));
    }

    [Fact]
    public void Folders_are_the_recents_or_the_home_when_there_are_none()
    {
        Assert.Equal(["/a", "/b"], NewChatMachines.From("m", Info(recents: ["/a", "/b"])).Folders());
        Assert.Equal(["/Users/w"], NewChatMachines.From("m", Info(recents: [])).Folders());
        Assert.Equal(["/Users/w"], NewChatMachines.From("m", Info(recents: null)).Folders());
        Assert.Empty(NewChatMachines.From("m", Info(recents: [], home: null)).Folders());
        Assert.Empty(NewChatMachines.From("m", Info(recents: [], home: "")).Folders());
    }

    [Fact]
    public void Accounts_are_filtered_per_cli()
    {
        var s = NewChatMachines.From("m", Info(accounts: [("claude", "default", "Default"), ("claude", "/p/work", "Work"), ("codex", "default", "Default")]));
        Assert.Equal(["Default", "Work"], s.Accounts("claude").Select(a => a.Label));
        Assert.Single(s.Accounts("codex"));
        Assert.Empty(s.Accounts("grok"));
        Assert.Empty(NewChatMachines.From("m", new PeerLaunchInfo(true, "macos")).Accounts("claude"));
    }
}
