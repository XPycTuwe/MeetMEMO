using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Export;

/// <summary>Запись журнала созданных архивов (ТЗ 15.2).</summary>
public sealed record ExportLogEntry(
    DateTimeOffset CreatedLocal, string MeetingFolder, string ArchivePath, long SizeBytes, int FileCount);

/// <summary>
/// Сборка ZIP-пакета встречи для ручной передачи в Claude.
///
/// Приложение только создаёт архив — никуда его не отправляет. Передача выполняется
/// пользователем вручную, поэтому в продукте нет ни ключей API, ни сетевых адаптеров экспорта.
/// </summary>
public sealed class ZipPackager
{
    private readonly ILogger _log;

    public ZipPackager(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    /// <summary>Имя архива по умолчанию совпадает с именем папки встречи.</summary>
    public static string SuggestArchiveName(string meetingFolderPath) =>
        Path.GetFileName(meetingFolderPath.TrimEnd(Path.DirectorySeparatorChar)) + ".zip";

    public async Task<FileInfo> CreateAsync(
        ExportPlan plan,
        string archivePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var included = plan.Items.Where(i => i.Included).ToList();
        if (included.Count == 0)
            throw new InvalidOperationException("В составе архива не выбрано ни одного файла");

        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        // Пишем во временный файл: прерванная сборка не оставит недоделанный архив,
        // который пользователь примет за готовый.
        var temp = archivePath + ".part";
        if (File.Exists(temp)) File.Delete(temp);

        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var rootName = Path.GetFileName(plan.MeetingFolder.TrimEnd(Path.DirectorySeparatorChar));

                for (var i = 0; i < included.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = included[i];

                    // Уже сжатые форматы повторно не жмём — экономит время на больших пакетах.
                    var level = IsAlreadyCompressed(item.RelativePath)
                        ? CompressionLevel.NoCompression
                        : CompressionLevel.Optimal;

                    var entryName = $"{rootName}/{item.RelativePath}";
                    var entry = archive.CreateEntry(entryName, level);

                    await using var entryStream = entry.Open();
                    await using var source = File.OpenRead(item.FullPath);
                    await source.CopyToAsync(entryStream, ct).ConfigureAwait(false);

                    progress?.Report((i + 1) / (double)included.Count);
                }

                await AddReadmeAsync(archive, plan, ct).ConfigureAwait(false);
            }

            if (File.Exists(archivePath)) File.Delete(archivePath);
            File.Move(temp, archivePath);

            var info = new FileInfo(archivePath);
            _log.LogInformation("Архив собран: {Path} ({Size} байт, {Count} файлов)",
                archivePath, info.Length, included.Count);

            await AppendLogAsync(plan, archivePath, info.Length, included.Count, ct).ConfigureAwait(false);
            return info;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    private static bool IsAlreadyCompressed(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".flac" or ".zip" or ".mp3";
    }

    /// <summary>
    /// Короткая записка внутри архива: кто его открывает в Claude, должен сразу понимать,
    /// что стенограмма автоматическая и дословным протоколом не является.
    /// </summary>
    private static async Task AddReadmeAsync(ZipArchive archive, ExportPlan plan, CancellationToken ct)
    {
        var entry = archive.CreateEntry("КАК_ИСПОЛЬЗОВАТЬ.md", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        await writer.WriteLineAsync("# Пакет встречи MeetMemo").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync(
            "Загрузите этот архив в Claude и попросите подготовить мемо — обработку выполняет "
            + "Skill `meeting-memo`.").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("## Что внутри").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("- `session.json` — параметры встречи;").ConfigureAwait(false);
        await writer.WriteLineAsync("- `timeline.jsonl` — события с таймкодами;").ConfigureAwait(false);
        await writer.WriteLineAsync("- `transcript.jsonl` / `transcript.md` — стенограмма;").ConfigureAwait(false);
        await writer.WriteLineAsync("- `glossary.md` — словарь терминов встречи;").ConfigureAwait(false);
        await writer.WriteLineAsync("- `screenshots/` — снимки экрана и их индекс.").ConfigureAwait(false);
        if (plan.ContainsAudio)
            await writer.WriteLineAsync("- `audio/` — исходные аудиодорожки.").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("## Важно").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync(
            "Стенограмма создана автоматическим распознаванием речи. В ней возможны ошибки в "
            + "словах, окончаниях, фамилиях и терминах, а знаки препинания могут отсутствовать. "
            + "Это не дословный протокол: спорные места нужно проверять по исходному аудио.")
            .ConfigureAwait(false);
    }

    /// <summary>Журнал экспорта — чтобы пользователь всегда мог посмотреть, что и когда выгружалось.</summary>
    private async Task AppendLogAsync(
        ExportPlan plan, string archivePath, long size, int fileCount, CancellationToken ct)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetMemo", "exports.jsonl");

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            var entry = new ExportLogEntry(
                DateTimeOffset.Now, plan.MeetingFolder, archivePath, size, fileCount);
            var json = System.Text.Json.JsonSerializer.Serialize(entry, Storage.JsonSetup.Compact);

            await File.AppendAllTextAsync(logPath, json + Environment.NewLine,
                new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось записать журнал экспорта");
        }
    }
}
