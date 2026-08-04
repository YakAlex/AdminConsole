using AdminConsole.Core.Models;
using AdminConsole.Views;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
namespace AdminConsole.Services;

public sealed class OverlayDialogService : IDialogService
{
    private MainWindow?                       _window;
    private Dispatcher?                       _dispatcher;
    private readonly ILogger<OverlayDialogService> _logger;

    public OverlayDialogService(ILogger<OverlayDialogService> logger)
    {
        _logger = logger;
    }

    public void Attach(MainWindow window)
    {
        _window     = window;
        _dispatcher = window.Dispatcher;
    }

    public Task<bool> ShowConfirmationAsync(
        string title, string body, string confirmLabel = "Confirm")
    {
        if (_window is null || _dispatcher is null)
        {
            _logger.LogWarning(
                "OverlayDialogService: ShowConfirmationAsync викликано до Attach() — " +
                "діалог '{Title}' не показано, повернено false автоматично.", title);
            return Task.FromResult(false);
        }

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.InvokeAsync(() =>
            _window.ShowDialog(title, body, confirmLabel, tcs));

        return tcs.Task;
    }

    /// <summary>
    /// На відміну від ShowConfirmationAsync (overlay Grid у самому MainWindow),
    /// вибір тривалості показується окремим модальним Window — той самий
    /// підхід, що й CredentialDialog/ZabbixTokenDialog. Простіше зробити
    /// кілька кнопок-пресетів так, ніж розширювати overlay під новий UI.
    /// </summary>
    public Task<MaintenanceDurationChoice?> ShowMaintenanceDurationAsync(string serverName, string ip)
    {
        if (_window is null || _dispatcher is null)
        {
            _logger.LogWarning(
                "OverlayDialogService: ShowMaintenanceDurationAsync викликано до Attach() — " +
                "діалог для {Server} не показано.", serverName);
            return Task.FromResult<MaintenanceDurationChoice?>(null);
        }

        var tcs = new TaskCompletionSource<MaintenanceDurationChoice?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.InvokeAsync(() =>
        {
            var dialog = new MaintenanceDialog(serverName, ip) { Owner = _window };
            bool? result = dialog.ShowDialog();
            tcs.SetResult(result == true ? dialog.Choice : null);
        });

        return tcs.Task;
    }
}