using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Published by ZabbixPollerService after each successful or failed poll.
/// On failure, Problems is empty and ErrorMessage is set.
/// </summary>
public sealed class ZabbixProblemsUpdatedMessage
    : ValueChangedMessage<ZabbixProblemsPayload>
{
    public ZabbixProblemsUpdatedMessage(ZabbixProblemsPayload payload)
        : base(payload) { }
}

public sealed record ZabbixProblemsPayload(
    IReadOnlyList<ZabbixProblem> Problems,
    string?                      ErrorMessage,
    DateTimeOffset               FetchedAt
);