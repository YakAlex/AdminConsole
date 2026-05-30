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
    : ObservableObject, IRecipient<PingStatusChangedMessage>
{
    public ObservableCollection<PingResultViewModel> Servers { get; } = [];
    public CollectionViewSource GroupedServers { get; } = new();

    [ObservableProperty] private string              _summaryText   = "Initialising…";
    [ObservableProperty] private int                 _onlineCount;
    [ObservableProperty] private int                 _offlineCount;
    [ObservableProperty] private int                 _totalCount;
    [ObservableProperty] private PingResultViewModel? _selectedServer;

    // ── Services injected here so they can be forwarded to each row VM ────────
    private readonly RemoteManagementService _remoteMgmt;
    private readonly IDialogService          _dialog;

    public PingDashboardViewModel(
        IMessenger messenger,
        IOptions<List<ServerEntry>> serversOptions,
        RemoteManagementService remoteMgmt,
        IDialogService dialog)
    {
        _remoteMgmt = remoteMgmt;
        _dialog     = dialog;

        foreach (var entry in serversOptions.Value)
        {
            Servers.Add(new PingResultViewModel(
                entry.Name, entry.IP, entry.Group,
                _remoteMgmt, _dialog));
        }

        TotalCount = Servers.Count;

        GroupedServers.Source = Servers;
        GroupedServers.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(PingResultViewModel.Group)));

        messenger.RegisterAll(this);
        UpdateSummary();
    }

    public void Receive(PingStatusChangedMessage message)
    {
        var result = message.Value;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var row = Servers.FirstOrDefault(s => s.IP == result.IP);
            if (row is null) return;
            row.ApplyResult(result);
            UpdateSummary();
        });
    }

    private void UpdateSummary()
    {
        OnlineCount  = Servers.Count(s => s.Status == PingStatus.Online);
        OfflineCount = Servers.Count(s => s.Status == PingStatus.Offline);
        SummaryText  = $"Online: {OnlineCount}  |  Offline: {OfflineCount}  |  Total: {TotalCount}";
    }
}