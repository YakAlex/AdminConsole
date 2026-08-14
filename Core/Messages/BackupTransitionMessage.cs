using AdminConsole.Core.Models;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Публікується BackupMonitorService.OnConfirmedTransition ЛИШЕ коли
/// підтверджений перехід у Stale/Missing і НЕ під активним Maintenance-
/// вікном — тобто саме тоді, коли варто розбудити людину.
/// TelegramBotService підписується і розсилає push, той самий принцип,
/// що BroadcastIncidentAlertAsync для DowntimeRecord.
/// </summary>
public sealed class BackupTransitionMessage
{
    public required string        ServerName { get; init; }
    public required BackupKind    Kind       { get; init; }
    public required BackupOutcome Previous   { get; init; }
    public required BackupOutcome Current    { get; init; }
}