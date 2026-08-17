using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using MeetMemo.Contracts;
using MeetMemo.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Capture;

/// <summary>
/// Склейка одинаковых снимков после встречи.
///
/// Докладчик возвращается к слайду по нескольку раз, и автоснимки честно фиксируют каждое
/// появление. В пакете оказывается три-пять копий одной картинки: место тратится зря,
/// а модель, собирающая мемо, вынуждена разбираться, разные это слайды или один.
///
/// Здесь одинаковые кадры схлопываются в один файл, а моменты показа перечисляются
/// в его записи индекса. Сравнение по восприятию, а не побайтно: у одного и того же
/// слайда кадры различаются курсором, мигающим индикатором, сглаживанием шрифтов.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenshotDeduplicator
{
    /// <summary>
    /// Насколько кадры должны совпасть, чтобы считаться одним слайдом. Мера — число
    /// различающихся бит из 64; порог тот же, что у автоснимков, только с обратным знаком:
    /// там решают «картинка изменилась — снимай», здесь «не изменилась — это дубль».
    /// </summary>
    public const int SameFrameDistance = 6;

    /// <summary>
    /// Насколько может отличаться общий тон кадров, которые считаем одним слайдом.
    /// Небольшой разброс нормален: подсветка курсора, выделение строки, плавное затемнение.
    /// </summary>
    public const int SameToneDistance = 12;

    public sealed record Result(int Removed, int Kept, long FreedBytes);

    /// <summary>
    /// Проходит по снимкам встречи и оставляет по одному файлу на каждый уникальный кадр.
    /// Ручные снимки не трогаем никогда: человек нажал кнопку осознанно, и даже повтор
    /// слайда в этот момент значит «обрати внимание сюда».
    /// </summary>
    public static Result Deduplicate(string meetingFolderPath, ILogger? log = null)
    {
        log ??= NullLogger.Instance;

        var folder = new MeetingFolder(meetingFolderPath);
        if (!File.Exists(folder.ScreenshotIndex)) return new Result(0, 0, 0);

        ScreenshotIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<ScreenshotIndex>(
                File.ReadAllText(folder.ScreenshotIndex), JsonSetup.Compact);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Индекс снимков не прочитан — склейка пропущена");
            return new Result(0, 0, 0);
        }

        var entries = index?.Items;
        if (entries is null || entries.Count < 2) return new Result(0, entries?.Count ?? 0, 0);

        var kept = new List<(ScreenshotEntry Entry, ulong Hash, int Tone, List<long> Repeats)>();
        var removedFiles = new List<string>();
        long freed = 0;

        foreach (var entry in entries.OrderBy(e => e.OffsetMs))
        {
            var path = Path.Combine(meetingFolderPath, entry.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                kept.Add((entry, 0, -1, new List<long>()));
                continue;
            }

            ulong hash;
            int tone;
            try
            {
                using var bitmap = new Bitmap(path);
                hash = PerceptualHash.Compute(bitmap);
                tone = AverageTone(bitmap);
            }
            catch (Exception)
            {
                kept.Add((entry, 0, -1, new List<long>()));
                continue;
            }

            // Ручной снимок остаётся всегда — он сделан намеренно.
            if (entry.Manual || entry.Important)
            {
                kept.Add((entry, hash, tone, new List<long>()));
                continue;
            }

            // Совпадения хеша мало: он ловит расположение деталей, а у однотонных кадров
            // деталей нет вовсе — белый экран и тёмный дают один и тот же хеш. Поэтому
            // дополнительно сверяем общий тон.
            var twin = kept.FirstOrDefault(k =>
                k.Tone >= 0
                && PerceptualHash.Distance(k.Hash, hash) <= SameFrameDistance
                && Math.Abs(k.Tone - tone) <= SameToneDistance);

            if (twin.Entry is null)
            {
                kept.Add((entry, hash, tone, new List<long>()));
                continue;
            }

            // Такой кадр уже сохранён: запоминаем момент повтора и убираем файл.
            twin.Repeats.Add(entry.OffsetMs);

            try
            {
                freed += new FileInfo(path).Length;
                File.Delete(path);
                removedFiles.Add(entry.File);
            }
            catch (IOException ex)
            {
                log.LogDebug(ex, "Дубль снимка не удалён: {File}", entry.File);
            }
        }

        if (removedFiles.Count == 0) return new Result(0, kept.Count, 0);

        var updated = kept
            .Select(k => k.Repeats.Count > 0
                ? k.Entry with { AlsoShownAtMs = k.Repeats }
                : k.Entry)
            .ToList();

        try
        {
            var temp = folder.ScreenshotIndex + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(
                index! with { Items = updated }, JsonSetup.Pretty));
            File.Move(temp, folder.ScreenshotIndex, overwrite: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Индекс снимков не переписан после склейки");
        }

        log.LogInformation("Склейка снимков: убрано {Removed}, осталось {Kept}",
            removedFiles.Count, updated.Count);

        return new Result(removedFiles.Count, updated.Count, freed);
    }

    /// <summary>
    /// Средняя яркость кадра. Считается по сетке, а не по каждому пикселю: снимок 4K —
    /// это пять миллионов точек, и полный обход занял бы больше, чем вся склейка.
    /// </summary>
    private static int AverageTone(Bitmap bitmap)
    {
        const int steps = 24;

        long sum = 0;
        var counted = 0;

        for (var y = 0; y < steps; y++)
        {
            for (var x = 0; x < steps; x++)
            {
                var px = bitmap.GetPixel(
                    x * (bitmap.Width - 1) / (steps - 1),
                    y * (bitmap.Height - 1) / (steps - 1));

                sum += (px.R * 299 + px.G * 587 + px.B * 114) / 1000;
                counted++;
            }
        }

        return counted > 0 ? (int)(sum / counted) : 0;
    }
}
