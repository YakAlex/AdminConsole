using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Services;

namespace AdminConsole.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // ── Child ViewModels ──────────────────────────────────────────────────────
    public PingDashboardViewModel   PingDashboard   { get; }
    public UptimeViewModel          Uptime          { get; }
    public ResourceMonitorViewModel ResourceMonitor { get; }
    public RdpSessionViewModel      RdpSessions     { get; }
    public ZabbixViewModel          Zabbix          { get; }
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
        PingDashboardViewModel   pingDashboard,
        UptimeViewModel          uptime,
        ResourceMonitorViewModel resourceMonitor,
        RdpSessionViewModel      rdpSessions,
        ZabbixViewModel          zabbix,
        LogsViewModel            logs,
        SettingsViewModel        settings,
        UserSettingsService      userSettings)
    {
        PingDashboard   = pingDashboard;
        Uptime          = uptime;
        ResourceMonitor = resourceMonitor;
        RdpSessions     = rdpSessions;
        Zabbix          = zabbix;
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
            0 => ((object)PingDashboard,  "Ping Dashboard"),
            1 => (Uptime,                  "Uptime & Incidents"),
            2 => (ResourceMonitor,         "Resource Monitor"),
            3 => (RdpSessions,             "RDP Sessions"),
            4 => (Zabbix,                  "Zabbix Alerts"),
            5 => (Logs,                    "Logs"),
            _ => (PingDashboard,           "Admin Console")
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