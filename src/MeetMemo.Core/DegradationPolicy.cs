using MeetMemo.Contracts;

namespace MeetMemo.Core;

/// <summary>
/// Уровни деградации в порядке отключения из ТЗ 17.1:
/// предпросмотр → финальный ASR → автоснимки → снижение профиля чернового ASR.
/// Захват аудио, живая стенограмма и временная шкала сохраняются максимально долго.
/// </summary>
public enum DegradationLevel
{
    /// <summary>Всё работает.</summary>
    None = 0,

    /// <summary>Отключён предпросмотр (живые миниатюры, метры высокой частоты).</summary>
    NoPreview = 1,

    /// <summary>Дополнительно отключён финальный проход ASR.</summary>
    NoFinalPass = 2,

    /// <summary>Дополнительно отключены автоснимки.</summary>
    NoAutoScreenshots = 3,

    /// <summary>Дополнительно снижен профиль чернового ASR (более лёгкая модель/реже сегменты).</summary>
    ReducedAsr = 4
}

/// <summary>Причина деградации — попадает в timeline и в карточку завершения.</summary>
public enum DegradationReason
{
    None,
    CpuPressure,
    DiskSpaceLow,
    AsrQueueBacklog,
    Manual
}

/// <summary>
/// Единственный владелец решения о деградации (ТЗ 17.1). Подсистемы только подписываются:
/// монитор диска, ASR-очередь и загрузка CPU сообщают давление, политика выдаёт уровень.
/// </summary>
public sealed class DegradationPolicy
{
    private readonly object _gate = new();
    private DegradationLevel _level = DegradationLevel.None;

    public event Action<DegradationLevel, DegradationReason>? Changed;

    public DegradationLevel Level
    {
        get { lock (_gate) return _level; }
    }

    public bool IsPreviewAllowed => Level < DegradationLevel.NoPreview;

    public bool IsFinalPassAllowed => Level < DegradationLevel.NoFinalPass;

    public bool AreAutoScreenshotsAllowed => Level < DegradationLevel.NoAutoScreenshots;

    public bool IsFullAsrProfileAllowed => Level < DegradationLevel.ReducedAsr;

    /// <summary>Поднять уровень деградации. Снижение уровня выполняется только через <see cref="Recover"/>.</summary>
    public void Escalate(DegradationLevel target, DegradationReason reason)
    {
        DegradationLevel applied;
        lock (_gate)
        {
            if (target <= _level) return;
            _level = target;
            applied = target;
        }

        Changed?.Invoke(applied, reason);
    }

    /// <summary>Вернуться на уровень ниже, когда давление снято (например, освободилось место).</summary>
    public void Recover(DegradationLevel target, DegradationReason reason)
    {
        DegradationLevel applied;
        lock (_gate)
        {
            if (target >= _level) return;
            _level = target;
            applied = target;
        }

        Changed?.Invoke(applied, reason);
    }

    public void Reset() => Recover(DegradationLevel.None, DegradationReason.None);

    public static string Describe(DegradationLevel level) => level switch
    {
        DegradationLevel.None => "полный режим",
        DegradationLevel.NoPreview => "отключён предпросмотр",
        DegradationLevel.NoFinalPass => "отключён финальный проход распознавания",
        DegradationLevel.NoAutoScreenshots => "отключены автоснимки",
        DegradationLevel.ReducedAsr => "снижен профиль распознавания",
        _ => level.ToString()
    };
}
