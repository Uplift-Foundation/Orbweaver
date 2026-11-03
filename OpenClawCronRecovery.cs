using System.Text.Json;

namespace Orbweaver
{
    // CB-115: recovers a delivered picture's real path when OpenClaw's cron
    // delivery route has already stripped both the "MEDIA:" prefix and the
    // directory out of the transcript, leaving a caption and a bare filename
    // with no directory at all:
    //
    //   sunset over the harbour today 🌇
    //   poster_harbour_sunset_10000001.png
    //
    // LocalMediaPathFrom cannot resolve that today (see its own comment): a
    // MEDIA: line only counts when it is a line of its own, and the bare-path
    // arm only fires when the ENTIRE trimmed text is nothing but the path — a
    // caption sharing the message with it fails both. 13 of Owner's 14
    // pictures in main's DM failed this way.
    //
    // The original string survives elsewhere, though: the cron run record
    // that produced the message. Every automation turn carries
    //
    //   "openclawAutomation": {"kind":"cron","jobId":"3c29d831-…","runId":"…"}
    //
    // and `cron.runs {"jobId": …}` — declared `operator.read` in the
    // gateway's own dist/method-scopes-K6J_UQGL.js, unlike every mutating
    // cron.* method, which needs operator.admin — answers with a page of that
    // job's history, each entry carrying the very `summary` the agent
    // originally produced, MEDIA: line and all.
    //
    // ---- Matching: basename within the job, not runId/timestamp ----
    //
    // The first version of this matched on time: parse the trailing
    // milliseconds off `runId` and pick the run whose `runAtMs` was the
    // greatest value at or before it, within a tolerance. That was wrong on
    // its own terms even measured against real data — `runId`'s suffix and
    // the matching entry's `runAtMs` differ by 65 ms, so an equality match
    // finds nothing and looks like the whole approach failed — and it was
    // wrong in a second way that only showed up thinking past today's
    // measurement: the tolerance it needed ("a few seconds is unambiguous
    // because runs are ~25 minutes apart") is a property of Owner's cron
    // schedule, not of the mechanism, and a job firing every 30 seconds would
    // collapse it silently. Scrapped mid-flight rather than kept as a
    // fallback — `runId` is not read anywhere in this file.
    //
    // What is actually unique is simpler and needs no tunable at all: the
    // bare filename in the transcript is *itself* the thing being resolved,
    // and it is unique within one job's run history. Measured against the
    // real jobId this was built against (`.cb115-evidence/cron-runs-live-capture.json`
    // holds one page of it): of 51 runs, 35 carry a MEDIA: path, 35 distinct
    // basenames, zero collisions — every one of main's bare-filename captions
    // matched uniquely.
    //
    // So the match is: read every run's `summary` through the *existing*
    // LocalMediaPathFrom (no new parser — this ticket supplies a better
    // input, not a new arm), take the basename of whatever path comes back,
    // and find the one entry (per job) whose basename equals the transcript's.
    // A collision — two different full paths sharing one basename inside a
    // job — is refused rather than guessed, the same rule
    // OpenClawSessions.MediaPathsByFileName already applies to duplicate
    // basenames and for the same reason: showing the wrong picture is worse
    // than showing none. Not observed once in the real corpus; the refusal
    // exists for the day it is.
    //
    // ---- Paging: a small limit silently drops old runs ----
    //
    // An early capture of this job's runs used limit:5 and held only 4 of the
    // 35 real MEDIA: paths — a shorter job history, silently, with no error
    // and no empty result to notice. A picture scrolled back to must not fail
    // merely because its run fell off the first page, so the RPC layer that
    // calls this (OpenClawSessions.RecoverCronMediaPathAsync) keeps paging on
    // `nextOffset` until `hasMore` is false or a bounded number of pages is
    // spent, and caches the whole accumulated map rather than one page's
    // worth at a time.
    //
    // ---- Trigger: three conjuncts, checked at two different call sites ----
    //
    //   openclawAutomation.kind == "cron"
    //     AND the message's trailing whitespace-separated token is
    //         image-shaped (a bare filename with an image extension, or a
    //         rooted/~ path with one)
    //     AND the turn has no resolved image yet
    //
    // Not "any turn carrying openclawAutomation": measured, all 14 of main's
    // picture turns carry `openclawAutomation.kind == "cron"`, including one
    // whose caption pairs with a rooted path rather than a bare filename — a
    // shape a future fix to caption+path recognition (CB-107) would resolve
    // directly, without ever reaching this file. Gating on the *outcome*
    // rather than the key's presence means that turn stops costing a lookup
    // the moment the earlier arm starts succeeding, with nothing here to
    // revisit when that lands — and it means the trigger fires identically
    // whether or not CB-107 is present, since "no resolved image yet" is true
    // in both worlds until something upstream of this file actually resolves
    // one.
    //
    // And not "any automation turn, resolved or not": a text-only cron job —
    // a heartbeat, a status post — carries the same
    // `openclawAutomation.kind == "cron"` and would otherwise cost an RPC on
    // every single reply for a job that never once mentions a picture. The
    // image-shape check is the second conjunct precisely to exclude that
    // population; it costs nothing to check and excludes nothing in main's
    // corpus today (a picture-delivery job's every turn already ends in a
    // filename), but it is not free to skip for a job that looks different.
    //
    // CB-272: both predicates now accept a clip as well (a picture-delivery
    // job's cron run can end on `clip.mp4` just as readily); the rooted case
    // goes through LooksLikeAMediaPath for that reason, which still refuses
    // anything that LooksLikeAnImagePath refuses on structure.
    //
    // The rooted case reuses OpenClawSessions.LooksLikeAnImagePath, already
    // present for LocalMediaPathFrom's own bare-path arm. The bare-filename
    // case has no existing predicate to reuse — 290d086 (this ticket's base)
    // carries LooksLikeAnImagePath but not a bare-filename twin, which is
    // CB-107's to add — so LooksLikeABareImageFilename below is new. If
    // CB-107 lands, that predicate and this one are the same idea reached
    // from two directions and should be reconciled to one; this file does
    // not depend on CB-107 existing.
    //
    // ---- Pure, like OpenClawMediaSource ----
    //
    // Everything in this file is parsing and matching over values already in
    // hand — no window, no settings, no socket. The RPC call itself
    // (OpenClawSessions.RecoverCronMediaPathAsync) is the only excluded half,
    // the same split CB-109's OpenClawMediaSource and CB-88's
    // LocalMediaPathFrom already use.
    internal readonly record struct OpenClawAutomation(string Kind, string JobId);

    // One cron.runs entry, reduced to the two fields this file's matching
    // actually reads. JobId is read back out of the entry — rather than
    // trusted to equal the jobId the request was made with — because the
    // filter in MediaPathsByBasenameForJob is defence in depth, not a
    // request-shape assumption: the same caution LocalMediaPathFrom already
    // applies to an ordinary sentence that happens to start with "MEDIA:".
    internal readonly record struct OpenClawCronRun(string JobId, string? Summary);

    internal static class OpenClawCronRecovery
    {
        internal const string CronKind = "cron";

        // ---- reading the trigger's first conjunct off a message ----

        // "openclawAutomation" as CB-115's measurement shows it:
        //   {"kind":"cron","jobId":"…","runId":"…"}
        // runId is deliberately not read here — nothing downstream of this
        // ticket's basename redesign uses it (see the header). Kind is read
        // rather than assumed "cron" so a caller can gate on it explicitly,
        // and this parser stays honest about what the object actually said
        // rather than about what this ticket happens to handle.
        internal static OpenClawAutomation? AutomationOf(JsonElement carrier)
        {
            if (carrier.ValueKind != JsonValueKind.Object) return null;
            if (!carrier.TryGetProperty("openclawAutomation", out var automation)
                || automation.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var kind = StringOrNull(automation, "kind");
            var jobId = StringOrNull(automation, "jobId");

            return string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(jobId)
                ? null
                : new OpenClawAutomation(kind!, jobId!);
        }

        // ---- the trigger's second conjunct, and the value it hands back ----

        // The basename to look up, when the message's trailing
        // whitespace-separated token is image-shaped — null otherwise, which
        // is this method's way of saying the second conjunct failed. Takes
        // the *basename* of whichever shape matched, not the token verbatim:
        // a rooted path and a bare filename resolve to the same lookup key
        // once any directory is stripped, so one caller does not need to
        // handle the two shapes differently.
        //
        // "Trailing token", not "trailing line": an earlier version of this
        // split on '\n' alone, which happens to agree with a whitespace split
        // for every real case measured (a filename never contains a space),
        // but the whitespace split is what was actually specified and does
        // not depend on that coincidence holding for a caption not yet seen.
        internal static string? CandidateBasenameFrom(string text)
        {
            var token = TrailingTokenOf(text);
            if (token is null) return null;

            if (LooksLikeABareImageFilename(token) || LooksLikeABareVideoFilename(token)) return token;
            if (OpenClawSessions.LooksLikeAMediaPath(token)) return BasenameOf(token);

            return null;
        }

        private static string? TrailingTokenOf(string text)
        {
            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 ? tokens[^1] : null;
        }

        // Mirrors OpenClawSessions' own ImageExtensions list rather than
        // sharing it — the same small, deliberate duplication this file
        // already accepts for StringOrNull: one file to a concern, rather
        // than a shared utility neither file would then fully own. That
        // reasoning is about the *image* list only. The video half is not
        // duplicated: CB-272 reads it from OpenClawVideo.Extensions, so there
        // is exactly one list of clip extensions in the app.
        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        // A bare filename: no directory separator of either kind, no space,
        // and a recognised image extension. See the header for why this has
        // no existing predicate to reuse and what should happen to it if
        // CB-107 adds one.
        private static bool LooksLikeABareImageFilename(string token) =>
            token.Length > 0
            && !token.Contains('/')
            && !token.Contains('\\')
            && !token.Contains(' ')
            && Array.Exists(ImageExtensions, ext => token.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        private static bool LooksLikeABareVideoFilename(string token) =>
            token.Length > 0
            && !token.Contains('/')
            && !token.Contains('\\')
            && !token.Contains(' ')
            && (OpenClawVideo.HasVideoExtension(token) || OpenClawAudio.HasAudioExtension(token));

        // CB-274: the filename a failed attachment block names, as a lookup
        // key for the cron run's summary. Any extension: the block already
        // says what kind of file it is, so the extension tests above (which
        // exist to nominate a candidate out of prose) have no job here, and
        // the basename equality in MediaPathsByBasenameForJob is the real
        // match. A label with a separator or whitespace is not a bare name.
        internal static string? BareMediaBasename(string? label) =>
            !string.IsNullOrWhiteSpace(label)
            && !label!.Contains('/')
            && !label.Contains('\\')
            && !label.Any(char.IsWhiteSpace)
                ? label
                : null;

        internal static string BasenameOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        // ---- reading a cron.runs page ----

        internal static IReadOnlyList<OpenClawCronRun> RunsFrom(JsonElement response)
        {
            var runs = new List<OpenClawCronRun>();

            if (response.ValueKind != JsonValueKind.Object) return runs;
            if (!response.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return runs;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var jobId = StringOrNull(entry, "jobId");
                if (jobId is null) continue;

                runs.Add(new OpenClawCronRun(jobId, StringOrNull(entry, "summary")));
            }

            return runs;
        }

        internal static bool HasMore(JsonElement response) =>
            response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("hasMore", out var value)
            && value.ValueKind == JsonValueKind.True;

        // Null both when the field is absent (the last page) and when it is
        // present but not a number, which the paging loop treats identically
        // — either way there is nowhere further to ask.
        internal static int? NextOffset(JsonElement response)
        {
            if (response.ValueKind != JsonValueKind.Object) return null;
            if (!response.TryGetProperty("nextOffset", out var value)) return null;
            return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
        }

        // ---- matching ----

        // Every basename this job's runs can account for, mapped to the
        // MEDIA: path behind it — or to null, when two runs claim the same
        // basename with two different paths and neither is trustworthy. Built
        // once per job rather than once per lookup, so paging and caching
        // (OpenClawSessions.RecoverCronMediaPathAsync) are a job-level
        // concern and this stays a pure fold over whatever runs were fetched
        // for it.
        //
        // A collision maps explicitly to null rather than being left out of
        // the dictionary, so a caller — and a test — can tell "refused"
        // apart from "never seen": ContainsKey is true either way, and the
        // value is what tells them apart.
        internal static Dictionary<string, string?> MediaPathsByBasenameForJob(
            IEnumerable<OpenClawCronRun> runs, string jobId)
        {
            var byBasename = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var run in runs)
            {
                if (run.JobId != jobId) continue;
                if (run.Summary is null) continue;

                var candidate = OpenClawSessions.LocalMediaPathFrom(run.Summary);

                // CB-275: a document's `MEDIA:` path has no picture, clip or
                // voice-note extension, so fall back to the marker alone.
                var path = candidate?.Path ?? OpenClawSessions.MediaMarkerPathFrom(run.Summary);
                if (path is null) continue;

                var basename = BasenameOf(path);
                if (!byBasename.TryGetValue(basename, out var paths))
                {
                    paths = new HashSet<string>(StringComparer.Ordinal);
                    byBasename[basename] = paths;
                }

                paths.Add(path);
            }

            var result = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var (basename, paths) in byBasename)
            {
                // Two different full paths sharing one basename inside a
                // single job — not observed once across the real 51-run
                // corpus this was measured against, but showing the wrong
                // picture is worse than showing none, so this is refused
                // rather than guessed.
                if (paths.Count > 1)
                {
                    result[basename] = null;
                    continue;
                }

                result[basename] = paths.First();
            }

            return result;
        }

        private static string? StringOrNull(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.SafeString()
                : null;
    }
}
