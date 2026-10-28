using Avalonia.Headless.XUnit;
using Xunit;
using static Orbweaver.Tests.AccountOrbEditorTestSupport;

namespace Orbweaver.Tests;

// CB-287: the shared editor behind both "Customize…" and the Settings rows,
// driven through the real AccountOrbCustomization (settings.json lives in the
// per-run scratch directory TestBootstrap sets up), so a case here proves the
// write reached the model and not merely that a box held some text.
[Collection("Settings")]
public class AccountOrbEditorPanelTests : IDisposable
{
    private readonly AccountOrbIdentity _account;
    private readonly string _dir = TempDir();

    public AccountOrbEditorPanelTests()
    {
        _account = Account(Guid.NewGuid().ToString("N")[..8]);
        AccountOrbCustomization.Reset(_account.PositionKey);
        AccountOrbEditorPanel.ChooseIconFileForTests = null;
    }

    public void Dispose()
    {
        AccountOrbEditorPanel.ChooseIconFileForTests = null;
        AccountOrbCustomization.Reset(_account.PositionKey);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public void Initials_CommitOnEnter()
    {
        var panel = new AccountOrbEditorPanel(_account);
        panel.InitialsBox.Text = "AC";
        PressEnter(panel.InitialsBox);

        Assert.Equal("AC", AccountOrbCustomization.Current(_account.PositionKey)?.Initials);
    }

    [AvaloniaFact]
    public void Initials_CommitOnLostFocus()
    {
        var panel = new AccountOrbEditorPanel(_account);
        panel.InitialsBox.Text = "Zq";
        LoseFocus(panel.InitialsBox);

        Assert.Equal("Zq", AccountOrbCustomization.Current(_account.PositionKey)?.Initials);
    }

    // Negative control for the two above: typing alone writes nothing, which is
    // the whole point of committing on Enter/LostFocus rather than per keystroke.
    [AvaloniaFact]
    public void Initials_NotCommittedWhileTyping()
    {
        var panel = new AccountOrbEditorPanel(_account);
        panel.InitialsBox.Text = "AC";

        Assert.Null(AccountOrbCustomization.Current(_account.PositionKey));
    }

    [AvaloniaFact]
    public void Initials_ClippedOnCommit()
    {
        var panel = new AccountOrbEditorPanel(_account);
        panel.InitialsBox.Text = "ABCD";
        PressEnter(panel.InitialsBox);

        Assert.Equal("AB", AccountOrbCustomization.Current(_account.PositionKey)?.Initials);
        Assert.Equal("AB", panel.InitialsBox.Text);
    }

    [AvaloniaFact]
    public void ClearingInitials_RemovesOverride()
    {
        var panel = new AccountOrbEditorPanel(_account);
        panel.InitialsBox.Text = "AC";
        PressEnter(panel.InitialsBox);
        panel.InitialsBox.Text = "";
        PressEnter(panel.InitialsBox);

        Assert.Null(AccountOrbCustomization.Current(_account.PositionKey));
    }

    [AvaloniaFact]
    public void Name_CommitsOnEnterAndLostFocus()
    {
        var panel = new AccountOrbEditorPanel(_account);
        panel.NameBox.Text = "Acme Corp";
        PressEnter(panel.NameBox);
        Assert.Equal("Acme Corp", AccountOrbCustomization.Current(_account.PositionKey)?.Name);

        panel.NameBox.Text = "Acme Labs";
        LoseFocus(panel.NameBox);
        Assert.Equal("Acme Labs", AccountOrbCustomization.Current(_account.PositionKey)?.Name);
    }

    // Enter and then LostFocus (the normal way to leave a box) is one write.
    [AvaloniaFact]
    public void Commit_EnterThenLostFocus_WritesOnce()
    {
        var panel = new AccountOrbEditorPanel(_account);
        var changes = new List<string>();
        void Handler(string key) => changes.Add(key);
        AccountOrbCustomization.Changed += Handler;
        try
        {
            panel.InitialsBox.Text = "AC";
            PressEnter(panel.InitialsBox);
            LoseFocus(panel.InitialsBox);
            panel.NameBox.Text = "Acme";
            PressEnter(panel.NameBox);
            LoseFocus(panel.NameBox);
        }
        finally
        {
            AccountOrbCustomization.Changed -= Handler;
        }

        Assert.Equal(2, changes.Count);
    }

    [AvaloniaFact]
    public async Task ChooseIcon_ImportsAndPreviews()
    {
        var png = WritePng(_dir);
        AccountOrbEditorPanel.ChooseIconFileForTests = () => Task.FromResult<string?>(png);
        var panel = new AccountOrbEditorPanel(_account);

        await panel.ChooseIconAsync();

        Assert.NotNull(AccountOrbCustomization.Current(_account.PositionKey)?.IconFile);
        Assert.True(panel.PreviewImage.IsVisible);
        Assert.NotNull(panel.PreviewImage.Source);
        Assert.False(panel.PreviewGlyph.IsVisible);
        Assert.True(panel.RemoveIconButton.IsEnabled);
        Assert.False(panel.ErrorText.IsVisible);
    }

    [AvaloniaFact]
    public async Task ChooseIcon_Cancelled_NoChange()
    {
        AccountOrbEditorPanel.ChooseIconFileForTests = () => Task.FromResult<string?>(null);
        var panel = new AccountOrbEditorPanel(_account);

        await panel.ChooseIconAsync();

        Assert.Null(AccountOrbCustomization.Current(_account.PositionKey));
        Assert.False(panel.PreviewImage.IsVisible);
        Assert.True(panel.PreviewGlyph.IsVisible);
    }

    [AvaloniaFact]
    public async Task ChooseIcon_Failure_ShowsErrorKeepsOld()
    {
        var good = WritePng(_dir);
        AccountOrbEditorPanel.ChooseIconFileForTests = () => Task.FromResult<string?>(good);
        var panel = new AccountOrbEditorPanel(_account);
        await panel.ChooseIconAsync();
        var before = AccountOrbCustomization.Current(_account.PositionKey)?.IconFile;

        var bad = Path.Combine(_dir, "notes.png");
        File.WriteAllText(bad, "this is not a picture");
        AccountOrbEditorPanel.ChooseIconFileForTests = () => Task.FromResult<string?>(bad);
        await panel.ChooseIconAsync();

        Assert.True(panel.ErrorText.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(panel.ErrorText.Text));
        Assert.Equal(before, AccountOrbCustomization.Current(_account.PositionKey)?.IconFile);
        Assert.True(panel.PreviewImage.IsVisible);
    }

    // The error clears as soon as the user does anything else — a stale
    // "isn't an image" under a working icon would read as still failing.
    [AvaloniaFact]
    public async Task Error_ClearsOnNextRefresh()
    {
        var bad = Path.Combine(_dir, "notes.png");
        File.WriteAllText(bad, "nope");
        AccountOrbEditorPanel.ChooseIconFileForTests = () => Task.FromResult<string?>(bad);
        var panel = new AccountOrbEditorPanel(_account);
        await panel.ChooseIconAsync();
        Assert.True(panel.ErrorText.IsVisible);

        panel.Reset();

        Assert.False(panel.ErrorText.IsVisible);
    }

    [AvaloniaFact]
    public async Task RemoveIcon_KeepsInitials()
    {
        AccountOrbEditorPanel.ChooseIconFileForTests = () => Task.FromResult<string?>(WritePng(_dir));
        var panel = new AccountOrbEditorPanel(_account);
        panel.InitialsBox.Text = "AC";
        PressEnter(panel.InitialsBox);
        await panel.ChooseIconAsync();

        panel.RemoveIcon();

        var over = AccountOrbCustomization.Current(_account.PositionKey);
        Assert.Equal("AC", over?.Initials);
        Assert.Null(over?.IconFile);
        Assert.False(panel.PreviewImage.IsVisible);
        Assert.False(panel.RemoveIconButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Reset_ClearsAll()
    {
        AccountOrbEditorPanel.ChooseIconFileForTests = () => Task.FromResult<string?>(WritePng(_dir));
        var panel = new AccountOrbEditorPanel(_account);
        panel.InitialsBox.Text = "AC";
        PressEnter(panel.InitialsBox);
        panel.NameBox.Text = "Acme";
        PressEnter(panel.NameBox);
        await panel.ChooseIconAsync();
        Assert.True(panel.ResetButton.IsEnabled);

        panel.Reset();

        Assert.Null(AccountOrbCustomization.Current(_account.PositionKey));
        Assert.Equal("", panel.InitialsBox.Text);
        Assert.Equal("", panel.NameBox.Text);
        Assert.False(panel.ResetButton.IsEnabled);
        Assert.True(panel.PreviewGlyph.IsVisible);
    }

    [AvaloniaFact]
    public void ButtonClicks_ReachTheirHandlers()
    {
        var panel = new AccountOrbEditorPanel(_account);
        panel.InitialsBox.Text = "AC";
        PressEnter(panel.InitialsBox);

        panel.ResetButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert.Null(AccountOrbCustomization.Current(_account.PositionKey));
    }

    [AvaloniaFact]
    public void PanelOnScreen_RefreshesWhenAnotherSurfaceChangesTheAccount()
    {
        var panel = new AccountOrbEditorPanel(_account);
        var window = new Avalonia.Controls.Window { Content = panel };
        window.Show();
        try
        {
            AccountOrbCustomization.SetInitials(_account.PositionKey, "XY");
            Assert.Equal("XY", panel.InitialsBox.Text);

            // Another account's change leaves this panel alone.
            AccountOrbCustomization.SetInitials(Account("other-" + Guid.NewGuid().ToString("N")[..6]).PositionKey, "ZZ");
            Assert.Equal("XY", panel.InitialsBox.Text);
        }
        finally
        {
            window.Close();
        }

        // Detached: no longer listening.
        AccountOrbCustomization.SetInitials(_account.PositionKey, "QQ");
        Assert.Equal("XY", panel.InitialsBox.Text);
    }

    [AvaloniaFact]
    public void NameBox_IsCapped()
    {
        var panel = new AccountOrbEditorPanel(_account);

        Assert.Equal(AccountOrbEditorPanel.MaxNameLength, panel.NameBox.MaxLength);
    }

    // Two surfaces on one account: committing initials in the second must not
    // wipe a name still being typed in the first.
    [AvaloniaFact]
    public void OtherSurfaceCommit_KeepsAHalfTypedDraft()
    {
        var first = new AccountOrbEditorPanel(_account);
        var second = new AccountOrbEditorPanel(_account);
        var window = new Avalonia.Controls.Window
        {
            Content = new Avalonia.Controls.StackPanel { Children = { first, second } }
        };
        window.Show();
        try
        {
            first.NameBox.Text = "half-typed draft";
            second.InitialsBox.Text = "AC";
            PressEnter(second.InitialsBox);

            Assert.Equal("AC", first.InitialsBox.Text);
            Assert.Equal("half-typed draft", first.NameBox.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // Negative control: a clean box does take the other surface's value, so the
    // draft rule above is not "never refresh".
    [AvaloniaFact]
    public void OtherSurfaceCommit_UpdatesACleanBox()
    {
        var first = new AccountOrbEditorPanel(_account);
        var second = new AccountOrbEditorPanel(_account);
        var window = new Avalonia.Controls.Window
        {
            Content = new Avalonia.Controls.StackPanel { Children = { first, second } }
        };
        window.Show();
        try
        {
            second.NameBox.Text = "Acme";
            PressEnter(second.NameBox);

            Assert.Equal("Acme", first.NameBox.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Reset_DiscardsDrafts()
    {
        var panel = new AccountOrbEditorPanel(_account);
        panel.NameBox.Text = "never committed";

        panel.Reset();

        Assert.Equal("", panel.NameBox.Text);
    }

    // Every failure has its own words; none falls through to a generic line.
    [Fact]
    public void EveryImportErrorHasItsOwnMessage()
    {
        var messages = Enum.GetValues<IconImportError>()
            .Select(e => AccountOrbEditorPanel.MessageFor(e))
            .ToList();

        Assert.All(messages, m => Assert.False(string.IsNullOrWhiteSpace(m)));
        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    // A value added to the enum without words fails loudly here rather than
    // quietly showing a generic line.
    [Fact]
    public void UnknownImportError_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountOrbEditorPanel.MessageFor((IconImportError)999));
}
