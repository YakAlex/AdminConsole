using AdminConsole.Core.Messages;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdminConsole.ViewModels;

/// <summary>
/// One row in the "server status" header strip — shows last poll time
/// and whether quser succeeded or errored per server.
/// </summary>
public sealed partial class ServerPollStatusViewModel : ObservableObject
{
    public string ServerName { get; }
    public string ServerIp   { get; }

    [ObservableProperty] private int    _sessionCount;
    [ObservableProperty] private string _lastPolled  = "—";
    [ObservableProperty] private string _pollStatus  = "Pending";
    [ObservableProperty] private string _statusColor = "#FF607D8B";
    [ObservableProperty] private bool   _hasError;

    public ServerPollStatusViewModel(string name, string ip)
    {
        ServerName = name;
        ServerIp   = ip;
    }

    public void Update(RdpSessionsPayload payload)
    {
        SessionCount = payload.Sessions.Count;
        LastPolled   = DateTime.Now.ToString("HH:mm:ss");

        if (payload.ErrorMessage is not null)
        {
            PollStatus  = $"Error: {payload.ErrorMessage}";
            StatusColor = "#FFF44336";
            HasError    = true;
        }
        else
        {
            PollStatus  = $"{payload.Sessions.Count} session(s)";
            StatusColor = "#FF4CAF50";
            HasError    = false;
        }
    }
}