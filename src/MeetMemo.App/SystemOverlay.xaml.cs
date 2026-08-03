using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeetMemo.Capture.Interop;
using MeetMemo.Core;

namespace MeetMemo.App;

/// <summary>
/// Плавающий индикатор записи всей системы.
///
/// У такой записи нет окна-цели, поэтому значок в заголовке показать негде — вместо него
/// свободно плавающая панель поверх экрана. Как и значок в заголовке, она не забирает
/// фокус ввода и исключается из захвата экрана: иначе попадала бы в собственные снимки
/// рабочего стола, ради которых и существует.
/// </summary>
public partial class SystemOverlay : Window
{
    private readonly Func<SessionController?> _controllerProvider;
    private readonly DispatcherTimer _timer;

    private SessionController? Controller => _controllerProvider();

    public SystemOverlay(Func<SessionController?> controllerProvider, Point? savedPosition)
    {
        InitializeComponent();
        _controllerProvider = controllerProvider;

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += (_, _) => UpdateState();
        _timer.Start();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => RestorePosition(savedPosition);
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Куда пользователь перетащил панель — приложение сохраняет это между запусками.</summary>
    public Point CurrentPosition => new(Left, Top);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        var exStyle = Win32.GetWindowLongW(hwnd, Win32.GWL_EXSTYLE);
        SetWindowLongW(hwnd, Win32.GWL_EXSTYLE,
            exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);

        // Без этого панель попадала бы в снимок рабочего стола, который сама же и делает.
        Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int index, int newLong);

    /// <summary>
    /// Возвращает панель на прежнее место. Сохранённая позиция может оказаться за пределами
    /// экрана — например, если отключили монитор, — поэтому проверяем и поправляем.
    /// </summary>
    private void RestorePosition(Point? saved)
    {
        var area = SystemParameters.WorkArea;

        if (saved is { } point
            && point.X >= area.Left && point.X + ActualWidth <= area.Right + 1
            && point.Y >= area.Top && point.Y + ActualHeight <= area.Bottom + 1)
        {
            Left = point.X;
            Top = point.Y;
            return;
        }

        Left = area.Right - ActualWidth - 24;
        Top = area.Top + 24;
    }

    public void ShowLiveText(string text)
    {
        LiveText.Text = text;
        LiveText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateState()
    {
        var controller = Controller;
        if (controller is null) return;

        TimerText.Text = controller.Elapsed.ToString(@"hh\:mm\:ss");

        var paused = controller.State == SessionState.Paused;
        StatusDot.Fill = new SolidColorBrush(paused
            ? Color.FromRgb(0xFB, 0xC0, 0x2D)
            : Color.FromRgb(0xE5, 0x39, 0x35));

        PauseButton.Content = paused ? "Продолжить" : "Пауза";

        // Снимки на паузе в пакет не попадают — кнопку глушим, чтобы не вводить в заблуждение.
        DesktopShotButton.IsEnabled = !paused;
        ImportantButton.IsEnabled = !paused;
    }

    private async void OnDesktopShotClick(object sender, RoutedEventArgs e)
    {
        var controller = Controller;
        if (controller is null) return;

        await controller.SendAsync(new SessionCommand.CaptureDesktop());
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        var controller = Controller;
        if (controller is null) return;

        SessionCommand command = controller.State == SessionState.Paused
            ? new SessionCommand.Resume()
            : new SessionCommand.Pause();

        await controller.SendAsync(command);
    }

    private async void OnImportantClick(object sender, RoutedEventArgs e)
    {
        var controller = Controller;
        if (controller is null) return;

        await controller.SendAsync(new SessionCommand.MarkImportant());
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        var controller = Controller;
        if (controller is null) return;

        await controller.SendAsync(new SessionCommand.Stop());
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
