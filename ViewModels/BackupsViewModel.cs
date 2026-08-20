using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using AdminConsole.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;

namespace AdminConsole.ViewModels;

public sealed partial class BackupsViewModel
    : ObservableObject,
      IRecipient<BackupStatusUpdatedMessage>,
      IRecipient<MonitoringToggledMessage>
{
    public ObservableCollection<BackupRowViewModel> Rows { get; } = [];

    [ObservableProperty] private BackupRowViewModel? _selectedRow;
    [ObservableProperty] private int    _okCount;
    [ObservableProperty] private int    _warningCount;
    [ObservableProperty] private int    _badCount;      // Stale + Missing
    [ObservableProperty] private int    _unknownCount;
    [ObservableProperty] private string _lastUpdated = "—";

    /// <summary>Сирий (не форматований) час LastUpdated — для Overview-картки "наступна перевірка через...".</summary>
    [ObservableProperty] private DateTimeOffset? _lastUpdatedAt;

    /// <summary>
    /// true — Backup-моніторинг вимкнено в Settings. XAML ховає таблицю
    /// рядків і показує заглушку "Backup-моніторинг вимкнено".
    /// </summary>
    [ObservableProperty] private bool _isMonitoringDisabled;

    public BackupsViewModel(IMessenger messenger, BackupMonitorService backupMonitor)
    {
        messenger.RegisterAll(this);

        // Холодний старт — не чекаємо перший цикл BackupMonitorService,
        // одразу показуємо те, що пережило рестарт (logs/backups.json).
        ApplySnapshot(backupMonitor.GetSnapshot());
    }

    /// <summary>
    /// Синхронізує IsMonitoringDisabled. Спрацьовує на реальне перемикання
    /// користувачем та один раз на холодному старті (SettingsViewModel конструктор).
    /// </summary>
    public void Receive(MonitoringToggledMessage message)
    {
        if (message.Service != MonitoredService.Backups) return;

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            IsMonitoringDisabled = !message.Value;

            if (!message.Value)
            {
                Rows.Clear();
                SelectedRow  = null;
                OkCount      = 0;
                WarningCount = 0;
                BadCount     = 0;
                UnknownCount = 0;
                LastUpdated  = "—";
            }
        });
    }

    public void Receive(BackupStatusUpdatedMessage message) =>
        Application.Current?.Dispatcher?.InvokeAsync(() => ApplySnapshot(message.Value));

    private void ApplySnapshot(IReadOnlyList<BackupCheckState> states)
    {
        // Захист від запізнілого знімку, що надійшов у чергу диспетчера
        // одразу після вимкнення моніторингу (той самий принцип, що ZabbixViewModel).
        if (IsMonitoringDisabled)
            return;

        var selectedKey = SelectedRow?.Key;

        var incomingKeys = states.Select(s => $"{s.Name}|{s.Kind}").ToHashSet();
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!incomingKeys.Contains(Rows[i].Key))
                Rows.RemoveAt(i);

        var existing = Rows.ToDictionary(r => r.Key);
        foreach (var state in states)
        {
            var key = $"{state.Name}|{state.Kind}";
            if (existing.TryGetValue(key, out var row))
                row.Update(state);
            else
                Rows.Add(new BackupRowViewModel(state));
        }

        if (selectedKey is not null)
            SelectedRow = Rows.FirstOrDefault(r => r.Key == selectedKey);

        OkCount      = Rows.Count(r => r.Outcome == BackupOutcome.Ok);
        WarningCount = Rows.Count(r => r.Outcome == BackupOutcome.SizeWarning);
        BadCount     = Rows.Count(r => r.Outcome is BackupOutcome.Stale or BackupOutcome.Missing);
        UnknownCount = Rows.Count(r => r.Outcome == BackupOutcome.Unknown);
        LastUpdatedAt = DateTimeOffset.Now;
        LastUpdated  = LastUpdatedAt.Value.ToString("HH:mm:ss");
    }
}