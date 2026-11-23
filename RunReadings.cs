using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-404: the parsers that turn files and screens this app already reads into
    // a RunReading (what a session runs) or a RunCatalogue (what it could run).
    //
    // All pure, all fed bytes by RunControlsHost, and all written against real
    // output captured on 2026-11-21: a 2.1.330 Claude Code transcript, a Codex
    // 0.164.0 rollout, a Grok 1.0.77 summary.json, the `list_models` and
    // `model/list` answers, and a Codex 0.154.0 picker in a private tmux pane.
    // Every one of these formats belongs to somebody else and fails quietly, so
    // a row that does not parse is skipped rather than guessed at.
    public static class RunReadings
    {
        // --- Claude Code transcript --------------------------------------------

        // The last assistant row's message.model and top-level effort, superseded
        // by a later `/model` or `/effort` confirmation row. Sidechain rows are a
        // subagent's own model and say nothing about the session.
        public static RunReading FromClaudeTranscript(IEnumerable<string> lines, RunReadingSource source = RunReadingSource.Transcript)
        {
            var reading = RunReading.None;

            foreach (var line in lines)
            {
                if (!TryParse(line, out var root)) continue;
                using (root)
                {
                    var row = root.RootElement;
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    if (Bool(row, "isSidechain")) continue;
                    var at = Time(row, "timestamp");

                    switch (Str(row, "type"))
                    {
                        case "assistant":
                            var model = row.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object ? Str(msg, "model") : null;
                            // Claude Code writes "<synthetic>" for rows it made up
                            // itself (an API error, an interrupt), which name no model.
                            if (model is not null && model.StartsWith('<')) model = null;
                            var effort = Str(row, "effort");
                            if (model is null && effort is null) continue;
                            reading = RunReading.Merge(reading, new RunReading(model, effort, at, source));
                            break;

                        case "user":
                            if (!row.TryGetProperty("message", out var um) || um.ValueKind != JsonValueKind.Object) continue;
                            if (!um.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) continue;
                            if (CommandConfirmation(c.GetString()!) is { } confirmed)
                                reading = RunReading.Merge(reading, confirmed with { At = at, Source = source });
                            break;
                    }
                }
            }

            return reading;
        }

        // "Set model to `Opus 5.5` and saved as your default for new sessions",
        // "Kept model as `Opus 5.5 (1M context)`", "Set effort level to medium
        // (saved as your default for new sessions): …" — measured, from real
        // transcripts. The model comes back as its display name, which the
        // catalogue finds by label; a trailing "(default)" or "(1M context)" is
        // a qualifier, not part of the name. "Model 'x' not found" is a refusal
        // and changes nothing.
        private static readonly Regex SetModel = new(@"^<local-command-stdout>(?:Set model to|Kept model as) `(?<m>[^`]+)`", RegexOptions.Compiled);
        private static readonly Regex SetEffort = new(@"^<local-command-stdout>Set effort level to (?<e>[a-z]+)\b", RegexOptions.Compiled);
        private static readonly Regex Qualifier = new(@"\s+\([^)]*\)$", RegexOptions.Compiled);

        internal static RunReading? CommandConfirmation(string content)
        {
            if (SetModel.Match(content) is { Success: true } m)
                return new RunReading(Qualifier.Replace(m.Groups["m"].Value, ""), null, null, RunReadingSource.Transcript);
            if (SetEffort.Match(content) is { Success: true } e)
                return new RunReading(null, e.Groups["e"].Value, null, RunReadingSource.Transcript);
            return null;
        }

        // The pane footer's `◐ medium · /effort`, the last one on the screen.
        private static readonly Regex ClaudeFooter = new(@"(?<e>[a-z]+)\s+·\s+/effort\b", RegexOptions.Compiled);

        public static string? ClaudeFooterEffort(string? pane)
        {
            if (string.IsNullOrEmpty(pane)) return null;
            var matches = ClaudeFooter.Matches(pane);
            return matches.Count == 0 ? null : matches[^1].Groups["e"].Value;
        }

        // --- Codex ---------------------------------------------------------------

        public sealed record CodexRollout(RunReading Reading, Version? CliVersion);

        // Every turn's turn_context carries model and effort; a change made with
        // no turn writes event_msg/thread_settings_applied at once (measured at
        // 0.167.0 within 3 s). session_meta.cli_version decides the route.
        public static CodexRollout FromCodexRollout(IEnumerable<string> lines)
        {
            var reading = RunReading.None;
            Version? version = null;

            foreach (var line in lines)
            {
                if (!TryParse(line, out var root)) continue;
                using (root)
                {
                    var row = root.RootElement;
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    if (!row.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                    var at = Time(row, "timestamp");

                    switch (Str(row, "type"))
                    {
                        case "session_meta":
                            version = ParseVersion(Str(p, "cli_version")) ?? version;
                            break;

                        case "turn_context":
                            reading = Fold(reading, Str(p, "model"), Str(p, "effort"), at);
                            break;

                        case "event_msg" when Str(p, "type") == "thread_settings_applied"
                            && p.TryGetProperty("thread_settings", out var s) && s.ValueKind == JsonValueKind.Object:
                            reading = Fold(reading, Str(s, "model"), Str(s, "reasoning_effort"), at);
                            break;
                    }
                }
            }

            return new CodexRollout(reading, version);

            static RunReading Fold(RunReading r, string? model, string? effort, DateTimeOffset? at) =>
                model is null && effort is null ? r : RunReading.Merge(r, new RunReading(model, effort, at, RunReadingSource.Rollout));
        }

        // Before the first turn there is no rollout, so the footer is the
        // reading: `<model> <effort> · <cwd>` (measured "sonnet default · ~/…" at
        // 0.154.0, "GPT-6.2-Sol high · …" at 0.167.0). "default" is the model's
        // own default effort, which is not a level, so it reads as unknown.
        private static readonly Regex CodexFooter = new(
            @"^\s*(?<m>\S+)\s+(?<e>default|minimal|none|low|medium|high|xhigh|max|ultra)\s+·\s", RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex CodexBanner = new(@"OpenAI Codex \(v(?<v>\d+\.\d+\.\d+)", RegexOptions.Compiled);

        public static RunReading FromCodexFooter(string? pane)
        {
            if (string.IsNullOrEmpty(pane)) return RunReading.None;
            var matches = CodexFooter.Matches(pane);
            if (matches.Count == 0) return RunReading.None;
            var last = matches[^1];
            var effort = last.Groups["e"].Value;
            return new RunReading(last.Groups["m"].Value, effort == "default" ? null : effort, null, RunReadingSource.PaneFooter);
        }

        public static Version? CodexBannerVersion(string? pane) =>
            pane is not null && CodexBanner.Match(pane) is { Success: true } m ? ParseVersion(m.Groups["v"].Value) : null;

        // --- Codex's picker, before 0.164.0 ----------------------------------------

        public enum CodexPickerStage { Models, Levels }

        public sealed record CodexPickerRow(int Digit, string Id, bool Highlighted);

        // One screen of the picker, or null when the screen is neither. Only the
        // first nine rows have a digit; "More reasoning…" is a sub-list that was
        // not exercised, so it is not a row.
        public sealed record CodexPickerScreen(CodexPickerStage Stage, string? ForModel, IReadOnlyList<CodexPickerRow> Rows)
        {
            public int? DigitFor(string id) =>
                Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))?.Digit;
        }

        private static readonly Regex PickerRow = new(@"^\s*(?<hi>›)?\s*(?<d>[1-9])\.\s+(?<rest>.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex LevelsHeader = new(@"Select Reasoning Level for (?<m>\S+)", RegexOptions.Compiled);

        // The level screen shows labels, the typed route takes ids.
        private static readonly IReadOnlyDictionary<string, string> LevelIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Minimal"] = "minimal", ["Low"] = "low", ["Medium"] = "medium", ["High"] = "high",
            ["Extra high"] = "xhigh", ["Max"] = "max", ["Ultra"] = "ultra",
        };

        public static CodexPickerScreen? ParseCodexPicker(string? screen)
        {
            if (string.IsNullOrEmpty(screen)) return null;
            var lines = screen.Replace("\r", "").Split('\n');

            var levels = LevelsHeader.Match(screen);
            var models = screen.Contains("Select Model and Effort", StringComparison.Ordinal);
            if (!levels.Success && !models) return null;

            // Rows after the last header only: a picker drawn under an older one
            // must not borrow its rows.
            var header = levels.Success ? "Select Reasoning Level for" : "Select Model and Effort";
            var start = Array.FindLastIndex(lines, l => l.Contains(header, StringComparison.Ordinal));

            var rows = new List<CodexPickerRow>();
            for (var i = start + 1; i < lines.Length; i++)
            {
                if (PickerRow.Match(lines[i]) is not { Success: true } m) continue;
                var rest = m.Groups["rest"].Value;
                var digit = m.Groups["d"].Value[0] - '0';
                var hi = m.Groups["hi"].Success;

                if (levels.Success)
                {
                    // "Medium (default)  Balances …": the label runs to the first
                    // double space or a "(default)".
                    var label = Regex.Split(rest, @"\s{2,}")[0];
                    label = label.Replace("(default)", "").Trim();
                    if (LevelIds.TryGetValue(label, out var id)) rows.Add(new CodexPickerRow(digit, id, hi));
                }
                else
                {
                    rows.Add(new CodexPickerRow(digit, rest.Split(' ', 2)[0], hi));
                }
            }

            return new CodexPickerScreen(levels.Success ? CodexPickerStage.Levels : CodexPickerStage.Models,
                levels.Success ? levels.Groups["m"].Value : null, rows);
        }

        // The transcript line that confirms a set: "Model changed to
        // gpt-5.6-terra high" (0.154.0), "… for this session only" (0.164.0+).
        // And the refusals the typed form gives, which are worth showing as
        // they are.
        private static readonly Regex CodexChanged = new(@"Model changed to (?<m>\S+)(?:\s+(?<e>[a-z]+))?", RegexOptions.Compiled);
        private static readonly Regex CodexRefused = new(
            @"(?<why>Model '[^']+' is unavailable\.[^\n]*|Reasoning level '[^']+' is unavailable[^\n]*|Unexpected argument '[^']+')", RegexOptions.Compiled);

        public static RunReading FromCodexChanged(string? screen)
        {
            if (string.IsNullOrEmpty(screen)) return RunReading.None;
            var all = CodexChanged.Matches(screen);
            if (all.Count == 0) return RunReading.None;
            var m = all[^1];
            var effort = m.Groups["e"].Success && m.Groups["e"].Value != "for" ? m.Groups["e"].Value : null;
            return new RunReading(m.Groups["m"].Value, effort, null, RunReadingSource.PaneFooter);
        }

        public static string? CodexRefusal(string? screen)
        {
            if (string.IsNullOrEmpty(screen)) return null;
            var all = CodexRefused.Matches(screen);
            return all.Count == 0 ? null : all[^1].Groups["why"].Value.Trim();
        }

        // --- Grok ----------------------------------------------------------------

        // summary.json: current_model_id, reasoning_effort, updated_at (measured
        // on grok 1.0.77 on the Windows box).
        public static RunReading FromGrokSummary(string? json)
        {
            if (!TryParse(json, out var doc)) return RunReading.None;
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return RunReading.None;
                var model = Str(root, "current_model_id");
                var effort = Str(root, "reasoning_effort");
                if (model is null && effort is null) return RunReading.None;
                return new RunReading(model, effort, Time(root, "updated_at") ?? Time(root, "last_active_at"), RunReadingSource.GrokSummary);
            }
        }

        // --- Catalogues ------------------------------------------------------------

        // Claude Code's `list_models` control answer, either the whole
        // control_response line or just its innermost object. supportsEffort
        // absent (Haiku 4.5) is a model with no effort setting.
        public static RunCatalogue ClaudeModelList(string? json)
        {
            if (!TryParse(json, out var doc)) return RunCatalogue.Empty;
            using (doc)
            {
                var root = doc.RootElement;
                // control_response.response.response.models, or a bare { models }.
                for (var i = 0; i < 2 && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var inner); i++) root = inner;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                    return RunCatalogue.Empty;

                var list = new List<RunModel>();
                foreach (var m in models.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object || Str(m, "value") is not { } id) continue;
                    var levels = Bool(m, "supportsEffort") ? Strings(m, "supportedEffortLevels") : null;
                    list.Add(new RunModel(id, Str(m, "displayName") ?? id, Str(m, "resolvedModel"), levels));
                }

                return new RunCatalogue(list);
            }
        }

        // Codex app-server `model/list`: result.data[] with id, displayName,
        // hidden and supportedReasoningEfforts[].reasoningEffort (measured at
        // 0.154.0 and 0.167.0). Hidden models are not offered.
        public static RunCatalogue CodexModelList(string? json)
        {
            if (!TryParse(json, out var doc)) return RunCatalogue.Empty;
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result)) root = result;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return RunCatalogue.Empty;

                var list = new List<RunModel>();
                foreach (var m in data.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object || Str(m, "id") is not { } id || Bool(m, "hidden")) continue;
                    var levels = new List<string>();
                    if (m.TryGetProperty("supportedReasoningEfforts", out var efforts) && efforts.ValueKind == JsonValueKind.Array)
                        foreach (var e in efforts.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.Object && Str(e, "reasoningEffort") is { } level) levels.Add(level);
                    list.Add(new RunModel(id, Str(m, "displayName") ?? id, Str(m, "model"), levels.Count == 0 ? null : levels));
                }

                return new RunCatalogue(list);
            }
        }

        // ~/.grok/models_cache.json: models{ id: { info: { name, hidden,
        // supports_reasoning_effort, reasoning_efforts[{id}] } } }, measured on
        // 1.0.13 and 1.0.77. The efforts are listed highest first; the dial
        // wants lowest first, so they are put in the ladder's order.
        public static RunCatalogue GrokModelsCache(string? json)
        {
            if (!TryParse(json, out var doc)) return RunCatalogue.Empty;
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
                    return RunCatalogue.Empty;

                var list = new List<RunModel>();
                foreach (var entry in models.EnumerateObject())
                {
                    var info = entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("info", out var i) ? i : entry.Value;
                    if (info.ValueKind != JsonValueKind.Object || Bool(info, "hidden")) continue;
                    var id = Str(info, "id") ?? entry.Name;

                    IReadOnlyList<string>? levels = null;
                    if (Bool(info, "supports_reasoning_effort") && info.TryGetProperty("reasoning_efforts", out var efforts) && efforts.ValueKind == JsonValueKind.Array)
                    {
                        levels = efforts.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.Object)
                            .Select(e => Str(e, "id"))
                            .OfType<string>()
                            .OrderBy(Rank)
                            .ToList();
                    }

                    list.Add(new RunModel(id, Str(info, "name") ?? id, Str(info, "model"), levels));
                }

                return new RunCatalogue(list);
            }
        }

        // The order effort names run in across every CLI measured; a name not
        // on it sorts last, in the order it came.
        internal static readonly string[] Ladder = { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" };

        internal static int Rank(string level)
        {
            var i = Array.IndexOf(Ladder, level.ToLowerInvariant());
            return i < 0 ? Ladder.Length : i;
        }

        // --- JSON helpers ----------------------------------------------------------

        private static bool TryParse(string? text, out JsonDocument doc)
        {
            doc = null!;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try { doc = JsonDocument.Parse(text); return true; }
            catch (JsonException) { return false; }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static IReadOnlyList<string>? Strings(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var list = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
            return list.Count == 0 ? null : list;
        }

        private static DateTimeOffset? Time(JsonElement e, string name) =>
            Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

        private static Version? ParseVersion(string? s) => Version.TryParse(s, out var v) ? v : null;
    }
}
