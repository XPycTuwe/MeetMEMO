using MeetMemo.Asr;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Карта речи и обратный перевод времени.
///
/// Здесь ошибка не видна глазом: разметка просто съедет, и в стенограмме окажутся
/// чужие имена. Поэтому проверяем не только обычные случаи, но и стыки — именно
/// на них отрезок из склейки распадается на несколько настоящих.
/// </summary>
public sealed class SpeechMapTests
{
    private static SpeechMap Build(params (long, long)[] speech) =>
        SpeechMap.Build(speech, totalDurationMs: 600_000, padMs: 0, mergeGapMs: 0);

    [Fact]
    public void Один_кусок_переводится_сам_в_себя_со_сдвигом()
    {
        var map = Build((10_000, 20_000));

        var piece = Assert.Single(map.Pieces);
        Assert.Equal(0, piece.MappedStartMs);
        Assert.Equal(10_000, piece.MappedEndMs);

        var (start, end) = Assert.Single(map.ToSource(0, 10_000));
        Assert.Equal(10_000, start);
        Assert.Equal(20_000, end);
    }

    [Fact]
    public void Второй_кусок_встаёт_сразу_за_первым()
    {
        var map = Build((10_000, 20_000), (100_000, 130_000));

        Assert.Equal(2, map.Pieces.Count);
        Assert.Equal(10_000, map.Pieces[1].MappedStartMs);
        Assert.Equal(40_000, map.MappedDurationMs);

        // Начало второго куска в склейке — это 100-я секунда оригинала.
        var (start, _) = Assert.Single(map.ToSource(10_000, 11_000));
        Assert.Equal(100_000, start);
    }

    /// <summary>
    /// Главная ловушка: движок про склейку не знает и может протянуть говорящего
    /// через шов. Растягивать такой отрезок на всю дырку нельзя — там была тишина.
    /// </summary>
    [Fact]
    public void Отрезок_через_шов_распадается_на_настоящие_куски()
    {
        var map = Build((10_000, 20_000), (100_000, 130_000));

        var parts = map.ToSource(9_000, 12_000).ToList();

        Assert.Equal(2, parts.Count);
        Assert.Equal((19_000, 20_000), parts[0]);
        Assert.Equal((100_000, 102_000), parts[1]);
    }

    [Fact]
    public void Соседние_куски_сливаются_если_промежуток_мал()
    {
        var map = SpeechMap.Build(
            new[] { (10_000L, 20_000L), (21_000L, 30_000L) },
            totalDurationMs: 600_000, padMs: 0, mergeGapMs: 3000);

        var piece = Assert.Single(map.Pieces);
        Assert.Equal(10_000, piece.SourceStartMs);
        Assert.Equal(30_000, piece.SourceEndMs);
    }

    [Fact]
    public void Далёкие_куски_не_сливаются()
    {
        var map = SpeechMap.Build(
            new[] { (10_000L, 20_000L), (60_000L, 70_000L) },
            totalDurationMs: 600_000, padMs: 0, mergeGapMs: 3000);

        Assert.Equal(2, map.Pieces.Count);
    }

    /// <summary>
    /// После расширения полями соседние фразы налезают друг на друга. Без слияния
    /// один и тот же звук попал бы в склейку дважды, и время поехало бы вперёд.
    /// </summary>
    [Fact]
    public void Наложившиеся_после_полей_куски_не_дублируют_звук()
    {
        var map = SpeechMap.Build(
            new[] { (10_000L, 20_000L), (20_500L, 30_000L) },
            totalDurationMs: 600_000, padMs: 700, mergeGapMs: 0);

        var piece = Assert.Single(map.Pieces);
        Assert.Equal(9_300, piece.SourceStartMs);
        Assert.Equal(30_700, piece.SourceEndMs);
        Assert.Equal(21_400, map.MappedDurationMs);
    }

    [Fact]
    public void Поля_не_вылезают_за_границы_записи()
    {
        var map = SpeechMap.Build(
            new[] { (100L, 5_000L) }, totalDurationMs: 5_200, padMs: 700, mergeGapMs: 0);

        var piece = Assert.Single(map.Pieces);
        Assert.Equal(0, piece.SourceStartMs);
        Assert.Equal(5_200, piece.SourceEndMs);
    }

    [Fact]
    public void Неупорядоченный_вход_не_ломает_карту()
    {
        var map = Build((100_000, 130_000), (10_000, 20_000));

        Assert.Equal(10_000, map.Pieces[0].SourceStartMs);
        Assert.Equal(100_000, map.Pieces[1].SourceStartMs);
    }

    [Fact]
    public void Пустая_и_вырожденная_речь_дают_пустую_карту()
    {
        Assert.Empty(Build().Pieces);
        Assert.Empty(Build((5_000, 5_000)).Pieces);
        Assert.Empty(Build((5_000, 4_000)).Pieces);
    }

    [Fact]
    public void За_пределами_склейки_ничего_не_возвращается()
    {
        var map = Build((10_000, 20_000));

        Assert.Empty(map.ToSource(10_000, 20_000));
        Assert.Empty(map.ToSource(500, 400));
    }

    /// <summary>
    /// Сквозная проверка: любая точка склейки переводится туда, где в оригинале
    /// действительно был этот звук. Считаем прямым перебором, без формул.
    /// </summary>
    [Fact]
    public void Каждая_секунда_склейки_ложится_в_свой_кусок()
    {
        var map = SpeechMap.Build(
            new[] { (7_000L, 19_000L), (44_000L, 61_000L), (300_000L, 305_000L) },
            totalDurationMs: 600_000, padMs: 500, mergeGapMs: 2000);

        long walked = 0;
        foreach (var piece in map.Pieces)
        {
            for (var t = piece.MappedStartMs; t < piece.MappedEndMs; t += 1000)
            {
                var (start, _) = Assert.Single(map.ToSource(t, t + 1));
                var expected = piece.SourceStartMs + (t - piece.MappedStartMs);

                Assert.Equal(expected, start);
                Assert.InRange(start, piece.SourceStartMs, piece.SourceEndMs);
            }

            walked += piece.DurationMs;
        }

        Assert.Equal(map.MappedDurationMs, walked);
    }

    [Fact]
    public void Сплошная_речь_обрезать_не_стоит()
    {
        var map = SpeechMap.Build(
            new[] { (0L, 590_000L) }, totalDurationMs: 600_000, padMs: 0, mergeGapMs: 0);

        Assert.False(map.WorthTrimming(600_000));
    }

    [Fact]
    public void Разрежённую_речь_обрезать_стоит()
    {
        var map = Build((10_000, 20_000), (100_000, 130_000));

        Assert.True(map.WorthTrimming(600_000));
    }
}
