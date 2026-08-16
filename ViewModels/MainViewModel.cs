using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Services;

namespace AdminConsole.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
// ── Child ViewModels ──────────────────────────────────────────────────────
    public OverviewViewModel        Overview        { get; }
    public PingDashboardViewModel   PingDashboard   { get; }
    public UptimeViewModel          Uptime          { get; }
    public ResourceMonitorViewModel ResourceMonitor { get; }
    public RdpSessionViewModel      RdpSessions     { get; }
    public ZabbixViewModel          Zabbix          { get; }
    public BackupsViewModel         Backups         { get; }
    public LogsViewModel            Logs            { get; }
    public SettingsViewModel        Settings        { get; }

    private readonly UserSettingsService _userSettings;

    // ── Navigation ────────────────────────────────────────────────────────────
    [ObservableProperty]
    private object _currentView = null!;

    [ObservableProperty]
    private string _currentPageTitle = string.Empty;
    [ObservableProperty]
    private string _statusBarText = "Ready";

    // ── Settings overlay ──────────────────────────────────────────────────────
    [ObservableProperty]
    private bool _isSettingsOpen = false;

    public MainViewModel(
        OverviewViewModel        overview,
        PingDashboardViewModel   pingDashboard,
        UptimeViewModel          uptime,
        ResourceMonitorViewModel resourceMonitor,
        RdpSessionViewModel      rdpSessions,
        ZabbixViewModel          zabbix,
        BackupsViewModel         backups,
        LogsViewModel            logs,
        SettingsViewModel        settings,
        UserSettingsService      userSettings)
    {
        Overview        = overview;
        PingDashboard   = pingDashboard;
        Uptime          = uptime;
        ResourceMonitor = resourceMonitor;
        RdpSessions     = rdpSessions;
        Zabbix          = zabbix;
        Backups         = backups;
        Logs            = logs;
        Settings        = settings;
        _userSettings   = userSettings;

        NavigateTo("0");
    }

    [RelayCommand]
    private void NavigateTo(string? tabIndexParam)
    {
        if (!int.TryParse(tabIndexParam, out int index)) return;

        (CurrentView, CurrentPageTitle) = index switch
        {
            0 => ((object)Overview,        "Overview"),
            1 => (PingDashboard,           "Ping Dashboard"),
            2 => (Uptime,                  "Uptime & Incidents"),
            3 => (ResourceMonitor,         "Resource Monitor"),
            4 => (RdpSessions,             "RDP Sessions"),
            5 => (Zabbix,                  "Zabbix Alerts"),
            6 => (Backups,                 "Backup Verification"),
            7 => (Logs,                    "Logs"),
            _ => (Overview,                "Admin Console")
        };
    }

    [RelayCommand]
    private void OpenSettings()
    {
        Settings.RefreshCredentialState();
        Settings.LoadCloseToTray(_userSettings.Current.CloseToTray);
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        _userSettings.Current.CloseToTray = Settings.CloseToTray;
        _userSettings.Save();
        Settings.CancelPendingTest();
        IsSettingsOpen = false;
    }

    public void SetStatusBarText(string text)
    {
        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(
            () => StatusBarText = text);
    }
}