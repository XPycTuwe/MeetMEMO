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
        set { _known = value; Changed(nameof(WhoBrush)); Changed(nameof(ActionLabel)); }
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
/// Мышь отслеживается опросом координат: события WPF до окна без активации не доходят.
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

        // Тем же тактом ловятся нажатия на «Назвать» и «Не он».
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
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
        Left = area.Right - Width - 24;
        Top = area.Bottom - ActualHeight - 24;
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

        if (down) { _mouseWasDown = true; return; }
        if (!_mouseWasDown) return;
        _mouseWasDown = false;

        if (_lines.Count == 0 || !GetCursorPos(out var cursor)) return;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var x = cursor.X / scale;
        var y = cursor.Y / scale;

        if (x < Left || x > Left + Width || y < Top || y > Top + ActualHeight) return;

        // Кнопки живут у правого края; клик по тексту ничего не переключает.
        if (x < Left + Width - 90) return;

        var rowHeight = ActualHeight / _lines.Count;
        var index = (int)((y - Top) / rowHeight);
        if (index < 0 || index >= _lines.Count) return;

        var line = _lines[index];
        if (line.ActionVisibility != Visibility.Visible) return;

        LineAction?.Invoke(line);
    }
}
