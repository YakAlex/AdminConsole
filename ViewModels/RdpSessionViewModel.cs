using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;

namespace AdminConsole.ViewModels;

public sealed partial class RdpSessionViewModel
    : ObservableObject, IRecipient<RdpSessionsUpdatedMessage>, IRecipient<RdpCredentialsClearedMessage>
{
    // ── Observable state ─────────────────────────────────────────────────────

    /// <summary>Flat list of all sessions across all terminal servers.</summary>
    public ObservableCollection<RdpSessionRowViewModel> Sessions { get; } = [];

    /// <summary>One status entry per polled server.</summary>
    public ObservableCollection<ServerPollStatusViewModel> ServerStatuses { get; } = [];

    [ObservableProperty] private RdpSessionRowViewModel? _selectedSession;
    [ObservableProperty] private string _statusText = "Waiting for first poll…";
    [ObservableProperty] private int    _activeCount;
    [ObservableProperty] private int    _disconnectedCount;

    public RdpSessionViewModel(IMessenger messenger)
    {
        messenger.RegisterAll(this);
    }
    
    public void Receive(RdpCredentialsClearedMessage _)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            Sessions.Clear();
            foreach (var s in ServerStatuses)
                s.Update(new RdpSessionsPayload(
                    s.ServerName, s.ServerIp,
                    Sessions: [],
                    ErrorMessage: "Credentials видалено"));

            ActiveCount       = 0;
            DisconnectedCount = 0;
            StatusText        = "RDP credentials видалено — моніторинг призупинено.";
        });
    }

    // ── IRecipient ────────────────────────────────────────────────────────────

    public void Receive(RdpSessionsUpdatedMessage message)
    {
        var payload = message.Value;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            // Remove all existing sessions for this server, then re-add.
            var toRemove = Sessions
                .Where(s => s.ServerIp == payload.ServerIp)
                .ToList();

            foreach (var row in toRemove)
                Sessions.Remove(row);

            foreach (var session in payload.Sessions)
                Sessions.Add(new RdpSessionRowViewModel(session));

            // Update or insert server poll status.
            var statusRow = ServerStatuses
                .FirstOrDefault(s => s.ServerIp == payload.ServerIp);

            if (statusRow is null)
            {
                statusRow = new ServerPollStatusViewModel(
                    payload.ServerName, payload.ServerIp);
                ServerStatuses.Add(statusRow);
            }

            statusRow.Update(payload);

            // Recompute totals.
            ActiveCount       = Sessions.Count(s => s.State == RdpSessionState.Active);
            DisconnectedCount = Sessions.Count(s => s.State == RdpSessionState.Disconnected);

            int total = Sessions.Count;
            StatusText = payload.ErrorMessage is not null
                ? $"Error on {payload.ServerName}: {payload.ErrorMessage}"
                : $"{ActiveCount} active, {DisconnectedCount} disconnected" +
                  $" — {total} total session(s) — " +
                  $"last updated {DateTime.Now:HH:mm:ss}";
        });
    }
}