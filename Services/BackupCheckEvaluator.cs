using AdminConsole.Core.Models;
using System.IO;

namespace AdminConsole.Services;

/// <summary>
/// Чиста логіка перевірки одного бекапу (FileAge). Не має стану,
/// нічого не персистує і нікуди не шле повідомлень — лише файлова
/// система на вхід, BackupCheckResult на вихід. Це навмисно окремо
/// від BackupMonitorService (Фаза 2): можна викликати напряму
/// й перевірити вручну на тестовій теці, без DI/хосту.
///
/// Anti-flapping і LastConfirmed*-логіка сюди НЕ входять — це вже
/// відповідальність сервіса, який викликає Evaluate і вирішує, чи
/// "сирий" результат достатньо стабільний, щоб стати підтвердженим.
/// </summary>
public sealed class BackupCheckEvaluator
{
    /// <summary>
    /// Виконує одну перевірку. <paramref name="history"/> — поточна
    /// (до цього виклику) rolling-історія розмірів для цього
    /// сервера+Kind; використовується лише для порівняння, нічого
    /// в ній не змінюється.
    /// </summary>
    public async Task<BackupCheckResult> EvaluateAsync(
        BackupCheckDefinition definition,
        BackupKind kind,
        IReadOnlyList<BackupSample> history,
        CancellationToken ct = default)
    {
        var pattern = kind == BackupKind.Full ? definition.FullPattern : definition.DiffPattern;
        var maxAgeHours = kind == BackupKind.Full ? definition.MaxAgeHoursFull : definition.MaxAgeHoursDiff;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            // Викликач (BackupMonitorService, Фаза 2) має пропускати Kind
            // з порожнім патерном ще ДО виклику Evaluate — це не "Missing",
            // а "ця перевірка для цього сервера не налаштована взагалі".
            throw new ArgumentException(
                $"Патерн для {kind} порожній — не викликай Evaluate для не налаштованого Kind.",
                nameof(kind));
        }

        // ── Stage A0 — швидка перевірка досяжності UNC-хоста ДО важкого I/O ──
        // Directory.Exists/EnumerateFiles на недоступному мережевому шарі
        // можуть блокуватись на нативному Windows-таймауті десятки секунд,
        // і CancellationToken тут не рятує (Task.Run не скасовує вже
        // запущений синхронний виклик). Коротким пінгом (той самий підхід,
        // що WinEventLogReader.IsReachableAsync) відсікаємо це заздалегідь.
        if (TryGetUncHost(definition.Path, out var host))
        {
            var reachable = await WinEventLogReader
                .IsReachableAsync(host, timeoutMs: 1000, ct)
                .ConfigureAwait(false);

            if (!reachable)
                return BackupCheckResult.Unknown($"Хост '{host}' недоступний (ping timeout).");
        }

        // ── Stage A
        // Будь-який збій доступу тут — завжди Unknown, без винятків
        // (жодна причина недоступності не прирівнюється до Missing).
        FileInfo? newest;
        try
        {
            newest = await Task.Run(
                () => FindNewestMatchingFile(definition.Path, pattern), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BackupCheckResult.Unknown(ex.Message);
        }

        // ── Stage B
        if (newest is null)
            return BackupCheckResult.Missing();

        var sample = new BackupSample
        {
            ObservedAt = DateTimeOffset.Now,
            SizeBytes  = newest.Length
        };

        var age = DateTimeOffset.Now - newest.LastWriteTime;
        if (age.TotalHours > maxAgeHours)
            return BackupCheckResult.Stale(sample);

        if (history.Count < definition.MinSamplesForBaseline)
            return BackupCheckResult.Ok(sample); // замало історії — чесно оцінити розмір поки не можемо

        var average = history.Average(s => (double)s.SizeBytes);
        if (average <= 0)
            return BackupCheckResult.Ok(sample); // захист від ділення на нуль (History з самих нульових розмірів)

        var deviationPct = Math.Abs(sample.SizeBytes - average) / average * 100.0;

        return deviationPct > definition.SizeWarningThresholdPct
            ? BackupCheckResult.SizeWarning(sample)
            : BackupCheckResult.Ok(sample);
    }

    private static FileInfo? FindNewestMatchingFile(string path, string pattern)
    {
        if (!Directory.Exists(path))
            throw new IOException($"Шлях недоступний: {path}");

        return new DirectoryInfo(path)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .MaxBy(f => f.LastWriteTime);
    }

    /// <summary>Витягує ім'я хоста з UNC-шляху (\\host\share\...). false для локальних шляхів (C:\...).</summary>
    private static bool TryGetUncHost(string path, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var trimmed = path.TrimStart('\\');
        var sepIndex = trimmed.IndexOfAny(['\\', '/']);
        host = sepIndex > 0 ? trimmed[..sepIndex] : trimmed;
        return !string.IsNullOrWhiteSpace(host);
    }
}