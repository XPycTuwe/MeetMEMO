using System.Threading.Channels;
using MeetMemo.Contracts;
using MeetMemo.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Storage;

/// <summary>
/// Хранилище пакета встречи: создаёт папку, ведёт timeline.jsonl и session.json, финализирует.
/// Единственный писатель timeline — внутренняя очередь, поэтому строки не перемешиваются
/// даже когда события приходят из потоков аудио, захвата окна и ASR одновременно (ТЗ 11.2).
/// </summary>
public sealed class MeetingSessionStore : ISessionStore, IAsyncDisposable
{
    private readonly ILogger<MeetingSessionStore> _log;
    private readonly Channel<TimelineEvent> _events =
        Channel.CreateUnbounded<TimelineEvent>(new UnboundedChannelOptions { SingleReader = true });

    private MeetingFolder? _folder;
    private JsonlWriter? _timeline;
    private SessionManifest? _manifest;
    private ISessionClock? _clock;
    private Task? _drain;
    private SessionLock? _lock;

    public MeetingSessionStore(ILogger<MeetingSessionStore>? log = null)
        => _log = log ?? NullLogger<MeetingSessionStore>.Instance;

    public MeetingFolder? Folder => _folder;

    /// <summary>Писатель transcript.jsonl — отдаётся ASR-подсистеме, чтобы был один общий канал записи.</summary>
    public JsonlWriter? TranscriptWriter { get; private set; }

    public int SegmentCount => (int)(TranscriptWriter?.Count ?? 0);

    public int ScreenshotCount { get; set; }

    public async Task<SessionHandle> CreateSessionAsync(
        SessionStartRequest request, ISessionClock clock, CancellationToken ct)
    {
        _clock = clock;
        var sessionId = Guid.NewGuid().ToString();

        _folder = MeetingFolderFactory.Create(request.MeetingsRoot, clock.StartLocal, request.Title);
        _folder.EnsureCreated(withAudio: request.SaveAudioFiles);

        // Метка живой сессии: по ней при следующем запуске находим незакрытые встречи (ТЗ 11.3).
        _lock = SessionLock.Acquire(_folder.LockFile, sessionId);

        _manifest = new SessionManifest
        {
            SessionId = sessionId,
            Title = request.Title,
            StartUtc = clock.StartUtc,
            StartLocal = clock.StartLocal,
            Timezone = TimeZoneInfo.Local.Id,
            MonotonicOrigin = clock.MonotonicOrigin,
            Status = SessionStatus.Recording,
            AppVersion = typeof(MeetingSessionStore).Assembly.GetName().Version?.ToString(),
            Target = request.Target is null ? null : new TargetInfo
            {
                Application = request.Target.ApplicationName,
                ProcessIdAtStart = request.Target.ProcessId,
                WindowTitleAtStart = request.Target.WindowTitle,
                Executable = request.Target.ExecutablePath
            },
            Audio = new AudioInfo
            {
                Mode = request.AudioMode,
                MicrophoneDevice = request.MicrophoneDeviceName,
                SaveFiles = request.SaveAudioFiles
            },
            Screenshots = new ScreenshotsInfo { AutoEnabled = request.AutoScreenshotsEnabled }
        };

        await AtomicJsonStore.WriteAsync(_folder.SessionJson, _manifest, JsonSetup.Pretty, ct)
            .ConfigureAwait(false);

        _timeline = new JsonlWriter(_folder.TimelineJsonl);
        TranscriptWriter = new JsonlWriter(_folder.TranscriptJsonl);
        _drain = Task.Run(DrainEventsAsync, CancellationToken.None);

        _log.LogInformation("Сессия {Id} создана в {Path}", sessionId, _folder.Root);
        return new SessionHandle(sessionId, _folder.Root);
    }

    public void Publish(TimelineEvent evt)
    {
        // Никогда не блокируем вызывающий поток: очередь неограниченная, запись идёт фоном.
        _events.Writer.TryWrite(evt);
    }

    private async Task DrainEventsAsync()
    {
        try
        {
            await foreach (var evt in _events.Reader.ReadAllAsync())
            {
                if (_timeline is null) continue;
                try
                {
                    await _timeline.AppendAsync(evt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Не удалось записать событие {Type}", evt.Type);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<SessionResult> FinalizeSessionAsync(
        SessionStatus status, TimeSpan duration, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        if (_folder is null || _manifest is null)
            throw new InvalidOperationException("Сессия не была создана");

        // Сначала дописываем всё, что стоит в очереди событий, потом закрываем файлы.
        _events.Writer.TryComplete();
        if (_drain is not null)
        {
            try { await _drain.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch (TimeoutException) { _log.LogWarning("Очередь событий не опустела за 10 с"); }
        }

        if (TranscriptWriter is not null)
        {
            await TranscriptWriter.FlushAsync(ct).ConfigureAwait(false);
            await TranscriptWriter.DisposeAsync().ConfigureAwait(false);
            TranscriptWriter = null;
        }

        if (_timeline is not null)
        {
            await _timeline.FlushAsync(ct).ConfigureAwait(false);
            await _timeline.DisposeAsync().ConfigureAwait(false);
            _timeline = null;
        }

        var segments = TranscriptRenderer.Render(_folder, _manifest);

        var audioFiles = Directory.Exists(_folder.AudioDir)
            ? Directory.GetFiles(_folder.AudioDir)
                .Select(f => Path.Combine("audio", Path.GetFileName(f)).Replace('\\', '/'))
                .ToArray()
            : Array.Empty<string>();

        _manifest = _manifest with
        {
            Status = status,
            DurationMs = (long)duration.TotalMilliseconds,
            Warnings = warnings,
            Audio = _manifest.Audio with { Files = audioFiles },
            Screenshots = _manifest.Screenshots with { Count = ScreenshotCount }
        };

        await AtomicJsonStore.WriteAsync(_folder.SessionJson, _manifest, JsonSetup.Pretty, ct)
            .ConfigureAwait(false);

        GlossaryTemplate.Ensure(_folder);

        _lock?.Dispose();
        _lock = null;

        _log.LogInformation("Сессия {Id} финализирована: {Status}", _manifest.SessionId, status);

        return new SessionResult
        {
            SessionId = _manifest.SessionId,
            FolderPath = _folder.Root,
            Status = status,
            Duration = duration,
            ScreenshotCount = ScreenshotCount,
            SegmentCount = segments,
            Warnings = warnings
        };
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        if (_drain is not null) { try { await _drain.ConfigureAwait(false); } catch { } }
        if (_timeline is not null) await _timeline.DisposeAsync().ConfigureAwait(false);
        if (TranscriptWriter is not null) await TranscriptWriter.DisposeAsync().ConfigureAwait(false);
        _lock?.Dispose();
    }
}

/// <summary>
/// Файл-метка активной сессии с PID: позволяет отличить работающую встречу от брошенной
/// после аварийного завершения (ТЗ 11.3).
/// </summary>
public sealed class SessionLock : IDisposable
{
    private readonly string _path;
    private readonly FileStream _stream;

    private SessionLock(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    public static SessionLock Acquire(string path, string sessionId)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var payload = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"pid\":{Environment.ProcessId},\"session_id\":\"{sessionId}\"}}");
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
        return new SessionLock(path, stream);
    }

    public void Dispose()
    {
        _stream.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
