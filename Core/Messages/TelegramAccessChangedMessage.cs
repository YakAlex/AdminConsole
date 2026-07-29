namespace AdminConsole.Core.Messages;

public enum TelegramAccessAction { Approved, Denied, Revoked }

/// <summary>
/// Push у WPF при approve/deny/revoke (з будь-якого каналу — Telegram-кнопка
/// чи WPF Settings) — синхронізує список pending/users в обох UI одночасно.
/// </summary>
public sealed class TelegramAccessChangedMessage
{
    public required TelegramAccessAction Action { get; init; }
    public required long                 ChatId { get; init; }
    public string?                       Username { get; init; }
}