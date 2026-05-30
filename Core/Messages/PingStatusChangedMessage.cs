using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Published by PingMonitorService on every completed ping attempt.
/// PingDashboardViewModel receives this and updates its ObservableCollection.
/// Neither layer holds a reference to the other — IMessenger is the only bridge.
/// </summary>
public sealed class PingStatusChangedMessage : ValueChangedMessage<PingResult>
{
    public PingStatusChangedMessage(PingResult result) : base(result) { }
}