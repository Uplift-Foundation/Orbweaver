using System;

namespace Orbweaver
{
    // CB-109: the fully-formed assistant-media request for a picture on the
    // gateway — the file, and *whose* conversation named it.
    //
    // The gateway's assistant-media route resolves its media policy against a
    // session. This app never sent one, so every fetch was judged against a
    // server-side *default* agent instead of the agent whose reply named the
    // file, and a picture in an agent's own workspace came back
    //
    //   {"available":false,"code":"outside-allowed-folders"}
    //
    // for a file that was on disk and that the gateway would have served
    // happily to the right asker. Measured against a healthy gateway (OpenClaw
    // 2026.9.2, 3928bad): with no sessionKey the file is refused; with
    // `sessionKey=agent:comfyui:discord:direct:100000000000000001` and nothing
    // else it answers `available:true` and then 200 with 1,116,874 bytes.
    //
    // **The session key is the whole of it — this deliberately does not send
    // an `agentId`, and adding one back would be a regression.** Three
    // measurements say so, and the third is what makes the case:
    //
    //   sessionKey only                  meta available:true, bytes 200
    //   agentId=comfyui, no sessionKey   refused — the parameter is inert
    //   agentId=main + comfyui's key     404 Not Found
    //   agentId=comfyui + comfyui's key  available:true (the control)
    //
    // The gateway's handler reads them as
    //
    //   const sessionKey = url.searchParams.get("sessionKey")?.trim() || void 0;
    //   const agentId = sessionKey ? url.searchParams.get("agentId")?.trim() || void 0 : opts?.agentId;
    //
    // so `agentId` is consulted only when a `sessionKey` came with it — alone
    // it changes nothing at all while looking exactly like the fix, and the
    // refusal it leaves behind is indistinguishable from a real permissions
    // problem. Two agents working this ticket sent it alone and read the same
    // refusal. With the key present and `agentId` absent the server resolves
    // the agent from the key itself, which is strictly more authoritative than
    // anything this process can derive; the matching-id control returning 200
    // is what proves the 404 above isolates the *mismatch* rather than the
    // parameter's presence.
    //
    // So the key carries the provenance and `agentId` carries no additional
    // information — only a way to disagree with the key and 404 a fetch that
    // was going to succeed. Deriving it client-side would have been safe and
    // would have made a six-line parse load-bearing forever; omitting it
    // cannot fail that way at all. Same argument that retired
    // OpenClawMediaRefusal.PathFromUrl: delete rather than carefully maintain.
    //
    // **Parameter order is load-bearing, and so is the cache key. Neither is
    // tidying.** `source` stays first because the app recognises one of these
    // routes by prefix (OpenClawSessions.AssistantMediaPathPrefix, and
    // AssistantMediaRoute which includes `?source=`), and because
    // FetchMediaAsync keys its cache on the whole url — so the identity being
    // *in* the url is what stops bytes fetched under one session being served
    // to another. Move `source` off the front, or key the cache on the path
    // instead, and both properties go quietly, with no test failing and no
    // picture visibly breaking.
    //
    // CB-277: that was measured for images. For audio and video the folder
    // allowlist still applies under whatever session is named, and only a
    // session of the agent whose workspace holds the file clears it — see
    // OpenClawMediaOwner, which picks that session from agents.list rather
    // than guessing.
    //
    // What this does **not** do is guess at what the gateway will allow.
    // Measured from two directions: supplying a session does not scope the
    // request to that agent's own workspace, it disables the folder allowlist
    // outright — a `workspace-sample-agent` file serves under `agent:main`'s
    // session, and `~/Desktop`, `/tmp` and other agents' workspaces all clear
    // the folder check and fail only on existence or media type (`/etc/passwd`
    // answers `unsupported-media-type` for meta and 404 for bytes). Owner has
    // signed off on that trust model: send the session, fetch the path the
    // agent named, and let the gateway judge it. The one thing this app still
    // refuses on its own is traversal — see LooksLikeAnImagePath, which is now
    // the only structural guard left on this path and must not be deleted as
    // redundant.
    //
    // Pure — no window, no settings, no I/O — the same habit as OrbGlyph and
    // OrbArrangement, so the exact query string this app sends is one
    // constructor away from a test rather than something only visible on a
    // real gateway. It is a route *builder* and is deliberately not stored on
    // a ChatTurn: that model is transport-agnostic and does not know what a
    // gateway is. What a turn carries is the finished url and the clean path
    // beside it (ChatTurn.ImageSourcePath).
    //
    // No mediaTicket. The `&meta=1` answer carries a `mediaTicket` and an
    // expiry, and it looks like a credential the byte fetch ought to present;
    // it is not. The bytes come back 200 with the sessionKey alone, measured,
    // so threading a ticket through would put a mandatory meta round trip on
    // the critical path of every picture the panel draws to obtain something
    // the server does not ask for.
    //
    // **A null SessionKey is permitted here and does not happen in
    // production.** The type stays permissive on purpose — the no-session
    // route is the byte-identical shape the app sent before this ticket, and
    // pinning that is what proves nothing already working was changed. But
    // every production producer has a key in hand and passes it:
    // TurnsFromHistory has one call site, which holds chat.GatewayKey;
    // TryResolveLocalMedia is a method on the session; TryResolveLiveImage
    // matches against history turns that were parsed with it; a room parses
    // each member's page with that member's own key. GatewayKey is itself
    // structurally non-null (ChatFor refuses an id that is not
    // "openclaw:<key>"), and all 54 live session keys read off a real
    // sessions.list are "agent:<id>:…".
    //
    // So the invariant does not live in this type — it lives at the call
    // sites, and what keeps it true is the three tests asserting that each
    // picture-producing arm of TurnsFromHistory carries the session. Those are
    // load-bearing for a documented invariant rather than coverage: a producer
    // added later without a key is what they exist to fail on. Read
    // "nullable" here as "a fixture may omit it", not as a case to write
    // defensive code for.
    internal readonly record struct OpenClawMediaSource(string Path, string? SessionKey)
    {
        // The same request asking a capability question instead of bytes.
        //
        // Takes the finished url rather than rebuilding one, and that is the
        // whole point: an explanation asked with a different identity than the
        // fetch does not fail — it *lies*, which is worse than the silence
        // CB-93 set out to remove. Appending a flag to the exact string that
        // was fetched is the only way the two cannot come apart.
        internal static string MetaOf(string route) => route + "&meta=1";

        // The session a finished route was built with, or null when it names
        // none (the no-session shape, or a url that is not one of these).
        // Read back out of the query rather than stored beside it, so what a
        // tooltip reports is what the request actually carried. Tolerates the
        // &meta=1 suffix and any parameter order after `source`.
        internal static string? SessionKeyOf(string? route)
        {
            if (string.IsNullOrEmpty(route)) return null;

            var q = route.IndexOf('?');
            if (q < 0) return null;

            foreach (var pair in route[(q + 1)..].Split('&'))
            {
                const string Name = "sessionKey=";
                if (!pair.StartsWith(Name, StringComparison.Ordinal)) continue;

                var value = Uri.UnescapeDataString(pair[Name.Length..]);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }

        // The GET path for the bytes, fully formed — what ChatTurn.ImageUrl
        // holds and what TurnView.LoadImage fetches verbatim.
        //
        // With no session this is byte-identical to what the app sent before
        // this type existed: prefix plus the escaped path, nothing else. Every
        // picture that loads today loads through a route of exactly that shape,
        // and a type introduced to fix a refusal must not quietly change the
        // requests that were already working.
        //
        // Both values are percent-escaped, the sessionKey included: its colons
        // are legal in a query value, but escaping them costs nothing and means
        // one rule for both fields rather than a judgement call per field about
        // what this particular server tolerates.
        internal string Route
        {
            get
            {
                // `source` first. See the header: the app recognises one of
                // these routes by prefix, and putting anything ahead of
                // `source` would make that recognition stop matching — which
                // costs the CB-93 explanation and nothing louder.
                var route = OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(Path);

                return string.IsNullOrWhiteSpace(SessionKey)
                    ? route
                    : route + "&sessionKey=" + Uri.EscapeDataString(SessionKey!);
            }
        }

        internal string MetaRoute => MetaOf(Route);
    }
}
