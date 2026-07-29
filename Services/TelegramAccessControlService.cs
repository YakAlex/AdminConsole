using System.Collections.Concurrent;
using System.Security.Cryptography;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Services;

/// <summary>
/// Централізована перевірка доступу до Telegram-бота: Primary Admin,
/// approval-флоу для інших користувачів, rate limiting.
///
/// Singleton — стан (AllowedChatIds, PrimaryAdminChatId) персистентний
/// через UserSettingsService; pending-запити та claim-код — лише в пам'яті.
/// </summary>
public sealed class TelegramAccessControlService
{
    private readonly UserSettingsService                  _userSettings;
    private readonly IMessenger                           _messenger;
    private readonly ILogger<TelegramAccessControlService> _logger;

    private const string LogSource = "TelegramAccess";

    // ── Pending requests ────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<int, TelegramPendingRequest> _pending = new();
    private int _nextPendingId;

    // ── Claim-код для Primary Admin ─────────────────────────────────────────

    private string?         _claimCode;
    private DateTimeOffset  _claimCodeExpiresAt;
    private readonly object _claimLock = new();

    // ── Rate limiting ────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<long, ConcurrentQueue<DateTimeOffset>> _rateLimits = new();
    private const int    RateLimitMaxActions = 10;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    public TelegramAccessControlService(
        UserSettingsService                  userSettings,
        IMessenger                           messenger,
        ILogger<TelegramAccessControlService> logger)
    {
        _userSettings = userSettings;
        _messenger    = messenger;
        _logger       = logger;
    }

    // ── Primary Admin claim-флоу ─────────────────────────────────────────────

    public bool IsPrimaryAdminClaimed => _userSettings.Current.TelegramPrimaryAdminChatId is not null;

    public bool IsPrimaryAdmin(long chatId) =>
        _userSettings.Current.TelegramPrimaryAdminChatId == chatId;
    
    /// <summary>
    /// Поточний chat_id Primary Admin, якщо вже прив'язаний. Потрібен
    /// TelegramBotService, щоб знати, кому саме слати повідомлення
    /// "новий запит доступу від @username" — без цього бот не знав би,
    /// в який чат писати approve/deny-кнопки.
    /// </summary>
    public long? PrimaryAdminChatId => _userSettings.Current.TelegramPrimaryAdminChatId;

    /// <summary>
    /// Генерує 6-значний код, дійсний 10 хв. Викликається з SettingsViewModel
    /// (кнопка "Згенерувати код прив'язки адміна"). Лише в пам'яті — код
    /// не переживає перезапуск застосунку (це нормально, безпечніше).
    /// </summary>
    public string GenerateClaimCode()
    {
        lock (_claimLock)
        {
            _claimCode          = RandomNumberGenerator.GetInt32(100_000, 999_999).ToString();
            _claimCodeExpiresAt = DateTimeOffset.Now.AddMinutes(10);
            return _claimCode;
        }
    }

    /// <summary>
    /// Обробляє /claim_admin &lt;код&gt;. Повертає true, якщо прив'язка успішна.
    /// </summary>
    public bool TryClaimAdmin(string code, long chatId)
    {
        if (IsPrimaryAdminClaimed) return false;

        lock (_claimLock)
        {
            if (_claimCode is null || DateTimeOffset.Now > _claimCodeExpiresAt)
                return false;

            if (!string.Equals(_claimCode, code.Trim(), StringComparison.Ordinal))
                return false;

            _claimCode = null; // одноразовий — гасимо одразу після використання
        }

        _userSettings.Current.TelegramPrimaryAdminChatId = chatId;
        _userSettings.Save();

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Telegram Primary Admin прив'язано: chat_id={chatId}."));

        return true;
    }

    /// <summary>Скидання прив'язки — виключно вручну з WPF Settings.</summary>
    public void ResetPrimaryAdmin()
    {
        _userSettings.Current.TelegramPrimaryAdminChatId = null;
        _userSettings.Save();
        _messenger.Send(AppLogEntryMessage.Warning(LogSource,
            "Telegram Primary Admin відв'язано вручну з Settings."));
    }

    // ── Доступ read-only користувачів ────────────────────────────────────────

    public bool IsAllowed(long chatId) =>
        IsPrimaryAdmin(chatId) || _userSettings.Current.TelegramAllowedChatIds.Contains(chatId);

    public IReadOnlyList<long> GetAllowedChatIds() =>
        _userSettings.Current.TelegramAllowedChatIds.ToList();

    /// <summary>Відкликає доступ. Доступно лише виклику від Primary Admin (перевіряє викликач).</summary>
    public bool Revoke(long chatId)
    {
        bool removed = _userSettings.Current.TelegramAllowedChatIds.Remove(chatId);
        if (!removed) return false;

        _userSettings.Save();
        _messenger.Send(AppLogEntryMessage.Info(LogSource, $"Telegram доступ відкликано: chat_id={chatId}."));
        _messenger.Send(new TelegramAccessChangedMessage
        {
            Action = TelegramAccessAction.Revoked,
            ChatId = chatId
        });
        return true;
    }

    // ── Pending requests (approval-флоу) ────────────────────────────────────

    /// <summary>
    /// Реєструє новий /start від неавторизованого chat_id. Повертає
    /// short-ID pending-запиту (для approve:&lt;id&gt;/deny:&lt;id&gt; callback_data).
    /// </summary>
    public TelegramPendingRequest RegisterPendingRequest(long chatId, string username)
    {
        // Якщо запит від цього chat_id вже є в pending — не дублюємо,
        // повертаємо існуючий (людина могла кілька разів натиснути /start).
        var existing = _pending.Values.FirstOrDefault(p => p.ChatId == chatId);
        if (existing is not null) return existing;

        int id = Interlocked.Increment(ref _nextPendingId);
        var request = new TelegramPendingRequest(id, chatId, username, DateTimeOffset.Now);
        _pending[id] = request;

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Новий запит доступу: @{username} (chat_id={chatId})."));
        _messenger.Send(new TelegramAccessRequestMessage { Request = request });

        return request;
    }

    public TelegramPendingRequest? TryGetPending(int id) =>
        _pending.TryGetValue(id, out var r) ? r : null;

    public IReadOnlyList<TelegramPendingRequest> GetAllPending() => _pending.Values.ToList();

    /// <summary>
    /// Критика #4 (stale callbacks): якщо запиту вже немає в _pending
    /// (вирішено іншим каналом раніше) — повертає false, виклик має
    /// показати "запит вже неактуальний" і НЕ падати.
    /// </summary>
    public bool Approve(int id)
    {
        if (!_pending.TryRemove(id, out var request)) return false;

        var list = _userSettings.Current.TelegramAllowedChatIds;
        if (!list.Contains(request.ChatId))
            list.Add(request.ChatId);
        _userSettings.Save();

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Доступ дозволено: @{request.Username} (chat_id={request.ChatId})."));
        _messenger.Send(new TelegramAccessChangedMessage
        {
            Action   = TelegramAccessAction.Approved,
            ChatId   = request.ChatId,
            Username = request.Username
        });
        return true;
    }

    public bool Deny(int id)
    {
        if (!_pending.TryRemove(id, out var request)) return false;

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Доступ відхилено: @{request.Username} (chat_id={request.ChatId})."));
        _messenger.Send(new TelegramAccessChangedMessage
        {
            Action   = TelegramAccessAction.Denied,
            ChatId   = request.ChatId,
            Username = request.Username
        });
        return true;
    }

    // ── Rate limiting ────────────────────────────────────────────────────────

    /// <summary>true — дія дозволена; false — перевищено ліміт (10 дій/хв).</summary>
    public bool CheckRateLimit(long chatId)
    {
        var queue = _rateLimits.GetOrAdd(chatId, _ => new ConcurrentQueue<DateTimeOffset>());
        var now   = DateTimeOffset.Now;

        // Прибираємо застарілі мітки часу з початку черги
        while (queue.TryPeek(out var oldest) && now - oldest > RateLimitWindow)
            queue.TryDequeue(out _);

        if (queue.Count >= RateLimitMaxActions)
            return false;

        queue.Enqueue(now);
        return true;
    }
}