using MeetMemo.Audio;
using MeetMemo.Contracts;
using MeetMemo.Core;
using MeetMemo.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Asr;

/// <summary>
/// Подсистема живой стенограммы: подключается к аудиотракту, распознаёт речь обоих каналов
/// и дозаписывает сегменты в transcript.jsonl по ходу встречи (P0 по ТЗ 10.2).
///
/// Отказ распознавания не критичен для сессии: запись звука и временная шкала продолжаются,
/// а в пакете остаётся предупреждение. Это прямое требование ТЗ 16.
/// </summary>
public sealed class AsrEngine : ISessionParticipant, IDisposable
{
    private readonly MeetingSessionStore _store;
    private readonly string _modelsRoot;
    private readonly AsrModelDescriptor _model;
    private readonly ILogger<AsrEngine> _log;

    private LiveTranscriber? _transcriber;
    private AudioEngine? _audio;
    private SessionContext? _context;
    private AsrFeeder? _micFeeder;
    private AsrFeeder? _appFeeder;
    private volatile bool _degraded;

    public AsrEngine(
        MeetingSessionStore store,
        AudioEngine audio,
        string? modelsRoot = null,
        AsrModelDescriptor? model = null,
        ILogger<AsrEngine>? log = null)
    {
        _store = store;
        _audio = audio;
        _modelsRoot = modelsRoot ?? AsrModelCatalog.DefaultModelsRoot;
        _model = model ?? AsrModelCatalog.GigaAmCtc;
        _log = log ?? NullLogger<AsrEngine>.Instance;
    }

    public string Name => "Распознавание";

    /// <summary>Останавливается перед аудио, но после всего остального: успевает дослать хвост.</summary>
    public int StopOrder => 90;

    public int SegmentCount { get; private set; }

    public bool IsRunning => _transcriber?.IsReady == true && !_degraded;

    /// <summary>Живой текст для панели — подписывается UI.</summary>
    public event Action<RecognizedSegment>? SegmentRecognized;

    /// <summary>
    /// Кто-то заговорил, звука уже достаточно для узнавания голоса. Приходит примерно
    /// через секунду после первого слова, а не после того, как человек договорит.
    /// </summary>
    public event Action<AudioChannel, float[]>? SpeechStarted;

    public Task StartAsync(SessionContext context, CancellationToken ct)
    {
        _context = context;
        _degraded = false;
        SegmentCount = 0;

        try
        {
            _transcriber = new LiveTranscriber(_model, _modelsRoot, _log);
            _transcriber.SegmentReady += OnSegmentReady;
            _transcriber.SpeechStarted += (channel, head) => SpeechStarted?.Invoke(channel, head);
            _transcriber.Failed += OnTranscriberFailed;
            _transcriber.Initialize();

            _transcriber.AddChannel(AudioChannel.Microphone);
            _micFeeder = new AsrFeeder(AudioChannel.Microphone,
                (samples, offset) => _transcriber?.Push(AudioChannel.Microphone, samples, offset));
            _audio?.AddTap(_micFeeder);

            if (context.Request.AudioMode != AudioMode.MicrophoneOnly)
            {
                _transcriber.AddChannel(AudioChannel.Application);
                _appFeeder = new AsrFeeder(AudioChannel.Application,
                    (samples, offset) => _transcriber?.Push(AudioChannel.Application, samples, offset));
                _audio?.AddTap(_appFeeder);
            }

            _log.LogInformation("Живая стенограмма запущена на модели {Model}", _model.DisplayName);
        }
        catch (Exception ex)
        {
            // Без распознавания встреча всё равно записывается — это лишь деградация функции.
            _log.LogError(ex, "Не удалось запустить распознавание");
            _degraded = true;
            context.Events.Emit(context.Clock, EventTypes.BackendFallback, EventSeverity.Error,
                new Dictionary<string, string>
                {
                    ["component"] = "asr",
                    ["reason"] = ex.Message,
                    ["effect"] = "живая стенограмма недоступна, запись продолжается"
                });
        }

        return Task.CompletedTask;
    }

    private void OnSegmentReady(RecognizedSegment segment)
    {
        var writer = _store.TranscriptWriter;
        if (writer is null || _context is null) return;

        var record = new TranscriptSegment
        {
            StartMs = segment.StartMs,
            EndMs = segment.EndMs,
            Source = segment.Channel,
            Text = segment.Text,
            Language = "ru",
            Final = false,
            Engine = "sherpa-onnx/" + _model.Id
        };

        try
        {
            // Ждём завершения записи: очередь распознавания и так однопоточная,
            // а порядок строк в файле должен соответствовать порядку фраз.
            writer.AppendAsync(record).GetAwaiter().GetResult();
            SegmentCount++;

            _context.Events.Emit(_context.Clock, EventTypes.TranscriptionSegmentCreated,
                ("source", ChannelName(segment.Channel)),
                ("start_ms", segment.StartMs.ToString()));

            SegmentRecognized?.Invoke(segment);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось записать сегмент стенограммы");
        }
    }

    /// <summary>
    /// Имена каналов в событиях должны совпадать с тем, что записано в transcript.jsonl:
    /// нижний регистр. Значения в data — строки, поэтому конвертер перечислений их не касается.
    /// </summary>
    private static string ChannelName(AudioChannel channel) => channel switch
    {
        AudioChannel.Microphone => "microphone",
        AudioChannel.Application => "application",
        AudioChannel.System => "system",
        _ => channel.ToString().ToLowerInvariant()
    };

    private void OnTranscriberFailed(Exception ex)
    {
        if (_context is null) return;

        _context.Events.Emit(_context.Clock, EventTypes.BackendFallback, EventSeverity.Warning,
            new Dictionary<string, string> { ["component"] = "asr", ["reason"] = ex.Message });
    }

    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken ct)
    {
        // Сначала отключаемся от аудио, потом дожидаемся разбора очереди: так в стенограмму
        // попадает последняя фраза встречи и не добавляется ничего нового.
        if (_micFeeder is not null) { _audio?.RemoveTap(_micFeeder); _micFeeder = null; }
        if (_appFeeder is not null) { _audio?.RemoveTap(_appFeeder); _appFeeder = null; }

        if (_transcriber is not null)
        {
            try
            {
                await _transcriber.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка при завершении распознавания");
            }

            if (_transcriber.DroppedSegments > 0 && _context is not null)
            {
                _context.Events.Emit(_context.Clock, EventTypes.DegradationApplied, EventSeverity.Warning,
                    new Dictionary<string, string>
                    {
                        ["component"] = "asr",
                        ["dropped_segments"] = _transcriber.DroppedSegments.ToString()
                    });
            }

            _transcriber.Dispose();
            _transcriber = null;
        }

        if (_context is not null)
            _context.Events.Emit(_context.Clock, EventTypes.TranscriptionFinalized,
                ("segments", SegmentCount.ToString()));
    }

    public void Dispose()
    {
        _transcriber?.Dispose();
        _transcriber = null;
    }
}
