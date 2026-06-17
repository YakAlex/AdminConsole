using AdminConsole.Views;

namespace AdminConsole.Services;

/// <summary>
/// Серіалізує та дедуплікує credential-діалоги між фоновими сервісами.
///
/// Використовує TaskCompletionSource замість Task для уникнення race condition:
/// всі виклики що прийшли поки діалог відкритий — отримують той самий TCS
/// і чекають на один результат.
/// </summary>
public sealed class CredentialPromptCoordinator : ICredentialPrompt
{
    private readonly ICredentialPrompt _inner;
    private readonly object _sync = new();

    private TaskCompletionSource<(string Username, string Password)?>? _pendingRdp;
    private TaskCompletionSource<string?>?                              _pendingZabbix;

    public CredentialPromptCoordinator(MainWindow inner)
    {
        _inner = inner;
    }

    public Task<(string Username, string Password)?> PromptAsync(string targetName)
    {
        lock (_sync)
        {
            if (_pendingRdp is not null)
                return _pendingRdp.Task;  // діалог вже відкритий — приєднуємось

            _pendingRdp = new TaskCompletionSource<(string, string)?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // Запускаємо діалог поза lock щоб не тримати його під час await
            _ = RunRdpAsync(targetName);

            return _pendingRdp.Task;
        }
    }

    public Task<string?> PromptZabbixTokenAsync()
    {
        lock (_sync)
        {
            if (_pendingZabbix is not null)
                return _pendingZabbix.Task;

            _pendingZabbix = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _ = RunZabbixAsync();

            return _pendingZabbix.Task;
        }
    }

    private async Task RunRdpAsync(string targetName)
    {
        try
        {
            var result = await _inner.PromptAsync(targetName).ConfigureAwait(false);

            lock (_sync)
            {
                _pendingRdp?.TrySetResult(result);
                _pendingRdp = null;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _pendingRdp?.TrySetException(ex);
                _pendingRdp = null;
            }
        }
    }

    private async Task RunZabbixAsync()
    {
        try
        {
            var result = await _inner.PromptZabbixTokenAsync().ConfigureAwait(false);

            lock (_sync)
            {
                _pendingZabbix?.TrySetResult(result);
                _pendingZabbix = null;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _pendingZabbix?.TrySetException(ex);
                _pendingZabbix = null;
            }
        }
    }
}