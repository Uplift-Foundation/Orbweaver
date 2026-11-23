using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    internal enum PaperclipConnectOutcome
    {
        Connected,        // a key was issued, approved and verified
        ConnectedNoKey,   // local_trusted: no credential needed
        Cancelled,        // the user cancelled here or declined in Paperclip
        Expired,          // the challenge ran out, or its own expiry passed
        Refused,          // approvalUrl was not http or https
        Failed,           // anything else; Message says what
    }

    internal sealed record PaperclipConnectResult(
        PaperclipConnectOutcome Outcome, string? Key = null, DateTime? KeyExpires = null, string? Message = null);

    // The approval flow of plan 2.3, as a function over the transport, a
    // browser opener, a clock and a delay - so every branch is a unit test and
    // the suites never open a browser or wait a second.
    //
    // Release 1 issues exactly two writes: the challenge POST here and
    // revoke-current on disconnect. The key lives in memory until Paperclip's
    // own `cli-auth/me` accepts it; only then does the caller store it.
    internal static class PaperclipConnect
    {
        // Paperclip's default key lifetime, measured once by the lead ("expires
        // in 30 days"). Neither `me` nor the challenge reports it, so the
        // stored expiry is approval time plus this: an assumption, and the 401
        // on a poll is what is actually believed.
        internal static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(30);

        internal const int MaxFailedPolls = 3;

        // pollPath comes back as "/cli-auth/challenges/<id>" with no /api prefix
        // and no query (measured); the poll that works is under /api with the
        // challenge token. Accept either shape: prefix /api when it is absent and
        // append the token only when the path has no query already.
        internal static string PollPathFor(PaperclipApi.Challenge ch)
        {
            var path = ch.PollPath.StartsWith("/api/", StringComparison.Ordinal) ? ch.PollPath : "/api" + ch.PollPath;
            return path.Contains('?') ? path : path + "?token=" + Uri.EscapeDataString(ch.Token);
        }

        // The server builds approvalUrl from the address it was called on, so for
        // a Paperclip reached at a different address than it believes it has, the
        // absolute URL points somewhere the browser may not reach. When the hosts
        // differ and the server gave a path, the address the user connected to
        // plus that path is what opens. Unmeasured for a LAN instance.
        internal static string ApprovalUrlFor(string baseUrl, PaperclipApi.Challenge ch)
        {
            if (ch.ApprovalPath != null && ch.ApprovalPath.StartsWith('/')
                && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
                && Uri.TryCreate(ch.ApprovalUrl, UriKind.Absolute, out var a)
                // A non-web scheme is never rewritten into a safe-looking one: it is
                // refused by the caller, which is the point of checking it.
                && (a.Scheme == Uri.UriSchemeHttp || a.Scheme == Uri.UriSchemeHttps)
                && !string.Equals(a.Authority, b.Authority, StringComparison.OrdinalIgnoreCase))
                return baseUrl.TrimEnd('/') + ch.ApprovalPath;
            return ch.ApprovalUrl;
        }

        internal const string ChallengeBody =
            "{\"command\":\"Orbweaver\",\"clientName\":\"Orbweaver\",\"requestedAccess\":\"board\"}";

        // apiFor(null) is the unauthenticated transport; apiFor(key) one that
        // sends the key. openBrowser returns false when it could not.
        internal static async Task<PaperclipConnectResult> RunAsync(
            string baseUrl,
            Func<string?, IPaperclipApi> apiFor,
            Func<string, bool> openBrowser,
            Func<DateTime> now,
            Func<TimeSpan, CancellationToken, Task> delay,
            CancellationToken ct,
            Action<string>? progress = null)
        {
            var anon = apiFor(null);
            try
            {
                var health = await anon.GetAsync("/api/health", ct).ConfigureAwait(false);
                if (!health.IsOk) return Fail("Can't reach Paperclip." + Suffix(health));

                var mode = PaperclipApi.ParseHealth(health.Body)?.DeploymentMode;
                if (mode == "local_trusted")
                {
                    var open = await anon.GetAsync("/api/companies", ct).ConfigureAwait(false);
                    if (open.IsOk) return new PaperclipConnectResult(PaperclipConnectOutcome.ConnectedNoKey);
                    // Claimed local_trusted but refused: fall through to approval.
                }

                var post = await anon.PostAsync("/api/cli-auth/challenges", ChallengeBody, ct).ConfigureAwait(false);
                if (!post.IsOk) return Fail("Paperclip would not start an approval." + Suffix(post));
                var ch = PaperclipApi.ParseChallenge(post.Body);
                if (ch == null) return Fail("Paperclip's approval answer was not understood.");

                var approval = ApprovalUrlFor(baseUrl, ch);
                if (!Uri.TryCreate(approval, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return new PaperclipConnectResult(PaperclipConnectOutcome.Refused, Message: "Paperclip's approval link was not a web address.");

                if (!openBrowser(approval))
                    return Fail("Couldn't open the browser.");
                progress?.Invoke("Approve Orbweaver in your browser…");

                var pollPath = PollPathFor(ch);
                var failedPolls = 0;
                var interval = TimeSpan.FromMilliseconds(ch.PollIntervalMs);
                var deadline = ch.ExpiresAt ?? now() + TimeSpan.FromMinutes(10);

                while (true)
                {
                    if (now() >= deadline) return new PaperclipConnectResult(PaperclipConnectOutcome.Expired);
                    await delay(interval, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    var poll = await anon.GetAsync(pollPath, ct).ConfigureAwait(false);
                    if (!poll.IsOk)
                    {
                        // A hiccup is ridden out, but a poll that keeps failing (a
                        // wrong path answers 404 for ever) ends the attempt with a
                        // reason instead of spinning to the challenge's expiry.
                        if (++failedPolls >= MaxFailedPolls)
                            return Fail("Paperclip stopped answering the approval check ("
                                + (poll.StatusCode > 0 ? "HTTP " + poll.StatusCode : poll.Kind.ToString()) + ").");
                        continue;
                    }
                    failedPolls = 0;
                    switch (PaperclipApi.ParseChallengeStatus(poll.Body))
                    {
                        case "approved":
                            var me = await apiFor(ch.BoardApiToken).GetAsync("/api/cli-auth/me", ct).ConfigureAwait(false);
                            if (!me.IsOk) return Fail("Paperclip approved Orbweaver but the key did not work.");
                            return new PaperclipConnectResult(PaperclipConnectOutcome.Connected,
                                ch.BoardApiToken, now() + KeyLifetime);
                        case "cancelled":
                            return new PaperclipConnectResult(PaperclipConnectOutcome.Cancelled);
                        case "expired":
                            return new PaperclipConnectResult(PaperclipConnectOutcome.Expired);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new PaperclipConnectResult(PaperclipConnectOutcome.Cancelled);
            }

            static PaperclipConnectResult Fail(string m) => new(PaperclipConnectOutcome.Failed, Message: m);
            static string Suffix(PaperclipResponse r) => r.Detail == null ? "" : " " + r.Detail;
        }

        // Several companies means the user picks; one means it is chosen; the
        // stored choice wins while it is still in the list.
        internal static PaperclipApi.Company? ChooseCompany(
            IReadOnlyList<PaperclipApi.Company> companies, string? storedId)
        {
            if (companies.Count == 0) return null;
            foreach (var c in companies) if (c.Id == storedId) return c;
            return companies.Count == 1 ? companies[0] : null;
        }

        internal static async Task<IReadOnlyList<PaperclipApi.Company>> ListCompaniesAsync(IPaperclipApi api, CancellationToken ct)
        {
            var res = await api.GetAsync("/api/companies", ct).ConfigureAwait(false);
            return res.IsOk ? PaperclipApi.ParseCompanies(res.Body) : Array.Empty<PaperclipApi.Company>();
        }

        // Revoke first, then forget the local copy, and forget it even when the
        // revoke failed: a key nobody can use is worse kept than lost. The
        // return value is what the UI says: true when Paperclip confirmed.
        internal static async Task<bool> DisconnectAsync(IPaperclipApi? authed, Action deleteLocal, CancellationToken ct)
        {
            var revoked = false;
            try
            {
                if (authed != null)
                    revoked = (await authed.PostAsync("/api/cli-auth/revoke-current", "{}", ct).ConfigureAwait(false)).IsOk;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally { deleteLocal(); }
            return revoked;
        }
    }
}
