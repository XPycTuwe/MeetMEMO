using System.Collections.Concurrent;
using System.Threading.Channels;
using MeetMemo.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace MeetMemo.Asr;

/// <summary>
/// Готовый к записи результат распознавания одной фразы.
///
/// Вместе с текстом отдаются и сами отсчёты: по ним берётся отпечаток голоса, чтобы
/// узнать говорящего. Массив не копируется — читающий не должен его менять.
/// </summary>
public sealed record RecognizedSegment(
    AudioChannel Channel, long StartMs, long EndMs, string Text, float[] Samples);

/// <summary>
/// Живая стенограмма на готовом движке sherpa-onnx: Silero VAD режет поток на фразы,
/// офлайн-модель GigaAM распознаёт каждую. Такой подход даёт лучший открытый русский WER
/// и укладывается в требование «сегменты не реже раза в 5 с» (ТЗ 10.2).
///
/// Нарезанные фразы кладутся в очередь, а разбирает её один фоновый поток. Это даёт
/// три нужных свойства: модель никогда не вызывается из двух потоков сразу, перегрузка
/// не плодит неограниченное число задач, а остановка детерминирована — сначала
/// дорабатывает очередь, потом освобождается нативная память.
///
/// Ограничение выбранной модели: текст без пунктуации и заглавных букв. Читаемую версию
/// даёт финальный проход Whisper и мемо, собранное в Claude.
/// </summary>
public sealed class LiveTranscriber : IDisposable
{
    private const int SampleRate = 16000;
    private const int MaxQueuedSegments = 64;

    private readonly AsrModelDescriptor _model;
    private readonly string _modelsRoot;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<AudioChannel, ChannelPipeline> _channels = new();

    private readonly Channel<PendingSegment> _queue =
        Channel.CreateBounded<PendingSegment>(new BoundedChannelOptions(MaxQueuedSegments)
        {
            SingleReader = true,
            SingleWriter = false,
            // Переполнение очереди означает, что распознавание отстаёт от речи. Выкидываем
            // самое старое: свежий текст на экране полезнее, чем полный, но опоздавший.
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private OfflineRecognizer? _recognizer;
    private Task? _worker;
    private CancellationTokenSource? _workerCts;
    private volatile bool _disposed;

    public LiveTranscriber(AsrModelDescriptor model, string modelsRoot, ILogger? log = null)
    {
        _model = model;
        _modelsRoot = modelsRoot;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Распознанная фраза готова к записи в transcript.jsonl.</summary>
    public event Action<RecognizedSegment>? SegmentReady;

    /// <summary>Отказ распознавания. Запись при этом продолжается — требование ТЗ 16.</summary>
    public event Action<Exception>? Failed;

    public bool IsReady => _recognizer is not null;

    public string ModelName => _model.DisplayName;

    /// <summary>Сколько фраз потеряно из-за переполнения очереди — попадает в диагностику.</summary>
    public long DroppedSegments { get; private set; }

    public static bool IsModelInstalled(AsrModelDescriptor model, string modelsRoot)
    {
        var dir = Path.Combine(modelsRoot, model.FolderName);
        return model.Files.All(f => File.Exists(Path.Combine(dir, f.Name)));
    }

    private static string VadModelPath(string modelsRoot) => Path.Combine(
        modelsRoot, AsrModelCatalog.SileroVad.FolderName, AsrModelCatalog.SileroVad.ModelFile);

    public void Initialize()
    {
        var dir = Path.Combine(_modelsRoot, _model.FolderName);
        var modelPath = Path.Combine(dir, _model.ModelFile);
        var tokensPath = Path.Combine(dir, _model.TokensFile);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Файл модели не найден: {modelPath}");
        if (!File.Exists(tokensPath))
            throw new FileNotFoundException($"Файл словаря не найден: {tokensPath}");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = tokensPath;
        // Два потока: распознавание не должно вытеснять поток захвата звука.
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        switch (_model.Kind)
        {
            case AsrModelKind.NemoCtc:
                config.ModelConfig.NeMoCtc.Model = modelPath;
                break;

            case AsrModelKind.Transducer:
                config.ModelConfig.Transducer.Encoder = modelPath;
                config.ModelConfig.Transducer.Decoder = Path.Combine(dir, "decoder.int8.onnx");
                config.ModelConfig.Transducer.Joiner = Path.Combine(dir, "joiner.int8.onnx");
                break;

            default:
                throw new NotSupportedException($"Тип модели {_model.Kind} не поддерживается");
        }

        _recognizer = new OfflineRecognizer(config);

        _workerCts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoopAsync(_workerCts.Token));

        _log.LogInformation("Распознавание готово: {Model}", _model.DisplayName);
    }

    /// <summary>Подключает канал: под каждый — свой VAD, чтобы фразы каналов не смешивались.</summary>
    public void AddChannel(AudioChannel channel)
    {
        if (_channels.ContainsKey(channel)) return;

        var vadConfig = new VadModelConfig();
        vadConfig.SileroVad.Model = VadModelPath(_modelsRoot);
        vadConfig.SileroVad.Threshold = 0.5f;
        vadConfig.SileroVad.MinSilenceDuration = 0.4f;   // пауза, по которой режем фразу
        vadConfig.SileroVad.MinSpeechDuration = 0.25f;
        vadConfig.SileroVad.MaxSpeechDuration = 15f;     // длинную речь режем принудительно
        vadConfig.SampleRate = SampleRate;
        vadConfig.NumThreads = 1;
        vadConfig.Provider = "cpu";
        vadConfig.Debug = 0;

        var vad = new VoiceActivityDetector(vadConfig, bufferSizeInSeconds: 60);
        _channels[channel] = new ChannelPipeline(channel, vad);
        _log.LogInformation("Канал {Channel} подключён к распознаванию", channel);
    }

    /// <summary>
    /// Приём очередной порции 16 кГц mono из аудиотракта. Здесь только нарезка по VAD —
    /// тяжёлое распознавание выполняет фоновый потребитель очереди.
    /// </summary>
    public void Push(AudioChannel channel, float[] samples16k, long offsetMs)
    {
        if (_disposed || !_channels.TryGetValue(channel, out var pipeline)) return;

        try
        {
            lock (pipeline.VadGate)
            {
                if (pipeline.StreamStartOffsetMs < 0) pipeline.StreamStartOffsetMs = offsetMs;

                pipeline.Vad.AcceptWaveform(samples16k);
                DrainVad(pipeline);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка нарезки речи в канале {Channel}", channel);
            Failed?.Invoke(ex);
        }
    }

    private void DrainVad(ChannelPipeline pipeline)
    {
        while (!pipeline.Vad.IsEmpty())
        {
            var segment = pipeline.Vad.Front();
            pipeline.Vad.Pop();

            var startMs = pipeline.StreamStartOffsetMs + SamplesToMs(segment.Start);
            var endMs = startMs + SamplesToMs(segment.Samples.Length);

            var pending = new PendingSegment(pipeline.Channel, segment.Samples, startMs, endMs);
            if (!_queue.Writer.TryWrite(pending)) DroppedSegments++;
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Recognize(item);
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Поток распознавания завершился аварийно");
            Failed?.Invoke(ex);
        }
    }

    private void Recognize(PendingSegment item)
    {
        var recognizer = _recognizer;
        if (recognizer is null || _disposed) return;

        try
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, item.Samples);
            recognizer.Decode(stream);
            var text = stream.Result.Text?.Trim();

            if (!string.IsNullOrWhiteSpace(text))
                SegmentReady?.Invoke(new RecognizedSegment(
                    item.Channel, item.StartMs, item.EndMs, text, item.Samples));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Распознавание фразы в канале {Channel} не удалось", item.Channel);
            Failed?.Invoke(ex);
        }
    }

    /// <summary>
    /// Досылает остатки из VAD и дожидается, пока очередь опустеет. Вызывается при остановке
    /// сессии: последняя фраза встречи не должна потеряться.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        foreach (var pipeline in _channels.Values)
        {
            try
            {
                lock (pipeline.VadGate)
                {
                    pipeline.Vad.Flush();
                    DrainVad(pipeline);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Не удалось дослать остаток канала {Channel}", pipeline.Channel);
            }
        }

        // Закрываем очередь и ждём, пока потребитель разберёт всё до конца.
        _queue.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogWarning("Очередь распознавания не разобрана за 30 с");
            }
            catch (OperationCanceledException) { }
        }
    }

    private static long SamplesToMs(int samples) => samples * 1000L / SampleRate;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Порядок важен: сначала останавливаем потребителя, потом освобождаем нативные
        // объекты. Иначе Decode может обратиться к уже освобождённой памяти.
        _queue.Writer.TryComplete();
        try { _worker?.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { }

        _workerCts?.Cancel();
        _workerCts?.Dispose();
        _workerCts = null;
        _worker = null;

        foreach (var pipeline in _channels.Values)
        {
            try { pipeline.Vad.Dispose(); } catch { }
        }
        _channels.Clear();

        _recognizer?.Dispose();
        _recognizer = null;
    }

    private sealed record PendingSegment(
        AudioChannel Channel, float[] Samples, long StartMs, long EndMs);

    private sealed class ChannelPipeline
    {
        public ChannelPipeline(AudioChannel channel, VoiceActivityDetector vad)
        {
            Channel = channel;
            Vad = vad;
        }

        public AudioChannel Channel { get; }
        public VoiceActivityDetector Vad { get; }
        public object VadGate { get; } = new();
        public long StreamStartOffsetMs { get; set; } = -1;
    }
}
