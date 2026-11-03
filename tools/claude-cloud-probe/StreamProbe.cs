using System.Net.Http.Headers;
using System.Text.Json;
using Orbweaver;

namespace Orbweaver;

// CB-306: what the events stream replays to a late subscriber.
//
// The question is whether `system/init` — the row that carries
// `slash_commands` and `terminal_slash_commands` — comes back when the
// stream is opened from sequence 0, or only to whoever was attached when the
// session started. The app's own ClaudeCloudStreamRequest builds the request
// and ClaudeCloudStreamEvents reads it, so the answer is read off the same
// parser the panel uses.
//
// Prints, per event: kind, SSE event name, sequence number, payload type and
// subtype — and for an init row the payload's key names with the length of
// each array. Never a value: a command name is harmless, but the rule here
// is shape only, the same as every other verb in this probe.
internal static class StreamProbe
{
    internal static async Task<int> RunAsync(string[] args, Func<Task<CredentialRead>> readCredential)
    {
        if (args.Length == 0 || !ClaudeCloudRoster.IsWellFormedId(args[0]))
        {
            Console.Error.WriteLine("stream <id> [--from <n>] [--seconds <n>]");
            return 2;
        }

        var id = args[0];
        long? from = Value(args, "--from") is { } f && long.TryParse(f, out var parsed) ? parsed : null;
        var seconds = Value(args, "--seconds") is { } s && int.TryParse(s, out var secs) ? secs : 15;

        var read = await readCredential();
        if (read.Outcome != CredentialOutcome.Found || read.AccessToken is null)
        {
            Console.Error.WriteLine($"no usable credential: {ClaudeCliCredentials.Describe(read.Outcome)}");
            return 1;
        }

        using var request = ClaudeCloudStreamRequest.Build(read.AccessToken, id, from)!;
        Console.WriteLine($"request  GET {request.RequestUri}");
        Console.WriteLine($"from     {(from is null ? "(none: live only)" : from.ToString())}");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"transport {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        using (response)
        {
            Console.WriteLine($"status   {(int)response.StatusCode}");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            Console.WriteLine($"ctype    {mediaType ?? "(none)"}");
            if (!response.IsSuccessStatusCode) return 1;

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var count = 0;
            var inits = 0;
            try
            {
                await foreach (var ev in ClaudeCloudStreamEvents.ReadAsync(reader, (int)response.StatusCode, mediaType, cts.Token))
                {
                    count++;
                    if (ev.Kind == CloudStreamEventKind.Keepalive) { Console.WriteLine("event    keepalive"); continue; }
                    Console.WriteLine($"event    {ev.Kind} name={ev.Name ?? "-"} seq={ev.SequenceNum?.ToString() ?? "-"} type={ev.PayloadType ?? "-"} subtype={ev.Subtype ?? "-"}");
                    if (ev.PayloadType == "system" && ev.Subtype == "init" && ev.PayloadJson is { } json)
                    {
                        inits++;
                        Console.WriteLine("init     " + Keys(json));
                    }
                    if (ev.Kind == CloudStreamEventKind.Ended) break;
                }
            }
            catch (OperationCanceledException) { }

            Console.WriteLine($"summary  {count} event(s) in {seconds}s, {inits} system/init row(s)");
            return 0;
        }
    }

    private static string Keys(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // PayloadJson is the whole `data:` object; the CLI row is its `payload`.
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("payload", out var inner)
                && inner.ValueKind == JsonValueKind.Object)
            {
                root = inner;
            }
            if (root.ValueKind != JsonValueKind.Object) return "(payload not an object)";
            var parts = new List<string>();
            foreach (var p in root.EnumerateObject())
            {
                parts.Add(p.Value.ValueKind == JsonValueKind.Array
                    ? $"{p.Name}[{p.Value.GetArrayLength()}]"
                    : p.Name);
            }
            return string.Join(" ", parts);
        }
        catch (JsonException) { return "(payload not JSON)"; }
    }

    private static string? Value(string[] opts, string flag)
    {
        var i = Array.IndexOf(opts, flag);
        return i >= 0 && i + 1 < opts.Length ? opts[i + 1] : null;
    }
}
