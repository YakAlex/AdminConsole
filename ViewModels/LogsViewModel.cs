using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
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
    private const int MaxEntries = 1000;

    /// <summary>
    /// Скільки рядків читати з хвоста найновішого лог-файлу при старті.
    /// Дорівнює MaxEntries — більше однаково буде обрізано тримом нижче.
    /// </summary>
    private const int MaxHistoryLines = 1000;
    
    /// <summary>
    /// Скільки останніх app-*.log файлів максимум переглядати при доборі
    /// історії, якщо найновішого файлу не вистачає до MaxHistoryLines.
    /// Запобіжник, щоб при щойно створеному найновішому файлі (0-1 рядок)
    /// не читати весь архів логів за раз.
    /// </summary>
    private const int MaxHistoryFiles = 7;
    
    private static readonly string LogDirectory =
        System.IO.Path.Combine(AdminConsole.Utils.AppPaths.BaseDirectory, "logs");

    // Формат рядка фіксований — AppLogEntry.Formatted:
    // [yyyy-MM-dd HH:mm:ss] [Severity] [Source] Message
    // Повідомлення вже гарантовано однорядкове (санітизація \r\n при записі),
    // тому парсинг рядок-за-рядком безпечний — жоден запис не розірветься.
    private static readonly Regex LogLineRegex = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s*\[(?<sev>[A-Za-z]+)\s*\]\s*\[(?<source>[^\]]*)\]\s(?<msg>.*)$",
        RegexOptions.Compiled);

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

    /// <summary>
    /// Підрядок пошуку над DataGrid. Порожній/пробільний — всі записи видимі.
    /// Фільтрує в пам'яті (LogEntriesView, тільки поточна сесія, до 500 записів) —
    /// для пошуку по всій історії (інші дні, після рестарту) потрібно відкрити
    /// папку логів (кнопка "Open Folder") — там текстові файли app-YYYY-MM-DD.log,
    /// які пише FileLoggerService — вони вже персистентні та не обмежені 500 записами.
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    public LogsViewModel(IMessenger messenger)
    {
        _messenger = messenger;
        LogEntriesView.Source = _logEntries;
        LogEntriesView.SortDescriptions.Add(
            new System.ComponentModel.SortDescription(
                nameof(LogEntryViewModel.TimeFull),
                System.ComponentModel.ListSortDirection.Descending));
        LogEntriesView.Filter += OnFilter;

        messenger.RegisterAll(this);

        _ = LoadHistoryAsync();
    }

    /// <summary>
    /// Підвантажує хвіст найновішого лог-файлу (app-*.log) при старті програми —
    /// щоб після рестарту записи з попередньої сесії одразу були видні й доступні
    /// для пошуку в цій вкладці, а не тільки в текстовому файлі на диску.
    /// </summary>
    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await Task.Run(() => ParseTailLines(MaxHistoryLines));

            if (history.Count == 0) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // history вже у хронологічному порядку (старі → нові).
                // Вставляємо НА ПОЧАТОК — якщо якісь live-події вже встигли
                // прилетіти через Receive() поки читався файл, вони новіші
                // за будь-який запис з попередньої сесії і мають лишитись у кінці.
                // Так _logEntries лишається природньо хронологічним, а існуючий
                // RemoveAt(0) в Receive() продовжує видаляти саме найстаріше.
                for (int i = 0; i < history.Count; i++)
                    _logEntries.Insert(i, history[i]);

                while (_logEntries.Count > MaxEntries)
                    _logEntries.RemoveAt(0);

                if (_logEntries.Count > 0)
                    StatusText = $"{_logEntries.Count} entries — last: {_logEntries[^1].TimeShort}";
            });
        }
        catch (Exception ex)
        {
            _messenger.Send(AppLogEntryMessage.Warning("Logs",
                $"Не вдалося завантажити історію логів: {ex.Message}"));
        }
    }

    private static List<string> GetLogFilesDescending()
    {
        if (!System.IO.Directory.Exists(LogDirectory)) return [];

        return System.IO.Directory
            .EnumerateFiles(LogDirectory, "app-*.log")
            .OrderByDescending(f => f, StringComparer.Ordinal) // найновіший файл першим
            .Take(MaxHistoryFiles)
            .ToList();
    }

    /// <summary>
    /// Добирає до maxLines останніх рядків, починаючи з найновішого файлу і,
    /// якщо в ньому рядків не вистачає, підхоплюючи попередні (старіші) файли.
    /// Результат повертається у хронологічному порядку (старі → нові),
    /// щоб не ламати логіку вставки в LoadHistoryAsync.
    /// </summary>
    private static List<LogEntryViewModel> ParseTailLines(int maxLines)
    {
        var files = GetLogFilesDescending(); // newest → oldest
        var remaining = maxLines;

        // buffers[0] — рядки з найновішого залученого файлу, buffers[^1] — з найстарішого
        var buffers = new List<List<string>>();

        foreach (var file in files)
        {
            if (remaining <= 0) break;

            var lines = System.IO.File.ReadLines(file).TakeLast(remaining).ToList();
            if (lines.Count == 0) continue;

            buffers.Add(lines);
            remaining -= lines.Count;
        }

        // Розвертаємо порядок файлів (найстаріший залучений файл — перший),
        // усередині кожного файлу рядки вже йдуть старі → нові.
        buffers.Reverse();

        var result = new List<LogEntryViewModel>(maxLines);
        foreach (var buffer in buffers)
        foreach (var line in buffer)
            if (TryParseLine(line, out var entry))
                result.Add(new LogEntryViewModel(entry));

        return result;
    }

    private static bool TryParseLine(string line, out AppLogEntry entry)
    {
        entry = null!;

        var m = LogLineRegex.Match(line);
        if (!m.Success) return false;

        if (!DateTime.TryParseExact(
                m.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime))
            return false;
        
        if (!Enum.TryParse<LogSeverity>(m.Groups["sev"].Value, ignoreCase: true, out var severity))
            severity = LogSeverity.Info;

        var timestamp = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));

        entry = new AppLogEntry(severity, m.Groups["source"].Value, m.Groups["msg"].Value, timestamp);
        return true;
    }

    /// <summary>
    /// Шукає підрядком в Source або Message (case-insensitive, підрядок-входження).
    /// Наприклад, ввівши частину коментаря або ім'я сервера — знайдеш запис про Maintenance
    /// серед сотень інших записів без ручного скролу.
    /// </summary>
    private void OnFilter(object? sender, System.Windows.Data.FilterEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            e.Accepted = true;
            return;
        }

        if (e.Item is not LogEntryViewModel entry)
        {
            e.Accepted = false;
            return;
        }

        e.Accepted =
            entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            entry.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value) => LogEntriesView.View.Refresh();

    public void Receive(AppLogEntryMessage message)
    {
        var vm = new LogEntryViewModel(message.Value);

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            // Add() = O(1), не зсуває масив і не перебудовує всі індекси у DataGrid.
            // Сортування від нових до старих — через CollectionViewSource.SortDescriptions.
            _logEntries.Add(vm);

            // Видаляємо всі зайві — не тільки один, бо при burst
            // кілька InvokeAsync можуть одночасно побачити Count > MaxEntries
            while (_logEntries.Count > MaxEntries)
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
            string fullPath = System.IO.Path.Combine(AdminConsole.Utils.AppPaths.BaseDirectory, "logs");
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