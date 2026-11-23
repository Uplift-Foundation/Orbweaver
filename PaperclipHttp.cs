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
    internal sealed class PaperclipHttp : IPaperclipApi, IDisposable
    {
        // What the app logs through: status, size and time, never a body, a key or a query.
        internal static readonly Action<string> DefaultLog = line => Console.Error.WriteLine("paperclip: " + line);

        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string? _key;
        private readonly Action<string>? _log;
        private readonly bool _logSuccess;

        // handler is the seam for tests that want to count connections; null
        // builds the default one.
        internal PaperclipHttp(string baseUrl, string? key, HttpMessageHandler? handler = null,
            TimeSpan? timeout = null, Action<string>? log = null, bool logSuccess = true)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _key = string.IsNullOrEmpty(key) ? null : key;
            _log = log;
            _logSuccess = logSuccess;
            _http = handler == null ? NewClient() : new HttpClient(handler);
            _http.Timeout = timeout ?? RequestTimeout;
        }

        [ExcludeFromCodeCoverage] // real handler construction; the tests inject their own
        private static HttpClient NewClient() => new();

        // A client replaced on a key or URL change is disposed, not left to the
        // finaliser holding its connections.
        public void Dispose() => _http.Dispose();

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
                if (_logSuccess || Classify(code) != PaperclipHttpKind.Ok)
                    _log?.Invoke($"{method} {PathOnly(path)} -> {code}, {text.Length} bytes, {(int)(DateTime.UtcNow - started).TotalMilliseconds} ms");
                return new PaperclipResponse(Classify(code), code, text);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _log?.Invoke($"{method} {PathOnly(path)} -> timed out, {(int)(DateTime.UtcNow - started).TotalMilliseconds} ms");
                return new PaperclipResponse(PaperclipHttpKind.Timeout, 0, "", "Timed out");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"{method} {PathOnly(path)} -> unreachable ({ex.GetType().Name}), {(int)(DateTime.UtcNow - started).TotalMilliseconds} ms");
                return new PaperclipResponse(PaperclipHttpKind.Unreachable, 0, "",
                    Explain(ex, _baseUrl, OperatingSystem.IsMacOS()));
            }
        }

        // One reason, once, with the address. .NET nests the same sentence three
        // deep (the HttpRequestException's, with the address appended, then the
        // SocketException's), and joining the chain printed it twice. The
        // innermost message is the reason; the address is added if it lacks it.
        internal static string Explain(Exception ex, string baseUrl, bool onMacOS)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            var reason = inner.Message.Trim();
            var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Authority : baseUrl;
            var text = reason.Contains(host, StringComparison.OrdinalIgnoreCase) ? reason : $"{reason} ({host})";
            return onMacOS && OpenClawGateway.IsHostUnreachable(ex)
                ? text + " \u2014 " + OpenClawGateway.LocalNetworkHint
                : text;
        }

        // The query is never logged: the poll URL carries the challenge token, and
        // a path is all anyone reading a log needs.
        internal static string PathOnly(string path)
        {
            var q = path.IndexOf('?');
            return q < 0 ? path : path[..q];
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
