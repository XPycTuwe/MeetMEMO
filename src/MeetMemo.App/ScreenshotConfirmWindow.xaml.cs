using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeetMemo.Capture.Interop;

namespace MeetMemo.App;

/// <summary>Автоснимок, ожидающий решения: сохранить или выбросить.</summary>
public sealed class PendingScreenshot : INotifyPropertyChanged
{
    private int _secondsLeft;

    public required Bitmap Image { get; init; }

    public required BitmapSource Thumbnail { get; init; }

    public string? WindowTitle { get; init; }

    /// <summary>Свой отсчёт у каждого снимка: сосед, появившийся позже, его не сбрасывает.</summary>
    public int SecondsLeft
    {
        get => _secondsLeft;
        set
        {
            if (_secondsLeft == value) return;
            _secondsLeft = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecondsLeft)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Countdown)));
        }
    }

    public string Countdown => $"Сохраню через {SecondsLeft} с";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Подтверждение автоснимков.
///
/// Приложение снимает экран само, и раньше человек узнавал об этом только по содержимому
/// пакета. Теперь под значком MeetMemo появляется карточка с миниатюрой и обратным
/// отсчётом: не тронешь — снимок сохранится, нажмёшь «Не сохранять» — исчезнет.
///
/// Отсчёт у каждого снимка свой, а таймер один на всех: у отдельных таймеров накапливается
/// расхождение, и карточки с одинаковым остатком показывали бы разные цифры.
/// </summary>
public partial class ScreenshotConfirmWindow : Window
{
    /// <summary>Сколько времени даётся на отказ. Больше — карточки копятся, меньше — не успеть.</summary>
    private const int CountdownSeconds = 6;

    private readonly ObservableCollection<PendingScreenshot> _pending = new();
    private readonly DispatcherTimer _timer;
    private readonly Func<System.Windows.Point?> _anchorProvider;
    private readonly Action<PendingScreenshot> _onConfirmed;

    private bool _mouseWasDown;

    public ScreenshotConfirmWindow(Func<System.Windows.Point?> anchorProvider, Action<PendingScreenshot> onConfirmed)
    {
        InitializeComponent();

        _anchorProvider = anchorProvider;
        _onConfirmed = onConfirmed;
        PendingList.ItemsSource = _pending;

        SourceInitialized += OnSourceInitialized;

        // Такт чаще секунды: тем же таймером ловятся нажатия на «Не сохранять» —
        // события мыши до окна без активации не доходят.
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            foreach (var item in _pending) item.Image.Dispose();
            _pending.Clear();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        var exStyle = Win32.GetWindowLongW(hwnd, Win32.GWL_EXSTYLE);
        SetWindowLongW(hwnd, Win32.GWL_EXSTYLE,
            exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);

        // Карточки не должны попасть в следующий же автоснимок того же окна.
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

    /// <summary>Добавляет снимок в очередь ожидания. Владение кадром переходит окну.</summary>
    public void Add(Bitmap image, string? windowTitle)
    {
        _pending.Add(new PendingScreenshot
        {
            Image = image,
            Thumbnail = MakeThumbnail(image),
            WindowTitle = windowTitle,
            SecondsLeft = CountdownSeconds
        });

        if (!IsVisible) Show();
        Reposition();
    }

    private DateTime _lastSecond = DateTime.UtcNow;

    private void Tick()
    {
        HandleCancelClick();

        // Отсчёт идёт по часам, а не по числу тактов: подтормозивший таймер не должен
        // растягивать шесть секунд до десяти.
        var now = DateTime.UtcNow;
        if ((now - _lastSecond).TotalMilliseconds < 1000) return;
        _lastSecond = now;

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            item.SecondsLeft--;

            if (item.SecondsLeft > 0) continue;

            _pending.RemoveAt(i);
            _onConfirmed(item);
        }

        if (_pending.Count == 0)
        {
            if (IsVisible) Hide();
            return;
        }

        Reposition();
    }

    /// <summary>
    /// Нажатие на «Не сохранять». Считается по координатам: попадание в правую половину
    /// карточки, туда, где нарисована кнопка.
    /// </summary>
    private void HandleCancelClick()
    {
        var down = (GetAsyncKeyState(0x01) & 0x8000) != 0;

        if (down || !_mouseWasDown)
        {
            _mouseWasDown = down;
            return;
        }

        _mouseWasDown = false;

        if (!GetCursorPos(out var cursor) || _pending.Count == 0) return;

        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var x = cursor.X / scale;
        var y = cursor.Y / scale;

        if (x < Left || x > Left + Width) return;

        // Карточки одинаковой высоты и идут сверху вниз — по вертикали и находим нужную.
        var cardHeight = ActualHeight / _pending.Count;
        var index = (int)((y - Top) / cardHeight);
        if (index < 0 || index >= _pending.Count) return;

        // Кнопка занимает нижнюю правую часть карточки; попадание в миниатюру не считаем.
        var withinCard = y - Top - index * cardHeight;
        if (withinCard < cardHeight * 0.45 || x < Left + Width * 0.35) return;

        var item = _pending[index];
        _pending.RemoveAt(index);
        item.Image.Dispose();

        if (_pending.Count == 0) Hide();
        else Reposition();
    }

    private void Reposition()
    {
        var anchor = _anchorProvider();
        if (anchor is not { } point) return;

        Left = point.X - Width;
        Top = point.Y;
    }

    /// <summary>
    /// Миниатюра для карточки. Кадр уменьшается сразу при загрузке, иначе в памяти
    /// висела бы полноразмерная копия каждого ожидающего снимка.
    /// </summary>
    private static BitmapSource MakeThumbnail(Bitmap source)
    {
        using var buffer = new MemoryStream();
        source.Save(buffer, ImageFormat.Bmp);
        buffer.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = buffer;
        image.DecodePixelWidth = 172;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
