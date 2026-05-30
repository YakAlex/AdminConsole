using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AdminConsole.ViewModels;

/// <summary>
/// Placeholder — fully implemented in Phase 7.
/// </summary>
public sealed partial class ZabbixViewModel : ObservableObject
{
    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    private string _connectionStatus = "Not connected.";
}