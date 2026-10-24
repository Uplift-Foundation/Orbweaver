namespace Orbweaver
{
    // Which bearer the HTTP media fetches present to the gateway (CB-278).
    //
    // Two are on hand. The gateway's shared secret (`gateway.auth.token`) is
    // what every connect carries, and over HTTP the gateway scores it as the
    // CLI's default operator scopes: admin, read, write, approvals, questions,
    // pairing, talk.secrets. That is its rule for that credential, read from
    // `http-auth-utils` on 2026.10.5 and measured: the shared secret passes two
    // different admin gates on the media route. The device token is the one
    // hello-ok issues to this paired device, and the same route scores it as
    // `device-token` with exactly the scopes the user approved, read-only
    // unless reply is on. It is refused at both admin gates with the right
    // reason, and fetches a picture and a clip byte for byte the same.
    //
    // So the device token wins whenever one is held. The shared secret is the
    // fallback, and the fallback is today's behaviour, named rather than
    // hidden: a gateway that never issues a device token, or a first connect
    // that has not completed yet, still gets its pictures, over the
    // admin-scored credential the app always used. It is never a retry after
    // the device token is refused, because a 401 on the device token falling
    // through to an admin credential would undo the point of picking it.
    //
    // Pure and separate from the fetchers, which are excluded from coverage for
    // being a real HTTP GET, so the decision is not excluded with them.
    internal static class OpenClawMediaCredential
    {
        internal enum Kind { None, Device, Shared }

        internal static (string? Token, Kind Kind) Pick(string? deviceToken, string? gatewayToken) =>
            !string.IsNullOrWhiteSpace(deviceToken) ? (deviceToken, Kind.Device) :
            !string.IsNullOrWhiteSpace(gatewayToken) ? (gatewayToken, Kind.Shared) :
            (null, Kind.None);

        // What both fetchers call: the two lookups and the pick, in one place
        // outside their coverage exclusion. This is the line CB-278 changes,
        // and while it sat inside the excluded fetchers, swapping its two
        // arguments or reverting it to GatewayTokenFor alone (straight back to
        // admin over HTTP) left every test green. A blank host has no
        // credential, which is the same early return the fetchers made
        // themselves.
        internal static (string? Token, Kind Kind) For(string? host) =>
            string.IsNullOrWhiteSpace(host)
                ? (null, Kind.None)
                : Pick(OpenClawIdentity.TokenFor(host), OpenClawIdentity.GatewayTokenFor(host));
    }
}
