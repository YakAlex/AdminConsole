using System.Collections.Concurrent;
using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using AdminConsole.Utils;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AdminConsole.Services;

/// <summary>
/// Telegram-бот: read-only доступ до статусу інфраструктури через кнопки.
///
/// Архітектурно — ще один "глядач" наявних Push-повідомлень (як ViewModels),
/// плюс живі read-only виклики GetSnapshot() для холодного старту (критика #3).
///
/// Критичні технічні застереження (враховані нижче за номерами критики):
///  #1 TelegramCallbackRegistry — короткі ID замість довгих рядків у callback_data
///  #1 TelegramTextChunker — пагінація під ліміт 4096 символів
///  #2 Усі внутрішні кеші — ConcurrentDictionary (thread-safety)
///  #3 GetSnapshot() з Ping/Rdp сервісів — не покладаємось лише на кеш повідомлень
///  #4 Перевірка актуальності pending-запиту в кожному callback + AnswerCallbackQuery
///  #5 CancellationTokenSource з await старого таска перед створенням нового клієнта
/// </summary>

/// <summary>
/// Один "екран" пагінації: ключ екрану (щоб не плутати Офлайн з Інцидентами
/// при stale callback) + вже побудовані сторінки + заголовок для перебудови.
/// </summary>
internal sealed record TelegramPagedScreen(string ScreenKey, IReadOnlyList<string> Pages);
public sealed class TelegramBotService 
    : BackgroundService,
      IRecipient<PingBatchResultMessage>,
      IRecipient<UptimeUpdatedMessage>,
      IRecipient<RdpSessionsUpdatedMessage>,
      IRecipient<CredentialsChangedMessage>,
      IRecipient<BackupTransitionMessage>
{
    private readonly IMessenger                        _messenger;
    private readonly ILogger<TelegramBotService>       _logger;
    private readonly CredentialStore                   _credentials;
    private readonly TelegramAccessControlService       _access;
    private readonly UserSettingsService                 _userSettings;
    private readonly PingMonitorService                 _pingMonitor;
    private readonly RdpMonitorService                  _rdpMonitor;
    private readonly UptimeTrackerService                _uptimeTracker;
    private readonly MaintenanceService                  _maintenance;
    private readonly BackupMonitorService                _backupMonitor;
    private readonly IReadOnlyList<ServerEntry>          _terminalServers;
    private readonly IReadOnlyList<ServerEntry>          _allServers;
    
    /// <summary>
    /// Кеш Name → ServerEntry для SendBackupsListAsync (Maintenance-бейдж).
    /// _allServers статичний (читається з конфігу один раз при старті),
    /// тому словник будується один раз у конструкторі, а не на кожен /backups.
    /// </summary>
    private readonly IReadOnlyDictionary<string, ServerEntry> _serverLookup;
    
    private const string LogSource = "TelegramBot";
    private bool IsSingleTerminalServer => _terminalServers.Count == 1;

    // ── Push-кеші
    private readonly ConcurrentDictionary<string, PingStatus>          _pingCache = new();
    private readonly ConcurrentDictionary<string, RdpSessionsPayload>  _rdpCache  = new();
    
    /// <summary>
    /// Кеш побудованих сторінок для пагінації (Офлайн/Інциденти/Обслуговування).
    /// Ключ — chat_id (одна активна "навігація" на користувача одночасно —
    /// цього достатньо, бо reply-кнопки відкривають новий екран синхронно).
    /// Значення скидається при перезапуску процесу — це нормально,
    /// callback просто попросить користувача відкрити екран заново.
    /// </summary>
    private readonly ConcurrentDictionary<long, TelegramPagedScreen> _pagedScreens = new();
    
    /// <summary>
    /// Ключі вже відомих ВІДКРИТИХ інцидентів (ServerIp + FellAt — унікально
    /// на кожен випадок падіння). Використовується ЛИШЕ для диференціації
    /// "цей інцидент я вже алертив" від "цей інцидент новий" — жодного
    /// зберігання деталей, самі DowntimeRecord беремо напряму з UptimeTrackerService.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _knownOpenIncidents = new();
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private static string IncidentKey(DowntimeRecord r) => $"{r.ServerIp}|{r.FellAt.Ticks}";

    // ── Callback registry — короткі ID для inline-кнопок (критика #1) ───────
    private readonly TelegramCallbackRegistry _serverPicker = new();

    // ── Hot-reload полінгу (критика #5) ──────────────────────────────────────
    private ITelegramBotClient?      _client;
    private CancellationTokenSource? _pollingCts;
    private Task?                    _pollingTask;
    private CancellationToken        _hostToken;

    public TelegramBotService(
        IMessenger                    messenger,
        ILogger<TelegramBotService>  logger,
        CredentialStore               credentials,
        TelegramAccessControlService  access,
        UserSettingsService           userSettings,
        PingMonitorService            pingMonitor,
        RdpMonitorService             rdpMonitor,
        UptimeTrackerService          uptimeTracker,
        MaintenanceService            maintenance,
        BackupMonitorService          backupMonitor,
        IOptions<List<ServerEntry>>   servers)
    {
        _messenger      = messenger;
        _logger         = logger;
        _credentials    = credentials;
        _access         = access;
        _userSettings   = userSettings;
        _pingMonitor    = pingMonitor;
        _rdpMonitor     = rdpMonitor;
        _uptimeTracker  = uptimeTracker;
        _maintenance    = maintenance;
        _backupMonitor  = backupMonitor;
        _allServers = servers.Value.ToList().AsReadOnly();

        _terminalServers = servers.Value
            .Where(s => s.Group.Equals("Terminal Servers", StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();

        _serverLookup = _allServers
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        try { _credentials.LoadTelegramFromVault(); }
        catch (Exception ex) { _logger.LogError(ex, "TelegramBotService: не вдалось завантажити токен."); }

        _messenger.RegisterAll(this);
    }

    // ── IRecipient — Push-кеші ────────────────────────────────────────────────

    public void Receive(PingBatchResultMessage message)
    {
        foreach (var r in message.Value.Results)
            _pingCache[r.IP] = r.Status;
    }

    public void Receive(RdpSessionsUpdatedMessage message)
    {
        _rdpCache[message.Value.ServerIp] = message.Value;
    }

    public void Receive(CredentialsChangedMessage message)
    {
        if (message.Target != CredentialTarget.Telegram) return;
        if (message.Action != CredentialAction.Saved) return;

        // Critичка #5: перезапуск полінгу — не awaited тут (Receive синхронний),
        // тому фоново, з власним лог-обробленням помилок.
        _ = Task.Run(() => RestartPollingAsync(_hostToken));
    }
    
    /// <summary>
/// Єдине джерело push-сповіщень про інфраструктуру. Спрацьовує ЛИШЕ на
/// новий, ще не бачений відкритий інцидент (!IsResolved). Відновлення
/// сервера (RecoveredAt заповнено) — НІКОЛИ не алертиться, лише мовчки
/// прибирається з _knownOpenIncidents, щоб той самий сервер міг знову
/// згенерувати push при наступному падінні.
/// </summary>
public void Receive(UptimeUpdatedMessage message)
{
    var currentlyOpen = message.Value.Where(r => !r.IsResolved).ToList();
    var currentKeys   = currentlyOpen.Select(IncidentKey).ToHashSet();

    // Прибираємо закриті/зниклі інциденти з відомих — без жодного алерту.
    foreach (var knownKey in _knownOpenIncidents.Keys.ToList())
    {
        if (!currentKeys.Contains(knownKey))
            _knownOpenIncidents.TryRemove(knownKey, out _);
    }

    // Нові відкриті інциденти — саме те, що вже пройшло
    // MinIncidentDurationSeconds (гарантія від UptimeTrackerService).
    foreach (var record in currentlyOpen)
    {
        string key = IncidentKey(record);
        if (_knownOpenIncidents.TryAdd(key, 0))
        {
            _ = BroadcastIncidentAlertAsync(record);
        }
    }
}

/// <summary>
/// Розсилає push усім авторизованим (Primary Admin + AllowedChatIds).
/// Fire-and-forget з try/catch — Receive() синхронний і не має "чекати" мережу.
/// </summary>
private async Task BroadcastIncidentAlertAsync(DowntimeRecord record)
{
    if (_client is null) return; // бот ще не підключений (немає токена) — просто мовчки пропускаємо

    string text = $"🔴 СЕРВЕР ОФЛАЙН\n" +
                  $"{record.ServerName} ({record.ServerIp})\n" +
                  $"Початок інциденту: {record.FellAt:dd.MM HH:mm:ss}";

    var recipients = new List<long>();
    if (_access.PrimaryAdminChatId is long adminId) recipients.Add(adminId);
    recipients.AddRange(_access.GetAllowedChatIds());

    foreach (var chatId in recipients.Distinct())
    {
        try
        {
            await _client.SendMessage(chatId, text, cancellationToken: _hostToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TelegramBotService: не вдалось надіслати alert у chat_id={ChatId}", chatId);
        }
    }
}

/// <summary>
/// Backup Verification — на відміну від UptimeUpdatedMessage (повний
/// знімок кожен цикл, диффиться тут), BackupTransitionMessage приходить
/// ЛИШЕ на вже відфільтрований (не-Maintenance, підтверджений) перехід —
/// BackupMonitorService.OnConfirmedTransition вирішує "казати чи мовчати"
/// сам, тут лишається тільки розіслати.
/// </summary>
public void Receive(BackupTransitionMessage message)
{
    _ = BroadcastBackupAlertAsync(message);
}

private async Task BroadcastBackupAlertAsync(BackupTransitionMessage message)
{
    if (_client is null) return;

    string icon = message.Current == BackupOutcome.Missing ? "🚫" : "⏰";
    string text = $"{icon} БЕКАП {message.Current.ToString().ToUpperInvariant()}\n" +
                  $"{message.ServerName} ({message.Kind})\n" +
                  $"Було: {message.Previous}";

    var recipients = new List<long>();
    if (_access.PrimaryAdminChatId is long adminId) recipients.Add(adminId);
    recipients.AddRange(_access.GetAllowedChatIds());

    foreach (var chatId in recipients.Distinct())
    {
        try
        {
            await _client.SendMessage(chatId, text, cancellationToken: _hostToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TelegramBotService: не вдалось надіслати backup alert у chat_id={ChatId}", chatId);
        }
    }
}

// ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hostToken = stoppingToken;
        
        foreach (var r in _uptimeTracker.GetSnapshot().Where(r => !r.IsResolved))
            _knownOpenIncidents[IncidentKey(r)] = 0;

        await RestartPollingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        await _restartLock.WaitAsync();
        try { await StopPollingAsync(); }
        finally { _restartLock.Release(); }

        _messenger.UnregisterAll(this);
    }

    /// <summary>
    /// Критика #5: перед створенням нового клієнта — Cancel() старого токена
    /// і await завершення старого polling-таска. Без цього старий цикл
    /// лишається "висіти" назавжди (memory leak + подвійна обробка updates).
    /// </summary>
    private async Task RestartPollingAsync(CancellationToken hostToken)
    {
        // Критично: лише один потік одночасно може зупиняти/перестворювати клієнт.
        // Без цього подвійний швидкий Save токена в UI спричиняє гонку і
        // NullReferenceException при Dispose() старого CancellationTokenSource.
        await _restartLock.WaitAsync(hostToken);
        try
        {
            await StopPollingAsync();

            if (!_credentials.HasTelegramCredentials)
            {
                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    "Telegram bot token відсутній — бот не запущений."));
                return;
            }

            var token = _credentials.GetTelegramToken();
            var client = new TelegramBotClient(token);

            try
            {
                var me = await client.GetMe();
                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"Telegram bot запущено: @{me.Username}"));
            }
            catch (Exception ex)
            {
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"Не вдалося підключитись до Telegram API: {ex.Message}"));
                return;
            }

            _client      = client;
            _pollingCts  = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _pollingTask = PollLoopAsync(client, _pollingCts.Token);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private async Task StopPollingAsync()
    {
        if (_pollingCts is null) return;

        _pollingCts.Cancel();
        try { if (_pollingTask is not null) await _pollingTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "TelegramBotService: помилка зупинки полінгу."); }

        _pollingCts.Dispose();
        _pollingCts  = null;
        _pollingTask = null;
        _client      = null;
    }

    // ── Ручний long-polling цикл ──────────────────────────────────────────────

    private async Task PollLoopAsync(ITelegramBotClient client, CancellationToken ct)
    {
        int offset = 0;
        while (!ct.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                updates = await client.GetUpdates(offset, timeout: 30, cancellationToken: ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TelegramBotService: помилка отримання updates.");
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.Id + 1;
                try { await HandleUpdateAsync(client, update, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TelegramBotService: помилка обробки update {Id}", update.Id);
                }
            }
        }
    }

    // ── Диспетчер вхідних Update ──────────────────────────────────────────────

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message is { Text: not null } message)
            await HandleMessageAsync(client, message, ct);
        else if (update.CallbackQuery is not null)
            await HandleCallbackQueryAsync(client, update.CallbackQuery, ct);
    }

    // ── Текстові команди ──────────────────────────────────────────────────────

    private async Task HandleMessageAsync(ITelegramBotClient client, Message message, CancellationToken ct)
    {
        long   chatId   = message.Chat.Id;
        string username = message.From?.Username ?? message.From?.FirstName ?? "unknown";
        string text     = message.Text!.Trim();

        // rate-limit перевіряється ПЕРШИМ, до будь-якої іншої логіки —
        // включно з /start і /claim_admin. Раніше ліміт рахувався лише ПІСЛЯ
        // IsAllowed(chatId), тобто саме анонімні/відхилені/відкликані
        // користувачі (найбільш імовірне джерело спаму) не мали жодного
        // обмеження. Неавторизованим — мовчки ігноруємо, щоб не заохочувати
        // повторні спроби відповіддю; авторизованим — та сама відповідь, що й раніше.
        if (!_access.CheckRateLimit(chatId))
        {
            if (_access.IsAllowed(chatId))
                await client.SendMessage(chatId, "⏳ Забагато запитів, зачекайте хвилину.", cancellationToken: ct);
            return;
        }

        // /start і /claim_admin обробляються ДО access-check — це вхідні точки для НЕавторизованих
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStartAsync(client, chatId, username, ct);
            return;
        }

        if (text.StartsWith("/claim_admin", StringComparison.OrdinalIgnoreCase))
        {
            await HandleClaimAdminAsync(client, chatId, text, ct);
            return;
        }

        if (!_access.IsAllowed(chatId))
        {
            _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                $"Unauthorized: chat_id={chatId}, username=@{username}, text='{text}'"));
            return; // мовчки ігноруємо
        }

        switch (text)
        {
            case "📊 Статус":
                await SendStatusAsync(client, chatId, ct);
                return;
            case "🔴 Офлайн":
                await SendOfflineListAsync(client, chatId, ct);
                return;
            case "⏱ Інциденти":
                await SendIncidentsListAsync(client, chatId, ct);
                return;
            case "🖥 RDP":
                await SendRdpPickerAsync(client, chatId, ct);
                return;
            case "🔧 Обслуговування":
                await SendMaintenanceListAsync(client, chatId, ct);
                return;
            case "🏓 Пінг":
                await SendPingNowAsync(client, chatId, ct);
                return;
            case "💾 Бекапи":
                await SendBackupsListAsync(client, chatId, ct);
                return;
            case "👥 Користувачі":
                if (_access.IsPrimaryAdmin(chatId))
                    await SendUsersListAsync(client, chatId, ct);
                return;
        }

        switch (text.Split(' ')[0].ToLowerInvariant())
        {
            case "/help":
                await SendHelpAsync(client, chatId, ct);
                break;
            case "/status":
                await SendStatusAsync(client, chatId, ct);
                break;
            case "/rdp":
                await SendRdpPickerAsync(client, chatId, ct);
                break;
            case "/ping":
                await SendPingNowAsync(client, chatId, ct);
                break;
            case "/backups":
                await SendBackupsListAsync(client, chatId, ct);
                break;
            case "/users":
                if (_access.IsPrimaryAdmin(chatId))
                    await SendUsersListAsync(client, chatId, ct);
                break;
            default:
                await SendHelpAsync(client, chatId, ct);
                break;
        }
    }

    private async Task HandleStartAsync(ITelegramBotClient client, long chatId, string username, CancellationToken ct)
    {
        _access.RefreshUsername(chatId, username);
        if (_access.IsAllowed(chatId))
        {
            await client.SendMessage(chatId, "Ви вже маєте доступ. /help — список команд.",
                replyMarkup: BuildMainMenu(chatId), cancellationToken: ct);
            return;
        }

        if (!_access.IsPrimaryAdminClaimed)
        {
            await client.SendMessage(chatId,
                "Бот ще не має Primary Admin. Якщо це ви — введіть /claim_admin <код>, " +
                "згенерований у розділі Settings → Telegram у застосунку.",
                cancellationToken: ct);
            return;
        }

        var result = _access.RegisterPendingRequest(chatId, username);

        if (result.Request is null)
        {
            if (result.CooldownRemaining is { } cooldown)
            {
                int minutesLeft = (int)Math.Ceiling(cooldown.TotalMinutes);
                await client.SendMessage(chatId,
                    $"⏳ Ваш попередній запит було відхилено. Спробуйте ще раз через {minutesLeft} хв.",
                    cancellationToken: ct);
            }
            else
            {
                await client.SendMessage(chatId,
                    "⏳ Забагато очікуючих запитів доступу зараз. Спробуйте пізніше.",
                    cancellationToken: ct);
            }
            return;
        }

        var request = result.Request;
        await client.SendMessage(chatId, "Запит на доступ надіслано адміністратору. Очікуйте підтвердження.",
            cancellationToken: ct);

        if (_access.PrimaryAdminChatId is long adminId)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Дозволити", $"approve:{request.Id}"),
                    InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"deny:{request.Id}")
                }
            });

            await client.SendMessage(adminId,
                $"🔔 Новий запит доступу: @{username} (chat_id={chatId})",
                replyMarkup: keyboard, cancellationToken: ct);
        }
    }

    private async Task HandleClaimAdminAsync(ITelegramBotClient client, long chatId, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await client.SendMessage(chatId, "Використання: /claim_admin <код>", cancellationToken: ct);
            return;
        }

        bool success = _access.TryClaimAdmin(parts[1], chatId);
        await client.SendMessage(chatId,
            success
                ? "✅ Ви прив'язані як Primary Admin. /help — список команд."
                : "❌ Невірний або протермінований код.",
            replyMarkup: success ? BuildMainMenu(chatId) : null,
            cancellationToken: ct);
    }

    // ── Inline callback (кнопки) ──────────────────────────────────────────────

    private async Task HandleCallbackQueryAsync(ITelegramBotClient client, CallbackQuery query, CancellationToken ct)
    {
        if (query.Message is null)
        {
            _logger.LogWarning(
                "TelegramBotService: CallbackQuery без Message (id={QueryId}, data={Data}) — " +
                "ймовірно, застаріле/видалене повідомлення.", query.Id, query.Data);

            try
            {
                await client.AnswerCallbackQuery(query.Id,
                    "⚠️ Це повідомлення застаріло, відкрийте екран знову через меню.",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TelegramBotService: не вдалось відповісти на застарілий callback.");
            }

            return;
        }

        long chatId = query.Message.Chat.Id;
        int  messageId = query.Message.MessageId;
        string data   = query.Data ?? string.Empty;

        if (!_access.CheckRateLimit(chatId))
        {
            await client.AnswerCallbackQuery(query.Id, "Забагато запитів.", cancellationToken: ct);
            return;
        }

        // Approve/Deny доступні лише Primary Admin, незалежно від AllowedChatIds
        if (data.StartsWith("approve:") || data.StartsWith("deny:"))
        {
            if (!_access.IsPrimaryAdmin(chatId))
            {
                await client.AnswerCallbackQuery(query.Id, "Немає прав.", cancellationToken: ct);
                return;
            }

            int id = int.Parse(data.Split(':')[1]);
            bool isApprove = data.StartsWith("approve:");

            // Критика #4: перевіряємо, чи запит ще актуальний
            bool ok = isApprove ? _access.Approve(id) : _access.Deny(id);

            if (!ok)
            {
                await client.AnswerCallbackQuery(query.Id, "Запит вже неактуальний.", cancellationToken: ct);
                await client.EditMessageText(chatId, messageId, "⚠️ Цей запит вже опрацьовано раніше.",
                    cancellationToken: ct);
                return;
            }

            await client.AnswerCallbackQuery(query.Id,
                isApprove ? "Дозволено ✅" : "Відхилено ❌", cancellationToken: ct);
            await client.EditMessageText(chatId, messageId,
                isApprove ? "✅ Доступ дозволено." : "❌ Доступ відхилено.", cancellationToken: ct);
            return;
        }

        // Усі інші callback — тільки для авторизованих
        if (!_access.IsAllowed(chatId))
        {
            await client.AnswerCallbackQuery(query.Id, "Немає доступу.", cancellationToken: ct);
            return;
        }

        // Критика #4: обов'язковий AnswerCallbackQuery навіть для звичайної навігації —
        // інакше кнопка в клієнті Telegram показує вічний "спінер".
        await client.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (data.StartsWith("rdp_server:"))
        {
            int shortId = int.Parse(data.Split(':')[1]);
            string? ip  = _serverPicker.Resolve(shortId);
            if (ip is null)
            {
                await client.EditMessageText(chatId, messageId, "⚠️ Список застарів, відкрийте /rdp знову.", cancellationToken: ct);
                return;
            }
            await EditWithRdpSessionsAsync(client, chatId, messageId, ip, ct);
        }
        else if (data == "back:status")
        {
            await EditWithStatusAsync(client, chatId, messageId, ct);
        }
        else if (data == "back:rdp_picker")
        {
            // Якщо сервер один — "picker" не існує, одразу показуємо його сесії
            // (сюди можна дійти лише з BuildStatusKeyboard(), кнопка "🖥 RDP по серверах").
            if (IsSingleTerminalServer)
                await EditWithRdpSessionsAsync(client, chatId, messageId, _terminalServers[0].IP, ct);
            else
                await EditWithRdpPickerAsync(client, chatId, messageId, ct);
        }
        else if (data.StartsWith("revoke:"))
        {
            if (!_access.IsPrimaryAdmin(chatId)) return;
            long targetChatId = long.Parse(data.Split(':')[1]);
            _access.Revoke(targetChatId);
            await client.EditMessageText(chatId, messageId, $"🚫 Доступ для chat_id={targetChatId} відкликано.", cancellationToken: ct);
        }
        else if (data.StartsWith("page:"))
        {
            // Формат: page:<screenKey>:<pageIndex>
            var parts = data.Split(':');
            string screenKey = parts[1];
            int    pageIndex = int.Parse(parts[2]);

            if (!_pagedScreens.TryGetValue(chatId, out var screen) || screen.ScreenKey != screenKey)
            {
                await client.EditMessageText(chatId, messageId,
                    "⚠️ Список застарів, відкрийте екран знову через меню.", cancellationToken: ct);
                return;
            }

            pageIndex = Math.Clamp(pageIndex, 0, screen.Pages.Count - 1);
            await client.EditMessageText(chatId, messageId, screen.Pages[pageIndex],
                replyMarkup: BuildPaginationKeyboard(screenKey, pageIndex, screen.Pages.Count),
                cancellationToken: ct);
        }
    }

    // ── Побудова відповідей ───────────────────────────────────────────────────

    private ReplyKeyboardMarkup BuildMainMenu(long chatId)
    {
        var rows = new List<KeyboardButton[]>
        {
            new KeyboardButton[] { "📊 Статус", "🔴 Офлайн" },
            new KeyboardButton[] { "⏱ Інциденти", "🖥 RDP" },
            new KeyboardButton[] { "🔧 Обслуговування", "🏓 Пінг" }
        };

        if (_access.IsPrimaryAdmin(chatId))
        {
            rows.Add(new KeyboardButton[] { "💾 Бекапи", "👥 Користувачі" });
        }
        else
        {
            rows.Add(new KeyboardButton[] { "💾 Бекапи" });
        }

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private async Task SendHelpAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        string help = "Доступні команди:\n" +
                      "/status — загальний огляд\n" +
                      "/rdp — RDP-сесії по серверах\n" +
                      "/ping — пінгувати всі сервери прямо зараз (реальний час)\n" +
                      "/backups — статус перевірок бекапів (Full/Diff)\n";
        if (_access.IsPrimaryAdmin(chatId))
            help += "/users — керування доступом (тільки Primary Admin)\n";

        await client.SendMessage(chatId, help, replyMarkup: BuildMainMenu(chatId), cancellationToken: ct);
    }

    private async Task SendStatusAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var msg = await client.SendMessage(chatId, BuildStatusText(),
            replyMarkup: BuildStatusKeyboard(), cancellationToken: ct);
    }

    private async Task EditWithStatusAsync(ITelegramBotClient client, long chatId, int messageId, CancellationToken ct)
        => await client.EditMessageText(chatId, messageId, BuildStatusText(),
            replyMarkup: BuildStatusKeyboard(), cancellationToken: ct);
    
    // ── /ping — пряме опитування всіх серверів у реальному часі ──────────────

    private async Task SendPingNowAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!_access.TryConsumePingCooldown(chatId, out var remaining))
        {
            await client.SendMessage(chatId,
                $"⏳ Зачекайте ще {Math.Ceiling(remaining.TotalSeconds)}с перед наступним пінгом.",
                cancellationToken: ct);
            return;
        }

        var placeholder = await client.SendMessage(chatId, "🏓 Пінгую всі сервери…", cancellationToken: ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<PingResult> results;
        try
        {
            results = await _pingMonitor.PingAllNowAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TelegramBotService: помилка виконання /ping.");
            await client.EditMessageText(chatId, placeholder.MessageId,
                "❌ Помилка під час пінгування серверів.", cancellationToken: ct);
            return;
        }
        sw.Stop();

        var lines = results
            .GroupBy(r => r.Group)
            .OrderBy(g => g.Key)
            .SelectMany(BuildGroupLines)
            .ToList();

        int online  = results.Count(r => r.Status == PingStatus.Online);
        int offline = results.Count(r => r.Status == PingStatus.Offline);

        string header = $"🏓 Результат пінгу ({results.Count} серв., {sw.Elapsed.TotalSeconds:F1}с)\n" +
                         $"✅ {online} online   🔴 {offline} offline\n\n";

        var pages = TelegramTextChunker.BuildPages(lines, header);
        _pagedScreens[chatId] = new TelegramPagedScreen("ping", pages);

        await client.EditMessageText(chatId, placeholder.MessageId, pages[0],
            replyMarkup: BuildPaginationKeyboard("ping", 0, pages.Count),
            cancellationToken: ct);
    }

    /// <summary>Форматує групу серверів з заголовком-назвою групи і статусом/latency кожного.</summary>
    private static IEnumerable<string> BuildGroupLines(IGrouping<string, PingResult> group)
    {
        yield return $"— {group.Key} —";
        foreach (var r in group.OrderBy(x => x.Name))
        {
            string icon = r.Status switch
            {
                PingStatus.Online  => "✅",
                PingStatus.Offline => "🔴",
                _                  => "⏳"
            };
            string latency = r.Status == PingStatus.Online && r.LatencyMs is not null
                ? $" — {r.LatencyMs} мс"
                : string.Empty;

            yield return $"{icon} {r.Name} ({r.IP}){latency}";
        }
    }
    
    private string BuildStatusText()
    {
        // Критика #3: пряме звернення до GetSnapshot(), а не лише до Push-кешу —
        // коректно навіть у перші секунди після старту застосунку.
        var pingSnapshot = _pingMonitor.GetSnapshot();
        int offline = pingSnapshot.Values.Count(s => s == PingStatus.Offline);
        int online  = pingSnapshot.Values.Count(s => s == PingStatus.Online);

        int openIncidents = _uptimeTracker.GetSnapshot().Count(r => !r.IsResolved);

        var rdpSnapshot = _rdpMonitor.GetSnapshot();
        int rdpSessions = rdpSnapshot.Values.Sum(list => list.Count(s => s.State == RdpSessionState.Active));

        int activeMaintenance = _maintenance.GetActiveWindows().Count;

        return $"📊 Статус інфраструктури\n" +
               $"✅ Онлайн: {online}\n" +
               $"🔴 Офлайн: {offline}\n" +
               $"⏱ Відкритих інцидентів: {openIncidents}\n" +
               $"🖥 RDP-сесій (активних): {rdpSessions}\n" +
               $"🔧 Активних вікон обслуговування: {activeMaintenance}";
    }

    private static InlineKeyboardMarkup BuildStatusKeyboard() =>
        new(Array.Empty<InlineKeyboardButton[]>());

    private async Task SendRdpPickerAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!_userSettings.Current.RdpMonitoringEnabled)
        {
            await client.SendMessage(chatId,
                "🖥 Моніторинг RDP-сесій зараз вимкнено в Settings.\n",
                cancellationToken: ct);
            return;
        }
        
        if (IsSingleTerminalServer)
        {
            // Один сервер — одразу показуємо сесії, без проміжного кроку вибору.
            await SendRdpSessionsDirectAsync(client, chatId, _terminalServers[0].IP, ct);
            return;
        }

        var msg = await client.SendMessage(chatId, "Оберіть сервер:",
            replyMarkup: BuildRdpPickerKeyboard(), cancellationToken: ct);
    }

    /// <summary>
    /// Показує сесії конкретного сервера як НОВЕ повідомлення (не EditMessageText —
    /// на відміну від навігації picker→сесії через callback, тут немає попереднього
    /// повідомлення для редагування). Без кнопки "◀ Назад" — повертатись нікуди,
    /// адже picker для єдиного сервера не показувався.
    /// </summary>
    private async Task SendRdpSessionsDirectAsync(
        ITelegramBotClient client, long chatId, string serverIp, CancellationToken ct)
    {
        var snapshot = _rdpMonitor.GetSnapshot();
        var sessions = snapshot.TryGetValue(serverIp, out var list) ? list : [];

        var lines = sessions.Count == 0
            ? new List<string> { "Немає активних сесій." }
            : sessions.Select(s => $"{s.Username} — {s.State} (logon: {s.LogonTime})").ToList();

        var pages = TelegramTextChunker.BuildPages(lines, header: "🖥 Сесії:\n");
        _pagedScreens[chatId] = new TelegramPagedScreen("rdp_direct", pages);

        await client.SendMessage(chatId, pages[0],
            replyMarkup: BuildPaginationKeyboard("rdp_direct", 0, pages.Count),
            cancellationToken: ct);
    }

    private async Task EditWithRdpPickerAsync(ITelegramBotClient client, long chatId, int messageId, CancellationToken ct)
    {
        if (!_userSettings.Current.RdpMonitoringEnabled)
        {
            await client.EditMessageText(chatId, messageId,
                "🖥 Моніторинг RDP-сесій зараз вимкнено в Settings.", cancellationToken: ct);
            return;
        }

        await client.EditMessageText(chatId, messageId, "Оберіть сервер:",
            replyMarkup: BuildRdpPickerKeyboard(), cancellationToken: ct);
    }

    private InlineKeyboardMarkup BuildRdpPickerKeyboard()
    {
        // Критика #1: реєструємо IP через TelegramCallbackRegistry — навіть
        // якщо IP короткий, це захищає від майбутнього збільшення даних
        // у callback_data (наприклад, комбінації з групою/сторінкою).
        var rows = _terminalServers
            .Select(s => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    s.Name, $"rdp_server:{_serverPicker.Register(s.IP)}")
            })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    private async Task EditWithRdpSessionsAsync(
        ITelegramBotClient client, long chatId, int messageId, string serverIp, CancellationToken ct)
    {
        if (!_userSettings.Current.RdpMonitoringEnabled)
        {
            await client.EditMessageText(chatId, messageId,
                "🖥 Моніторинг RDP-сесій зараз вимкнено в Settings.", cancellationToken: ct);
            return;
        }

        // Критика #3: пряме звернення до GetSnapshot() замість покладання
        // тільки на _rdpCache — актуально одразу після старту.
        var snapshot = _rdpMonitor.GetSnapshot();
        var sessions = snapshot.TryGetValue(serverIp, out var list) ? list : [];

        var lines = sessions.Count == 0
            ? new List<string> { "Немає активних сесій." }
            : sessions.Select(s => $"{s.Username} — {s.State} (logon: {s.LogonTime})").ToList();

        // Критика #1: пагінація під ліміт 4096 символів (хоч для RDP рідко
        // актуально — сесій зазвичай небагато, але захист лишається).
        var pages = TelegramTextChunker.BuildPages(lines, header: $"🖥 Сесії:\n");

        // Якщо сервер лише один — picker'а не існувало, тому й "Назад" нікуди.
        var keyboard = IsSingleTerminalServer
            ? null
            : new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("◀ Назад", "back:rdp_picker") }
            });

        await client.EditMessageText(chatId, messageId, pages[0], replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task SendUsersListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var users = _access.GetAllowedUsers();

        if (users.Count == 0)
        {
            await client.SendMessage(chatId, "👥 Дозволених користувачів немає.", cancellationToken: ct);
            return;
        }

        var rows = users
            .Select(u => new[] { InlineKeyboardButton.WithCallbackData(
                $"🚫 @{u.Username} ({u.ChatId})", $"revoke:{u.ChatId}") })
            .ToArray();

        await client.SendMessage(chatId, "👥 Дозволені користувачі:",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }
    
    // ── Офлайн / Інциденти / Обслуговування (з пагінацією)

    private async Task SendOfflineListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        // Критика #3: живий знімок напряму з сервісу, не з push-кешу.
        var snapshot = _pingMonitor.GetSnapshot();

        var lines = _allServers
            .Where(s => snapshot.TryGetValue(s.IP, out var status) && status == PingStatus.Offline)
            .Select(s => $"🔴 {s.Name} ({s.IP}) — {s.Group}")
            .ToList();

        if (lines.Count == 0)
            lines.Add("Немає офлайн-серверів. ✅");

        await SendPagedScreenAsync(client, chatId, screenKey: "offline", header: "🔴 Офлайн-сервери:\n", lines, ct);
    }

    private async Task SendIncidentsListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var openIncidents = _uptimeTracker.GetSnapshot()
            .Where(r => !r.IsResolved)
            .OrderByDescending(r => r.FellAt)
            .ToList();

        var lines = openIncidents
            .Select(r => $"⏱ {r.ServerName} ({r.ServerIp})\n   Впав: {r.FellAt:dd.MM HH:mm} — триває {r.DurationDisplay}")
            .ToList();

        if (lines.Count == 0)
            lines.Add("Відкритих інцидентів немає. ✅");

        await SendPagedScreenAsync(client, chatId, screenKey: "incidents", header: "⏱ Відкриті інциденти:\n", lines, ct);
    }

    private async Task SendMaintenanceListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var windows = _maintenance.GetActiveWindows()
            .OrderBy(w => w.To ?? DateTimeOffset.MaxValue) // "без обмеження" — в кінець списку
            .ToList();

        var lines = windows
            .Select(w => $"- {w.DisplayName}\n" +
                         $"   {(string.IsNullOrWhiteSpace(w.Reason) ? "Без причини" :  w.Reason)}\n" +
                         $"   {(w.To is { } to ? $"До {to.ToLocalTime():dd.MM HH:mm}." : "без обмеження часу.")}")
            .ToList();

        if (lines.Count == 0)
            lines.Add("Активних вікон обслуговування немає.");

        await SendPagedScreenAsync(client, chatId, screenKey: "maintenance", header: "🔧 Обслуговування:\n", lines, ct);
    }
    
    /// <summary>
    /// Backup Verification: живий знімок напряму з BackupMonitorService.GetSnapshot()
    /// (deep-clone, безпечно). Бейдж 🔧 біля рядка — сервер під активним
    /// Maintenance-вікном прямо зараз; статус все одно показується чесно
    /// (той самий принцип, що для Ping/UptimeIncidents), лише broadcast
    /// на перехід був придушений — SendBackupsListAsync ніколи не приховує
    /// стан, тільки підказує чому alert міг не прийти.
    /// </summary>
    private async Task SendBackupsListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        // Pull-перевірка (той самий принцип, що EvaluateMonitoringToggle у
        // BackupMonitorService) — НЕ показуємо кеш GetSnapshot(), якщо
        // моніторинг вимкнено в Settings: BackupMonitorService перестав його
        // оновлювати, тож дані там можуть бути застарілими (з часу вимкнення).
        if (!_userSettings.Current.BackupMonitoringEnabled)
        {
            await client.SendMessage(chatId,
                "💾 Backup-моніторинг зараз вимкнено в Settings.\n" +
                "Дані про стан бекапів не оновлюються.",
                cancellationToken: ct);
            return;
        }

        var states = _backupMonitor.GetSnapshot()
            .OrderBy(s => s.Host)
            .ThenBy(s => s.Name)
            .ThenBy(s => s.Kind)
            .ToList();

        var lines = states
            .Select(s =>
            {
                bool underMaintenance =
                    _serverLookup.TryGetValue(s.Host, out var entry) &&
                    _maintenance.IsUnderMaintenance(entry.IP, entry.Group);

                string maintenanceBadge = underMaintenance ? " 🔧" : string.Empty;
                string maintenanceText = underMaintenance ? "\n            [Maintenance]" : string.Empty;
            
                // Якщо статус ОК, показуємо дату. Якщо проблема — показуємо текст помилки (MISSING/STALE).
                string statusDisplay = s.Outcome == BackupOutcome.Ok 
                    ? (s.LastConfirmedAt is { } at ? at.ToLocalTime().ToString("dd.MM HH:mm") : "Невідомо")
                    : s.Outcome.ToString().ToUpper();
                
                // Додаємо мітку [Diff] тільки якщо це диференційний бекап, Full приховуємо щоб не засмічувати екран
                string kindTag = s.Kind == BackupKind.Diff ? " [Diff]" : string.Empty;

                
                return $"{BackupIcon(s.Outcome)}{maintenanceBadge} {s.Name}{kindTag} — {statusDisplay}{maintenanceText}";
            })
            .ToList();

        if (lines.Count == 0)
            lines.Add("Перевірки бекапів не сконфігуровано (BackupChecks у appsettings.json).");

        await SendPagedScreenAsync(client, chatId,  screenKey: "backups", header: "💾 Статус бекапів:\n", lines, ct);
    }

    private static string BackupIcon(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Ok          => "✅",
        BackupOutcome.SizeWarning => "⚠️",
        BackupOutcome.Stale       => "⏰",
        BackupOutcome.Missing     => "🚫",
        _                         => "❓"
    };
    
    /// <summary>
    /// Спільна логіка для трьох екранів вище: будує сторінки через
    /// TelegramTextChunker (критика #2 — ліміт 4096 символів), кладе їх
    /// у _pagedScreens для подальшої навігації і відправляє першу сторінку.
    /// </summary>
    private async Task SendPagedScreenAsync(
        ITelegramBotClient client, long chatId, string screenKey, string header,
        List<string> lines, CancellationToken ct)
    {
        var pages = TelegramTextChunker.BuildPages(lines, header);
        _pagedScreens[chatId] = new TelegramPagedScreen(screenKey, pages);

        await client.SendMessage(chatId, pages[0],
            replyMarkup: BuildPaginationKeyboard(screenKey, 0, pages.Count),
            cancellationToken: ct);
    }

    /// <summary>
    /// "◀ Назад" / "Далі ▶" — показуються лише коли є куди гортати.
    /// Критика #1: callback_data короткий ("page:offline:3"), навіть
    /// для довгих екранів завжди в межах 64 байт.
    /// </summary>
    private static InlineKeyboardMarkup BuildPaginationKeyboard(string screenKey, int pageIndex, int pageCount)
    {
        if (pageCount <= 1)
            return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());

        var row = new List<InlineKeyboardButton>();

        if (pageIndex > 0)
            row.Add(InlineKeyboardButton.WithCallbackData("◀ Назад", $"page:{screenKey}:{pageIndex - 1}"));

        if (pageIndex < pageCount - 1)
            row.Add(InlineKeyboardButton.WithCallbackData("Далі ▶", $"page:{screenKey}:{pageIndex + 1}"));

        return new InlineKeyboardMarkup(new[] { row.ToArray() });
    }
    
    public override void Dispose()
    {
        _restartLock.Dispose();
        base.Dispose();
    }
}