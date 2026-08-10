using MeetMemo.Contracts;
using MeetMemo.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using SherpaOnnx;

namespace MeetMemo.Asr;

/// <summary>
/// Различение собеседников по голосу в записи звука приложения.
///
/// Читать «кто говорит» из интерфейса встречи ненадёжно: у каждого приложения своя
/// вёрстка, и любое обновление её ломает. Поэтому — диаризация: pyannote находит,
/// где кто-то говорит, отпечатки голосов группируют находки в «spk1, spk2, …».
/// Дорожка микрофона в этом не участвует: она целиком принадлежит владельцу компьютера,
/// что уже даёт главное разделение «я / остальные».
///
/// Метки — тембры, а не имена. Имя можно узнать только из самой речи, и этим займётся
/// модель, собирающая мемо: «Елена, что скажешь?» перед репликой spk2 связывает их.
/// </summary>
public sealed class SpeakerDiarizer
{
    private readonly string _modelsRoot;
    private readonly ILogger _log;

    public SpeakerDiarizer(string? modelsRoot = null, ILogger? log = null)
    {
        _modelsRoot = modelsRoot ?? AsrModelCatalog.DefaultModelsRoot;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Обе модели диаризации на месте — можно запускать.</summary>
    public bool ModelsInstalled
    {
        get
        {
            var manager = new ModelManager(_modelsRoot);
            return manager.IsInstalled(AsrModelCatalog.PyannoteSegmentation)
                && manager.IsInstalled(AsrModelCatalog.SpeakerEmbedding);
        }
    }

    /// <summary>Голосовой сегмент: кто (spk-индекс) и когда говорил.</summary>
    public sealed record VoiceSegment(long StartMs, long EndMs, int Speaker);

    /// <summary>
    /// Размечает говорящих в записи встречи: диаризует звук приложения, проставляет
    /// метки в transcript.jsonl и пересобирает transcript.md. Возвращает число
    /// различённых голосов, 0 — если размечать нечего.
    /// </summary>
    public async Task<int> AnnotateMeetingAsync(string folderPath, CancellationToken ct = default)
    {
        var folder = new MeetingFolder(folderPath);

        var audioPath = File.Exists(folder.ApplicationAudio())
            ? folder.ApplicationAudio()
            : folder.ApplicationAudio("mp3");
        if (!File.Exists(audioPath)) return 0;

        var segments = JsonlWriter.ReadAll<TranscriptSegment>(folder.TranscriptJsonl);
        if (!segments.Any(s => s.Source is AudioChannel.Application or AudioChannel.System))
            return 0;

        var voices = await Task.Run(() => Diarize(audioPath, ct), ct).ConfigureAwait(false);
        if (voices.Count == 0) return 0;

        var speakers = voices.Select(v => v.Speaker).Distinct().Count();

        // Один голос размечать бессмысленно: пометка «spk1» на каждой реплике не добавляет
        // ничего к тому, что уже сказано каналом источника.
        if (speakers < 2) return speakers;

        var annotated = segments
            .Select(s => s.Source is AudioChannel.Application or AudioChannel.System
                ? s with { Speaker = AssignSpeaker(s, voices) }
                : s)
            .ToList();

        // Переписываем атомарно: упавшая на середине запись не должна съесть стенограмму.
        var temp = folder.TranscriptJsonl + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);   // писатель открывает файл на дозапись
        await using (var writer = new JsonlWriter(temp))
        {
            foreach (var segment in annotated) await writer.AppendAsync(segment, ct).ConfigureAwait(false);
        }
        File.Move(temp, folder.TranscriptJsonl, overwrite: true);

        RerenderMarkdown(folder);

        _log.LogInformation("Диаризация: {Speakers} голосов на {Segments} сегментов",
            speakers, annotated.Count);
        return speakers;
    }

    /// <summary>
    /// Метка для сегмента стенограммы — голос с наибольшим перекрытием по времени.
    /// Сегменты распознавания и диаризации режут речь по-разному, поэтому точного
    /// совпадения границ не бывает; побеждает большинство пересечённых миллисекунд.
    /// </summary>
    public static string? AssignSpeaker(TranscriptSegment segment, IReadOnlyList<VoiceSegment> voices)
    {
        var overlapBySpeaker = new Dictionary<int, long>();

        foreach (var voice in voices)
        {
            var overlap = Math.Min(segment.EndMs, voice.EndMs) - Math.Max(segment.StartMs, voice.StartMs);
            if (overlap <= 0) continue;

            overlapBySpeaker[voice.Speaker] =
                overlapBySpeaker.GetValueOrDefault(voice.Speaker) + overlap;
        }

        if (overlapBySpeaker.Count == 0) return null;

        var best = overlapBySpeaker.MaxBy(p => p.Value);

        // Слишком слабое перекрытие — это шум на границе, а не совпадение: честнее
        // оставить сегмент неразмеченным, чем приписать его случайному голосу.
        var duration = segment.EndMs - segment.StartMs;
        if (duration > 0 && best.Value * 5 < duration) return null;

        return $"spk{best.Key + 1}";
    }

    private IReadOnlyList<VoiceSegment> Diarize(string audioPath, CancellationToken ct)
    {
        var manager = new ModelManager(_modelsRoot);

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = Path.Combine(
            manager.GetModelDirectory(AsrModelCatalog.PyannoteSegmentation),
            AsrModelCatalog.PyannoteSegmentation.ModelFile);
        config.Segmentation.NumThreads = 2;
        config.Segmentation.Provider = "cpu";
        config.Embedding.Model = Path.Combine(
            manager.GetModelDirectory(AsrModelCatalog.SpeakerEmbedding),
            AsrModelCatalog.SpeakerEmbedding.ModelFile);
        config.Embedding.NumThreads = 2;
        config.Embedding.Provider = "cpu";

        // Число собеседников заранее неизвестно — кластеризация сама решает по порогу.
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = 0.5f;

        // Реплики короче полусекунды и паузы короче — дребезг, а не смена говорящего.
        config.MinDurationOn = 0.5f;
        config.MinDurationOff = 0.5f;

        using var diarizer = new OfflineSpeakerDiarization(config);

        var samples = ReadMonoResampled(audioPath, diarizer.SampleRate);
        ct.ThrowIfCancellationRequested();

        var result = diarizer.Process(samples);

        return result
            .Select(s => new VoiceSegment((long)(s.Start * 1000), (long)(s.End * 1000), s.Speaker))
            .OrderBy(s => s.StartMs)
            .ToList();
    }

    /// <summary>
    /// Читает аудио в моно с частотой движка. Свежие записи и так 16 кГц моно, но старые
    /// бывают 96 кГц стерео, а сжатые лежат в MP3. Понижение — усреднением с фазовым
    /// накопителем: простое прореживание даёт слышимый призвук, который путает отпечатки.
    /// </summary>
    private static float[] ReadMonoResampled(string path, int targetRate)
    {
        var isMp3 = Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase);

        // К этому моменту дорожка обычно уже ужата в MP3 — читаем её через Media Foundation,
        // а он требует явного запуска (повторный вызов безвреден).
        if (isMp3) NAudio.MediaFoundation.MediaFoundationApi.Startup();

        using WaveStream reader = isMp3
            ? new MediaFoundationReader(path)
            : new WaveFileReader(path);

        var provider = reader.ToSampleProvider();
        var channels = provider.WaveFormat.Channels;
        var sourceRate = provider.WaveFormat.SampleRate;
        var decimation = sourceRate / (double)targetRate;

        var output = new List<float>(
            (int)(reader.TotalTime.TotalSeconds * targetRate) + targetRate);

        var frame = new float[channels];
        var buffer = new float[channels * 4096];
        double phase = 0, sum = 0;
        var summed = 0;

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i + channels <= read; i += channels)
            {
                double mono = 0;
                for (var c = 0; c < channels; c++) mono += buffer[i + c];
                mono /= channels;

                if (decimation <= 1.0)
                {
                    output.Add((float)mono);
                    continue;
                }

                sum += mono;
                summed++;
                phase += 1;
                if (phase < decimation) continue;

                phase -= decimation;
                output.Add((float)(sum / summed));
                sum = 0;
                summed = 0;
            }
        }

        _ = frame;
        return output.ToArray();
    }

    private static void RerenderMarkdown(MeetingFolder folder)
    {
        try
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<SessionManifest>(
                File.ReadAllText(folder.SessionJson), JsonSetup.Compact);
            if (manifest is not null) TranscriptRenderer.Render(folder, manifest);
        }
        catch (Exception)
        {
            // Markdown производен от jsonl и пересобирается в любой момент; его сбой
            // не должен отменять уже проставленную разметку.
        }
    }
}
