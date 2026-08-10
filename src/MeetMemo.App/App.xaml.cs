using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using MeetMemo.Asr;
using MeetMemo.Audio;
using MeetMemo.Capture;
using MeetMemo.Contracts;
using MeetMemo.Core;
using MeetMemo.Storage;

namespace MeetMemo.App;

/// <summary>
/// Точка входа. Приложение живёт в трее: главного окна нет, всё управление идёт через
/// значок, плавающую панель и глобальные горячие клавиши.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\MeetMemo.SingleInstance";

    private Mutex? _instanceMutex;
    private TaskbarIcon? _tray;
    private HotkeyWindow? _hotkeys;
    private Window? _dialogOwner;
    private AppSettings _settings = new();

    private SessionController? _controller;
    private MeetingSessionStore? _store;
    private AudioEngine? _audio;
    private CaptureEngine? _capture;
    private AsrEngine? _asr;
    private SourcePickerWindow? _picker;

    /// <summary>Кнопки MeetMemo в заголовках окон отмеченных приложений — по одной на окно.</summary>
    private readonly Dictionary<nint, TitleBarOverlay> _overlays = new();

    private System.Windows.Threading.DispatcherTimer? _overlayScanTimer;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // Второй экземпляр перехватил бы горячие клавиши и мог начать писать в ту же папку.
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            // Здесь окна-владельца ещё нет, но и меню трея тоже — обычный вызов безопасен.
            MessageBox.Show("MeetMemo уже запущен — значок находится в области уведомлений.",
                "MeetMemo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Необработанное исключение в интерфейсе не должно тихо ронять приложение.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError("ui", args.Exception);
            args.Handled = true;
            ShowMessage($"Внутренняя ошибка: {args.Exception.Message}\n\n"
                + "Подробности записаны в app-errors.log.", "MeetMemo", MessageBoxImage.Error);
        };

        _settings = await AppSettings.LoadAsync();
        Directory.CreateDirectory(_settings.MeetingsRoot);
        TitleBarOverlay.SystemButtonsWidth = _settings.TitleBarOffset;
        TitleBarOverlay.OffsetChanged += OnTitleBarOffsetChanged;
        ScreenshotStore.ScreenshotColors = _settings.ScreenshotColors;

        CreateDialogOwner();
        SetupTray();
        SetupHotkeys();

        await CheckRecoverableSessionsAsync();

        StartOverlayScanner();

        if (!_settings.LegalNoticeAccepted) ShowLegalNotice();
    }

    /// <summary>
    /// Невидимое окно-владелец для диалогов.
    ///
    /// Без него MessageBox, вызванный из меню значка в трее, берёт владельцем всплывающее
    /// окно самого меню — а оно закрывается сразу после щелчка и утаскивает диалог за собой:
    /// пользователь видит вспышку вместо сообщения. Постоянный владелец эту связку разрывает.
    /// </summary>
    private void CreateDialogOwner()
    {
        _dialogOwner = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -32000,
            Top = -32000,
            Visibility = Visibility.Hidden
        };

        _dialogOwner.Show();
    }

    /// <summary>
    /// Показ сообщения из меню значка в трее.
    ///
    /// Диалог, открытый прямо в обработчике пункта меню, у части пользователей появлялся
    /// на долю секунды и закрывался. Поэтому здесь два независимых средства: показ
    /// откладывается таймером, чтобы меню успело полностью закрыться и отпустить ввод,
    /// и диалогу задаётся постоянный владелец вместо исчезающего окна меню.
    ///
    /// Задержка сделана таймером, а не Dispatcher.BeginInvoke с низким приоритетом:
    /// приоритет ApplicationIdle ниже Background и в занятом приложении может не наступить.
    /// </summary>
    private void ShowMessage(string text, string caption = "MeetMemo",
        MessageBoxImage icon = MessageBoxImage.Information)
        => DeferToUi(() => ShowMessageNow(text, caption, MessageBoxButton.OK, icon));

    /// <summary>Отложенный запуск действия после закрытия меню трея.</summary>
    private void DeferToUi(Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // Исключение в отложенном действии иначе осталось бы незамеченным.
                Debug.WriteLine($"Отложенное действие завершилось ошибкой: {ex}");
                LogError("deferred", ex);
            }
        };

        timer.Start();
    }

    private MessageBoxResult ShowMessageNow(string text, string caption,
        MessageBoxButton buttons, MessageBoxImage icon)
    {
        // Окно-владелец должно быть видимым для системы, иначе диалог снова окажется
        // без владельца. Держим его за пределами экрана и прячем сразу после показа.
        if (_dialogOwner is null)
            return MessageBox.Show(text, caption, buttons, icon);

        _dialogOwner.Visibility = Visibility.Visible;
        try
        {
            return MessageBox.Show(_dialogOwner, text, caption, buttons, icon);
        }
        finally
        {
            _dialogOwner.Visibility = Visibility.Hidden;
        }
    }

    private void SetupTray()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "MeetMemo — готов к записи",
            ContextMenu = (ContextMenu)Resources["TrayMenu"],
            IconSource = LoadIcon("tray-idle.ico")
        };

        _tray.TrayMouseDoubleClick += (_, _) => ConfigureRecording();
        UpdateRecentMenu();
    }

    private static BitmapImage LoadIcon(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        return new BitmapImage(new Uri(path, UriKind.Absolute));
    }

    private void SetupHotkeys()
    {
        _hotkeys = new HotkeyWindow();
        var results = _hotkeys.Manager.Register(HotkeyBinding.Defaults);

        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
        {
            // Занятую комбинацию нельзя проглатывать молча — пользователь должен понимать,
            // почему клавиши не работают, и иметь возможность назначить другие (AC-19).
            var message = string.Join("\n", failed.Select(f => $"• {f.Error}"));
            _tray?.ShowBalloonTip("MeetMemo: горячие клавиши",
                $"Часть комбинаций недоступна:\n{message}", BalloonIcon.Warning);
        }

        _hotkeys.Manager.HotkeyPressed += OnHotkeyPressed;
    }

    private async void OnHotkeyPressed(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.StartStop:
                if (_controller is null || _controller.State is SessionState.Idle
                    or SessionState.Completed or SessionState.Failed)
                    // Пишем то окно, в котором человек сейчас работает: это ровно то же,
                    // что даёт кнопка в заголовке, только не отрывая рук от клавиатуры.
                    await StartRecordingForWindowAsync(Capture.Interop.Win32.GetForegroundWindow());
                else
                    await _controller.SendAsync(new SessionCommand.Stop());
                break;

            case HotkeyAction.PauseResume when _controller is not null:
                SessionCommand cmd = _controller.State == SessionState.Paused
                    ? new SessionCommand.Resume()
                    : new SessionCommand.Pause();
                await _controller.SendAsync(cmd);
                break;

            case HotkeyAction.CaptureWindow when _controller is not null:
                await _controller.SendAsync(new SessionCommand.CaptureWindow());
                break;

            case HotkeyAction.CaptureDesktop when _controller is not null:
                await _controller.SendAsync(new SessionCommand.CaptureDesktop());
                break;

            case HotkeyAction.MarkImportant when _controller is not null:
                await _controller.SendAsync(new SessionCommand.MarkImportant());
                break;
        }
    }

    private void OnNewMeetingClick(object sender, RoutedEventArgs e) => ConfigureRecording();

    /// <summary>
    /// Запись всего, что звучит в системе, без привязки к окну.
    ///
    /// Нужна, когда встреча идёт в браузере, в нескольких приложениях сразу или когда
    /// искать нужное окно попросту некогда. Изоляции по процессу здесь нет: в дорожку
    /// попадёт любой звук с компьютера, включая музыку и уведомления — поэтому режим
    /// вызывается явно и отдельным пунктом.
    /// </summary>
    private void OnRecordEverythingClick(object sender, RoutedEventArgs e)
        => DeferToUi(async () => await StartSystemWideRecordingAsync());

    private async Task StartSystemWideRecordingAsync()
    {
        if (_controller is not null && _controller.State is SessionState.Recording
            or SessionState.Paused or SessionState.Starting)
        {
            _tray?.ShowBalloonTip("MeetMemo", "Запись уже идёт", BalloonIcon.Info);
            return;
        }

        var models = new ModelManager(_settings.ModelsRoot);
        if (models.GetMissing().Count > 0)
        {
            var answer = ShowMessageNow(
                $"Модели распознавания не установлены (~{models.GetMissingBytes() / 1024 / 1024} МБ).\n\n"
                + "Без них запись пойдёт, но живой стенограммы не будет.\n\nСкачать сейчас?",
                "MeetMemo", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) await DownloadModelsAsync(models);
        }

        var request = new SessionStartRequest
        {
            Title = $"Запись системы {DateTime.Now:dd.MM.yyyy HH:mm}",
            MeetingsRoot = _settings.MeetingsRoot,
            // Общий loopback устройства вывода: пишем всё, что слышно на компьютере.
            AudioMode = AudioMode.System,
            MicrophoneDeviceId = _settings.MicrophoneDeviceId,
            SaveAudioFiles = _settings.SaveAudioFiles,
            // Целевого окна нет, значит и снимать нечего: автоснимки привязаны к окну.
            AutoScreenshotsEnabled = false,
            Target = null
        };

        await StartSessionAsync(request);

        if (_controller?.State == SessionState.Recording)
        {
            _tray?.ShowBalloonTip(
                "MeetMemo — запись всей системы",
                "Пишется весь звук компьютера и микрофон. Остановить: Ctrl+Alt+M "
                + "или пункт «Остановить запись» в меню значка.",
                BalloonIcon.Info);
        }
    }

    /// <summary>
    /// Настройка записи: какие приложения ведём и как пишем звук. Саму запись отсюда
    /// не запускаем — для этого есть кнопка в заголовке отмеченного окна.
    /// </summary>
    private async void ConfigureRecording()
    {
        // Без моделей живой стенограммы не будет — предупреждаем заранее, а не в момент записи.
        var models = new ModelManager(_settings.ModelsRoot);
        if (models.GetMissing().Count > 0)
        {
            var answer = ShowMessageNow(
                $"Модели распознавания не установлены (нужно ~{models.GetMissingBytes() / 1024 / 1024} МБ).\n\n"
                + "Без них запись пойдёт, но живой стенограммы не будет.\n\nСкачать сейчас?",
                "MeetMemo", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                await DownloadModelsAsync(models);
            }
        }

        if (_picker is not null) { _picker.Activate(); return; }

        _picker = new SourcePickerWindow(_settings);
        _picker.TrackedAppsChanged += OnTrackedAppChanged;
        var dialogResult = _picker.ShowDialog();
        var preferences = _picker.Result;
        _picker.TrackedAppsChanged -= OnTrackedAppChanged;
        _picker = null;

        if (dialogResult != true || preferences is null) return;

        _settings = _settings with
        {
            MicrophoneDeviceId = preferences.MicrophoneDeviceId,
            AudioMode = preferences.AudioMode,
            SaveAudioFiles = preferences.SaveAudioFiles,
            AutoScreenshots = preferences.AutoScreenshotsEnabled,
            ShowSubtitles = preferences.ShowSubtitles
        };
        await _settings.SaveAsync();

        // Подсказываем, где теперь начинается запись: кнопка «Сохранить» её не запускает,
        // и без пояснения человек ждёт старта, которого не будет.
        _tray?.ShowBalloonTip(
            "MeetMemo",
            _settings.TrackedApps.Count > 0
                ? "Параметры сохранены. Запись начинается кнопкой «Записать» в заголовке "
                  + "отмеченного окна."
                : "Параметры сохранены. Отметьте приложение галочкой «Вести», чтобы в заголовке "
                  + "его окон появилась кнопка записи.",
            BalloonIcon.Info);
    }

    /// <summary>
    /// Отмеченные приложения запускаются и закрываются в любой момент, а системного
    /// события «появилось новое окно нужного приложения» нет. Поэтому список окон
    /// пересматривается редким опросом — раз в три секунды нагрузки не создаёт.
    /// </summary>
    private void StartOverlayScanner()
    {
        RefreshTitleBarOverlays();

        _overlayScanTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        _overlayScanTimer.Tick += (_, _) => RefreshTitleBarOverlays();
        _overlayScanTimer.Start();
    }

    /// <summary>Пользователь отметил или снял приложение — сохраняем и обновляем кнопки в шапках.</summary>
    private async void OnTrackedAppChanged(string processName, bool tracked)
    {
        _settings = _settings.WithTracked(processName, tracked);
        await _settings.SaveAsync();
        RefreshTitleBarOverlays();
    }

    /// <summary>
    /// Приводит набор кнопок в заголовках в соответствие с отмеченными приложениями:
    /// добавляет их к новым окнам и убирает у закрытых или снятых с отметки.
    /// </summary>
    private void RefreshTitleBarOverlays()
    {
        if (!_settings.ShowTitleBarControls)
        {
            CloseAllOverlays();
            return;
        }

        try
        {
            var windows = WindowEnumerator.Enumerate()
                .Where(w => _settings.IsTracked(w.ProcessName))
                .ToList();

            var wanted = windows.Select(w => w.Handle).ToHashSet();

            foreach (var handle in _overlays.Keys.ToList())
            {
                if (wanted.Contains(handle) && WindowEnumerator.IsAlive(handle)) continue;

                _overlays[handle].Close();
                _overlays.Remove(handle);
            }

            foreach (var window in windows)
            {
                if (_overlays.ContainsKey(window.Handle)) continue;

                var appName = window.ProcessName ?? "приложение";
                var overlay = new TitleBarOverlay(
                    window.Handle, appName, () => _controller, _settings.OffsetFor(appName));
                overlay.RecordRequested += OnOverlayRecordRequested;
                WireAutoScreenshots(overlay);
                overlay.Closed += (_, _) => _overlays.Remove(window.Handle);
                overlay.Show();

                _overlays[window.Handle] = overlay;
            }
        }
        catch (Exception ex)
        {
            LogError("overlays", ex);
        }
    }

    /// <summary>
    /// Подключает галочку автоснимков к движку захвата. Движок живёт только во время
    /// сессии, поэтому связь идёт через функции, а не через прямую ссылку.
    /// Пользовательский выбор сохраняется в настройки: он же станет умолчанием
    /// для следующей встречи.
    /// </summary>
    private void WireAutoScreenshots(TitleBarOverlay overlay)
    {
        overlay.AutoScreenshotsGetter = () => _capture?.AutoScreenshotsEnabled
            ?? _settings.AutoScreenshots;

        overlay.AutoScreenshotsSetter = enabled =>
        {
            if (_capture is not null) _capture.AutoScreenshotsEnabled = enabled;

            _settings = _settings with { AutoScreenshots = enabled };
            _ = _settings.SaveAsync();
        };
    }

    private void CloseAllOverlays()
    {
        foreach (var overlay in _overlays.Values.ToList())
        {
            try { overlay.Close(); } catch (Exception) { }
        }
        _overlays.Clear();
    }

    /// <summary>Кнопка «Записать» в заголовке окна: сразу начинаем встречу именно с этим окном.</summary>
    private async void OnOverlayRecordRequested(TitleBarOverlay overlay)
        => await StartRecordingForWindowAsync(overlay.TargetWindow);

    /// <summary>
    /// Единственный путь к записи встречи: и кнопка в заголовке, и горячая клавиша ведут сюда.
    /// Параметры берутся из настроек — их задают в окне «Приложения и параметры записи».
    /// </summary>
    private async Task StartRecordingForWindowAsync(nint window)
    {
        if (_controller is not null && _controller.State is SessionState.Recording
            or SessionState.Paused or SessionState.Starting)
        {
            _tray?.ShowBalloonTip("MeetMemo", "Запись уже идёт", BalloonIcon.Info);
            return;
        }

        var models = new ModelManager(_settings.ModelsRoot);
        if (models.GetMissing().Count > 0)
        {
            var answer = ShowMessageNow(
                $"Модели распознавания не установлены (~{models.GetMissingBytes() / 1024 / 1024} МБ).\n\n"
                + "Без них запись пойдёт, но живой стенограммы не будет.\n\nСкачать сейчас?",
                "MeetMemo", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) await DownloadModelsAsync(models);
        }

        var candidate = WindowEnumerator.Enumerate()
            .FirstOrDefault(w => w.Handle == window);

        if (candidate is null)
        {
            _tray?.ShowBalloonTip("MeetMemo",
                "Это окно записать нельзя. Откройте окно встречи и нажмите «Записать» в его заголовке.",
                BalloonIcon.Warning);
            return;
        }

        var request = new SessionStartRequest
        {
            Title = candidate.Title.Length > 60 ? candidate.Title[..60] : candidate.Title,
            MeetingsRoot = _settings.MeetingsRoot,
            AudioMode = _settings.AudioMode,
            MicrophoneDeviceId = _settings.MicrophoneDeviceId,
            SaveAudioFiles = _settings.SaveAudioFiles,
            AutoScreenshotsEnabled = _settings.AutoScreenshots,
            Target = new TargetSelection
            {
                WindowHandle = candidate.Handle,
                ProcessId = candidate.ProcessId,
                ApplicationName = candidate.ProcessName,
                WindowTitle = candidate.Title,
                ExecutablePath = candidate.ExecutablePath
            }
        };

        await StartSessionAsync(request);
    }

    private async Task DownloadModelsAsync(ModelManager models)
    {
        _tray?.ShowBalloonTip("MeetMemo", "Загрузка моделей распознавания…", BalloonIcon.Info);
        try
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (_tray is not null)
                    _tray.ToolTipText = $"MeetMemo — загрузка {p.FileName}: {p.Fraction:P0}";
            });

            await models.DownloadMissingAsync(progress);
            if (_tray is not null) _tray.ToolTipText = "MeetMemo — готов к записи";
            _tray?.ShowBalloonTip("MeetMemo", "Модели загружены", BalloonIcon.Info);
        }
        catch (Exception ex)
        {
            ShowMessageNow($"Не удалось загрузить модели: {ex.Message}", "MeetMemo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartSessionAsync(SessionStartRequest request)
    {
        _store = new MeetingSessionStore();
        _audio = new AudioEngine();
        var degradation = new DegradationPolicy();

        _capture = new CaptureEngine(_store, degradation, new AutoScreenshotOptions
        {
            MinInterval = TimeSpan.FromSeconds(_settings.AutoScreenshotIntervalSeconds),
            ChangeThreshold = _settings.AutoScreenshotThreshold
        });

        if (_settings.ConfirmAutoScreenshots)
        {
            _capture.AutoScreenshotPending += (bitmap, title) =>
                Dispatcher.BeginInvoke(() => ShowScreenshotConfirmation(bitmap, title));
        }

        _asr = new AsrEngine(_store, _audio, _settings.ModelsRoot,
            AsrModelCatalog.FindById(_settings.LiveModelId) ?? AsrModelCatalog.GigaAmCtc);

        _controller = new SessionController(
            new ISessionParticipant[] { _capture, _asr, _audio },
            _store,
            degradation);

        _controller.StateChanged += OnStateChanged;
        _controller.SessionCompleted += OnSessionCompleted;
        _controller.UserFacingError += message =>
            Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip("MeetMemo", message, BalloonIcon.Error));

        _asr.SegmentRecognized += segment => Dispatcher.BeginInvoke(
            () => _subtitles?.ShowLiveText(segment.Text));

        var result = await _controller.SendAsync(new SessionCommand.Start(request));
        if (!result.Accepted)
        {
            ShowMessageNow($"Не удалось начать запись: {result.Message}", "MeetMemo",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await DisposeSessionAsync();
            return;
        }

        // Кнопки в заголовке должны знать, какое именно окно сейчас пишется:
        // у остальных отмеченных окон запись начать нельзя, пока идёт эта.
        TitleBarOverlay.RecordingTarget = request.Target?.WindowHandle ?? 0;

        if (request.Target is null)
        {
            // Записи всей системы не к чему привязать значок — показываем плавающую панель.
            ShowSystemOverlay();
        }
        else
        {
            // Управление записью живёт в значке на заголовке окна встречи. Если приложение
            // не отмечено и значка ещё нет, заводим его на время записи — иначе остановить
            // встречу можно было бы только горячей клавишей или из трея.
            EnsureOverlayForRecording(request);
        }

        // Субтитры одинаково нужны в обоих режимах: это единственное место, где видно,
        // что речь действительно распознаётся, а не просто идёт запись звука.
        ShowSubtitles();

        if (_settings.StartSound) System.Media.SystemSounds.Asterisk.Play();
    }

    /// <summary>
    /// Гарантирует, что у записываемого окна есть значок управления. Значок, созданный
    /// только ради записи, убирается вместе с её завершением.
    /// </summary>
    private void EnsureOverlayForRecording(SessionStartRequest request)
    {
        var handle = request.Target?.WindowHandle ?? 0;
        if (handle == 0 || _overlays.ContainsKey(handle)) return;

        try
        {
            var appName = request.Target?.ApplicationName ?? "приложение";
            var overlay = new TitleBarOverlay(
                handle, appName, () => _controller, _settings.OffsetFor(appName));
            overlay.RecordRequested += OnOverlayRecordRequested;
            overlay.Closed += (_, _) => _overlays.Remove(handle);
            overlay.Show();

            _overlays[handle] = overlay;
            _temporaryOverlay = handle;
        }
        catch (Exception ex)
        {
            LogError("overlay-recording", ex);
        }
    }

    /// <summary>Значок, заведённый только на время записи неотмеченного приложения.</summary>
    private nint _temporaryOverlay;

    /// <summary>Плавающая панель записи всей системы. Живёт только во время такой записи.</summary>
    private SystemOverlay? _systemOverlay;

    /// <summary>Субтитры распознавания внизу экрана. Живут только во время записи.</summary>
    private SubtitleOverlay? _subtitles;

    /// <summary>Карточки автоснимков, ожидающих подтверждения.</summary>
    private ScreenshotConfirmWindow? _confirm;

    /// <summary>
    /// Показывает карточку нового автоснимка. Окно одно на все снимки: карточки
    /// выстраиваются списком под значком, каждая со своим отсчётом.
    /// </summary>
    private void ShowScreenshotConfirmation(System.Drawing.Bitmap bitmap, string? windowTitle)
    {
        try
        {
            _confirm ??= new ScreenshotConfirmWindow(FindBadgeAnchor, SaveConfirmedScreenshot);
            _confirm.Add(bitmap, windowTitle);
        }
        catch (Exception ex)
        {
            LogError("screenshot-confirm", ex);

            // Показать не вышло — снимок всё равно должен попасть в пакет.
            SaveConfirmedScreenshot(new PendingScreenshot
            {
                Image = bitmap,
                Thumbnail = null!,
                WindowTitle = windowTitle
            });
        }
    }

    /// <summary>Куда цеплять список карточек: под значок записываемого окна.</summary>
    private Point? FindBadgeAnchor()
    {
        if (!_overlays.TryGetValue(TitleBarOverlay.RecordingTarget, out var overlay)) return null;
        if (!overlay.IsVisible) return null;

        return new Point(overlay.Left + overlay.Width, overlay.Top + 24);
    }

    private async void SaveConfirmedScreenshot(PendingScreenshot pending)
    {
        try
        {
            if (_capture is not null)
                await _capture.SaveConfirmedAsync(pending.Image, pending.WindowTitle);
        }
        catch (Exception ex)
        {
            LogError("screenshot-save", ex);
        }
        finally
        {
            pending.Image.Dispose();
        }
    }

    /// <summary>
    /// Показывает субтитры распознавания. Без моделей распознавания их показывать не за чем:
    /// строки в них не появятся, и пульсирующая точка будет обещать работу, которой нет.
    /// </summary>
    private void ShowSubtitles()
    {
        if (!_settings.ShowSubtitles) return;
        if (new ModelManager(_settings.ModelsRoot).GetMissing().Count > 0) return;

        try
        {
            _subtitles ??= new SubtitleOverlay();
            _subtitles.Reset();
            _subtitles.Show();
        }
        catch (Exception ex)
        {
            LogError("subtitles", ex);
        }
    }

    private void CloseSubtitles()
    {
        if (_subtitles is null) return;

        try
        {
            _subtitles.Close();
        }
        catch (Exception ex)
        {
            LogError("subtitles-close", ex);
        }
        finally
        {
            _subtitles = null;
        }
    }

    private void ShowSystemOverlay()
    {
        try
        {
            Point? saved = _settings.SystemOverlayX is { } x && _settings.SystemOverlayY is { } y
                ? new Point(x, y)
                : null;

            _systemOverlay = new SystemOverlay(() => _controller, saved);
            _systemOverlay.Show();
        }
        catch (Exception ex)
        {
            LogError("system-overlay", ex);
        }
    }

    private void CloseSystemOverlay()
    {
        if (_systemOverlay is null) return;

        try
        {
            // Панель перетаскиваемая: запоминаем, куда её поставил пользователь.
            var position = _systemOverlay.CurrentPosition;
            _settings = _settings with
            {
                SystemOverlayX = position.X,
                SystemOverlayY = position.Y
            };
            _ = _settings.SaveAsync();

            _systemOverlay.Close();
        }
        catch (Exception ex)
        {
            LogError("system-overlay-close", ex);
        }
        finally
        {
            _systemOverlay = null;
        }
    }

    private void OnStateChanged(SessionState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateTrayRecordingItems(state);

            if (_tray is null) return;

            (_tray.IconSource, _tray.ToolTipText) = state switch
            {
                SessionState.Recording => (LoadIcon("tray-recording.ico"), "MeetMemo — идёт запись"),
                SessionState.Paused => (LoadIcon("tray-paused.ico"), "MeetMemo — пауза"),
                SessionState.Finalizing => (LoadIcon("tray-paused.ico"), "MeetMemo — обработка…"),
                _ => (LoadIcon("tray-idle.ico"), "MeetMemo — готов к записи")
            };
        });
    }

    private void OnSessionCompleted(SessionResult result)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            TitleBarOverlay.RecordingTarget = 0;
            CloseSystemOverlay();
            CloseSubtitles();

            // Значок, заведённый только ради записи, вместе с ней и исчезает:
            // у неотмеченного приложения он висеть не должен.
            if (_temporaryOverlay != 0)
            {
                if (_overlays.Remove(_temporaryOverlay, out var temp)) temp.Close();
                _temporaryOverlay = 0;
            }

            // Сжимаем до показа карточки: иначе в составе пакета будет виден WAV,
            // которого через секунду уже не станет, и размер архива окажется неверным.
            await CompressAudioAsync(result.FolderPath);

            UpdateRecentMenu();

            var window = new CompletionWindow(result, _settings);
            window.Show();

            // Различение собеседников идёт после карточки, в фоне: на часовой записи это
            // минуты, и держать человека у пустого экрана ради него нельзя. Когда закончится,
            // стенограмма уже размечена — а если ZIP собрали раньше, его можно пересобрать
            // из «Последних встреч».
            _ = AnnotateSpeakersAsync(result.FolderPath);

            await DisposeSessionAsync();
        });
    }

    private async Task DisposeSessionAsync()
    {
        if (_controller is not null)
        {
            await _controller.DisposeAsync();
            _controller = null;
        }

        _asr?.Dispose();
        _asr = null;
        _capture?.Dispose();
        _capture = null;
        _audio?.Dispose();
        _audio = null;

        if (_store is not null)
        {
            await _store.DisposeAsync();
            _store = null;
        }
    }

    /// <summary>Незакрытая сессия означает аварийное завершение прошлого запуска (ТЗ 11.3).</summary>
    private async Task CheckRecoverableSessionsAsync()
    {
        try
        {
            var recovery = new RecoveryService();
            var sessions = recovery.Scan(_settings.MeetingsRoot);
            if (sessions.Count == 0) return;

            foreach (var session in sessions)
            {
                var answer = ShowMessageNow(
                    $"Найдена незавершённая встреча:\n\n«{session.Title}»\n"
                    + $"{session.StartLocal:dd.MM.yyyy HH:mm}\n"
                    + $"Реплик в стенограмме: {session.TranscriptLines}\n"
                    + $"{(session.HasAudio ? "Аудио сохранено" : "Аудио не сохранялось")}\n\n"
                    + "Восстановить пакет встречи?",
                    "MeetMemo — восстановление",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);


                if (answer == MessageBoxResult.Yes)
                    await recovery.RecoverAsync(session.FolderPath);
            }

            UpdateRecentMenu();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Проверка восстановления не удалась: {ex.Message}");
        }
    }

    /// <summary>
    /// Журнал ошибок приложения (вне сессии). Нужен, чтобы сбои в обработчиках меню
    /// и фоновых действиях оставляли след: иначе окно «мигает», а причины не найти.
    /// Текст стенограммы и содержимое снимков сюда не попадают.
    /// </summary>
    private static void LogError(string where, Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetMemo", "app-errors.log");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex.GetType().Name}: {ex.Message}"
                + Environment.NewLine + ex.StackTrace + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception)
        {
            // Журнал — вспомогательный механизм: его недоступность не должна ничего ломать.
        }
    }

    /// <summary>
    /// Показывает в меню трея управление записью, пока она идёт. Это единственный способ
    /// остановить запись всей системы: окна-цели у неё нет, значит нет и значка в заголовке.
    /// </summary>
    private void UpdateTrayRecordingItems(SessionState state)
    {
        if (_tray?.ContextMenu is null) return;

        var recording = state is SessionState.Recording or SessionState.Paused;
        var visibility = recording ? Visibility.Visible : Visibility.Collapsed;

        foreach (var header in new[] { "Остановить запись", "Снимок экрана" })
        {
            var item = _tray.ContextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(i => (i.Header as string) == header);

            if (item is not null) item.Visibility = visibility;
        }

        var newMeeting = _tray.ContextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(i => (i.Header as string) == "Приложения и параметры записи");
        if (newMeeting is not null) newMeeting.IsEnabled = !recording;

        var everything = _tray.ContextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(i => (i.Header as string) == "Записать весь звук системы");
        if (everything is not null) everything.IsEnabled = !recording;
    }

    private void OnStopRecordingClick(object sender, RoutedEventArgs e)
        => DeferToUi(async () =>
        {
            if (_controller is not null) await _controller.SendAsync(new SessionCommand.Stop());
        });

    private void OnDesktopShotClick(object sender, RoutedEventArgs e)
        => DeferToUi(async () =>
        {
            if (_controller is null) return;

            var result = await _controller.SendAsync(new SessionCommand.CaptureDesktop());
            if (!result.Accepted)
                _tray?.ShowBalloonTip("MeetMemo", result.Message ?? "Снимок не сделан", BalloonIcon.Warning);
        });

    /// <summary>
    /// Значок перетащили на новое место. Запоминаем его для этого приложения и сразу
    /// переставляем остальные его окна: в одном приложении значок должен стоять одинаково.
    /// </summary>
    private async void OnTitleBarOffsetChanged(string applicationName, double offset)
    {
        foreach (var overlay in _overlays.Values)
        {
            if (!string.Equals(overlay.ApplicationName, applicationName, StringComparison.OrdinalIgnoreCase))
                continue;

            overlay.Offset = offset;
            overlay.Reposition();
        }

        _settings = _settings.WithOffset(applicationName, offset);

        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            LogError("save-title-bar-offset", ex);
        }
    }

    /// <summary>
    /// Размечает, кто из собеседников говорит, — по тембрам в звуке приложения.
    /// Ошибка разметки не трогает пакет: стенограмма остаётся как была.
    /// </summary>
    private async Task AnnotateSpeakersAsync(string folderPath)
    {
        try
        {
            var diarizer = new SpeakerDiarizer(_settings.ModelsRoot);
            if (!diarizer.ModelsInstalled) return;

            var speakers = await diarizer.AnnotateMeetingAsync(folderPath);

            if (speakers >= 2)
            {
                _tray?.ShowBalloonTip("MeetMemo",
                    $"В записи различено голосов: {speakers}. Стенограмма размечена по собеседникам.",
                    BalloonIcon.Info);
            }
        }
        catch (Exception ex)
        {
            LogError("diarize", ex);
        }
    }

    /// <summary>
    /// Переводит записанные дорожки в MP3. Час речи весит около двадцати мегабайт вместо
    /// сотни с лишним, а разницы на слух нет. Если сжать не вышло, WAV остаётся на месте:
    /// экономия места не стоит записи встречи.
    /// </summary>
    private static async Task CompressAudioAsync(string folderPath)
    {
        try
        {
            var folder = new MeetingFolder(folderPath);
            await Task.Run(() =>
            {
                AudioCompressor.CompressInPlace(folder.MicrophoneAudio());
                AudioCompressor.CompressInPlace(folder.ApplicationAudio());
            });
        }
        catch (Exception ex)
        {
            LogError("audio-compress", ex);
        }
    }

    /// <summary>Подменю последних встреч — минимальный доступ к истории из трея (ТЗ 5.1).</summary>
    private void UpdateRecentMenu()
    {
        if (_tray?.ContextMenu is null) return;

        var recentItem = _tray.ContextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(i => (i.Header as string) == "Последние встречи");
        if (recentItem is null) return;

        recentItem.Items.Clear();

        try
        {
            if (!Directory.Exists(_settings.MeetingsRoot))
            {
                recentItem.IsEnabled = false;
                return;
            }

            var folders = new DirectoryInfo(_settings.MeetingsRoot)
                .GetDirectories()
                .OrderByDescending(d => d.LastWriteTime)
                .Take(10)
                .ToList();

            recentItem.IsEnabled = folders.Count > 0;

            foreach (var folder in folders)
            {
                var item = new MenuItem { Header = folder.Name };
                var path = folder.FullName;

                // Открываем ту же карточку, что и сразу после встречи: собрать ZIP
                // можно в любой момент, а не только пока окно не закрыли.
                item.Click += (_, _) => DeferToUi(() =>
                {
                    var card = CompletionWindow.ForFolder(path, _settings);
                    if (card is not null)
                    {
                        card.Show();
                    }
                    else
                    {
                        // Папка без session.json — не встреча MeetMemo, показывать в карточке нечего.
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                        {
                            UseShellExecute = true
                        });
                    }
                });

                recentItem.Items.Add(item);
            }
        }
        catch (Exception)
        {
            recentItem.IsEnabled = false;
        }
    }

    private void OnOpenMeetingsRootClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.MeetingsRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.MeetingsRoot}\"")
        {
            UseShellExecute = true
        });
    }

    private void OnCheckModelsClick(object sender, RoutedEventArgs e)
    {
        // Меню трея должно успеть закрыться до появления диалога.
        DeferToUi(async () =>
            {
                var models = new ModelManager(_settings.ModelsRoot);
                var missing = models.GetMissing();

                if (missing.Count == 0)
                {
                    var installed = string.Join("\n", AsrModelCatalog.Required
                        .Select(m => $"  • {m.DisplayName} — {m.License}"));

                    ShowMessageNow(
                        $"Модели распознавания установлены:\n\n{installed}\n\n"
                        + $"Каталог: {models.ModelsRoot}",
                        "MeetMemo — модели", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var answer = ShowMessageNow(
                    $"Не хватает моделей: {string.Join(", ", missing.Select(m => m.DisplayName))}\n"
                    + $"Объём загрузки: ~{models.GetMissingBytes() / 1024 / 1024} МБ\n\nСкачать сейчас?",
                    "MeetMemo — модели", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer == MessageBoxResult.Yes) await DownloadModelsAsync(models);
            });
    }

    private void ShowLegalNotice()
    {
        ShowMessageNow(
            "MeetMemo записывает звук встречи и делает снимки выбранного окна.\n\n"
            + "Предупредите участников о записи и соблюдайте правила вашей организации "
            + "и применимое законодательство.\n\n"
            + "Во время записи в области уведомлений виден красный индикатор, "
            + "а на экране — панель с таймером.",
            "MeetMemo — перед первой записью",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        _settings = _settings with { LegalNoticeAccepted = true };
        _ = _settings.SaveAsync();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

        ShowMessage(
            $"MeetMemo {version} — локальная стенография встреч.\n\n"
            + "Распознавание речи: sherpa-onnx + GigaAM v2 (русский), локально на этом компьютере.\n"
            + "Финальный проход: Whisper.net.\n\n"
            + "Звук и стенограмма не покидают компьютер. Единственная передача данных — "
            + "ZIP-архив, который вы сами загружаете в Claude.\n\n"
            + "Горячие клавиши:\n"
            + "  Ctrl+Alt+M — начать / завершить\n"
            + "  Ctrl+Alt+P — пауза / продолжить\n"
            + "  Ctrl+Alt+S — снимок окна\n"
            + "  Ctrl+Alt+D — снимок экрана\n"
            + "  Ctrl+Alt+I — маркер «Важно»\n\n"
            + $"Папка встреч: {_settings.MeetingsRoot}\n"
            + $"Модели: {_settings.ModelsRoot}",
            "О программе MeetMemo");
    }

    private async void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (_controller is not null && _controller.State is SessionState.Recording or SessionState.Paused)
        {
            var answer = ShowMessageNow(
                "Идёт запись встречи. Завершить её и выйти?",
                "MeetMemo", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            await _controller.SendAsync(new SessionCommand.Stop());
        }

        Shutdown();
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        _overlayScanTimer?.Stop();
        CloseSystemOverlay();
        CloseAllOverlays();

        await DisposeSessionAsync();
        _hotkeys?.Dispose();
        _tray?.Dispose();

        if (_dialogOwner is not null)
        {
            _dialogOwner.Close();
            _dialogOwner = null;
        }

        _instanceMutex?.Dispose();
    }
}
