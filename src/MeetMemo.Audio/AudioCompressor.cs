using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MeetMemo.Audio;

/// <summary>
/// Сжатие записанных дорожек в MP3 и обратное чтение.
///
/// Час речи в WAV занимает больше сотни мегабайт, и папки встреч разрастаются до гигабайтов.
/// В MP3 тот же час — около двадцати мегабайт, а на слух речь не меняется: на 64 кбит/с
/// моно потери приходятся на частоты, которых в голосе почти нет.
///
/// Кодировщик берётся из Media Foundation — он есть в самой Windows, ставить ничего не нужно.
/// </summary>
public static class AudioCompressor
{
    /// <summary>Хватает для разборчивой речи; выше поднимать смысла нет.</summary>
    public const int BitrateBytesPerSecond = 64000 / 8;

    private static bool _started;
    private static readonly object Gate = new();

    private static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            MediaFoundationApi.Startup();
            _started = true;
        }
    }

    /// <summary>
    /// Сжимает WAV в MP3 рядом с ним и удаляет исходник. Возвращает путь к MP3,
    /// либо null, если сжать не удалось — тогда WAV остаётся нетронутым: потерять
    /// запись встречи из-за неудачной экономии места недопустимо.
    /// </summary>
    public static string? CompressInPlace(string wavPath)
    {
        if (!File.Exists(wavPath)) return null;

        var mp3Path = Path.ChangeExtension(wavPath, ".mp3");

        try
        {
            EnsureStarted();

            using (var reader = new WaveFileReader(wavPath))
            {
                // Пустую дорожку кодировать не во что: Media Foundation на ней падает.
                if (reader.Length == 0) return null;

                // MP3 не бывает выше 48 кГц, а микрофоны отдают и 96 — на таком файле
                // кодировщик молча отказывается работать. Заодно сводим в моно.
                var format = reader.WaveFormat;
                MediaFoundationResampler? resampler = null;
                try
                {
                    IWaveProvider source = reader;
                    if (format.SampleRate > 48000 || format.Channels > 1)
                    {
                        resampler = new MediaFoundationResampler(
                            reader, new WaveFormat(WaveFileTap.TargetSampleRate, 16, 1))
                        {
                            ResamplerQuality = 60
                        };
                        source = resampler;
                    }

                    MediaFoundationEncoder.EncodeToMp3(source, mp3Path, BitrateBytesPerSecond);
                }
                finally
                {
                    resampler?.Dispose();
                }
            }

            // Удаляем исходник только после того, как MP3 закрыт и действительно на месте.
            if (!File.Exists(mp3Path) || new FileInfo(mp3Path).Length == 0)
            {
                TryDelete(mp3Path);
                return null;
            }

            File.Delete(wavPath);
            return mp3Path;
        }
        catch (Exception)
        {
            TryDelete(mp3Path);
            return null;
        }
    }

    /// <summary>
    /// Разворачивает MP3 во временный WAV: финальный проход распознавания читает
    /// только WAV. Вызывающий обязан удалить файл после использования.
    /// </summary>
    public static string DecodeToTempWav(string mp3Path)
    {
        EnsureStarted();

        var temp = Path.Combine(Path.GetTempPath(),
            $"meetmemo-{Path.GetFileNameWithoutExtension(mp3Path)}-{Guid.NewGuid():N}.wav");

        using var reader = new MediaFoundationReader(mp3Path);
        WaveFileWriter.CreateWaveFile(temp, reader);
        return temp;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
