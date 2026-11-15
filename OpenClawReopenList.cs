using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // CB-329: the "Open existing" picker's rows. Stub signatures only so the
    // window can be built against real types; bodies follow in the next commit.
    internal enum OpenClawReopenTarget { Session, Room }

    internal sealed record OpenClawReopenRow(
        OpenClawReopenTarget Target,
        string OrbSessionId,
        string Title,
        string PanelTitle,
        SessionKind Kind,
        OpenClawNativeKind Native,
        bool Heartbeat,
        IReadOnlyList<string> AgentIds,
        IReadOnlyList<string> MemberKeys,
        DateTime LastActivity);

    internal static class OpenClawReopenList
    {
        internal static IReadOnlyList<OpenClawReopenRow> Build(
            IReadOnlyList<OpenClawSessions.Session> listed,
            ClusterMode heartbeats, ClusterMode crons, ClusterMode native)
            => throw new NotImplementedException();

        internal static IReadOnlyList<OpenClawReopenRow> Filter(
            IReadOnlyList<OpenClawReopenRow> rows, string? agentId, string? search,
            Func<string, string> agentName)
            => throw new NotImplementedException();

        internal static string Badge(OpenClawReopenRow row) => throw new NotImplementedException();

        internal static string Ago(DateTime activityUtc, DateTime nowUtc) => throw new NotImplementedException();
    }
}
