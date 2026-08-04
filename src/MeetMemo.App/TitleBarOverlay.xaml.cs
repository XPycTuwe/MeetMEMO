using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeetMemo.Capture;
using MeetMemo.Capture.Interop;
using MeetMemo.Core;

namespace MeetMemo.App;

/// <summary>
/// Кнопки управления записью, встроенные в заголовок окна отмеченного приложения.
///
/// Окно чужое, поэтому «встроить» кнопки по-настоящему нельзя: панель — отдельное окно
/// поверх заголовка, которое следует за целевым окном. Отсюда три требования:
/// она не забирает фокус ввода, исключается из собственных снимков и прячется,
/// когда целевое окно свёрнуто или ушло на задний план.
/// </summary>
public partial class TitleBarOverlay : Window
{
    /// <summary>
    /// Отступ от правого края окна, за которым начинается зона чужих кнопок.
    ///
    /// Системные «свернуть/развернуть/закрыть» занимают около 140 точек, но приложения
    /// нередко ставят слева от них своё: у Яндекс Браузера это аватар профиля и меню,
    /// и значок налезал прямо на них. Поэтому по умолчанию отступ заметно больше,
    /// а точное значение подстраивается настройкой `title_bar_offset`.
    /// </summary>
    public static double SystemButtonsWidth { get; set; } = 250;

    /// <summary>
    /// Пользователь перетащил значок — новый отступ от правого края. Приложение сохраняет
    /// его в настройки и переставляет остальные значки: место должно быть общим для всех окон.
    /// </summary>
    public static event Action<double>? OffsetChanged;

    private bool _dragging;
    private double _dragStartScreenX;
    private double _dragStartOffset;

    private readonly nint _targetWindow;

    /// <summary>
    /// Контроллер запрашивается функцией, а не хранится ссылкой: значки живут всё время
    /// работы приложения, а контроллер существует только во время сессии — создаётся
    /// при старте записи и уничтожается после её завершения.
    /// </summary>
    private readonly Func<SessionController?> _controllerProvider;

    private readonly WindowTracker _tracker;
    private readonly DispatcherTimer _stateTimer;

    private bool _targetMinimized;

    private SessionController? _controller => _controllerProvider();

    public TitleBarOverlay(
        nint targetWindow, string applicationName, Func<SessionController?> controllerProvider)
    {
        InitializeComponent();

        _targetWindow = targetWindow;
        _controllerProvider = controllerProvider;
        ApplicationName = applicationName;

        _tracker = new WindowTracker(targetWindow);
        _tracker.Moved += bounds => Dispatcher.BeginInvoke(() => PositionOver(bounds));
        _tracker.MinimizedChanged += minimized => Dispatcher.BeginInvoke(() =>
        {
            _targetMinimized = minimized;
            UpdateVisibility();
        });
        _tracker.Closed += () => Dispatcher.BeginInvoke(Close);

        // Состояние сессии меняется не только по нажатию наших кнопок (есть горячие
        // клавиши и плавающая панель), поэтому подписи обновляем по таймеру. Тем же
        // тактом ловится и смена активного окна: полсекунды задержки на переключении
        // между приложениями были бы заметны, значок появлялся бы с опозданием.
        _stateTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _stateTimer.Tick += (_, _) =>
        {
            UpdateState();
            UpdateHoverState();
            // Страховка: если хук пропустил перемещение (например, окно пересоздалось
            // или сменило монитор), панель всё равно вернётся на своё место.
            var bounds = WindowTracker.GetBounds(_targetWindow);
            if (!bounds.IsEmpty && bounds != _lastBounds) PositionOver(bounds);
        };
        _stateTimer.Start();

        // Ширина панели меняется, когда разворачивается управление: после каждого
        // изменения её нужно снова прижать к системным кнопкам. Границы окна берём
        // свежие — кэш может быть ещё не заполнен на самом первом измерении,
        // из-за чего панель однажды вставала не на своё место.
        // Позицию задаём после того, как компоновка завершена: WPF при SizeToContent
        // сам двигает окно, расширяя его вправо, и перетирает выставленный Left,
        // если сделать это прямо в обработчике изменения размера.
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => PositionOver(WindowTracker.GetBounds(_targetWindow)));

        Loaded += (_, _) => PositionOver(WindowTracker.GetBounds(_targetWindow));

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _stateTimer.Stop();
            _tracker.Dispose();
        };
    }

    public string ApplicationName { get; }

    public nint TargetWindow => _targetWindow;

    /// <summary>Пользователь нажал «Записать» в заголовке этого окна.</summary>
    public event Action<TitleBarOverlay>? RecordRequested;

    /// <summary>
    /// Перетаскивание значка по горизонтали. Экранные координаты берём напрямую у системы:
    /// окно во время перетаскивания само едет за курсором, и позиция относительно него
    /// всё время «догоняет» — сдвиг получался бы вдвое меньше настоящего.
    /// </summary>
    /// <summary>Переставить значок после того, как отступ поменяли на другом окне.</summary>
    public void Reposition() => PositionOver(WindowTracker.GetBounds(_targetWindow));

    private void OnBadgeDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out var cursor)) return;

        _dragging = true;
        _dragStartScreenX = cursor.X;
        _dragStartOffset = SystemButtonsWidth;
        Badge.CaptureMouse();
        e.Handled = true;
    }

    private void OnBadgeDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out var cursor)) return;

        var scale = _lastBounds.Dpi > 0 ? _lastBounds.Dpi / 96.0 : 1.0;

        // Тянем влево — значок отходит от правого края, поэтому знак обратный.
        var moved = (_dragStartScreenX - cursor.X) / scale;

        // Границы: у правого края значок налезет на системные кнопки, слишком далеко
        // влево — уедет за пределы даже широкого окна.
        SystemButtonsWidth = Math.Clamp(_dragStartOffset + moved, 40, 1200);

        PositionOver(WindowTracker.GetBounds(_targetWindow));
    }

    private void OnBadgeDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        Badge.ReleaseMouseCapture();
        e.Handled = true;

        OffsetChanged?.Invoke(SystemButtonsWidth);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Панель не активируется мышью: фокус остаётся в окне встречи.
        var exStyle = Win32.GetWindowLongW(hwnd, Win32.GWL_EXSTYLE);
        SetWindowLongW(hwnd, Win32.GWL_EXSTYLE,
            exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);

        // Собственные снимки не должны содержать наши же кнопки.
        Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);

        _targetMinimized = Win32.IsIconic(_targetWindow);
        PositionOver(WindowTracker.GetBounds(_targetWindow));
        UpdateState();
        UpdateVisibility();
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int index, int newLong);

    /// <summary>
    /// Ставит панель в заголовок целевого окна — слева от системных кнопок.
    ///
    /// Границы окна приходят в физических пикселях, а Left/Top у WPF-окна задаются
    /// в аппаратно-независимых единицах, поэтому переводим их по DPI монитора,
    /// на котором сейчас окно. Ширина панели уже в этих единицах — её не масштабируем.
    /// </summary>
    private void PositionOver(WindowBounds bounds, double? width = null)
    {
        if (bounds.IsEmpty) return;

        _lastBounds = bounds;

        var scale = bounds.Dpi / 96.0;

        // Ширина окна постоянна, содержимое внутри прижато вправо — поэтому правый
        // край окна всегда стоит на одном месте, чем бы ни было заполнено содержимое.
        var rightEdge = (bounds.Left + bounds.Width) / scale;
        var left = rightEdge - SystemButtonsWidth - Width;

        // У узкого окна места слева может не хватить: тогда прижимаем к левому краю.
        var leftEdge = bounds.Left / scale;
        if (left < leftEdge) left = leftEdge;

        Left = left;
        Top = bounds.Top / scale + TitleBarPadding;
    }

    /// <summary>
    /// Отступ от верхней границы окна. Высота панели постоянна (28), поэтому с этим
    /// отступом она садится по центру стандартного заголовка и не прыгает
    /// при разворачивании управления.
    /// </summary>
    private const double TitleBarPadding = 3;

    private WindowBounds _lastBounds;

    private void UpdateVisibility()
    {
        // Свёрнутое окно заголовка не показывает — прятать панель обязательно,
        // иначе она повиснет посреди пустого экрана.
        if (_targetMinimized || !Win32.IsWindow(_targetWindow))
        {
            if (IsVisible) Hide();
            return;
        }

        // Значок висит поверх всех окон, поэтому у отмеченного приложения он был бы
        // виден и тогда, когда само окно закрыто другими: при пяти отмеченных
        // приложениях экран покрывался значками, не относящимися к тому, что видно.
        // Показываем значок только на окне, с которым человек работает прямо сейчас.
        if (!IsTargetActive())
        {
            if (IsVisible) Hide();
            return;
        }

        if (!IsVisible) Show();
    }

    /// <summary>
    /// Работает ли пользователь сейчас именно с этим окном. Учитываем и дочерние окна:
    /// у открытого модального диалога активен он, но окно-владелец остаётся тем же,
    /// и прятать значок из-за диалога не за чем.
    /// </summary>
    private bool IsTargetActive()
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == _targetWindow) return true;

        return Win32.GetAncestor(foreground, Win32.GA_ROOTOWNER) == _targetWindow;
    }

    private void UpdateState()
    {
        UpdateVisibility();

        var state = _controller?.State ?? SessionState.Idle;
        var recordingHere = IsRecordingThisWindow(state);

        if (recordingHere)
        {
            var elapsed = _controller!.Elapsed;
            StatusText.Text = elapsed.ToString(@"hh\:mm\:ss");

            // Значок сам по себе показывает состояние: во время записи он красный,
            // на паузе жёлтый. Разворачивать панель ради этого не нужно.
            var color = state == SessionState.Paused
                ? Color.FromRgb(0xFB, 0xC0, 0x2D)
                : Color.FromRgb(0xE5, 0x39, 0x35);

            StatusDot.Fill = new SolidColorBrush(color);
            BadgeRing.Stroke = new SolidColorBrush(color) { Opacity = 0.75 };

            RecordButton.Content = "Стоп";
            RecordButton.IsEnabled = true;
            PauseButton.Content = state == SessionState.Paused ? "Продолжить" : "Пауза";
            PauseButton.Visibility = Visibility.Visible;
            ShotButton.Visibility = Visibility.Visible;
            ImportantButton.Visibility = Visibility.Visible;

            // Быстрый снимок доступен прямо в заголовке, без разворачивания панели.
            // На паузе он бессмыслен: кадры в это время в пакет не попадают.
            QuickShotButton.Visibility = state == SessionState.Recording
                ? Visibility.Visible
                : Visibility.Collapsed;

            AutoShotsBox.Visibility = Visibility.Visible;
            SyncAutoShotsBox();
        }
        else
        {
            StatusText.Text = "MeetMemo";
            // В покое значок нейтрально-серый: он не должен выделяться в чужом заголовке.
            var idle = Color.FromRgb(0x9C, 0xA3, 0xAF);
            StatusDot.Fill = new SolidColorBrush(idle);
            BadgeRing.Stroke = new SolidColorBrush(idle) { Opacity = 0.7 };

            RecordButton.Content = "Записать";
            PauseButton.Visibility = Visibility.Collapsed;
            ShotButton.Visibility = Visibility.Collapsed;
            ImportantButton.Visibility = Visibility.Collapsed;
            QuickShotButton.Visibility = Visibility.Collapsed;
            AutoShotsBox.Visibility = Visibility.Collapsed;

            // Пока идёт запись другого окна, начать вторую встречу нельзя.
            RecordButton.IsEnabled = state is SessionState.Idle
                or SessionState.Completed or SessionState.Failed;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>Сколько тактов таймера курсор уже вне панели — для задержки сворачивания.</summary>
    private int _awayTicks;

    /// <summary>
    /// Разворачивание при наведении.
    ///
    /// Положение курсора проверяется напрямую, а не по событиям MouseEnter/MouseLeave:
    /// окно панели создано без активации и с прозрачным фоном, и события наведения
    /// у него срабатывают ненадёжно. Опрос идёт вместе с обновлением состояния,
    /// отдельного таймера не нужно.
    /// </summary>
    private void UpdateHoverState()
    {
        if (!GetCursorPos(out var cursor)) return;

        var bounds = _lastBounds;
        var scale = bounds.Dpi > 0 ? bounds.Dpi / 96.0 : 1.0;

        // Окно шире видимого содержимого, и его прозрачная часть не должна считаться
        // наведением: иначе панель разворачивалась бы от курсора, проходящего далеко
        // левее значка. Считаем только по тому, что реально нарисовано.
        var contentWidth = ButtonsPanel.ActualWidth > 0 ? ButtonsPanel.ActualWidth : 24;

        var right = (Left + Width) * scale;
        var left = right - contentWidth * scale;
        var top = Top * scale;
        var bottom = top + Height * scale;

        // Небольшой запас вокруг панели: иначе она схлопывается от дрожания курсора
        // на самой границе.
        const double margin = 6;
        var inside = cursor.X >= left - margin && cursor.X <= right + margin
                  && cursor.Y >= top - margin && cursor.Y <= bottom + margin;

        if (inside)
        {
            _awayTicks = 0;
            ControlsPanel.Visibility = Visibility.Visible;
            return;
        }

        if (ControlsPanel.Visibility != Visibility.Visible) return;

        // Пара тактов отсрочки, чтобы панель не исчезала при переходе между кнопками.
        _awayTicks++;
        if (_awayTicks < 2) return;

        ControlsPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Идёт ли запись именно этого окна (а не какого-то другого).</summary>
    private bool IsRecordingThisWindow(SessionState state)
    {
        if (state is not (SessionState.Recording or SessionState.Paused)) return false;
        return RecordingTarget == _targetWindow;
    }

    /// <summary>Окно, которое сейчас записывается. Устанавливается приложением при старте сессии.</summary>
    public static nint RecordingTarget { get; set; }

    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        var state = _controller?.State ?? SessionState.Idle;

        if (_controller is not null && IsRecordingThisWindow(state))
        {
            await _controller.SendAsync(new SessionCommand.Stop());
            return;
        }

        RecordRequested?.Invoke(this);
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;

        SessionCommand command = _controller.State == SessionState.Paused
            ? new SessionCommand.Resume()
            : new SessionCommand.Pause();
        await _controller.SendAsync(command);
    }

    private async void OnShotClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        await _controller.SendAsync(new SessionCommand.CaptureWindow());
    }

    private async void OnImportantClick(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        await _controller.SendAsync(new SessionCommand.MarkImportant());
    }

    /// <summary>Кто читает и меняет состояние автоснимков — задаётся приложением.</summary>
    public Func<bool>? AutoScreenshotsGetter { get; set; }

    public Action<bool>? AutoScreenshotsSetter { get; set; }

    private bool _syncingAutoShots;

    /// <summary>
    /// Приводит галочку в соответствие с реальным состоянием: автоснимки могут быть
    /// отключены не только отсюда (например, автоматически при нехватке места).
    /// </summary>
    private void SyncAutoShotsBox()
    {
        if (AutoScreenshotsGetter is null) return;

        var actual = AutoScreenshotsGetter();
        if (AutoShotsBox.IsChecked == actual) return;

        _syncingAutoShots = true;
        AutoShotsBox.IsChecked = actual;
        _syncingAutoShots = false;
    }

    private void OnAutoShotsToggled(object sender, RoutedEventArgs e)
    {
        // Программное обновление галочки не должно возвращаться обратно в движок.
        if (_syncingAutoShots) return;
        AutoScreenshotsSetter?.Invoke(AutoShotsBox.IsChecked == true);
    }
}
