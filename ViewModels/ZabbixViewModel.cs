using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;

namespace AdminConsole.ViewModels;

public sealed partial class ZabbixViewModel
    : ObservableObject, IRecipient<ZabbixProblemsUpdatedMessage>
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

    // ── Constructor ──────────────────────────────────────────────────────────

    public ZabbixViewModel(IMessenger messenger)
    {
        messenger.RegisterAll(this);
    }

    // ── IRecipient ────────────────────────────────────────────────────────────

    public void Receive(ZabbixProblemsUpdatedMessage message)
    {
        var payload = message.Value;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            var incoming = payload.Problems ?? [];
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

            // 3. Оновлюємо статус підключення залежно від результату поллінгу
            if (payload.ErrorMessage is not null)
            {
                IsConnected      = false;
                ConnectionStatus = "Error";
                StatusColor      = "#FFF44336"; // червоний
            }
            else
            {
                IsConnected      = true;
                ConnectionStatus = "Connected";
                StatusColor      = "#FF4CAF50"; // зелений
            }

            // 4. Час останнього оновлення
            LastFetched = payload.FetchedAt.ToLocalTime().ToString("HH:mm:ss");
        });
    }
}