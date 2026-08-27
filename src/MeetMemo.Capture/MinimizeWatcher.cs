namespace MeetMemo.Capture;

/// <summary>
/// Решает, когда сказать человеку про свёрнутое окно встречи.
///
/// Свёрнутое окно система не рисует и кадров не отдаёт — автоснимки на это время встают.
/// Молчать нельзя: пропажу замечали уже после встречи, по пустой папке. Но и говорить
/// на каждое сворачивание нельзя тоже — за час их набирается много, и подсказка
/// превращается в шум.
///
/// Отсюда два правила. Первое: между предупреждениями выдерживаем паузу. Второе:
/// про возвращение окна говорим только если про пропажу успели сказать — иначе выходило
/// бы «снимки снова идут» там, где никто не жаловался, что они встали.
/// </summary>
public sealed class MinimizeWatcher
{
    private readonly long _cooldownMs;

    private bool _minimized;
    private bool _warned;

    /// <summary>Когда предупреждали в прошлый раз. Пусто — ещё ни разу за встречу.</summary>
    private long? _lastWarnMs;

    public MinimizeWatcher(TimeSpan cooldown) => _cooldownMs = (long)cooldown.TotalMilliseconds;

    /// <summary>
    /// Принимает текущее состояние окна и время от начала встречи.
    /// </summary>
    /// <returns>
    /// <c>true</c> — сказать, что окно свёрнуто; <c>false</c> — что вернулось;
    /// <c>null</c> — говорить нечего.
    /// </returns>
    public bool? Update(bool minimized, long nowMs)
    {
        if (minimized == _minimized) return null;
        _minimized = minimized;

        if (minimized)
        {
            if (_lastWarnMs is { } last && nowMs - last < _cooldownMs) return null;

            _lastWarnMs = nowMs;
            _warned = true;
            return true;
        }

        if (!_warned) return null;

        _warned = false;
        return false;
    }

    public void Reset()
    {
        _minimized = false;
        _warned = false;
        _lastWarnMs = null;
    }
}
