namespace AdminConsole.Core.Models;

public enum RdpSessionState
{
    Active,
    Disconnected,
    Idle,
    Unknown
}

/// <summary>
/// Immutable snapshot of a single session returned by quser.
/// </summary>
public sealed record RdpSessionInfo(
    string          Username,
    string          SessionName,
    string          SessionId,
    RdpSessionState State,
    string          IdleTime,
    string          LogonTime,
    string          ServerName,
    string          ServerIp
);