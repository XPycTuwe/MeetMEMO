using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeetMemo.Capture.Interop;
using MeetMemo.Contracts;

namespace MeetMemo.App;

/// <summary>Одна реплика в стенограмме: что сказано, кем и что с этим можно сделать.</summary>
public sealed class SpokenLine : INotifyPropertyChanged
{
    private string _who = "…";
    private string? _printId;
    private bool _known;

    public required string Text { get; init; }

    public required AudioChannel Channel { get; init; }

    /// <summary>Отпечаток этой фразы — по нему запоминается голос именно этого говорящего.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>Сама речь: её дают послушать, когда называют человека.</summary>
    public float[]? Audio { get; set; }

    /// <summary>Узнанный голос из памяти, null — если голос незнаком.</summary>
    public string? PrintId
    {
        get => _printId;
        set { _printId = value; Changed(nameof(ActionLabel)); }
    }

    public string Who
    {
        get => _who;
        set { _who = value; Changed(nameof(Who)); }
    }

    /// <summary>Голос узнан: имя показываем зелёным, а не вопросительным жёлтым.</summary>
    public bool Known
    {
        get => _known;
        set
        {
            _known = value;
            Changed(nameof(WhoBrush));
            Changed(nameof(ActionLabel));
            Changed(nameof(ConfirmVisibility));
        }
    }

    public Brush WhoBrush => Channel == AudioChannel.Microphone
        ? new SolidColorBrush(Color.FromRgb(0x9C, 0xC6, 0xEA))
        : Known
            ? new SolidColorBrush(Color.FromRgb(0x7F, 0xD1, 0xA8))
            : new SolidColorBrush(Color.FromRgb(0xF3, 0xC1, 0x60));

    public Brush TextBrush => new SolidColorBrush(Color.FromRgb(0xF3, 0xF6, 0xFA));

    /// <summary>Свой микрофон называть не нужно; знакомого можно поправить, чужого — назвать.</summary>
    public string ActionLabel => Known ? "Не он" : "Назвать";

    public Visibility ActionVisibility =>
        Channel == AudioChannel.Microphone || Embedding is null
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>
    /// Подтвердить можно только узнанный голос: у незнакомого подтверждать нечего,
    /// его сначала называют. Своему микрофону подтверждение тоже ни к чему.
    /// </summary>
    public Visibility ConfirmVisibility =>
        Known && ActionVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Стенограмма беседы поверх экрана: последние фразы списком, у каждой свой говорящий.
///
/// Раньше строка была одна, и назвать можно было только последнего услышанного. Пока
/// соображаешь, кто это, человек договаривает и начинает говорить следующий — имя уходит
/// не тому. Теперь фразы висят несколько штук, и каждая называется отдельно.
///
/// С мышью тут пополам: WS_EX_NOACTIVATE глотает нажатия, но перемещения пропускает.
/// Поэтому захват ловится опросом координат, а сама проводка идёт по живым событиям —
/// на одном опросе панель ехала рывками.
/// </summary>
public partial class SubtitleOverlay : Window
{
    /// <summary>Сколько реплик держать на экране. Больше — закрывает встречу собой.</summary>
    private const int MaxLines = 4;

    /// <summary>Через сколько тишины стенограмма возвращается к «слушаю…».</summary>
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(20);

    private readonly ObservableCollection<SpokenLine> _lines = new();
    private readonly DispatcherTimer _timer;
    private DateTime _lastTextUtc = DateTime.UtcNow;
    private bool _mouseWasDown;

    public SubtitleOverlay()
    {
        InitializeComponent();
        Lines.ItemsSource = _lines;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PlaceAtBottom();

        // Движение мыши до окна доходит, а нажатия нет: WS_EX_NOACTIVATE глотает
        // кнопки. Захват ловим опросом, а саму проводку ведём по живым событиям.
        PreviewMouseMove += OnMouseMoved;

        // Тем же тактом ловятся нажатия на «Назвать» и «Не он». Во время
        // перетаскивания такт учащается — иначе панель едет рывками.
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(IdlePollMs)
        };
        _timer.Tick += (_, _) =>
        {
            HandleClicks();
            DropToIdleIfSilent();
        };
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Человек выбрал реплику, чтобы назвать или поправить говорящего.</summary>
    public event Action<SpokenLine>? LineAction;

    /// <summary>
    /// Голос подтвердили: «да, это он». Отпечаток уточняется усреднением с весом
    /// накопленных подтверждений — узнавание после этого увереннее, а одна неудачная
    /// фраза не перебивает накопленное.
    /// </summary>
    public event Action<SpokenLine>? LineConfirmed;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        var exStyle = Win32.GetWindowLongW(hwnd, Win32.GWL_EXSTYLE);
        SetWindowLongW(hwnd, Win32.GWL_EXSTYLE,
            exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);

        // Стенограмма не должна попасть в собственные снимки встречи.
        Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int index, int newLong);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int VK_LBUTTON = 0x01;

    /// <summary>
    /// Правый нижний угол, а не центр экрана: посередине стенограмма ложилась поверх
    /// кнопок самой встречи — микрофона, камеры, завершения звонка. У края она никому
    /// не мешает, а движение строк всё равно попадает в поле зрения.
    /// </summary>
    private void PlaceAtBottom()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(620, area.Width * 0.42);

        // Панель, которую поставили руками, не двигаем: иначе она прыгала бы обратно
        // в угол на каждой новой реплике.
        if (_placed)
        {
            KeepOnScreen(area);
            return;
        }

        Left = area.Right - Width - 24;
        Top = area.Bottom - ActualHeight - 24;
    }

    /// <summary>
    /// Держит панель в пределах экрана. Высота меняется с числом реплик, и панель,
    /// поставленную у нижнего края, новая строка вытолкнула бы под панель задач.
    /// </summary>
    private void KeepOnScreen(Rect area)
    {
        if (Top + ActualHeight > area.Bottom) Top = area.Bottom - ActualHeight;
        if (Top < area.Top) Top = area.Top;
        if (Left + Width > area.Right) Left = area.Right - Width;
        if (Left < area.Left) Left = area.Left;
    }

    /// <summary>Новая распознанная реплика в конец списка.</summary>
    public void AddLine(SpokenLine line)
    {
        IdleRow.Visibility = Visibility.Collapsed;

        _lines.Add(line);
        while (_lines.Count > MaxLines) _lines.RemoveAt(0);

        _lastTextUtc = DateTime.UtcNow;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceAtBottom);
    }

    /// <summary>Голос узнан позже, чем показана фраза, — подставляем имя на месте.</summary>
    public void UpdateSpeaker(SpokenLine line, string who, string? printId, bool known)
    {
        line.Who = who;
        line.PrintId = printId;
        line.Known = known;
    }

    private void DropToIdleIfSilent()
    {
        if (_lines.Count == 0 || DateTime.UtcNow - _lastTextUtc < IdleAfter) return;

        _lines.Clear();
        IdleRow.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceAtBottom);
    }

    public void Reset()
    {
        _lines.Clear();
        IdleRow.Visibility = Visibility.Visible;
        _lastTextUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Нажатие на кнопку строки. Строки одинаковой высоты и идут сверху вниз — по вертикали
    /// и находим нужную, а кнопка занимает правый край.
    /// </summary>
    private void HandleClicks()
    {
        var down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        var hasCursor = GetCursorPos(out var cursor);

        if (down)
        {
            if (hasCursor) HandleDrag(cursor);
            _mouseWasDown = true;
            return;
        }

        if (!_mouseWasDown) return;
        _mouseWasDown = false;

        // Кнопку отпустили — начатое перетаскивание закончилось в любом случае.
        var wasDragging = _dragging;
        _dragStart = null;
        _dragging = false;
        _timer.Interval = TimeSpan.FromMilliseconds(IdlePollMs);

        // Панель тащили, а не нажимали: место запомнили, действие не выполняем.
        if (wasDragging)
        {
            PositionChanged?.Invoke(new Point(Left, Top));
            return;
        }

        if (!hasCursor) return;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var x = cursor.X / scale;
        var y = cursor.Y / scale;

        if (x < Left || x > Left + Width || y < Top || y > Top + ActualHeight) return;

        if (Probe(new Point(x - Left, y - Top)) is not { } hit) return;

        switch (hit.Action)
        {
            case "collapse":
                ToggleCollapsed();
                return;

            case "confirm" when hit.Line is { } confirmed:
                LineConfirmed?.Invoke(confirmed);
                return;

            case "rename" when hit.Line is { } renamed:
                LineAction?.Invoke(renamed);
                return;
        }
    }

    /// <summary>
    /// Что находится под курсором. Раньше строку вычисляли делением высоты панели
    /// на число реплик, но реплики разной высоты: длинная переносится на две-три
    /// строки, и нажатие уходило соседней. Спрашиваем у самого дерева — оно знает
    /// точно, и заодно различает две кнопки в одной строке.
    /// </summary>
    private (string Action, SpokenLine? Line)? Probe(Point windowPoint)
    {
        var found = VisualTreeHelper.HitTest(this, windowPoint)?.VisualHit;

        for (var node = found; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Tag: string action } element)
                return (action, element.DataContext as SpokenLine);
        }

        return null;
    }

    /// <summary>Панель перетащили — приложение запоминает место между встречами.</summary>
    public event Action<Point>? PositionChanged;

    private System.Drawing.Point? _dragStart;
    private Point _windowAtDragStart;
    private bool _dragging;
    private bool _collapsed;

    /// <summary>Место выбрано человеком — автоматическое размещение его больше не трогает.</summary>
    private bool _placed;

    /// <summary>Насколько нужно увести мышь, чтобы это считалось перетаскиванием, а не щелчком.</summary>
    private const int DragThreshold = 4;

    /// <summary>Такт опроса во время перетаскивания: обычные 120 мс дают видимые рывки.</summary>
    private const int DragPollMs = 15;

    /// <summary>Такт в покое. Реже — и нажатие на кнопку начинает «залипать».</summary>
    private const int IdlePollMs = 120;

    /// <summary>
    /// Перетаскивание за шапку. Как и кнопки, отслеживается опросом координат:
    /// окно создано без активации, и события мыши до него не доходят.
    /// </summary>
    private void HandleDrag(POINT cursor)
    {
        if (_dragStart is null)
        {
            if (_mouseWasDown) return;

            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var local = new Point(cursor.X / scale - Left, cursor.Y / scale - Top);

            // Тянуть можно за что угодно, кроме кнопок. Раньше держались только за
            // шапку, а это полоска в полтора десятка пикселей: попасть в неё с первого
            // раза не выходило, приходилось нащупывать.
            if (local.X < 0 || local.Y < 0 || local.X > Width || local.Y > ActualHeight) return;
            if (Probe(local) is not null) return;

            _dragStart = new System.Drawing.Point(cursor.X, cursor.Y);
            _windowAtDragStart = new Point(Left, Top);
            return;
        }

        MoveWithCursor(cursor.X, cursor.Y);
    }

    /// <summary>
    /// Двигает панель за курсором. Позиция считается от начала захвата, а не от
    /// предыдущего шага: повторный вызов из другого источника ничего не сдвинет дважды.
    /// </summary>
    private void MoveWithCursor(int screenX, int screenY)
    {
        if (_dragStart is not { } start) return;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var dx = (screenX - start.X) / scale;
        var dy = (screenY - start.Y) / scale;

        if (!_dragging && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;

        if (!_dragging)
        {
            _dragging = true;

            // На такте опроса в 120 мс панель дёргалась рывками — восемь положений
            // в секунду видно глазом. На время перетаскивания частим.
            _timer.Interval = TimeSpan.FromMilliseconds(DragPollMs);
        }

        _placed = true;
        Left = _windowAtDragStart.X + dx;
        Top = _windowAtDragStart.Y + dy;
    }

    /// <summary>
    /// Движение мыши до окна доходит, в отличие от нажатий: WS_EX_NOACTIVATE глотает
    /// кнопки, но не перемещения. Значит саму проводку можно вести на живых событиях,
    /// а не ждать следующего такта опроса.
    /// </summary>
    private void OnMouseMoved(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is null) return;
        if (!GetCursorPos(out var cursor)) return;

        MoveWithCursor(cursor.X, cursor.Y);
    }

    /// <summary>
    /// Сворачивает панель до одной шапки. Пульсирующая точка остаётся видна: свёрнутая
    /// стенограмма всё равно должна показывать, что запись идёт.
    /// </summary>
    private void ToggleCollapsed()
    {
        _collapsed = !_collapsed;

        Lines.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        IdleRow.Visibility = _collapsed || _lines.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        CollapseLabel.Text = _collapsed ? "Развернуть" : "Свернуть";
        RestoreHeader();

        CollapsedChanged?.Invoke(_collapsed);
    }

    private void RestoreHeader() =>
        HeaderText.Text = _collapsed ? "Стенограмма идёт" : "Стенограмма";

    private DispatcherTimer? _hintTimer;

    /// <summary>
    /// Короткий ответ на действие прямо в шапке. Нажали «Это он» — должно быть видно,
    /// что нажатие дошло: своего окна у этой панели нет, и подтвердить действие
    /// больше негде.
    /// </summary>
    public void ShowHint(string text)
    {
        HeaderText.Text = text;

        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };

        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer?.Stop();
            _hintTimer = null;
            RestoreHeader();
        };

        _hintTimer.Start();
    }

    /// <summary>Панель свернули или развернули — состояние переживает перезапуск.</summary>
    public event Action<bool>? CollapsedChanged;

    /// <summary>Восстанавливает место и свёрнутость с прошлого раза.</summary>
    public void Restore(Point? position, bool collapsed)
    {
        if (collapsed != _collapsed) ToggleCollapsed();

        if (position is not { } point) return;

        // Сохранённое место могло уехать за границы: монитор отключили или сменили
        // разрешение. Проверяем, что панель останется видимой.
        var area = SystemParameters.WorkArea;
        if (point.X < area.Left - 40 || point.X > area.Right - 80) return;
        if (point.Y < area.Top - 10 || point.Y > area.Bottom - 40) return;

        Left = point.X;
        Top = point.Y;
        _placed = true;
    }

    private bool HitTest(FrameworkElement element, POINT cursor)
    {
        if (!element.IsVisible) return false;

        try
        {
            var topLeft = element.PointToScreen(new Point(0, 0));
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

            return cursor.X >= topLeft.X && cursor.X <= topLeft.X + element.ActualWidth * scale
                && cursor.Y >= topLeft.Y && cursor.Y <= topLeft.Y + element.ActualHeight * scale;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
