using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;

namespace AdminConsole.ViewModels;

public sealed partial class RdpSessionViewModel
    : ObservableObject,
      IRecipient<RdpSessionsUpdatedMessage>,
      IRecipient<RdpCredentialsClearedMessage>,
      IRecipient<CredentialsChangedMessage>       // ← додаємо новий інтерфейс
{
    public ObservableCollection<RdpSessionRowViewModel> Sessions { get; } = [];
    public ObservableCollection<ServerPollStatusViewModel> ServerStatuses { get; } = [];

    [ObservableProperty] private RdpSessionRowViewModel? _selectedSession;
    [ObservableProperty] private string _statusText = "Waiting for first poll…";
    [ObservableProperty] private int    _activeCount;
    [ObservableProperty] private int    _disconnectedCount;

    // Встановлюється при видаленні credentials — блокує успішні повідомлення
    // що лежали в черзі диспетчера до видалення.
    // Скидається одразу при отриманні CredentialsChangedMessage(Saved) —
    // тобто до того як прийдуть результати нового poll.
    private bool _credentialsCleared;

    public RdpSessionViewModel(IMessenger messenger)
    {
        messenger.RegisterAll(this);
    }

    // Скидаємо прапорець як тільки нові credentials збережено —
    // наступний poll буде від нових credentials і має відображатись нормально.
    public void Receive(CredentialsChangedMessage message)
    {
        if (message.Target != CredentialTarget.Rdp) return;
        if (message.Action != CredentialAction.Saved) return;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            _credentialsCleared = false;
        });
    }

    public void Receive(RdpCredentialsClearedMessage _)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            _credentialsCleared = true;

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

    public void Receive(RdpSessionsUpdatedMessage message)
    {
        var payload = message.Value;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            // Якщо credentials видалено і нові ще не збережено —
            // ігноруємо будь-які повідомлення (і успішні і з помилками від старого poll).
            // Як тільки юзер збереже нові credentials — Receive(CredentialsChangedMessage)
            // скине _credentialsCleared до того як прийде перший результат нового poll.
            if (_credentialsCleared)
                return;

            var toRemove = Sessions
                .Where(s => s.ServerIp == payload.ServerIp)
                .ToList();

            foreach (var row in toRemove)
                Sessions.Remove(row);

            foreach (var session in payload.Sessions)
                Sessions.Add(new RdpSessionRowViewModel(session));

            var statusRow = ServerStatuses
                .FirstOrDefault(s => s.ServerIp == payload.ServerIp);

            if (statusRow is null)
            {
                statusRow = new ServerPollStatusViewModel(
                    payload.ServerName, payload.ServerIp);
                ServerStatuses.Add(statusRow);
            }

            statusRow.Update(payload);

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