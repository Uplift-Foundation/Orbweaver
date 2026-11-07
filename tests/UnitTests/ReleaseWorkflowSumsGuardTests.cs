using Xunit;

namespace Orbweaver.Tests;

// release.yml must run the checksum guard in the publish job, after the sums are
// generated and before the release is created. The script itself is behaviourally
// tested in IntegrationTests/ReleaseSumsGuardTests; this pins the wiring, which
// is the part nobody compiles and CI never runs.
public class ReleaseWorkflowSumsGuardTests
{
    [Fact]
    public void ThePublishJobRunsTheGuardBetweenGeneratingSumsAndCreatingTheRelease()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".github", "workflows", "release.yml")))
            root = root.Parent;
        var text = File.ReadAllText(Path.Combine(root!.FullName, ".github", "workflows", "release.yml"));

        var generate = text.IndexOf("sha256sum *.dmg", StringComparison.Ordinal);
        var guard = text.IndexOf("bash tools/check-release-sums.sh artifacts", StringComparison.Ordinal);
        var create = text.IndexOf("gh release create", StringComparison.Ordinal);
        if (create < 0) create = text.IndexOf("release create", StringComparison.Ordinal);

        Assert.True(generate > 0, "sums generation step not found");
        Assert.True(guard > generate, "guard must run after the sums are generated");
        Assert.True(create > guard, "guard must run before the release is created");
        Assert.True(File.Exists(Path.Combine(root.FullName, "tools", "check-release-sums.sh")));
    }
}
