using AdminConsole.Configuration;
using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AdminConsole.Services;

/// <summary>
/// Polls CPU and RAM usage on a fixed interval using PerformanceCounters
/// (Windows-only) and publishes ResourceSnapshotUpdatedMessage.
///
/// Threading: runs entirely on the thread pool via BackgroundService.
/// UI thread is never touched here.
/// </summary>
public sealed class ResourceMonitorService : BackgroundService
{
    private readonly IMessenger                    _messenger;
    private readonly ILogger<ResourceMonitorService> _logger;
    private readonly MonitoringSettings            _settings;

    // PerformanceCounter is IDisposable — we own its lifetime.
    private PerformanceCounter? _cpuCounter;

    public ResourceMonitorService(
        IMessenger messenger,
        ILogger<ResourceMonitorService> logger,
        IOptions<MonitoringSettings> settings)
    {
        _messenger = messenger;
        _logger    = logger;
        _settings  = settings.Value;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogWarning(
                "ResourceMonitorService: not running on Windows — service disabled.");
            return;
        }

        try
        {
            // PerformanceCounter must be initialised on a background thread;
            // the first call to NextValue() always returns 0 (warm-up read),
            // so we do that here before the poll loop starts.
            _cpuCounter = new PerformanceCounter(
                "Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue(); // discard warm-up reading

            // Give the counter 1 second to settle before first real read.
            await Task.Delay(1000, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise CPU PerformanceCounter.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ResourceMonitorService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = BuildSnapshot();
                _messenger.Send(new ResourceSnapshotUpdatedMessage(snapshot));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ResourceMonitorService: error building snapshot.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_settings.LocalResourcePollIntervalSeconds),
                stoppingToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("ResourceMonitorService stopped.");
    }

    public override void Dispose()
    {
        _cpuCounter?.Dispose();
        base.Dispose();
    }

    // -------------------------------------------------------------------------

    private ResourceSnapshot BuildSnapshot()
    {
        double cpuPercent = 0;

        if (_cpuCounter is not null)
        {
            try { cpuPercent = Math.Round(_cpuCounter.NextValue(), 1); }
            catch { cpuPercent = 0; }
        }

        GetMemoryInfo(out double usedGb, out double totalGb);

        double ramPercent = totalGb > 0
            ? Math.Round(usedGb / totalGb * 100.0, 1)
            : 0;

        return new ResourceSnapshot(
            CpuPercent:  cpuPercent,
            RamUsedGb:   Math.Round(usedGb,  2),
            RamTotalGb:  Math.Round(totalGb, 2),
            RamPercent:  ramPercent,
            Timestamp:   DateTimeOffset.Now);
    }

    /// <summary>
    /// Uses GlobalMemoryStatusEx (Win32) for an accurate RAM reading.
    /// PerformanceCounter for RAM is less reliable than the native call.
    /// </summary>
    private static void GetMemoryInfo(out double usedGb, out double totalGb)
    {
        usedGb  = 0;
        totalGb = 0;

        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status))
            {
                totalGb = status.ullTotalPhys / (1024.0 * 1024 * 1024);
                double availGb = status.ullAvailPhys / (1024.0 * 1024 * 1024);
                usedGb  = totalGb - availGb;
            }
        }
        catch { /* fallback: values stay 0 */ }
    }

    // ── Win32 interop ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint  dwLength       = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}