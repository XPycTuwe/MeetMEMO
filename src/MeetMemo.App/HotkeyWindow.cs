using System.Runtime.InteropServices;
using System.Windows.Interop;
using MeetMemo.Capture;

namespace MeetMemo.App;

/// <summary>
/// Невидимое окно-приёмник сообщений для глобальных горячих клавиш.
///
/// RegisterHotKey шлёт WM_HOTKEY в конкретное окно, поэтому такое окно нужно даже
/// приложению без видимого главного окна. Создаётся как message-only (HWND_MESSAGE):
/// оно не появляется на панели задач и не участвует в переключении окон.
/// </summary>
public sealed class HotkeyWindow : IDisposable
{
    private const int HWND_MESSAGE = -3;

    private readonly HwndSource _source;

    public HotkeyWindow()
    {
        var parameters = new HwndSourceParameters("MeetMemo.HotkeyWindow")
        {
            ParentWindow = new IntPtr(HWND_MESSAGE),
            WindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        Manager = new HotkeyManager(_source.Handle);
    }

    public HotkeyManager Manager { get; }

    public nint Handle => _source.Handle;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Manager.HandleMessage(msg, wParam)) handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Manager.Dispose();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
