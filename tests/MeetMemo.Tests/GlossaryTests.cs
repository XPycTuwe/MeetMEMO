using MeetMemo.Contracts;
using MeetMemo.Storage;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Общий словарь терминов и отбор кандидатов в него.
///
/// Словарь — единственное, что переживает встречу и влияет на все следующие, поэтому
/// ошибка здесь не разовая: неверная запись будет портить каждую стенограмму.
/// </summary>
public sealed class GlossaryTests
{
    private static string TempFile() => Path.Combine(
        Path.GetTempPath(), "meetmemo-tests", Guid.NewGuid().ToString("N"), "glossary.json");

    private static TranscriptSegment Seg(string text) => new()
    {
        StartMs = 0,
        EndMs = 1000,
        Source = AudioChannel.Application,
        Text = text
    };

    [Fact]
    public void Термин_переживает_перезапуск()
    {
        var path = TempFile();

        new GlossaryStore(path).Add("ка гэ эф", "КГФ", "конденсатно-газовый фактор");

        var term = Assert.Single(new GlossaryStore(path).All);

        Assert.Equal("КГФ", term.Correct);
        Assert.Equal("конденсатно-газовый фактор", term.Meaning);
    }

    [Fact]
    public void Повторное_добавление_обновляет_а_не_дублирует()
    {
        var store = new GlossaryStore(TempFile());

        store.Add("ач три", "Ач-3", null);
        store.Add("АЧ ТРИ", "Ач3", "пласт ачимовской свиты");

        var term = Assert.Single(store.All);
        Assert.Equal("Ач3", term.Correct);
        Assert.Equal("пласт ачимовской свиты", term.Meaning);
    }

    [Fact]
    public void Пустое_поле_в_словарь_не_попадает()
    {
        var store = new GlossaryStore(TempFile());

        store.Add("   ", "КГФ", null);
        store.Add("ка гэ эф", "  ", null);

        Assert.Empty(store.All);
    }

    [Fact]
    public void Битый_файл_не_роняет_словарь()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ это не json");

        var store = new GlossaryStore(path);
        Assert.Empty(store.All);

        store.Add("ка гэ эф", "КГФ", null);
        Assert.Single(store.All);
    }

    [Fact]
    public void В_кандидаты_попадают_только_частые_слова()
    {
        var segments = new[]
        {
            Seg("по кгфу там всё сходится"),
            Seg("кгфу считали вчера"),
            Seg("кгфу надо пересчитать"),
            Seg("однократное слово тут")
        };

        var candidates = GlossaryCandidates.Collect(segments, Array.Empty<GlossaryTerm>());

        var kgf = Assert.Single(candidates, c => c.Word == "кгфу");
        Assert.Equal(3, kgf.Count);
        Assert.DoesNotContain(candidates, c => c.Word == "однократное");
    }

    [Fact]
    public void Уже_известный_термин_в_кандидаты_не_лезет()
    {
        var segments = Enumerable.Repeat(Seg("кгфу посчитали"), 5).ToArray();
        var known = new[] { new GlossaryTerm { Heard = "кгфу", Correct = "КГФ" } };

        var candidates = GlossaryCandidates.Collect(segments, known);

        Assert.DoesNotContain(candidates, c => c.Word == "кгфу");
        Assert.Contains(candidates, c => c.Word == "посчитали");
    }

    [Fact]
    public void Служебные_слова_отсекаются()
    {
        var segments = Enumerable.Repeat(Seg("давайте просто сделать это сегодня"), 5).ToArray();

        Assert.Empty(GlossaryCandidates.Collect(segments, Array.Empty<GlossaryTerm>()));
    }

    [Fact]
    public void У_кандидата_есть_пример_реплики()
    {
        var segments = new[]
        {
            Seg("на кустовой площадке всё готово"),
            Seg("кустовой подъезд размыло"),
            Seg("кустовой график сдвинули")
        };

        var candidate = Assert.Single(
            GlossaryCandidates.Collect(segments, Array.Empty<GlossaryTerm>()),
            c => c.Word == "кустовой");

        Assert.Equal("на кустовой площадке всё готово", candidate.Example);
    }

    [Fact]
    public void Словарь_кладётся_в_папку_встречи()
    {
        var root = Path.Combine(Path.GetTempPath(), "meetmemo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var store = new GlossaryStore(TempFile());
        store.Add("ка гэ эф", "КГФ", "конденсатно-газовый фактор");

        var folder = new MeetingFolder(root);
        store.WriteToMeeting(folder);

        Assert.Contains(
            "| ка гэ эф | КГФ | конденсатно-газовый фактор |",
            File.ReadAllText(folder.GlossaryMd));
    }

    [Fact]
    public void Кандидаты_кладутся_в_папку_встречи()
    {
        var root = Path.Combine(Path.GetTempPath(), "meetmemo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var segments = Enumerable.Repeat(Seg("по кгфу всё сходится"), 3).ToArray();
        var candidates = GlossaryCandidates.Collect(segments, Array.Empty<GlossaryTerm>());

        var folder = new MeetingFolder(root);
        GlossaryCandidates.WriteToMeeting(folder, candidates);

        var text = File.ReadAllText(Path.Combine(root, "glossary-candidates.md"));
        Assert.Contains("кгфу", text);
        Assert.Contains("по кгфу всё сходится", text);
    }
}
