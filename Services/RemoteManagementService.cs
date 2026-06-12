using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using System.Diagnostics;
using System.Management;

namespace AdminConsole.Services;

/// <summary>
/// Executes remote management actions against servers.
/// All methods are async and run process/WMI work on the thread pool.
/// Results (success or error) are published via AppLogEntryMessage so
/// they appear in the Logs tab and on disk automatically.
///
/// WMI dependency: System.Management is included in the Windows Desktop
/// SDK — no extra NuGet package needed for net8.0-windows.
/// </summary>
public sealed class RemoteManagementService
{
    private readonly IMessenger _messenger;
    private const string LogSource = "RemoteMgmt";

    public RemoteManagementService(IMessenger messenger)
    {
        _messenger = messenger;
    }

    // ── Ping -t in a new terminal window ─────────────────────────────────────

    /// <summary>
    /// Opens a new cmd.exe window running "ping -t <ip>".
    /// Fire-and-forget — the window is independent of the app.
    /// </summary>
    public void OpenContinuousPing(string ip, string serverName)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/k ping -t {ip}",
                UseShellExecute = true,
                CreateNoWindow  = false
            });

            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                $"Opened continuous ping window for {serverName} ({ip})."));
        }
        catch (Exception ex)
        {
            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"Failed to open ping window for {serverName} ({ip}): {ex.Message}"));
        }
    }

    // ── RDP connection ────────────────────────────────────────────────────────

    /// <summary>
    /// Launches mstsc.exe targeting the given IP.
    /// Async because Process.Start can briefly block on some systems
    /// when resolving the executable path.
    /// </summary>
    public Task OpenRdpAsync(string ip, string serverName)
    {
        return Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "mstsc.exe",
                    Arguments       = $"/v:{ip}",
                    UseShellExecute = true
                });

                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"Launched RDP session to {serverName} ({ip})."));
            }
            catch (Exception ex)
            {
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"Failed to launch RDP to {serverName} ({ip}): {ex.Message}"));
            }
        });
    }

    // ── Remote restart ────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a WMI Win32_OperatingSystem.Reboot() call against the remote host.
    /// Requires the current user to have admin rights on the target machine.
    /// </summary>
    public Task RemoteRestartAsync(string ip, string serverName)
    {
        return Task.Run(() =>
        {
            try
            {
                ExecuteWmiShutdown(ip, isReboot: true);

                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"RESTART command sent to {serverName} ({ip})."));
            }
            catch (Exception ex)
            {
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"Restart of {serverName} ({ip}) FAILED: {ex.Message}"));
            }
        });
    }

    // ── Remote shutdown ───────────────────────────────────────────────────────

    /// <summary>
    /// Issues a WMI Win32_OperatingSystem.Shutdown() call against the remote host.
    /// </summary>
    public Task RemoteShutdownAsync(string ip, string serverName)
    {
        return Task.Run(() =>
        {
            try
            {
                ExecuteWmiShutdown(ip, isReboot: false);

                _messenger.Send(AppLogEntryMessage.Warning(LogSource,
                    $"SHUTDOWN command sent to {serverName} ({ip})."));
            }
            catch (Exception ex)
            {
                _messenger.Send(AppLogEntryMessage.Error(LogSource,
                    $"Shutdown of {serverName} ({ip}) FAILED: {ex.Message}"));
            }
        });
    }

    // ── WMI helper ────────────────────────────────────────────────────────────

    private static void ExecuteWmiShutdown(string ip, bool isReboot)
    {
        // Connect to the remote host's WMI namespace.
        var scope = new ManagementScope(
            $@"\\{ip}\root\cimv2",
            new ConnectionOptions
            {
                Impersonation  = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true
            });

        scope.Connect();

        var query = new ObjectQuery("SELECT * FROM Win32_OperatingSystem WHERE Primary=true");
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var results  = searcher.Get();

        foreach (ManagementObject os in results.Cast<ManagementObject>())
        {
            using (os)
            using (var inParams = os.GetMethodParameters("Win32Shutdown"))
            {
                inParams["Flags"]    = isReboot ? 2 : 1;
                inParams["Reserved"] = 0;
                os.InvokeMethod("Win32Shutdown", inParams, null);
            }
        }
    }
}