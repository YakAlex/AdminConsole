using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;

namespace AdminConsole.ViewModels;

public sealed partial class ZabbixViewModel
    : ObservableObject,
      IRecipient<ZabbixProblemsUpdatedMessage>,
      IRecipient<MonitoringToggledMessage>     // ← edge-case #3: UI-заглушка при вимкненому моніторингу
{
    // ── Observable state ─────────────────────────────────────────────────────

    public ObservableCollection<ZabbixProblemViewModel> Problems { get; } = [];

    [ObservableProperty] private ZabbixProblemViewModel? _selectedProblem;
    [ObservableProperty] private string _connectionStatus = "Connecting…";
    [ObservableProperty] private string _statusColor      = "#FF607D8B";
    [ObservableProperty] private int    _disasterCount;
    [ObservableProperty] private int    _highCount;
    [ObservableProperty] private string _lastFetched      = "—";
    [ObservableProperty] private bool   _isConnected;

    /// <summary>
    /// true — Zabbix-моніторинг вимкнено в Settings. XAML повинен сховати
    /// список проблем і показати заглушку "Моніторинг вимкнено" (edge-case #3).
    /// </summary>
    [ObservableProperty] private bool _isMonitoringDisabled;

    // ── Constructor ──────────────────────────────────────────────────────────

    public ZabbixViewModel(IMessenger messenger)
    {
        messenger.RegisterAll(this);
    }

    /// <summary>
    /// Edge-case #3: синхронізує IsMonitoringDisabled. Спрацьовує на реальне
    /// перемикання користувачем та один раз на холодному старті
    /// (SettingsViewModel конструктор).
    /// </summary>
    public void Receive(MonitoringToggledMessage message)
    {
        if (message.Service != MonitoredService.Zabbix) return;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            IsMonitoringDisabled = !message.Value;

            if (!message.Value)
            {
                Problems.Clear();
                DisasterCount    = 0;
                HighCount        = 0;
                IsConnected      = false;
                ConnectionStatus = "Моніторинг вимкнено в Settings";
                StatusColor      = "#FF607D8B";
            }
            else
            {
                ConnectionStatus = "Connecting…";
                StatusColor      = "#FF607D8B";
            }
        });
    }

    // ── IRecipient ────────────────────────────────────────────────────────────

    public void Receive(ZabbixProblemsUpdatedMessage message)
    {
        var payload = message.Value;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            // Захист від запізнілих повідомлень в черзі диспетчера,
            // які могли надійти тільки що після вимкнення (edge-case #3).
            if (IsMonitoringDisabled)
                return;

            if (payload.Problems is not null)
            {
                var incoming    = payload.Problems;
                var incomingIds = incoming.Select(p => p.EventId).ToHashSet();

                for (int i = Problems.Count - 1; i >= 0; i--)
                {
                    if (!incomingIds.Contains(Problems[i].EventId))
                        Problems.RemoveAt(i);
                }

                var existingIds = Problems.Select(p => p.EventId).ToHashSet();
                foreach (var p in incoming)
                {
                    if (!existingIds.Contains(p.EventId))
                        Problems.Add(new ZabbixProblemViewModel(p));
                }

                DisasterCount = Problems.Count(p => p.Severity == ZabbixSeverity.Disaster);
                HighCount     = Problems.Count(p => p.Severity == ZabbixSeverity.High);
            }

            if (payload.ErrorMessage is not null)
            {
                IsConnected      = false;
                ConnectionStatus = "Connection lost";
                StatusColor      = "#FFF44336";
            }
            else
            {
                IsConnected      = true;
                ConnectionStatus = "Connected";
                StatusColor      = "#FF4CAF50";
            }

            LastFetched = payload.FetchedAt.ToLocalTime().ToString("HH:mm:ss");
        });
    }
}