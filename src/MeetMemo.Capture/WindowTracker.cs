using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetMemo.Capture.Interop;

namespace MeetMemo.Capture;

/// <summary>Положение окна на экране в физических пикселях.</summary>
public readonly record struct WindowBounds(int Left, int Top, int Width, int Height, uint Dpi)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Слежение за окном: перемещение, изменение размера, сворачивание, закрытие.
///
/// Нужен, чтобы панель управления держалась в заголовке чужого окна и не «отклеивалась»
/// при перетаскивании. Используется системный хук событий, а не опрос по таймеру:
/// перетаскивание окна порождает поток событий, и опрос заметно отставал бы.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowTracker : IDisposable
{
    private delegate void WinEventProc(
        IntPtr hook, uint evt, IntPtr hWnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc callback,
        uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;

    private readonly nint _target;
    private readonly WinEventProc _callback;
    private readonly List<IntPtr> _hooks = new();
    private bool _disposed;

    public WindowTracker(nint targetWindow)
    {
        _target = targetWindow;

        // Делегат обязан жить в поле: хук держит на него ссылку из нативного кода,
        // и сборщик мусора не должен его освободить раньше времени.
        _callback = OnWinEvent;

        Win32.GetWindowThreadProcessId(targetWindow, out var pid);

        // Хук ограничен процессом целевого окна: глобальный поток событий от всей
        // системы был бы заметной нагрузкой при каждом движении мыши.
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, pid);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, pid);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, pid);
    }

    private void Hook(uint min, uint max, uint pid)
    {
        var handle = SetWinEventHook(
            min, max, IntPtr.Zero, _callback, pid, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (handle != IntPtr.Zero) _hooks.Add(handle);
    }

    /// <summary>Окно переместилось или изменило размер.</summary>
    public event Action<WindowBounds>? Moved;

    /// <summary>Окно свёрнуто или развёрнуто обратно.</summary>
    public event Action<bool>? MinimizedChanged;

    /// <summary>Окно закрыто — панель больше показывать не на чем.</summary>
    public event Action? Closed;

    public bool IsMinimized => Win32.IsIconic(_target);

    private void OnWinEvent(
        IntPtr hook, uint evt, IntPtr hWnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || hWnd != _target || idObject != OBJID_WINDOW) return;

        switch (evt)
        {
            case EVENT_OBJECT_DESTROY:
                Closed?.Invoke();
                break;

            case EVENT_SYSTEM_MINIMIZESTART:
                MinimizedChanged?.Invoke(true);
                break;

            case EVENT_SYSTEM_MINIMIZEEND:
                MinimizedChanged?.Invoke(false);
                break;

            case EVENT_OBJECT_LOCATIONCHANGE:
                var bounds = GetBounds(_target);
                if (!bounds.IsEmpty) Moved?.Invoke(bounds);
                break;
        }
    }

    /// <summary>
    /// Границы окна по видимой рамке DWM: обычный GetWindowRect у современных окон
    /// включает невидимые поля тени, и панель вставала бы со смещением.
    /// </summary>
    public static WindowBounds GetBounds(nint hWnd)
    {
        if (!Win32.IsWindow(hWnd)) return default;

        if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT rect, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!Win32.GetWindowRect(hWnd, out rect)) return default;
        }

        var dpi = Win32.GetDpiForWindow(hWnd);
        return new WindowBounds(rect.Left, rect.Top, rect.Width, rect.Height, dpi == 0 ? 96 : dpi);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        GC.KeepAlive(_callback);
    }
}
