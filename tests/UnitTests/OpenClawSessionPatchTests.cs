using Xunit;

namespace Orbweaver.Tests;

// CB-270: the one builder every sessions.patch goes through. What it may send is
// the whole point, so the tests pin both halves: the exact shape of each request
// and the closed set of fields all of them draw from.
public class OpenClawSessionPatchTests
{
    private static string[] Keys(OpenClawSessionPatch patch) => patch.Params.Keys.ToArray();

    // Exact ordered keys: this is the order on the wire, and for the archive it
    // is the order the app has always sent.
    [Fact]
    public void ArchiveIsKeyArchivedExpectedSessionId()
    {
        var patch = OpenClawSessionPatch.Archive("agent:a:dashboard:u", "sid-1");

        Assert.Equal(new[] { "key", "archived", "expectedSessionId" }, Keys(patch));
        Assert.Equal("agent:a:dashboard:u", patch.Params["key"]);
        Assert.Equal(true, patch.Params["archived"]);
        Assert.Equal("sid-1", patch.Params["expectedSessionId"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PinIsKeyPinnedExpectedSessionId(bool pinned)
    {
        var patch = OpenClawSessionPatch.Pin("k", "sid-2", pinned);

        Assert.Equal(new[] { "key", "pinned", "expectedSessionId" }, Keys(patch));
        Assert.Equal(pinned, patch.Params["pinned"]);
        Assert.Equal("sid-2", patch.Params["expectedSessionId"]);
    }

    [Fact]
    public void RenameIsKeyLabelExpectedSessionId()
    {
        var patch = OpenClawSessionPatch.Rename("k", "sid-3", "Planning");

        Assert.Equal(new[] { "key", "label", "expectedSessionId" }, Keys(patch));
        Assert.Equal("Planning", patch.Params["label"]);
        Assert.Equal("sid-3", patch.Params["expectedSessionId"]);
    }

    // The union of what the factories build stays inside the declared set.
    [Fact]
    public void EveryFactoryStaysInsideTheDeclaredFields()
    {
        var all = new[]
        {
            OpenClawSessionPatch.Archive("k", "s"),
            OpenClawSessionPatch.Pin("k", "s", true),
            OpenClawSessionPatch.Rename("k", "s", "n"),
        };

        Assert.Subset(OpenClawSessionPatch.Fields.ToHashSet(), all.SelectMany(Keys).ToHashSet());
    }

    // The admin-gated fields are never reachable from here.
    [Fact]
    public void NeitherSandboxNorPermissionModeIsSendable()
    {
        Assert.DoesNotContain("sandboxMode", OpenClawSessionPatch.Fields);
        Assert.DoesNotContain("permissionMode", OpenClawSessionPatch.Fields);
    }

    // The gateway's declared write-scope set, read from 2026.10.5's
    // SESSIONS_PATCH_WRITE_SCOPE_MUTATIONS and recorded in
    // docs/openclaw-findings.md (CB-270). Everything this builder may send
    // beyond the envelope must be in it, or the call would need admin.
    private static readonly HashSet<string> GatewayWriteScopeMutations_2026_10_5 = new()
    {
        "label", "autoLabel", "icon", "color", "category", "boardFace", "boardPresentation",
        "pinned", "archived", "snoozedUntil", "unread", "model", "agentRuntime",
        "thinkingLevel", "fastMode", "permissionMode",
    };

    [Fact]
    public void TheFieldsBeyondTheEnvelopeAreAllWriteScope()
    {
        var envelope = new HashSet<string> { "key", "expectedSessionId" };

        var mutations = OpenClawSessionPatch.Fields.Where(f => !envelope.Contains(f)).ToHashSet();

        Assert.Subset(GatewayWriteScopeMutations_2026_10_5, mutations);
        // Six since Stage 4 added a native session's run controls; the set is
        // still pinned exactly, so a seventh cannot arrive unnoticed.
        Assert.Equal(
            new HashSet<string> { "archived", "pinned", "label", "model", "thinkingLevel", "fastMode" },
            mutations);
    }

    // --- run controls (Stage 4) ---

    [Fact]
    public void ModelIsKeyModelExpectedSessionId()
    {
        var patch = OpenClawSessionPatch.Model("k", "sid", "claude-haiku-4-5");

        Assert.Equal(new[] { "key", "model", "expectedSessionId" }, Keys(patch));
        Assert.Equal("claude-haiku-4-5", patch.Params["model"]);
        Assert.Equal("sid", patch.Params["expectedSessionId"]);
    }

    [Fact]
    public void ThinkingIsKeyThinkingLevelExpectedSessionId()
    {
        var patch = OpenClawSessionPatch.Thinking("k", "sid", "xhigh");

        Assert.Equal(new[] { "key", "thinkingLevel", "expectedSessionId" }, Keys(patch));
        Assert.Equal("xhigh", patch.Params["thinkingLevel"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FastIsKeyFastModeExpectedSessionId(bool on)
    {
        var patch = OpenClawSessionPatch.Fast("k", "sid", on);

        Assert.Equal(new[] { "key", "fastMode", "expectedSessionId" }, Keys(patch));
        Assert.Equal(on, patch.Params["fastMode"]);
    }

    // A blank value is refused here, never sent as an empty string.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankModelOrLevelIsRefused(string blank)
    {
        Assert.Throws<ArgumentException>(() => OpenClawSessionPatch.Model("k", "s", blank));
        Assert.Throws<ArgumentException>(() => OpenClawSessionPatch.Thinking("k", "s", blank));
    }

    // The three new factories stay inside the declared fields, and the fields
    // added for them are exactly the three, none of them an admin one.
    [Fact]
    public void TheRunControlFactoriesStayInsideTheDeclaredFields()
    {
        var all = new[]
        {
            OpenClawSessionPatch.Model("k", "s", "m"),
            OpenClawSessionPatch.Thinking("k", "s", "low"),
            OpenClawSessionPatch.Fast("k", "s", true),
        };

        Assert.Subset(OpenClawSessionPatch.Fields.ToHashSet(), all.SelectMany(Keys).ToHashSet());
        Assert.Equal(
            new[] { "fastMode", "model", "thinkingLevel" },
            OpenClawSessionPatch.Fields.Except(new[] { "key", "expectedSessionId", "archived", "pinned", "label" })
                .OrderBy(f => f, StringComparer.Ordinal));
    }
}

