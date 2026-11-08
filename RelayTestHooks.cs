using System.Globalization;

namespace Orbweaver
{
    // A scripted pairing for the installed app, so a phone client in a test
    // harness can pair with the real desktop without a person at the
    // keyboard (CB-314's pre-gate end to end). Off unless the environment
    // says ORBWEAVER_RELAY_TEST_HOOKS=1 explicitly — no default, no setting,
    // no UI, and a release build behaves exactly as before without it.
    //
    // While on, at launch: the relay is enabled, an offer is made and its QR
    // text written to relay-test-qr.txt in the settings directory (the
    // harness reads it instead of a camera), the first pair.request is
    // Allowed without a click, and every step goes into
    // relay-test-events.txt with a UTC timestamp — including the check code,
    // so the harness can compare its own. An offer that expires unanswered
    // is replaced. State changes come from status files, the way the hooks
    // write them; nothing here fakes a session.
    internal static class RelayTestHooks
    {
        internal const string QrFile = "relay-test-qr.txt";
        internal const string EventsFile = "relay-test-events.txt";

        internal static bool Enabled(Func<string, string?> env) => BrandEnv.Get(BrandEnv.RelayTestHooks, env) == "1";

        internal static async Task RunAsync(IPairingClient client, string dir, Func<DateTimeOffset> now,
            Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token)
        {
            Directory.CreateDirectory(dir);
            void Log(string line) =>
                File.AppendAllText(Path.Combine(dir, EventsFile),
                    $"{now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} {line}\n");

            var allowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRequest(RelayLink.PairRequest r)
            {
                Log($"request {r.PhoneId} \"{r.Name}\" check code {r.CheckCodeDisplay}");
                var sent = client.Allow(r);
                Log(sent ? $"allowed {r.PhoneId}" : $"allow not sent for {r.PhoneId} (no connection)");
                if (sent) allowed.TrySetResult();
            }

            client.PairRequested += OnRequest;
            Log("test hooks on");
            try
            {
                while (!allowed.Task.IsCompleted)
                {
                    RelayLink.PairOffer offer;
                    try
                    {
                        offer = await client.BeginPairingAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or RelayHttpException or IOException
                                               or System.Text.Json.JsonException or RelaySecretUnavailableException)
                    {
                        Log($"offer failed: {ex.Message}");
                        await delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                        continue;
                    }
                    File.WriteAllText(Path.Combine(dir, QrFile), offer.QrText);
                    Log($"offer {offer.OfferId} expires {offer.ExpiresAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");
                    var left = offer.ExpiresAt - now();
                    await Task.WhenAny(allowed.Task, delay(left > TimeSpan.Zero ? left : TimeSpan.Zero, token)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                Log("paired; test hooks done");
            }
            catch (OperationCanceledException)
            {
                Log("test hooks stopped");
            }
            finally
            {
                client.PairRequested -= OnRequest;
            }
        }
    }
}
