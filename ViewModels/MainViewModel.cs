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

    /// <summary>
    /// Прив'язується до CheckBox у Settings overlay.
    /// При зміні — одразу зберігає на диск.
    /// </summary>
    public bool CloseToTray
    {
        get => _userSettings.Current.CloseToTray;
        set
        {
            if (_userSettings.Current.CloseToTray == value) return;
            _userSettings.Current.CloseToTray = value;
            _userSettings.Save();
            OnPropertyChanged();
        }
    }

    public MainViewModel(
        PingDashboardViewModel   pingDashboard,
        UptimeViewModel          uptime,
        ResourceMonitorViewModel resourceMonitor,
        RdpSessionViewModel      rdpSessions,
        ZabbixViewModel          zabbix,
        LogsViewModel            logs,
        UserSettingsService      userSettings)
    {
        PingDashboard   = pingDashboard;
        Uptime          = uptime;
        ResourceMonitor = resourceMonitor;
        RdpSessions     = rdpSessions;
        Zabbix          = zabbix;
        Logs            = logs;
        _userSettings   = userSettings;

        // Ініціалізуємо через NavigateTo щоб CurrentPageTitle
        // завжди був синхронізований з CurrentView,
        // навіть якщо порядок вкладок зміниться у майбутньому.
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
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    public void SetStatusBarText(string text)
    {
        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(
            () => StatusBarText = text);
    }
}