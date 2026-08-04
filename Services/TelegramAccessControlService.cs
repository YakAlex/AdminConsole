using System.Collections.Concurrent;
using System.Security.Cryptography;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Services;

/// <summary>Пара chat_id + username для відображення в списку дозволених користувачів.</summary>
public sealed record TelegramAllowedUser(long ChatId, string Username);

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

    /// <summary>
    /// TTL для pending-запитів — якщо Primary Admin ігнорує запит довше
    /// цього часу, він автоматично прибирається (як мовчазний "timeout",
    /// не Deny — кулдауну з Плану B тут НЕ ставимо, бо адмін просто
    /// не встиг відреагувати, це не свідома відмова).
    /// </summary>
    private static readonly TimeSpan PendingRequestTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Верхня межа одночасних pending-запитів. Захист від сценарію, коли
    /// атакуючий створює запити з БАГАТЬОХ різних chat_id (не одного й того
    /// самого — той випадок уже покритий кулдауном Плану B) — понад цю
    /// межу нові /start від НОВИХ chat_id тихо відхиляються без push адміну,
    /// поки старі pending не буде оброблено або не спливе TTL.
    /// </summary>
    private const int MaxPendingRequests = 50;

    //Кулдаун на повторний запит після Deny/Revoke ────────────────

    /// <summary>
    /// chat_id → момент, до якого повторний /start НЕ створює новий
    /// pending-запит і НЕ шле push адміну. Заповнюється при Deny і при
    /// Revoke — обидва сценарії дозволяли раніше миттєво "перезапитати"
    /// доступ і засипати Primary Admin новими сповіщеннями без обмежень.
    /// </summary>
    private readonly ConcurrentDictionary<long, DateTimeOffset> _requestCooldownUntil = new();
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(15);

    // ── Claim-код для Primary Admin ─────────────────────────────────────────

    private string?         _claimCode;
    private DateTimeOffset  _claimCodeExpiresAt;
    private readonly object _claimLock = new();

    // ── Rate limiting ────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<long, ConcurrentQueue<DateTimeOffset>> _rateLimits = new();
    private const int    RateLimitMaxActions = 10;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    // ── Окремий, суворіший throttle для /ping ────────────────────────────────
    private readonly ConcurrentDictionary<long, DateTimeOffset> _pingCooldownUntil = new();
    private static readonly TimeSpan PingCooldown = TimeSpan.FromSeconds(20);
    
    /// <summary>
    /// Результат спроби зареєструвати pending-запит:
    ///  - Request заповнено   → запит створено (або вже існував — дедуп).
    ///  - Request == null     → chat_id ще в кулдауні після Deny/Revoke;
    ///                          CooldownRemaining підказує, скільки ще чекати.
    /// </summary>
    public readonly record struct PendingRequestResult(
        TelegramPendingRequest? Request,
        TimeSpan?               CooldownRemaining);

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

    public bool IsPrimaryAdminClaimed =>
        _userSettings.ReadTelegramState(s => s.TelegramPrimaryAdminChatId is not null);

    public bool IsPrimaryAdmin(long chatId) =>
        _userSettings.ReadTelegramState(s => s.TelegramPrimaryAdminChatId == chatId);
    
    /// <summary>
    /// Поточний chat_id Primary Admin, якщо вже прив'язаний. Потрібен
    /// TelegramBotService, щоб знати, кому саме слати повідомлення
    /// "новий запит доступу від @username" — без цього бот не знав би,
    /// в який чат писати approve/deny-кнопки.
    /// </summary>
    public long? PrimaryAdminChatId =>
        _userSettings.ReadTelegramState(s => s.TelegramPrimaryAdminChatId);
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

        _userSettings.MutateTelegramState(s => s.TelegramPrimaryAdminChatId = chatId);

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Telegram Primary Admin прив'язано: chat_id={chatId}."));

        return true;
    }

    /// <summary>Скидання прив'язки — виключно вручну з WPF Settings.</summary>
    public void ResetPrimaryAdmin()
    {
        _userSettings.MutateTelegramState(s => s.TelegramPrimaryAdminChatId = null);
        _messenger.Send(AppLogEntryMessage.Warning(LogSource,
            "Telegram Primary Admin відв'язано вручну з Settings."));
    }

    // ── Доступ read-only користувачів ────────────────────────────────────────

    public bool IsAllowed(long chatId) =>
        IsPrimaryAdmin(chatId) ||
        _userSettings.ReadTelegramState(s => s.TelegramAllowedChatIds.Contains(chatId));

    public IReadOnlyList<long> GetAllowedChatIds() =>
        _userSettings.ReadTelegramState(s => s.TelegramAllowedChatIds.ToList());

    /// <summary>
    /// Список дозволених користувачів разом з username — для показу
    /// в WPF Settings і в боті ("👥 Користувачі"), у тому ж вигляді,
    /// що й запити на approve ("@username (chat_id=...)").
    /// Якщо username з якоїсь причини невідомий (старі записи до цієї фічі,
    /// або approve стався ще до додавання username-трекінгу) — показуємо
    /// "невідомо", а не падаємо чи ховаємо запис.
    /// </summary>
    public IReadOnlyList<TelegramAllowedUser> GetAllowedUsers() =>
        _userSettings.ReadTelegramState(s => s.TelegramAllowedChatIds
            .Select(chatId => new TelegramAllowedUser(
                chatId,
                s.TelegramUsernames.TryGetValue(chatId, out var u) ? u : "невідомо"))
            .ToList());

    /// <summary>Відкликає доступ. Доступно лише виклику від Primary Admin (перевіряє викликач).</summary>
    public bool Revoke(long chatId)
    {
        bool removed = false;

        _userSettings.MutateTelegramState(s =>
        {
            removed = s.TelegramAllowedChatIds.Remove(chatId);
            if (removed) s.TelegramUsernames.Remove(chatId);
        });

        if (!removed) return false;
        _requestCooldownUntil[chatId] = DateTimeOffset.Now.Add(RequestCooldown);

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Telegram доступ відкликано: chat_id={chatId}. " +
            $"Кулдаун на повторний запит: {RequestCooldown.TotalMinutes} хв."));
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
    public PendingRequestResult RegisterPendingRequest(long chatId, string username)
    {
        // Якщо запит від цього chat_id вже є в pending — не дублюємо,
        // повертаємо існуючий (людина могла кілька разів натиснути /start).
        var existing = _pending.Values.FirstOrDefault(p => p.ChatId == chatId);
        if (existing is not null) return new PendingRequestResult(existing, null);

        if (_requestCooldownUntil.TryGetValue(chatId, out var until))
        {
            var remaining = until - DateTimeOffset.Now;
            if (remaining > TimeSpan.Zero)
                return new PendingRequestResult(null, remaining);

            _requestCooldownUntil.TryRemove(chatId, out _);
        }

        PurgeExpiredPending();
        PurgeExpiredThrottleState();

        // Верхня межа кількості одночасних pending — захист від атаки
        // "багато різних chat_id" (на відміну від кулдауну Плану B,
        // який захищає лише від ПОВТОРНОГО запиту з ОДНОГО й того ж chat_id).
        if (_pending.Count >= MaxPendingRequests)
        {
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                $"Досягнуто ліміту pending-запитів ({MaxPendingRequests}). " +
                $"Новий запит від chat_id={chatId} відхилено без реєстрації."));
            return new PendingRequestResult(null, null);
        }

        int id = Interlocked.Increment(ref _nextPendingId);
        var request = new TelegramPendingRequest(id, chatId, username, DateTimeOffset.Now);
        _pending[id] = request;

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Новий запит доступу: @{username} (chat_id={chatId})."));
        _messenger.Send(new TelegramAccessRequestMessage { Request = request });

        return new PendingRequestResult(request, null);
    }
    
    /// <summary>Прибирає протерміновані (TTL) pending-запити. Дешево викликати часто.</summary>
    private void PurgeExpiredPending()
    {
        var cutoff = DateTimeOffset.Now - PendingRequestTtl;
        foreach (var (id, request) in _pending)
        {
            if (request.RequestedAt < cutoff)
                _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Прибирає протерміновані записи з cooldown/rate-limit словників.
    /// Без цього _requestCooldownUntil, _pingCooldownUntil і _rateLimits
    /// ростуть необмежено для кожного нового chat_id, що написав боту —
    /// на відміну від _pending, який обмежений MaxPendingRequests.
    /// Викликається разом з PurgeExpiredPending (той самий "дешево часто" підхід).
    /// </summary>
    private void PurgeExpiredThrottleState()
    {
        var now = DateTimeOffset.Now;

        foreach (var (chatId, until) in _requestCooldownUntil)
            if (until <= now)
                _requestCooldownUntil.TryRemove(chatId, out _);

        foreach (var (chatId, until) in _pingCooldownUntil)
            if (until <= now)
                _pingCooldownUntil.TryRemove(chatId, out _);

        foreach (var (chatId, queue) in _rateLimits)
        {
            if (queue.IsEmpty ||
                (queue.TryPeek(out var oldest) && now - oldest > RateLimitWindow))
            {
                _rateLimits.TryRemove(chatId, out _);
            }
        }
    }
    
    public TelegramPendingRequest? TryGetPending(int id) =>
        _pending.TryGetValue(id, out var r) ? r : null;

    public IReadOnlyList<TelegramPendingRequest> GetAllPending()
    {
        PurgeExpiredPending();
        return _pending.Values.ToList();
    }

    /// <summary>
    /// Критика #4 (stale callbacks): якщо запиту вже немає в _pending
    /// (вирішено іншим каналом раніше) — повертає false, виклик має
    /// показати "запит вже неактуальний" і НЕ падати.
    /// </summary>
    public bool Approve(int id)
    {
        if (!_pending.TryRemove(id, out var request)) return false;

        _userSettings.MutateTelegramState(s =>
        {
            if (!s.TelegramAllowedChatIds.Contains(request.ChatId))
                s.TelegramAllowedChatIds.Add(request.ChatId);

            s.TelegramUsernames[request.ChatId] = request.Username;
        });

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

        _requestCooldownUntil[request.ChatId] = DateTimeOffset.Now.Add(RequestCooldown);

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Доступ відхилено: @{request.Username} (chat_id={request.ChatId}). " +
            $"Кулдаун на повторний запит: {RequestCooldown.TotalMinutes} хв."));
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
    
    /// <summary>
    /// true — /ping дозволено зараз, кулдаун оновлено.
    /// false — ще зарано, виклик заблокований.
    /// </summary>
    public bool TryConsumePingCooldown(long chatId, out TimeSpan remaining)
    {
        var now = DateTimeOffset.Now;

        if (_pingCooldownUntil.TryGetValue(chatId, out var until) && until > now)
        {
            remaining = until - now;
            return false;
        }

        _pingCooldownUntil[chatId] = now.Add(PingCooldown);
        remaining = TimeSpan.Zero;
        return true;
    }
    
    /// <summary>
    /// Оновлює збережений username для вже дозволеного chat_id — викликається
    /// при кожному /start, щоб список "Дозволені користувачі" не застарівав,
    /// якщо людина змінила username в Telegram після approve.
    /// </summary>
    public void RefreshUsername(long chatId, string username)
    {
        if (!IsAllowed(chatId)) return;

        _userSettings.MutateTelegramState(s =>
        {
            if (s.TelegramUsernames.TryGetValue(chatId, out var existing) && existing == username)
                return;

            s.TelegramUsernames[chatId] = username;
        });
    }
}