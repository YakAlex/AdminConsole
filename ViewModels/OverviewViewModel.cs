using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using AdminConsole.Services;
using AdminConsole.Core.Models.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AdminConsole.Configuration;

namespace AdminConsole.ViewModels;

/// <summary>
/// Рівень "здоров'я" одного модуля для кольорового кодування картки.
/// Disabled — модуль свідомо вимкнено в Settings: це НЕ помилка, тому
/// нейтральний сірий колір, а не червоний, і Disabled-модуль не псує
/// загальний OverallHealth-банер.
/// </summary>
public enum OverviewHealthStatus { Ok, Warning, Critical, Disabled }

/// <summary>Середня затримка по одній групі серверів (з appsettings.json) — для розбивки на Ping-картці.</summary>
public sealed record GroupLatency(string GroupName, double AverageMs);

/// <summary>
/// Стартова вкладка — Overview 2.0. Досі НЕ рахує нічого сам і не
/// підписується на raw Push-повідомлення (PingBatchResultMessage тощо) —
/// читає вже готові ObservableProperty з existing singleton ViewModels.
///
/// Нове у 2.0: похідні top-5 "фіди" для кожної картки (офлайн-сервери,
/// останні RDP-сесії, топ Zabbix-проблем, проблемні бекапи) — це чисті
/// проекції (сортування/фільтр/Take(5)) з тих самих колекцій, що вже
/// живуть у child VM. Перераховуються при спрацюванні PropertyChanged
/// відповідного child VM — того самого хука, що вже існував для
/// Health-властивостей. Жодної нової Push-підписки на сирі повідомлення
/// не додано.
///
/// Виняток — MaintenanceService: Overview сам підписується на
/// MaintenanceChangedMessage і при кожній зміні повністю перечитує
/// список активних вікон (списки тут завжди малі — diff не потрібен).
/// </summary>
public sealed partial class OverviewViewModel
    : ObservableObject, IRecipient<MaintenanceChangedMessage>, IRecipient<AppLogEntryMessage>
{
    public PingDashboardViewModel Ping    { get; }
    public UptimeViewModel        Uptime  { get; }
    public RdpSessionViewModel    Rdp     { get; }
    public ZabbixViewModel        Zabbix  { get; }
    public BackupsViewModel       Backups { get; }

    private readonly MaintenanceService _maintenance;
    private readonly SlaReportService _slaReport;

    [ObservableProperty] private int _activeMaintenanceCount;

    // ── Ping: середня затримка + розбивка по групах ──────────────────────────

    /// <summary>null, якщо жоден сервер зараз не Online з валідним latency.</summary>
    [ObservableProperty] private double? _averageLatencyMs;

    /// <summary>Топ-3 групи за середньою затримкою (найвища першою — найцікавіша для діагностики).</summary>
    [ObservableProperty] private IReadOnlyList<GroupLatency> _latencyByGroup = Array.Empty<GroupLatency>();

    // ── Backups: сумарний обсяг + найсвіжіший бекап ──────────────────────────

    [ObservableProperty] private string  _totalBackupSizeDisplay = "—";
    [ObservableProperty] private string? _latestBackupName;
    [ObservableProperty] private string? _latestBackupAgoText;

    private readonly DispatcherTimer _relativeTimeTimer;
    
    /// <summary>
    /// Останні події з журналу застосунку (той самий AppLogEntryMessage,
    /// що наповнює FileLoggerService/LogsViewModel) — обмежена в пам'яті
    /// стрічка, найновіші зверху. Не читає файл з диска — лише те, що
    /// прилетіло через messenger, поки OverviewViewModel живий (тобто
    /// весь час роботи застосунку, він singleton).
    /// </summary>
    public ObservableCollection<AppLogEntry> RecentActivity { get; } = new();
    private const int MaxRecentActivity = 4;

    /// <summary>
    /// Погодинний Uptime% за останні 24 години (24 точки, від найстарішої
    /// до найновішої) — для міні-кривої на картці Uptime. Рахується через
    /// SlaReportService.Generate() по одному виклику на кожну годинну
    /// "коробку" — це чиста in-memory математика над UptimeTrackerService.
    /// GetSnapshot(), без жодного нового збору/зберігання даних.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<double> _uptimeTrendPercents = Array.Empty<double>();

    /// <summary>Агрегований Uptime% за весь 24-годинний період одним числом (для підпису біля кривої).</summary>
    [ObservableProperty] private double _uptime24HPercent = 100.0;

    private readonly DispatcherTimer _trendTimer;
    private volatile bool _isRecomputingTrend;

    // Час роботи застосунку ─────────────────────────────────────

    private readonly DateTimeOffset _appStartedAt =
        System.Diagnostics.Process.GetCurrentProcess().StartTime;

    [ObservableProperty] private string _appUptimeText = "—";

    // "наступна перевірка через..." ─────────────────────────────

    private readonly int _pingIntervalSeconds;
    private readonly int _backupPollIntervalMinutes;

    [ObservableProperty] private string _nextPingCheckText   = "—";
    [ObservableProperty] private string _nextBackupCheckText = "—";

    private readonly DispatcherTimer _pingCountdownTimer;
    

    // ── Overview

    public ObservableCollection<PingResultViewModel>    OfflineServers           { get; } = [];
    public ObservableCollection<RdpSessionRowViewModel> TopSessions              { get; } = [];
    public ObservableCollection<ZabbixProblemViewModel> TopProblems              { get; } = [];
    public ObservableCollection<BackupRowViewModel>     ProblemBackups           { get; } = [];
    public ObservableCollection<MaintenanceWindow>      ActiveMaintenanceWindows { get; } = [];

    [ObservableProperty] private int _offlineOverflowCount;
    [ObservableProperty] private int _topSessionsOverflowCount;
    [ObservableProperty] private int _topProblemsOverflowCount;
    [ObservableProperty] private int _problemBackupsOverflowCount;

    /// <summary>Найдавніший ПІДТВЕРДЖЕНИЙ бекап серед УСІХ рядків (включно з Ok) — раннє попередження ще до формального Stale.</summary>
    [ObservableProperty] private BackupRowViewModel? _oldestConfirmedBackup;
    [ObservableProperty] private string              _oldestConfirmedBackupAge = "—";

    /// <summary>Найгірший (за Outcome) проблемний запис — для спарклайну розміру.</summary>
    [ObservableProperty] private BackupRowViewModel? _worstBackup;

    public OverviewViewModel(
        IMessenger              messenger,
        PingDashboardViewModel  ping,
        UptimeViewModel         uptime,
        RdpSessionViewModel     rdp,
        ZabbixViewModel         zabbix,
        BackupsViewModel        backups,
        MaintenanceService      maintenance,
        SlaReportService        slaReport,
        IOptions<MonitoringSettings>  monitoringSettings)
    {
        Ping         = ping;
        Uptime       = uptime;
        Rdp          = rdp;
        Zabbix       = zabbix;
        Backups      = backups;
        _maintenance = maintenance;
        _slaReport = slaReport;
        _pingIntervalSeconds       = monitoringSettings.Value.PingIntervalSeconds;
        _backupPollIntervalMinutes = monitoringSettings.Value.BackupPollIntervalMinutes;
        
        ActiveMaintenanceCount = maintenance.GetActiveWindows().Count;

        // Холодний старт — не чекаємо перший PropertyChanged, одразу
        // рахуємо всі похідні фіди й лічильники з поточного стану child VM.
        RefreshMaintenanceWindows();
        RefreshOfflineServers();
        RefreshTopSessions();
        RefreshTopProblems();
        RefreshBackupsDerived();

        Ping.PropertyChanged    += (_, _) => { RaiseHealth(nameof(PingHealth), nameof(PingOnlineFraction)); RecomputeNextPingCheckText(); };
        Uptime.PropertyChanged  += (_, _) => RaiseHealth(nameof(UptimeHealth));
        Rdp.PropertyChanged     += (_, _) => RaiseHealth(nameof(RdpHealth));
        Zabbix.PropertyChanged  += (_, _) => RaiseHealth(nameof(ZabbixHealth));
        Backups.PropertyChanged += (_, _) => { RaiseHealth(nameof(BackupsHealth)); RecomputeNextBackupCheckText(); };

        // Sessions завжди повністю Remove+Add за сервер на кожен poll
        // (RdpSessionRowViewModel незмінний) — CollectionChanged гарантовано
        // ловить кожну реальну зміну складу сесій, на відміну від
        // Rdp.PropertyChanged (лічильники можуть лишитись незмінними навіть
        // при зміні СКЛАДУ сесій).
        Rdp.Sessions.CollectionChanged += (_, _) => RefreshTopSessions();

        // Друга підписка "про всяк випадок" — якщо колись з'явиться проблема
        // без зміни складу колекції (малоймовірно з огляду на архітектуру
        // RdpMonitorService, але дешево підстрахуватись).
        Zabbix.Problems.CollectionChanged += (_, _) => RefreshTopProblems();

        foreach (var server in Ping.Servers)
            server.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PingResultViewModel.LatencyMs))
                    RecomputeLatencyStats();
                
                if (e.PropertyName == nameof(PingResultViewModel.Status))
                    RefreshOfflineServers();
            };

        foreach (var row in Backups.Rows)
            row.PropertyChanged += (_, _) => { RecomputeBackupStats(); RefreshBackupsDerived(); };

        Backups.Rows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (BackupRowViewModel row in e.NewItems)
                    row.PropertyChanged += (_, _) => { RecomputeBackupStats(); RefreshBackupsDerived(); };

            RecomputeBackupStats();
            RefreshBackupsDerived();
        };

        messenger.RegisterAll(this);

        // Холодний старт для обох — не чекаємо першої зміни LatencyMs/History.
        RecomputeLatencyStats();
        RecomputeBackupStats();

        // "X хв тому" застаріває навіть без нових даних — окремий легкий
        // таймер (1 хв), що лише перераховує текст, без важкої математики
        // (на відміну від _trendTimer вище, тут немає SlaReportService.Generate).
        _relativeTimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _relativeTimeTimer.Tick += (_, _) =>
        {
            RecomputeBackupStats();
            RecomputeAppUptime();
            RecomputeNextBackupCheckText();
        };
        _relativeTimeTimer.Start();

        // Холодний старт — крива не має бути порожньою до першого тику таймера.
        // Fire-and-forget: конструктор не може бути async, а тут це прийнятно —
        // RecomputeUptimeTrendAsync сам ловить власні винятки, тож unobserved
        // task exception тут не загрожує.
        _ = RecomputeUptimeTrendAsync();

        // Свідомо ОКРЕМИЙ, повільний таймер (5 хв), а НЕ підписка на
        // Uptime.PropertyChanged — той піднімається кожні 10с через
        // власний _refreshTimer UptimeViewModel (лічильник тривалості
        // активного інциденту), і перераховувати 24 SlaReportService.Generate()
        // виклики так часто було б зайвим навантаженням заради даних,
        // які реально змінюються раз на годину-дві.
        _trendTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _trendTimer.Tick += async (_, _) => await RecomputeUptimeTrendAsync();
        _trendTimer.Start();

        // Холодний старт для ідей 2 і 4 — не чекаємо перший тик таймера.
        RecomputeAppUptime();
        RecomputeNextPingCheckText();
        RecomputeNextBackupCheckText();

        // Окремий секундний таймер лише для ping-відліку (30с інтервал за
        // замовчуванням — хвилинної гранулярності _relativeTimeTimer нижче
        // для нього замало). Дешевий тик: лише віднімання TimeSpan і
        // форматування рядка, без жодних обчислень над колекціями.
        _pingCountdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pingCountdownTimer.Tick += (_, _) => RecomputeNextPingCheckText();
        _pingCountdownTimer.Start();
    }

    /// <summary>
    /// Сама важка частина (24× SlaReportService.Generate() над потенційно
    /// багаторічною історією UptimeTrackerService) виконується у фоновому
    /// потоці через Task.Run — Tick DispatcherTimer виконується на UI-потоці,
    /// і без цього винесення кожен перерахунок раз на 5 хв міг би дати
    /// помітний мікро-фріз інтерфейсу (завмирання скролу/анімацій) при
    /// великій історії.
    /// </summary>
    private async Task RecomputeUptimeTrendAsync()
    {
        if (_isRecomputingTrend) return;
        _isRecomputingTrend = true;

        try
        {
            var now = DateTimeOffset.Now;

            var (buckets, overallPercent) = await Task.Run(() =>
            {
                var tempBuckets = new List<double>(24);

                // 24 годинні "коробки", від найстарішої (i=23) до найновішої (i=0).
                // GetFleetAvailabilityPercent (а НЕ Generate().OverallUptimePercent) —
                // свідомо: останній рахує середнє по флоту, де коротке падіння
                // одного сервера розчиняється серед решти завжди-онлайн серверів
                // і на Overview показувало б оманливі "100.00%" навіть при
                // реальних падіннях. Деталі — в коментарі над самим методом.
                for (int i = 23; i >= 0; i--)
                {
                    var bucketFrom = now.AddHours(-(i + 1));
                    var bucketTo   = now.AddHours(-i);

                    tempBuckets.Add(_slaReport.GetFleetAvailabilityPercent(bucketFrom, bucketTo));
                }

                var overall = _slaReport.GetFleetAvailabilityPercent(now.AddHours(-24), now);

                return (tempBuckets, overall);
            });

            // Повернулись у UI-потік завдяки await — присвоєння ObservableProperty безпечне.
            UptimeTrendPercents = buckets;
            Uptime24HPercent    = overallPercent;
        }
        finally
        {
            _isRecomputingTrend = false;
        }
    }
    
    private void RecomputeLatencyStats()
    {
        var onlineWithLatency = Ping.Servers
            .Where(s => s.Status == PingStatus.Online && s.LatencyMs.HasValue)
            .ToList();

        AverageLatencyMs = onlineWithLatency.Count > 0
            ? onlineWithLatency.Average(s => s.LatencyMs!.Value)
            : null;

        LatencyByGroup = onlineWithLatency
            .GroupBy(s => s.Group)
            .Select(g => new GroupLatency(g.Key, g.Average(s => s.LatencyMs!.Value)))
            .OrderByDescending(g => g.AverageMs) // найвища затримка — найцікавіша для діагностики
            .Take(3)
            .ToList();
    }

    private void RecomputeBackupStats()
    {
        long totalBytes = Backups.Rows
            .Where(r => r.History.Count > 0)
            .Sum(r => r.History[^1].SizeBytes);

        TotalBackupSizeDisplay = FormatSize(totalBytes);

        var mostRecent = Backups.Rows
            .Where(r => r.LastConfirmedAt.HasValue)
            .OrderByDescending(r => r.LastConfirmedAt)
            .FirstOrDefault();

        LatestBackupName    = mostRecent?.Name;
        LatestBackupAgoText = mostRecent?.LastConfirmedAt is { } at
            ? FormatRelativeTime(DateTimeOffset.Now - at)
            : null;
    }
    
    private void RecomputeAppUptime()
    {
        AppUptimeText = FormatDuration(DateTimeOffset.Now - _appStartedAt);
    }

    private void RecomputeNextPingCheckText()
    {
        NextPingCheckText = Ping.LastCycleAt is { } at
            ? FormatCountdown(at.AddSeconds(_pingIntervalSeconds) - DateTimeOffset.Now)
            : "—";
    }

    private void RecomputeNextBackupCheckText()
    {
        NextBackupCheckText = Backups.LastUpdatedAt is { } at
            ? FormatCountdown(at.AddMinutes(_backupPollIntervalMinutes) - DateTimeOffset.Now)
            : "—";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays  >= 1) return $"{(int)span.TotalDays} дн {span.Hours} год";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} год {span.Minutes} хв";
        return $"{Math.Max(1, (int)span.TotalMinutes)} хв";
    }

    /// <summary>
    /// "≈" навмисно скрізь — це оцінка на основі часу останнього
    /// PingBatchResultMessage/BackupStatusUpdatedMessage + інтервал з
    /// конфіга, а не гарантований момент (recovery loop може оновити
    /// ping-дані частіше за основний цикл; ручне пробудження бекапів через
    /// Settings теж може статись раніше запланового).
    /// </summary>
    private static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "з хвилини на хвилину";
        if (remaining.TotalMinutes < 1) return $"≈{(int)remaining.TotalSeconds}с";
        if (remaining.TotalHours   < 1) return $"≈{(int)remaining.TotalMinutes} хв";
        return $"≈{(int)remaining.TotalHours} год {remaining.Minutes} хв";
    }

    private static string FormatSize(long bytes)
    {
        double tb = bytes / 1024.0 / 1024.0 / 1024.0 / 1024.0;
        if (tb >= 1) return $"{tb:0.##} TB";

        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
        if (gb >= 1) return $"{gb:0.##} GB";

        double mb = bytes / 1024.0 / 1024.0;
        return $"{mb:0.#} MB";
    }

    private static string FormatRelativeTime(TimeSpan ago)
    {
        if (ago < TimeSpan.Zero) ago = TimeSpan.Zero; // захист від дрейфу годинника

        if (ago.TotalMinutes < 1)   return "щойно";
        if (ago.TotalMinutes < 60)  return $"{(int)ago.TotalMinutes} хв тому";
        if (ago.TotalHours   < 24)  return $"{(int)ago.TotalHours} год тому";
        return $"{(int)ago.TotalDays} дн тому";
    }

    /// <summary>
    /// Разом з переданою Health-властивістю завжди проштовхує й композитні
    /// лічильники (OverallHealth, CriticalIssueCount, WarningIssueCount) —
    /// вони залежать від усіх child VM одразу, тому їх найпростіше й
    /// найнадійніше перераховувати на кожну зміну будь-якого з модулів,
    /// а не намагатись точково відстежувати, яка саме подія на що впливає.
    /// </summary>
    private void RaiseHealth(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
            OnPropertyChanged(name);

        OnPropertyChanged(nameof(OverallHealth));
        OnPropertyChanged(nameof(CriticalIssueCount));
        OnPropertyChanged(nameof(WarningIssueCount));
        OnPropertyChanged(nameof(OverallHealthDetail));
    }

    public void Receive(MaintenanceChangedMessage message)
    {
        Application.Current?.Dispatcher?.InvokeAsync(RefreshMaintenanceWindows);
    }

    public void Receive(AppLogEntryMessage message)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            RecentActivity.Insert(0, message.Value);

            while (RecentActivity.Count > MaxRecentActivity)
                RecentActivity.RemoveAt(RecentActivity.Count - 1);
        });
    }

    // ── Похідні top-5 фіди — обчислення ──────────────────────────────────────

    private void RefreshOfflineServers()
    {
        var offline = Ping.Servers
            .Where(s => s.Status == PingStatus.Offline)
            .OrderBy(s => s.Group)
            .ThenBy(s => s.Name)
            .ToList();

        ReplaceTop(OfflineServers, offline, 5);
        OfflineOverflowCount = Math.Max(0, offline.Count - 5);
    }

    private void RefreshTopSessions()
    {
        // Немає надійного "сирого" часу для LogonTime (лише форматований
        // рядок з quser) — сортувати за ним як за реальним часом було б
        // оманливо, тому впорядковуємо тільки за State (активні перші),
        // далі за іменем сервера/користувача для стабільності.
        var ordered = Rdp.Sessions
            .OrderBy(s => s.State switch
            {
                RdpSessionState.Active       => 0,
                RdpSessionState.Disconnected => 1,
                RdpSessionState.Idle         => 2,
                _                             => 3
            })
            .ThenBy(s => s.ServerName)
            .ThenBy(s => s.Username)
            .ToList();

        ReplaceTop(TopSessions, ordered, 5);
        TopSessionsOverflowCount = Math.Max(0, ordered.Count - 5);
    }

    private void RefreshTopProblems()
    {
        var ordered = Zabbix.Problems
            .OrderByDescending(p => (int)p.Severity)
            .ThenByDescending(p => p.StartTimeRaw)
            .ToList();

        ReplaceTop(TopProblems, ordered, 5);
        TopProblemsOverflowCount = Math.Max(0, ordered.Count - 5);
    }

    private void RefreshBackupsDerived()
    {
        var problems = Backups.Rows
            .Where(r => r.Outcome != BackupOutcome.Ok)
            .OrderByDescending(r => SeverityRank(r.Outcome))
            .ToList();

        ReplaceTop(ProblemBackups, problems, 5);
        ProblemBackupsOverflowCount = Math.Max(0, problems.Count - 5);

        WorstBackup = problems.FirstOrDefault();

        OldestConfirmedBackup = Backups.Rows
            .Where(r => r.LastConfirmedAt is not null)
            .OrderBy(r => r.LastConfirmedAt)
            .FirstOrDefault();

        OldestConfirmedBackupAge = OldestConfirmedBackup?.LastConfirmedAt is { } at
            ? FormatAge(DateTimeOffset.Now - at)
            : "—";
    }

    private void RefreshMaintenanceWindows()
    {
        var windows = _maintenance.GetActiveWindows();

        ActiveMaintenanceWindows.Clear();
        foreach (var w in windows) ActiveMaintenanceWindows.Add(w);

        ActiveMaintenanceCount = windows.Count;
    }

    private static void ReplaceTop<T>(ObservableCollection<T> target, IEnumerable<T> ordered, int take)
    {
        target.Clear();
        foreach (var item in ordered.Take(take)) target.Add(item);
    }

    private static int SeverityRank(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Missing     => 3,
        BackupOutcome.Stale       => 2,
        BackupOutcome.SizeWarning => 1,
        _                         => 0
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays  >= 1) return $"{(int)age.TotalDays} дн. тому";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours} год тому";
        return $"{Math.Max(1, (int)age.TotalMinutes)} хв тому";
    }

    // ── Health-статуси для кольорового кодування карток ─────────────────────

    public OverviewHealthStatus PingHealth =>
        Ping.OfflineCount > 0 ? OverviewHealthStatus.Critical : OverviewHealthStatus.Ok;

    public OverviewHealthStatus UptimeHealth =>
        Uptime.ActiveIncidents > 0 ? OverviewHealthStatus.Critical : OverviewHealthStatus.Ok;

    public OverviewHealthStatus RdpHealth =>
        Rdp.IsMonitoringDisabled ? OverviewHealthStatus.Disabled : OverviewHealthStatus.Ok;

    public OverviewHealthStatus ZabbixHealth
    {
        get
        {
            if (Zabbix.IsMonitoringDisabled) return OverviewHealthStatus.Disabled;
            if (Zabbix.DisasterCount > 0)    return OverviewHealthStatus.Critical;
            if (Zabbix.HighCount > 0)        return OverviewHealthStatus.Warning;
            return OverviewHealthStatus.Ok;
        }
    }

    public OverviewHealthStatus BackupsHealth
    {
        get
        {
            if (Backups.IsMonitoringDisabled) return OverviewHealthStatus.Disabled;
            if (Backups.BadCount > 0)         return OverviewHealthStatus.Critical;
            if (Backups.WarningCount > 0)     return OverviewHealthStatus.Warning;
            return OverviewHealthStatus.Ok;
        }
    }

    public OverviewHealthStatus OverallHealth
    {
        get
        {
            var statuses = new[] { PingHealth, UptimeHealth, ZabbixHealth, BackupsHealth }
                .Where(s => s != OverviewHealthStatus.Disabled)
                .ToList();

            if (statuses.Contains(OverviewHealthStatus.Critical)) return OverviewHealthStatus.Critical;
            if (statuses.Contains(OverviewHealthStatus.Warning))  return OverviewHealthStatus.Warning;
            return OverviewHealthStatus.Ok;
        }
    }

    // ── Дані для System Health hero-картки та Attention-панелі (Фаза 1) ─────

    /// <summary>
    /// Частка живих серверів (0.0–1.0) для кільця-прогресу. 1.0 (повне зелене
    /// кільце), якщо серверів взагалі не сконфігуровано — порожній список не
    /// повинен виглядати як "все офлайн".
    /// </summary>
    public double PingOnlineFraction =>
        Ping.TotalCount > 0 ? (double)Ping.OnlineCount / Ping.TotalCount : 1.0;

    /// <summary>
    /// Сумарна кількість "критичних" пунктів по всіх модулях одразу — для
    /// панелі Attention Required. Рахує реальну кількість проблемних
    /// елементів (не просто "модулів у Critical"), щоб цифра щось означала:
    /// напр. 3 offline-сервери + 1 відкритий інцидент = 4, а не 2.
    /// </summary>
    public int CriticalIssueCount =>
        Ping.OfflineCount + Uptime.ActiveIncidents + Zabbix.DisasterCount + Backups.BadCount;

    /// <summary>Те саме, але для Warning-рівня (Zabbix High + Backup SizeWarning).</summary>
    public int WarningIssueCount =>
        Zabbix.HighCount + Backups.WarningCount;


    /// <summary>
    /// Короткий рядок-розшифровка під банером — щоб зрозуміти "з чого саме"
    /// складається поточний статус, не відкриваючи жодної картки.
    /// </summary>
    public string OverallHealthDetail
    {
        get
        {
            var parts = new List<string>();

            if (Ping.OfflineCount > 0)
                parts.Add($"{Ping.OfflineCount} сервер(и) офлайн");
            if (Uptime.ActiveIncidents > 0)
                parts.Add($"{Uptime.ActiveIncidents} активних інцидент(и)");
            if (!Zabbix.IsMonitoringDisabled && Zabbix.DisasterCount > 0)
                parts.Add($"{Zabbix.DisasterCount} Zabbix Disaster");
            if (!Zabbix.IsMonitoringDisabled && Zabbix.HighCount > 0)
                parts.Add($"{Zabbix.HighCount} Zabbix High");
            if (!Backups.IsMonitoringDisabled && Backups.BadCount > 0)
                parts.Add($"{Backups.BadCount} бекап(и) критично");
            if (!Backups.IsMonitoringDisabled && Backups.WarningCount > 0)
                parts.Add($"{Backups.WarningCount} бекап(и) з попередженням");

            return parts.Count == 0 ? "Активних проблем не виявлено" : string.Join("   ·   ", parts);
        }
    }
}