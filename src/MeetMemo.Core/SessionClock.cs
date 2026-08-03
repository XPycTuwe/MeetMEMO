using System.Diagnostics;

namespace MeetMemo.Core;

/// <summary>
/// Единая шкала времени сессии (ТЗ 11.1). Относительные таймкоды берутся из монотонных часов
/// процесса, поэтому перевод системных часов во время встречи не нарушает порядок событий.
/// Владелец шкалы один — все подсистемы (аудио, снимки, ASR) штампуют события через него.
/// </summary>
public interface ISessionClock
{
    /// <summary>Момент старта в UTC — только для отображения и session.json.</summary>
    DateTimeOffset StartUtc { get; }

    /// <summary>Момент старта в локальном времени со смещением.</summary>
    DateTimeOffset StartLocal { get; }

    /// <summary>Значение монотонного счётчика на старте (ТЗ: monotonic_origin).</summary>
    long MonotonicOrigin { get; }

    /// <summary>Миллисекунд от старта сессии. Строго неубывающая величина.</summary>
    long ElapsedMs { get; }

    /// <summary>Локальное время, соответствующее смещению (для отображения в индексах).</summary>
    DateTimeOffset ToLocal(long offsetMs);
}

/// <summary>Реализация на <see cref="Stopwatch.GetTimestamp"/> — не зависит от системных часов.</summary>
public sealed class SessionClock : ISessionClock
{
    private readonly long _origin;

    public SessionClock(TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        _origin = Stopwatch.GetTimestamp();
        StartUtc = tp.GetUtcNow();
        StartLocal = tp.GetLocalNow();
        TimezoneId = TimeZoneInfo.Local.Id;
    }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset StartLocal { get; }

    public string TimezoneId { get; }

    public long MonotonicOrigin => _origin;

    public long ElapsedMs =>
        (long)((Stopwatch.GetTimestamp() - _origin) * 1000.0 / Stopwatch.Frequency);

    public DateTimeOffset ToLocal(long offsetMs) => StartLocal.AddMilliseconds(offsetMs);
}
