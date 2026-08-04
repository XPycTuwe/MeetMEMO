using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MeetMemo.Capture.Interop;

namespace MeetMemo.App;

/// <summary>
/// Полупрозрачные субтитры внизу экрана: показывают последние распознанные реплики,
/// пока идёт запись.
///
/// Смысл не в чтении — реплики идут без пунктуации и не рассчитаны на чтение с экрана.
/// Смысл в том, что стенограмма видимо живёт: строки сменяются, точка пульсирует. Раньше
/// эту роль играла строка в заголовке окна, но текст распирал панель и выдавливал кнопки
/// за её край.
///
/// Окно не перехватывает мышь (клик проходит к окну под ним), не забирает фокус
/// и исключено из захвата экрана — иначе попадало бы в собственные снимки встречи.
/// </summary>
public partial class SubtitleOverlay : Window
{
    /// <summary>
    /// Через сколько тишины субтитры возвращаются к «слушаю…». Реплика, провисевшая
    /// полминуты, уже вводит в заблуждение: кажется, что распознавание застряло на ней.
    /// </summary>
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(12);

    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastTextUtc = DateTime.UtcNow;
    private string _current = string.Empty;

    public SubtitleOverlay()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PlaceAtBottom();

        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _idleTimer.Tick += (_, _) => DropToIdleIfSilent();
        _idleTimer.Start();

        Closed += (_, _) => _idleTimer.Stop();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_TRANSPARENT пропускает клики насквозь: субтитры висят поверх встречи,
        // и перехватывать нажатия у окна под собой они не должны.
        var exStyle = Win32.GetWindowLongW(hwnd, Win32.GWL_EXSTYLE);
        SetWindowLongW(hwnd, Win32.GWL_EXSTYLE,
            exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);

        Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
    }

    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int index, int newLong);

    /// <summary>
    /// Ставит плашку по центру внизу рабочей области — там, где субтитры и ожидают увидеть.
    /// Панель задач в рабочую область не входит, поэтому субтитры её не перекрывают.
    /// </summary>
    private void PlaceAtBottom()
    {
        var area = SystemParameters.WorkArea;

        Width = Math.Min(900, area.Width * 0.7);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - ActualHeight - 48;
    }

    /// <summary>Новая распознанная реплика: текущая уезжает наверх, новая занимает её место.</summary>
    public void ShowLiveText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_current.Length > 0)
        {
            PreviousLine.Text = _current;
            PreviousLine.Visibility = Visibility.Visible;
        }

        _current = text.Trim();
        CurrentLine.Text = _current;
        _lastTextUtc = DateTime.UtcNow;

        // Высота меняется вместе с содержимым — держим плашку прижатой к низу экрана.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceAtBottom);
    }

    private void DropToIdleIfSilent()
    {
        if (_current.Length == 0 || DateTime.UtcNow - _lastTextUtc < IdleAfter) return;

        _current = string.Empty;
        CurrentLine.Text = "слушаю…";
        PreviousLine.Visibility = Visibility.Collapsed;
        PreviousLine.Text = string.Empty;
    }

    /// <summary>Сбрасывает субтитры к исходному виду перед новой встречей.</summary>
    public void Reset()
    {
        _current = string.Empty;
        _lastTextUtc = DateTime.UtcNow;
        CurrentLine.Text = "слушаю…";
        PreviousLine.Visibility = Visibility.Collapsed;
        PreviousLine.Text = string.Empty;
    }
}
