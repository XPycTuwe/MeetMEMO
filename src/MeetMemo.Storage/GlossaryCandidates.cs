using System.Text;
using System.Text.RegularExpressions;
using MeetMemo.Contracts;

namespace MeetMemo.Storage;

/// <summary>
/// Отбор слов, похожих на термины встречи, для последующей расшифровки.
///
/// Распознаватель уверенно портит именно специальную лексику: КГФ слышится как «ка гэ эф»,
/// Ач3 — как «ач три», фамилии коллег превращаются во что попало. Заметить это в стенограмме
/// на тысячу реплик человек не может, а модель при разборе архива — может, если ей показать,
/// на какие слова смотреть.
///
/// Здесь только отбор кандидатов: что из них термин, а что мусор, решает модель, а последнее
/// слово остаётся за человеком — он подтверждает добавление в словарь.
/// </summary>
public static class GlossaryCandidates
{
    /// <summary>Сколько раз слово должно прозвучать, чтобы попасть в кандидаты.</summary>
    private const int MinOccurrences = 3;

    /// <summary>Сколько кандидатов класть в пакет. Больше — список никто не осилит.</summary>
    private const int MaxCandidates = 40;

    /// <summary>
    /// Служебные слова, которые встречаются часто в любой встрече. Список короткий
    /// намеренно: полноценный словарь русского языка сюда тащить незачем, а частотный
    /// порог и длина слова отсекают основное.
    /// </summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "который", "которые", "которых", "поэтому", "потому", "сейчас", "сегодня",
        "нужно", "можно", "должны", "будет", "было", "если", "когда", "тогда",
        "здесь", "там", "вот", "как", "что", "это", "этот", "эта", "эти", "так",
        "давайте", "пожалуйста", "спасибо", "хорошо", "понятно", "конечно",
        "вопрос", "ответ", "работа", "работы", "время", "года", "день", "неделю",
        "просто", "значит", "получается", "смотри", "смотрите", "слушай",
        "думаю", "считаю", "говорю", "сказал", "сделать", "сделали", "делать"
    };

    /// <summary>Слово из букв и цифр: «ач3», «2а», «квд». Дефис внутри допускаем.</summary>
    private static readonly Regex WordPattern = new(
        @"[\p{L}\p{Nd}][\p{L}\p{Nd}\-]{2,}", RegexOptions.Compiled);

    /// <summary>Кандидат: как звучало и сколько раз прозвучало.</summary>
    public sealed record Candidate(string Word, int Count, string Example);

    /// <summary>
    /// Собирает кандидатов из стенограммы. Известные термины пропускаем — они уже
    /// в словаре, спрашивать о них второй раз незачем.
    /// </summary>
    public static IReadOnlyList<Candidate> Collect(
        IEnumerable<TranscriptSegment> segments, IReadOnlyList<GlossaryTerm> known)
    {
        var knownWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in known)
        {
            knownWords.Add(term.Heard);
            knownWords.Add(term.Correct);
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var examples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;

            foreach (Match match in WordPattern.Matches(segment.Text))
            {
                var word = match.Value;

                if (word.Length < 4) continue;
                if (Common.Contains(word) || knownWords.Contains(word)) continue;

                counts[word] = counts.GetValueOrDefault(word) + 1;

                // Пример нужен для расшифровки: по одному слову не понять, о чём речь.
                if (!examples.ContainsKey(word)) examples[word] = segment.Text.Trim();
            }
        }

        return counts
            .Where(p => p.Value >= MinOccurrences)
            .OrderByDescending(p => p.Value)
            .Take(MaxCandidates)
            .Select(p => new Candidate(p.Key, p.Value, examples[p.Key]))
            .ToList();
    }

    /// <summary>
    /// Кладёт кандидатов в папку встречи. Файл попадает в архив, и модель при разборе
    /// предлагает по нему написание и значение — а человек решает, что взять в словарь.
    /// </summary>
    public static void WriteToMeeting(MeetingFolder folder, IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("# Кандидаты в словарь");
        sb.AppendLine();
        sb.AppendLine("Слова, которые часто звучали на встрече и не похожи на обычную речь:");
        sb.AppendLine("термины, сокращения, фамилии. Отобраны механически, поэтому часть —");
        sb.AppendLine("обычные слова, и это нормально.");
        sb.AppendLine();
        sb.AppendLine("Что с ними делать — см. раздел «Кандидаты в словарь» в правилах обработки.");
        sb.AppendLine();
        sb.AppendLine("| Как распозналось | Раз | Пример реплики |");
        sb.AppendLine("|---|---|---|");

        foreach (var candidate in candidates)
        {
            var example = candidate.Example.Length > 90
                ? candidate.Example[..90] + "…"
                : candidate.Example;

            sb.AppendLine($"| {candidate.Word} | {candidate.Count} | {example.Replace('|', '/')} |");
        }

        try
        {
            File.WriteAllText(
                Path.Combine(folder.Root, "glossary-candidates.md"),
                sb.ToString(),
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Подсказка не настолько важна, чтобы прерывать финализацию встречи.
        }
    }
}
