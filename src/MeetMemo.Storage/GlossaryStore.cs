using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetMemo.Storage;

/// <summary>Термин, который распознавание стабильно портит.</summary>
public sealed record GlossaryTerm
{
    /// <summary>Как слышится распознавателю: «ач три», «ка гэ эф», «басанаев».</summary>
    [JsonPropertyName("heard")]
    public required string Heard { get; init; }

    /// <summary>Как правильно писать: «Ач3», «КГФ», «Басанаев».</summary>
    [JsonPropertyName("correct")]
    public required string Correct { get; init; }

    /// <summary>Что это означает — подсказка для мемо и для памяти самого человека.</summary>
    [JsonPropertyName("meaning")]
    public string? Meaning { get; init; }

    [JsonPropertyName("added_utc")]
    public DateTimeOffset AddedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Общий словарь терминов: как слышится и как правильно.
///
/// Раньше словарь лежал в папке каждой встречи пустым шаблоном — его надо было заполнять
/// заново и читал его только тот, кто собирал мемо. Теперь он один на все встречи,
/// живёт рядом с настройками и кладётся в каждый пакет уже заполненным.
///
/// Термины у каждого свои: КГФ, ГКИ, Ач3 и фамилии коллег не угадать заранее, поэтому
/// словарь наполняется по ходу — из подсказок, которые модель выдаёт после разбора встречи.
/// </summary>
public sealed class GlossaryStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<GlossaryTerm> _terms = new();

    public GlossaryStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetMemo", "glossary.json");

    public IReadOnlyList<GlossaryTerm> All
    {
        get { lock (_gate) return _terms.ToList(); }
    }

    public int Count
    {
        get { lock (_gate) return _terms.Count; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var loaded = JsonSerializer.Deserialize<List<GlossaryTerm>>(
                File.ReadAllText(_path), JsonSetup.Compact);

            lock (_gate) _terms = loaded ?? new List<GlossaryTerm>();
        }
        catch (Exception)
        {
            // Битый словарь не должен мешать записи: начинаем с пустого, файл не трогаем.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            List<GlossaryTerm> snapshot;
            lock (_gate) snapshot = _terms.ToList();

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonSetup.Pretty));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Словарь — вспомогательная вещь; его сбой не должен ронять встречу.
        }
    }

    /// <summary>Добавляет термин или обновляет существующий с тем же «как слышится».</summary>
    public void Add(string heard, string correct, string? meaning)
    {
        var key = heard.Trim();
        var value = correct.Trim();
        if (key.Length == 0 || value.Length == 0) return;

        lock (_gate)
        {
            var existing = _terms.FindIndex(t =>
                string.Equals(t.Heard, key, StringComparison.OrdinalIgnoreCase));

            var term = new GlossaryTerm
            {
                Heard = key,
                Correct = value,
                Meaning = string.IsNullOrWhiteSpace(meaning) ? null : meaning.Trim()
            };

            if (existing >= 0) _terms[existing] = term;
            else _terms.Add(term);
        }

        Save();
    }

    public bool Remove(string heard)
    {
        bool removed;
        lock (_gate)
        {
            removed = _terms.RemoveAll(t =>
                string.Equals(t.Heard, heard, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (removed) Save();
        return removed;
    }

    /// <summary>
    /// Кладёт словарь в папку встречи в читаемом виде. Файл попадает в архив, и тот,
    /// кто собирает мемо, правит по нему термины — не догадками, а по вашему списку.
    /// </summary>
    public void WriteToMeeting(MeetingFolder folder)
    {
        var terms = All;

        var sb = new StringBuilder();
        sb.AppendLine("# Словарь встречи");
        sb.AppendLine();
        sb.AppendLine("Термины, имена и сокращения, которые распознавание стабильно искажает.");
        sb.AppendLine("Исправляй по этой таблице только подтверждённые ошибки — если в стенограмме");
        sb.AppendLine("стоит слово из колонки «как слышится», пиши вместо него «верное написание».");
        sb.AppendLine();
        sb.AppendLine("| Как слышится | Верное написание | Пояснение |");
        sb.AppendLine("|---|---|---|");

        if (terms.Count == 0)
        {
            sb.AppendLine("|  |  |  |");
            sb.AppendLine();
            sb.AppendLine("_Словарь пуст. Термины добавляются в MeetMemo: Параметры → Словарь._");
        }
        else
        {
            foreach (var term in terms.OrderBy(t => t.Correct, StringComparer.CurrentCulture))
                sb.AppendLine($"| {term.Heard} | {term.Correct} | {term.Meaning ?? string.Empty} |");
        }

        try
        {
            File.WriteAllText(folder.GlossaryMd, sb.ToString(), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Файл мог быть занят — словарь не настолько важен, чтобы прерывать финализацию.
        }
    }
}
