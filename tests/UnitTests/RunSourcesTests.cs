using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-404: RunSources, the cached readings and catalogues behind the live run
/// controls, over a fake disk and fake probes. The parsers themselves are
/// RunReadingsTests'; what is pinned here is what is read from where, that a
/// file is parsed only when it moved, and that no ask ever waits on a process.
/// </summary>
public class RunSourcesTests
{
    private sealed class Desk
    {
        public readonly Dictionary<string, (string Text, long Length, DateTime Written)> Files = new();
        public int Tails, Reads;
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public Func<string?, Task<string?>> Claude = _ => Task.FromResult<string?>(null);
        public Func<string?, Task<string?>> Codex = _ => Task.FromResult<string?>(null);
        public Func<Task<Version?>> CodexBinary = () => Task.FromResult<Version?>(null);
        public readonly List<string?> ClaudeAsked = [], CodexAsked = [];
        public int BinaryAsks;
        public RunSources Sources;

        public Desk()
        {
            Sources = new RunSources(new RunSources.Seams(
                p => Files.TryGetValue(p, out var f) ? (f.Length, f.Written) : null,
                p => { Tails++; return Files[p].Text.Split('\n', StringSplitOptions.RemoveEmptyEntries); },
                p => Files.TryGetValue(p, out var f) ? f.Text.Split('\n')[0] : null,
                p => { Reads++; return Files.TryGetValue(p, out var f) ? f.Text : null; },
                root => { ClaudeAsked.Add(root); return Claude(root); },
                home => { CodexAsked.Add(home); return Codex(home); },
                () => { BinaryAsks++; return CodexBinary(); },
                () => "/home/u",
                () => Now));
        }

        public void Write(string path, string text, int stamp = 1) =>
            Files[path] = (text, text.Length, DateTime.UnixEpoch.AddSeconds(stamp));
    }

    private static SessionStatus Of(SessionSource source, string path = "") =>
        new() { SessionId = "s", Source = source, TranscriptPath = path, Cwd = "/w" };

    private const string ClaudeRow =
        """{"type":"assistant","timestamp":"2026-11-21T10:00:00Z","effort":"high","message":{"role":"assistant","model":"claude-opus-5-5","content":[]}}""";

    private const string CodexMeta = """{"type":"session_meta","timestamp":"2026-11-21T10:00:00Z","payload":{"cli_version":"0.154.0"}}""";
    private const string CodexTurn = """{"type":"turn_context","timestamp":"2026-11-21T10:00:01Z","payload":{"model":"gpt-6-luna","effort":"low"}}""";

    [Fact]
    public void A_claude_reading_comes_from_the_transcript_and_is_parsed_again_only_when_it_moves()
    {
        var d = new Desk();
        d.Write("/c/t.jsonl", ClaudeRow);

        var r = d.Sources.ReadingFor(Of(SessionSource.ClaudeCode, "/c/t.jsonl"))!;
        Assert.Equal(("claude-opus-5-5", "high"), (r.Model, r.Effort));
        d.Sources.ReadingFor(Of(SessionSource.ClaudeCode, "/c/t.jsonl"));
        Assert.Equal(1, d.Tails);

        d.Write("/c/t.jsonl", ClaudeRow.Replace("high", "low"), stamp: 2);
        Assert.Equal("low", d.Sources.ReadingFor(Of(SessionSource.ClaudeCode, "/c/t.jsonl"))!.Effort);
        Assert.Equal(2, d.Tails);
    }

    [Fact]
    public void No_path_a_missing_file_or_a_session_kind_it_does_not_serve_reads_nothing()
    {
        var d = new Desk();
        Assert.Null(d.Sources.ReadingFor(Of(SessionSource.ClaudeCode)));
        Assert.Null(d.Sources.ReadingFor(Of(SessionSource.ClaudeCode, "/gone.jsonl")));
        d.Write("/o.jsonl", ClaudeRow);
        Assert.Null(d.Sources.ReadingFor(Of(SessionSource.OpenClaw, "/o.jsonl")));
        Assert.Null(d.Sources.CatalogueFor(Of(SessionSource.OpenClaw, "/o.jsonl")));
        Assert.Null(d.Sources.CodexVersionFor(Of(SessionSource.ClaudeCode, "/o.jsonl")));
    }

    [Fact]
    public void A_codex_reading_comes_from_the_rollout_and_its_version_from_the_first_line_once()
    {
        var d = new Desk();
        d.Write("/x/.codex/sessions/2026/11/21/rollout-1.jsonl", CodexMeta + "\n" + CodexTurn);
        var s = Of(SessionSource.Codex, "/x/.codex/sessions/2026/11/21/rollout-1.jsonl");

        Assert.Equal(("gpt-6-luna", "low"), (d.Sources.ReadingFor(s)!.Model, d.Sources.ReadingFor(s)!.Effort));
        Assert.Equal(new Version(0, 154, 0), d.Sources.CodexVersionFor(s));
        d.Files.Clear();                                   // cached: the first line never changes
        Assert.Equal(new Version(0, 154, 0), d.Sources.CodexVersionFor(s));
        Assert.Equal(0, d.BinaryAsks);
    }

    [Fact]
    public async Task A_codex_with_no_rollout_yet_takes_the_installed_binarys_version_from_the_background()
    {
        var d = new Desk();
        var answer = new TaskCompletionSource<Version?>();
        d.CodexBinary = () => answer.Task;
        var s = Of(SessionSource.Codex);

        Assert.Null(d.Sources.CodexVersionFor(s));         // never waits
        Assert.Null(d.Sources.CodexVersionFor(s));
        Assert.Equal(1, d.BinaryAsks);                     // one ask in flight at a time

        answer.SetResult(new Version(0, 167, 0));
        await d.Sources.LastProbe;
        Assert.Equal(new Version(0, 167, 0), d.Sources.CodexVersionFor(s));

        // A rollout without session_meta falls back to the binary too.
        d.Write("/r.jsonl", CodexTurn);
        Assert.Equal(new Version(0, 167, 0), d.Sources.CodexVersionFor(Of(SessionSource.Codex, "/r.jsonl")));
    }

    [Fact]
    public void A_binary_version_ask_that_fails_or_throws_is_retried_after_five_minutes()
    {
        var d = new Desk { CodexBinary = () => throw new InvalidOperationException() };
        Assert.Null(d.Sources.CodexVersionFor(Of(SessionSource.Codex)));
        Assert.Null(d.Sources.CodexVersionFor(Of(SessionSource.Codex)));
        Assert.Equal(1, d.BinaryAsks);

        d.CodexBinary = () => Task.FromResult<Version?>(new Version(0, 164, 0));
        d.Now += RunSources.RetryAfter + TimeSpan.FromSeconds(1);
        Assert.Equal(new Version(0, 164, 0), d.Sources.CodexVersionFor(Of(SessionSource.Codex)));
    }

    [Theory]
    [InlineData("codex-cli 0.154.0", "0.154.0")]
    [InlineData("codex-cli 0.167.0\n", "0.167.0")]
    [InlineData("nothing", null)]
    [InlineData(null, null)]
    public void The_binary_version_is_read_out_of_its_own_line(string? text, string? version) =>
        Assert.Equal(version is null ? null : Version.Parse(version), RunSources.ParseCliVersion(text));

    [Fact]
    public void A_grok_reading_comes_from_the_summary_beside_the_updates_file()
    {
        var d = new Desk();
        d.Write("/g/sess/summary.json", """{"current_model_id":"grok-4.7","reasoning_effort":"high"}""");
        var r = d.Sources.ReadingFor(Of(SessionSource.Grok, "/g/sess/updates.jsonl"))!;
        Assert.Equal(("grok-4.7", "high"), (r.Model, r.Effort));
        Assert.Null(RunSources.GrokSummaryOf("updates.jsonl"));
    }

    private const string ClaudeModels =
        """{"type":"control_response","response":{"subtype":"success","request_id":"cb404-models","response":{"models":[{"value":"opus","displayName":"Opus 5.5","resolvedModel":"claude-opus-5-5","supportsEffort":true,"supportedEffortLevels":["low","high"]}]}}}""";

    [Fact]
    public async Task A_claude_catalogue_is_the_aliases_until_list_models_answers_then_its_answer_for_an_hour()
    {
        var d = new Desk();
        var answer = new TaskCompletionSource<string?>();
        d.Claude = _ => answer.Task;
        var s = Of(SessionSource.ClaudeCode, "/u/.claude-b/projects/p/t.jsonl");

        Assert.Same(RunSources.ClaudeFallback, d.Sources.CatalogueFor(s));
        Assert.Same(RunSources.ClaudeFallback, d.Sources.CatalogueFor(s));
        Assert.Single(d.ClaudeAsked);

        answer.SetResult(ClaudeModels);
        await d.Sources.LastProbe;
        Assert.Equal("opus", Assert.Single(d.Sources.CatalogueFor(s)!.Models).Id);

        d.Now += TimeSpan.FromMinutes(59);
        d.Sources.CatalogueFor(s);
        Assert.Single(d.ClaudeAsked);
        d.Now += TimeSpan.FromMinutes(2);
        d.Claude = _ => Task.FromResult<string?>(ClaudeModels);
        d.Sources.CatalogueFor(s);
        Assert.Equal(2, d.ClaudeAsked.Count);
    }

    [Fact]
    public void A_failed_or_empty_probe_keeps_the_last_answer_and_is_retried_after_five_minutes()
    {
        var d = new Desk { Claude = _ => throw new InvalidOperationException() };
        var s = Of(SessionSource.ClaudeCode, "/t.jsonl");
        Assert.Same(RunSources.ClaudeFallback, d.Sources.CatalogueFor(s));

        d.Claude = _ => Task.FromResult<string?>("{}");
        d.Now += TimeSpan.FromMinutes(4);
        d.Sources.CatalogueFor(s);
        Assert.Single(d.ClaudeAsked);                      // four minutes: not yet
        d.Now += TimeSpan.FromMinutes(2);
        Assert.Same(RunSources.ClaudeFallback, d.Sources.CatalogueFor(s));
        Assert.Equal(2, d.ClaudeAsked.Count);
    }

    [Fact]
    public void A_codex_catalogue_is_asked_of_the_sessions_own_codex_home()
    {
        var d = new Desk
        {
            Codex = _ => Task.FromResult<string?>(
                """{"data":[{"id":"gpt-6-luna","displayName":"GPT-6 Luna","hidden":false,"supportedReasoningEfforts":[{"reasoningEffort":"low"}]}]}"""),
        };
        var s = Of(SessionSource.Codex, "/x/.codex-b/sessions/2026/11/21/rollout-1.jsonl");
        Assert.Equal("gpt-6-luna", Assert.Single(d.Sources.CatalogueFor(s)!.Models).Id);
        Assert.Equal("/x/.codex-b", d.CodexAsked.Single());
    }

    [Theory]
    [InlineData("/x/.codex/sessions/2026/11/21/rollout.jsonl", "/x/.codex")]
    [InlineData("/elsewhere/rollout.jsonl", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_codex_home_is_the_folder_above_sessions(string? path, string? home) =>
        Assert.Equal(home, RunSources.CodexHomeOf(path));

    [Fact]
    public void A_grok_catalogue_is_its_models_cache_read_again_only_when_it_moves()
    {
        var d = new Desk();
        d.Write("/home/u/.grok/models_cache.json",
            """{"models":{"grok-4.7":{"info":{"name":"Grok 4.7","supports_reasoning_effort":true,"reasoning_efforts":[{"id":"high"},{"id":"low"}]}}}}""");
        Assert.Equal("grok-4.7", Assert.Single(d.Sources.CatalogueFor(Of(SessionSource.Grok, "/g/updates.jsonl"))!.Models).Id);
        d.Sources.CatalogueFor(Of(SessionSource.Grok, "/g/updates.jsonl"));
        Assert.Equal(1, d.Reads);
    }
}
