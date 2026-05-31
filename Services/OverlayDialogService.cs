using AdminConsole.Views;
using System.Windows.Threading;

namespace AdminConsole.Services;

/// <summary>
/// Shows confirmation dialogs via a named overlay Grid embedded directly
/// in MainWindow. No DialogHost, no visual tree disruption.
///
/// Attach() is called once by MainWindow after InitializeComponent().
/// ShowConfirmationAsync() can be called from any thread — it marshals
/// to the UI dispatcher internally.
/// </summary>
public sealed class OverlayDialogService : IDialogService
{
    private MainWindow?  _window;
    private Dispatcher?  _dispatcher;

    public void Attach(MainWindow window)
    {
        _window     = window;
        _dispatcher = window.Dispatcher;
    }

    public Task<bool> ShowConfirmationAsync(
        string title,
        string body,
        string confirmLabel = "Confirm")
    {
        if (_window is null || _dispatcher is null)
            return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.InvokeAsync(() =>
            _window.ShowDialog(title, body, confirmLabel, tcs));

        return tcs.Task;
    }
}