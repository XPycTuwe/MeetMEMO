using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeetMemo.Capture.Interop;
using MeetMemo.Storage;

namespace MeetMemo.App;

/// <summary>
/// Блокнот поверх встречи: ключевые цифры, питч, что вспомнилось по ходу разговора.
///
/// Заметки одни на все встречи — как стикер на мониторе, который никуда не девается
/// между разговорами. Копия уезжает в папку встречи: разбирающей модели полезно знать,
/// что человек счёл важным, — этого нет ни в стенограмме, ни на снимках.
///
/// Окно исключено из захвата экрана: заметки личные, и в снимках встречи им не место.
/// </summary>
public partial class NotesWindow : Window
{
    /// <summary>Оформление: цвет бумаги, цвет букв, подпись на кнопке.</summary>
    private sealed record Look(string Name, Color Paper, Color Ink);

    /// <summary>
    /// Три вида под разное окружение: тёмный к тёмным окнам встреч, светлый когда
    /// вокруг светло, и предельный контраст — когда читать надо издалека или глаза
    /// к вечеру устали.
    /// </summary>
    private static readonly Look[] Looks =
    [
        new("Тёмный",       Color.FromRgb(0x1B, 0x1D, 0x22), Color.FromRgb(0xE8, 0xEC, 0xF1)),
        new("Светлый",      Color.FromRgb(0xFC, 0xF7, 0xE3), Color.FromRgb(0x24, 0x26, 0x2B)),
        new("Контрастный",  Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0xFF, 0xFF, 0xFF))
    ];

    private const double MinFont = 11;
    private const double MaxFont = 34;

    private readonly NotesStore _store = new();
    private readonly DispatcherTimer _saveTimer;

    private int _look;
    private double _fontSize = 15;
    private bool _loading;

    /// <summary>Заметки изменились — приложение запоминает вид и размер окна.</summary>
    public event Action<NotesLayout>? LayoutChanged;

    public NotesWindow(NotesLayout layout)
    {
        InitializeComponent();

        // Сохраняем не на каждую букву: файл на диске, а печатают здесь быстро.
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };

        SourceInitialized += OnSourceInitialized;
        Closing += (_, _) => { _saveTimer.Stop(); Save(); };

        Restore(layout);

        _loading = true;
        Editor.Text = _store.Read();
        _loading = false;

        ApplyLook();
        UpdateHint();

        Loaded += (_, _) => Editor.Focus();
        LocationChanged += (_, _) => RememberLayout();
        SizeChanged += (_, _) => RememberLayout();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Заметки бывают личными, а снимки встречи уходят вместе с пакетом.
        Win32.SetWindowDisplayAffinity(
            new WindowInteropHelper(this).Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
    }

    private void Restore(NotesLayout layout)
    {
        _look = Math.Clamp(layout.Look, 0, Looks.Length - 1);
        _fontSize = Math.Clamp(layout.FontSize, MinFont, MaxFont);

        if (layout.Width is > 0) Width = layout.Width.Value;
        if (layout.Height is > 0) Height = layout.Height.Value;

        if (layout.X is not { } x || layout.Y is not { } y) return;

        // Сохранённое место могло уехать за границы: монитор отключили или сменили
        // разрешение. Проверяем, что окно останется досягаемым.
        var area = SystemParameters.VirtualScreenWidth > 0
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : SystemParameters.WorkArea;

        if (x < area.Left - 40 || x > area.Right - 80) return;
        if (y < area.Top - 10 || y > area.Bottom - 40) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = x;
        Top = y;
    }

    private void RememberLayout()
    {
        if (!IsLoaded) return;

        LayoutChanged?.Invoke(new NotesLayout
        {
            X = Left,
            Y = Top,
            Width = Width,
            Height = Height,
            FontSize = _fontSize,
            Look = _look
        });
    }

    private void ApplyLook()
    {
        var look = Looks[_look];

        Background = new SolidColorBrush(look.Paper);
        Editor.Foreground = new SolidColorBrush(look.Ink);
        Editor.CaretBrush = new SolidColorBrush(look.Ink);
        Editor.FontSize = _fontSize;

        // Выделение должно читаться на любой бумаге: берём чернила вполсилы.
        Editor.SelectionBrush = new SolidColorBrush(look.Ink) { Opacity = 0.35 };

        ThemeButton.Content = look.Name;
        HintText.Foreground = new SolidColorBrush(look.Ink);
    }

    private void UpdateHint() =>
        HintText.Text = $"{_fontSize:F0} пт · в пакет встречи";

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save() => _store.Write(Editor.Text);

    private void OnSmaller(object sender, RoutedEventArgs e) => Resize(-2);

    private void OnBigger(object sender, RoutedEventArgs e) => Resize(+2);

    private void Resize(double delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, MinFont, MaxFont);
        ApplyLook();
        UpdateHint();
        RememberLayout();
    }

    private void OnCycleTheme(object sender, RoutedEventArgs e)
    {
        _look = (_look + 1) % Looks.Length;
        ApplyLook();
        RememberLayout();
    }
}
