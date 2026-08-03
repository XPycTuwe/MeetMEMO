using System.Reflection;
using System.Text;

namespace MeetMemo.Export;

/// <summary>
/// Правила обработки пакета, которые кладутся внутрь каждого архива.
///
/// Skill в Claude ставится отдельно и есть далеко не у всех, а мемо без правил получается
/// «на усмотрение модели»: с придуманными ответственными и сроками. Поэтому те же правила
/// едут вместе с данными одним файлом — тот, кто открыл архив, сразу видит, что делать.
/// </summary>
public static class SkillTemplate
{
    /// <summary>Имя файла в корне архива. Начинается с «!», чтобы стоять первым в списке.</summary>
    public const string FileName = "!ИНСТРУКЦИЯ_ДЛЯ_CLAUDE.md";

    /// <summary>
    /// Собирает единый файл правил. Разбивать на несколько файлов смысла нет: тот, кто читает
    /// архив, должен получить всё за одно открытие, не разыскивая приложения по папкам.
    /// </summary>
    public static string Build()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Инструкция для Claude: как обработать этот пакет");
        sb.AppendLine();
        sb.AppendLine("Это архив встречи, записанной приложением MeetMemo. Его загрузили, чтобы получить");
        sb.AppendLine("мемо: **отдельной просьбы может не быть — приступай сразу**. Ниже правила обработки;");
        sb.AppendLine("они совпадают со Skill `meeting-memo` и вложены в архив, чтобы работать без него.");
        sb.AppendLine();
        sb.AppendLine("Результат: `memo.docx` (документ Word — основной результат), `memo.md`,");
        sb.AppendLine("`transcript_clean.md` (вся речь разобранная, с пометками неуверенных мест)");
        sb.AppendLine("и `actions.json`.");
        sb.AppendLine();
        sb.AppendLine("Если Skill `meeting-memo` уже установлен, следуй ему — этот файл его дублирует.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(StripFrontMatter(Read("SKILL.md")));
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Приложение А. Проверочный список перед выдачей");
        sb.AppendLine();
        sb.Append(StripFrontMatter(Read("quality-checklist.md")));
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Приложение Б. Пример готового мемо");
        sb.AppendLine();
        sb.Append(StripFrontMatter(Read("memo-example.md")));

        return sb.ToString();
    }

    private static string Read(string name)
    {
        var resource = "MeetMemo.Export.SkillTemplate." + name;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"В сборку не вшит шаблон скилла «{name}». Проверьте EmbeddedResource в MeetMemo.Export.csproj.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Убирает YAML-заголовок скилла (name/description): внутри архива это служебные поля
    /// формата скиллов, а читателю они только мешают.
    /// </summary>
    private static string StripFrontMatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return text;

        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return text;

        return normalized[(end + 4)..].TrimStart('\n');
    }
}
