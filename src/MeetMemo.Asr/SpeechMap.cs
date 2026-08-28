namespace MeetMemo.Asr;

/// <summary>
/// Карта речи: какие куски дорожки стоит отдавать диаризации и как вернуть её ответы
/// обратно в настоящее время встречи.
///
/// Диаризация идёт примерно вполовину реального времени, и на встрече в четыре часа
/// это два часа работы процессора. При этом от четверти до половины записи — тишина:
/// на разных встречах речь занимала от 26% до 85% времени. Размечать тишину незачем,
/// её и так никто не расшифровывает.
///
/// Поэтому берём куски, где VAD уже нашёл речь при записи, склеиваем их подряд
/// и отдаём движку короткую дорожку. Ответы он вернёт во времени склейки — здесь же
/// они переводятся обратно.
///
/// Тайминг — самое хрупкое место: ошибка в отображении сдвинет всю разметку, и это
/// заметят не сразу, а по чужим именам в стенограмме. Поэтому карта отделена от всего
/// остального и проверяется отдельно.
/// </summary>
public sealed class SpeechMap
{
    /// <summary>Кусок речи: где он в оригинале и куда попал в склейке.</summary>
    public sealed record Piece(long SourceStartMs, long SourceEndMs, long MappedStartMs)
    {
        public long DurationMs => SourceEndMs - SourceStartMs;

        public long MappedEndMs => MappedStartMs + DurationMs;
    }

    private readonly List<Piece> _pieces;

    private SpeechMap(List<Piece> pieces) => _pieces = pieces;

    public IReadOnlyList<Piece> Pieces => _pieces;

    /// <summary>Сколько звука в склейке.</summary>
    public long MappedDurationMs =>
        _pieces.Count == 0 ? 0 : _pieces[^1].MappedEndMs;

    /// <summary>Сколько звука было в оригинале на всём протяжении речи.</summary>
    public long SourceSpanMs =>
        _pieces.Count == 0 ? 0 : _pieces[^1].SourceEndMs - _pieces[0].SourceStartMs;

    /// <summary>
    /// Строит карту по границам речи.
    ///
    /// Куски расширяются полями и склеиваются, если между ними меньше <paramref name="mergeGapMs"/>:
    /// резать по самому краю фразы нельзя — сегментации нужен контекст вокруг речи,
    /// а частые склейки создают лишние стыки, каждый из которых движок может принять
    /// за смену говорящего.
    /// </summary>
    public static SpeechMap Build(
        IEnumerable<(long StartMs, long EndMs)> speech,
        long totalDurationMs,
        long padMs = 700,
        long mergeGapMs = 3000)
    {
        var sorted = speech
            .Where(s => s.EndMs > s.StartMs)
            .Select(s => (
                Start: Math.Max(0, s.StartMs - padMs),
                End: Math.Min(totalDurationMs, s.EndMs + padMs)))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();

        var pieces = new List<Piece>();
        long mapped = 0;

        var i = 0;
        while (i < sorted.Count)
        {
            var start = sorted[i].Start;
            var end = sorted[i].End;
            i++;

            // Соседние куски сливаем, пока промежуток невелик. Пересечения сюда же:
            // после расширения полями соседние фразы часто налезают друг на друга,
            // и без слияния один и тот же звук попал бы в склейку дважды.
            while (i < sorted.Count && sorted[i].Start - end <= mergeGapMs)
            {
                end = Math.Max(end, sorted[i].End);
                i++;
            }

            pieces.Add(new Piece(start, end, mapped));
            mapped += end - start;
        }

        return new SpeechMap(pieces);
    }

    /// <summary>
    /// Переводит отрезок из времени склейки обратно в время встречи.
    ///
    /// Отрезок может лечь на стык двух кусков — движок про склейку не знает и мог
    /// протянуть говорящего через шов. Тогда он распадается на несколько: растянуть
    /// его на всю дырку между кусками нельзя, там была тишина, а не речь.
    /// </summary>
    public IEnumerable<(long StartMs, long EndMs)> ToSource(long mappedStartMs, long mappedEndMs)
    {
        if (mappedEndMs <= mappedStartMs) yield break;

        foreach (var piece in _pieces)
        {
            var from = Math.Max(mappedStartMs, piece.MappedStartMs);
            var to = Math.Min(mappedEndMs, piece.MappedEndMs);
            if (to <= from) continue;

            var offset = piece.SourceStartMs - piece.MappedStartMs;
            yield return (from + offset, to + offset);
        }
    }

    /// <summary>
    /// Стоит ли вообще связываться со склейкой. Если речь занимает почти всю запись,
    /// выигрыша не будет, а лишние швы разметке только вредят.
    /// </summary>
    public bool WorthTrimming(long totalDurationMs) =>
        totalDurationMs > 0
        && _pieces.Count > 0
        && MappedDurationMs < totalDurationMs * 0.8;
}
