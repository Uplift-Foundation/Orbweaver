using System.Text.Json;
using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-306: the routing table, one case per arm per channel, pinned to what
// the gate probes measured on 2026-11-03 (docs/*-findings.md, CB-306
// sections). A change here is a change to what a panel offers, so every arm
// is named rather than swept.
public class SlashRouteTests
{
    [Theory]
    [InlineData("/compact")]
    [InlineData("/color")]
    [InlineData("/rename")]
    [InlineData("/my-custom")]
    public void ATerminalTypesEverything(string name)
    {
        Assert.Equal(SlashRouteKind.Typed, SlashRouting.Resolve(SlashChannel.Terminal, name, "x").Kind);
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/new")]
    [InlineData("/reset")]
    [InlineData("/model")]
    public void OpenClawPassesEverythingThrough(string name)
    {
        // The gateway parses a leading slash in chat.send itself (measured on
        // 2026.10.10); what is offered is the gateway's own commands.list.
        Assert.Equal(SlashRouteKind.PassThrough, SlashRouting.Resolve(SlashChannel.OpenClaw, name, "").Kind);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("/color")]
    [InlineData("/my-custom")]
    public void APeerSocketRunsNothing(string name)
    {
        var route = SlashRouting.Resolve(SlashChannel.PeerSocket, name, "");

        Assert.Equal(SlashRouteKind.Unavailable, route.Kind);
        Assert.Equal(SlashRouting.PeerSocketReason, route.Reason);
    }

    [Theory]
    [InlineData("/color", "set_color")]
    [InlineData("/rename", "rename_session")]
    [InlineData("/model", "set_model")]
    [InlineData("/permissions", "set_permission_mode")]
    [InlineData("/think", "set_max_thinking_tokens")]
    [InlineData("/COLOR", "set_color")]
    public void TheBridgeRunsTheFiveControlRequests(string name, string subtype)
    {
        var route = SlashRouting.Resolve(SlashChannel.Bridge, name, "pink");

        Assert.Equal(SlashRouteKind.Control, route.Kind);
        Assert.Equal(subtype, route.ControlSubtype);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("/help")]
    [InlineData("/cb306probe")]
    [InlineData("/clear")]
    public void TheBridgeRefusesEverythingElseAsAPeerMessage(string name)
    {
        // Measured: every one of these, posted as a user event, arrived as
        // "Another Claude session sent a message" and was declined.
        var route = SlashRouting.Resolve(SlashChannel.Bridge, name, "");

        Assert.Equal(SlashRouteKind.Unavailable, route.Kind);
        Assert.Equal(SlashRouting.PeerMessageReason, route.Reason);
    }

    [Theory]
    [InlineData("/rename", "rename_session")]
    [InlineData("/model", "set_model")]
    [InlineData("/permissions", "set_permission_mode")]
    [InlineData("/think", "set_max_thinking_tokens")]
    public void TheCloudRunsFourControlRequests(string name, string subtype)
    {
        var route = SlashRouting.Resolve(SlashChannel.Cloud, name, "");

        Assert.Equal(SlashRouteKind.Control, route.Kind);
        Assert.Equal(subtype, route.ControlSubtype);
    }

    [Fact]
    public void TheCloudHasNoColour()
    {
        // Measured: "Unsupported control request subtype: set_color" from the
        // cloud worker, success from a bridge.
        var route = SlashRouting.Resolve(SlashChannel.Cloud, "/color", "pink");

        Assert.Equal(SlashRouteKind.Unavailable, route.Kind);
        Assert.Equal(SlashRouting.CloudNoColorReason, route.Reason);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("/help")]
    public void TheCloudRefusesEverythingElseAsAPeerMessage(string name)
    {
        var route = SlashRouting.Resolve(SlashChannel.Cloud, name, "");

        Assert.Equal(SlashRouteKind.Unavailable, route.Kind);
        Assert.Equal(SlashRouting.PeerMessageReason, route.Reason);
    }

    [Fact]
    public void AnUnknownChannelIsRefusedLoudly()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => SlashRouting.Resolve((SlashChannel)99, "/x", ""));
    }

    [Fact]
    public void HasControlNamesExactlyTheFive()
    {
        Assert.True(SlashRouting.HasControl("/color"));
        Assert.True(SlashRouting.HasControl("/Think"));
        Assert.False(SlashRouting.HasControl("/compact"));
    }

    // --- Split ---------------------------------------------------------------

    [Theory]
    [InlineData("/compact", "/compact", "")]
    [InlineData("/color pink", "/color", "pink")]
    [InlineData("  /rename  my   session  ", "/rename", "my   session")]
    [InlineData("/think\t8000", "/think", "8000")]
    [InlineData("/model\nopus", "/model", "opus")]
    public void SplitTakesTheFirstTokenAsTheCommand(string line, string name, string args)
    {
        var split = SlashRouting.Split(line);

        Assert.NotNull(split);
        Assert.Equal(name, split!.Value.Name);
        Assert.Equal(args, split.Value.Args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("hello /world")]
    [InlineData(null)]
    public void SplitRefusesALineThatIsNotACommand(string? line)
    {
        Assert.Null(SlashRouting.Split(line!));
    }

    // --- ControlRequest ------------------------------------------------------

    private static JsonElement Parse(System.Text.Json.Nodes.JsonObject o) =>
        JsonDocument.Parse(o.ToJsonString()).RootElement;

    [Fact]
    public void ColourNeedsAColour()
    {
        var ok = SlashRouting.ControlRequest("set_color", " pink ", out var problem);
        Assert.Null(problem);
        Assert.Equal("pink", Parse(ok!).GetProperty("color").GetString());
        Assert.Equal("set_color", Parse(ok!).GetProperty("subtype").GetString());

        Assert.Null(SlashRouting.ControlRequest("set_color", "", out problem));
        Assert.Contains("/color pink", problem);
    }

    [Fact]
    public void RenameNeedsATitle()
    {
        var ok = SlashRouting.ControlRequest("rename_session", "my session", out var problem);
        Assert.Null(problem);
        Assert.Equal("my session", Parse(ok!).GetProperty("title").GetString());

        Assert.Null(SlashRouting.ControlRequest("rename_session", "  ", out problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void ModelNeedsAName()
    {
        // No bare /model: a mapping of empty-to-default was never measured, so
        // it is refused rather than invented (Nadia's finding 2).
        Assert.Null(SlashRouting.ControlRequest("set_model", "", out var problem));
        Assert.Contains("/model opus", problem);

        Assert.Equal("opus", Parse(SlashRouting.ControlRequest("set_model", "opus", out _)!).GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("acceptEdits")]
    [InlineData("plan")]
    [InlineData("bypassPermissions")]
    public void PermissionsTakesAKnownMode(string mode)
    {
        Assert.Equal(mode, Parse(SlashRouting.ControlRequest("set_permission_mode", mode, out var problem)!).GetProperty("mode").GetString());
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plam")]        // a typo
    [InlineData("AcceptEdits")] // wrong case — the modes are case-sensitive
    [InlineData("ask")]         // a per-tool behaviour, not a session mode
    public void PermissionsRefusesAnythingElse(string mode)
    {
        // Validated here rather than after a round trip (Nadia's finding 3).
        Assert.Null(SlashRouting.ControlRequest("set_permission_mode", mode, out var problem));
        Assert.NotNull(problem);
        Assert.Contains("bypassPermissions", problem);
    }

    [Theory]
    [InlineData("", true, 0)]
    [InlineData("default", true, 0)]
    [InlineData("DEFAULT", true, 0)]
    [InlineData("8000", false, 8000)]
    [InlineData("0", false, 0)]
    public void ThinkIsABudgetOrNull(string args, bool expectNull, int budget)
    {
        var request = SlashRouting.ControlRequest("set_max_thinking_tokens", args, out var problem);

        Assert.Null(problem);
        var value = Parse(request!).GetProperty("max_thinking_tokens");
        if (expectNull) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(budget, value.GetInt32());
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("-5")]
    [InlineData("8.5")]
    public void ThinkRefusesANonBudget(string args)
    {
        Assert.Null(SlashRouting.ControlRequest("set_max_thinking_tokens", args, out var problem));
        Assert.Contains("/think 8000", problem);
    }

    [Fact]
    public void AnUnknownSubtypeHasNoRequest()
    {
        Assert.Null(SlashRouting.ControlRequest("interrupt", "", out var problem));
        Assert.Contains("interrupt", problem);
    }

    [Fact]
    public void TheRefusalNoteNamesTheCommandAndTheReason()
    {
        Assert.Equal("Not sent: /compact can't run from here — because.",
            SlashRouting.RefusalNote("/compact", "because"));
    }

    [Fact]
    public void ASlashCommandRoutesAsTypedUnlessToldOtherwise()
    {
        Assert.Equal(SlashRouteKind.Typed, new SlashCommand("/x", "").Route);
        Assert.Equal(SlashRouteKind.Control, new SlashCommand("/x", "", SlashRouteKind.Control).Route);
    }

    // --- NamesKnownCommand: the gate for "command vs message" (Nadia 1) -------

    private static readonly string[] Catalogue =
        { "/color", "/rename", "/compact", "/clear", "/my-custom" };

    [Theory]
    [InlineData("/color pink")]
    [InlineData("/compact")]         // known but may be Unavailable — still a command
    [InlineData("/CLEAR")]           // case-insensitive name match
    [InlineData("/my-custom args")]
    public void AKnownFirstTokenIsACommand(string line)
    {
        Assert.True(SlashRouting.NamesKnownCommand(line, Catalogue));
    }

    [Theory]
    [InlineData("/tmp/build.log has the error")]
    [InlineData("/Users/x/foo.cs fails")]
    [InlineData("/etc/hosts")]
    [InlineData("/ hello")]          // the name is just "/"
    [InlineData("/unknown-word do a thing")]
    [InlineData("hello /color")]     // slash not at the start
    [InlineData("")]
    [InlineData("/tmp/x\nsecond line")] // a multi-line message starting with "/"
    public void AnythingElseIsNotACommandAndIsSent(string line)
    {
        Assert.False(SlashRouting.NamesKnownCommand(line, Catalogue));
    }

    [Fact]
    public void AnEmptyCatalogueKnowsNoCommands()
    {
        Assert.False(SlashRouting.NamesKnownCommand("/color pink", System.Array.Empty<string>()));
    }

    // The alias-aware overload: a first token matching a Name or an Alias is a
    // command (so "/t" is "/think").
    [Theory]
    [InlineData("/think 5000", true)]
    [InlineData("/t 5000", true)]
    [InlineData("/thinking off", true)]
    [InlineData("/thonk", false)]
    [InlineData("/tmp/x.log", false)]
    public void AnAliasCountsAsAKnownCommand(string line, bool known)
    {
        var commands = new[]
        {
            new SlashCommand("/think", "", SlashRouteKind.Control, new[] { "/t", "/thinking" }),
            new SlashCommand("/color", "", SlashRouteKind.Control),
        };
        Assert.Equal(known, SlashRouting.NamesKnownCommand(line, commands));
    }

}
