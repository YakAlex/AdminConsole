using AdminConsole.Core.Models;
using AdminConsole.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Messages;
using CommunityToolkit.Mvvm.Messaging;
using System.Windows;

namespace AdminConsole.ViewModels;

/// <summary>
/// Per-row observable wrapper.
/// Now owns the three Quick Action commands so each DataGrid row
/// is entirely self-contained and the View needs no code-behind
/// to wire up button clicks.
/// </summary>
public sealed partial class PingResultViewModel : ObservableObject, IRecipient<MaintenanceChangedMessage>
{
    // ── Identity (never changes) ──────────────────────────────────────────────
    public string Name  { get; }
    public string IP    { get; }
    public string Group { get; }
    
    public ServerType Type  { get; }
    
    // ── Computed visibility helpers (використовуються у XAML DataTrigger) ────
    public bool IsWindows => Type == ServerType.Windows;
    public bool IsLinux   => Type == ServerType.Linux;
    public bool IsDevice  => Type is ServerType.Network;

    // ── Live state ────────────────────────────────────────────────────────────
    [ObservableProperty] private PingStatus _status = PingStatus.Unknown;
    [ObservableProperty] private long?      _latencyMs;
    [ObservableProperty] private string     _lastChecked    = "—";
    [ObservableProperty] private string     _latencyDisplay = "—";
    [ObservableProperty] private bool       _isActionBusy;

    // ── Maintenance ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isUnderMaintenance;
    [ObservableProperty] private string _maintenanceTooltip = string.Empty;

    // ── Injected services ─────────────────────────────────────────────────────
    private readonly RemoteManagementService _remoteMgmt;
    private readonly IDialogService          _dialog;
    private readonly MaintenanceService      _maintenance;

    public PingResultViewModel(
        string name,
        string ip,
        string group,
        ServerType type,
        RemoteManagementService remoteMgmt,
        IDialogService dialog,
        MaintenanceService maintenance,
        IMessenger messenger)
    {
        Name         = name;
        IP           = ip;
        Group        = group;
        Type         = type;
        _remoteMgmt  = remoteMgmt;
        _dialog      = dialog;
        _maintenance = maintenance;
        RefreshMaintenanceState();

        messenger.Register(this);
    }

    // ── IRecipient<MaintenanceChangedMessage> ───────────────────────────────

    public void Receive(MaintenanceChangedMessage message)
    {
        bool affectsMe = message.Window.ServerIp == IP ||
            (message.Window.TargetGroup?.Equals(Group, StringComparison.OrdinalIgnoreCase) ?? false);

        if (!affectsMe) return;

        Application.Current?.Dispatcher?.InvokeAsync(RefreshMaintenanceState);
    }

    private void RefreshMaintenanceState()
    {
        var window = _maintenance.GetActiveWindow(IP, Group);
        IsUnderMaintenance = window is not null;
        MaintenanceTooltip = window is not null
            ? $"{(string.IsNullOrWhiteSpace(window.Reason) ? "Maintenance" : window.Reason)} — " +
              (window.To is { } to
                  ? $"до {to.ToLocalTime():dd.MM HH:mm}"
                  : "без обмеження часу")
            : string.Empty;
    }

    // ── Maintenance команда ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleMaintenance()
    {
        if (IsUnderMaintenance)
        {
            var activeWindow = _maintenance.GetActiveWindow(IP, Group);
            if (activeWindow is not null)
                _maintenance.EndMaintenanceEarly(activeWindow.Key);
            return;
        }

        IsActionBusy = true;
        try
        {
            var choice = await _dialog.ShowMaintenanceDurationAsync(Name, IP);
            if (choice is null) return; // користувач скасував діалог

            var now = DateTimeOffset.Now;

            // Порожній/пробільний коментар — свідомо підставляємо дефолтну
            // причину замість розмитого "без причини" — адмін, який просто клацнув кнопку
            // тривалості не вводячи коментар, отримає однаковий, очікуваний текст у логах/тултіпах
            // замість порожнього рядка.
            var reason = string.IsNullOrWhiteSpace(choice.Comment)
                ? "Планове обслуговування"
                : choice.Comment.Trim();

            _maintenance.StartMaintenance(new MaintenanceWindow
            {
                ServerIp    = IP,
                DisplayName = Name,
                From        = now,
                To          = choice.Duration is { } d ? now + d : null,
                Reason      = reason
            });
        }
        finally
        {
            IsActionBusy = false;
        }
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
    private void OpenSsh()
        => _remoteMgmt.OpenSsh(IP, Name);

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