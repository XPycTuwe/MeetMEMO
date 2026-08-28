using System.IO;
using System.Drawing;
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
    /// <summary>
    /// Про пропажу окна говорим один раз за встречу. Флаг целочисленный и меняется
    /// через Interlocked не для красоты: колбэк таймера живёт в пуле потоков, и на
    /// закрытии браузера предупреждение выскочило дважды.
    /// </summary>
    private int _targetLostReported;

    /// <summary>
    /// Сколько проверок подряд окно должно отсутствовать. Одной мало: приложения
    /// на секунду прячут окно при пересоздании или смене режима, и это не закрытие.
    /// </summary>
    private const int GoneChecksToConfirm = 2;

    private int _goneChecks;
    private bool _targetMinimized;
    private readonly MinimizeWatcher _minimizeWatcher = new(MinimizeWarnCooldown);

    /// <summary>
    /// Окно встречи закрыли, а запись идёт. Звук при этом никуда не девается — дорожка
    /// сама переходит на общий звук системы, — но снимков больше не будет, и человек
    /// об этом узнаёт только по пустой папке. Обрывать запись за него нельзя: он мог
    /// закрыть окно намеренно и продолжать говорить. Поэтому спрашиваем.
    /// </summary>
    public event Action? TargetClosed;

    /// <summary>
    /// Окно свернули или развернули обратно. У свёрнутого окна система не отдаёт кадры,
    /// автоснимки на это время встают.
    /// </summary>
    public event Action<bool>? TargetMinimizedChanged;

    /// <summary>Как часто напоминать про свёрнутое окно, если его сворачивают снова и снова.</summary>
    private static readonly TimeSpan MinimizeWarnCooldown = TimeSpan.FromMinutes(5);

    /// <summary>Собирать ли текст из окна встречи. Выключается настройкой приложения.</summary>
    public static bool CollectWindowContext { get; set; } = true;

    /// <summary>
    /// Как часто заглядывать в окно. Список участников и чат меняются медленно, а обход
    /// дерева доступности стоит заметно дороже снимка — чаще незачем.
    /// </summary>
    private static readonly TimeSpan ContextInterval = TimeSpan.FromSeconds(30);

    private Timer? _contextTimer;
    private JsonlWriter? _context_writer;
    private string _lastContextKey = string.Empty;

    /// <summary>
    /// Снимает текст окна встречи: имена в списке участников, сообщения чата, тему.
    /// Пишется только то, что изменилось, — иначе за час набежит сотня одинаковых копий
    /// одного и того же списка.
    /// </summary>
    private void TryCollectContext()
    {
        if (_context is null || _paused || _context_writer is null) return;

        // Обход дерева доступности тяжёлого окна занимает секунды, а таймер этого не ждёт:
        // такты накладывались и забивали пул потоков.
        if (Interlocked.Exchange(ref _contextBusy, 1) == 1) return;

        try
        {
            if (!WindowEnumerator.IsAlive(_targetWindow)) return;

            var fragments = WindowTextReader.ReadVisibleText(_targetWindow);
            if (fragments.Count == 0) return;

            var key = string.Join("|", fragments);
            if (key == _lastContextKey) return;
            _lastContextKey = key;

            var entry = new WindowContextEntry
            {
                OffsetMs = _context.Clock.ElapsedMs,
                WindowTitle = Interop.Win32.GetWindowTitle(_targetWindow),
                Fragments = fragments
            };

            _context_writer.AppendAsync(entry).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Контекст — приятное дополнение, а не основа пакета: его сбой не должен
            // мешать записи встречи.
            _log.LogDebug(ex, "Не удалось снять контекст окна");
        }
        finally
        {
            Interlocked.Exchange(ref _contextBusy, 0);
        }
    }

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
        _targetLostReported = 0;
        _goneChecks = 0;
        _lastAutoOffsetMs = -1;
        _lastHash = 0;
        _targetMinimized = false;
        _minimizeWatcher.Reset();

        _targetWindow = context.Request.Target?.WindowHandle ?? 0;
        _applicationName = context.Request.Target?.ApplicationName;

        var folder = new MeetingFolder(context.FolderPath);
        _screenshots = new ScreenshotStore(folder, context.Clock, _log);
        _autoEnabled = context.Request.AutoScreenshotsEnabled;

        if (_targetWindow != 0 && CollectWindowContext)
        {
            _context_writer = new JsonlWriter(folder.ContextJsonl);
            _contextTimer = new Timer(
                _ => TryCollectContext(), null,
                TimeSpan.FromSeconds(5), ContextInterval);
        }

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

        // Наложение тактов: снимок 4K-окна со сжатием и распознаванием текста иногда
        // не укладывается в интервал, а таймер этого не ждёт — второй такт начинался
        // поверх первого, и они дрались за пул потоков.
        if (Interlocked.Exchange(ref _autoBusy, 1) == 1) return;

        try
        {
            // За окном следим всегда, даже когда автоснимки выключены: закрытое окно
            // встречи — повод спросить человека, а не молча продолжать запись.
            if (CheckTargetGone()) return;

            UpdateMinimizedState(Interop.Win32.IsIconic(_targetWindow));

            if (!_autoEnabled) return;
            if (!_degradation.AreAutoScreenshotsAllowed) return;
            if (_targetMinimized) return;

            var offset = _context.Clock.ElapsedMs;
            if (_lastAutoOffsetMs >= 0
                && offset - _lastAutoOffsetMs < _options.MinInterval.TotalMilliseconds) return;

            using var bitmap = ScreenCapture.CaptureWindow(_targetWindow);
            if (bitmap is null) return;

            var hash = PerceptualHash.Compute(bitmap);
            if (_lastHash != 0 && PerceptualHash.Distance(_lastHash, hash) < _options.ChangeThreshold)
                return;

            // Кадр принят к сохранению: отмечаем это до записи на диск, иначе следующий
            // тик таймера увидит ту же картинку и сделает дубль, пока идёт подтверждение.
            _lastHash = hash;
            _lastAutoOffsetMs = offset;

            if (AutoScreenshotPending is { } pending)
            {
                // Кадр живёт в using и вот-вот освободится — отдаём копию: решение
                // о сохранении принимается несколько секунд спустя.
                pending(new Bitmap(bitmap), Interop.Win32.GetWindowTitle(_targetWindow));
                return;
            }

            var entry = _screenshots.SaveAsync(
                bitmap, ScreenshotKind.ApplicationAuto, "frame_changed",
                _applicationName, Interop.Win32.GetWindowTitle(_targetWindow))
                .GetAwaiter().GetResult();

            if (entry is null) return;

            _store.ScreenshotCount = _screenshots.Count;
            EmitScreenshotEvent(entry);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Автоснимок не удался");
        }
        finally
        {
            Interlocked.Exchange(ref _autoBusy, 0);
        }
    }

    private int _autoBusy;
    private int _contextBusy;

    /// <summary>
    /// Следит за сворачиванием окна. Свёрнутое окно кадров не отдаёт, и человек должен
    /// узнать об этом сразу, а не по пустой папке снимков после встречи. Повторные
    /// сворачивания придерживаем: за час их бывает много, и всплывающая подсказка
    /// на каждое превратилась бы в шум.
    /// </summary>
    private void UpdateMinimizedState(bool minimized)
    {
        _targetMinimized = minimized;

        if (_minimizeWatcher.Update(minimized, _context?.Clock.ElapsedMs ?? 0) is { } tell)
            TargetMinimizedChanged?.Invoke(tell);
    }

    /// <summary>
    /// Автоснимок сделан и ждёт решения: кадр и заголовок окна. Пока подписчик есть,
    /// сам движок такие снимки на диск не пишет — это делает <see cref="SaveConfirmedAsync"/>.
    /// Владение переданным кадром переходит подписчику: освобождать его ему.
    /// </summary>
    public event Action<Bitmap, string?>? AutoScreenshotPending;

    /// <summary>Сохраняет подтверждённый автоснимок — тот, что пережил обратный отсчёт.</summary>
    public async Task<ScreenshotEntry?> SaveConfirmedAsync(Bitmap bitmap, string? windowTitle)
    {
        if (_screenshots is null) return null;

        var entry = await _screenshots.SaveAsync(
            bitmap, ScreenshotKind.ApplicationAuto, "frame_changed",
            _applicationName, windowTitle).ConfigureAwait(false);

        if (entry is null) return null;

        _store.ScreenshotCount = _screenshots.Count;
        EmitScreenshotEvent(entry);
        return entry;
    }

    private void EmitScreenshotEvent(ScreenshotEntry entry)
    {
        _context?.Events.Emit(_context.Clock, EventTypes.ScreenshotCreated,
            ("file", entry.File),
            ("type", entry.Type.ToString()),
            ("manual", entry.Manual ? "true" : "false"));

        ScreenshotSaved?.Invoke(entry);

        // Снимок уже на диске — прочитаем с него текст, пока он свежий. Это второй
        // источник контекста, и для приложений вроде TrueConf единственный рабочий.
        _ = ReadScreenshotTextAsync(entry);
    }

    private ScreenshotTextReader? _ocr;

    /// <summary>
    /// Читает текст с только что сделанного снимка и кладёт в context.jsonl.
    ///
    /// Дерево доступности отдаёт не всё: TrueConf на Qt показывает снаружи одни
    /// «Свернуть» и «Закрыть», а бейдж говорящего и панель участников — только на экране.
    /// Со снимка они читаются: «Тухватуллин Айрат Мансурович» в бейдже и семь имён
    /// в панели, за треть секунды. Пишем только когда текст изменился, иначе набежит
    /// сотня одинаковых списков.
    /// </summary>
    private async Task ReadScreenshotTextAsync(ScreenshotEntry entry)
    {
        if (_context is null || _context_writer is null || !CollectWindowContext) return;

        try
        {
            _ocr ??= new ScreenshotTextReader();
            if (!_ocr.Ready) return;

            var path = Path.Combine(_context.FolderPath, entry.File.Replace('/', Path.DirectorySeparatorChar));
            var lines = await _ocr.ReadLinesAsync(path).ConfigureAwait(false);
            if (lines.Count == 0) return;

            var key = "ocr:" + string.Join("|", lines);
            if (key == _lastOcrKey) return;
            _lastOcrKey = key;

            await _context_writer.AppendAsync(new WindowContextEntry
            {
                OffsetMs = entry.OffsetMs,
                WindowTitle = entry.WindowTitle,
                Fragments = lines,
                Source = "screenshot"
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Не удалось прочитать текст со снимка");
        }
    }

    private string _lastOcrKey = string.Empty;

    /// <summary>
    /// Отмечает пропажу окна в шкале встречи. Возвращает true только тому, кто сообщил
    /// первым: говорить человеку об одном и том же дважды не нужно.
    /// </summary>
    private bool NotifyTargetProblem(string reason)
    {
        if (_context is null) return false;
        if (Interlocked.Exchange(ref _targetLostReported, 1) == 1) return false;

        _context.Events.Emit(_context.Clock, EventTypes.TargetLost, EventSeverity.Warning,
            new Dictionary<string, string> { ["reason"] = reason });
        _log.LogWarning("Целевое окно недоступно: {Reason}", reason);
        return true;
    }

    /// <summary>
    /// Окно встречи пропало. Возвращает true, если дальше делать нечего.
    ///
    /// Пропасть можно двумя способами, и это выяснилось не сразу. Браузер окно
    /// уничтожает — тогда его больше нет вовсе. А Teams при закрытии прячется в трей:
    /// окно живо, просто не показано, и проверка «существует ли» его пропускала —
    /// человек так и не узнавал, что встреча кончилась, а запись идёт.
    ///
    /// Свёрнутое окно под это не подпадает: система считает его показанным, и отличает
    /// его отдельный признак.
    /// </summary>
    private bool CheckTargetGone()
    {
        var gone = !Interop.Win32.IsWindow(_targetWindow)
            || !Interop.Win32.IsWindowVisible(_targetWindow);

        if (!gone)
        {
            _goneChecks = 0;
            return false;
        }

        // Одной проверки мало: приложения на секунду прячут окно при пересоздании,
        // и спрашивать «остановить запись?» на каждое такое мигание нельзя.
        if (++_goneChecks < GoneChecksToConfirm) return true;

        // Спрашиваем только того, кто застолбил сообщение: иначе два такта успевали
        // проскочить проверку одновременно, и предупреждение выскакивало дважды.
        if (NotifyTargetProblem("окно закрыто или скрыто")) TargetClosed?.Invoke();
        return true;
    }

    /// <summary>Смена целевого окна во время встречи (после «Выбрать другое окно»).</summary>
    public void SetTargetWindow(nint handle, string? applicationName)
    {
        _targetWindow = handle;
        _applicationName = applicationName;
        _targetLostReported = 0;
        _goneChecks = 0;
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

        if (_contextTimer is not null)
        {
            await _contextTimer.DisposeAsync().ConfigureAwait(false);
            _contextTimer = null;
        }

        if (_context_writer is not null)
        {
            await _context_writer.DisposeAsync().ConfigureAwait(false);
            _context_writer = null;
        }

        if (_screenshots is not null)
        {
            await _screenshots.FlushAsync(ct).ConfigureAwait(false);
            _store.ScreenshotCount = _screenshots.Count;
        }
    }

    public void Dispose()
    {
        _autoTimer?.Dispose();
        _contextTimer?.Dispose();
        _context_writer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
