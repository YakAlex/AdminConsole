using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;


namespace AdminConsole.Services;

/// <summary>
/// Читає Error/Critical записи з Windows Event Log (System + Application)
/// і публікує EventLogUpdatedMessage.
///
/// Оптимізація: зберігає _lastRead timestamp між ітераціями.
/// Перший запуск — читає останні FetchCount помилок за весь час.
/// Наступні запуски — сканує лише записи новіші за _lastRead,
/// зупиняючись одразу як тільки зустрів старий запис (early exit).
/// Повідомлення не надсилається якщо нових записів немає.
/// </summary>
public sealed class EventLogService : BackgroundService
{
    private readonly IMessenger               _messenger;
    private readonly ILogger<EventLogService> _logger;

    private const int FetchCount          = 20;
    private const int PollIntervalSeconds = 30;

    // Максимальна кількість записів для сканування при першому запуску.
    // Після першого запуску — не використовується (early exit по timestamp).
    private const int InitialMaxScan = 2000;

    // Зберігає час останнього прочитаного запису.
    // null = перший запуск, читаємо InitialMaxScan записів назад.
    // non-null = інкрементальний режим, читаємо лише нове.
    private DateTimeOffset? _lastRead = null;

    // Кеш останнього повного знімку — потрібен бо UI (ResourceMonitorViewModel)
    // може підписатись через IMessenger ПІСЛЯ того як цей BackgroundService
    // вже опублікував перше повідомлення (воно "губиться", якщо нікому
    // було дослухати в момент Send). ViewModel читає це поле напряму
    // при ініціалізації, не чекаючи наступного PollIntervalSeconds.
    private readonly List<EventLogEntry> _lastSnapshot = new();
    private readonly object _snapshotLock = new();

    // Повертаємо знімок під lock — захищаємо від race з FetchAndPublishAsync
    // яка пише з thread pool, поки UI-потік читає через LoadCachedLocalEventLog.
    public IReadOnlyList<EventLogEntry> LastSnapshot
    {
        get
        {
            lock (_snapshotLock)
                return _lastSnapshot.ToList();
        }
    }

    public EventLogService(
        IMessenger messenger,
        ILogger<EventLogService> logger)
    {
        _messenger = messenger;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogWarning("EventLogService: not running on Windows — disabled.");
            return;
        }

        _logger.LogInformation("EventLogService started.");

        await FetchAndPublishAsync();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(PollIntervalSeconds),
                    stoppingToken).ConfigureAwait(false);

                if (stoppingToken.IsCancellationRequested) break;

                await FetchAndPublishAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальне завершення при StopAsync — ігноруємо.
        }

        _logger.LogInformation("EventLogService stopped.");
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    private async Task FetchAndPublishAsync()
    {
        try
        {
            // Знімаємо час ДО читання — щоб не пропустити записи
            // що з'явились поки ми читали.
            var readStart = DateTimeOffset.Now;
            var since     = _lastRead;

            var entries = await Task
                .Run(() => ReadErrors(since))
                .ConfigureAwait(false);

            // Оновлюємо курсор лише якщо читання пройшло успішно
            _lastRead = readStart;

            // Оновлюємо кеш — при першому читанні просто зберігаємо,
            // при наступних додаємо нові записи на початок (найновіші зверху)
            // і ріжемо хвіст так само як робить ViewModel.
            lock (_snapshotLock)
            {
                if (since is null)
                {
                    _lastSnapshot.Clear();
                    _lastSnapshot.AddRange(entries);
                }
                else if (entries.Count > 0)
                {
                    _lastSnapshot.InsertRange(0, entries);
                    if (_lastSnapshot.Count > FetchCount)
                        _lastSnapshot.RemoveRange(FetchCount, _lastSnapshot.Count - FetchCount);
                }
            }

            // Не надсилаємо повідомлення якщо нічого нового немає.
            // Виключення: перший запуск (since == null) — завжди надсилаємо
            // щоб UI отримав початковий стан.
            if (since is not null && entries.Count == 0)
            {
                _logger.LogDebug("EventLogService: no new errors since {LastRead}.", since);
                return;
            }

            _logger.LogDebug(
                "EventLogService: found {Count} new error(s) since {Since}.",
                entries.Count, since);

            _messenger.Send(new EventLogUpdatedMessage(entries));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EventLogService: failed to read event logs.");
        }
    }

    // ── Читання записів ───────────────────────────────────────────────────────
    // Винесено у EventLogReader — спільний для local (цей сервіс)
    // і remote (RemoteEventLogService) читання.
    private static List<EventLogEntry> ReadErrors(DateTimeOffset? since)
        => EventLogReader.ReadErrors(".", since);
}