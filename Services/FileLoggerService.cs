using AdminConsole.Core.Messages;
using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace AdminConsole.Services;

/// <summary>
/// Listens for AppLogEntryMessage on the messenger, buffers entries in a
/// thread-safe queue, and flushes them to a rolling log file on a
/// background loop.
///
/// Design decisions:
///   - Uses a ConcurrentQueue + dedicated flush loop so disk I/O never
///     blocks the messenger publish call (which may come from any thread).
///   - Rolls to a new file each calendar day: logs/app-2025-01-15.log
///   - StreamWriter is held open with AutoFlush=false; we flush explicitly
///     in batches for efficiency.
/// </summary>
public sealed class FileLoggerService
    : BackgroundService, IRecipient<AppLogEntryMessage>
{
    private readonly IMessenger                   _messenger;
    private readonly ILogger<FileLoggerService>   _logger;

    private readonly ConcurrentQueue<AppLogEntry> _queue = new();
    private readonly SemaphoreSlim                _signal = new(0);

    private const string LogDirectory   = "logs";
    private const int    FlushBatchSize = 50;
    private const string LogSource      = "FileLogger";

    public FileLoggerService(
        IMessenger messenger,
        ILogger<FileLoggerService> logger)
    {
        _messenger = messenger;
        _logger    = logger;
    }

    // ── IHostedService lifecycle ─────────────────────────────────────────────

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LogDirectory);

        // Register AFTER the directory exists so the first message
        // is never lost.
        _messenger.RegisterAll(this);

        // Emit our own startup entry directly (bypasses the queue since
        // the flush loop hasn't started yet — write synchronously).
        WriteDirectly(new AppLogEntry(
            LogSeverity.Info, LogSource,
            "FileLoggerService started. Log directory: " +
            Path.GetFullPath(LogDirectory),
            DateTimeOffset.Now));

        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _messenger.UnregisterAll(this);

        // Drain the queue one final time before the host shuts down.
        FlushQueueToFile();

        return base.StopAsync(cancellationToken);
    }

    // ── IRecipient<AppLogEntryMessage> ──────────────────────────────────────

    /// <summary>
    /// Called on whichever thread published the message.
    /// We enqueue and signal — never touch the file here.
    /// </summary>
    public void Receive(AppLogEntryMessage message)
    {
        _queue.Enqueue(message.Value);
        _signal.Release();   // wake the flush loop
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait until at least one entry has been enqueued.
            await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);

            // Small coalesce window: if more entries are in flight, wait
            // briefly to batch them together in a single file open/write.
            await Task.Delay(200, stoppingToken).ConfigureAwait(false);

            FlushQueueToFile();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void FlushQueueToFile()
    {
        if (_queue.IsEmpty) return;

        try
        {
            string path = CurrentLogFilePath();

            using var writer = new StreamWriter(
                path,
                append: true,
                encoding: Encoding.UTF8)
            {
                AutoFlush = false
            };

            int written = 0;
            while (_queue.TryDequeue(out var entry) && written < FlushBatchSize)
            {
                writer.WriteLine(entry.Formatted);
                written++;
            }

            writer.Flush();
        }
        catch (Exception ex)
        {
            // Never crash the host over a logging failure.
            _logger.LogError(ex, "FileLoggerService: failed to write to log file.");
        }
    }

    private void WriteDirectly(AppLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                CurrentLogFilePath(),
                entry.Formatted + Environment.NewLine,
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileLoggerService: failed to write startup entry.");
        }
    }

    private static string CurrentLogFilePath()
    {
        string fileName = $"app-{DateTime.Now:yyyy-MM-dd}.log";
        return Path.Combine(LogDirectory, fileName);
    }
}