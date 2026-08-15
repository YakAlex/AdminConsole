using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace AdminConsole.ViewModels;

/// <summary>
/// Один рядок вкладки Backups — сервер + Full/Diff. Мутабельний
/// ObservableObject з Update(state) "на місці" — той самий підхід, що
/// ServerPollStatusViewModel (RDP): дозволяє зберігати SelectedRow
/// між циклами опитування замість пересворення рядків щоразу.
/// </summary>
public sealed partial class BackupRowViewModel : ObservableObject
{
    public string     Name { get; }
    public string     Host       { get; }
    public BackupKind Kind       { get; }
    public string     Key        => $"{Name}|{Kind}";
    public string     KindLabel  => Kind == BackupKind.Full ? "Full" : "Diff";

    [ObservableProperty] private BackupOutcome _outcome = BackupOutcome.Unknown;
    [ObservableProperty] private string        _outcomeLabel         = "UNKNOWN";
    [ObservableProperty] private string        _lastConfirmedDisplay = "—";
    [ObservableProperty] private string        _lastSizeDisplay      = "—";
    [ObservableProperty] private string        _samplesDisplay       = "0";
    [ObservableProperty] private string?       _lastError;
    [ObservableProperty] private bool          _hasError;

    [ObservableProperty] private SolidColorBrush _badgeBackground = Freeze("#CC607D8B");
    [ObservableProperty] private SolidColorBrush _badgeForeground = Freeze("#FFFFFFFF");
    [ObservableProperty] private SolidColorBrush _rowForeground   = Freeze("#FFCFD8DC");

    public BackupRowViewModel(BackupCheckState state)
    {
        Name = state.Name;
        Host = state.Host;
        Kind = state.Kind;
        Update(state);
    }

    /// <summary>Оновлює всі відображувані поля з нового знімку стану. Викликається на кожен BackupStatusUpdatedMessage.</summary>
    public void Update(BackupCheckState state)
    {
        Outcome   = state.Outcome;
        LastError = state.LastError;
        HasError  = state.LastError is not null;

        LastConfirmedDisplay = state.LastConfirmedAt is { } at
            ? at.ToLocalTime().ToString("MM-dd HH:mm")
            : "—";

        var lastSample = state.History.Count > 0 ? state.History[^1] : null;
        LastSizeDisplay = lastSample is not null
            ? FormatSize(lastSample.SizeBytes)
            : "—";

        SamplesDisplay = state.History.Count.ToString();

        (OutcomeLabel, BadgeBackground, BadgeForeground, RowForeground) = state.Outcome switch
        {
            BackupOutcome.Ok          => ("OK",           Freeze("#CC4CAF50"), Freeze("#FFFFFFFF"), Freeze("#FFC8E6C9")),
            BackupOutcome.SizeWarning => ("SIZE WARNING", Freeze("#CCFFD600"), Freeze("#FF000000"), Freeze("#FFFFF9C4")),
            BackupOutcome.Stale       => ("STALE",        Freeze("#CCFF9800"), Freeze("#FF000000"), Freeze("#FFFFE0B2")),
            BackupOutcome.Missing     => ("MISSING",      Freeze("#CCE53935"), Freeze("#FFFFFFFF"), Freeze("#FFEF9A9A")),
            _                         => ("UNKNOWN",      Freeze("#CC607D8B"), Freeze("#FFFFFFFF"), Freeze("#FFCFD8DC"))
        };
    }

    private static string FormatSize(long bytes)
    {
        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
        if (gb >= 1) return $"{gb:0.##} GB";

        double mb = bytes / 1024.0 / 1024.0;
        return $"{mb:0.#} MB";
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}