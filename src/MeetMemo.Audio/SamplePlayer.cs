using NAudio.Wave;

namespace MeetMemo.Audio;

/// <summary>
/// Проигрывание коротких образцов речи — чтобы опознать голос на слух.
///
/// Отпечаток это 512 чисел, на глаз его не проверить: человек убеждается, что перед ним
/// именно Елена Петровна, только услышав фразу. Играем в устройство по умолчанию:
/// это проверка на слух, а не часть записи встречи.
/// </summary>
public sealed class SamplePlayer : IDisposable
{
    private WaveOutEvent? _output;
    private IDisposable? _reader;

    /// <summary>Идёт ли воспроизведение прямо сейчас.</summary>
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    /// <summary>Проигрывает WAV-файл. Повторный вызов обрывает предыдущий.</summary>
    public void Play(string path)
    {
        Stop();

        if (!File.Exists(path)) return;

        var reader = new WaveFileReader(path);
        _reader = reader;

        _output = new WaveOutEvent();
        _output.Init(reader);
        _output.Play();
    }

    /// <summary>Проигрывает отсчёты из памяти — фразу, которая только что прозвучала.</summary>
    public void Play(float[] samples, int sampleRate = 16000)
    {
        Stop();

        if (samples.Length == 0) return;

        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        var stream = new RawSourceWaveStream(
            new MemoryStream(bytes), new WaveFormat(sampleRate, 16, 1));
        _reader = stream;

        _output = new WaveOutEvent();
        _output.Init(stream);
        _output.Play();
    }

    public void Stop()
    {
        try
        {
            _output?.Stop();
            _output?.Dispose();
            _reader?.Dispose();
        }
        catch (Exception)
        {
            // Устройство могло исчезнуть (отключили наушники) — для проверки на слух
            // это не повод падать.
        }
        finally
        {
            _output = null;
            _reader = null;
        }
    }

    public void Dispose() => Stop();
}
