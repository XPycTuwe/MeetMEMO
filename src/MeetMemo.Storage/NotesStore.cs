using System.Text;

namespace MeetMemo.Storage;

/// <summary>
/// Заметки: то, что человек держит перед глазами и дописывает по ходу встречи.
///
/// Комментарии в приложении всё равно никто не читает, а место для «ключевых цифр,
/// питча и что вспомнилось» нужно постоянно. Поэтому заметки одни на все встречи и
/// живут рядом с настройками — как стикер на мониторе, который никуда не девается
/// между разговорами.
///
/// Копия уезжает в папку встречи: разбирающей модели полезно знать, что человек
/// считал важным, — этого нет ни в стенограмме, ни на снимках.
/// </summary>
public sealed class NotesStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public NotesStore(string? path = null)
        => _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetMemo", "notes.md");

    /// <summary>Читает заметки. Файла нет — значит пусто, это обычное дело.</summary>
    public string Read()
    {
        try
        {
            lock (_gate) return File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;
        }
        catch (IOException)
        {
            // Файл мог быть занят синхронизацией: пустая заметка лучше исключения.
            return string.Empty;
        }
    }

    /// <summary>
    /// Сохраняет заметки. Пишем через временный файл: заметки правятся во время
    /// встречи, и оборванная запись стёрла бы то, что человек только что придумал.
    /// </summary>
    public void Write(string text)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                var temp = _path + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                File.Move(temp, _path, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Заметки — вспомогательная вещь: их сбой не должен ронять запись встречи.
        }
    }

    /// <summary>
    /// Кладёт заметки в папку встречи. Пустые не кладём: пустой файл в пакете только
    /// сбивает с толку того, кто его разбирает.
    /// </summary>
    public bool WriteToMeeting(MeetingFolder folder)
    {
        var text = Read().Trim();
        if (text.Length == 0) return false;

        var sb = new StringBuilder();
        sb.AppendLine("# Заметки участника");
        sb.AppendLine();
        sb.AppendLine("Записал человек — до встречи или по её ходу. Это не стенограмма:");
        sb.AppendLine("здесь то, что он счёл важным, и это весомее случайной фразы в записи.");
        sb.AppendLine();
        sb.AppendLine(text);

        try
        {
            File.WriteAllText(folder.NotesMd, sb.ToString(), new UTF8Encoding(false));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
