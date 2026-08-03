using MeetMemo.Audio;
using MeetMemo.Contracts;

namespace MeetMemo.Asr;

/// <summary>
/// Мост между аудиотрактом и распознаванием: сводит в моно и приводит к 16 кГц —
/// формату, который ждут русские модели. Распознавание некритично: при перегрузке его
/// порции дропаются, а запись на диск и шкала времени не страдают (ТЗ 16, 17.1).
/// </summary>
public sealed class AsrFeeder : IAudioTap
{
    private const int TargetSampleRate = 16000;

    private readonly AudioChannel _channel;
    private readonly Action<float[], long> _onResampled;
    private readonly int _maxQueuedSamples;
    private int _queuedSamples;

    public AsrFeeder(AudioChannel channel, Action<float[], long> onResampled, int maxQueuedSeconds = 30)
    {
        _channel = channel;
        _onResampled = onResampled;
        _maxQueuedSamples = TargetSampleRate * maxQueuedSeconds;
    }

    public string Name => $"asr-feeder:{_channel}";

    /// <summary>Некритичный потребитель — его можно душить при нехватке ресурсов.</summary>
    public bool IsCritical => false;

    /// <summary>Сколько порций отброшено из-за перегрузки — попадает в диагностику.</summary>
    public long DroppedBuffers { get; private set; }

    public void OnBuffer(AudioBuffer buffer)
    {
        if (buffer.Channel != _channel) return;

        // Очередь распознавания переполнена: лучше потерять кусок черновика,
        // чем тормозить поток захвата и получить пропуски в записи.
        if (Volatile.Read(ref _queuedSamples) > _maxQueuedSamples)
        {
            DroppedBuffers++;
            return;
        }

        var mono = buffer.ToMono();
        var resampled = Resample(mono, buffer.SampleRate, TargetSampleRate);
        if (resampled.Length == 0) return;

        Interlocked.Add(ref _queuedSamples, resampled.Length);
        try
        {
            _onResampled(resampled, buffer.OffsetMs);
        }
        finally
        {
            Interlocked.Add(ref _queuedSamples, -resampled.Length);
        }
    }

    /// <summary>
    /// Линейная интерполяция 48000 → 16000. Для речевых моделей этого достаточно:
    /// они всё равно считают лог-мел-спектрограмму, а не анализируют форму волны.
    /// </summary>
    public static float[] Resample(float[] input, int sourceRate, int targetRate)
    {
        if (input.Length == 0) return Array.Empty<float>();
        if (sourceRate == targetRate) return input;

        var ratio = (double)sourceRate / targetRate;
        var outputLength = (int)(input.Length / ratio);
        if (outputLength <= 0) return Array.Empty<float>();

        var output = new float[outputLength];
        for (var i = 0; i < outputLength; i++)
        {
            var srcPos = i * ratio;
            var idx = (int)srcPos;
            var frac = (float)(srcPos - idx);

            output[i] = idx + 1 < input.Length
                ? input[idx] * (1 - frac) + input[idx + 1] * frac
                : input[idx];
        }

        return output;
    }
}
