namespace AdminConsole.Services;

public interface ICredentialPrompt
{
    /// <summary>Запит RDP credentials через Windows Credential Dialog.</summary>
    Task<(string Username, string Password)?> PromptAsync(string targetName);

    /// <summary>Запит Zabbix API токену через простий текстовий діалог.</summary>
    Task<string?> PromptZabbixTokenAsync();
}