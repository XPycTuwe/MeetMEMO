using System.Runtime.Versioning;
using MeetMemo.Contracts;
using MeetMemo.Core;
using MeetMemo.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Capture;

/// <summary>Настройки автоснимков (ТЗ 9.2, группа настроек «Снимки»).</summary>
public sealed record AutoScreenshotOptions
{
    /// <summary>Минимальный интервал между автоснимками.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Порог различия dHash (из 64 бит), выше которого кадр считается новым.</summary>
    public int ChangeThreshold { get; init; } = 10;

    /// <summary>Как часто проверять кадр на изменение.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Подсистема снимков: ручные снимки окна и рабочего стола, маркер «Важно» и автоснимки
/// целевого окна. Автоснимок создаётся только при выполнении обоих условий — истёк
/// минимальный интервал И кадр существенно изменился (ТЗ 9.2).
///
/// Сбой захвата не должен влиять на запись звука, поэтому все ошибки здесь гасятся
/// и превращаются в события временной шкалы.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CaptureEngine : ISessionParticipant, ICommandHandler, IDisposable
{
    private readonly MeetingSessionStore _store;
    private readonly DegradationPolicy _degradation;
    private readonly AutoScreenshotOptions _options;
    private readonly ILogger<CaptureEngine> _log;

    private SessionContext? _context;
    private ScreenshotStore? _screenshots;
    private Timer? _autoTimer;
    private nint _targetWindow;
    private string? _applicationName;
    private ulong _lastHash;

    /// <summary>
    /// Смещение последнего автоснимка. Отрицательное значение означает «снимков ещё не было»,
    /// но брать long.MinValue нельзя: вычитание переполняет long, условие «интервал истёк»
    /// становится ложным навсегда, и автоснимки не делаются вообще.
    /// </summary>
    private long _lastAutoOffsetMs = -1;
    private bool _paused;
    private bool _targetLostReported;

    public CaptureEngine(
        MeetingSessionStore store,
        DegradationPolicy degradation,
        AutoScreenshotOptions? options = null,
        ILogger<CaptureEngine>? log = null)
    {
        _store = store;
        _degradation = degradation;
        _options = options ?? new AutoScreenshotOptions();
        _log = log ?? NullLogger<CaptureEngine>.Instance;
    }

    public string Name => "Снимки";

    /// <summary>Останавливается раньше аудио: снимки менее ценны, чем звук.</summary>
    public int StopOrder => 50;

    public int ScreenshotCount => _screenshots?.Count ?? 0;

    /// <summary>Уведомление для панели: снимок сделан.</summary>
    public event Action<ScreenshotEntry>? ScreenshotSaved;

    /// <summary>
    /// Автоснимки включены. Переключается во время встречи — например, когда на экране
    /// появляется то, чему не место в пакете. Ручные снимки продолжают работать.
    /// </summary>
    public bool AutoScreenshotsEnabled
    {
        get => _autoEnabled;
        set
        {
            if (_autoEnabled == value) return;
            _autoEnabled = value;

            _context?.Events.Emit(_context.Clock, EventTypes.DegradationApplied,
                ("component", "auto_screenshots"),
                ("enabled", value ? "true" : "false"),
                ("reason", "user"));

            _log.LogInformation("Автоснимки {State} пользователем", value ? "включены" : "выключены");
        }
    }

    private bool _autoEnabled = true;

    public Task StartAsync(SessionContext context, CancellationToken ct)
    {
        _context = context;
        _paused = false;
        _targetLostReported = false;
        _lastAutoOffsetMs = -1;
        _lastHash = 0;

        _targetWindow = context.Request.Target?.WindowHandle ?? 0;
        _applicationName = context.Request.Target?.ApplicationName;

        var folder = new MeetingFolder(context.FolderPath);
        _screenshots = new ScreenshotStore(folder, context.Clock, _log);
        _autoEnabled = context.Request.AutoScreenshotsEnabled;

        // Таймер заводим всегда: автоснимки можно включить уже во время встречи,
        // и пересоздавать таймер на лету не потребуется.
        if (_targetWindow != 0)
        {
            _autoTimer = new Timer(
                _ => TryAutoCapture(), null, _options.PollInterval, _options.PollInterval);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> TryHandleAsync(SessionCommand command, CancellationToken ct)
    {
        if (_context is null || _screenshots is null) return false;

        switch (command)
        {
            case SessionCommand.CaptureWindow c:
                return await CaptureWindowAsync(
                    c.Important ? ScreenshotKind.Important : ScreenshotKind.ApplicationManual,
                    c.Important ? "important_hotkey" : "manual_hotkey",
                    ct).ConfigureAwait(false);

            case SessionCommand.CaptureDesktop d:
                return await CaptureDesktopAsync(d.MonitorId, ct).ConfigureAwait(false);

            default:
                return false;
        }
    }

    private async Task<bool> CaptureWindowAsync(ScreenshotKind kind, string trigger, CancellationToken ct)
    {
        if (_targetWindow == 0)
        {
            _log.LogWarning("Снимок окна запрошен, но целевое окно не выбрано");
            return false;
        }

        using var bitmap = ScreenCapture.CaptureWindow(_targetWindow);
        if (bitmap is null)
        {
            NotifyTargetProblem("окно не отдало кадр");
            return false;
        }

        var entry = await _screenshots!.SaveAsync(
            bitmap, kind, trigger,
            _applicationName,
            Interop.Win32.GetWindowTitle(_targetWindow),
            ct: ct).ConfigureAwait(false);

        if (entry is null) return false;

        _lastHash = PerceptualHash.Compute(bitmap);
        _store.ScreenshotCount = _screenshots.Count;
        EmitScreenshotEvent(entry);
        return true;
    }

    private async Task<bool> CaptureDesktopAsync(string? monitorId, CancellationToken ct)
    {
        var monitors = ScreenCapture.GetMonitors();
        if (monitors.Count == 0) return false;

        var monitor = monitorId is null
            ? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]
            : monitors.FirstOrDefault(m => m.Id == monitorId) ?? monitors[0];

        using var bitmap = ScreenCapture.CaptureMonitor(monitor);
        if (bitmap is null) return false;

        var entry = await _screenshots!.SaveAsync(
            bitmap, ScreenshotKind.DesktopManual, "manual_hotkey",
            monitor: monitor.Name, ct: ct).ConfigureAwait(false);

        if (entry is null) return false;

        _store.ScreenshotCount = _screenshots.Count;
        EmitScreenshotEvent(entry);
        return true;
    }

    /// <summary>
    /// Автоснимок: оба условия обязательны — прошёл минимальный интервал и кадр изменился.
    /// При свёрнутом или пропавшем окне автоснимки приостанавливаются (ТЗ 6.3, AC-11).
    /// </summary>
    private void TryAutoCapture()
    {
        if (_context is null || _screenshots is null || _paused || _targetWindow == 0) return;
        if (!_autoEnabled) return;
        if (!_degradation.AreAutoScreenshotsAllowed) return;

        try
        {
            if (!WindowEnumerator.IsAlive(_targetWindow))
            {
                NotifyTargetProblem("окно закрыто");
                return;
            }

            if (Interop.Win32.IsIconic(_targetWindow)) return;

            var offset = _context.Clock.ElapsedMs;
            if (_lastAutoOffsetMs >= 0
                && offset - _lastAutoOffsetMs < _options.MinInterval.TotalMilliseconds) return;

            using var bitmap = ScreenCapture.CaptureWindow(_targetWindow);
            if (bitmap is null) return;

            var hash = PerceptualHash.Compute(bitmap);
            if (_lastHash != 0 && PerceptualHash.Distance(_lastHash, hash) < _options.ChangeThreshold)
                return;

            var entry = _screenshots.SaveAsync(
                bitmap, ScreenshotKind.ApplicationAuto, "frame_changed",
                _applicationName, Interop.Win32.GetWindowTitle(_targetWindow))
                .GetAwaiter().GetResult();

            if (entry is null) return;

            _lastHash = hash;
            _lastAutoOffsetMs = offset;
            _store.ScreenshotCount = _screenshots.Count;
            EmitScreenshotEvent(entry);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Автоснимок не удался");
        }
    }

    private void EmitScreenshotEvent(ScreenshotEntry entry)
    {
        _context?.Events.Emit(_context.Clock, EventTypes.ScreenshotCreated,
            ("file", entry.File),
            ("type", entry.Type.ToString()),
            ("manual", entry.Manual ? "true" : "false"));

        ScreenshotSaved?.Invoke(entry);
    }

    private void NotifyTargetProblem(string reason)
    {
        if (_targetLostReported || _context is null) return;
        _targetLostReported = true;

        _context.Events.Emit(_context.Clock, EventTypes.TargetLost, EventSeverity.Warning,
            new Dictionary<string, string> { ["reason"] = reason });
        _log.LogWarning("Целевое окно недоступно: {Reason}", reason);
    }

    /// <summary>Смена целевого окна во время встречи (после «Выбрать другое окно»).</summary>
    public void SetTargetWindow(nint handle, string? applicationName)
    {
        _targetWindow = handle;
        _applicationName = applicationName;
        _targetLostReported = false;
        _lastHash = 0;

        _context?.Events.Emit(_context.Clock, EventTypes.TargetRestored,
            ("window", Interop.Win32.GetWindowTitle(handle)));
    }

    public Task PauseAsync(CancellationToken ct)
    {
        _paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct)
    {
        _paused = false;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_autoTimer is not null)
        {
            await _autoTimer.DisposeAsync().ConfigureAwait(false);
            _autoTimer = null;
        }

        if (_screenshots is not null)
        {
            await _screenshots.FlushAsync(ct).ConfigureAwait(false);
            _store.ScreenshotCount = _screenshots.Count;
        }
    }

    public void Dispose() => _autoTimer?.Dispose();
}
