using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
namespace AdminConsole.ViewModels;
using System.Collections.Specialized;
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

    private readonly ObservableCollection<LogEntryViewModel> _logEntries = [];
    private readonly IMessenger _messenger;
    public INotifyCollectionChanged LogEntries => _logEntries;

    // CollectionViewSource — сортує від нових до старих на рівні відображення.
    // Add() в кінець колекції = O(1) і WPF не перебудовує всі індекси.
    public CollectionViewSource LogEntriesView { get; } = new();
    
    [ObservableProperty]
    private string _statusText = "Listening for log events…";

    [ObservableProperty]
    private LogEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private bool _autoScroll = true;

    public LogsViewModel(IMessenger messenger)
    {
        _messenger = messenger;
        LogEntriesView.Source = _logEntries;
        LogEntriesView.SortDescriptions.Add(
            new System.ComponentModel.SortDescription(
                nameof(LogEntryViewModel.TimeFull),
                System.ComponentModel.ListSortDirection.Descending));

        messenger.RegisterAll(this);
    }

    public void Receive(AppLogEntryMessage message)
    {
        var vm = new LogEntryViewModel(message.Value);

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            // Add() = O(1), не зсуває масив і не перебудовує всі індекси у DataGrid.
            // Сортування від нових до старих — через CollectionViewSource.SortDescriptions.
            _logEntries.Add(vm);

            // Видаляємо найстаріші (кінець несортованої колекції) якщо перевищили ліміт.
            // RemoveAt(0) = O(n), але спрацьовує рідко (раз на MaxEntries записів).
            if (_logEntries.Count > MaxEntries)
                _logEntries.RemoveAt(0);

            StatusText = $"{_logEntries.Count} entries — last: {vm.TimeShort}";
        });
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClearLog()
    {
        _logEntries.Clear();
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
        catch (Exception ex)
        {
            // Надсилаємо у лог через messenger замість ILogger —
            // щоб не додавати нову залежність у VM.
            _messenger.Send(AppLogEntryMessage.Warning("Logs",
                $"Не вдалося відкрити папку логів: {ex.Message}"));
        }
    }
}