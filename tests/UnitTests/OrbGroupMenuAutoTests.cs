using Xunit;

namespace Orbweaver.Tests;

// CB-281: the menu context resolves the auto group from the team ROOT, like every other
// group decision (CB-268 T1), and takes the classifier as a function so it needs no disk.
[Collection("Settings")]
public class OrbGroupMenuAutoTests
{
    private const string Auto = "a-cli-0123456789ab";

    private static IReadOnlyDictionary<string, SessionStatus> Of(params (string Id, SessionStatus Status)[] rows) =>
        rows.ToDictionary(r => r.Id, r => r.Status, StringComparer.Ordinal);

    private static SessionStatus Row(string title, string lead = "") =>
        new() { Cwd = "/cb281-auto/" + Guid.NewGuid(), Title = title, Lead = lead };

    [Fact]
    public void ContextForUsesTheClassifiersAnswerForTheRoot()
    {
        var statuses = Of(("root", Row("root")));
        var context = OrbGroupMenu.ContextFor("root", new[] { "root" }, statuses,
            ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats, _ => Auto);
        Assert.Equal(Auto, context.GroupId);
    }

    [Fact]
    public void WithoutAClassifierTheRootIsInMain()
    {
        var statuses = Of(("root", Row("root")));
        var context = OrbGroupMenu.ContextFor("root", new[] { "root" }, statuses,
            ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats);
        Assert.Equal(OrbGroups.Main, context.GroupId);
    }

    [Fact]
    public void AMissingRootStatusIsNeverAskedAndFallsToMain()
    {
        var asked = false;
        var context = OrbGroupMenu.ContextFor("ghost", new[] { "ghost" }, Of(),
            ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats, _ => { asked = true; return Auto; });
        Assert.False(asked);
        Assert.Equal(OrbGroups.Main, context.GroupId);
    }

    [Fact]
    public void ResolveClassifiesTheTeamRootNotTheMember()
    {
        var root = Row("root");
        var member = Row("member", lead: "root");
        var statuses = Of(("root", root), ("member", member));
        var asked = new List<SessionStatus>();

        var context = OrbGroupMenu.Resolve("member", statuses,
            ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats,
            s => { asked.Add(s); return Auto; });

        Assert.Equal(Auto, context.GroupId);
        Assert.Single(asked);
        Assert.Same(root, asked[0]);
    }
}
