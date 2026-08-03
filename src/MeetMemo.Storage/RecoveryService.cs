using System.Text.Json;
using MeetMemo.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Storage;

/// <summary>Найденная незакрытая сессия — кандидат на восстановление.</summary>
public sealed record RecoverableSession(
    string FolderPath,
    string Title,
    DateTimeOffset StartLocal,
    bool HasAudio,
    int TranscriptLines);

/// <summary>
/// Поиск и восстановление сессий, оборванных аварийным завершением (ТЗ 11.3, AC-16).
/// Пакет помечается статусом recovered и остаётся валидным: transcript.md пересобирается
/// из уцелевших строк transcript.jsonl, session_id не меняется.
/// </summary>
public sealed class RecoveryService
{
    private readonly ILogger<RecoveryService> _log;

    public RecoveryService(ILogger<RecoveryService>? log = null)
        => _log = log ?? NullLogger<RecoveryService>.Instance;

    /// <summary>
    /// Сессия считается брошенной, если рядом лежит session.lock, а процесса с таким PID уже нет.
    /// Живую сессию другого экземпляра приложения мы таким образом не трогаем.
    /// </summary>
    public IReadOnlyList<RecoverableSession> Scan(string meetingsRoot)
    {
        var result = new List<RecoverableSession>();
        if (!Directory.Exists(meetingsRoot)) return result;

        foreach (var dir in Directory.EnumerateDirectories(meetingsRoot))
        {
            var folder = new MeetingFolder(dir);
            if (!File.Exists(folder.LockFile)) continue;
            if (IsLockAlive(folder.LockFile)) continue;

            var manifest = TryReadManifest(folder);
            if (manifest is null) continue;
            if (manifest.Status is SessionStatus.Completed or SessionStatus.CompletedWithWarnings)
            {
                TryDeleteLock(folder);
                continue;
            }

            var lines = File.Exists(folder.TranscriptJsonl)
                ? JsonlWriter.ReadAll<TranscriptSegment>(folder.TranscriptJsonl).Count()
                : 0;

            result.Add(new RecoverableSession(
                dir,
                manifest.Title,
                manifest.StartLocal,
                Directory.Exists(folder.AudioDir) && Directory.EnumerateFiles(folder.AudioDir).Any(),
                lines));
        }

        return result;
    }

    /// <summary>
    /// Достраивает пакет: пересобирает transcript.md из уцелевшего jsonl, проставляет статус
    /// recovered и убирает метку блокировки. Аудио, если оно писалось, остаётся как есть.
    /// </summary>
    public async Task<SessionManifest?> RecoverAsync(string folderPath, CancellationToken ct = default)
    {
        var folder = new MeetingFolder(folderPath);
        var manifest = TryReadManifest(folder);
        if (manifest is null)
        {
            _log.LogWarning("В {Path} нет читаемого session.json — восстановление невозможно", folderPath);
            return null;
        }

        var duration = EstimateDuration(folder, manifest);

        manifest = manifest with
        {
            Status = SessionStatus.Recovered,
            DurationMs = duration,
            Warnings = manifest.Warnings
                .Append("Сессия восстановлена после аварийного завершения приложения")
                .Distinct()
                .ToArray()
        };

        TranscriptRenderer.Render(folder, manifest);
        await GlossaryTemplate.EnsureAsync(folder, ct).ConfigureAwait(false);
        await AtomicJsonStore.WriteAsync(folder.SessionJson, manifest, JsonSetup.Pretty, ct)
            .ConfigureAwait(false);

        TryDeleteLock(folder);
        _log.LogInformation("Сессия в {Path} восстановлена", folderPath);
        return manifest;
    }

    /// <summary>
    /// Длительность берём по последнему уцелевшему следу: событию timeline или сегменту стенограммы.
    /// Значение из оборванного session.json доверия не заслуживает.
    /// </summary>
    private static long EstimateDuration(MeetingFolder folder, SessionManifest manifest)
    {
        long last = 0;

        foreach (var evt in JsonlWriter.ReadAll<TimelineEvent>(folder.TimelineJsonl))
            if (evt.OffsetMs > last) last = evt.OffsetMs;

        foreach (var seg in JsonlWriter.ReadAll<TranscriptSegment>(folder.TranscriptJsonl))
            if (seg.EndMs > last) last = seg.EndMs;

        return last > 0 ? last : manifest.DurationMs ?? 0;
    }

    private static bool IsLockAlive(string lockPath)
    {
        try
        {
            var json = File.ReadAllText(lockPath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pid", out var pidElement)) return false;

            var pid = pidElement.GetInt32();
            if (pid == Environment.ProcessId) return true;

            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Процесса с таким PID нет — сессия брошена.
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SessionManifest? TryReadManifest(MeetingFolder folder)
    {
        try
        {
            if (!File.Exists(folder.SessionJson)) return null;
            var json = File.ReadAllText(folder.SessionJson);
            return JsonSerializer.Deserialize<SessionManifest>(json, JsonSetup.Pretty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteLock(MeetingFolder folder)
    {
        try { if (File.Exists(folder.LockFile)) File.Delete(folder.LockFile); }
        catch (IOException) { }
    }
}
