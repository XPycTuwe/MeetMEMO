using MeetMemo.Storage;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Заметки человека: пишутся во время встречи, значит терять их нельзя ни при каких
/// обстоятельствах — ни при занятом файле, ни при обрыве записи.
/// </summary>
public sealed class NotesTests
{
    private static string TempFile() => Path.Combine(
        Path.GetTempPath(), "meetmemo-tests", Guid.NewGuid().ToString("N"), "notes.md");

    private static MeetingFolder TempFolder()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "meetmemo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new MeetingFolder(root);
    }

    [Fact]
    public void Заметка_переживает_перезапуск()
    {
        var path = TempFile();
        new NotesStore(path).Write("КГФ по кусту 2А — 118");

        Assert.Equal("КГФ по кусту 2А — 118", new NotesStore(path).Read());
    }

    [Fact]
    public void Пустых_заметок_нет_и_это_не_ошибка()
    {
        Assert.Equal(string.Empty, new NotesStore(TempFile()).Read());
    }

    [Fact]
    public void Перезапись_не_оставляет_временных_файлов()
    {
        var path = TempFile();
        var store = new NotesStore(path);

        store.Write("первая мысль");
        store.Write("вторая мысль");

        Assert.Equal("вторая мысль", store.Read());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Многострочный_текст_не_портится()
    {
        var path = TempFile();
        var text = "Спросить:\n  про сроки\n  про смету\n\nПитч: логистика";

        new NotesStore(path).Write(text);

        Assert.Equal(text, new NotesStore(path).Read());
    }

    [Fact]
    public void Заметки_кладутся_в_папку_встречи()
    {
        var path = TempFile();
        new NotesStore(path).Write("сроки сдвинули на 12 дней");

        var folder = TempFolder();
        Assert.True(new NotesStore(path).WriteToMeeting(folder));

        var written = File.ReadAllText(folder.NotesMd);
        Assert.Contains("сроки сдвинули на 12 дней", written);
        Assert.Contains("# Заметки участника", written);
    }

    /// <summary>
    /// Пустой файл в пакете только сбивает с толку того, кто его разбирает:
    /// он выглядит как «человек ничего не счёл важным», а на деле просто не писал.
    /// </summary>
    [Fact]
    public void Пустые_заметки_в_пакет_не_кладутся()
    {
        var path = TempFile();
        new NotesStore(path).Write("   \n  \n ");

        var folder = TempFolder();

        Assert.False(new NotesStore(path).WriteToMeeting(folder));
        Assert.False(File.Exists(folder.NotesMd));
    }

    [Fact]
    public void Заметок_нет_вовсе_в_пакет_тоже_не_кладём()
    {
        var folder = TempFolder();

        Assert.False(new NotesStore(TempFile()).WriteToMeeting(folder));
        Assert.False(File.Exists(folder.NotesMd));
    }
}
