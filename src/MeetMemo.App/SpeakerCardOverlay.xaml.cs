using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeetMemo.Capture.Interop;

namespace MeetMemo.App;

/// <summary>
/// Карточка говорящего справа сверху: имя, должность и фото.
///
/// Стенограмма внизу отвечает на вопрос «что сказали», карточка — на вопрос «кто это».
/// Смотреть в текст ради имени неудобно: пока читаешь, разговор уходит вперёд.
///
/// Прячется сама, когда все замолчали: висеть с чужим именем над тишиной хуже,
/// чем не висеть вовсе.
/// </summary>
public partial class SpeakerCardOverlay : Window
{
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(8);

    private readonly DispatcherTimer _timer;
    private DateTime _lastShownUtc = DateTime.UtcNow;

    public SpeakerCardOverlay()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PlaceTopRight();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => HideIfStale();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Клики проходят насквозь: карточка только показывает, нажимать в ней нечего.
        var exStyle = Win32.GetWindowLongW(hwnd, Win32.GWL_EXSTYLE);
        SetWindowLongW(hwnd, Win32.GWL_EXSTYLE,
            exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);

        Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
    }

    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int index, int newLong);

    private void PlaceTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Top + 24;
    }

    /// <summary>Показывает, кто говорит. Фото необязательно — без него рисуются инициалы.</summary>
    public void ShowSpeaker(string name, string? role, string? photoPath)
    {
        NameText.Text = name;

        RoleText.Text = role ?? string.Empty;
        RoleText.Visibility = string.IsNullOrWhiteSpace(role)
            ? Visibility.Collapsed
            : Visibility.Visible;

        SetPhoto(name, photoPath);

        _lastShownUtc = DateTime.UtcNow;
        if (!IsVisible) Show();
        PlaceTopRight();
    }

    private void SetPhoto(string name, string? photoPath)
    {
        if (photoPath is not null && File.Exists(photoPath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(photoPath);

                // Уменьшаем при загрузке: держать в памяти полноразмерный портрет
                // ради кружка в тридцать пикселей незачем.
                image.DecodePixelWidth = 90;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();

                PhotoBrush.ImageSource = image;
                PhotoCircle.Visibility = Visibility.Visible;
                InitialsCircle.Visibility = Visibility.Collapsed;
                InitialsText.Visibility = Visibility.Collapsed;
                return;
            }
            catch (Exception)
            {
                // Файл мог оказаться битым или недоступным — покажем инициалы.
            }
        }

        PhotoCircle.Visibility = Visibility.Collapsed;
        InitialsCircle.Visibility = Visibility.Visible;
        InitialsText.Visibility = Visibility.Visible;
        InitialsText.Text = MeetMemo.Asr.VoicePrint.MakeInitials(name);
    }


    private void HideIfStale()
    {
        if (!IsVisible || DateTime.UtcNow - _lastShownUtc < HideAfter) return;
        Hide();
    }
}
