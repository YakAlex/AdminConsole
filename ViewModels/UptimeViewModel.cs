using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using AdminConsole.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;

namespace AdminConsole.ViewModels;

public sealed partial class UptimeViewModel
    : ObservableObject, IRecipient<UptimeUpdatedMessage>, IDisposable
{
    private readonly UptimeTrackerService  _tracker;
    private readonly System.Threading.Timer _refreshTimer;

    // ── Колекція і групування ────────────────────────────────────────────────

    // Всі записи (без фільтрів) — прив'язуємо CollectionViewSource до неї
    private readonly ObservableCollection<DowntimeRecord> _allRecords = [];

    // CollectionViewSource — фільтрація і сортування без дублювання даних
    public CollectionViewSource RecordsView { get; } = new();

    // ── Зведена статистика (верхній рядок) ──────────────────────────────────

    [ObservableProperty] private int    _totalIncidents;
    [ObservableProperty] private int    _activeIncidents;
    [ObservableProperty] private string _longestDowntime = "—";
    [ObservableProperty] private string _todayIncidents  = "0";

    // ── Фільтри ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private string _filterServer = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private string _filterGroup = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private string _filterStatus = "All";      // "All" | "Active" | "Resolved"

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private DateTime? _filterDateFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private DateTime? _filterDateTo;

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(FilterServer) ||
        FilterGroup    != "All"                  ||
        FilterStatus   != "All"                  ||
        FilterDateFrom.HasValue                  ||
        FilterDateTo.HasValue;

    // Списки для ComboBox-ів (динамічно з даних)
    public ObservableCollection<string> AvailableGroups  { get; } = ["All"];
    public ObservableCollection<string> AvailableServers { get; } = ["All"];
    public List<string>                 StatusOptions    { get; } =
        ["All", "Active", "Resolved"];

    // ── Обраний запис ────────────────────────────────────────────────────────

    [ObservableProperty] private DowntimeRecord? _selectedRecord;

    // ── Constructor ──────────────────────────────────────────────────────────

    public UptimeViewModel(IMessenger messenger, UptimeTrackerService tracker)
    {
        _tracker = tracker;

        RecordsView.Source = _allRecords;
        RecordsView.Filter += OnFilter;
        RecordsView.SortDescriptions.Add(
            new System.ComponentModel.SortDescription(
                nameof(DowntimeRecord.FellAt),
                System.ComponentModel.ListSortDirection.Descending));

        // Підписуємось ПІСЛЯ налаштування CollectionViewSource
        messenger.RegisterAll(this);

        // Завантажуємо початковий знімок напряму у колекцію
        var initial = _tracker.GetSnapshot();
        foreach (var r in initial)
            _allRecords.Add(r);
        UpdateSummary(initial);
        UpdateFilterLists(initial);

        // Запускаємо таймер оновлення тривалості активних інцидентів.
        // Спрацьовує кожні 10 секунд — тільки якщо є активні інциденти,
        // щоб не смикати UI даремно коли всі інциденти завершені.
        _refreshTimer = new System.Threading.Timer(
            _ => Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                if (ActiveIncidents > 0)
                    RecordsView.View?.Refresh();
            }),
            state:     null,
            dueTime:   TimeSpan.FromSeconds(10),
            period:    TimeSpan.FromSeconds(10));
    }

    // ── Реакція на зміни фільтрів ────────────────────────────────────────────

    partial void OnFilterServerChanged(string value)    => RefreshView();
    partial void OnFilterGroupChanged(string value)     => RefreshView();
    partial void OnFilterStatusChanged(string value)    => RefreshView();
    partial void OnFilterDateFromChanged(DateTime? value) => RefreshView();
    partial void OnFilterDateToChanged(DateTime? value)   => RefreshView();

    private void RefreshView()
        => Application.Current?.Dispatcher?.InvokeAsync(
            () => RecordsView.View?.Refresh());

    // ── IRecipient ────────────────────────────────────────────────────────────

    public void Receive(UptimeUpdatedMessage message)
        => Application.Current?.Dispatcher?.InvokeAsync(
            () => ApplySnapshot(message.Value));

    // ── Логіка оновлення ─────────────────────────────────────────────────────

    private void ApplySnapshot(IReadOnlyList<DowntimeRecord> records)
    {
        // Diff-оновлення: не скидаємо всю колекцію щоб не ламати виділення/скрол
        var incomingKeys = records
            .Select(r => (r.ServerIp, r.FellAt))
            .ToHashSet();

        for (int i = _allRecords.Count - 1; i >= 0; i--)
        {
            var key = (_allRecords[i].ServerIp, _allRecords[i].FellAt);
            if (!incomingKeys.Contains(key))
                _allRecords.RemoveAt(i);
        }

        foreach (var incoming in records)
        {
            var existing = _allRecords.FirstOrDefault(
                x => x.ServerIp == incoming.ServerIp &&
                     x.FellAt   == incoming.FellAt);

            if (existing is null)
            {
                _allRecords.Add(incoming);
            }
            else if (existing.RecoveredAt != incoming.RecoveredAt)
            {
                existing.RecoveredAt = incoming.RecoveredAt;
            }
        }

        UpdateSummary(records);
        UpdateFilterLists(records);
        RecordsView.View?.Refresh();
    }

    private void UpdateSummary(IReadOnlyList<DowntimeRecord> records)
    {
        var today = DateTimeOffset.Now.Date;

        TotalIncidents  = records.Count;
        ActiveIncidents = records.Count(r => !r.IsResolved);
        TodayIncidents  = records.Count(r => r.FellAt.Date == today).ToString();

        // Враховуємо всі інциденти — і завершені, і активні.
        // Активний інцидент може тривати довше за будь-який завершений.
        var longest = records
            .OrderByDescending(r => r.Duration)
            .FirstOrDefault();

        LongestDowntime = longest is not null
            ? longest.DurationDisplay
            : "—";
    }

    private void UpdateFilterLists(IReadOnlyList<DowntimeRecord> records)
    {
        var groups  = records.Select(r => r.ServerGroup).Distinct().OrderBy(g => g).ToList();
        var servers = records.Select(r => r.ServerName).Distinct().OrderBy(s => s).ToList();

        AvailableGroups.Clear();
        AvailableGroups.Add("All");
        foreach (var g in groups) AvailableGroups.Add(g);

        AvailableServers.Clear();
        AvailableServers.Add("All");
        foreach (var s in servers) AvailableServers.Add(s);
    }

    // ── CollectionViewSource фільтр ──────────────────────────────────────────

    private void OnFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not DowntimeRecord r)
        {
            e.Accepted = false;
            return;
        }

        // Фільтр по серверу
        if (!string.IsNullOrWhiteSpace(FilterServer) && FilterServer != "All")
        {
            if (!r.ServerName.Contains(FilterServer, StringComparison.OrdinalIgnoreCase))
            {
                e.Accepted = false;
                return;
            }
        }

        // Фільтр по групі
        if (!string.IsNullOrWhiteSpace(FilterGroup) && FilterGroup != "All")
        {
            if (!r.ServerGroup.Equals(FilterGroup, StringComparison.OrdinalIgnoreCase))
            {
                e.Accepted = false;
                return;
            }
        }

        // Фільтр по статусу
        e.Accepted = FilterStatus switch
        {
            "Active"   => !r.IsResolved,
            "Resolved" => r.IsResolved,
            _          => true
        };

        if (!e.Accepted) return;

        // Фільтр по даті (від)
        if (FilterDateFrom.HasValue &&
            r.FellAt.LocalDateTime.Date < FilterDateFrom.Value.Date)
        {
            e.Accepted = false;
            return;
        }

        // Фільтр по даті (до)
        if (FilterDateTo.HasValue &&
            r.FellAt.LocalDateTime.Date > FilterDateTo.Value.Date)
        {
            e.Accepted = false;
        }
    }

    // ── Команди ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClearFilters()
    {
        FilterServer   = string.Empty;
        FilterGroup    = "All";
        FilterStatus   = "All";
        FilterDateFrom = null;
        FilterDateTo   = null;
    }

    // ── IDisposable ──────────────────────────────────────────────────────────
    public void Dispose()
    {
        // Зупиняємо таймер щоб не смикати Dispatcher після закриття вікна.
        _refreshTimer.Dispose();
    }
}