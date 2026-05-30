using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Published by EventLogService after each fetch of recent log entries.
/// Carries the full replacement list — ViewModel clears and repopulates.
/// </summary>
public sealed class EventLogUpdatedMessage
    : ValueChangedMessage<IReadOnlyList<EventLogEntry>>
{
    public EventLogUpdatedMessage(IReadOnlyList<EventLogEntry> value) : base(value) { }
}