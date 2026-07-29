using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;

namespace AdminConsole.Services;

public sealed class PingMonitorService : BackgroundService, IDisposable
{
    private readonly IMessenger                  _messenger;
    private readonly ILogger<PingMonitorService> _logger;
    private readonly MonitoringSettings          _settings;
    private readonly IReadOnlyList<ServerEntry>  _servers;
    private readonly MaintenanceService _maintenance;

    // ── Стан статусів ────────────────────────────────────────────────────────

    // Єдине джерело правди про поточний статус кожного IP.
    // ConcurrentDictionary — читається і пишеться з обох циклів паралельно.
    private readonly ConcurrentDictionary<string, PingStatus> _previousStatus = new();

    // ── Throttle ─────────────────────────────────────────────────────────────

    // Основний цикл: до 10 паралельних пінгів (15 серверів → 10+5)
    private readonly SemaphoreSlim _mainThrottle     = new(10);

    // Recovery loop: окремий throttle на 5 слотів.
    // Не ділимо з основним — recovery не блокується основним циклом
    // навіть якщо всі 10 слотів зайняті.
    private readonly SemaphoreSlim _recoveryThrottle = new(5);

    // ── Константи ────────────────────────────────────────────────────────────

    private const int    PingTimeoutMs        = 2000;
    private const string LogSource            = "PingMonitor";
    private const int    MinOfflineIntervalSec = 5; // захист від некоректного appsettings

    // ── Constructor ──────────────────────────────────────────────────────────

    public PingMonitorService(
        IMessenger                   messenger,
        ILogger<PingMonitorService>  logger,
        IOptions<MonitoringSettings> settings,
        IOptions<List<ServerEntry>>  servers,
        MaintenanceService           maintenance)
    {
        _messenger = messenger;
        _logger    = logger;
        _settings  = settings.Value;
        _servers   = servers.Value.AsReadOnly();
        _maintenance = maintenance;
        _messenger.Register<MaintenanceChangedMessage>(
            this, (_, msg) => OnMaintenanceChanged(msg));
    }

    private void OnMaintenanceChanged(MaintenanceChangedMessage msg)
    {
        if (msg.Action != MaintenanceAction.Ended) return;

        // Скидаємо previousStatus для зачеплених серверів на Unknown —
        // наступний цикл пінгу сприйме поточний Offline (якщо сервер
        // не встиг піднятись вчасно) як "перехід з Unknown", що вже
        // існуючою гілкою коду генерує Warning — без окремої логіки
        // "примусового алерту".
        var affected = msg.Window.TargetGroup is not null
            ? _servers.Where(s => s.Group.Equals(msg.Window.TargetGroup,
                StringComparison.OrdinalIgnoreCase))
            : _servers.Where(s => s.IP == msg.Window.ServerIp);

        foreach (var s in affected)
            _previousStatus[s.IP] = PingStatus.Unknown;
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Валідація налаштувань — захист від некоректного appsettings.json
        var offlineInterval = Math.Max(
            _settings.OfflinePingIntervalSeconds,
            MinOfflineIntervalSec);

        _logger.LogInformation(
            "PingMonitorService started. {Count} servers, main: {Main}s, recovery: {Recovery}s.",
            _servers.Count, _settings.PingIntervalSeconds, offlineInterval);

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Ping monitor started — {_servers.Count} server(s), " +
            $"main cycle: {_settings.PingIntervalSeconds}s, " +
            $"recovery cycle: {offlineInterval}s."));

        PublishInitialCheckingState();

        // LinkedCts дозволяє одному циклу скасувати інший при падінні.
        // Без цього якщо MainLoop впаде з винятком — RecoveryLoop
        // продовжує крутитись нескінченно і навпаки.
        using var linkedCts = CancellationTokenSource
            .CreateLinkedTokenSource(stoppingToken);

        try
        {
            await Task.WhenAll(
                RunLoopGuardedAsync(RunMainLoopAsync(linkedCts.Token),     linkedCts),
                RunLoopGuardedAsync(RunRecoveryLoopAsync(offlineInterval,
                    linkedCts.Token),                  linkedCts)
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PingMonitorService: критична помилка циклу.");
        }

        _logger.LogInformation("PingMonitorService stopped.");
        _messenger.Send(AppLogEntryMessage.Warning(LogSource, "Ping monitor stopped."));
    }

    // ── Основний цикл (всі сервери, кожні N секунд) ──────────────────────────

    private async Task RunMainLoopAsync(CancellationToken ct)
    {
        bool firstRun = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Перша ітерація — одразу пінгуємо без затримки.
                // Наступні — чекаємо PingIntervalSeconds.
                if (firstRun)
                    firstRun = false;
                else
                    await Task.Delay(
                        TimeSpan.FromSeconds(_settings.PingIntervalSeconds),
                        ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                await PingServersAsync(_servers, _mainThrottle, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальне завершення при StopAsync — ігноруємо.
        }
    }

    // ── Recovery loop (тільки Offline сервери, кожні M секунд) ──────────────

    private async Task RunRecoveryLoopAsync(int intervalSec, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(intervalSec),
                    ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                var offlineServers = _servers
                    .Where(s => _previousStatus.TryGetValue(s.IP, out var st)
                                && st == PingStatus.Offline)
                    .ToList();

                if (offlineServers.Count == 0) continue;

                _logger.LogDebug(
                    "Recovery loop: pinging {Count} offline server(s).",
                    offlineServers.Count);

                await PingServersAsync(offlineServers, _recoveryThrottle, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
    
    // ── Guard для циклів ──────────────────────────────────────────────────────

    /// <summary>
    /// Обгортка над циклом: якщо цикл впав з неочікуваним винятком —
    /// скасовує linkedCts щоб зупинити паралельний цикл,
    /// потім перекидає виняток щоб Task.WhenAll його побачив.
    /// OperationCanceledException — нормальне завершення, ігнорується.
    /// </summary>
    private static async Task RunLoopGuardedAsync(
        Task                       loop,
        CancellationTokenSource    linkedCts)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Падіння одного циклу → зупиняємо другий
            linkedCts.Cancel();
            throw;
        }
    }

    // ── Спільна логіка пінгування ─────────────────────────────────────────────

    /// <summary>
    /// Пінгує список серверів паралельно через вказаний throttle,
    /// збирає результати і публікує один PingBatchResultMessage.
    /// Використовується і основним циклом і recovery loop —
    /// різниця тільки у списку серверів і throttle.
    /// </summary>
    private async Task PingServersAsync(
        IEnumerable<ServerEntry> servers,
        SemaphoreSlim            throttle,
        CancellationToken        ct)
    {
        // Локальний bag — не поле класу.
        // Кожен виклик PingServersAsync має свій ізольований bag,
        // тому основний і recovery цикли не можуть перезаписати один одного.
        var bag = new ConcurrentBag<PingResult>();

        var tasks = servers.Select(s => PingSingleServerAsync(s, throttle, bag, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        // Публікуємо навіть якщо bag порожній (всі OperationCanceled) —
        // перевірка вище це покриває.
        var results = bag.ToArray();
        if (results.Length == 0) return;

        _messenger.Send(new PingBatchResultMessage(
            new PingBatchPayload(
                Results:          results,
                CycleCompletedAt: DateTimeOffset.Now)));
    }

    // ── Пінг одного сервера ───────────────────────────────────────────────────

    private async Task PingSingleServerAsync(
        ServerEntry       server,
        SemaphoreSlim     throttle,
        ConcurrentBag<PingResult> bag,
        CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;  // слот захоплено — тепер Release() безпечний

            PingStatus status;
            long?      latencyMs = null;

            try
            {
                using var ping  = new Ping();
                var reply = await ping
                    .SendPingAsync(server.IP, PingTimeoutMs)
                    .WaitAsync(ct)
                    .ConfigureAwait(false);

                if (reply.Status == IPStatus.Success)
                {
                    status    = PingStatus.Online;
                    latencyMs = reply.RoundtripTime;
                }
                else
                {
                    status = PingStatus.Offline;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                status = PingStatus.Offline;
                _logger.LogWarning(ex,
                    "Ping to {Name} ({IP}) threw an exception.",
                    server.Name, server.IP);
            }
            
            var prev = _previousStatus.GetOrAdd(server.IP, PingStatus.Unknown);

            if (prev != status)
            {
                if (_previousStatus.TryUpdate(server.IP, status, prev))
                {
                    if (status == PingStatus.Online && prev == PingStatus.Offline)
                    {
                        _messenger.Send(AppLogEntryMessage.Success(LogSource,
                            $"{server.Name} ({server.IP}) is back ONLINE. " +
                            $"Latency: {latencyMs} ms."));
                    }
                    else if (status == PingStatus.Offline)
                    {
                        bool underMaintenance = _maintenance.IsUnderMaintenance(server.IP, server.Group);

                        if (!underMaintenance)
                        {
                            if (prev is PingStatus.Unknown or PingStatus.Checking)
                                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                                    $"{server.Name} ({server.IP}) недоступний при старті."));
                            else
                                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                                    $"{server.Name} ({server.IP}) went OFFLINE."));
                        }
                        // Під maintenance — жодного Warning/Error, але статус
                        // все одно оновлюється (PingResult нижче), UI покаже
                        // Offline + бейдж 🔧 замість тривоги.
                    }
                    // Checking/Unknown → Online: тихо, без логу — не спам при старті.
                }
            }

            bag.Add(new PingResult(
                server.Name, server.IP, server.Group,
                status, latencyMs, DateTimeOffset.Now));
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (acquired) throttle.Release();
        }
    }

    // ── Initial state ─────────────────────────────────────────────────────────

    private void PublishInitialCheckingState()
    {
        var initialResults = new List<PingResult>(_servers.Count);

        foreach (var server in _servers)
        {
            _previousStatus[server.IP] = PingStatus.Unknown;
            initialResults.Add(new PingResult(
                server.Name, server.IP, server.Group,
                PingStatus.Checking, null, DateTimeOffset.Now));
        }

        _messenger.Send(new PingBatchResultMessage(new PingBatchPayload(
            Results:          initialResults,
            CycleCompletedAt: DateTimeOffset.Now)));
    }
    
    // ── Public API для TelegramBotService

    /// <summary>
    /// Живий знімок поточного статусу всіх серверів прямо зараз.
    /// ConcurrentDictionary вже є єдиним джерелом правди (_previousStatus),
    /// тому це тонкий read-only метод без додаткової синхронізації.
    /// Дозволяє боту відповідати коректно навіть у перші секунди після старту,
    /// не покладаючись лише на PingBatchResultMessage (який ще міг не прийти).
    /// </summary>
    public IReadOnlyDictionary<string, PingStatus> GetSnapshot()
        => _previousStatus.ToDictionary(kv => kv.Key, kv => kv.Value);
    
    // ── IDisposable ───────────────────────────────────────────────────────────

    public override void Dispose()
    {
        // Обидва SemaphoreSlim містять внутрішній WaitHandle — звільняємо обидва.
        _mainThrottle.Dispose();
        _recoveryThrottle.Dispose();
        base.Dispose();
    }
}