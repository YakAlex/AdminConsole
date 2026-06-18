using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Published by PingMonitorService once per full poll cycle.
/// Contains results for ALL servers — one message per cycle instead of
/// one message per server. PingDashboardViewModel applies all updates
/// in a single Dispatcher.InvokeAsync call.
/// </summary>
public sealed class PingBatchResultMessage : ValueChangedMessage<PingBatchPayload>
{
    public PingBatchResultMessage(PingBatchPayload payload) : base(payload) { }
}

public sealed record PingBatchPayload(
    IReadOnlyList<PingResult> Results,
    DateTimeOffset            CycleCompletedAt
);