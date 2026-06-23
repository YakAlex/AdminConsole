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

    private readonly ConcurrentDictionary<string, PingStatus> _previousStatus = new();

    private const int PingTimeoutMs = 2000;
    private const string LogSource  = "PingMonitor";
    private bool _firstRun = true;
    private readonly SemaphoreSlim _pingThrottle = new(10);
    private ConcurrentBag<PingResult> _cycleBag = new();
    public PingMonitorService(
        IMessenger messenger,
        ILogger<PingMonitorService> logger,
        IOptions<MonitoringSettings> settings,
        IOptions<List<ServerEntry>> servers)
    {
        _messenger = messenger;
        _logger    = logger;
        _settings  = settings.Value;
        _servers   = servers.Value.AsReadOnly();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PingMonitorService started. Monitoring {Count} servers every {Interval}s.",
            _servers.Count, _settings.PingIntervalSeconds);

        _messenger.Send(AppLogEntryMessage.Info(LogSource,
            $"Ping monitor started — watching {_servers.Count} server(s) " +
            $"every {_settings.PingIntervalSeconds}s."));

        PublishInitialCheckingState();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Delay BEFORE ping — except on the very first iteration.
            // This way results appear immediately on startup,
            // then repeat every PingIntervalSeconds.
            if (_firstRun)
            {
                _firstRun = false;
            }
            else
            {
                await Task.Delay(
                        TimeSpan.FromSeconds(_settings.PingIntervalSeconds),
                        stoppingToken)
                    .ConfigureAwait(false);
            }

            await PingAllServersAsync(stoppingToken);
        }

        _logger.LogInformation("PingMonitorService stopped.");
        _messenger.Send(AppLogEntryMessage.Warning(LogSource, "Ping monitor stopped."));
    }

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

    private async Task PingAllServersAsync(CancellationToken ct)
    {
        _cycleBag = new ConcurrentBag<PingResult>();
        
        var tasks = _servers.Select(s => PingSingleServerAsync(s, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        
        if (ct.IsCancellationRequested) return;

        // Одне зведене повідомлення замість N окремих
        var payload = new PingBatchPayload(
            Results:          _cycleBag.ToArray(),
            CycleCompletedAt: DateTimeOffset.Now);

        _messenger.Send(new PingBatchResultMessage(payload));
    }

    private async Task PingSingleServerAsync(ServerEntry server, CancellationToken ct)
    {
        await _pingThrottle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PingStatus status;
            long?      latencyMs = null;

            try
            {
                using var ping = new Ping();
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
                _logger.LogWarning(ex, "Ping to {Name} ({IP}) threw an exception.",
                    server.Name, server.IP);
            }

            // Логуємо тільки реальні переходи станів, ігноруємо стартовий шум.
            // SUCCESS — тільки при реальному відновленні: Offline → Online.
            //           Unknown/Checking → Online при старті логувати не потрібно.
            // ERROR   — тільки при реальному падінні: Online → Offline.
            //           Checking → Offline при старті логуємо — сервер реально недоступний.
            _previousStatus.TryGetValue(server.IP, out var prev);

            if (status == PingStatus.Online && prev == PingStatus.Offline)
            {
                _messenger.Send(AppLogEntryMessage.Success(LogSource,
                    $"{server.Name} ({server.IP}) is back ONLINE. Latency: {latencyMs} ms."));
            }
            else if (status == PingStatus.Offline && prev != PingStatus.Offline)
            {
                // При старті (Checking→Offline) — логуємо як Warning, не Error.
                // При реальному падінні (Online→Offline) — логуємо як Error.
                if (prev is PingStatus.Unknown or PingStatus.Checking)
                    _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                        $"{server.Name} ({server.IP}) недоступний при старті."));
                else
                    _messenger.Send(AppLogEntryMessage.Error(LogSource,
                        $"{server.Name} ({server.IP}) went OFFLINE."));
            }

            _previousStatus[server.IP] = status;

            _cycleBag.Add(new PingResult(
                server.Name, server.IP, server.Group,
                status, latencyMs, DateTimeOffset.Now));
        }
        finally
        {
            _pingThrottle.Release();
        }
    }
    
    // ── IDisposable ──────────────────────────────────────────────────────────
    public override void Dispose()
    {
        // SemaphoreSlim містить внутрішній WaitHandle (некерований ресурс).
        // BackgroundService.Dispose() викликається хостом — перевизначаємо тут.
        _pingThrottle.Dispose();
        base.Dispose();
    }
}