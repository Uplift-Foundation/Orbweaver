using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-404: a cloud session's model and effort. The reading is the events rows'
// model and effort, the roster's external_metadata.model before any row; a set
// goes over the session's own control channel and is pending until a row
// confirms it.
public class ClaudeCloudRunTests
{
    private sealed class CloudRunApi : ICloudApi
    {
        public int Status = 200;
        public string Body = "";
        public readonly List<CloudRequestContext> Requests = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            return Task.FromResult(new CloudApiResult(CloudOutcomes.OutcomeFor(Status, Body), Body));
        }
    }

    private sealed class Creds : ICloudCredentialSource
    {
        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => new(CredentialOutcome.Found, "tok-123", null, "a credential is present");
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly RunCatalogue Catalogue = new(new[]
    {
        new RunModel("opus", "Opus 5.5", "claude-opus-5-5", new[] { "low", "medium", "high", "xhigh", "max" }),
        new RunModel("sonnet", "Sonnet 5.5", "claude-sonnet-5-5", new[] { "low", "medium", "high", "xhigh", "max" }),
    });

    private static ClaudeCloudSessions.Session Row(string? model = "claude-opus-5-5") =>
        new("session_a", "a cloud session", "idle", new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/session_a", "idle", false, model, null, null, null);

    private static ClaudeCloudChatSession Chat(CloudRunApi api, ClaudeCloudSessions.Session? row = null, bool enabled = true) =>
        new(row ?? Row(), api, new Creds(), action => action())
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Enabled = () => enabled,
            RunCatalogue = () => Catalogue,
            RunNow = () => T0,
        };

    private static string Body(CloudRequestContext c) => c.Body ?? "";

    [Fact]
    public void TheRostersModelIsTheReadingBeforeAnyRow()
    {
        var o = Chat(new CloudRunApi()).RunOptions!;

        Assert.Equal("opus", o.ModelId);
        Assert.Equal(RunReadingSource.CloudRoster, o.Source);
        Assert.Equal($"Opus 5.5 · {RunText.CloudEffortUnreported}", o.ChipText);
        Assert.DoesNotContain(o.ThinkingChoices, c => c.Id == "max");
        Assert.Null(o.ModelDisabledReason);
        Assert.False(o.HasFast);
    }

    [Fact]
    public void ARowWinsOverTheRosterAndARosterUpdateDoesNotUndoIt()
    {
        var chat = Chat(new CloudRunApi());
        var changed = 0;
        chat.RunOptionsChanged += () => changed++;

        chat.ObserveRun(new RunReading("claude-sonnet-5-5", "high", T0, RunReadingSource.CloudEvents));
        chat.UpdateStatus(Row("claude-opus-5-5"));

        Assert.Equal(("sonnet", "high"), (chat.RunOptions!.ModelId, chat.RunOptions.ThinkingId));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ASwitchedOffCloudDisablesBothWithTheReason()
    {
        var o = Chat(new CloudRunApi(), enabled: false).RunOptions!;

        Assert.Equal(ClaudeCloudChatSession.CloudOffReason, o.ModelDisabledReason);
        Assert.Equal(ClaudeCloudChatSession.CloudOffReason, o.EffortDisabledReason);
    }

    [Fact]
    public async Task SettingTheModelPostsSetModelAndIsPendingUntilARowAgrees()
    {
        var api = new CloudRunApi();
        var chat = Chat(api);

        Assert.Null(await chat.SetModelAsync("sonnet"));

        Assert.Contains("\"subtype\":\"set_model\"", Body(api.Requests.Single()));
        Assert.Contains("\"model\":\"sonnet\"", Body(api.Requests.Single()));
        Assert.True(chat.RunOptions!.ModelPending);

        chat.ObserveRun(new RunReading("claude-sonnet-5-5", null, T0.AddSeconds(5), RunReadingSource.CloudEvents));
        Assert.False(chat.RunOptions!.ModelPending);
        Assert.Equal("sonnet", chat.RunOptions.ModelId);
    }

    [Fact]
    public async Task SettingEffortPostsApplyFlagSettings()
    {
        var api = new CloudRunApi();
        var chat = Chat(api);

        Assert.Null(await chat.SetThinkingAsync("high"));

        Assert.Contains("\"settings\":{\"effortLevel\":\"high\"}", Body(api.Requests.Single()));
    }

    [Fact]
    public async Task ARowThatDisagreesIsAFailureUnderItsField()
    {
        var chat = Chat(new CloudRunApi());
        await chat.SetThinkingAsync("high");

        chat.ObserveRun(new RunReading(null, "low", T0.AddSeconds(5), RunReadingSource.CloudEvents));

        Assert.Equal(RunText.Contradicted(RunField.Effort, "high", "low"), chat.RunOptions!.EffortFailure);
        Assert.Null(chat.RunOptions.ModelFailure);

        // A new set of the field, made after that row, clears it.
        chat.RunNow = () => T0.AddSeconds(10);
        await chat.SetThinkingAsync("medium");
        Assert.Null(chat.RunOptions!.EffortFailure);
    }

    [Fact]
    public async Task MaxIsRefusedOnACloudSessionBeforeAnythingIsSent()
    {
        var api = new CloudRunApi();
        var chat = Chat(api);

        Assert.NotNull(await chat.SetThinkingAsync("max"));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task TheWorkersRefusalComesBack()
    {
        var api = new CloudRunApi { Status = 403 };
        var chat = Chat(api);

        var why = await chat.SetModelAsync("sonnet");

        Assert.NotNull(why);
        Assert.False(chat.RunOptions!.ModelPending);
    }

    [Fact]
    public async Task ADisabledControlAndANullChoiceSendNothing()
    {
        var api = new CloudRunApi();

        Assert.Equal(ClaudeCloudChatSession.CloudOffReason, await Chat(api, enabled: false).SetModelAsync("sonnet"));
        Assert.Equal("name a model", await Chat(api).SetModelAsync(null));
        Assert.Equal("name an effort level", await Chat(api).SetThinkingAsync(null));
        Assert.Equal(RunText.NoRoute, await Chat(api).SetFastModeAsync(true));
        var chat = Chat(api);
        chat.TimeoutMinutes = 3;
        Assert.Null(chat.TimeoutMinutes);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void ARosterWithNoModelSaysTheModelIsNotKnown()
    {
        var o = Chat(new CloudRunApi(), Row(model: null)).RunOptions!;

        Assert.Null(o.ModelId);
        Assert.StartsWith(RunText.ModelUnknown, o.ChipText);
    }
}
