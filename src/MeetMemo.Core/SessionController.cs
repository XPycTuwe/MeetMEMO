using System.Threading.Channels;
using MeetMemo.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Core;

/// <summary>Данные для карточки завершения (ТЗ 5.1).</summary>
public sealed record SessionResult
{
    public required string SessionId { get; init; }
    public required string FolderPath { get; init; }
    public required SessionStatus Status { get; init; }
    public required TimeSpan Duration { get; init; }
    public int ScreenshotCount { get; init; }
    public int SegmentCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Ядро приложения: единственный потребитель канала команд. Любое изменение состояния сессии
/// проходит здесь, поэтому недопустимых переходов и гонок между источниками команд быть не может.
/// Подсистемы подключаются как <see cref="ISessionParticipant"/> и останавливаются по StopOrder
/// с таймаутом — зависшая подсистема не мешает корректно закрыть остальные (ТЗ 16, 17.2).
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    private static readonly TimeSpan ParticipantStopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ParticipantStartTimeout = TimeSpan.FromSeconds(20);

    private readonly Channel<SessionCommand> _commands =
        Channel.CreateBounded<SessionCommand>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly IReadOnlyList<ISessionParticipant> _participants;
    private readonly ISessionStore _store;
    private readonly ILogger<SessionController> _log;
    private readonly Task _pump;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<string> _warnings = new();

    private SessionState _state = SessionState.Idle;
    private SessionContext? _context;
    private SessionClock? _clock;
    private long _pausedAtMs;
    private long _totalPausedMs;

    public SessionController(
        IEnumerable<ISessionParticipant> participants,
        ISessionStore store,
        DegradationPolicy degradation,
        ILogger<SessionController>? log = null)
    {
        _participants = participants.OrderBy(p => p.StopOrder).ToList();
        _store = store;
        Degradation = degradation;
        _log = log ?? NullLogger<SessionController>.Instance;
        _pump = Task.Run(PumpAsync);
    }

    public DegradationPolicy Degradation { get; }

    /// <summary>Текущее состояние. Читается из любого потока (UI, таймеры) без блокировок.</summary>
    public SessionState State => (SessionState)Volatile.Read(ref _stateVolatile);

    private int _stateVolatile = (int)SessionState.Idle;

    public string? CurrentFolder => _context?.FolderPath;

    /// <summary>Длительность записи без учёта пауз — для таймера на панели.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            if (_clock is null) return TimeSpan.Zero;
            var raw = _clock.ElapsedMs - _totalPausedMs;
            if (State == SessionState.Paused)
                raw -= _clock.ElapsedMs - _pausedAtMs;
            return TimeSpan.FromMilliseconds(Math.Max(0, raw));
        }
    }

    public event Action<SessionState>? StateChanged;

    public event Action<SessionResult>? SessionCompleted;

    /// <summary>Ошибка, которую нужно показать пользователю понятным текстом.</summary>
    public event Action<string>? UserFacingError;

    public async Task<CommandResult> SendAsync(SessionCommand command, CancellationToken ct = default)
    {
        try
        {
            await _commands.Writer.WriteAsync(command, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return CommandResult.Rejected("Приложение завершает работу");
        }

        return await command.Completion.Task.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var cmd in _commands.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    var result = await HandleAsync(cmd).ConfigureAwait(false);
                    cmd.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Команда {Command} завершилась ошибкой", cmd.GetType().Name);
                    cmd.Completion.TrySetResult(CommandResult.Rejected(ex.Message));
                    UserFacingError?.Invoke($"Не удалось выполнить команду: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // штатное завершение
        }
    }

    private Task<CommandResult> HandleAsync(SessionCommand cmd) => cmd switch
    {
        SessionCommand.Start s => StartAsync(s.Request),
        SessionCommand.Pause => PauseAsync(),
        SessionCommand.Resume => ResumeAsync(),
        SessionCommand.Stop => StopAsync(),
        SessionCommand.CaptureWindow c => ForwardToParticipantsAsync(cmd, c.Important),
        SessionCommand.CaptureDesktop => ForwardToParticipantsAsync(cmd, false),
        SessionCommand.MarkImportant => MarkImportantAsync(),
        SessionCommand.SwitchAudioSource s => SwitchAudioAsync(s.Mode),
        _ => Task.FromResult(CommandResult.Rejected("Неизвестная команда"))
    };

    private async Task<CommandResult> StartAsync(SessionStartRequest request)
    {
        if (_state is not SessionState.Idle and not SessionState.Completed and not SessionState.Failed)
            return CommandResult.Rejected("Сессия уже запущена");

        SetState(SessionState.Starting);
        _warnings.Clear();
        _totalPausedMs = 0;
        Degradation.Reset();

        try
        {
            _clock = new SessionClock();
            var session = await _store.CreateSessionAsync(request, _clock, _shutdown.Token)
                .ConfigureAwait(false);

            _context = new SessionContext
            {
                SessionId = session.SessionId,
                FolderPath = session.FolderPath,
                Clock = _clock,
                Request = request,
                Events = _store
            };

            _store.Events.Emit(_clock, EventTypes.SessionStarted,
                ("title", request.Title),
                ("audio_mode", request.AudioMode.ToString()),
                ("save_audio", request.SaveAudioFiles ? "true" : "false"));

            var started = new List<ISessionParticipant>();
            foreach (var p in _participants)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    timeout.CancelAfter(ParticipantStartTimeout);
                    await p.StartAsync(_context, timeout.Token).ConfigureAwait(false);
                    started.Add(p);
                }
                catch (Exception ex) when (!IsCritical(p))
                {
                    // Некритичная подсистема (снимки, ASR) не должна мешать записи начаться.
                    _log.LogError(ex, "Подсистема {Name} не стартовала", p.Name);
                    AddWarning($"{p.Name}: не удалось запустить ({ex.Message})");
                }
            }

            if (started.Count == 0)
                throw new InvalidOperationException("Ни одна подсистема не запустилась");

            SetState(SessionState.Recording);
            return CommandResult.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось запустить сессию");
            SetState(SessionState.Failed);
            UserFacingError?.Invoke($"Не удалось начать запись: {ex.Message}");
            return CommandResult.Rejected(ex.Message);
        }
    }

    private async Task<CommandResult> PauseAsync()
    {
        if (_state != SessionState.Recording)
            return CommandResult.Rejected("Пауза доступна только во время записи");

        _pausedAtMs = _clock!.ElapsedMs;
        await ForEachParticipantAsync((p, ct) => p.PauseAsync(ct)).ConfigureAwait(false);
        _store.Events.Emit(_clock, EventTypes.RecordingPaused);
        SetState(SessionState.Paused);
        return CommandResult.Ok();
    }

    private async Task<CommandResult> ResumeAsync()
    {
        if (_state != SessionState.Paused)
            return CommandResult.Rejected("Продолжение доступно только из паузы");

        _totalPausedMs += _clock!.ElapsedMs - _pausedAtMs;
        await ForEachParticipantAsync((p, ct) => p.ResumeAsync(ct)).ConfigureAwait(false);
        // Разрыв фиксируется явным событием: шкала времени остаётся непрерывной (ТЗ 11.1).
        _store.Events.Emit(_clock, EventTypes.RecordingResumed,
            ("gap_ms", (_clock.ElapsedMs - _pausedAtMs).ToString()));
        SetState(SessionState.Recording);
        return CommandResult.Ok();
    }

    private async Task<CommandResult> StopAsync()
    {
        if (_state is not SessionState.Recording and not SessionState.Paused)
            return CommandResult.Rejected("Нет активной записи");

        SetState(SessionState.Finalizing);
        _store.Events.Emit(_clock!, EventTypes.SessionStopped);

        // Останавливаем в порядке StopOrder: аудио и стенограмма (наибольший order) — последними.
        foreach (var p in _participants)
        {
            try
            {
                using var timeout = new CancellationTokenSource(ParticipantStopTimeout);
                await p.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("Подсистема {Name} не остановилась за отведённое время", p.Name);
                AddWarning($"{p.Name}: остановка по таймауту");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка остановки подсистемы {Name}", p.Name);
                AddWarning($"{p.Name}: ошибка остановки ({ex.Message})");
            }
        }

        var status = _warnings.Count > 0
            ? SessionStatus.CompletedWithWarnings
            : SessionStatus.Completed;

        SessionResult result;
        try
        {
            result = await _store.FinalizeSessionAsync(status, Elapsed, _warnings, _shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Финализация не удалась");
            SetState(SessionState.Failed);
            UserFacingError?.Invoke($"Ошибка финализации: {ex.Message}");
            return CommandResult.Rejected(ex.Message);
        }

        SetState(SessionState.Completed);
        _context = null;
        SessionCompleted?.Invoke(result);
        return CommandResult.Ok();
    }

    private async Task<CommandResult> MarkImportantAsync()
    {
        if (_state != SessionState.Recording)
            return CommandResult.Rejected("Маркер доступен только во время записи");

        _store.Events.Emit(_clock!, EventTypes.ImportantMarkAdded);
        return await ForwardToParticipantsAsync(new SessionCommand.CaptureWindow(true), true)
            .ConfigureAwait(false);
    }

    private async Task<CommandResult> SwitchAudioAsync(AudioMode mode)
    {
        if (_state is not SessionState.Recording and not SessionState.Paused)
            return CommandResult.Rejected("Нет активной записи");

        foreach (var p in _participants.OfType<IAudioSourceSwitchable>())
        {
            await p.SwitchAsync(mode, _shutdown.Token).ConfigureAwait(false);
        }

        _store.Events.Emit(_clock!, EventTypes.AudioSourceChanged, ("mode", mode.ToString()));
        return CommandResult.Ok("Источник звука переключён");
    }

    private async Task<CommandResult> ForwardToParticipantsAsync(SessionCommand cmd, bool important)
    {
        if (_state != SessionState.Recording)
            return CommandResult.Rejected("Доступно только во время записи");

        var handled = false;
        foreach (var p in _participants.OfType<ICommandHandler>())
        {
            if (await p.TryHandleAsync(cmd, _shutdown.Token).ConfigureAwait(false))
                handled = true;
        }

        return handled
            ? CommandResult.Ok()
            : CommandResult.Rejected("Нет подсистемы, обрабатывающей команду");
    }

    private async Task ForEachParticipantAsync(Func<ISessionParticipant, CancellationToken, Task> action)
    {
        foreach (var p in _participants)
        {
            try
            {
                using var timeout = new CancellationTokenSource(ParticipantStopTimeout);
                await action(p, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Подсистема {Name} не обработала команду", p.Name);
                AddWarning($"{p.Name}: {ex.Message}");
            }
        }
    }

    private static bool IsCritical(ISessionParticipant p) => p is ICriticalParticipant;

    private void AddWarning(string message)
    {
        if (!_warnings.Contains(message)) _warnings.Add(message);
    }

    private void SetState(SessionState state)
    {
        _state = state;
        Volatile.Write(ref _stateVolatile, (int)state);
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        _shutdown.Cancel();
        try { await _pump.ConfigureAwait(false); } catch { /* завершение */ }
        _shutdown.Dispose();
    }
}

/// <summary>Подсистема, без которой сессия не имеет смысла — её отказ прерывает старт.</summary>
public interface ICriticalParticipant;

/// <summary>Подсистема, умеющая обрабатывать разовые команды (снимки, маркеры).</summary>
public interface ICommandHandler
{
    Task<bool> TryHandleAsync(SessionCommand command, CancellationToken ct);
}

/// <summary>Подсистема, поддерживающая смену источника звука на лету (AC-05).</summary>
public interface IAudioSourceSwitchable
{
    Task SwitchAsync(AudioMode mode, CancellationToken ct);
}

/// <summary>Хранилище сессии: создаёт папку, пишет события, финализирует пакет.</summary>
public interface ISessionStore : IEventSink
{
    IEventSink Events => this;

    Task<SessionHandle> CreateSessionAsync(
        SessionStartRequest request, ISessionClock clock, CancellationToken ct);

    Task<SessionResult> FinalizeSessionAsync(
        SessionStatus status, TimeSpan duration, IReadOnlyList<string> warnings, CancellationToken ct);
}

public sealed record SessionHandle(string SessionId, string FolderPath);
