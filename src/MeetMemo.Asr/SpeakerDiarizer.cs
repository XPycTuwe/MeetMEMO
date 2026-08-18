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
        _modelsRoot = string.IsNullOrWhiteSpace(modelsRoot)
            ? AsrModelCatalog.DefaultModelsRoot
            : modelsRoot;
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

        // Кластеризация всё равно дробит людей: один и тот же человек попадает
        // в несколько «голосов». Сливаем те, что звучат одинаково, и выбрасываем обрывки.
        voices = await Task.Run(() => MergeSimilarVoices(audioPath, voices, ct), ct)
            .ConfigureAwait(false);

        var speakers = voices.Select(v => v.Speaker).Distinct().Count();

        // Один голос размечать бессмысленно: пометка «spk1» на каждой реплике не добавляет
        // ничего к тому, что уже сказано каналом источника.
        if (speakers < 2) return speakers;

        // Кого из различённых голосов мы уже знаем по имени.
        var names = await Task.Run(() => RecognizeKnownVoices(audioPath, voices, ct), ct)
            .ConfigureAwait(false);

        var annotated = segments
            .Select(s =>
            {
                if (s.Source is not (AudioChannel.Application or AudioChannel.System)) return s;

                var label = AssignSpeaker(s, voices);
                if (label is null) return s with { Speaker = null };

                // Имя вместо «spk2», если этот голос знаком: приложение узнало человека,
                // значит и стенограмма должна называть его по имени.
                return s with { Speaker = names.GetValueOrDefault(label, label) };
            })
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

    /// <summary>
    /// Насколько похожими должны звучать два кластера, чтобы счесть их одним человеком.
    /// Порог мягче, чем при узнавании по памяти: там нужно не спутать людей между собой,
    /// здесь — собрать одного человека из кусков, на которые его разрезала кластеризация.
    /// </summary>
    private const float SameVoiceSimilarity = 0.62f;

    /// <summary>
    /// Короткий кусок речи, зажатый между репликами одного и того же голоса, почти
    /// наверняка принадлежит ему же: человек сделал паузу, а кластеризация успела
    /// завести новый голос. Такие куски присоединяем к соседям.
    ///
    /// Отбрасывать короткие реплики нельзя: «Паша, возьмёшься?» — «Да» это согласие
    /// на поручение, самая ценная секунда встречи, и она может быть единственной
    /// репликой человека за весь разговор.
    /// </summary>
    private static readonly TimeSpan ShortPieceLimit = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Сливает голоса, звучащие одинаково, и отбрасывает мимолётные.
    ///
    /// Кластеризация настроена осторожно и охотнее заводит новый голос, чем ошибается
    /// слиянием. На часовой встрече это давало десяток «собеседников» вместо нескольких
    /// человек. Здесь считаем отпечаток каждого кластера и объединяем похожие — то же
    /// сравнение тембров, только между собой, а не с памятью.
    /// </summary>
    private IReadOnlyList<VoiceSegment> MergeSimilarVoices(
        string audioPath, IReadOnlyList<VoiceSegment> voices, CancellationToken ct)
    {
        try
        {
            using var embedder = new VoiceEmbedder(_modelsRoot, _log);
            if (!embedder.Ready) return voices;

            var samples = ReadMonoResampled(audioPath, 16000);
            var prints = BuildVoicePrints(embedder, samples, voices, ct);
            if (prints.Count < 2) return voices;

            // Каждому кластеру ищем самый ранний похожий и приписываем его номер.
            var merged = new Dictionary<int, int>();
            foreach (var (speaker, print) in prints.OrderBy(p => p.Key))
            {
                var twin = merged.Keys
                    .Where(known => prints.ContainsKey(known))
                    .FirstOrDefault(known =>
                        VoicePrintStore.CosineSimilarity(prints[known], print) >= SameVoiceSimilarity);

                merged[speaker] = merged.TryGetValue(twin, out var root) && twin != speaker
                    ? root
                    : speaker;
            }

            var byNewSpeaker = voices
                .Select(v => v with { Speaker = merged.GetValueOrDefault(v.Speaker, v.Speaker) })
                .ToList();

            var survivors = AbsorbShortPieces(byNewSpeaker);

            // Перенумеровываем по порядку появления: после слияния уцелевшие номера
            // разрежены, и на встрече из пяти человек получался «Собеседник 17».
            var renumbered = survivors
                .OrderBy(v => v.StartMs)
                .Select(v => v.Speaker)
                .Distinct()
                .Select((speaker, index) => (speaker, index))
                .ToDictionary(p => p.speaker, p => p.index);

            var result = survivors
                .Select(v => v with { Speaker = renumbered[v.Speaker] })
                .ToList();

            _log.LogInformation(
                "Голоса после слияния: {After} из {Before}",
                result.Select(v => v.Speaker).Distinct().Count(),
                voices.Select(v => v.Speaker).Distinct().Count());

            return result.Count > 0 ? result : voices;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Слить похожие голоса не удалось");
            return voices;
        }
    }

    /// <summary>
    /// Присоединяет короткие куски к соседям, когда до и после говорил один и тот же
    /// голос. Это чинит частый случай: человек говорит долго, посреди его речи пауза,
    /// и кластеризация заводит на этот кусок отдельный «голос».
    ///
    /// Одиночные короткие реплики — «да», «согласен», «принял» — не трогаем: соседи
    /// у них разные, и это действительно чей-то ответ, часто самый важный на встрече.
    /// </summary>
    public static List<VoiceSegment> AbsorbShortPieces(IReadOnlyList<VoiceSegment> voices)
    {
        var ordered = voices.OrderBy(v => v.StartMs).ToList();
        var result = new List<VoiceSegment>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var duration = current.EndMs - current.StartMs;

            if (duration <= ShortPieceLimit.TotalMilliseconds
                && i > 0 && i < ordered.Count - 1
                && ordered[i - 1].Speaker == ordered[i + 1].Speaker
                && ordered[i - 1].Speaker != current.Speaker)
            {
                // Окружён одним голосом с обеих сторон — значит это он и есть.
                result.Add(current with { Speaker = ordered[i - 1].Speaker });
                continue;
            }

            result.Add(current);
        }

        return result;
    }

    /// <summary>Отпечаток каждого голоса — по самому длинному его куску речи.</summary>
    private static Dictionary<int, float[]> BuildVoicePrints(
        VoiceEmbedder embedder, float[] samples,
        IReadOnlyList<VoiceSegment> voices, CancellationToken ct)
    {
        var prints = new Dictionary<int, float[]>();

        foreach (var group in voices.GroupBy(v => v.Speaker))
        {
            ct.ThrowIfCancellationRequested();

            var longest = group.MaxBy(v => v.EndMs - v.StartMs);
            if (longest is null) continue;

            var start = (int)(longest.StartMs * 16);
            var length = (int)((longest.EndMs - longest.StartMs) * 16);

            if (start < 0 || start >= samples.Length) continue;
            length = Math.Min(length, samples.Length - start);
            if (length < 16000) continue;

            var piece = new float[length];
            Array.Copy(samples, start, piece, 0, length);

            var embedding = embedder.Compute(piece);
            if (embedding is not null) prints[group.Key] = embedding;
        }

        return prints;
    }

    /// <summary>
    /// Сопоставляет различённые голоса с памятью: «spk2» → «Елена Петрова».
    ///
    /// Берём самый длинный кусок речи каждого голоса — на нём тембр слышен лучше всего,
    /// а короткие реплики вроде «да, согласен» узнаются плохо. Возвращает только тех,
    /// кого удалось опознать: остальные останутся «Собеседником N», и это честно.
    /// </summary>
    private Dictionary<string, string> RecognizeKnownVoices(
        string audioPath, IReadOnlyList<VoiceSegment> voices, CancellationToken ct)
    {
        var names = new Dictionary<string, string>();

        try
        {
            var store = new VoicePrintStore();
            if (store.Count == 0) return names;

            using var embedder = new VoiceEmbedder(_modelsRoot, _log);
            if (!embedder.Ready) return names;

            var samples = ReadMonoResampled(audioPath, 16000);

            foreach (var group in voices.GroupBy(v => v.Speaker))
            {
                ct.ThrowIfCancellationRequested();

                var longest = group.MaxBy(v => v.EndMs - v.StartMs);
                if (longest is null) continue;

                var start = (int)(longest.StartMs * 16);          // мс → отсчёты на 16 кГц
                var length = (int)((longest.EndMs - longest.StartMs) * 16);

                if (start < 0 || start >= samples.Length) continue;
                length = Math.Min(length, samples.Length - start);
                if (length < 16000) continue;                      // короче секунды не берём

                var piece = new float[length];
                Array.Copy(samples, start, piece, 0, length);

                var embedding = embedder.Compute(piece);
                if (embedding is null) continue;

                var hit = store.Recognize(embedding);
                if (hit is null) continue;

                names[$"spk{group.Key + 1}"] = hit.Value.Print.Name;

                _log.LogInformation("Голос spk{Index} опознан как {Name} (похожесть {Score:N2})",
                    group.Key + 1, hit.Value.Print.Name, hit.Value.Similarity);
            }
        }
        catch (Exception ex)
        {
            // Не опознали — не беда: метки «Собеседник N» останутся как есть.
            _log.LogWarning(ex, "Не удалось сверить голоса с памятью");
        }

        return names;
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
        // На живой встрече 0.5 дробил одного человека на десятки «собеседников»:
        // на записи из Teams получалось 29 голосов там, где людей было пятеро.
        // Проверено на часовой записи: 0.5 → 29 голосов, 0.65 → 17, 0.8 → 13, 0.9 → 11.
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = 0.8f;

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
