using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetMemo.Capture.Interop;

namespace MeetMemo.Capture;

/// <summary>Монитор для снимка рабочего стола.</summary>
public sealed record MonitorInfo(string Id, string Name, int Left, int Top, int Width, int Height, bool IsPrimary);

/// <summary>
/// Снимки целевого окна и рабочего стола.
///
/// Окно снимается через PrintWindow с полным рендерингом содержимого: в отличие от копирования
/// с экрана, так в кадр не попадают перекрывающие окна и уведомления — это прямое требование
/// приватности из ТЗ 9.1. Снимок рабочего стола делается только вручную.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenCapture
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>Рендерить всё содержимое окна, включая ускоренные поверхности (Windows 8.1+).</summary>
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>
    /// Снимок конкретного окна.
    ///
    /// Порядок попыток важен. PrintWindow с полным рендерингом даёт самый «чистый» кадр —
    /// в него не попадают перекрывающие окна и уведомления. Но браузеры на Chromium и другие
    /// приложения с аппаратным ускорением нередко возвращают на него чёрный кадр. Тогда
    /// пробуем обычный PrintWindow, а последним средством копируем область экрана: это
    /// работает всегда, но требует, чтобы окно не было перекрыто — поэтому используется
    /// только когда первые два способа не дали изображения.
    ///
    /// Возвращает null, если окно свёрнуто, закрыто или содержимое защищено от захвата.
    /// </summary>
    public static Bitmap? CaptureWindow(nint hWnd)
    {
        if (!Win32.IsWindow(hWnd) || Win32.IsIconic(hWnd)) return null;

        // Границы берём по видимой рамке DWM: GetWindowRect у современных окон
        // включает невидимые поля тени, из-за чего по краям появляются чёрные полосы.
        if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT rect, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!Win32.GetWindowRect(hWnd, out rect)) return null;
        }

        if (rect.Width <= 0 || rect.Height <= 0) return null;

        return TryPrintWindow(hWnd, rect, PW_RENDERFULLCONTENT)
            ?? TryPrintWindow(hWnd, rect, 0)
            ?? TryCopyFromScreen(rect);
    }

    private static Bitmap? TryPrintWindow(nint hWnd, RECT rect, uint flags)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                var ok = false;
                try
                {
                    ok = PrintWindow(hWnd, hdc, flags);
                }
                finally
                {
                    try { graphics.ReleaseHdc(hdc); } catch (ArgumentException) { }
                }

                if (!ok)
                {
                    bitmap.Dispose();
                    return null;
                }
            }

            if (IsBlank(bitmap))
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch (Exception)
        {
            bitmap.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Копия области экрана. Кадр может содержать перекрывающие окна, поэтому способ
    /// применяется только когда прямой захват окна не сработал.
    /// </summary>
    private static Bitmap? TryCopyFromScreen(RECT rect)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                    new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            }

            if (IsBlank(bitmap))
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch (Exception)
        {
            bitmap.Dispose();
            return null;
        }
    }

    /// <summary>Снимок монитора целиком. Только по явной команде пользователя (ТЗ 9.1).</summary>
    public static Bitmap? CaptureMonitor(MonitorInfo monitor)
    {
        try
        {
            var bitmap = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(monitor.Left, monitor.Top, 0, 0,
                new Size(monitor.Width, monitor.Height), CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint MONITORINFOF_PRIMARY = 1;

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        var index = 0;

        // Делегат держим в переменной: переданный напрямую, он может быть собран
        // сборщиком мусора во время перечисления (см. WindowEnumerator).
        var callback = new MonitorEnumProc((IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info))
            {
                var bounds = info.rcMonitor;
                result.Add(new MonitorInfo(
                    $"monitor-{index}",
                    string.IsNullOrWhiteSpace(info.szDevice) ? $"Монитор {index + 1}" : info.szDevice,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                index++;
            }

            return true;
        });

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return result;
    }

    /// <summary>Проверка «кадр пустой»: выборочная, чтобы не обходить каждый пиксель 4K-снимка.</summary>
    private static unsafe bool IsBlank(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var scan = (byte*)data.Scan0;
            var stepY = Math.Max(1, bitmap.Height / 64);
            var stepX = Math.Max(1, bitmap.Width / 64);

            for (var y = 0; y < bitmap.Height; y += stepY)
            {
                var row = scan + (long)y * data.Stride;
                for (var x = 0; x < bitmap.Width; x += stepX)
                {
                    var px = row + (long)x * 4;
                    if (px[0] != 0 || px[1] != 0 || px[2] != 0) return false;
                }
            }

            return true;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
