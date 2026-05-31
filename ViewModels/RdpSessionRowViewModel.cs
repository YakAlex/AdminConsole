using AdminConsole.Core.Models;

namespace AdminConsole.ViewModels;

/// <summary>Read-only row wrapper for a single quser session.</summary>
public sealed class RdpSessionRowViewModel
{
    public string          Username    { get; }
    public string          SessionName { get; }
    public string          SessionId   { get; }
    public RdpSessionState State       { get; }
    public string          StateLabel  { get; }
    public string          IdleTime    { get; }
    public string          LogonTime   { get; }
    public string          ServerName  { get; }
    public string          ServerIp    { get; }

    // Pre-computed colours — same frozen-brush pattern used in other phases.
    public string StateDotColor { get; }
    public string StateTextColor { get; }

    public RdpSessionRowViewModel(RdpSessionInfo info)
    {
        Username    = info.Username;
        SessionName = string.IsNullOrWhiteSpace(info.SessionName) ? "—" : info.SessionName;
        SessionId   = info.SessionId;
        State       = info.State;
        IdleTime    = info.IdleTime;
        LogonTime   = info.LogonTime;
        ServerName  = info.ServerName;
        ServerIp    = info.ServerIp;

        (StateLabel, StateDotColor, StateTextColor) = info.State switch
        {
            RdpSessionState.Active       => ("Active",       "#FF4CAF50", "#FF4CAF50"),
            RdpSessionState.Disconnected => ("Disconnected", "#FFFFC107", "#FFFFC107"),
            _                            => ("Unknown",      "#FF607D8B", "#FF90A4AE")
        };
    }
}