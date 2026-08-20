using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Published by RdpMonitorService after each quser poll for a server.
/// Carries the full replacement list for that server.
/// </summary>
public sealed class RdpSessionsUpdatedMessage
    : ValueChangedMessage<RdpSessionsPayload>
{
    public RdpSessionsUpdatedMessage(RdpSessionsPayload payload) : base(payload) { }
}

public sealed record RdpSessionsPayload(
    string                      ServerName,
    string                      ServerIp,
    IReadOnlyList<RdpSessionInfo> Sessions,
    string?                     ErrorMessage,
    int                           GlobalDailyPeak,
    string?                       LastLogoutUsername,
    string?                       LastLogoutServer,
    DateTimeOffset?               LastLogoutAt
);