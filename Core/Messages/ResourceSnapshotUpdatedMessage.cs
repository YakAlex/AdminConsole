using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Published by ResourceMonitorService on every poll cycle.
/// </summary>
public sealed class ResourceSnapshotUpdatedMessage
    : ValueChangedMessage<ResourceSnapshot>
{
    public ResourceSnapshotUpdatedMessage(ResourceSnapshot value) : base(value) { }
}