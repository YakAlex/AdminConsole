using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Windows;

namespace AdminConsole.ViewModels;

/// <summary>
/// Drives the Resource Monitor view.
/// Receives ResourceSnapshotUpdatedMessage and EventLogUpdatedMessage
/// from their respective background services.
/// All ObservableCollection mutations are dispatched to the UI thread.
/// </summary>
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
    [ObservableProperty] private string _cpuLabel  = "— %";
    [ObservableProperty] private string _ramLabel  = "— / — GB";
    [ObservableProperty] private string _lastUpdated = "—";

    // ── CPU colour (green → amber → red by threshold) ────────────────────────
    [ObservableProperty] private string _cpuBarColor = "#FF4CAF50";
    [ObservableProperty] private string _ramBarColor = "#FF4CAF50";

    // ── Event log bindings ───────────────────────────────────────────────────

    public ObservableCollection<EventLogEntryViewModel> EventEntries { get; } = [];

    [ObservableProperty] private EventLogEntryViewModel? _selectedEntry;
    [ObservableProperty] private string _selectedEntryFullMessage = string.Empty;
    [ObservableProperty] private string _eventLogStatus = "Waiting for first fetch…";

    // -------------------------------------------------------------------------

    public ResourceMonitorViewModel(IMessenger messenger)
    {
        messenger.RegisterAll(this);
    }

    // ── IRecipient<ResourceSnapshotUpdatedMessage> ───────────────────────────

    public void Receive(ResourceSnapshotUpdatedMessage message)
    {
        var s = message.Value;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            CpuPercent   = s.CpuPercent;
            RamPercent   = s.RamPercent;
            RamUsedGb    = s.RamUsedGb;
            RamTotalGb   = s.RamTotalGb;
            CpuLabel     = $"{s.CpuPercent:F1} %";
            RamLabel     = $"{s.RamUsedGb:F1} GB  /  {s.RamTotalGb:F1} GB";
            LastUpdated  = s.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            CpuBarColor  = PickColor(s.CpuPercent);
            RamBarColor  = PickColor(s.RamPercent);
        });
    }

    // ── IRecipient<EventLogUpdatedMessage> ───────────────────────────────────

    public void Receive(EventLogUpdatedMessage message)
    {
        var entries = message.Value;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            EventEntries.Clear();
            foreach (var e in entries)
                EventEntries.Add(new EventLogEntryViewModel(e));

            EventLogStatus = EventEntries.Count == 0
                ? "No Error/Critical events found."
                : $"{EventEntries.Count} recent error(s) — last fetched {DateTime.Now:HH:mm:ss}";
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    partial void OnSelectedEntryChanged(EventLogEntryViewModel? value)
    {
        SelectedEntryFullMessage = value?.FullMessage ?? string.Empty;
    }

    private static string PickColor(double percent) => percent switch
    {
        >= 90 => "#FFF44336",   // Red
        >= 70 => "#FFFFC107",   // Amber
        _     => "#FF4CAF50"    // Green
    };
}