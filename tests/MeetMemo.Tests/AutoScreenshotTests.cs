using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Проверка правила «интервал истёк» для автоснимков.
///
/// Первая версия хранила время последнего снимка как long.MinValue, и вычитание
/// переполняло long: условие никогда не выполнялось, автоснимки не делались вообще.
/// Тест фиксирует поведение на границе, чтобы это не вернулось.
/// </summary>
public class AutoScreenshotIntervalTests
{
    /// <summary>Копия правила из CaptureEngine.TryAutoCapture.</summary>
    private static bool IsDue(long nowMs, long lastMs, double intervalMs) =>
        lastMs < 0 || nowMs - lastMs >= intervalMs;

    [Fact]
    public void Первый_снимок_делается_сразу()
    {
        // «Снимков ещё не было» кодируется отрицательным значением.
        Assert.True(IsDue(nowMs: 3000, lastMs: -1, intervalMs: 15000));
    }

    [Fact]
    public void До_истечения_интервала_снимок_не_делается()
    {
        Assert.False(IsDue(nowMs: 20000, lastMs: 10000, intervalMs: 15000));
    }

    [Fact]
    public void После_интервала_снимок_делается()
    {
        Assert.True(IsDue(nowMs: 26000, lastMs: 10000, intervalMs: 15000));
    }

    [Fact]
    public void Ровно_на_границе_интервала_снимок_делается()
    {
        Assert.True(IsDue(nowMs: 25000, lastMs: 10000, intervalMs: 15000));
    }

    [Fact]
    public void Прежний_способ_с_MinValue_ломался_переполнением()
    {
        // Демонстрация исходной ошибки: разность переполняет long и уходит в минус,
        // поэтому проверка «интервал истёк» давала ложь на первом же вызове.
        // unchecked — потому что в обычном коде арифметика long именно такая,
        // а компилятор отвергает то же выражение в константном виде.
        var now = 3000L;
        var never = long.MinValue;
        var overflowed = unchecked(now - never);

        Assert.True(overflowed < 15000, "переполнение делает разность непригодной для сравнения");
    }
}
