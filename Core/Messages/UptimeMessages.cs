using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Публікується UptimeTrackerService при кожній зміні списку інцидентів.
/// UptimeViewModel підписується і оновлює UI.
/// </summary>
public sealed class UptimeUpdatedMessage
    : ValueChangedMessage<IReadOnlyList<DowntimeRecord>>
{
    public UptimeUpdatedMessage(IReadOnlyList<DowntimeRecord> records)
        : base(records) { }
}