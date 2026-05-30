using AdminConsole.Core.Models;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AdminConsole.Core.Messages;

/// <summary>
/// Published by any service that wants to append a structured entry to
/// the application log (Logs tab + logs file on disk).
/// Replaces the earlier plain-string version.
/// </summary>
public sealed class AppLogEntryMessage : ValueChangedMessage<AppLogEntry>
{
    public AppLogEntryMessage(AppLogEntry entry) : base(entry) { }

    // ── Convenience factories so call-sites stay readable ──────────────────

    public static AppLogEntryMessage Info(string source, string message) =>
        new(new AppLogEntry(LogSeverity.Info, source, message, DateTimeOffset.Now));

    public static AppLogEntryMessage Success(string source, string message) =>
        new(new AppLogEntry(LogSeverity.Success, source, message, DateTimeOffset.Now));

    public static AppLogEntryMessage Warning(string source, string message) =>
        new(new AppLogEntry(LogSeverity.Warning, source, message, DateTimeOffset.Now));

    public static AppLogEntryMessage Error(string source, string message) =>
        new(new AppLogEntry(LogSeverity.Error, source, message, DateTimeOffset.Now));
}