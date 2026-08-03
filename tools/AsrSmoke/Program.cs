using System.Diagnostics;
using MeetMemo.Asr;
using MeetMemo.Contracts;

// Смоук-проверка распознавания: подаём WAV, получаем текст и real-time factor.
// Нужна, чтобы отделить проблемы движка от проблем аудиотракта.
//   AsrSmoke <путь-к-wav>

var modelsRoot = AsrModelCatalog.DefaultModelsRoot;
var manager = new ModelManager(modelsRoot);

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"Каталог моделей: {modelsRoot}");

foreach (var model in AsrModelCatalog.Required)
    Console.WriteLine($"  {(manager.IsInstalled(model) ? "OK  " : "НЕТ ")} {model.DisplayName}");

if (manager.GetMissing().Count > 0)
{
    Console.WriteLine("Модели не установлены. Запустите загрузку в приложении.");
    return 2;
}

if (args.Length == 0)
{
    Console.WriteLine("Модели на месте. Укажите WAV-файл для распознавания.");
    return 0;
}

var wavPath = args[0];
if (!File.Exists(wavPath))
{
    Console.WriteLine($"Файл не найден: {wavPath}");
    return 1;
}

var (samples, sampleRate, channels) = WavReader.Read(wavPath);
var audioSeconds = samples.Length / (double)channels / sampleRate;
Console.WriteLine($"Вход: {audioSeconds:F1} с, {sampleRate} Гц, {channels} кан.");

// Приводим к тому, что ждёт модель: моно 16 кГц.
var mono = channels == 1 ? samples : Downmix(samples, channels);
var resampled = AsrFeeder.Resample(mono, sampleRate, 16000);

using var transcriber = new LiveTranscriber(AsrModelCatalog.GigaAmCtc, modelsRoot);

var initSw = Stopwatch.StartNew();
transcriber.Initialize();
initSw.Stop();
Console.WriteLine($"Модель загружена за {initSw.ElapsedMilliseconds} мс");

var segments = new List<RecognizedSegment>();
var done = new ManualResetEventSlim(false);
transcriber.SegmentReady += seg =>
{
    lock (segments) segments.Add(seg);
    Console.WriteLine($"  [{seg.StartMs / 1000.0:F1}-{seg.EndMs / 1000.0:F1}s] {seg.Text}");
};
transcriber.Failed += ex => Console.WriteLine($"ОШИБКА: {ex.Message}");

transcriber.AddChannel(AudioChannel.Microphone);

Console.WriteLine("Распознавание...");
var sw = Stopwatch.StartNew();

// Подаём кусками по 0.5 с — так же, как это делает живой аудиотракт.
const int chunk = 8000;
for (var i = 0; i < resampled.Length; i += chunk)
{
    var len = Math.Min(chunk, resampled.Length - i);
    var slice = new float[len];
    Array.Copy(resampled, i, slice, 0, len);
    transcriber.Push(AudioChannel.Microphone, slice, 0);
}

await transcriber.FlushAsync();
sw.Stop();

var rtf = sw.Elapsed.TotalSeconds / audioSeconds;
Console.WriteLine();
Console.WriteLine($"Сегментов: {segments.Count}");
Console.WriteLine($"Время обработки: {sw.Elapsed.TotalSeconds:F2} с");
Console.WriteLine($"Real-time factor: {rtf:F3} {(rtf < 1 ? "(укладываемся в реальное время)" : "(МЕДЛЕННЕЕ реального времени)")}");

return 0;

static float[] Downmix(float[] samples, int channels)
{
    var frames = samples.Length / channels;
    var mono = new float[frames];
    for (var i = 0; i < frames; i++)
    {
        var sum = 0f;
        for (var c = 0; c < channels; c++) sum += samples[i * channels + c];
        mono[i] = sum / channels;
    }
    return mono;
}

/// <summary>Минимальный читатель WAV: только то, что нужно смоук-тесту.</summary>
internal static class WavReader
{
    public static (float[] Samples, int SampleRate, int Channels) Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Не RIFF-файл");
        br.ReadUInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Не WAVE-файл");

        int sampleRate = 0, channels = 0, bits = 0;
        ushort format = 1;

        while (fs.Position < fs.Length)
        {
            var chunkId = new string(br.ReadChars(4));
            var chunkSize = br.ReadUInt32();

            if (chunkId == "fmt ")
            {
                format = br.ReadUInt16();
                channels = br.ReadUInt16();
                sampleRate = (int)br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt16();
                bits = br.ReadUInt16();
                if (chunkSize > 16) br.ReadBytes((int)chunkSize - 16);
            }
            else if (chunkId == "data")
            {
                var bytes = br.ReadBytes((int)chunkSize);
                return (Decode(bytes, bits, format), sampleRate, channels);
            }
            else
            {
                br.ReadBytes((int)chunkSize);
            }
        }

        throw new InvalidDataException("В файле нет блока data");
    }

    private static float[] Decode(byte[] bytes, int bits, ushort format)
    {
        if (format == 3 && bits == 32)
        {
            var result = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        if (bits == 16)
        {
            var result = new float[bytes.Length / 2];
            for (var i = 0; i < result.Length; i++)
                result[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            return result;
        }

        throw new NotSupportedException($"Формат WAV не поддержан: {bits} бит, тег {format}");
    }
}
