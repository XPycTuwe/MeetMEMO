using System.Runtime.InteropServices;
using System.Text;

namespace MeetMemo.Capture.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Win32-вызовы для перечисления окон, снимков и плавающей панели.</summary>
public static class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

    /// <summary>
    /// Размер и положение окна, включая «нормальные» — те, что были до сворачивания.
    /// У свёрнутого окна GetWindowRect отдаёт служебные координаты (-32000) и крошечный
    /// размер, поэтому судить о нём можно только по этой структуре.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public int minX, minY;
        public int maxX, maxY;
        public RECT rcNormalPosition;

        public static WINDOWPLACEMENT Create() =>
            new() { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
    }

    /// <summary>
    /// Габариты окна для показа в списке. Для свёрнутого окна возвращает размер,
    /// который оно займёт после разворачивания.
    /// </summary>
    public static RECT GetLogicalBounds(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
        {
            var placement = WINDOWPLACEMENT.Create();
            if (GetWindowPlacement(hWnd, ref placement)) return placement.rcNormalPosition;
        }

        GetWindowRect(hWnd, out var rect);
        return rect;
    }

    [DllImport("user32.dll")]
    public static extern int GetWindowLongW(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Исключение окна из захвата экрана. Best effort: механизм не является DRM и не гарантирует
    /// невидимость во всех способах записи — на это прямо указано в документации продукта.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr hWnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    public const uint GA_ROOT = 2;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CHILD = 0x40000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_APPWINDOW = 0x00040000;

    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>Окно видно везде, включая снимки экрана.</summary>
    public const uint WDA_NONE = 0;

    /// <summary>Окно исключается из захвата экрана (Windows 10 2004+).</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>Без этого флага удержание клавиши порождает поток повторных команд.</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    /// <summary>Код ошибки, когда комбинация уже занята другим приложением.</summary>
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    public static string GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Окно скрыто механизмом DWM (например, свёрнутое приложение из другого рабочего стола).
    /// Такие окна нельзя показывать в списке выбора — они не отдают кадры.
    /// </summary>
    public static bool IsCloaked(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0)
            return false;
        return cloaked != 0;
    }
}
