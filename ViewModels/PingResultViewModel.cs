using AdminConsole.Core.Models;
using AdminConsole.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.ViewModels;

/// <summary>
/// Per-row observable wrapper.
/// Now owns the three Quick Action commands so each DataGrid row
/// is entirely self-contained and the View needs no code-behind
/// to wire up button clicks.
/// </summary>
public sealed partial class PingResultViewModel : ObservableObject
{
    // ── Identity (never changes) ──────────────────────────────────────────────
    public string Name  { get; }
    public string IP    { get; }
    public string Group { get; }

    // ── Live state ────────────────────────────────────────────────────────────
    [ObservableProperty] private PingStatus _status = PingStatus.Unknown;
    [ObservableProperty] private long?      _latencyMs;
    [ObservableProperty] private string     _lastChecked    = "—";
    [ObservableProperty] private string     _latencyDisplay = "—";
    [ObservableProperty] private bool       _isActionBusy;

    // ── Injected services ─────────────────────────────────────────────────────
    private readonly RemoteManagementService _remoteMgmt;
    private readonly IDialogService          _dialog;

    public PingResultViewModel(
        string name,
        string ip,
        string group,
        RemoteManagementService remoteMgmt,
        IDialogService dialog)
    {
        Name        = name;
        IP          = ip;
        Group       = group;
        _remoteMgmt = remoteMgmt;
        _dialog     = dialog;
    }

    // ── State update ──────────────────────────────────────────────────────────

    public void ApplyResult(PingResult result)
    {
        Status         = result.Status;
        LatencyMs      = result.LatencyMs;
        LastChecked    = result.LastChecked.ToLocalTime().ToString("HH:mm:ss");
        LatencyDisplay = result.LatencyMs.HasValue
            ? $"{result.LatencyMs} ms"
            : result.Status == PingStatus.Checking ? "…" : "Timeout";
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void PingContinuous()
        => _remoteMgmt.OpenContinuousPing(IP, Name);

    [RelayCommand]
    private async Task OpenRdp()
    {
        IsActionBusy = true;
        try   { await _remoteMgmt.OpenRdpAsync(IP, Name); }
        finally { IsActionBusy = false; }
    }

    [RelayCommand]
    private async Task RemoteRestart()
    {
        bool confirmed = await _dialog.ShowConfirmationAsync(
            title:        $"Restart {Name}?",
            body:         $"This will immediately restart {Name} ({IP}).\n\n" +
                          "All active user sessions will be terminated without warning.",
            confirmLabel: "Restart Now");

        if (!confirmed) return;

        IsActionBusy = true;
        try   { await _remoteMgmt.RemoteRestartAsync(IP, Name); }
        finally { IsActionBusy = false; }
    }

    [RelayCommand]
    private async Task RemoteShutdown()
    {
        bool confirmed = await _dialog.ShowConfirmationAsync(
            title:        $"Shut down {Name}?",
            body:         $"This will immediately power off {Name} ({IP}).\n\n" +
                          "All active user sessions will be terminated without warning. " +
                          "The machine will NOT restart automatically.",
            confirmLabel: "Shut Down");

        if (!confirmed) return;

        IsActionBusy = true;
        try   { await _remoteMgmt.RemoteShutdownAsync(IP, Name); }
        finally { IsActionBusy = false; }
    }
}