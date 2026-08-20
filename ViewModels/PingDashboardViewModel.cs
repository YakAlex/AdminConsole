using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using AdminConsole.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Options;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;

namespace AdminConsole.ViewModels;

public sealed partial class PingDashboardViewModel
    : ObservableObject, IRecipient<PingBatchResultMessage>
{
    public ObservableCollection<PingResultViewModel> Servers { get; } = [];
    private readonly Dictionary<string, PingResultViewModel> _byIp = new();
    public CollectionViewSource GroupedServers { get; } = new();

    [ObservableProperty] private string              _summaryText   = "Initialising…";
    [ObservableProperty] private int                 _onlineCount;
    [ObservableProperty] private int                 _offlineCount;
    [ObservableProperty] private int                 _totalCount;
    [ObservableProperty] private PingResultViewModel? _selectedServer;

    /// <summary>
    /// Сирий час завершення останнього ping-циклу (з PingBatchPayload.
    /// CycleCompletedAt) — для Overview-картки "наступна перевірка через...".
    /// Прилітає і з main loop, і з recovery loop (recovery опитує лише
    /// офлайн-сервери) — тобто це "коли востаннє щось реально оновилось",
    /// не гарантовано повний цикл по всьому флоту.
    /// </summary>
    [ObservableProperty] private DateTimeOffset? _lastCycleAt;

    // ── Services injected here so they can be forwarded to each row VM ────────
    private readonly RemoteManagementService _remoteMgmt;
    private readonly IDialogService          _dialog;

    private readonly MaintenanceService _maintenance;

    public PingDashboardViewModel(
        IMessenger messenger,
        IOptions<List<ServerEntry>> serversOptions,
        RemoteManagementService remoteMgmt,
        IDialogService dialog,
        MaintenanceService maintenance)
    {
        _remoteMgmt  = remoteMgmt;
        _dialog      = dialog;
        _maintenance = maintenance;

        foreach (var entry in serversOptions.Value)
        {
            Servers.Add(new PingResultViewModel(
                entry.Name, entry.IP, entry.Group, entry.Type,
                _remoteMgmt, _dialog, _maintenance, messenger));
        }
        foreach (var s in Servers)
            _byIp[s.IP] = s;

        TotalCount = Servers.Count;

        GroupedServers.Source = Servers;
        GroupedServers.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(PingResultViewModel.Group)));

        messenger.RegisterAll(this);
        UpdateSummary();
    }
    
    public void Receive(PingBatchResultMessage message)
    {
        var results = message.Value.Results;
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            foreach (var result in results)
            {
                if (_byIp.TryGetValue(result.IP, out var row))
                    row.ApplyResult(result);
            }
            UpdateSummary();
            LastCycleAt = message.Value.CycleCompletedAt;
        });
    }

    private void UpdateSummary()
    {
        int online = 0, offline = 0;
        foreach (var s in Servers)
        {
            if      (s.Status == PingStatus.Online)  online++;
            else if (s.Status == PingStatus.Offline) offline++;
        }
        OnlineCount  = online;
        OfflineCount = offline;
        SummaryText  = $"Online: {OnlineCount}  |  Offline: {OfflineCount}  |  Total: {TotalCount}";
    }
}