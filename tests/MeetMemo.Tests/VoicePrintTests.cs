using MeetMemo.Asr;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Память на голоса: узнавание, уточнение отпечатка и правка имён. Сама модель здесь
/// не участвует — проверяется логика, в которой легко ошибиться незаметно.
/// </summary>
public sealed class VoicePrintTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"mm-voices-{Guid.NewGuid():N}.json");

    private VoicePrintStore NewStore() => new(_path);

    /// <summary>Похожий, но не тот же вектор: имитирует ту же речь в другой день.</summary>
    private static float[] Vector(float seed, float noise = 0f)
    {
        var v = new float[512];
        for (var i = 0; i < v.Length; i++)
            v[i] = MathF.Sin(seed + i * 0.01f) + noise * MathF.Sin(i * 0.37f);
        return v;
    }

    [Fact]
    public void Знакомый_голос_узнаётся_а_чужой_нет()
    {
        var store = NewStore();
        store.Remember("Елена Петрова", "аналитик", Vector(1f));

        var same = store.Recognize(Vector(1f, noise: 0.05f));
        Assert.NotNull(same);
        Assert.Equal("Елена Петрова", same!.Value.Print.Name);

        // Совсем другой тембр не должен получить чужое имя.
        Assert.Null(store.Recognize(Vector(50f)));
    }

    [Fact]
    public void Должность_попадает_в_подпись()
    {
        var store = NewStore();
        var print = store.Remember("Иванов", "руководитель проекта", Vector(2f));

        Assert.Equal("Иванов — руководитель проекта", print.Display);
    }

    [Fact]
    public void Повторное_подтверждение_уточняет_отпечаток_а_не_плодит_двойника()
    {
        var store = NewStore();
        store.Remember("Андрей", null, Vector(3f));
        var second = store.Remember("Андрей", "тимлид", Vector(3f, noise: 0.1f));

        Assert.Equal(1, store.Count);
        Assert.Equal(2, second.Confirmations);

        // Должность, названная позже, не теряется.
        Assert.Equal("тимлид", second.Role);
    }

    [Fact]
    public void Имя_и_должность_правятся_задним_числом()
    {
        var store = NewStore();
        var print = store.Remember("Собеседник с планёрки", null, Vector(4f));

        Assert.True(store.Rename(print.Id, "Екатерина Иванова", "финансовый директор"));

        var updated = store.All.Single();
        Assert.Equal("Екатерина Иванова", updated.Name);
        Assert.Equal("финансовый директор", updated.Role);
    }

    [Fact]
    public void Забытый_голос_больше_не_узнаётся()
    {
        var store = NewStore();
        var print = store.Remember("Временный", null, Vector(5f));

        Assert.True(store.Forget(print.Id));
        Assert.Equal(0, store.Count);
        Assert.Null(store.Recognize(Vector(5f)));
    }

    [Fact]
    public void Память_переживает_перезапуск_приложения()
    {
        NewStore().Remember("Пётр", "снабжение", Vector(6f));

        // Новый экземпляр читает тот же файл — как после перезапуска.
        var reopened = NewStore();
        Assert.Equal(1, reopened.Count);

        var found = reopened.Recognize(Vector(6f, noise: 0.03f));
        Assert.NotNull(found);
        Assert.Equal("Пётр", found!.Value.Print.Name);
    }

    [Fact]
    public void Мера_похожести_ведёт_себя_предсказуемо()
    {
        var a = Vector(7f);

        Assert.Equal(1f, VoicePrintStore.CosineSimilarity(a, a), 3);
        Assert.True(VoicePrintStore.CosineSimilarity(a, Vector(7f, 0.1f))
                  > VoicePrintStore.CosineSimilarity(a, Vector(40f)));
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { }
    }
}
