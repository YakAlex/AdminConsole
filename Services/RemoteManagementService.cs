using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using System.Diagnostics;
using System.Management;
using System.IO;

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
        var scope = new ManagementScope(
            $@"\\{ip}\root\cimv2",
            new ConnectionOptions
            {
                Impersonation    = ImpersonationLevel.Impersonate,
                Authentication   = AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true,
                Timeout          = TimeSpan.FromSeconds(15)
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
                // 6 = Reboot + Force, 5 = Shutdown + Force
                inParams["Flags"] = isReboot ? 6 : 5;
                inParams["Reserved"] = 0;

                using var outParams = os.InvokeMethod("Win32Shutdown", inParams, null);

                if (outParams != null)
                {
                    var returnValue = Convert.ToInt32(outParams["ReturnValue"]);
                    if (returnValue != 0)
                    {
                        throw new Exception($"WMI Error Code: {returnValue}. " +
                                            $"Check permissions, policies, or open applications.");
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Відкриває SSH сесію через PuTTY якщо встановлений,
    /// або через вбудований Windows SSH клієнт як fallback.
    /// </summary>
    public void OpenSsh(string ip, string name)
    {
        try
        {
            var puttyPaths = new[]
            {
                @"C:\Program Files\PuTTY\putty.exe",
                @"C:\Program Files (x86)\PuTTY\putty.exe",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\PuTTY\putty.exe")
            };

            string? putty = puttyPaths.FirstOrDefault(File.Exists);

            if (putty is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = putty,
                    Arguments       = $"-ssh {ip}",
                    UseShellExecute = false
                });

                _messenger.Send(AppLogEntryMessage.Info(LogSource,
                    $"Launched PuTTY SSH to {name} ({ip})."));
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/k ssh {ip}",
                UseShellExecute = true,
                CreateNoWindow  = false
            });

            _messenger.Send(AppLogEntryMessage.Info(LogSource,
                $"PuTTY not found. Launching Windows SSH to {name} ({ip})."));
        }
        catch (Exception ex)
        {
            _messenger.Send(AppLogEntryMessage.Error(LogSource,
                $"Failed to open SSH to {name} ({ip}): {ex.Message}"));
        }
    }
}