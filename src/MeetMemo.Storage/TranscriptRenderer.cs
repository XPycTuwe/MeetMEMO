using System.Globalization;
using System.Text;
using MeetMemo.Contracts;

namespace MeetMemo.Storage;

/// <summary>
/// Сборка читаемого transcript.md из transcript.jsonl. Запускается на финализации и после
/// восстановления: markdown полностью производен от jsonl, поэтому его всегда можно пересобрать.
/// </summary>
public static class TranscriptRenderer
{
    public static int Render(MeetingFolder folder, SessionManifest manifest)
    {
        var segments = JsonlWriter
            .ReadAll<TranscriptSegment>(folder.TranscriptJsonl)
            .OrderBy(s => s.StartMs)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# {manifest.Title}");
        sb.AppendLine();
        sb.AppendLine($"- Дата: {manifest.StartLocal:dd.MM.yyyy HH:mm} ({manifest.Timezone})");
        if (manifest.DurationMs is { } ms)
            sb.AppendLine($"- Длительность: {FormatDuration(TimeSpan.FromMilliseconds(ms))}");
        if (manifest.Target?.Application is { } app)
            sb.AppendLine($"- Источник: {app}");
        sb.AppendLine($"- Распознавание: {manifest.Transcription.Engine}"
            + (manifest.Transcription.Model is { } m ? $" ({m})" : string.Empty));
        sb.AppendLine();
        sb.AppendLine("> Стенограмма создана автоматическим распознаванием речи и может содержать");
        sb.AppendLine("> ошибки в словах, окончаниях, фамилиях и терминах. Первичный источник —");
        sb.AppendLine("> исходное аудио и временная шкала.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (segments.Count == 0)
        {
            sb.AppendLine("_Распознанных реплик нет._");
        }
        else
        {
            foreach (var seg in segments)
            {
                var stamp = FormatTimecode(seg.StartMs);

                // «Собеседник N» — голос, различённый диаризацией; без неё остаётся канал.
                // Если голос узнан по памяти, в поле speaker лежит имя человека —
                // его и печатаем. «Собеседник N» остаётся для незнакомых.
                var who = seg switch
                {
                    { Source: AudioChannel.Microphone } => "Микрофон",
                    { Speaker: { } spk } when TryParseSpeaker(spk, out var n) => $"Собеседник {n}",
                    { Speaker: { Length: > 0 } name } => name,
                    { Source: AudioChannel.Application } => "Приложение",
                    { Source: AudioChannel.System } => "Система",
                    _ => seg.Source.ToString()
                };
                sb.AppendLine($"**[{stamp}] {who}:** {seg.Text}");
                sb.AppendLine();
            }
        }

        File.WriteAllText(folder.TranscriptMd, sb.ToString(), new UTF8Encoding(false));
        return segments.Count;
    }

    private static bool TryParseSpeaker(string speaker, out int number) =>
        int.TryParse(
            speaker.StartsWith("spk", StringComparison.OrdinalIgnoreCase) ? speaker[3..] : speaker,
            out number);

    public static string FormatTimecode(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.Hours > 0
            ? ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.Hours > 0 ? $"{ts.Hours} ч {ts.Minutes} мин" : $"{ts.Minutes} мин {ts.Seconds} с";
}

/// <summary>
/// Кладёт словарь терминов в папку встречи при финализации (ТЗ 13.3).
///
/// Раньше сюда клали пустой шаблон, и заполнять его надо было заново в каждой встрече.
/// Теперь словарь один на все встречи — <see cref="GlossaryStore"/> — а сюда попадает
/// его копия: Skill читает её и исправляет по ней искажённые термины.
/// </summary>
public static class GlossaryTemplate
{
    public static void Ensure(MeetingFolder folder) => new GlossaryStore().WriteToMeeting(folder);
}
