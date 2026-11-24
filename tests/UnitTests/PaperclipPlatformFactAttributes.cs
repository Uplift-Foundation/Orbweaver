using Xunit;

namespace Orbweaver.Tests;

// The Paperclip key file is protected by a different mechanism on each platform:
// an explicit owner-only ACL on Windows, mode 0600 elsewhere. A test for one that
// returned early on the other would report "passed" on a leg where nothing was
// checked, which is a claim about the suite that is not true. These skip instead,
// with a reason, the pattern RelaySecretStorePlatformTests and PlatformFactAttributes
// use, so each leg's log shows what it actually exercised.
public sealed class WindowsAclFactAttribute : FactAttribute
{
    public WindowsAclFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "an owner-only ACL is the Windows mechanism; the Unix mode tests cover the other platforms";
    }
}

public sealed class UnixModeFactAttribute : FactAttribute
{
    public UnixModeFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "POSIX file modes do not exist on Windows; the ACL tests cover it";
    }
}
