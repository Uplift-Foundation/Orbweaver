using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;
using static Orbweaver.Tests.AccountOrbEditorTestSupport;

namespace Orbweaver.Tests;

// CB-287's Settings half: a row per account, in the section of the CLI the
// account belongs to, only while that CLI's usage-orbs switch is on.
[Collection("Settings")]
public class SettingsWindowAccountOrbRowsTests : IDisposable
{
    private readonly Func<IReadOnlyList<AccountOrbIdentity>> _savedAccounts = SettingsWindow.UsageAccounts;
    private readonly AccountOrbIdentity _claude = Account("row-claude-" + Guid.NewGuid().ToString("N")[..6], label: "claude-acme");
    private readonly AccountOrbIdentity _codex = Account("row-codex-" + Guid.NewGuid().ToString("N")[..6], AccountUsageSource.Codex, "codex-acme");
    private readonly AccountOrbIdentity _grok = Account("row-grok-" + Guid.NewGuid().ToString("N")[..6], AccountUsageSource.Grok, "grok-acme");

    public SettingsWindowAccountOrbRowsTests()
    {
        SettingsWindow.UsageAccounts = () => new[] { _claude, _codex, _grok };
        OrbweaverSettings.AccountUsageEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.CodexChatEnabled = true;
        OrbweaverSettings.CodexAccountUsageEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        OrbweaverSettings.GrokChatEnabled = true;
        OrbweaverSettings.GrokAccountUsageEnabled = true;
    }

    public void Dispose()
    {
        SettingsWindow.UsageAccounts = _savedAccounts;
        foreach (var a in new[] { _claude, _codex, _grok }) AccountOrbCustomization.Reset(a.PositionKey);
        OrbweaverSettings.AccountUsageEnabled = false;
        OrbweaverSettings.CodexAccountUsageEnabled = false;
        OrbweaverSettings.GrokAccountUsageEnabled = false;
    }

    private static SettingsWindow NewWindow() =>
        (SettingsWindow)(typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()")).Invoke(null);

    private static IEnumerable<AccountOrbEditorPanel> PanelsIn(IEnumerable<Control> rows) =>
        rows.SelectMany(r => r.GetLogicalDescendants().OfType<AccountOrbEditorPanel>());

    // Through the real sections rather than AccountOrbRows directly, so the
    // three places each CLI's section asks for its rows are the ones tested.
    [AvaloniaFact]
    public void RowPerAccount_InEachCliSection()
    {
        var window = NewWindow();

        foreach (var (section, label) in new[] { ("claude-code", "claude-acme"), ("codex", "codex-acme"), ("grok", "grok-acme") })
        {
            var panels = window.Sections[section].GetLogicalDescendants().OfType<AccountOrbEditorPanel>().ToList();
            Assert.Single(panels);
            Assert.Contains(label, Texts(new[] { window.Sections[section] }));
        }
    }

    [AvaloniaFact]
    public void EachSection_ListsOnlyItsOwnAccounts()
    {
        var window = NewWindow();

        Assert.Contains("claude-acme", Texts(window.AccountOrbRows(AccountUsageSource.ClaudeCode)));
        Assert.DoesNotContain("codex-acme", Texts(window.AccountOrbRows(AccountUsageSource.ClaudeCode)));
        Assert.Contains("codex-acme", Texts(window.AccountOrbRows(AccountUsageSource.Codex)));
        Assert.Contains("grok-acme", Texts(window.AccountOrbRows(AccountUsageSource.Grok)));
    }

    private static string Texts(IEnumerable<Control> rows) =>
        string.Join("|", rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>()).Select(t => t.Text));

    [AvaloniaFact]
    public void NoRows_WhenUsageSwitchOff()
    {
        OrbweaverSettings.AccountUsageEnabled = false;
        OrbweaverSettings.CodexAccountUsageEnabled = false;
        OrbweaverSettings.GrokAccountUsageEnabled = false;
        var window = NewWindow();

        foreach (var section in new[] { "claude-code", "codex", "grok" })
            Assert.Empty(window.Sections[section].GetLogicalDescendants().OfType<AccountOrbEditorPanel>());
    }

    [AvaloniaFact]
    public void NoAccountsYet_NoRows()
    {
        SettingsWindow.UsageAccounts = () => Array.Empty<AccountOrbIdentity>();

        Assert.Empty(NewWindow().AccountOrbRows(AccountUsageSource.ClaudeCode));
    }

    [AvaloniaFact]
    public void RowEdit_PersistsAndRaisesChanged()
    {
        var window = NewWindow();
        var panel = PanelsIn(window.AccountOrbRows(AccountUsageSource.Codex)).Single();
        var changed = new List<string>();
        void Handler(string key) => changed.Add(key);
        AccountOrbCustomization.Changed += Handler;
        try
        {
            panel.InitialsBox.Text = "CX";
            PressEnter(panel.InitialsBox);
        }
        finally
        {
            AccountOrbCustomization.Changed -= Handler;
        }

        Assert.Equal("CX", AccountOrbCustomization.Current(_codex.PositionKey)?.Initials);
        Assert.Equal(new[] { _codex.PositionKey }, changed);
    }

    // An open window keeps showing the truth when the other surface (the orb's
    // own Customize… window) changes an account — without rebuilding the page
    // under a text box someone is typing in.
    [AvaloniaFact]
    public void OpenWindow_RowsRefreshOnChanged()
    {
        var window = NewWindow();
        var panel = new AccountOrbEditorPanel(_grok);
        var host = new Window { Content = panel };
        host.Show();
        try
        {
            AccountOrbCustomization.SetName(_grok.PositionKey, "Grok Labs");

            Assert.Equal("Grok Labs", panel.NameBox.Text);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Rows_AreSearchableByAccountLabel()
    {
        var row = Assert.IsType<SettingsWindow.SettingsRow>(
            NewWindow().AccountOrbRows(AccountUsageSource.ClaudeCode).Single());

        Assert.Contains("claude-acme", row.SearchText);
    }
}
