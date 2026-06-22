using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;

namespace AdminConsole.ViewModels;

/// <summary>
/// Drives the Logs tab.
/// Receives AppLogEntryMessage, dispatches to the UI thread,
/// and appends to the observable collection.
/// Capped at MaxEntries to prevent unbounded memory growth during
/// long-running sessions.
/// </summary>
public sealed partial class LogsViewModel
    : ObservableObject, IRecipient<AppLogEntryMessage>
{
    private const int MaxEntries = 500;
    private const int CleanupThreshold = MaxEntries + 50;
    // ── Observable state ─────────────────────────────────────────────────────

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = [];

    [ObservableProperty]
    private string _statusText = "Listening for log events…";

    [ObservableProperty]
    private LogEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private bool _autoScroll = true;

    // ── Constructor ──────────────────────────────────────────────────────────

    public LogsViewModel(IMessenger messenger)
    {
        messenger.RegisterAll(this);
    }

    // ── IRecipient<AppLogEntryMessage> ───────────────────────────────────────

    public void Receive(AppLogEntryMessage message)
    {
        var vm = new LogEntryViewModel(message.Value);

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            // Нові записи зверху — адміністратор бачить останні події
            // без скролу. Insert(0) = O(n) по пам'яті, але так само
            // як Add + RemoveAt(0), тільки в правильному порядку.
            LogEntries.Insert(0, vm);

            // Пакетне видалення зі КІНЦЯ — O(1) на відміну від RemoveAt(0).
            // Кожен RemoveAt(Count-1) не зсуває масив.
            // Спрацьовує раз на ~50 нових записів завдяки порогу.
            if (LogEntries.Count > CleanupThreshold)
            {
                int toRemove = LogEntries.Count - MaxEntries;
                for (int i = 0; i < toRemove; i++)
                    LogEntries.RemoveAt(LogEntries.Count - 1);
            }

            StatusText = $"{LogEntries.Count} entries — last: {vm.TimeShort}";
        });
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        StatusText = "Log cleared.";
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            string fullPath = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(fullPath);
            System.Diagnostics.Process.Start("explorer.exe", fullPath);
        }
        catch { /* silently ignore if explorer fails */ }
    }
}