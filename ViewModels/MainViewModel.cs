using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // ── Child ViewModels ──────────────────────────────────────────────────────
    public PingDashboardViewModel   PingDashboard   { get; }
    public ResourceMonitorViewModel ResourceMonitor { get; }
    public RdpSessionViewModel      RdpSessions     { get; }
    public ZabbixViewModel          Zabbix          { get; }
    public LogsViewModel            Logs            { get; }

    // ── Navigation ────────────────────────────────────────────────────────────
    // CurrentView holds the active child ViewModel.
    // MainWindow.xaml maps each type to its View via typed DataTemplates —
    // no Visibility toggling, no RelativeSource, no inline UserControl instances.
    [ObservableProperty]
    private object _currentView = null!;

    [ObservableProperty]
    private string _currentPageTitle = "Ping Dashboard";

    [ObservableProperty]
    private string _statusBarText = "Ready";

    public MainViewModel(
        PingDashboardViewModel   pingDashboard,
        ResourceMonitorViewModel resourceMonitor,
        RdpSessionViewModel      rdpSessions,
        ZabbixViewModel          zabbix,
        LogsViewModel            logs)
    {
        PingDashboard   = pingDashboard;
        ResourceMonitor = resourceMonitor;
        RdpSessions     = rdpSessions;
        Zabbix          = zabbix;
        Logs            = logs;

        // Default view on startup.
        _currentView = pingDashboard;
    }

    [RelayCommand]
    private void NavigateTo(string? tabIndexParam)
    {
        if (!int.TryParse(tabIndexParam, out int index)) return;

        (CurrentView, CurrentPageTitle) = index switch
        {
            0 => ((object)PingDashboard,  "Ping Dashboard"),
            1 => (ResourceMonitor,         "Resource Monitor"),
            2 => (RdpSessions,             "RDP Sessions"),
            3 => (Zabbix,                  "Zabbix Alerts"),
            4 => (Logs,                    "Logs"),
            _ => (PingDashboard,           "Admin Console")
        };
    }

    public void SetStatusBarText(string text)
    {
        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(
            () => StatusBarText = text);
    }
}