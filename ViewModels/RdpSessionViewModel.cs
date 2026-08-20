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
      IRecipient<CredentialsChangedMessage>,      // ← додаємо новий інтерфейс
      IRecipient<MonitoringToggledMessage>        // ← edge-case #3: UI-заглушка при вимкненому моніторингу
{
    public ObservableCollection<RdpSessionRowViewModel> Sessions { get; } = [];
    public ObservableCollection<ServerPollStatusViewModel> ServerStatuses { get; } = [];

    [ObservableProperty] private RdpSessionRowViewModel? _selectedSession;
    [ObservableProperty] private string _statusText = "Waiting for first poll…";
    [ObservableProperty] private int    _activeCount;
    [ObservableProperty] private int    _disconnectedCount;

    /// <summary>Максимум одночасних активних сесій, зафіксований СЬОГОДНІ. Скидається при зміні календарної доби.</summary>
    [ObservableProperty] private int _dailyPeakSessions;

    /// <summary>Хто/звідки/коли востаннє відключився — для Overview-картки "Останній вихід" коли зараз 0 активних.</summary>
    [ObservableProperty] private string?         _lastLogoutUsername;
    [ObservableProperty] private string?         _lastLogoutServer;
    [ObservableProperty] private DateTimeOffset? _lastLogoutAt;

    /// <summary>
    /// true — RDP-моніторинг вимкнено в Settings. XAML повинен сховати
    /// таблицю сесій і показати заглушку "Моніторинг вимкнено" (edge-case #3).
    /// </summary>
    [ObservableProperty] private bool _isMonitoringDisabled;

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

    /// <summary>
    /// Edge-case #3: синхронізує IsMonitoringDisabled на основі перемиканняв Settings.
    /// Спрацьовує двічі: на реальне перемикання користувачем та один раз на
    /// холодному старті (SettingsViewModel конструктор).
    /// </summary>
    public void Receive(MonitoringToggledMessage message)
    {
        if (message.Service != MonitoredService.Rdp) return;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            IsMonitoringDisabled = !message.Value;

            if (!message.Value)
            {
                // Моніторинг вимкнено — чистимо застарілі дані, щоб під
                // заглушкою не лишались застарілі сесії.
                Sessions.Clear();
                ServerStatuses.Clear();
                ActiveCount       = 0;
                DisconnectedCount = 0;
                StatusText        = "RDP моніторинг вимкнено в Settings.";
            }
            else
            {
                StatusText = "Waiting for first poll…";
            }
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
                    ErrorMessage: "Credentials видалено",
                    GlobalDailyPeak: DailyPeakSessions,
                    LastLogoutUsername: LastLogoutUsername,
                    LastLogoutServer: LastLogoutServer,
                    LastLogoutAt: LastLogoutAt));

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
            if (_credentialsCleared)
                return;

            // Захист від запізнілих повідомлень в черзі диспетчера,
            // які могли надійти тільки що після вимкнення (edge-case #3).
            if (IsMonitoringDisabled)
                return;

            // Запам'ятовуємо виділену сесію по ключу (ServerIp+SessionId),
            // щоб відновити виділення після пересворення рядків нижче —
            // об'єкти RdpSessionRowViewModel readonly, тому "diff на місці"
            // тут не робимо, лише зберігаємо/відновлюємо SelectedSession.
            var selectedKey = SelectedSession is { } sel
                ? (sel.ServerIp, sel.SessionId)
                : ((string, string)?)null;

            var toRemove = Sessions
                .Where(s => s.ServerIp == payload.ServerIp)
                .ToList();

            foreach (var row in toRemove)
                Sessions.Remove(row);

            foreach (var session in payload.Sessions)
                Sessions.Add(new RdpSessionRowViewModel(session));
            
            var previousIds = toRemove.Select(s => s.SessionId).ToHashSet();
            var currentIds  = payload.Sessions.Select(s => s.SessionId).ToHashSet();

            foreach (var loggedOutId in previousIds.Except(currentIds))
            {
                var loggedOutRow = toRemove.First(s => s.SessionId == loggedOutId);
                LastLogoutUsername = loggedOutRow.Username;
                LastLogoutServer   = loggedOutRow.ServerName;
                LastLogoutAt       = DateTimeOffset.Now;
            }

            if (selectedKey is { } key)
            {
                SelectedSession = Sessions.FirstOrDefault(
                    s => s.ServerIp == key.Item1 && s.SessionId == key.Item2);
            }

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
            var errorServers = ServerStatuses
                .Where(s => s.HasError)
                .Select(s => s.ServerName)
                .ToList();

            StatusText = errorServers.Any()
                ? $"Errors on: {string.Join(", ", errorServers)} — " +
                  $"{ActiveCount} active, {DisconnectedCount} disconnected"
                : $"{ActiveCount} active, {DisconnectedCount} disconnected" +
                  $" — {total} total session(s) — " +
                  $"last updated {DateTime.Now:HH:mm:ss}";
        });
    }
}