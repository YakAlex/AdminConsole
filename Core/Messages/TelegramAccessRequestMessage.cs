using AdminConsole.Core.Models;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Push у WPF, коли прийшов новий /start від неавторизованого chat_id.
/// SettingsViewModel підписується, щоб показати pending-запит як backup-канал
/// (на випадок якщо Primary Admin не в мережі в Telegram, але сидить за WPF).
/// </summary>
public sealed class TelegramAccessRequestMessage
{
    public required TelegramPendingRequest Request { get; init; }
}