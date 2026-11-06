using System.Net;
using System.Net.Http.Headers;

namespace Orbweaver
{
    // The HTTP the updater needs, as two methods (CB-308). A seam rather than an
    // HttpClient parameter so the decisions around it — which URL, what a 403
    // means, what is left on disk when a download dies — are tests against a
    // fake, and the one real implementation is exercised against a loopback
    // HttpListener in tests/IntegrationTests/UpdateDownloadTests.
    internal interface IUpdateHttp
    {
        Task<string> GetStringAsync(string url, CancellationToken cancellationToken);

        // Streams the body into destination, reporting cumulative bytes written.
        Task DownloadAsync(string url, Stream destination, IProgress<long>? bytesWritten, CancellationToken cancellationToken);
    }

    internal sealed class HttpUpdateHttp : IUpdateHttp, IDisposable
    {
        private readonly HttpClient _http;

        private readonly TimeSpan _metadataTimeout;

        // The metadata bound is a parameter only so a test can make it 100ms
        // instead of waiting 30 seconds for a hung server to be given up on.
        internal HttpUpdateHttp(TimeSpan? metadataTimeout = null)
        {
            _metadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(30);
            // 30 minutes covers a slow link pulling a ~35MB installer; a metadata
            // fetch gets its own much shorter bound below.
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(Brand.ShortName, NeuralSpeech.EngineVersion));
        }

        public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(_metadataTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            try
            {
                using var response = await _http.SendAsync(request, bounded.Token).ConfigureAwait(false);
                Check(response, url);
                return await response.Content.ReadAsStringAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
            {
                throw new UpdateException(UpdateFailure.Network, "Could not reach the update server.", ex);
            }
        }

        public async Task DownloadAsync(string url, Stream destination, IProgress<long>? bytesWritten, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                Check(response, url);

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    bytesWritten?.Report(total);
                }
            }
            catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
            {
                throw new UpdateException(UpdateFailure.Network, "Could not reach the update server.", ex);
            }
        }

        // 403 and 429 are GitHub's unauthenticated rate limit; automatic checks
        // treat those as silent, so they get their own kind rather than "Network".
        private static void Check(HttpResponseMessage response, string url)
        {
            if (response.IsSuccessStatusCode) return;
            var status = (int)response.StatusCode;
            throw response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? new UpdateException(UpdateFailure.RateLimited, $"GitHub is limiting requests right now (HTTP {status}); try again later.")
                : new UpdateException(UpdateFailure.Network, $"The update server answered HTTP {status} for {url}.");
        }

        // A transport failure, or our own metadata timeout, is Network. Caller
        // cancellation and our own UpdateExceptions are not, and pass through.
        // HttpIOException is a body cut off mid-stream; it is an IOException, so
        // without naming it here it would be mistaken for the local disk failing.
        private static bool IsTransportFailure(Exception ex, CancellationToken caller) =>
            ex is HttpRequestException or HttpIOException
            || (ex is OperationCanceledException && !caller.IsCancellationRequested);

        public void Dispose() => _http.Dispose();
    }

    internal static class UpdateDownloader
    {
        // Download the installer for this rid and prove it is the file the release
        // published. Returns the path of the verified installer.
        //
        // The order is the point: the sums are fetched and the installer's entry
        // found BEFORE any byte of the installer is downloaded (no entry, no
        // download); the bytes go to "<name>.partial" and only a verified file is
        // renamed to its real name. So a file at the real name has always been
        // checked, and a failure at any stage — mismatch, network, cancel, disk —
        // leaves neither a partial nor a final file behind.
        internal static async Task<string> DownloadVerifiedAsync(
            UpdateRelease release,
            string rid,
            string directory,
            IUpdateHttp http,
            IProgress<UpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            var (installer, sums) = UpdateAssets.Select(release, rid);

            var finalPath = Path.Combine(directory, installer.Name);
            var partialPath = finalPath + ".partial";
            var succeeded = false;
            try
            {
                Directory.CreateDirectory(directory);
                DeleteQuietly(finalPath);
                DeleteQuietly(partialPath);

                var sumsText = await http.GetStringAsync(sums.Url, cancellationToken).ConfigureAwait(false);
                var expected = UpdateChecksums.Expect(sumsText, installer.Name);

                var bytes = progress is null ? null : new DownloadProgress(progress, installer.Size);
                await using (var file = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await http.DownloadAsync(installer.Url, file, bytes, cancellationToken).ConfigureAwait(false);

                progress?.Report(new UpdateProgress(UpdateStage.Verifying, installer.Size, installer.Size));
                await UpdateChecksums.VerifyAsync(partialPath, expected, cancellationToken).ConfigureAwait(false);

                File.Move(partialPath, finalPath, overwrite: true);
                succeeded = true;
                return finalPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException(UpdateFailure.Storage, $"Could not save the update to disk: {ex.Message}", ex);
            }
            finally
            {
                if (!succeeded) DeleteQuietly(partialPath);
            }
        }

        // Synchronous on purpose: Progress<T> would post each report to the thread
        // pool, so the last byte count could arrive after DownloadVerifiedAsync had
        // returned. The caller's own IProgress (the UI's Progress<T>) decides about
        // threads; this just forwards in order.
        private sealed class DownloadProgress(IProgress<UpdateProgress> inner, long total) : IProgress<long>
        {
            public void Report(long written) => inner.Report(new UpdateProgress(UpdateStage.Downloading, written, total));
        }

        private static void DeleteQuietly(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
