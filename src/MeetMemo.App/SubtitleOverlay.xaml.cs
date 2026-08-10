using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

        // Такт чаще секунды: этим же таймером ловятся нажатия на «Это он» и «Не он».
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _idleTimer.Tick += (_, _) =>
        {
            HandleSpeakerButtons();
            DropToIdleIfSilent();
        };
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

    /// <summary>Пользователь подтвердил догадку о говорящем — голос стоит запомнить крепче.</summary>
    public event Action<string>? SpeakerConfirmed;

    /// <summary>Догадка неверна: нужно спросить, кто это на самом деле.</summary>
    public event Action? SpeakerRejected;

    private string? _currentSpeakerId;
    private bool _buttonsWereDown;

    /// <summary>
    /// Показывает, чей это голос. <paramref name="printId"/> — узнанный отпечаток,
    /// null означает «голос незнаком»: тогда предлагаем назвать человека.
    /// </summary>
    public void ShowSpeaker(string? display, string? printId, float similarity)
    {
        _currentSpeakerId = printId;

        if (display is null)
        {
            SpeakerRow.Visibility = Visibility.Collapsed;
            return;
        }

        SpeakerRow.Visibility = Visibility.Visible;

        if (printId is null)
        {
            // Незнакомый голос: подтверждать нечего, можно только назвать.
            SpeakerName.Text = display;
            SpeakerName.Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xC1, 0x60));
            ConfirmButton.Visibility = Visibility.Collapsed;
            RejectLabel.Text = "Назвать";
            return;
        }

        // Уверенность показываем словом, а не числом: «0,58» ничего не говорит человеку.
        var certainty = similarity >= 0.72f ? string.Empty : "  •  возможно";
        SpeakerName.Text = display + certainty;
        SpeakerName.Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xD1, 0xA8));
        ConfirmButton.Visibility = Visibility.Visible;
        RejectLabel.Text = "Не он";
    }

    /// <summary>
    /// Нажатия на «Это он» и «Не он». Как и везде в наших окнах поверх чужих, мышь
    /// отслеживается опросом координат: события WPF до окна без активации не доходят.
    /// </summary>
    private void HandleSpeakerButtons()
    {
        if (SpeakerRow.Visibility != Visibility.Visible) return;

        var down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (down) { _buttonsWereDown = true; return; }
        if (!_buttonsWereDown) return;
        _buttonsWereDown = false;

        if (!GetCursorPos(out var cursor)) return;

        if (HitTest(ConfirmButton, cursor) && _currentSpeakerId is { } id)
        {
            SpeakerConfirmed?.Invoke(id);
            SpeakerRow.Visibility = Visibility.Collapsed;
            return;
        }

        if (HitTest(RejectButton, cursor))
        {
            SpeakerRejected?.Invoke();
            SpeakerRow.Visibility = Visibility.Collapsed;
        }
    }

    private bool HitTest(FrameworkElement element, POINT cursor)
    {
        if (!element.IsVisible) return false;

        try
        {
            var topLeft = element.PointToScreen(new Point(0, 0));
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

            return cursor.X >= topLeft.X && cursor.X <= topLeft.X + element.ActualWidth * scale
                && cursor.Y >= topLeft.Y && cursor.Y <= topLeft.Y + element.ActualHeight * scale;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int VK_LBUTTON = 0x01;

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
