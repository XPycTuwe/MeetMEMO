using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetMemo.Capture.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Capture;

/// <summary>Действие, привязываемое к глобальной комбинации.</summary>
public enum HotkeyAction
{
    StartStop,
    PauseResume,
    CaptureWindow,
    CaptureDesktop,
    MarkImportant
}

/// <summary>Комбинация клавиш.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, uint Modifiers, uint VirtualKey, string Display)
{
    /// <summary>
    /// Значения по умолчанию. Сознательно выбраны обычные сочетания, доступные на любой
    /// клавиатуре: клавиша Copilot есть далеко не везде, поэтому в дефолтах её нет.
    /// </summary>
    public static IReadOnlyList<HotkeyBinding> Defaults =>
    [
        new(HotkeyAction.StartStop, Win32.MOD_CONTROL | Win32.MOD_ALT, VK_M, "Ctrl+Alt+M"),
        new(HotkeyAction.PauseResume, Win32.MOD_CONTROL | Win32.MOD_ALT, VK_P, "Ctrl+Alt+P"),
        new(HotkeyAction.CaptureWindow, Win32.MOD_CONTROL | Win32.MOD_ALT, VK_S, "Ctrl+Alt+S"),
        new(HotkeyAction.CaptureDesktop, Win32.MOD_CONTROL | Win32.MOD_ALT, VK_D, "Ctrl+Alt+D"),
        new(HotkeyAction.MarkImportant, Win32.MOD_CONTROL | Win32.MOD_ALT, VK_I, "Ctrl+Alt+I")
    ];

    public const uint VK_M = 0x4D;
    public const uint VK_P = 0x50;
    public const uint VK_S = 0x53;
    public const uint VK_D = 0x44;
    public const uint VK_I = 0x49;

    /// <summary>Клавиша Copilot: на части клавиатур передаёт Win+Shift+F23.</summary>
    public const uint VK_F23 = 0x86;

    public static HotkeyBinding CopilotKey(HotkeyAction action) =>
        new(action, Win32.MOD_WIN | Win32.MOD_SHIFT, VK_F23, "Клавиша Copilot");
}

/// <summary>Результат попытки регистрации — нужен, чтобы показать понятное сообщение (AC-19).</summary>
public sealed record HotkeyRegistration(HotkeyBinding Binding, bool Success, string? Error);

/// <summary>
/// Глобальные горячие клавиши. Работают, когда фокус в Teams или браузере, поэтому
/// регистрируются системно, а не через обработку клавиатуры приложения.
///
/// Занятая другим приложением комбинация не «проглатывается»: пользователь получает
/// явное сообщение и может назначить другую (ТЗ 7.3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HotkeyManager : IDisposable
{
    private readonly ILogger _log;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private readonly nint _windowHandle;
    private int _nextId = 1;

    public HotkeyManager(nint messageWindowHandle, ILogger? log = null)
    {
        _windowHandle = messageWindowHandle;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Нажата зарегистрированная комбинация.</summary>
    public event Action<HotkeyAction>? HotkeyPressed;

    public IReadOnlyList<HotkeyRegistration> Register(IEnumerable<HotkeyBinding> bindings)
    {
        var results = new List<HotkeyRegistration>();

        foreach (var binding in bindings)
        {
            var id = _nextId++;
            // MOD_NOREPEAT: без него удержание клавиши порождает поток повторных команд.
            var success = Win32.RegisterHotKey(
                _windowHandle, id, binding.Modifiers | Win32.MOD_NOREPEAT, binding.VirtualKey);

            if (success)
            {
                _registered[id] = binding.Action;
                results.Add(new HotkeyRegistration(binding, true, null));
                _log.LogInformation("Горячая клавиша {Display} → {Action}", binding.Display, binding.Action);
            }
            else
            {
                var code = Marshal.GetLastWin32Error();
                var message = code == Win32.ERROR_HOTKEY_ALREADY_REGISTERED
                    ? $"Комбинация {binding.Display} уже занята другим приложением"
                    : new Win32Exception(code).Message;

                results.Add(new HotkeyRegistration(binding, false, message));
                _log.LogWarning("Не удалось зарегистрировать {Display}: {Message}", binding.Display, message);
            }
        }

        return results;
    }

    /// <summary>Обработка WM_HOTKEY из процедуры окна-приёмника сообщений.</summary>
    public bool HandleMessage(int msg, nint wParam)
    {
        if (msg != Win32.WM_HOTKEY) return false;

        var id = (int)wParam;
        if (!_registered.TryGetValue(id, out var action)) return false;

        HotkeyPressed?.Invoke(action);
        return true;
    }

    /// <summary>Проверка доступности комбинации до сохранения настроек.</summary>
    public bool IsAvailable(HotkeyBinding binding)
    {
        var probeId = 0xBEEF;
        if (!Win32.RegisterHotKey(_windowHandle, probeId,
                binding.Modifiers | Win32.MOD_NOREPEAT, binding.VirtualKey))
            return false;

        Win32.UnregisterHotKey(_windowHandle, probeId);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList())
            Win32.UnregisterHotKey(_windowHandle, id);
        _registered.Clear();
    }

    public void Dispose() => UnregisterAll();
}
