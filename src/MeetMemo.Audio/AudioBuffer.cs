using MeetMemo.Contracts;

namespace MeetMemo.Audio;

/// <summary>
/// Порция звука одного канала со штампом единой шкалы сессии. Данные — float32 mono/stereo
/// в исходной частоте устройства; ресемплинг для распознавания делает AsrFeeder.
/// </summary>
public sealed record AudioBuffer
{
    public required AudioChannel Channel { get; init; }

    /// <summary>Смещение начала порции от старта сессии (ТЗ 11.1).</summary>
    public required long OffsetMs { get; init; }

    public required float[] Samples { get; init; }

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public double DurationMs => Samples.Length / (double)Channels / SampleRate * 1000.0;

    /// <summary>Пиковый уровень порции — для индикатора и детекции тишины/клиппинга.</summary>
    public float Peak
    {
        get
        {
            var peak = 0f;
            foreach (var s in Samples)
            {
                var abs = Math.Abs(s);
                if (abs > peak) peak = abs;
            }
            return peak;
        }
    }

    /// <summary>Сводит многоканальный буфер в моно — вход для распознавания.</summary>
    public float[] ToMono()
    {
        if (Channels == 1) return Samples;

        var frames = Samples.Length / Channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < Channels; c++) sum += Samples[i * Channels + c];
            mono[i] = sum / Channels;
        }
        return mono;
    }
}

/// <summary>Потребитель звука: запись на диск, подача в распознавание, индикаторы уровня.</summary>
public interface IAudioTap
{
    string Name { get; }

    /// <summary>
    /// true — потребитель критичен и его переполнение недопустимо (запись на диск).
    /// false — при перегрузке его порции можно дропать (ASR, индикаторы), но не тишину диска.
    /// </summary>
    bool IsCritical { get; }

    void OnBuffer(AudioBuffer buffer);
}
