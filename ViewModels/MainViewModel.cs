using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public PingDashboardViewModel PingDashboard { get; }
    public ResourceMonitorViewModel ResourceMonitor { get; }
    public RdpSessionViewModel RdpSessions { get; }
    public ZabbixViewModel Zabbix { get; }
    public LogsViewModel Logs { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _currentPageTitle = "Ping Dashboard";

    [ObservableProperty]
    private string _statusBarText = "Ready";

    public MainViewModel(
        PingDashboardViewModel pingDashboard,
        ResourceMonitorViewModel resourceMonitor,
        RdpSessionViewModel rdpSessions,
        ZabbixViewModel zabbix,
        LogsViewModel logs)
    {
        PingDashboard = pingDashboard;
        ResourceMonitor = resourceMonitor;
        RdpSessions = rdpSessions;
        Zabbix = zabbix;
        Logs = logs;
    }

    // CommandParameter from XAML Button arrives as a string — parse it here.
    [RelayCommand]
    private void NavigateTo(string? tabIndexParam)
    {
        if (!int.TryParse(tabIndexParam, out int index)) return;

        SelectedTabIndex = index;
        CurrentPageTitle = index switch
        {
            0 => "Ping Dashboard",
            1 => "Resource Monitor",
            2 => "RDP Sessions",
            3 => "Zabbix Alerts",
            4 => "Logs",
            _ => "Admin Console"
        };
    }

    public void SetStatusBarText(string text)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => StatusBarText = text);
    }
}