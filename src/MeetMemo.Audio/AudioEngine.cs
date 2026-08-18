using MeetMemo.Contracts;
using MeetMemo.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Audio;

/// <summary>
/// Аудиотракт сессии: микрофон + звук приложения (или системный), общая шкала времени,
/// раздача порций потребителям. Захват идёт всегда — он нужен распознаванию; запись
/// на диск подключается отдельным тапом и может быть выключена настройкой (ТЗ 8.2).
/// </summary>
public sealed class AudioEngine : ISessionParticipant, IAudioSourceSwitchable, ICriticalParticipant, IDisposable
{
    private static readonly TimeSpan SilenceWarningAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Через сколько молчания дорожки приложения считать, что мы слушаем не то.
    ///
    /// Изоляция по дереву процессов работает не везде: Teams из Microsoft Store играет
    /// звук мимо своего дерева, и захват формально удаётся, а в дорожке тишина. Понять
    /// это по одной паузе нельзя — на встрече бывает тихо, — но если за полминуты
    /// не пришло ни одного звука, слушаем мы явно не тот процесс.
    /// </summary>
    private static readonly TimeSpan DeadChannelAfter = TimeSpan.FromSeconds(30);

    private readonly List<IAudioTap> _taps = new();
    private readonly ILogger<AudioEngine> _log;
    private readonly object _tapGate = new();

    private DeviceCapture? _microphone;
    private DeviceCapture? _systemLoopback;
    private ProcessLoopbackCapture? _processLoopback;

    private SessionContext? _context;
    private WaveFileTap? _micFile;
    private WaveFileTap? _appFile;

    private long _micSilenceSinceMs = -1;
    private long _appSilenceSinceMs = -1;
    private bool _micSilenceReported;
    private bool _appSilenceReported;
    private bool _paused;

    public AudioEngine(ILogger<AudioEngine>? log = null)
        => _log = log ?? NullLogger<AudioEngine>.Instance;

    public string Name => "Аудио";

    /// <summary>Останавливается последним: запись звука ценнее всех вспомогательных функций.</summary>
    public int StopOrder => 100;

    public AudioMode CurrentMode { get; private set; } = AudioMode.ApplicationProcessTree;

    /// <summary>Текущие пиковые уровни для индикаторов панели (0..1).</summary>
    public float MicrophonePeak { get; private set; }

    public float ApplicationPeak { get; private set; }

    /// <summary>Подписка потребителей: запись на диск, подача в распознавание, метры.</summary>
    public void AddTap(IAudioTap tap)
    {
        lock (_tapGate) _taps.Add(tap);
    }

    public void RemoveTap(IAudioTap tap)
    {
        lock (_tapGate) _taps.Remove(tap);
    }

    public Task StartAsync(SessionContext context, CancellationToken ct)
    {
        _context = context;
        CurrentMode = context.Request.AudioMode;
        _paused = false;

        StartMicrophone(context);

        if (CurrentMode != AudioMode.MicrophoneOnly)
        {
            StartApplicationAudio(context);
        }

        return Task.CompletedTask;
    }

    private void StartMicrophone(SessionContext context)
    {
        try
        {
            _microphone = new DeviceCapture(_log);
            _microphone.DataAvailable += samples => Dispatch(AudioChannel.Microphone, samples,
                _microphone!.SampleRate, _microphone.Channels);
            _microphone.Failed += ex => OnCaptureFailed(AudioChannel.Microphone, ex);
            _microphone.StartBestMicrophone(context.Request.MicrophoneDeviceId);

            // Замена устройства должна быть видна в пакете: иначе непонятно, чей голос записан.
            if (_microphone.SubstitutedFrom is { } replaced)
            {
                context.Events.Emit(context.Clock, EventTypes.AudioSourceChanged, EventSeverity.Warning,
                    new Dictionary<string, string>
                    {
                        ["channel"] = "microphone",
                        ["requested"] = replaced,
                        ["used"] = _microphone.ActiveDeviceName ?? "неизвестно",
                        ["reason"] = "выбранное устройство не запустилось"
                    });
            }

            if (context.Request.SaveAudioFiles)
            {
                var folder = new Storage.MeetingFolder(context.FolderPath);
                _micFile = new WaveFileTap(folder.MicrophoneAudio(), AudioChannel.Microphone,
                    _microphone.SampleRate, _microphone.Channels);
                AddTap(_micFile);
            }
        }
        catch (Exception ex)
        {
            // Отсутствие микрофона не блокирует старт: встреча может идти только по звуку
            // приложения. Пользователь предупреждён на экране подтверждения (ТЗ 17.2).
            _log.LogError(ex, "Не удалось запустить микрофон");
            _microphone = null;
            context.Events.Emit(context.Clock, EventTypes.AudioSourceChanged, EventSeverity.Warning,
                new Dictionary<string, string> { ["microphone"] = "unavailable", ["error"] = ex.Message });
        }
    }

    private void StartApplicationAudio(SessionContext context)
    {
        var pid = context.Request.Target?.ProcessId ?? 0;

        if (CurrentMode == AudioMode.ApplicationProcessTree && pid > 0)
        {
            try
            {
                _processLoopback = new ProcessLoopbackCapture(pid, includeProcessTree: true, _log);
                _processLoopback.DataAvailable += samples => Dispatch(AudioChannel.Application, samples,
                    _processLoopback!.Format_SampleRate, _processLoopback.Format_Channels);
                _processLoopback.Failed += ex => OnCaptureFailed(AudioChannel.Application, ex);
                _processLoopback.Start();

                CreateApplicationFileTap(context,
                    _processLoopback.Format_SampleRate, _processLoopback.Format_Channels);
                return;
            }
            catch (Exception ex)
            {
                // Изоляция по процессу может быть недоступна (устаревшая сборка Windows,
                // защищённый процесс) — сразу уходим на общий системный звук, не теряя встречу.
                _log.LogWarning(ex, "Process loopback недоступен для PID {Pid}, переключаюсь на системный звук", pid);
                _processLoopback?.Dispose();
                _processLoopback = null;
                CurrentMode = AudioMode.System;
                context.Events.Emit(context.Clock, EventTypes.BackendFallback, EventSeverity.Warning,
                    new Dictionary<string, string>
                    {
                        ["from"] = "application_process_tree",
                        ["to"] = "system",
                        ["reason"] = ex.Message
                    });
            }
        }

        StartSystemLoopback(context);
    }

    private void StartSystemLoopback(SessionContext context)
    {
        try
        {
            _systemLoopback = new DeviceCapture(_log);
            _systemLoopback.DataAvailable += samples => Dispatch(AudioChannel.Application, samples,
                _systemLoopback!.SampleRate, _systemLoopback.Channels);
            _systemLoopback.Failed += ex => OnCaptureFailed(AudioChannel.Application, ex);
            _systemLoopback.StartSystemLoopback();
            CurrentMode = AudioMode.System;

            CreateApplicationFileTap(context, _systemLoopback.SampleRate, _systemLoopback.Channels);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось запустить системный loopback");
            _systemLoopback = null;
        }
    }

    private void CreateApplicationFileTap(SessionContext context, int sampleRate, int channels)
    {
        if (!context.Request.SaveAudioFiles || _appFile is not null) return;

        var folder = new Storage.MeetingFolder(context.FolderPath);
        _appFile = new WaveFileTap(folder.ApplicationAudio(), AudioChannel.Application, sampleRate, channels);
        AddTap(_appFile);
    }

    private void Dispatch(AudioChannel channel, float[] samples, int sampleRate, int channels)
    {
        if (_context is null || _paused || samples.Length == 0) return;

        var buffer = new AudioBuffer
        {
            Channel = channel,
            OffsetMs = _context.Clock.ElapsedMs,
            Samples = samples,
            SampleRate = sampleRate,
            Channels = channels
        };

        var peak = buffer.Peak;
        if (channel == AudioChannel.Microphone) MicrophonePeak = peak;
        else ApplicationPeak = peak;

        TrackSilence(channel, peak, buffer.OffsetMs);

        IAudioTap[] snapshot;
        lock (_tapGate) snapshot = _taps.ToArray();

        foreach (var tap in snapshot)
        {
            try
            {
                tap.OnBuffer(buffer);
            }
            catch (Exception ex) when (!tap.IsCritical)
            {
                // Сбой некритичного потребителя (распознавание, индикатор) не должен
                // прерывать запись — это прямое требование ТЗ 16.
                _log.LogError(ex, "Потребитель звука {Tap} выбросил исключение", tap.Name);
            }
        }
    }

    /// <summary>Тишина дольше 10 секунд — неблокирующее предупреждение (ТЗ 8.3).</summary>
    private void TrackSilence(AudioChannel channel, float peak, long offsetMs)
    {
        const float threshold = 0.002f;
        var silent = peak < threshold;

        ref var since = ref _micSilenceSinceMs;
        ref var reported = ref _micSilenceReported;
        if (channel != AudioChannel.Microphone)
        {
            since = ref _appSilenceSinceMs;
            reported = ref _appSilenceReported;
        }

        if (!silent)
        {
            since = -1;
            reported = false;
            if (channel != AudioChannel.Microphone) _appEverHadSound = true;
            return;
        }

        // Дорожка приложения молчит с самого начала записи — переключаемся на общий
        // системный звук, не дожидаясь конца встречи и пустой стенограммы.
        if (channel != AudioChannel.Microphone
            && !_appEverHadSound
            && !_deadChannelHandled
            && CurrentMode == AudioMode.ApplicationProcessTree
            && offsetMs >= DeadChannelAfter.TotalMilliseconds)
        {
            _deadChannelHandled = true;
            SwitchToSystemAudio("в звуке приложения за полминуты не было ни звука");
            return;
        }

        if (since < 0) { since = offsetMs; return; }
        if (reported || offsetMs - since < SilenceWarningAfter.TotalMilliseconds) return;

        reported = true;
        _context?.Events.Emit(_context.Clock, EventTypes.AudioSilence, EventSeverity.Warning,
            new Dictionary<string, string>
            {
                ["channel"] = channel.ToString(),
                ["seconds"] = ((offsetMs - since) / 1000).ToString()
            });
    }

    /// <summary>Был ли в дорожке приложения хоть какой-то звук с начала записи.</summary>
    private bool _appEverHadSound;

    private bool _deadChannelHandled;

    /// <summary>
    /// Переводит запись на общий системный звук, не прерывая встречу. Плата за это —
    /// потеря изоляции: в дорожку попадёт и музыка, и уведомления. Зато встреча
    /// перестаёт записываться в тишину.
    /// </summary>
    private void SwitchToSystemAudio(string reason)
    {
        if (_context is null) return;

        _log.LogWarning("Переключаюсь на общий системный звук: {Reason}", reason);

        try
        {
            _processLoopback?.Dispose();
            _processLoopback = null;
            StartSystemLoopback(_context);

            _context.Events.Emit(_context.Clock, EventTypes.AudioSourceChanged,
                EventSeverity.Warning,
                new Dictionary<string, string>
                {
                    ["from"] = AudioMode.ApplicationProcessTree.ToString(),
                    ["to"] = AudioMode.System.ToString(),
                    ["reason"] = reason
                });

            AudioSourceSwitched?.Invoke(reason);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось перейти на общий системный звук");
        }
    }

    /// <summary>
    /// Запись перешла на общий системный звук. Человек должен об этом узнать: изоляции
    /// больше нет, и в дорожку теперь попадает всё, что звучит на компьютере.
    /// </summary>
    public event Action<string>? AudioSourceSwitched;

    private void OnCaptureFailed(AudioChannel channel, Exception ex)
    {
        _log.LogError(ex, "Канал {Channel} отвалился", channel);
        _context?.Events.Emit(_context.Clock, EventTypes.AudioOverrun, EventSeverity.Error,
            new Dictionary<string, string> { ["channel"] = channel.ToString(), ["error"] = ex.Message });

        // Звук приложения можно вернуть переключением на общий системный без остановки сессии.
        if (channel == AudioChannel.Application && _context is not null && CurrentMode != AudioMode.System)
        {
            try
            {
                _processLoopback?.Dispose();
                _processLoopback = null;
                StartSystemLoopback(_context);
            }
            catch (Exception fallbackEx)
            {
                _log.LogError(fallbackEx, "Резервный системный звук тоже не запустился");
            }
        }
    }

    public Task SwitchAsync(AudioMode mode, CancellationToken ct)
    {
        if (_context is null || mode == CurrentMode) return Task.CompletedTask;

        _processLoopback?.Dispose();
        _processLoopback = null;
        _systemLoopback?.Dispose();
        _systemLoopback = null;

        CurrentMode = mode;
        if (mode != AudioMode.MicrophoneOnly) StartApplicationAudio(_context);

        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        // Паузу делаем на уровне раздачи: устройства остаются открытыми, новых файлов
        // не создаётся, а разрыв фиксируется событием (ТЗ 7.2).
        _paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct)
    {
        _paused = false;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _microphone?.Stop();
        _processLoopback?.Stop();
        _systemLoopback?.Stop();

        // Файлы закрываем после остановки захвата: заголовок WAV дописывается корректно.
        if (_micFile is not null) { RemoveTap(_micFile); _micFile.Dispose(); _micFile = null; }
        if (_appFile is not null) { RemoveTap(_appFile); _appFile.Dispose(); _appFile = null; }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _microphone?.Dispose();
        _processLoopback?.Dispose();
        _systemLoopback?.Dispose();
        _micFile?.Dispose();
        _appFile?.Dispose();
    }
}
