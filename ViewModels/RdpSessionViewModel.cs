using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AdminConsole.ViewModels;

/// <summary>
/// Placeholder — fully implemented in Phase 6.
/// </summary>
public sealed partial class RdpSessionViewModel : ObservableObject
{
    public ObservableCollection<string> Sessions { get; } = [];

    [ObservableProperty]
    private string _statusText = "Select a terminal server to query sessions.";
}