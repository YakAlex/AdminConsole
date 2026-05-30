using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.NetworkInformation;

namespace AdminConsole.Services;

public sealed class PingMonitorService : BackgroundService
{
    private readonly IMessenger                  _messenger;
    private readonly ILogger<PingMonitorService> _logger;
    private readonly MonitoringSettings          _settings;
    private readonly IReadOnlyList<ServerEntry>  _servers;

    // Track previous state per IP so we only log actual transitions,
    // not every successful ping.
    private readonly Dictionary<string, PingStatus> _previousStatus = new();

    private const int PingTimeoutMs = 2000;
    private const string LogSource  = "PingMonitor";

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
            await PingAllServersAsync(stoppingToken);

            await Task.Delay(
                TimeSpan.FromSeconds(_settings.PingIntervalSeconds),
                stoppingToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("PingMonitorService stopped.");
        _messenger.Send(AppLogEntryMessage.Warning(LogSource, "Ping monitor stopped."));
    }

    private void PublishInitialCheckingState()
    {
        foreach (var server in _servers)
        {
            _previousStatus[server.IP] = PingStatus.Unknown;

            _messenger.Send(new PingStatusChangedMessage(new PingResult(
                server.Name, server.IP, server.Group,
                PingStatus.Checking, null, DateTimeOffset.Now)));
        }
    }

    private async Task PingAllServersAsync(CancellationToken ct)
    {
        var tasks = _servers.Select(s => PingSingleServerAsync(s, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task PingSingleServerAsync(ServerEntry server, CancellationToken ct)
    {
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
            _logger.LogWarning(ex, "Ping to {Name} ({IP}) threw an exception.",
                server.Name, server.IP);
        }

        // Only log state transitions — not every ping result.
        if (_previousStatus.TryGetValue(server.IP, out var prev) && prev != status)
        {
            if (status == PingStatus.Online)
                _messenger.Send(AppLogEntryMessage.Success(LogSource,
                    $"{server.Name} ({server.IP}) is back ONLINE. Latency: {latencyMs} ms."));
            else
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"{server.Name} ({server.IP}) went OFFLINE."));
        }

        _previousStatus[server.IP] = status;

        _messenger.Send(new PingStatusChangedMessage(new PingResult(
            server.Name, server.IP, server.Group,
            status, latencyMs, DateTimeOffset.Now)));
    }
}