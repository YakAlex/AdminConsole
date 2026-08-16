using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using AdminConsole.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace AdminConsole.ViewModels;

/// <summary>
/// Рівень "здоров'я" одного модуля для кольорового кодування картки.
/// Disabled — модуль свідомо вимкнено в Settings: це НЕ помилка, тому
/// нейтральний сірий колір, а не червоний, і Disabled-модуль не псує
/// загальний OverallHealth-банер.
/// </summary>
public enum OverviewHealthStatus { Ok, Warning, Critical, Disabled }

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
    : ObservableObject, IRecipient<MaintenanceChangedMessage>
{
    public PingDashboardViewModel Ping    { get; }
    public UptimeViewModel        Uptime  { get; }
    public RdpSessionViewModel    Rdp     { get; }
    public ZabbixViewModel        Zabbix  { get; }
    public BackupsViewModel       Backups { get; }

    private readonly MaintenanceService _maintenance;

    [ObservableProperty] private int _activeMaintenanceCount;

    // ── Overview 2.0: похідні top-5 фіди для кожної картки ──────────────────

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
        MaintenanceService      maintenance)
    {
        Ping         = ping;
        Uptime       = uptime;
        Rdp          = rdp;
        Zabbix       = zabbix;
        Backups      = backups;
        _maintenance = maintenance;

        // Холодний старт — не чекаємо перший PropertyChanged, одразу
        // рахуємо всі похідні фіди й лічильники з поточного стану child VM.
        RefreshMaintenanceWindows();
        RefreshOfflineServers();
        RefreshTopSessions();
        RefreshTopProblems();
        RefreshBackupsDerived();

        // Health-властивості нижче — звичайні C# getter-и, не [ObservableProperty],
        // тому WPF не дізнається про їх зміну автоматично. Перепідписуємось на
        // PropertyChanged кожного child VM і вручну проштовхуємо OnPropertyChanged
        // для Health-властивостей, композитного OverallHealth і похідних фідів.
        // Усі child VM вже піднімають власний PropertyChanged на UI-потоці,
        // тому тут додаткового InvokeAsync не потрібно.
        Ping.PropertyChanged += (_, _) =>
        {
            RaiseHealth(nameof(PingHealth));
            RefreshOfflineServers();
        };
        Uptime.PropertyChanged += (_, _) => RaiseHealth(nameof(UptimeHealth));
        Rdp.PropertyChanged += (_, _) =>
        {
            RaiseHealth(nameof(RdpHealth));
            RefreshTopSessions();
        };
        Zabbix.PropertyChanged += (_, _) =>
        {
            RaiseHealth(nameof(ZabbixHealth));
            RefreshTopProblems();
        };
        Backups.PropertyChanged += (_, _) =>
        {
            RaiseHealth(nameof(BackupsHealth));
            RefreshBackupsDerived();
        };

        messenger.RegisterAll(this);
    }

    private void RaiseHealth(string propertyName)
    {
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(OverallHealth));
        OnPropertyChanged(nameof(OverallHealthDetail));
    }

    public void Receive(MaintenanceChangedMessage message)
        => Application.Current?.Dispatcher?.InvokeAsync(RefreshMaintenanceWindows);

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