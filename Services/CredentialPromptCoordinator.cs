using AdminConsole.Views;

namespace AdminConsole.Services;

/// <summary>
/// Серіалізує та дедуплікує credential-діалоги між фоновими сервісами.
/// </summary>
public sealed class CredentialPromptCoordinator : ICredentialPrompt
{
    private readonly ICredentialPrompt _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private Task<(string Username, string Password)?>? _pendingRdp;
    private Task<string?>? _pendingZabbix;

    public CredentialPromptCoordinator(MainWindow inner)
    {
        _inner = inner;
    }

    public Task<(string Username, string Password)?> PromptAsync(string targetName)
    {
        lock (_sync)
        {
            _pendingRdp ??= RunRdpAsync(targetName);
            return _pendingRdp;
        }
    }

    public Task<string?> PromptZabbixTokenAsync()
    {
        lock (_sync)
        {
            _pendingZabbix ??= RunZabbixAsync();
            return _pendingZabbix;
        }
    }

    private async Task<(string Username, string Password)?> RunRdpAsync(string targetName)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { return await _inner.PromptAsync(targetName).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        finally { lock (_sync) { _pendingRdp = null; } }
    }

    private async Task<string?> RunZabbixAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { return await _inner.PromptZabbixTokenAsync().ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        finally { lock (_sync) { _pendingZabbix = null; } }
    }
}
