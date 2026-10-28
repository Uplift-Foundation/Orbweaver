using Avalonia.Headless.XUnit;
using Xunit;
using static Orbweaver.Tests.AccountOrbEditorTestSupport;

namespace Orbweaver.Tests;

[Collection("Settings")]
public class AccountOrbEditorWindowTests
{
    [AvaloniaFact]
    public void Open_TwiceSameAccount_OneWindow()
    {
        var account = Account("win-" + Guid.NewGuid().ToString("N")[..6]);
        AccountOrbEditorWindow.Open(account, () => { });
        try
        {
            var first = AccountOrbEditorWindow.Find(account.PositionKey);
            Assert.NotNull(first);

            AccountOrbEditorWindow.Open(account, () => { });

            Assert.Same(first, AccountOrbEditorWindow.Find(account.PositionKey));
            Assert.Contains(account.DerivedLabel, first!.Title);
        }
        finally
        {
            AccountOrbEditorWindow.Find(account.PositionKey)?.Close();
        }

        Assert.Null(AccountOrbEditorWindow.Find(account.PositionKey));
    }

    [AvaloniaFact]
    public void Open_DifferentAccounts_TwoWindows()
    {
        var a = Account("a-" + Guid.NewGuid().ToString("N")[..6]);
        var b = Account("b-" + Guid.NewGuid().ToString("N")[..6]);
        AccountOrbEditorWindow.Open(a, () => { });
        AccountOrbEditorWindow.Open(b, () => { });
        try
        {
            Assert.NotSame(AccountOrbEditorWindow.Find(a.PositionKey), AccountOrbEditorWindow.Find(b.PositionKey));
        }
        finally
        {
            AccountOrbEditorWindow.Find(a.PositionKey)?.Close();
            AccountOrbEditorWindow.Find(b.PositionKey)?.Close();
        }
    }

    [AvaloniaFact]
    public void OnChanged_CalledForThisAccountOnly_AndNotAfterClose()
    {
        var account = Account("cb-" + Guid.NewGuid().ToString("N")[..6]);
        var other = Account("cbo-" + Guid.NewGuid().ToString("N")[..6]);
        var calls = 0;
        AccountOrbEditorWindow.Open(account, () => calls++);
        var window = AccountOrbEditorWindow.Find(account.PositionKey)!;
        try
        {
            AccountOrbCustomization.SetName(other.PositionKey, "Elsewhere");
            Assert.Equal(0, calls);

            AccountOrbCustomization.SetName(account.PositionKey, "Here");
            Assert.Equal(1, calls);
        }
        finally
        {
            window.Close();
            AccountOrbCustomization.Reset(account.PositionKey);
            AccountOrbCustomization.Reset(other.PositionKey);
        }

        // Closed: unsubscribed, so the write below calls nothing.
        AccountOrbCustomization.SetName(account.PositionKey, "After");
        Assert.Equal(1, calls);
        AccountOrbCustomization.Reset(account.PositionKey);
    }

    [AvaloniaFact]
    public void Escape_InACleanBox_ClosesTheWindow()
    {
        var account = Account("esc-" + Guid.NewGuid().ToString("N")[..6]);
        AccountOrbEditorWindow.Open(account, () => { });
        var window = AccountOrbEditorWindow.Find(account.PositionKey)!;

        PressEscape(window.Panel.NameBox);

        Assert.Null(AccountOrbEditorWindow.Find(account.PositionKey));
    }

    // First Escape reverts the draft and keeps the window; the second closes.
    [AvaloniaFact]
    public void Escape_WithADraft_RevertsFirstThenCloses()
    {
        var account = Account("escd-" + Guid.NewGuid().ToString("N")[..6]);
        AccountOrbEditorWindow.Open(account, () => { });
        var window = AccountOrbEditorWindow.Find(account.PositionKey)!;
        try
        {
            window.Panel.NameBox.Text = "draft";
            PressEscape(window.Panel.NameBox);
            Assert.NotNull(AccountOrbEditorWindow.Find(account.PositionKey));
            Assert.Equal("", window.Panel.NameBox.Text);

            PressEscape(window.Panel.NameBox);
            Assert.Null(AccountOrbEditorWindow.Find(account.PositionKey));
        }
        finally
        {
            window.Close();
        }
    }

    // No field focused: Escape reaches the window itself.
    [AvaloniaFact]
    public void Escape_OnTheWindowItself_Closes()
    {
        var account = Account("escw-" + Guid.NewGuid().ToString("N")[..6]);
        AccountOrbEditorWindow.Open(account, () => { });
        var window = AccountOrbEditorWindow.Find(account.PositionKey)!;

        PressEscape(window);

        Assert.Null(AccountOrbEditorWindow.Find(account.PositionKey));
    }
}
