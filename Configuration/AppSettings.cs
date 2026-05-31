namespace AdminConsole.Configuration;

public sealed class MonitoringSettings
{
    public const string SectionName = "Monitoring";

    public int    PingIntervalSeconds              { get; init; } = 30;
    public string ZabbixUrl                        { get; init; } = string.Empty;
    public int    ZabbixPollIntervalSeconds        { get; init; } = 60;
    public int    RdpPollIntervalSeconds           { get; init; } = 120;
    public int    LocalResourcePollIntervalSeconds { get; init; } = 3;
}