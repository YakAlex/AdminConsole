namespace AdminConsole.Core.Models;

/// <summary>
/// Запит на доступ до Telegram-бота від нового користувача (/start).
/// Живе виключно в пам'яті (TelegramAccessControlService) — не персистентний,
/// бо втрачений при перезапуску запит користувач просто надішле повторно.
/// </summary>
public sealed record TelegramPendingRequest(
    int            Id,
    long           ChatId,
    string         Username,
    DateTimeOffset RequestedAt);