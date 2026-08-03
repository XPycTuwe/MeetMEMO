using System.Text;
using MeetMemo.Contracts;

namespace MeetMemo.Audio;

/// <summary>
/// Запись канала в WAV. Заголовок дописывается при закрытии, но реальный размер данных
/// известен из sidecar-файла: после аварийного завершения WAV чинится по нему, а не теряется.
/// Сохранение аудио — отключаемая опция, поэтому этот тап может вообще не создаваться.
/// </summary>
public sealed class WaveFileTap : IAudioTap, IDisposable
{
    private const int BytesPerSample = 2; // 16-bit PCM: вдвое меньше места, качества хватает

    private readonly FileStream _stream;
    private readonly string _sidecarPath;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly object _gate = new();
    private long _dataBytes;
    private long _lastSidecarFlush;
    private bool _disposed;

    public WaveFileTap(string path, AudioChannel channel, int sampleRate, int channels)
    {
        Channel = channel;
        Name = $"wav:{channel}";
        _sampleRate = sampleRate;
        _channels = channels;
        _sidecarPath = path + ".len";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 16384);
        WriteHeaderPlaceholder();
        Path_ = path;
    }

    public string Path_ { get; }

    public AudioChannel Channel { get; }

    public string Name { get; }

    /// <summary>Диск критичен: его порции не дропаются никогда (ТЗ 8.3 — потеря данных недопустима).</summary>
    public bool IsCritical => true;

    public void OnBuffer(AudioBuffer buffer)
    {
        if (buffer.Channel != Channel) return;

        lock (_gate)
        {
            if (_disposed) return;

            var samples = buffer.Samples;
            var bytes = new byte[samples.Length * BytesPerSample];
            for (var i = 0; i < samples.Length; i++)
            {
                var clamped = Math.Clamp(samples[i], -1f, 1f);
                var value = (short)(clamped * short.MaxValue);
                bytes[i * 2] = (byte)(value & 0xFF);
                bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            _stream.Write(bytes);
            _dataBytes += bytes.Length;

            // Раз в ~5 секунд фиксируем длину: этого достаточно, чтобы починить файл после аварии.
            if (_dataBytes - _lastSidecarFlush > _sampleRate * _channels * BytesPerSample * 5)
            {
                _stream.Flush();
                WriteSidecar();
                _lastSidecarFlush = _dataBytes;
            }
        }
    }

    private void WriteSidecar()
    {
        try
        {
            File.WriteAllText(_sidecarPath,
                $"{{\"data_bytes\":{_dataBytes},\"sample_rate\":{_sampleRate},\"channels\":{_channels}}}",
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Sidecar — вспомогательный файл; его недоступность не должна прерывать запись.
        }
    }

    private void WriteHeaderPlaceholder()
    {
        // Заголовок с нулевыми размерами; корректные значения проставим при закрытии.
        WriteHeader(0);
    }

    private void WriteHeader(long dataBytes)
    {
        var byteRate = _sampleRate * _channels * BytesPerSample;
        var blockAlign = (short)(_channels * BytesPerSample);

        _stream.Position = 0;
        using var w = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        w.Write("RIFF"u8.ToArray());
        w.Write((uint)(36 + dataBytes));
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16u);
        w.Write((short)1);              // PCM
        w.Write((short)_channels);
        w.Write((uint)_sampleRate);
        w.Write((uint)byteRate);
        w.Write(blockAlign);
        w.Write((short)(BytesPerSample * 8));
        w.Write("data"u8.ToArray());
        w.Write((uint)dataBytes);
        w.Flush();
    }

    public TimeSpan Duration =>
        TimeSpan.FromSeconds(_dataBytes / (double)(_sampleRate * _channels * BytesPerSample));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _stream.Flush();
            var dataBytes = _dataBytes;
            WriteHeader(dataBytes);
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();

            // Файл закрыт корректно — метка длины больше не нужна.
            try { if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Починка WAV, оставшегося после аварийного завершения: в заголовке нули, но данные на месте.
    /// Длину берём из sidecar, а если его нет — из фактического размера файла.
    /// </summary>
    public static bool TryRepair(string wavPath)
    {
        if (!File.Exists(wavPath)) return false;

        var sidecar = wavPath + ".len";
        long dataBytes;
        int sampleRate = 48000, channels = 2;

        var fileLength = new FileInfo(wavPath).Length;
        if (fileLength <= 44) return false;

        if (File.Exists(sidecar))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecar));
                dataBytes = doc.RootElement.GetProperty("data_bytes").GetInt64();
                sampleRate = doc.RootElement.GetProperty("sample_rate").GetInt32();
                channels = doc.RootElement.GetProperty("channels").GetInt32();
                dataBytes = Math.Min(dataBytes, fileLength - 44);
            }
            catch (Exception)
            {
                dataBytes = fileLength - 44;
            }
        }
        else
        {
            dataBytes = fileLength - 44;
        }

        using (var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var byteRate = sampleRate * channels * BytesPerSample;
            fs.Position = 0;
            using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
            w.Write("RIFF"u8.ToArray());
            w.Write((uint)(36 + dataBytes));
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16u);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write((uint)sampleRate);
            w.Write((uint)byteRate);
            w.Write((short)(channels * BytesPerSample));
            w.Write((short)(BytesPerSample * 8));
            w.Write("data"u8.ToArray());
            w.Write((uint)dataBytes);
        }

        try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch (IOException) { }
        return true;
    }
}
