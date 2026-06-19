using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Options;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using AdminConsole.Services;

namespace AdminConsole.ViewModels;

public sealed partial class ResourceMonitorViewModel
    : ObservableObject,
      IRecipient<ResourceSnapshotUpdatedMessage>,
      IRecipient<EventLogUpdatedMessage>,
      IRecipient<PingBatchResultMessage>,
      IDisposable
{
    // ── Server selector ──────────────────────────────────────────────────────

    /// <summary>
    /// Лише Windows-сервери — Linux і Network не підтримують WMI/EventLog.
    /// Localhost завжди перший.
    /// </summary>
    public ObservableCollection<ServerDashboardEntry> AvailableServers { get; } = [];

    [ObservableProperty]
    private ServerDashboardEntry? _selectedServer;

    // Прапор: чи підтримує система взагалі цей функціонал
    public bool IsWindowsSupported { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Окрема властивість для біндингу видимості банера —
    // без залежності від ConverterParameter (стандартний
    // BooleanToVisibilityConverter його не підтримує і мовчки ігнорує).
    public bool IsWindowsUnsupported => !IsWindowsSupported;

    // ── CPU / RAM bindings ───────────────────────────────────────────────────
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _ramUsedGb;
    [ObservableProperty] private double _ramTotalGb;
    [ObservableProperty] private string _cpuLabel    = "— %";
    [ObservableProperty] private string _ramLabel    = "— / — GB";
    [ObservableProperty] private string _lastUpdated = "—";
    [ObservableProperty] private string _serverStatusBadge = string.Empty;

    [ObservableProperty] private SolidColorBrush _cpuBarBrush = GreenBrush;
    [ObservableProperty] private SolidColorBrush _ramBarBrush = GreenBrush;

    // ── Event log bindings ───────────────────────────────────────────────────
    public ObservableCollection<EventLogEntryViewModel> EventEntries { get; } = [];

    [ObservableProperty] private EventLogEntryViewModel? _selectedEntry;
    [ObservableProperty] private string _selectedEntryFullMessage = string.Empty;
    [ObservableProperty] private string _eventLogStatus           = "Select a server…";

    // ── Static frozen brushes ────────────────────────────────────────────────
    private static readonly SolidColorBrush GreenBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly SolidColorBrush AmberBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)));
    private static readonly SolidColorBrush RedBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)));

    private readonly Dispatcher _dispatcher;

    // ── Constructor ──────────────────────────────────────────────────────────

    private readonly RemoteEventLogService  _remoteEventLog;
    private readonly EventLogService        _localEventLog;
    private readonly RemoteResourceService  _remoteResource;
    private CancellationTokenSource? _remoteEventLogCts;
    private CancellationTokenSource? _remoteResourceCts;

    public ResourceMonitorViewModel(
        IMessenger messenger,
        IOptions<List<ServerEntry>> serversOptions,
        RemoteEventLogService remoteEventLogService,
        EventLogService localEventLogService,
        RemoteResourceService remoteResourceService)
    {
        _remoteEventLog = remoteEventLogService;
        _localEventLog  = localEventLogService;
        _remoteResource = remoteResourceService;
        _dispatcher     = System.Windows.Application.Current.Dispatcher;

        // localhost — завжди перший
        AvailableServers.Add(ServerDashboardEntry.Localhost());

        // Лише Windows-сервери з appsettings
        foreach (var s in serversOptions.Value.Where(s => s.Type == ServerType.Windows))
            AvailableServers.Add(ServerDashboardEntry.FromServerEntry(s));

        // Дефолтний вибір — localhost
        _selectedServer = AvailableServers[0];

        messenger.RegisterAll(this);
    }

    // ── Server selection ─────────────────────────────────────────────────────

    partial void OnSelectedServerChanged(ServerDashboardEntry? value)
    {
        if (value is null) return;

        // Скидаємо UI при зміні сервера
        ClearMetrics();
        EventEntries.Clear();

        if (!IsWindowsSupported)
        {
            EventLogStatus    = "Windows only — not supported on this OS.";
            ServerStatusBadge = string.Empty;
            return;
        }

        if (value.IsLocal)
        {
            ServerStatusBadge = string.Empty;
            // Підхоплюємо те, що EventLogService вже встиг прочитати,
            // не чекаючи наступного PollIntervalSeconds (30с).
            LoadCachedLocalEventLog();
        }
        else
        {
            UpdateServerStatusBadge(value.PingStatus);
            _ = LoadRemoteEventLogAsync(value);
            _ = LoadRemoteResourceAsync(value);
        }
    }

    // ── IRecipient<ResourceSnapshotUpdatedMessage> ───────────────────────────
    // Отримуємо лише якщо вибрано localhost

    public void Receive(ResourceSnapshotUpdatedMessage message)
    {
        if (SelectedServer is null || !SelectedServer.IsLocal) return;

        var s = message.Value;
        _dispatcher.InvokeAsync(() =>
        {
            CpuPercent  = s.CpuPercent;
            RamPercent  = s.RamPercent;
            RamUsedGb   = s.RamUsedGb;
            RamTotalGb  = s.RamTotalGb;
            CpuLabel    = $"{s.CpuPercent:F1} %";
            RamLabel    = $"{s.RamUsedGb:F1} GB  /  {s.RamTotalGb:F1} GB";
            LastUpdated = s.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            CpuBarBrush = PickBrush(s.CpuPercent);
            RamBarBrush = PickBrush(s.RamPercent);
        });
    }

    // ── IRecipient<EventLogUpdatedMessage> ───────────────────────────────────
    // Отримуємо лише якщо вибрано localhost (EventLogService завжди читає локально)

    public void Receive(EventLogUpdatedMessage message)
    {
        if (SelectedServer is null || !SelectedServer.IsLocal) return;

        var entries = message.Value;
        if (entries.Count == 0) return;

        _dispatcher.InvokeAsync(() =>
        {
            if (EventEntries.Count == 0)
            {
                foreach (var e in entries)
                    EventEntries.Add(new EventLogEntryViewModel(e));
            }
            else
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                    EventEntries.Insert(0, new EventLogEntryViewModel(entries[i]));

                while (EventEntries.Count > 50)
                    EventEntries.RemoveAt(EventEntries.Count - 1);
            }

            EventLogStatus = $"{EventEntries.Count} recent error(s) — " +
                             $"last updated {DateTime.Now:HH:mm:ss}";
        });
    }

    // ── IRecipient<PingBatchResultMessage> ───────────────────────────────────
    // Оновлюємо ping-статус у ComboBox + badge обраного сервера

    public void Receive(PingBatchResultMessage message)
    {
        var results = message.Value.Results;

        _dispatcher.InvokeAsync(() =>
        {
            foreach (var result in results)
            {
                var entry = AvailableServers.FirstOrDefault(s => s.IP == result.IP);
                if (entry is not null)
                    entry.PingStatus = result.Status;
            }

            // Оновлюємо badge якщо вибрано віддалений сервер
            if (SelectedServer is not null && !SelectedServer.IsLocal)
                UpdateServerStatusBadge(SelectedServer.PingStatus);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    partial void OnSelectedEntryChanged(EventLogEntryViewModel? value)
        => SelectedEntryFullMessage = value?.FullMessage ?? string.Empty;

    private void ClearMetrics()
    {
        CpuPercent  = 0;
        RamPercent  = 0;
        RamUsedGb   = 0;
        RamTotalGb  = 0;
        CpuLabel    = "— %";
        RamLabel    = "— / — GB";
        LastUpdated = "—";
        CpuBarBrush = GreenBrush;
        RamBarBrush = GreenBrush;
    }

    private void UpdateServerStatusBadge(PingStatus status)
    {
        ServerStatusBadge = status switch
        {
            PingStatus.Online   => "● Online",
            PingStatus.Offline  => "● Offline",
            PingStatus.Checking => "● Checking…",
            _                   => string.Empty
        };
    }

    private static SolidColorBrush PickBrush(double percent) => percent switch
    {
        >= 90 => RedBrush,
        >= 70 => AmberBrush,
        _     => GreenBrush
    };

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    
    private void LoadCachedLocalEventLog()
    {
        var cached = _localEventLog.LastSnapshot;

        if (cached.Count == 0)
        {
            EventLogStatus = "Loading local event log…";
            return;
        }

        EventEntries.Clear();
        foreach (var e in cached)
            EventEntries.Add(new EventLogEntryViewModel(e));

        EventLogStatus = $"{EventEntries.Count} recent error(s) — " +
                         $"last updated {DateTime.Now:HH:mm:ss}";
    }
    
    private async Task LoadRemoteResourceAsync(ServerDashboardEntry server)
    {
        _remoteResourceCts?.Cancel();
        _remoteResourceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _remoteResourceCts = cts;

        try
        {
            var result = await _remoteResource
                .FetchAsync(server.IP, cts.Token)
                .ConfigureAwait(false);

            if (cts.Token.IsCancellationRequested) return;

            await _dispatcher.InvokeAsync(() =>
            {
                if (SelectedServer != server) return; // захист від race при швидкому перемиканні

                if (!result.IsReachable)
                {
                    // EventLog-статус вже повідомить про офлайн — тут просто лишаємо метрики порожніми
                    return;
                }

                if (!result.IsSuccess || result.Snapshot is null)
                {
                    CpuLabel = "WMI error";
                    RamLabel = result.ErrorMessage ?? "Failed to read metrics.";
                    return;
                }

                var s = result.Snapshot;
                CpuPercent  = s.CpuPercent;
                RamPercent  = s.RamPercent;
                RamUsedGb   = s.RamUsedGb;
                RamTotalGb  = s.RamTotalGb;
                CpuLabel    = $"{s.CpuPercent:F1} %";
                RamLabel    = $"{s.RamUsedGb:F1} GB  /  {s.RamTotalGb:F1} GB";
                LastUpdated = s.Timestamp.ToLocalTime().ToString("HH:mm:ss");
                CpuBarBrush = PickBrush(s.CpuPercent);
                RamBarBrush = PickBrush(s.RamPercent);
            });

            // Плануємо наступне оновлення через 5с, поки цей сервер обраний.
            // 5с (не 3с як для localhost) — WMI значно дорожчий за PerformanceCounter,
            // не хочемо створювати зайве навантаження на мережу/RPC.
            if (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                if (!_disposed && !cts.Token.IsCancellationRequested && SelectedServer == server)
                    _ = LoadRemoteResourceAsync(server);
            }
        }
        catch (OperationCanceledException)
        {
            // Очікувано при швидкому перемиканні серверів — таймер просто зупиняється
        }
    }
    
    private async Task LoadRemoteEventLogAsync(ServerDashboardEntry server)
    {
        // Скасовуємо попередній запит якщо користувач швидко перемикає сервери
        _remoteEventLogCts?.Cancel();
        _remoteEventLogCts?.Dispose();
        var cts = new CancellationTokenSource();
        _remoteEventLogCts = cts;

        EventLogStatus = $"Checking if {server.Name} is reachable…";

        try
        {
            var result = await _remoteEventLog
                .FetchAsync(server.IP, cts.Token)
                .ConfigureAwait(false);

            // Якщо користувач встиг обрати інший сервер поки чекали — ігноруємо застарілий результат
            if (cts.Token.IsCancellationRequested) return;

            await _dispatcher.InvokeAsync(() =>
            {
                if (SelectedServer != server) return; // подвійна страховка від races

                if (!result.IsReachable)
                {
                    EventLogStatus = $"{server.Name} is offline — event log not fetched.";
                    return;
                }

                if (!result.IsSuccess)
                {
                    EventLogStatus = $"Failed to read event log: {result.ErrorMessage}";
                    return;
                }

                EventEntries.Clear();
                foreach (var e in result.Entries)
                    EventEntries.Add(new EventLogEntryViewModel(e));

                EventLogStatus = result.Entries.Count == 0
                    ? $"No recent errors on {server.Name}."
                    : $"{result.Entries.Count} recent error(s) on {server.Name} — " +
                      $"fetched {DateTime.Now:HH:mm:ss}";
            });
        }
        catch (OperationCanceledException)
        {
            // Очікувано при швидкому перемиканні серверів — ігноруємо
        }
    }
    
    // ── IDisposable ──────────────────────────────────────────────────────────

    private bool _disposed;

    /// <summary>
    /// Скасовує будь-які активні фонові WMI/EventLog запити та зупиняє
    /// self-rescheduling polling-петлю в LoadRemoteResourceAsync.
    /// Без цього виклику при закритті програми поки відкрита вкладка
    /// Resources з обраним remote сервером — фоновий Task.Run(QueryWmi)
    /// може продовжити виконуватись на thread pool вже після завершення
    /// UI, "висячи" до WmiTimeoutSeconds.
    /// Викликається явно з App.xaml.cs у OnExit.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _remoteResourceCts?.Cancel();
        _remoteResourceCts?.Dispose();
        _remoteResourceCts = null;

        _remoteEventLogCts?.Cancel();
        _remoteEventLogCts?.Dispose();
        _remoteEventLogCts = null;
    }
}