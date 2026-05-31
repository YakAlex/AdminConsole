using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;

namespace AdminConsole.ViewModels;

public sealed partial class ResourceMonitorViewModel
    : ObservableObject,
      IRecipient<ResourceSnapshotUpdatedMessage>,
      IRecipient<EventLogUpdatedMessage>
{
    // ── CPU / RAM bindings ───────────────────────────────────────────────────
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _ramUsedGb;
    [ObservableProperty] private double _ramTotalGb;
    [ObservableProperty] private string _cpuLabel    = "— %";
    [ObservableProperty] private string _ramLabel    = "— / — GB";
    [ObservableProperty] private string _lastUpdated = "—";

    // Pre-computed frozen brushes — no converter needed in XAML,
    // no StringToColorConverter crash risk.
    [ObservableProperty] private SolidColorBrush _cpuBarBrush = GreenBrush;
    [ObservableProperty] private SolidColorBrush _ramBarBrush = GreenBrush;

    // ── Event log bindings ───────────────────────────────────────────────────
    public ObservableCollection<EventLogEntryViewModel> EventEntries { get; } = [];

    [ObservableProperty] private EventLogEntryViewModel? _selectedEntry;
    [ObservableProperty] private string _selectedEntryFullMessage = string.Empty;
    [ObservableProperty] private string _eventLogStatus = "Waiting for first fetch…";

    // ── Static frozen brushes ────────────────────────────────────────────────
    private static readonly SolidColorBrush GreenBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly SolidColorBrush AmberBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)));
    private static readonly SolidColorBrush RedBrush =
        Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)));

    private readonly Dispatcher _dispatcher;

    public ResourceMonitorViewModel(IMessenger messenger)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        messenger.RegisterAll(this);
    }

    // ── IRecipient<ResourceSnapshotUpdatedMessage> ───────────────────────────

    public void Receive(ResourceSnapshotUpdatedMessage message)
    {
        var s = message.Value;
        _dispatcher.InvokeAsync(() =>
        {
            CpuPercent   = s.CpuPercent;
            RamPercent   = s.RamPercent;
            RamUsedGb    = s.RamUsedGb;
            RamTotalGb   = s.RamTotalGb;
            CpuLabel     = $"{s.CpuPercent:F1} %";
            RamLabel     = $"{s.RamUsedGb:F1} GB  /  {s.RamTotalGb:F1} GB";
            LastUpdated  = s.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            CpuBarBrush  = PickBrush(s.CpuPercent);
            RamBarBrush  = PickBrush(s.RamPercent);
        });
    }

    // ── IRecipient<EventLogUpdatedMessage> ───────────────────────────────────

    public void Receive(EventLogUpdatedMessage message)
    {
        var entries = message.Value;
        _dispatcher.InvokeAsync(() =>
        {
            EventEntries.Clear();
            foreach (var e in entries)
                EventEntries.Add(new EventLogEntryViewModel(e));

            EventLogStatus = EventEntries.Count == 0
                ? "No Error/Critical events found."
                : $"{EventEntries.Count} recent error(s) — " +
                  $"last fetched {DateTime.Now:HH:mm:ss}";
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    partial void OnSelectedEntryChanged(EventLogEntryViewModel? value)
        => SelectedEntryFullMessage = value?.FullMessage ?? string.Empty;

    private static SolidColorBrush PickBrush(double percent) => percent switch
    {
        >= 90 => RedBrush,
        >= 70 => AmberBrush,
        _     => GreenBrush
    };

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}