using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    internal enum PaperclipHttpKind { Ok, Unauthorized, NotFound, ServerError, Timeout, Unreachable, Other }

    // One answer, already classified. Body is only ever parsed by
    // PaperclipApi and never logged: an agents listing carries adapterConfig
    // with local paths. Detail is a human sentence for the failure kinds.
    internal sealed record PaperclipResponse(PaperclipHttpKind Kind, int StatusCode, string Body, string? Detail = null)
    {
        internal bool IsOk => Kind == PaperclipHttpKind.Ok;
    }

    // The seam. The suites never reach a real Paperclip; they hand the poll
    // loop, the connect flow and the probe a fake of this (or, for the real
    // implementation, a loopback listener).
    internal interface IPaperclipApi
    {
        Task<PaperclipResponse> GetAsync(string path, CancellationToken ct);
        Task<PaperclipResponse> PostAsync(string path, string jsonBody, CancellationToken ct);
    }

    // The real thing. Bound to one base URL and, optionally, one board key; the
    // Authorization header is sent only when there is a key, so the unauthenticated
    // health and challenge calls carry nothing.
    internal sealed class PaperclipHttp : IPaperclipApi
    {
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string? _key;
        private readonly Action<string>? _log;

        // handler is the seam for tests that want to count connections; null
        // builds the default one.
        internal PaperclipHttp(string baseUrl, string? key, HttpMessageHandler? handler = null,
            TimeSpan? timeout = null, Action<string>? log = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _key = string.IsNullOrEmpty(key) ? null : key;
            _log = log;
            _http = handler == null ? NewClient() : new HttpClient(handler);
            _http.Timeout = timeout ?? RequestTimeout;
        }

        [ExcludeFromCodeCoverage] // real handler construction; the tests inject their own
        private static HttpClient NewClient() => new();

        public Task<PaperclipResponse> GetAsync(string path, CancellationToken ct) =>
            SendAsync(HttpMethod.Get, path, null, ct);

        public Task<PaperclipResponse> PostAsync(string path, string jsonBody, CancellationToken ct) =>
            SendAsync(HttpMethod.Post, path, jsonBody, ct);

        private async Task<PaperclipResponse> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            try
            {
                using var req = new HttpRequestMessage(method, _baseUrl + path);
                if (_key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                req.Headers.Accept.ParseAdd("application/json");
                if (body != null) req.Content = new StringContent(body, new UTF8Encoding(false), "application/json");

                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var code = (int)res.StatusCode;
                // Status, size and time only: never the body, never the key.
                _log?.Invoke($"{method} {path} -> {code}, {text.Length} bytes, {(int)(DateTime.UtcNow - started).TotalMilliseconds} ms");
                return new PaperclipResponse(Classify(code), code, text);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new PaperclipResponse(PaperclipHttpKind.Timeout, 0, "", "Timed out");
            }
            catch (Exception ex)
            {
                return new PaperclipResponse(PaperclipHttpKind.Unreachable, 0, "",
                    OpenClawGateway.ExplainConnectFailure(ex, OperatingSystem.IsMacOS()));
            }
        }

        internal static PaperclipHttpKind Classify(int code) => code switch
        {
            >= 200 and < 300 => PaperclipHttpKind.Ok,
            401 or 403 => PaperclipHttpKind.Unauthorized,
            404 => PaperclipHttpKind.NotFound,
            >= 500 => PaperclipHttpKind.ServerError,
            _ => PaperclipHttpKind.Other,
        };
    }
}
